"""Load an explicitly activated Colab model bundle for live vision inference.

The runtime keeps activation separate from training output.  A bundle must pass
the integrity validator, and an unpromoted bundle can only be activated through
an explicit override.  Importing this module does not import PyTorch; the heavy
runtime is loaded lazily only when an active bundle is selected.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from .context_classifier import Prediction, PrototypeContextClassifier
from .dataset import LegacyArchive
from .model_bundle import validate_model_bundle
from .station_detector import StationDetection, StationDetector


PROJECT_DIR = Path(__file__).resolve().parents[1]
DEFAULT_ACTIVATION = PROJECT_DIR / "artifacts" / "active-model.json"


class RuntimeModelError(RuntimeError):
    """Raised when an activated bundle cannot be used safely."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)


def _manifest(bundle: Path) -> dict[str, Any]:
    manifest_path = bundle / "release-manifest.json"
    if not manifest_path.is_file():
        raise RuntimeModelError(f"release manifest not found: {manifest_path}")
    return json.loads(manifest_path.read_text(encoding="utf-8"))


def _run_ids(manifest: dict[str, Any]) -> tuple[str, str]:
    scene = manifest.get("scene_results") or []
    detection = manifest.get("detection_results") or []
    if not scene or not detection:
        raise RuntimeModelError("bundle must contain scene and detection results")
    return str(scene[0]["id"]), str(detection[0]["id"])


def activate_model_bundle(
    bundle: str | Path,
    activation_path: str | Path = DEFAULT_ACTIVATION,
    *,
    allow_unpromoted: bool = False,
) -> dict[str, Any]:
    """Activate an unpacked PyTorch bundle after integrity and policy checks."""

    root = Path(bundle).resolve()
    if not root.is_dir():
        raise RuntimeModelError("live activation requires an unpacked bundle directory")
    validation = validate_model_bundle(root)
    if not validation["integrity_valid"]:
        raise RuntimeModelError("bundle integrity validation failed")
    if not validation["promotion_eligible"] and not allow_unpromoted:
        reasons = "; ".join(validation["promotion_reasons"])
        raise RuntimeModelError(f"bundle is not promotion eligible: {reasons}")
    manifest = _manifest(root)
    framework = manifest.get("framework") or {}
    if "torch" not in framework or "torchvision" not in framework:
        raise RuntimeModelError("live replacement currently requires a PyTorch bundle")
    scene_id, detector_id = _run_ids(manifest)
    scene_weight = root / scene_id / "best.pt"
    detector_weight = root / detector_id / "last.pt"
    for path in (scene_weight, detector_weight):
        if not path.is_file():
            raise RuntimeModelError(f"required runtime weight not found: {path}")

    record = {
        "schema": "mini-metro-active-model-v1",
        "backend": "pytorch",
        "activated_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "bundle": str(root),
        "manifest_sha256": _sha256(root / "release-manifest.json"),
        "scene_id": scene_id,
        "detector_id": detector_id,
        "detector_score_threshold": 0.5,
        "promotion_eligible": bool(validation["promotion_eligible"]),
        "approved_unpromoted": bool(allow_unpromoted and not validation["promotion_eligible"]),
        "promotion_reasons": validation["promotion_reasons"],
    }
    _atomic_json(Path(activation_path), record)
    return record


def read_activation(path: str | Path = DEFAULT_ACTIVATION) -> dict[str, Any] | None:
    source = Path(path)
    if not source.is_file():
        return None
    value = json.loads(source.read_text(encoding="utf-8"))
    if value.get("schema") != "mini-metro-active-model-v1":
        raise RuntimeModelError(f"unsupported activation schema: {value.get('schema')}")
    return value


