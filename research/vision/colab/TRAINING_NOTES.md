# Mini Metro Vision v2 Training Notes

## Objective and boundary

The experiments address visual domain drift between a historical 2018 dataset and a current Mini Metro build. They cover six-class scene classification and single-class station detection. Inference is advisory: every output remains behind freshness, context-confidence, detector-confidence, and native-count consistency checks.

The dataset builder preserves the historical test split. Current pseudo-labels may enter training only and remain marked `pseudo_needs_human_review`. Correlated frames must be split by capture session, never randomly by image.

## Experiment matrix

The machine-readable matrix is [experiments.json](experiments.json). Each framework defaults to its lightweight model; set `RUN_FULL_SWEEP=True` for all configurations.

| Task | Framework | Lightweight | Larger | Initialization |
| --- | --- | --- | --- | --- |
| Scene classification | TensorFlow | MobileNetV3Small 192 | EfficientNetV2B0 224 | ImageNet |
| Scene classification | PyTorch | MobileNetV3 Small 192 | EfficientNet-B0 224 | ImageNet-1K |
| Station detection | TensorFlow/KerasCV | YOLOv8-XS 640 | YOLOv8-S 640 | COCO backbone |
| Station detection | PyTorch/torchvision | Faster R-CNN MBv3 320 FPN | Faster R-CNN MBv3 large FPN | COCO V1 |

Scene training freezes the backbone for the classification head and then fine-tunes upper layers at a lower learning rate. Station labels are collapsed into one `station` class because the live gate consumes presence, count, and confidence. Shape classification remains an offline diagnostic.

## Metrics and promotion gates

- Scene: accuracy, macro-F1, balanced accuracy, and 10-bin expected calibration error.
- Detection: precision, recall, and F1 at IoU 0.5, plus per-frame count MAE.
- Promotion requires historical-test scene macro-F1 >= 0.95 and detection F1@0.5 >= 0.95.
- It also requires station count MAE <= 0.5 on at least three independent, human-reviewed, current-build sessions that were not used for training.
- Runtime publication separately requires gameplay confidence >= 0.35, average box confidence >= 0.55, and visual/native station-count agreement within the dynamic tolerance.

Good offline metrics never automatically weaken runtime gates.

## Colab sequence

1. Run `01_dataset_audit.ipynb` and confirm there is no cross-split content leakage, invalid box, or pseudo-label in test.
2. Run `02_tensorflow_experiments.ipynb` on a GPU runtime.
3. Run `03_pytorch_experiments.ipynb` on the same data split and seed.
4. Retain `config.json`, per-epoch history, held-out metrics, native weights, and the SHA-256 manifest.
5. Validate the downloaded ZIP locally with `run.ps1 validate-model-bundle`.

Colab GPU type and lifetime are not guaranteed. The notebooks write checkpoints each epoch. Google Drive mounting is optional and must target the user's own storage.

## Recorded environment notes

The 2026-09-20 TensorFlow run used a Colab T4 image with TensorFlow 2.20. KerasCV 0.9's ragged-box encoder was incompatible with GPU XLA `RaggedTensorToTensor`, so detection explicitly used `jit_compile=False` while training still ran on the T4. Protobuf was constrained to a runtime compatible with the installed `tensorflow-metadata` generated code.

The lightweight TensorFlow and PyTorch bundles passed manifest/hash validation but failed promotion metrics and lacked three independent current-build sessions. A later local PyTorch activation used the explicit `allow_unpromoted` research override. Runtime startup still revalidates every weight hash and falls back to the classical backend on failure.

## Next data campaign

- At least 200 images per scene from at least ten sessions.
- Gameplay across 16:9 and 16:10, 1080p through 4K, multiple cities/themes, day/night, water geometry, and 3-40 stations.
- Preserve raw frames; never train from annotated preview images.
- Human-review all validation and test labels.
- Deduplicate by session and perceptual hash before splitting.
- Prioritize disagreement frames, low-confidence detections, rare shapes, HUD occlusion, and transitions.

## Weight naming

Use `{framework}-{task}-{backbone}-{dataset_version}-{timestamp}.{keras|weights.h5|pt}` and store the exact configuration, dependency versions, held-out metrics, and SHA-256 in the same bundle.

## Official references

- [TensorFlow transfer learning](https://www.tensorflow.org/tutorials/images/transfer_learning)
- [Keras Applications](https://keras.io/api/applications/)
- [KerasCV YOLOv8 example](https://keras.io/examples/vision/yolov8/)
- [TorchVision object-detection fine-tuning](https://docs.pytorch.org/tutorials/intermediate/torchvision_tutorial.html)
- [TorchVision Faster R-CNN MobileNetV3 FPN](https://docs.pytorch.org/vision/main/models/generated/torchvision.models.detection.fasterrcnn_mobilenet_v3_large_fpn.html)

