# Colab experiments

1. Build `artifacts/mini_metro_v2_seed.zip` with the project CLI shown in the parent README.
2. Upload and run `01_dataset_audit.ipynb`; its local executed copy has already passed top-to-bottom validation.
3. Run `02_tensorflow_experiments.ipynb` and `03_pytorch_experiments.ipynb` on a Colab GPU.

The notebooks are self-contained and read `experiments.json` when it is uploaded beside the dataset; otherwise they use an embedded copy. They always export framework-native weights plus a JSON manifest, held-out metrics, training history, and hashes. Output ZIPs download automatically unless `AUTO_DOWNLOAD_OUTPUTS=False`. See `TRAINING_NOTES.md` for gates and data policy.

Validate a downloaded archive before using it: `..\run.ps1 validate-model-bundle <archive.zip>`. Integrity and model promotion are deliberately separate: valid Colab output remains research-only until it also carries three independent current-build session evaluations. The current PyTorch bundle is locally active only through an explicit, audited `--allow-unpromoted` experiment override; this does not change its formal promotion status.

The model notebooks default to one lightweight configuration per task. Change `RUN_FULL_SWEEP` to `True` for all eight configurations. Colab browser upload requires the ChatGPT Chrome extension setting **Allow access to file URLs** when the notebooks are driven through Codex.
