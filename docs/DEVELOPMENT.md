# Development notes

## Layout

| Path | Contents |
| --- | --- |
| `src/AiGym` | The WinUI 3 app (.NET 10, unpackaged, self-contained Windows App SDK). |
| `scripts` | `Publish.ps1`, the Python helpers the app runs (`Train-LocalAdapter.py`, `Serve-Transformers.py`, `Setup-QuantizedRuntime.py`), the model adapter table and verification scripts. |
| `tests` | Console check suites (no model, GPU or network needed). |
| `packaging/AiGym.iss` | Inno Setup script used by `Publish.ps1 -Installer`. |
| `brand` | Logo kit, tokens, avatars, social graphics and the Manrope licence. |
| `site` | The one-page website published to ai-gym.site. |

## Profile folder

`Store.Root` decides where everything is saved, in this order:

1. `AIGYM_DATA_HOME`, if set.
2. `data\` next to `AiGym.exe`, if `portable.flag` exists there.
3. `%LOCALAPPDATA%\AiGym`.

Use an isolated `AIGYM_DATA_HOME` while developing so your own profile is never touched. Main files:
`datasets\*.json`, `agent-labs\training\<run id>\` (config, status, `events.jsonl`, checkpoints,
adapter), `comparisons\*.json`, `agent-labs\*.json` (Agent lab experiments), `ai-connections.json`,
`model-library.json` and `appearance.json`.

## Runtimes

AI Gym does not bundle model weights or Python. It looks for `llama-server.exe` and `python.exe`
through `AIGYM_LLAMA_SERVER` / `AIGYM_PYTHON`, an active virtual or conda environment, and `PATH`;
you can change either under Runtime setup, and the choice is kept in `model-library.json`. Training
needs a Python environment with `torch`, `transformers` and `peft` and a base model folder with
`config.json` and safetensors weights; CUDA is used when you pick the `cuda` device.

## Verification

```powershell
dotnet run --project tests\AiGym.DatasetChecks -- $env:TEMP
dotnet run --project tests\AiGym.LabChecks     -- $env:TEMP
dotnet run --project tests\AiGym.ModelChecks   -- $env:TEMP
python scripts\Verify-AiPlatform.py                  # product copy has no machine-specific assumptions
python scripts\Verify-LocalTraining.py $env:TEMP\lt  # real CPU LoRA run on a tiny synthetic model (needs torch/transformers/peft)
```

A passing build or check suite shows the mechanics work; it says nothing about model quality.
