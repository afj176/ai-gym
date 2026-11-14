"""Real CPU LoRA acceptance on a tiny, locally constructed synthetic model. No downloads.

This proves optimizer/checkpoint mechanisms, not production model quality or GPU fit.
"""
import json
from pathlib import Path
import hashlib
import subprocess
import sys
import time

root = Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
import torch
from transformers import GPT2Config, GPT2LMHeadModel, PreTrainedTokenizerFast
from tokenizers import Tokenizer
from tokenizers.models import WordLevel
from tokenizers.pre_tokenizers import Whitespace

base = root / "base"
base.mkdir(exist_ok=True)
torch.manual_seed(17)
vocab = {w: i for i, w in enumerate(["[UNK]", "[PAD]", "[EOS]", "system", "user", "assistant", "source", "alpha", "beta", "gamma", "clue", "word", "a", "small", "test", "example", "answer"])}
raw = Tokenizer(WordLevel(vocab, unk_token="[UNK]"))
raw.pre_tokenizer = Whitespace()
tokenizer = PreTrainedTokenizerFast(tokenizer_object=raw, unk_token="[UNK]", pad_token="[PAD]", eos_token="[EOS]")
tokenizer.chat_template = "{% for message in messages %}{{ message['role'] + ' ' + message['content'] + ' ' }}{% endfor %}{{ eos_token }}"
tokenizer.save_pretrained(base)
GPT2LMHeadModel(GPT2Config(vocab_size=len(vocab), n_positions=2048, n_embd=32, n_layer=1, n_head=2, bos_token_id=2, eos_token_id=2)).save_pretrained(base)
bundle = root / "bundle"
bundle.mkdir(exist_ok=True)
for name, words in (("train", ["alpha", "beta", "gamma"]), ("evaluation", ["small"])):
    (bundle / (name + ".jsonl")).write_text("\n".join(json.dumps(dict(messages=[dict(role="system", content="word clue"), dict(role="user", content="source " + word), dict(role="assistant", content="answer " + word)])) for word in words) + "\n", encoding="utf-8")
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
(bundle / "manifest.json").write_text(json.dumps(dict(Version=1, Kind="ai-gym-training-handoff", Examples=[dict(EntityHash=hashlib.sha256(word.encode()).hexdigest(), Split=split) for split, words in (("train", ["alpha", "beta", "gamma"]), ("evaluation", ["small"])) for word in words], Files=dict(TrainSha256=sha(bundle / "train.jsonl"), EvaluationSha256=sha(bundle / "evaluation.jsonl"))), indent=2))
script = Path(__file__).with_name("Train-LocalAdapter.py")


def launch(name, pause=False, probe=False):
    out = root / name
    out.mkdir(exist_ok=True)
    request = out / "pause.request"
    request.unlink(missing_ok=True)
    spec = dict(Base=str(base), Bundle=str(bundle), Output=str(out), Steps=6, SequenceLength=64, Rank=2, LearningRate=.001, Device="cpu", Seed=3407, CheckpointEvery=1)
    config = out / "config.json"
    config.write_text(json.dumps(spec))
    args = [sys.executable, "-u", str(script), "--config", str(config)] + (["--probe"] if probe else [])
    with (out / "acceptance-runtime.log").open("a") as log:
        process = subprocess.Popen(args, stdout=log, stderr=log)
        deadline = time.monotonic() + 180
        while process.poll() is None:
            status = out / "status.json"
            if pause and status.exists():
                try:
                    current = json.loads(status.read_text())
                except (OSError, json.JSONDecodeError):
                    time.sleep(.02)
                    continue
                if current.get("state") in ("Training", "Evaluating") and 2 <= current.get("step", 0) < 6:
                    request.write_text("acceptance pause at real optimizer boundary")
            if time.monotonic() > deadline:
                process.kill()
                raise AssertionError("Local training acceptance timed out; see " + str(out))
            time.sleep(.02)
    result = json.loads((out / "status.json").read_text())
    assert process.returncode == 0, result
    return out, result


_, probe = launch("resumed", probe=True)
assert probe["state"] == "Ready"
print("PASS actual offline metadata/tokenizer/base/data fingerprint preflight", flush=True)
out, stopped = launch("resumed", pause=True)
assert stopped["state"] == "Interrupted" and 2 <= stopped["step"] < 6, stopped
assert (out / "latest.json").exists()
print("PASS actual optimizer work paused with committed adapter/optimizer/RNG checkpoint", flush=True)
out, complete = launch("resumed")
assert complete["state"] == "Completed" and complete["step"] == 6
assert isinstance(complete["evaluationLoss"], float)
observations = [json.loads(line) for line in (out / "events.jsonl").read_text(encoding="utf-8").splitlines()]
validation = {event["step"]: event for event in observations if event.get("event") == "validation"}
assert len(validation) >= 2 and 6 in validation
assert all(event["evaluationStep"] == step and event["evaluationExamples"] == 1 for step, event in validation.items())
assert complete["epoch"] == 2 and complete["trainingExamples"] == 3
assert complete["gpuAllocatedBytes"] is None
print("PASS step-bound held-out series, actual epoch and explicit unavailable CPU GPU telemetry", flush=True)
print("PASS actual checkpoint resume completed six steps and held-out loss", flush=True)
baseline, full = launch("uninterrupted")
from safetensors.torch import load_file
resumed_weights = load_file(str(out / "adapter" / "adapter_model.safetensors"))
full_weights = load_file(str(baseline / "adapter" / "adapter_model.safetensors"))
assert any("lora_B" in k and torch.count_nonzero(v) > 0 for k, v in resumed_weights.items())
assert all(torch.equal(v, full_weights[k]) for k, v in resumed_weights.items())
print("PASS resumed LoRA weights exactly match uninterrupted seeded optimizer run", flush=True)
# Changing the data must fail before loading/training.
original = (bundle / "train.jsonl").read_text()
(bundle / "train.jsonl").write_text(original + original.splitlines()[0] + "\n")
try:
    try:
        launch("resumed")
        raise AssertionError("Changed dataset accepted")
    except AssertionError as error:
        assert "integrity" in str(error).lower(), str(error)
    print("PASS changed bundle rejected before model updates", flush=True)
finally:
    (bundle / "train.jsonl").write_text(original)
    # Restore readable success status by revalidating/resuming the exact completed run.
    launch("resumed")
latest = json.loads((out / "latest.json").read_text())["directory"]
optimizer = out / latest / "optimizer.pt"
original_optimizer = optimizer.read_bytes()
optimizer.write_bytes(original_optimizer[:-1] + bytes([original_optimizer[-1] ^ 1]))
try:
    try:
        launch("resumed")
        raise AssertionError("Changed optimizer checkpoint accepted")
    except AssertionError as error:
        assert "checkpoint" in str(error).lower(), str(error)
    print("PASS changed optimizer checkpoint rejected before restoring state", flush=True)
finally:
    optimizer.write_bytes(original_optimizer)
    launch("resumed")
assert len(list(out.glob("checkpoint-*"))) <= 3
print("PASS checkpoint retention keeps at most three complete checkpoints", flush=True)
print("8 real local training acceptance checks passed. " + str(root), flush=True)
