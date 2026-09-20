"""PyTorch/torchvision Colab training entrypoint embedded in the notebook."""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import random
import shutil
import zipfile
from pathlib import Path

import numpy as np
import torch
import torchvision
from PIL import Image
from sklearn.metrics import balanced_accuracy_score, confusion_matrix, f1_score
from torch import nn
from torch.utils.data import DataLoader, Dataset
from torchvision import transforms as transforms
from torchvision.datasets import ImageFolder
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
from torchvision.ops import box_iou


def prepare_dataset() -> tuple[Path, dict]:
    dataset_zip = Path(os.environ.get("MINI_METRO_DATASET_ZIP", "/content/mini_metro_v2_seed.zip"))
    work_root = Path(os.environ.get("MINI_METRO_WORK_ROOT", "/content/mini_metro_torch_work"))
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


def scene_loaders(root: Path, config: dict):
    size = int(config["image_size"])
    normalize = transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225])
    train_transform = transforms.Compose(
        [
            transforms.Resize((size, size)),
            transforms.RandomAffine(3, translate=(0.03, 0.03)),
            transforms.ToTensor(),
            normalize,
        ]
    )
    eval_transform = transforms.Compose(
        [transforms.Resize((size, size)), transforms.ToTensor(), normalize]
    )
    datasets = {
        "train": ImageFolder(root / "classification" / "train", train_transform),
        "val": ImageFolder(root / "classification" / "val", eval_transform),
        "test": ImageFolder(root / "classification" / "test", eval_transform),
    }
    loaders = {
        split: DataLoader(
            dataset,
            batch_size=int(config["batch_size"]),
            shuffle=split == "train",
            num_workers=2,
            pin_memory=True,
        )
        for split, dataset in datasets.items()
    }
    return loaders, datasets["train"].classes


def build_scene_model(config: dict, class_count: int):
    if config["backbone"] == "mobilenet_v3_small":
        model = torchvision.models.mobilenet_v3_small(
            weights=torchvision.models.MobileNet_V3_Small_Weights.IMAGENET1K_V1
        )
    elif config["backbone"] == "efficientnet_b0":
        model = torchvision.models.efficientnet_b0(
            weights=torchvision.models.EfficientNet_B0_Weights.IMAGENET1K_V1
        )
    else:
        raise ValueError(config["backbone"])
    model.classifier[-1] = nn.Linear(model.classifier[-1].in_features, class_count)
    return model


def scene_epoch(model, loader, device, optimizer=None):
    model.train(optimizer is not None)
    total_loss = 0.0
    truth: list[int] = []
    probabilities: list[list[float]] = []
    for images, labels in loader:
        images, labels = images.to(device), labels.to(device)
        if optimizer is not None:
            optimizer.zero_grad(set_to_none=True)
        with torch.set_grad_enabled(optimizer is not None):
            logits = model(images)
            loss = nn.functional.cross_entropy(logits, labels)
            if optimizer is not None:
                loss.backward()
                optimizer.step()
        total_loss += loss.item() * len(labels)
        truth.extend(labels.cpu().tolist())
        probabilities.extend(logits.softmax(1).detach().cpu().tolist())
    return total_loss / len(loader.dataset), np.asarray(truth), np.asarray(probabilities)


def expected_calibration_error(probabilities: np.ndarray, labels: np.ndarray, bins: int = 10) -> float:
    confidence = probabilities.max(axis=1)
    predictions = probabilities.argmax(axis=1)
    result = 0.0
    for lower, upper in zip(np.linspace(0, 1, bins + 1)[:-1], np.linspace(0, 1, bins + 1)[1:]):
        selected = (confidence > lower) & (confidence <= upper)
        if selected.any():
            result += float(selected.mean() * abs((predictions[selected] == labels[selected]).mean() - confidence[selected].mean()))
    return result