class TorchContextClassifier:
    def __init__(self, run: Path, device: str = "auto"):
        try:
            import torch
            import torchvision
            from torch import nn
        except ImportError as exc:
            raise RuntimeModelError(
                "PyTorch runtime is missing; run setup.ps1 -TrainedModels"
            ) from exc

        config = json.loads((run / "config.json").read_text(encoding="utf-8"))
        metrics = json.loads((run / "metrics.json").read_text(encoding="utf-8"))
        if config["backbone"] == "mobilenet_v3_small":
            model = torchvision.models.mobilenet_v3_small(weights=None)
        elif config["backbone"] == "efficientnet_b0":
            model = torchvision.models.efficientnet_b0(weights=None)
        else:
            raise RuntimeModelError(f"unsupported scene backbone: {config['backbone']}")
        self.labels = [str(item) for item in metrics["classes"]]
        model.classifier[-1] = nn.Linear(model.classifier[-1].in_features, len(self.labels))
        selected_device = "cuda" if device == "auto" and torch.cuda.is_available() else device
        if selected_device == "auto":
            selected_device = "cpu"
        self.device = torch.device(selected_device)
        state = torch.load(run / "best.pt", map_location=self.device, weights_only=True)
        model.load_state_dict(state)
        self.model = model.to(self.device).eval()
        self.torch = torch
        self.image_size = int(config["image_size"])
        self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
        self.std = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)

    def predict(self, rgb: np.ndarray) -> Prediction:
        if rgb.ndim != 3 or rgb.shape[2] != 3:
            raise ValueError("scene classifier expects an RGB image")
        torch = self.torch
        resized = cv2.resize(rgb, (self.image_size, self.image_size), interpolation=cv2.INTER_AREA)
        tensor = torch.from_numpy(np.array(resized, dtype=np.uint8, copy=True, order="C")).to(
            self.device
        )
        tensor = tensor.permute(2, 0, 1).unsqueeze(0).float().div_(255.0)
        tensor = (tensor - self.mean) / self.std
        with torch.inference_mode():
            probabilities = self.model(tensor).softmax(1)[0].cpu().numpy()
        index = int(np.argmax(probabilities))
        return Prediction(
            self.labels[index],
            float(probabilities[index]),
            {label: float(probabilities[i]) for i, label in enumerate(self.labels)},
        )


class TorchStationDetector:
    def __init__(
        self,
        run: Path,
        minimum_confidence: float = 0.5,
        device: str = "auto",
    ):
        try:
            import torch
            import torchvision
            from torch import nn
            from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
        except ImportError as exc:
            raise RuntimeModelError(
                "PyTorch runtime is missing; run setup.ps1 -TrainedModels"
            ) from exc

        config = json.loads((run / "config.json").read_text(encoding="utf-8"))
        if config["model"] == "fasterrcnn_mobilenet_v3_large_320_fpn":
            model = torchvision.models.detection.fasterrcnn_mobilenet_v3_large_320_fpn(
                weights=None, weights_backbone=None
            )
        elif config["model"] == "fasterrcnn_mobilenet_v3_large_fpn":
            model = torchvision.models.detection.fasterrcnn_mobilenet_v3_large_fpn(
                weights=None, weights_backbone=None
            )
        else:
            raise RuntimeModelError(f"unsupported detector: {config['model']}")
        features = model.roi_heads.box_predictor.cls_score.in_features
        model.roi_heads.box_predictor = FastRCNNPredictor(features, 2)
        selected_device = "cuda" if device == "auto" and torch.cuda.is_available() else device
        if selected_device == "auto":
            selected_device = "cpu"
        self.device = torch.device(selected_device)
        state = torch.load(run / "last.pt", map_location=self.device, weights_only=True)
        model.load_state_dict(state)
        self.model = model.to(self.device).eval()
        self.torch = torch
        self.nn = nn
        self.maximum_size = int(config["max_image_size"])
        self.minimum_confidence = float(minimum_confidence)

    def detect(self, rgb: np.ndarray) -> list[StationDetection]:
        if rgb.ndim != 3 or rgb.shape[2] != 3:
            raise ValueError("station detector expects an RGB image")
        torch = self.torch
        height, width = rgb.shape[:2]
        scale = min(1.0, self.maximum_size / max(height, width))
        tensor = torch.from_numpy(np.array(rgb, dtype=np.uint8, copy=True, order="C")).to(
            self.device
        )
        tensor = tensor.permute(2, 0, 1).float().div_(255.0)
        if scale < 1.0:
            resized_height = max(1, round(height * scale))
            resized_width = max(1, round(width * scale))
            tensor = self.nn.functional.interpolate(
                tensor.unsqueeze(0),
                size=(resized_height, resized_width),
                mode="bilinear",
                align_corners=False,
            )[0]
        with torch.inference_mode():
            output = self.model([tensor])[0]
        boxes = output["boxes"].detach().cpu().numpy()
        scores = output["scores"].detach().cpu().numpy()
        detections: list[StationDetection] = []
        for box, score in zip(boxes, scores):
            confidence = float(score)
            if confidence < self.minimum_confidence:
                continue
            x1, y1, x2, y2 = (float(value) / scale for value in box)
            detections.append(
                StationDetection(
                    max(0, min(width, int(round(x1)))),
                    max(0, min(height, int(round(y1)))),
                    max(0, min(width, int(round(x2)))),
                    max(0, min(height, int(round(y2)))),
                    "station",
                    confidence,
                )
            )
        return detections

    def annotate(self, rgb: np.ndarray, detections: list[StationDetection]) -> np.ndarray:
        return StationDetector(self.minimum_confidence).annotate(rgb, detections)


