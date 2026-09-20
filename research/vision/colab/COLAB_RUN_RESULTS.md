# Mini Metro Vision v2 Colab Results

Run date: 2026-09-20  
Hardware: Google Colab T4  
Dataset: `mini-metro-v2-seed`

- Scene train/validation/test: 63/12/24.
- Detection train/validation/test: 86/17/20.
- The test split was used only for final evaluation.

## Lightweight model comparison

| Task | Framework and model | Accuracy | Macro-F1 | Balanced accuracy | ECE |
| --- | --- | ---: | ---: | ---: | ---: |
| Scene | TensorFlow MobileNetV3Small 192 | 0.9167 | 0.9111 | 0.9167 | 0.3448 |
| Scene | PyTorch MobileNetV3 Small 192 | 0.8750 | 0.8545 | 0.8750 | 0.1312 |

| Task | Framework and model | Precision@0.5 | Recall@0.5 | F1@0.5 | Count MAE |
| --- | --- | ---: | ---: | ---: | ---: |
| Detection | TensorFlow/KerasCV YOLOv8-XS 640 | 0.9158 | 0.6560 | 0.7645 | 4.2 |
| Detection | PyTorch Faster R-CNN MBv3 320 FPN | 0.8946 | 0.9196 | 0.9069 | 0.8 |

The TensorFlow scene classifier had higher accuracy on this very small test set but worse calibration. The PyTorch scene classifier mislabeled three of four `round_menu` samples as `game_play`. PyTorch detection had materially better recall, F1, and count error than the TensorFlow baseline, but still missed the joint F1/count-MAE promotion gate.

## Environments and artifacts

- TensorFlow 2.20.0, Keras 3.13.2, KerasCV 0.9.0.
- PyTorch 2.11.0+cu128, torchvision 0.26.0+cu128.
- TensorFlow outputs included scene `.keras`/`.weights.h5` and detector `.weights.h5` files.
- PyTorch outputs included scene `best.pt` and detector `last.pt`.

Both generated bundles passed their SHA-256 release-manifest checks. They are not committed to this repository.

## Release decision

Both bundles remain `promotion_eligible=false` because scene macro-F1 was below 0.95, detection did not simultaneously reach F1 >= 0.95 and count MAE <= 0.5, and three independent current-build validation sessions were not available.

An explicit `allow_unpromoted` override later activated the PyTorch pair for a controlled local experiment. That override did not rewrite promotion status or weaken runtime visual/native consistency gates.

The next priority is more `round_menu` and current-build gameplay data, human review of current pseudo-labels, and validation-set threshold calibration before the larger-model sweep.

