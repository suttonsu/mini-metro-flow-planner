"""Safe NumPy context classifier trained from the legacy labelled frames."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from .dataset import LegacyArchive


def extract_features(rgb: np.ndarray) -> np.ndarray:
    resized = cv2.resize(rgb, (32, 24), interpolation=cv2.INTER_AREA)
    lab = cv2.cvtColor(resized, cv2.COLOR_RGB2LAB).astype(np.float32) / 255.0
    gray = cv2.cvtColor(resized, cv2.COLOR_RGB2GRAY).astype(np.float32) / 255.0
    edges = cv2.Canny(resized, 60, 150).astype(np.float32) / 255.0
    color_grid = cv2.resize(lab, (16, 12), interpolation=cv2.INTER_AREA).reshape(-1)
    gray_grid = gray.reshape(-1)
    edge_grid = edges.reshape(-1)
    hist = np.concatenate(
        [np.histogram(lab[:, :, channel], bins=12, range=(0, 1), density=True)[0]
         for channel in range(3)]
    ).astype(np.float32)
    return np.concatenate((color_grid, gray_grid, edge_grid, hist)).astype(np.float32)


@dataclass(slots=True)
class Prediction:
    label: str
    confidence: float
    scores: dict[str, float]


class PrototypeContextClassifier:
    def __init__(
        self,
        labels: list[str],
        mean: np.ndarray,
        scale: np.ndarray,
        centroids: np.ndarray,
    ):
        self.labels = labels
        self.mean = mean.astype(np.float32)
        self.scale = scale.astype(np.float32)
        self.centroids = centroids.astype(np.float32)

    @classmethod
    def train(cls, archive: LegacyArchive) -> "PrototypeContextClassifier":
        rows: list[np.ndarray] = []
        labels: list[str] = []
        for sample, image in archive.iter_context_images("train"):
            rows.append(extract_features(image))
            labels.append(sample.label)
        if not rows:
            raise ValueError("legacy archive contains no context training images")
        matrix = np.stack(rows)
        mean = matrix.mean(axis=0)
        scale = matrix.std(axis=0)
        scale[scale < 0.025] = 0.025
        normalized = (matrix - mean) / scale
        classes = sorted(set(labels))
        centroids = np.stack(
            [normalized[np.asarray(labels) == label].mean(axis=0) for label in classes]
        )
        return cls(classes, mean, scale, centroids)

    def predict(self, rgb: np.ndarray) -> Prediction:
        vector = (extract_features(rgb) - self.mean) / self.scale
        distances = np.mean((self.centroids - vector) ** 2, axis=1)
        logits = -distances / max(0.05, float(np.std(distances)))
        logits -= logits.max()
        probabilities = np.exp(logits)
        probabilities /= probabilities.sum()
        index = int(np.argmax(probabilities))
        return Prediction(
            self.labels[index],
            float(probabilities[index]),
            {label: float(probabilities[i]) for i, label in enumerate(self.labels)},
        )

    def save(self, path: str | Path) -> None:
        target = Path(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(
            target,
            labels=np.asarray(self.labels),
            mean=self.mean,
            scale=self.scale,
            centroids=self.centroids,
        )

    @classmethod
    def load(cls, path: str | Path) -> "PrototypeContextClassifier":
        with np.load(path, allow_pickle=False) as data:
            return cls(
                [str(item) for item in data["labels"].tolist()],
                data["mean"],
                data["scale"],
                data["centroids"],
            )

    def evaluate(self, archive: LegacyArchive) -> dict[str, object]:
        total = 0
        correct = 0
        confusion: dict[str, dict[str, int]] = {}
        for sample, image in archive.iter_context_images("test"):
            predicted = self.predict(image).label
            total += 1
            correct += int(predicted == sample.label)
            confusion.setdefault(sample.label, {})[predicted] = (
                confusion.setdefault(sample.label, {}).get(predicted, 0) + 1
            )
        return {
            "samples": total,
            "correct": correct,
            "accuracy": correct / total if total else 0.0,
            "confusion": confusion,
        }
