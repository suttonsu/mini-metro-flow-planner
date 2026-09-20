# Mini Metro Vision Research Sidecar

This is the optional visual safety layer beside the native C# planner. It keeps the useful SerpentAI concepts of immutable frames, a bounded frame buffer, and explicit transform stages while using a maintained, service-free implementation.

The sidecar never sends mouse or keyboard input. The C# plugin remains the only action executor and consumes only a short-lived visual consistency signal.

## Setup

```powershell
.\setup.ps1
```

The script creates an isolated `.venv` in this directory. Use `-TrainedModels` to install the optional PyTorch runtime requirements.

## Legacy-data audit and classical baseline

Provide your own rights-cleared archive in the expected historical layout:

```powershell
.\run.ps1 --archive C:\path\to\labelled-data.zip audit
.\run.ps1 --archive C:\path\to\labelled-data.zip train-context
.\run.ps1 --archive C:\path\to\labelled-data.zip evaluate
```

The safe context baseline is a NumPy prototype model and never deserializes the historical FastAI pickle. The classical station detector uses explainable OpenCV contour rules and reports precision, recall, F1, and matched-shape accuracy against XML labels.

On the historical held-out split used during development, the classical backend achieved 24/24 context classifications and station-detection TP=278, FP=0, FN=8 (precision 1.0, recall 0.9720, F1 0.9858). These results document that dataset only and are not a current-game benchmark.

## Trained model bundles

Validate and activate an extracted PyTorch bundle:

```powershell
.\setup.ps1 -TrainedModels
.\run.ps1 validate-model-bundle C:\path\to\bundle.zip
.\run.ps1 activate-model-bundle C:\path\to\extracted-bundle --allow-unpromoted
.\run.ps1 model-status
```

`watch`, `control`, and `detect` default to `--vision-backend auto`. A valid activation loads the PyTorch MobileNetV3 scene model and Faster R-CNN station detector. Missing dependencies, manifest errors, or weight-hash errors fall back to the classical backend.

Use `--vision-backend trained --strict-trained` to make trained-backend failure explicit, or `--vision-backend classical` to force the fallback. `--allow-unpromoted` is an auditable experiment override: it never rewrites `promotion_eligible`, lowers metric gates, or weakens the C# control thresholds.

## Single-image and live observation

```powershell
.\run.ps1 detect .\frame.png --output .\artifacts\frame-annotated.png
.\run.ps1 watch --seconds 60 --raw-dir .\artifacts\raw --annotated-dir .\artifacts\annotated
```

Live observation captures only the client area of a window titled `Mini Metro`. Each JSONL record can combine visual context, station count, confidence, runtime backend, and the latest authoritative C# snapshot from `Player.log`.

## Decision-control protocol

```powershell
.\run.ps1 control --seconds 3600 --interval 1
```

`control` publishes a versioned `key=value` snapshot through a temporary file followed by atomic replacement. Only an enabled snapshot no older than six seconds participates in decisions.

- Low gameplay-context or detector confidence is treated as untrusted visual evidence.
- A trusted scene whose visual/native station count differs by at least `max(2, ceil(native * 0.25))` enters `HoldStationMismatch`.
- `HoldContext` and `HoldStationMismatch` pause irreversible topology edits, while stations at native risk >= 0.72 may still receive emergency capacity.
- Trained-model loading publishes `context=model_loading` every 0.75 seconds and keeps the plugin in `HoldContext` until the first real result is atomically published.
- Missing, disabled, invalid, or stale state enters `NativeFallback`, so the sidecar cannot permanently stop the planner.

The visual smoke test first publishes a synthetic 99-vs-3 station mismatch, verifies that the planner holds with zero actions, launches real observation, and asserts that the first planner action occurs after the first `Aligned` log event.

## Dataset v2 builder

Generated datasets are ignored by Git. Build one from a rights-cleared legacy archive plus optional current observations:

```powershell
.\run.ps1 --archive C:\path\to\labelled-data.zip build-dataset `
  --output .\datasets\mini_metro_v2_seed `
  --current-observations .\artifacts\observations.jsonl `
  --current-frames .\artifacts\raw `
  --package .\artifacts\mini_metro_v2_seed.zip
```

The builder preserves the historical test split. Current pseudo-labels are admitted only to training and carry `pseudo_needs_human_review`. Validation checks split leakage, missing images, invalid boxes, duplicate content, and pseudo-label policy.

## Colab experiments

The [colab](colab) directory contains:

- `01_dataset_audit.ipynb`;
- `02_tensorflow_experiments.ipynb`;
- `03_pytorch_experiments.ipynb`;
- reusable Python training entry points;
- an eight-configuration experiment matrix;
- training notes, recorded results, and notebook validation.

All model notebooks export framework-native weights, configuration, history, held-out metrics, and a SHA-256 release manifest. Weights and output archives are generated artifacts and are not stored in this source repository.

## Verification

```powershell
.\verify.ps1 -Archive C:\path\to\labelled-data.zip
python -m unittest discover -s .\tests -v
python .\colab\validate_notebooks.py
```

From the repository root, a full game/vision test can be run with:

```powershell
.\runtime-smoke.ps1 `
  -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro' `
  -TimeoutSeconds 120 `
  -MinimumActions 5 `
  -VisionObserver `
  -VisionArchive C:\path\to\labelled-data.zip
```

`artifacts/`, `datasets/`, `.venv/`, game binaries, captured frames, and trained weights are intentionally excluded from the public repository.