def train_scene(root: Path, config: dict, output_root: Path, device) -> dict:
    loaders, classes = scene_loaders(root, config)
    model = build_scene_model(config, len(classes)).to(device)
    for parameter in model.parameters():
        parameter.requires_grad = False
    for parameter in model.classifier.parameters():
        parameter.requires_grad = True
    run = output_root / config["id"]
    run.mkdir(parents=True, exist_ok=True)
    history: list[dict] = []
    best_loss = float("inf")
    phases = [
        (int(config["head_epochs"]), float(config["head_learning_rate"]), False),
        (int(config["fine_tune_epochs"]), float(config["fine_tune_learning_rate"]), True),
    ]
    for epochs, learning_rate, unfreeze in phases:
        if unfreeze:
            for parameter in model.parameters():
                parameter.requires_grad = True
        optimizer = torch.optim.AdamW(
            (parameter for parameter in model.parameters() if parameter.requires_grad),
            lr=learning_rate,
        )
        for _ in range(epochs):
            train_loss, train_truth, train_probabilities = scene_epoch(model, loaders["train"], device, optimizer)
            val_loss, val_truth, val_probabilities = scene_epoch(model, loaders["val"], device)
            history.append(
                {
                    "train_loss": train_loss,
                    "train_accuracy": float((train_probabilities.argmax(1) == train_truth).mean()),
                    "val_loss": val_loss,
                    "val_accuracy": float((val_probabilities.argmax(1) == val_truth).mean()),
                }
            )
            if val_loss < best_loss:
                best_loss = val_loss
                torch.save(model.state_dict(), run / "best.pt")
            (run / "history.json").write_text(json.dumps(history, indent=2), encoding="utf-8")
    model.load_state_dict(torch.load(run / "best.pt", map_location=device, weights_only=True))
    _, truth, probabilities = scene_epoch(model, loaders["test"], device)
    predictions = probabilities.argmax(1)
    metrics = {
        "accuracy": float((predictions == truth).mean()),
        "macro_f1": float(f1_score(truth, predictions, average="macro")),
        "balanced_accuracy": float(balanced_accuracy_score(truth, predictions)),
        "expected_calibration_error": expected_calibration_error(probabilities, truth),
        "classes": classes,
        "confusion_matrix": confusion_matrix(truth, predictions).tolist(),
    }
    (run / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    (run / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return {"id": config["id"], **metrics}


class StationDataset(Dataset):
    def __init__(self, root: Path, split: str, maximum_size: int):
        self.root = root / "detection"
        document = json.loads(
            (self.root / "annotations" / f"instances_{split}.json").read_text()
        )
        self.images = document["images"]
        self.annotations: dict[int, list[dict]] = {}
        for annotation in document["annotations"]:
            self.annotations.setdefault(int(annotation["image_id"]), []).append(annotation)
        self.maximum_size = maximum_size

    def __len__(self):
        return len(self.images)

    def __getitem__(self, index):
        info = self.images[index]
        image = transforms.functional.to_tensor(
            Image.open(self.root / info["file_name"]).convert("RGB")
        )
        scale = min(1.0, self.maximum_size / max(image.shape[-2:]))
        if scale < 1.0:
            image = transforms.functional.resize(
                image,
                [round(image.shape[-2] * scale), round(image.shape[-1] * scale)],
            )
        boxes = []
        for annotation in self.annotations.get(int(info["id"]), []):
            x_value, y_value, width, height = annotation["bbox"]
            boxes.append(
                [
                    x_value * scale,
                    y_value * scale,
                    (x_value + width) * scale,
                    (y_value + height) * scale,
                ]
            )
        target = {
            "boxes": torch.tensor(boxes, dtype=torch.float32).reshape(-1, 4),
            "labels": torch.ones(len(boxes), dtype=torch.int64),
            "image_id": torch.tensor(int(info["id"])),
        }
        return image, target


def collate(batch):
    return tuple(zip(*batch))


def build_detector(config: dict):
    if config["model"] == "fasterrcnn_mobilenet_v3_large_320_fpn":
        model = torchvision.models.detection.fasterrcnn_mobilenet_v3_large_320_fpn(
            weights=torchvision.models.detection.FasterRCNN_MobileNet_V3_Large_320_FPN_Weights.COCO_V1
        )
    else:
        model = torchvision.models.detection.fasterrcnn_mobilenet_v3_large_fpn(
            weights=torchvision.models.detection.FasterRCNN_MobileNet_V3_Large_FPN_Weights.COCO_V1
        )
    features = model.roi_heads.box_predictor.cls_score.in_features
    model.roi_heads.box_predictor = FastRCNNPredictor(features, 2)
    return model


def evaluate_detector(model, loader, device, score_threshold: float = 0.5) -> dict:
    model.eval()
    true_positive = false_positive = false_negative = 0
    count_errors: list[int] = []
    with torch.no_grad():
        for images, targets in loader:
            outputs = model([image.to(device) for image in images])
            for output, target in zip(outputs, targets):
                predicted = output["boxes"][output["scores"] >= score_threshold].cpu()
                truth = target["boxes"]
                count_errors.append(abs(len(predicted) - len(truth)))
                available = set(range(len(truth)))
                matched = 0
                if len(predicted) and len(truth):
                    overlaps = box_iou(predicted, truth)
                    for row in overlaps:
                        candidates = [(float(row[index]), index) for index in available]
                        if not candidates:
                            break
                        overlap, index = max(candidates)
                        if overlap >= 0.5:
                            available.remove(index)
                            matched += 1
                true_positive += matched
                false_positive += len(predicted) - matched
                false_negative += len(available)
    precision = true_positive / max(1, true_positive + false_positive)
    recall = true_positive / max(1, true_positive + false_negative)
    return {
        "precision_iou50": precision,
        "recall_iou50": recall,
        "f1_iou50": 2 * precision * recall / max(1e-9, precision + recall),
        "count_mae": float(np.mean(count_errors)),
    }


def train_detector(root: Path, config: dict, output_root: Path, device) -> dict:
    train_data = StationDataset(root, "train", int(config["max_image_size"]))
    test_data = StationDataset(root, "test", int(config["max_image_size"]))
    train_loader = DataLoader(
        train_data,
        batch_size=int(config["batch_size"]),
        shuffle=True,
        num_workers=2,
        collate_fn=collate,
    )
    test_loader = DataLoader(test_data, batch_size=1, shuffle=False, num_workers=2, collate_fn=collate)
    model = build_detector(config).to(device)
    optimizer = torch.optim.SGD(
        (parameter for parameter in model.parameters() if parameter.requires_grad),
        lr=float(config["learning_rate"]),
        momentum=0.9,
        weight_decay=5e-4,
    )
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=int(config["epochs"]))
    run = output_root / config["id"]
    run.mkdir(parents=True, exist_ok=True)
    history: list[dict] = []
    for epoch in range(int(config["epochs"])):
        model.train()
        total_loss = 0.0
        for images, targets in train_loader:
            images = [image.to(device) for image in images]
            targets = [{key: value.to(device) for key, value in target.items()} for target in targets]
            loss = sum(model(images, targets).values())
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            optimizer.step()
            total_loss += loss.item()
        scheduler.step()
        history.append({"epoch": epoch + 1, "train_loss": total_loss / max(1, len(train_loader))})
        torch.save(model.state_dict(), run / "last.pt")
        (run / "history.json").write_text(json.dumps(history, indent=2), encoding="utf-8")
    metrics = evaluate_detector(model, test_loader, device)
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
    seed = int(configs["seed"])
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    output_root = Path("/content/mini_metro_torch_outputs")
    output_root.mkdir(parents=True, exist_ok=True)
    full_sweep = bool(globals().get("RUN_FULL_SWEEP", False))
    scene_configs = configs["scene_classification"]["pytorch"]
    detector_configs = configs["station_detection"]["pytorch"]
    scene_results = [
        train_scene(root, item, output_root, device)
        for item in (scene_configs if full_sweep else scene_configs[:1])
    ]
    detector_results = [
        train_detector(root, item, output_root, device)
        for item in (detector_configs if full_sweep else detector_configs[:1])
    ]
    release = {
        "schema": "mini-metro-model-bundle-v1",
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "dataset": manifest,
        "framework": {"torch": torch.__version__, "torchvision": torchvision.__version__},
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
    archive_path = Path(shutil.make_archive("/content/mini_metro_torch_outputs", "zip", output_root))
    print(json.dumps(release, indent=2))
    print(f"Download {archive_path}")
    if bool(globals().get("AUTO_DOWNLOAD_OUTPUTS", True)):
        from google.colab import files

        files.download(str(archive_path))


if __name__ == "__main__":
    main()
