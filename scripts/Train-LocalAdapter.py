"""Offline LoRA execution protocol for Agent Labs. No server, Hub, remote code or shell.

Each checkpoint is committed by directory rename after adapter, optimizer and RNG writes.
The status file is an atomic observation; completed metrics are also append-only JSONL.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import random
import shutil
import sys
import time
import uuid
from collections.abc import Mapping

os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", WANDB_DISABLED="true")


def atomic(path, value):
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temp.write_text(json.dumps(value, allow_nan=False, indent=2), encoding="utf-8")
    for attempt in range(40):
        try:
            os.replace(temp, path)
            return
        except PermissionError:
            if attempt == 39:
                raise
            time.sleep(.025)


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(4 * 1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


class TrainingStop(Exception):
    pass


def run(config_path, probe=False):
    spec = json.loads(config_path.read_text(encoding="utf-8-sig"))
    out = Path(spec["Output"]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    state = dict(state="Preparing", step=0, total=spec["Steps"], checkpoint=None)
    started = time.monotonic()

    def emit(event="status", **values):
        state.update(values, event=event, elapsedSeconds=time.monotonic() - started)
        atomic(out / "status.json", state)
        for attempt in range(40):
            try:
                with (out / "events.jsonl").open("a", encoding="utf-8") as f:
                    f.write(json.dumps(state, allow_nan=False) + "\n")
                break
            except PermissionError:
                if attempt == 39:
                    raise
                time.sleep(.025)

    try:
        import torch
        import transformers
        import peft
        torch.set_num_threads(min(4, max(1, os.cpu_count() or 1)))
        from transformers import AutoConfig, AutoModelForCausalLM, AutoTokenizer
        from peft import LoraConfig, get_peft_model, get_peft_model_state_dict, set_peft_model_state_dict
        base = Path(spec["Base"]).resolve()
        if not base.is_dir() or not (base / "config.json").is_file():
            raise ValueError("Select a local Transformers base directory, not GGUF inference weights.")
        config = AutoConfig.from_pretrained(base, local_files_only=True, trust_remote_code=False)
        if getattr(config, "quantization_config", None):
            raise ValueError("This training backend requires unquantized trainable base weights; inference quantization is not a training base.")
        device = spec["Device"]
        if device not in ("cpu", "cuda") or device == "cuda" and not torch.cuda.is_available():
            raise ValueError("Requested training device is unavailable; no device fallback.")
        if not 1 <= spec["Steps"] <= 100000 or not 16 <= spec["SequenceLength"] <= 4096 or not 1 <= spec["Rank"] <= 64 or not 0 < spec["LearningRate"] <= .01:
            raise ValueError("Training bounds are invalid.")
        context = getattr(config, "max_position_embeddings", None) or getattr(config, "n_positions", None)
        if isinstance(context, int) and spec["SequenceLength"] > context:
            raise ValueError("Reviewed sequence bound exceeds the base model's declared context.")
        tokenizer = AutoTokenizer.from_pretrained(base, local_files_only=True, trust_remote_code=False)
        if not tokenizer.chat_template:
            raise ValueError("The selected base tokenizer has no chat template. Choose/configure a compatible local chat base explicitly.")
        model_files = sorted(p for p in base.iterdir() if p.is_file() and p.suffix in (".json", ".safetensors", ".bin", ".model", ".txt"))
        if len(model_files) > 300:
            raise ValueError("Base exceeds the 300-file fingerprint bound.")
        if not any(p.suffix == ".safetensors" or p.name == "pytorch_model.bin" for p in model_files):
            raise ValueError("Local trainable model tensor files were not found.")
        fingerprints = {}
        for i, path in enumerate(model_files):
            emit(state="Fingerprinting", detail=path.name, filesDone=i, filesTotal=len(model_files))
            if (out / "pause.request").exists():
                emit(state="Interrupted", detail="Stopped during preflight; no model update performed.")
                return 0
            fingerprints[path.name] = digest(path)
        bundle = Path(spec["Bundle"])
        manifest = json.loads((bundle / "manifest.json").read_text(encoding="utf-8-sig"))
        # AI Gym writes "ai-gym-training-handoff". The older kind is still accepted so datasets exported before
        # AI Gym was its own app keep training without a re-export.
        if manifest.get("Kind") not in ("ai-gym-training-handoff", "db-cowboy-training-handoff") or manifest.get("Version") != 1:
            raise ValueError("Expected a reviewed AI Gym training handoff.")
        provenance = manifest.get("Examples", [])
        train_entities = {r["EntityHash"] for r in provenance if r["Split"] == "train"}
        eval_entities = {r["EntityHash"] for r in provenance if r["Split"] == "evaluation"}
        if not train_entities or not eval_entities or train_entities & eval_entities:
            raise ValueError("Training/evaluation provenance must contain distinct non-overlapping entities.")
        for name, key in (("train.jsonl", "TrainSha256"), ("evaluation.jsonl", "EvaluationSha256")):
            # The existing handoff hashes decoded UTF-8 text as SHA256, equivalent to these UTF-8 files.
            if digest(bundle / name).upper() != manifest["Files"][key].upper():
                raise ValueError("Training bundle integrity check failed: " + name)
            split = "train" if name == "train.jsonl" else "evaluation"
            lines = (bundle / name).read_text(encoding="utf-8-sig").splitlines()
            if len(lines) != sum(r["Split"] == split for r in provenance):
                raise ValueError("Split example counts differ from retained provenance.")
        # A lower bound, not an activation-memory guarantee. Do not attempt a known-impossible load.
        minimum_bytes = 0
        for file in model_files:
            if file.suffix != ".safetensors":
                continue
            with file.open("rb") as stream:
                header_length = int.from_bytes(stream.read(8), "little")
                if not 1 <= header_length <= 16 * 1024 * 1024:
                    raise ValueError("Invalid safetensors header bound.")
                header = json.loads(stream.read(header_length))
            for name, tensor in header.items():
                if name != "__metadata__":
                    minimum_bytes += math.prod(tensor["shape"]) * (4 if device == "cpu" else 2)
        available_bytes = None
        if device == "cuda":
            available_bytes = torch.cuda.mem_get_info()[0]
        elif os.name == "nt":
            import ctypes
            class Memory(ctypes.Structure):
                _fields_ = [("length", ctypes.c_uint32), ("load", ctypes.c_uint32)] + [(x, ctypes.c_uint64) for x in ("total", "available", "total_page", "available_page", "total_virtual", "available_virtual", "extended")]
            memory = Memory()
            memory.length = ctypes.sizeof(memory)
            if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(memory)):
                available_bytes = memory.available
        if available_bytes is not None and minimum_bytes > available_bytes:
            raise ValueError(f"Base weight lower bound {minimum_bytes} bytes exceeds observed available {device} memory {available_bytes} bytes. Select a smaller trainable base; no load attempted.")
        identity = dict(protocol=1, python=sys.version.split()[0], base=str(base), weights=fingerprints, train=digest(bundle / "train.jsonl"), evaluation=digest(bundle / "evaluation.jsonl"),
                        steps=spec["Steps"], sequence=spec["SequenceLength"], rank=spec["Rank"], lr=spec["LearningRate"], device=device,
                        torch=torch.__version__, transformers=transformers.__version__, peft=peft.__version__, seed=spec["Seed"], objective="all-message causal language modeling")
        identity_hash = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()
        identity_path = out / "identity.json"
        if identity_path.exists() and json.loads(identity_path.read_text())["hash"] != identity_hash:
            raise ValueError("Base weights, data, runtime or training configuration changed. Resume refused; create a new training run.")
        atomic(identity_path, dict(hash=identity_hash, identity=identity))
        emit(state="Ready", detail="Offline LoRA preflight completed; activation/optimizer headroom still requires execution", identity=identity_hash, minimumWeightBytes=minimum_bytes, availableDeviceBytes=available_bytes)
        if probe:
            return 0
        random.seed(spec["Seed"])
        torch.manual_seed(spec["Seed"])
        if device == "cuda":
            torch.cuda.manual_seed_all(spec["Seed"])
            torch.cuda.reset_peak_memory_stats()
        dtype = torch.float32 if device == "cpu" else torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
        emit(state="Loading", detail="Loading local base; no downloads")
        model = AutoModelForCausalLM.from_pretrained(base, local_files_only=True, trust_remote_code=False, dtype=dtype).to(device)
        model.config.use_cache = False
        model = get_peft_model(model, LoraConfig(task_type="CAUSAL_LM", target_modules="all-linear", r=spec["Rank"], lora_alpha=spec["Rank"] * 2, lora_dropout=0.0))
        optimizer = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad), lr=spec["LearningRate"])

        def examples(name):
            result = []
            lines = (bundle / name).read_text(encoding="utf-8-sig").splitlines()
            if not 1 <= len(lines) <= 2000:
                raise ValueError("Each split needs 1–2,000 examples.")
            for line in lines:
                obj = json.loads(line)
                messages = obj["messages"]
                if not messages or messages[-1]["role"] != "assistant":
                    raise ValueError("A supervised example must end with an assistant response.")
                tokens = tokenizer.apply_chat_template(messages, tokenize=True, add_generation_prompt=False)
                if isinstance(tokens, Mapping):
                    tokens = tokens["input_ids"]
                if len(tokens) > spec["SequenceLength"] or len(tokens) < 2:
                    raise ValueError("Example outside reviewed sequence bound; no silent truncation.")
                result.append(torch.tensor([tokens], dtype=torch.long, device=device))
            return result

        train, evaluation = examples("train.jsonl"), examples("evaluation.jsonl")
        step = 0
        pointer = out / "latest.json"
        if pointer.exists():
            checkpoint = out / json.loads(pointer.read_text())["directory"]
            if checkpoint.parent != out:
                raise ValueError("Checkpoint path escaped this run.")
            saved = json.loads((checkpoint / "checkpoint.json").read_text())
            if saved["identity"] != identity_hash:
                raise ValueError("Checkpoint identity mismatch.")
            for name, sha in saved["files"].items():
                if Path(name).name != name or digest(checkpoint / name) != sha:
                    raise ValueError("Incomplete or changed checkpoint; resume refused.")
            from safetensors.torch import load_file
            set_peft_model_state_dict(model, load_file(str(checkpoint / "adapter_model.safetensors")))
            saved_state = torch.load(checkpoint / "optimizer.pt", map_location=device, weights_only=True)
            optimizer.load_state_dict(saved_state["optimizer"])
            torch.set_rng_state(saved_state["rng"].cpu())
            random.setstate(saved_state["python_rng"])
            if device == "cuda":
                torch.cuda.set_rng_state_all([r.cpu() for r in saved_state["cuda_rng"]])
            step = saved["step"]
            emit(state="Resuming", step=step, checkpoint=str(checkpoint), detail="Adapter, optimizer and RNG restored")

        def save_checkpoint():
            staging = out / (".checkpoint-" + uuid.uuid4().hex)
            staging.mkdir()
            model.save_pretrained(staging, safe_serialization=True)
            torch.save(dict(optimizer=optimizer.state_dict(), rng=torch.get_rng_state(), python_rng=random.getstate(), cuda_rng=torch.cuda.get_rng_state_all() if device == "cuda" else []), staging / "optimizer.pt")
            hashes = {p.name: digest(p) for p in staging.iterdir() if p.is_file()}
            atomic(staging / "checkpoint.json", dict(identity=identity_hash, step=step, files=hashes))
            committed = out / ("checkpoint-" + str(step) + "-" + uuid.uuid4().hex[:8])
            staging.rename(committed)
            atomic(pointer, dict(directory=committed.name, step=step))
            emit(checkpoint=str(committed), checkpointStep=step)
            # App-owned retention policy: keep the newest three complete checkpoints, never the final adapter.
            complete = sorted((p for p in out.glob("checkpoint-*") if p.is_dir() and not p.is_symlink() and p.resolve().parent == out and (p / "checkpoint.json").is_file()), key=lambda p: p.stat().st_mtime_ns, reverse=True)
            for old in complete[3:]:
                metadata = json.loads((old / "checkpoint.json").read_text())
                if old != committed and metadata.get("identity") == identity_hash:
                    shutil.rmtree(old)

        validation_every = max(1, math.ceil(spec["Steps"] / 20))

        def validate():
            # Evaluation observes the frozen held-out split. It never updates weights,
            # and restoring RNG makes the optimizer trajectory independent of this HUD.
            cpu_rng = torch.get_rng_state()
            cuda_rng = torch.cuda.get_rng_state_all() if device == "cuda" else None
            model.eval()
            try:
                with torch.no_grad():
                    losses = []
                    for ids in evaluation:
                        if (out / "pause.request").exists():
                            raise TrainingStop()
                        losses.append(float(model(input_ids=ids, labels=ids).loss.cpu()))
                value = sum(losses) / len(losses)
                if not math.isfinite(value):
                    raise ValueError("Non-finite held-out loss.")
                emit(event="validation", state="Evaluating", step=step,
                     evaluationStep=step, evaluationLoss=value,
                     evaluationExamples=len(evaluation))
                return value
            finally:
                torch.set_rng_state(cpu_rng)
                if cuda_rng is not None:
                    torch.cuda.set_rng_state_all(cuda_rng)
                model.train()

        model.train()
        while step < spec["Steps"]:
            if (out / "pause.request").exists():
                if step:
                    save_checkpoint()
                emit(state="Interrupted", step=step, detail="Stopped at an optimizer boundary; checkpoint retained")
                return 0
            optimizer.zero_grad(set_to_none=True)
            ids = train[step % len(train)]
            loss = model(input_ids=ids, labels=ids).loss
            if not torch.isfinite(loss):
                raise ValueError("Non-finite loss; previous complete checkpoint retained.")
            loss.backward()
            grad = torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            if not torch.isfinite(grad):
                raise ValueError("Non-finite gradient; previous checkpoint retained.")
            optimizer.step()
            step += 1
            emit(event="optimizer", state="Training", step=step, epoch=step / len(train),
                 trainingExamples=len(train), validationEvery=validation_every,
                 loss=float(loss.detach().cpu()),
                 gpuAllocatedBytes=torch.cuda.memory_allocated() if device == "cuda" else None,
                 gpuPeakBytes=torch.cuda.max_memory_allocated() if device == "cuda" else None)
            if step % validation_every == 0 or step == spec["Steps"]:
                eval_loss = validate()
            if step % spec["CheckpointEvery"] == 0 or step == spec["Steps"]:
                save_checkpoint()
        # A resumed checkpoint may already be at the final optimizer step.
        if "evaluationLoss" not in state:
            eval_loss = validate()
        else:
            eval_loss = state["evaluationLoss"]
        final = out / "adapter"
        final.mkdir(exist_ok=True)
        model.save_pretrained(final, safe_serialization=True)
        tokenizer.save_pretrained(final)
        atomic(out / "adapter-manifest.json", dict(identity=identity_hash, files={p.name: digest(p) for p in final.iterdir() if p.is_file()}))
        emit(state="Completed", step=step, evaluationLoss=eval_loss, adapter=str(final), detail="Real LoRA optimizer steps completed; held-out loss is not factual/game quality")
        return 0
    except TrainingStop:
        # validate() restored RNG and training mode before reaching this handler.
        # No partial validation average is emitted as a full held-out result.
        if step:
            save_checkpoint()
        emit(state="Interrupted", step=step, detail="Stopped between validation examples; optimizer-boundary checkpoint retained")
        return 0
    except Exception as exc:
        import traceback
        traceback.print_exc(file=sys.stderr)
        emit(state="Failed", error=type(exc).__name__ + ": " + str(exc))
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--probe", action="store_true")
    args = parser.parse_args()
    sys.exit(run(args.config.resolve(), args.probe))
