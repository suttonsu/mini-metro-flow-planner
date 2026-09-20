"""Generate self-contained Colab notebooks with nbformat."""

from __future__ import annotations

import json
from pathlib import Path

import nbformat as nbf


ROOT = Path(__file__).resolve().parent
EXPERIMENTS = json.loads((ROOT / "experiments.json").read_text(encoding="utf-8"))


def markdown(text: str):
    return nbf.v4.new_markdown_cell(text.strip())


def code(text: str):
    return nbf.v4.new_code_cell(text.strip())


def document(name: str, cells: list):
    return nbf.v4.new_notebook(
        cells=cells,
        metadata={
            "kernelspec": {"display_name": "Python 3", "language": "python", "name": "python3"},
            "language_info": {"name": "python", "version": "3.x"},
            "accelerator": "GPU",
            "colab": {"name": name, "provenance": [], "gpuType": "T4"},
        },
    )


def read_script(name: str) -> str:
    return (ROOT / name).read_text(encoding="utf-8")


def audit_notebook():
    return document(
        "01_dataset_audit.ipynb",
        [
            markdown("""
# Mini Metro Vision v2 — dataset audit

## tl;dr

This notebook validates the packaged dataset before GPU training. It checks split isolation, image presence, bounding-box bounds, review status, class balance, and representative labels.
"""),
            markdown("""
## Context & Methods

### Key Assumptions

- The legacy test split remains held out.
- Current-version detector output is training-only pseudo-label data.
- Correlated current frames are grouped in the same split.
"""),
            markdown("## Data\n\nUpload `mini_metro_v2_seed.zip` when prompted, or set `MINI_METRO_DATASET_ZIP` for a local run."),
            code(read_script("dataset_audit_colab.py")),
            markdown("""
## Results

A clean run prints the split counts and renders two figures: scene balance and one annotated example per split. Any leakage, missing image, invalid box, or unreviewed validation/test label stops execution.

## Takeaways

Use this exact package in both framework notebooks. Do not promote pseudo-label metrics as held-out model quality.
"""),
        ],
    )


def training_notebook(framework: str):
    if framework == "tensorflow":
        script = "train_tensorflow_colab.py"
        install = '%pip install -q "tensorflow>=2.20,<2.22" "keras-cv==0.9.0" "protobuf>=6.31.1,<7" "scikit-learn>=1.4" "pycocotools>=2.0.7"'
        title = "TensorFlow / KerasCV"
        output = "/content/mini_metro_tf_outputs.zip"
    else:
        script = "train_pytorch_colab.py"
        install = '%pip install -q "torch>=2.4" "torchvision>=0.19" "scikit-learn>=1.4" "pycocotools>=2.0.7"'
        title = "PyTorch / torchvision"
        output = "/content/mini_metro_torch_outputs.zip"
    return document(
        f"{'02' if framework == 'tensorflow' else '03'}_{framework}_experiments.ipynb",
        [
            markdown(f"""
# Mini Metro Vision v2 — {title} experiments

## Goal

Run transfer-learning experiments for six-way scene classification and single-class station detection. ImageNet initializes the scene backbone; COCO initializes the detection backbone.
"""),
            markdown("## Setup\n\nChoose a Colab GPU runtime. The next cell installs only the framework-specific experiment dependencies."),
            code(install),
            markdown("### Experiment controls\n\nThe default executes the light configuration. Set `RUN_FULL_SWEEP=True` to run both model sizes."),
            code(
                "EXPERIMENTS = " + repr(EXPERIMENTS) + "\n"
                "RUN_FULL_SWEEP = False\n"
                "AUTO_DOWNLOAD_OUTPUTS = True\n"
                "print('Configured experiments:', len(EXPERIMENTS['scene_classification']['"
                + framework
                + "']), 'scene and', len(EXPERIMENTS['station_detection']['"
                + framework
                + "']), 'detection')"
            ),
            markdown("## Steps\n\nUpload the dataset package when prompted. Training writes checkpoints, histories, metrics, exact configs, dependency versions, and hashes."),
            code(read_script(script)),
            markdown(f"""
## Checks

The final code cell prints the release manifest, creates `{output}`, and downloads it when `AUTO_DOWNLOAD_OUTPUTS=True`. Keep that archive before the Colab VM expires.

## Next Steps

Compare the held-out metrics with `TRAINING_NOTES.md`. A decreasing training loss alone does not qualify a weight for realtime control.
"""),
        ],
    )


def main() -> None:
    outputs = {
        "01_dataset_audit.ipynb": audit_notebook(),
        "02_tensorflow_experiments.ipynb": training_notebook("tensorflow"),
        "03_pytorch_experiments.ipynb": training_notebook("pytorch"),
    }
    for name, notebook in outputs.items():
        nbf.validate(notebook)
        nbf.write(notebook, ROOT / name)
        print(ROOT / name)


if __name__ == "__main__":
    main()
