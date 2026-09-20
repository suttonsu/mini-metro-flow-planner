"""TensorFlow/KerasCV Colab training entrypoint embedded in the notebook."""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import random
import shutil
import zipfile
from pathlib import Path

import keras
import keras_cv
import numpy as np
import tensorflow as tf
from sklearn.metrics import balanced_accuracy_score, confusion_matrix, f1_score


def prepare_dataset() -> tuple[Path, dict]:
    dataset_zip = Path(os.environ.get("MINI_METRO_DATASET_ZIP", "/content/mini_metro_v2_seed.zip"))
    work_root = Path(os.environ.get("MINI_METRO_WORK_ROOT", "/content/mini_metro_tf_work"))
    if not dataset_zip.is_file():
        from google.colab import files

        print("Upload mini_metro_v2_seed.zip")
        uploaded = files.upload()
        dataset_zip = Path("/content") / next(iter(uploaded))
    if work_root.exists():
        shutil.rmtree(work_root)
    work_root.mkdir(parents=True)
    with zipfile.ZipFile(dataset_zip) as archive:
        archive.extractall(work_root)
    roots = [path.parent for path in work_root.rglob("manifest.json")]
    if len(roots) != 1:
        raise ValueError(f"expected one dataset root, found {roots}")
    return roots[0], json.loads((roots[0] / "manifest.json").read_text(encoding="utf-8"))


def scene_datasets(root: Path, config: dict):
    size = int(config["image_size"])
    common = {
        "image_size": (size, size),
        "batch_size": int(config["batch_size"]),
        "label_mode": "int",
    }
    train = keras.utils.image_dataset_from_directory(
        root / "classification" / "train", shuffle=True, seed=SEED, **common
    )
    classes = train.class_names
    val = keras.utils.image_dataset_from_directory(
        root / "classification" / "val", shuffle=False, **common
    )
    test = keras.utils.image_dataset_from_directory(
        root / "classification" / "test", shuffle=False, **common
    )
    return (
        train.prefetch(tf.data.AUTOTUNE),
        val.prefetch(tf.data.AUTOTUNE),
        test.prefetch(tf.data.AUTOTUNE),
        classes,
    )


def build_scene_model(config: dict, class_count: int):
    size = int(config["image_size"])
    inputs = keras.Input((size, size, 3))
    values = keras.layers.RandomTranslation(0.03, 0.03)(inputs)
    values = keras.layers.RandomZoom(0.08)(values)
    backbone_type = getattr(keras.applications, config["backbone"])
    backbone = backbone_type(
        include_top=False,
        weights="imagenet",
        input_shape=(size, size, 3),
    )
    backbone.trainable = False
    values = backbone(values, training=False)
    values = keras.layers.GlobalAveragePooling2D()(values)
    values = keras.layers.Dropout(float(config["dropout"]))(values)
    outputs = keras.layers.Dense(class_count, activation="softmax")(values)
    return keras.Model(inputs, outputs), backbone


def expected_calibration_error(probabilities: np.ndarray, labels: np.ndarray, bins: int = 10) -> float:
    confidence = probabilities.max(axis=1)
    predictions = probabilities.argmax(axis=1)
    edges = np.linspace(0.0, 1.0, bins + 1)
    result = 0.0
    for lower, upper in zip(edges[:-1], edges[1:]):
        selected = (confidence > lower) & (confidence <= upper)
        if selected.any():
            accuracy = (predictions[selected] == labels[selected]).mean()
            result += float(selected.mean() * abs(accuracy - confidence[selected].mean()))
    return result