@dataclass(slots=True)
class RuntimeVision:
    classifier: Any
    detector: Any
    backend: str
    detail: str


def load_runtime_vision(
    archive: LegacyArchive,
    prototype_path: Path,
    minimum_confidence: float,
    *,
    activation_path: str | Path = DEFAULT_ACTIVATION,
    requested_backend: str = "auto",
    strict_trained: bool = False,
) -> RuntimeVision:
    """Resolve the active backend, with a safe classical fallback on load failure."""

    if requested_backend not in {"auto", "classical", "trained"}:
        raise ValueError(f"unsupported vision backend: {requested_backend}")
    activation = read_activation(activation_path) if requested_backend != "classical" else None
    use_trained = requested_backend == "trained" or (
        requested_backend == "auto" and activation is not None
    )
    if use_trained:
        try:
            if activation is None:
                raise RuntimeModelError("no active trained-model configuration")
            bundle = Path(str(activation["bundle"])).resolve()
            manifest_path = bundle / "release-manifest.json"
            if _sha256(manifest_path) != activation.get("manifest_sha256"):
                raise RuntimeModelError("active manifest hash differs from the approved activation")
            validation = validate_model_bundle(bundle)
            if not validation["integrity_valid"]:
                raise RuntimeModelError("active model bundle failed integrity validation")
            if not validation["promotion_eligible"] and not activation.get("approved_unpromoted"):
                raise RuntimeModelError("active model bundle is not promotion eligible")
            scene = TorchContextClassifier(bundle / str(activation["scene_id"]))
            threshold = max(
                float(minimum_confidence),
                float(activation.get("detector_score_threshold", 0.5)),
            )
            detector = TorchStationDetector(
                bundle / str(activation["detector_id"]),
                minimum_confidence=threshold,
            )
            return RuntimeVision(scene, detector, "pytorch", str(bundle))
        except (ImportError, OSError, RuntimeError, ValueError, KeyError, json.JSONDecodeError) as exc:
            if strict_trained or requested_backend == "trained":
                raise RuntimeModelError(f"trained runtime could not start: {exc}") from exc
            print(f"trained vision unavailable; using classical fallback: {exc}", file=sys.stderr)

    if prototype_path.is_file():
        classifier = PrototypeContextClassifier.load(prototype_path)
    else:
        classifier = PrototypeContextClassifier.train(archive)
        classifier.save(prototype_path)
    return RuntimeVision(
        classifier,
        StationDetector(minimum_confidence),
        "classical",
        str(prototype_path),
    )