def train_scene(root: Path, config: dict, output_root: Path) -> dict:
    train, val, test, classes = scene_datasets(root, config)
    model, backbone = build_scene_model(config, len(classes))
    run = output_root / config["id"]
    run.mkdir(parents=True, exist_ok=True)
    callbacks = [
        keras.callbacks.ModelCheckpoint(run / "best.weights.h5", save_weights_only=True, save_best_only=True),
        keras.callbacks.CSVLogger(run / "history.csv"),
        keras.callbacks.EarlyStopping(patience=4, restore_best_weights=True),
    ]
    model.compile(
        optimizer=keras.optimizers.Adam(config["head_learning_rate"]),
        loss="sparse_categorical_crossentropy",
        metrics=["accuracy"],
    )
    model.fit(train, validation_data=val, epochs=int(config["head_epochs"]), callbacks=callbacks)
    backbone.trainable = True
    for layer in backbone.layers[:-25]:
        layer.trainable = False
    for layer in backbone.layers:
        if isinstance(layer, keras.layers.BatchNormalization):
            layer.trainable = False
    model.compile(
        optimizer=keras.optimizers.Adam(config["fine_tune_learning_rate"]),
        loss="sparse_categorical_crossentropy",
        metrics=["accuracy"],
    )
    model.fit(train, validation_data=val, epochs=int(config["fine_tune_epochs"]), callbacks=callbacks)
    probabilities = model.predict(test)
    labels = np.concatenate([batch_labels.numpy() for _, batch_labels in test])
    predictions = probabilities.argmax(axis=1)
    metrics = {
        "accuracy": float((predictions == labels).mean()),
        "macro_f1": float(f1_score(labels, predictions, average="macro")),
        "balanced_accuracy": float(balanced_accuracy_score(labels, predictions)),
        "expected_calibration_error": expected_calibration_error(probabilities, labels),
        "classes": classes,
        "confusion_matrix": confusion_matrix(labels, predictions).tolist(),
    }
    model.save(run / "model.keras")
    (run / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    (run / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return {"id": config["id"], **metrics}


def coco_examples(root: Path, split: str):
    document = json.loads(
        (root / "detection" / "annotations" / f"instances_{split}.json").read_text()
    )
    annotations: dict[int, list[dict]] = {}
    for annotation in document["annotations"]:
        annotations.setdefault(int(annotation["image_id"]), []).append(annotation)
    for image in document["images"]:
        pixels = tf.io.decode_png(tf.io.read_file(str(root / "detection" / image["file_name"])), channels=3)
        boxes = []
        for annotation in annotations.get(int(image["id"]), []):
            x_value, y_value, width, height = annotation["bbox"]
            boxes.append([x_value, y_value, x_value + width, y_value + height])
        yield {
            "images": pixels,
            "bounding_boxes": {
                "boxes": np.asarray(boxes, dtype=np.float32),
                "classes": np.zeros(len(boxes), dtype=np.float32),
            },
        }


DETECTION_SIGNATURE = {
    "images": tf.TensorSpec((None, None, 3), tf.uint8),
    "bounding_boxes": {
        "boxes": tf.TensorSpec((None, 4), tf.float32),
        "classes": tf.TensorSpec((None,), tf.float32),
    },
}


def detection_dataset(root: Path, split: str, config: dict, training: bool):
    dataset = tf.data.Dataset.from_generator(
        lambda: coco_examples(root, split), output_signature=DETECTION_SIGNATURE
    )
    if training:
        dataset = dataset.shuffle(128, seed=SEED)
    resize = keras_cv.layers.JitteredResize(
        target_size=(int(config["image_size"]), int(config["image_size"])),
        scale_factor=(0.9, 1.1) if training else (1.0, 1.0),
        bounding_box_format="xyxy",
    )
    return (
        dataset.ragged_batch(int(config["batch_size"]), drop_remainder=False)
        .map(resize, num_parallel_calls=tf.data.AUTOTUNE)
        .prefetch(tf.data.AUTOTUNE)
    )


def box_iou(left: np.ndarray, right: np.ndarray) -> float:
    x1 = max(float(left[0]), float(right[0]))
    y1 = max(float(left[1]), float(right[1]))
    x2 = min(float(left[2]), float(right[2]))
    y2 = min(float(left[3]), float(right[3]))
    intersection = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    left_area = max(0.0, float(left[2] - left[0])) * max(0.0, float(left[3] - left[1]))
    right_area = max(0.0, float(right[2] - right[0])) * max(0.0, float(right[3] - right[1]))
    union = left_area + right_area - intersection
    return intersection / union if union > 0 else 0.0


def _sample_values(value, index: int) -> np.ndarray:
    sample = value[index]
    if isinstance(sample, tf.RaggedTensor):
        sample = sample.to_tensor()
    if hasattr(sample, "numpy"):
        sample = sample.numpy()
    return np.asarray(sample)


def evaluate_detector(
    model,
    dataset,
    *,
    score_threshold: float = 0.5,
    iou_threshold: float = 0.5,
) -> dict:
    true_positive = 0
    false_positive = 0
    false_negative = 0
    count_errors: list[int] = []
    for batch in dataset:
        images = batch["images"]
        targets = batch["bounding_boxes"]
        predictions = model.predict(images, verbose=0)
        batch_size = int(tf.shape(images)[0])
        for index in range(batch_size):
            truth_boxes = _sample_values(targets["boxes"], index).reshape((-1, 4))
            truth_classes = _sample_values(targets["classes"], index).reshape((-1,))
            truth_boxes = truth_boxes[truth_classes >= 0]

            predicted_boxes = _sample_values(predictions["boxes"], index).reshape((-1, 4))
            scores = _sample_values(predictions["confidence"], index).reshape((-1,))
            if "num_detections" in predictions:
                limit = int(_sample_values(predictions["num_detections"], index).reshape(-1)[0])
                predicted_boxes = predicted_boxes[:limit]
                scores = scores[:limit]
            finite = np.isfinite(predicted_boxes).all(axis=1) & np.isfinite(scores)
            positive_size = (
                (predicted_boxes[:, 2] > predicted_boxes[:, 0])
                & (predicted_boxes[:, 3] > predicted_boxes[:, 1])
            )
            selected = finite & positive_size & (scores >= score_threshold)
            predicted_boxes = predicted_boxes[selected]
            scores = scores[selected]
            order = np.argsort(-scores)
            predicted_boxes = predicted_boxes[order]

            matched_truth: set[int] = set()
            for predicted_box in predicted_boxes:
                best_index = -1
                best_iou = 0.0
                for truth_index, truth_box in enumerate(truth_boxes):
                    if truth_index in matched_truth:
                        continue
                    value = box_iou(predicted_box, truth_box)
                    if value > best_iou:
                        best_iou = value
                        best_index = truth_index
                if best_index >= 0 and best_iou >= iou_threshold:
                    matched_truth.add(best_index)
                    true_positive += 1
                else:
                    false_positive += 1
            false_negative += len(truth_boxes) - len(matched_truth)
            count_errors.append(abs(len(predicted_boxes) - len(truth_boxes)))

    precision = true_positive / max(1, true_positive + false_positive)
    recall = true_positive / max(1, true_positive + false_negative)
    f1 = 2 * precision * recall / max(1e-12, precision + recall)
    return {
        "precision_iou50": float(precision),
        "recall_iou50": float(recall),
        "f1_iou50": float(f1),
        "count_mae": float(np.mean(count_errors)) if count_errors else 0.0,
        "score_threshold": score_threshold,
        "true_positive": true_positive,
        "false_positive": false_positive,
        "false_negative": false_negative,
    }


def train_detector(root: Path, config: dict, output_root: Path) -> dict:
    train = detection_dataset(root, "train", config, True)
    val = detection_dataset(root, "val", config, False)
    test = detection_dataset(root, "test", config, False)
    backbone = keras_cv.models.YOLOV8Backbone.from_preset(config["backbone_preset"])
    model = keras_cv.models.YOLOV8Detector(
        num_classes=1,
        bounding_box_format="xyxy",
        backbone=backbone,
        fpn_depth=int(config["fpn_depth"]),
    )
    model.compile(
        optimizer=keras.optimizers.Adam(config["learning_rate"]),
        classification_loss="binary_crossentropy",
        box_loss="ciou",
        # Keras 3 may select XLA automatically on a GPU. KerasCV's label
        # encoder still uses RaggedTensorToTensor, which has no XLA GPU
        # kernel on current Colab runtimes; eager/graph GPU execution works.
        jit_compile=False,
    )
    run = output_root / config["id"]
    run.mkdir(parents=True, exist_ok=True)
    callbacks = [
        keras.callbacks.ModelCheckpoint(run / "best.weights.h5", save_weights_only=True, save_best_only=True),
        keras.callbacks.CSVLogger(run / "history.csv"),
        keras.callbacks.EarlyStopping(patience=6, restore_best_weights=True),
    ]
    history = model.fit(
        train,
        validation_data=val,
        epochs=int(config["epochs"]),
        callbacks=callbacks,
    )
    model.save_weights(run / "final.weights.h5")
    metrics = evaluate_detector(model, test)
    metrics["best_val_loss"] = float(min(history.history["val_loss"]))
    (run / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    (run / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return {"id": config["id"], **metrics}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    root, manifest = prepare_dataset()
    configs = globals().get("EXPERIMENTS")
    if configs is None:
        configs = json.loads(Path("/content/experiments.json").read_text())
    global SEED
    SEED = int(configs["seed"])
    random.seed(SEED)
    np.random.seed(SEED)
    tf.random.set_seed(SEED)
    output_root = Path("/content/mini_metro_tf_outputs")
    output_root.mkdir(parents=True, exist_ok=True)
    full_sweep = bool(globals().get("RUN_FULL_SWEEP", False))
    scene_configs = configs["scene_classification"]["tensorflow"]
    detector_configs = configs["station_detection"]["tensorflow"]
    scene_results = [train_scene(root, item, output_root) for item in (scene_configs if full_sweep else scene_configs[:1])]
    detector_results = [train_detector(root, item, output_root) for item in (detector_configs if full_sweep else detector_configs[:1])]
    release = {
        "schema": "mini-metro-model-bundle-v1",
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "dataset": manifest,
        "framework": {
            "tensorflow": tf.__version__,
            "keras": keras.__version__,
            "keras_cv": keras_cv.__version__,
        },
        "scene_results": scene_results,
        "detection_results": detector_results,
        "promotion_evidence": {
            "current_session_evaluations": [],
            "note": "Add three independent, human-reviewed current-build sessions before promotion.",
        },
    }
    release["files"] = {
        str(path.relative_to(output_root)): sha256(path)
        for path in output_root.rglob("*") if path.is_file()
    }
    (output_root / "release-manifest.json").write_text(json.dumps(release, indent=2), encoding="utf-8")
    archive_path = Path(shutil.make_archive("/content/mini_metro_tf_outputs", "zip", output_root))
    print(json.dumps(release, indent=2))
    print(f"Download {archive_path}")
    if bool(globals().get("AUTO_DOWNLOAD_OUTPUTS", True)):
        from google.colab import files

        files.download(str(archive_path))


if __name__ == "__main__":
    main()
