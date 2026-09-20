"""Classical station detector for research and current-version calibration."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from math import exp, hypot

import cv2
import numpy as np

from .dataset import Box, LegacyArchive


@dataclass(frozen=True, slots=True)
class StationDetection:
    xmin: int
    ymin: int
    xmax: int
    ymax: int
    label: str
    confidence: float

    @property
    def center(self) -> tuple[float, float]:
        return ((self.xmin + self.xmax) / 2.0, (self.ymin + self.ymax) / 2.0)

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def _shape_label(contour: np.ndarray) -> tuple[str, float]:
    perimeter = cv2.arcLength(contour, True)
    if perimeter <= 0:
        return "station", 0.0
    approximation = cv2.approxPolyDP(contour, 0.045 * perimeter, True)
    vertices = len(approximation)
    area = max(1.0, cv2.contourArea(contour))
    circularity = min(1.0, 4.0 * np.pi * area / (perimeter * perimeter))
    if vertices == 3:
        return "triangle", 0.92
    if vertices == 4:
        return "square", 0.88
    if vertices >= 6 or circularity >= 0.72:
        return "circle", max(0.72, float(circularity))
    return "station", 0.55


class StationDetector:
    def __init__(self, minimum_confidence: float = 0.48):
        self.minimum_confidence = minimum_confidence

    def detect(self, rgb: np.ndarray) -> list[StationDetection]:
        if rgb.ndim != 3 or rgb.shape[2] != 3:
            raise ValueError("station detector expects an RGB image")
        height, width = rgb.shape[:2]
        scale = max(0.45, min(width / 800.0, height / 600.0))
        channel_spread = rgb.max(axis=2).astype(np.int16) - rgb.min(axis=2).astype(np.int16)
        gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)

        # Mini Metro station borders are nearly neutral dark brown. Restricting
        # channel spread suppresses coloured rail lines before contour analysis.
        dark_neutral = ((gray < 145) & (channel_spread < 72)).astype(np.uint8) * 255
        kernel_size = max(1, int(round(2 * scale)))
        kernel = np.ones((kernel_size, kernel_size), dtype=np.uint8)
        mask = cv2.morphologyEx(dark_neutral, cv2.MORPH_CLOSE, kernel)
        contours, _ = cv2.findContours(mask, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)

        candidates: list[StationDetection] = []
        minimum = 16.0 * scale
        maximum = 62.0 * scale
        expected = 38.0 * scale
        for contour in contours:
            x, y, box_width, box_height = cv2.boundingRect(contour)
            if not (minimum <= box_width <= maximum and minimum <= box_height <= maximum):
                continue
            aspect = box_width / max(1.0, float(box_height))
            if not 0.62 <= aspect <= 1.45:
                continue
            area = cv2.contourArea(contour)
            if area < 75.0 * scale * scale:
                continue
            perimeter = cv2.arcLength(contour, True)
            if perimeter <= 0:
                continue

            center_x = x + box_width / 2.0
            center_y = y + box_height / 2.0
            # Remove stable HUD icons while retaining the full playable map.
            if center_y < 58 * scale and (center_x < 75 * scale or center_x > width - 180 * scale):
                continue
            if center_y > height - 57 * scale:
                continue

            radius = max(2, int(round(5 * scale)))
            cy, cx = int(round(center_y)), int(round(center_x))
            center_patch = gray[
                max(0, cy - radius):min(height, cy + radius + 1),
                max(0, cx - radius):min(width, cx + radius + 1),
            ]
            if center_patch.size == 0 or float(np.percentile(center_patch, 60)) < 105:
                continue

            label, shape_score = _shape_label(contour)
            dimension = (box_width + box_height) / 2.0
            size_score = exp(-abs(dimension - expected) / max(8.0, expected * 0.55))
            extent = min(1.0, area / max(1.0, box_width * box_height))
            confidence = 0.42 * size_score + 0.33 * shape_score + 0.25 * min(1.0, extent * 2.0)
            if confidence < self.minimum_confidence:
                continue

            half = max(13, int(round(20 * scale)))
            candidates.append(
                StationDetection(
                    max(0, cx - half),
                    max(0, cy - half),
                    min(width, cx + half),
                    min(height, cy + half),
                    label,
                    float(min(0.99, confidence)),
                )
            )

        candidates.sort(key=lambda item: item.confidence, reverse=True)
        selected: list[StationDetection] = []
        for candidate in candidates:
            if any(
                hypot(
                    candidate.center[0] - existing.center[0],
                    candidate.center[1] - existing.center[1],
                ) < 18 * scale
                for existing in selected
            ):
                continue
            selected.append(candidate)
        return sorted(selected, key=lambda item: (item.center[1], item.center[0]))

    def annotate(self, rgb: np.ndarray, detections: list[StationDetection]) -> np.ndarray:
        bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
        for item in detections:
            cv2.rectangle(
                bgr,
                (item.xmin, item.ymin),
                (item.xmax, item.ymax),
                (35, 210, 35),
                2,
            )
            cv2.putText(
                bgr,
                f"{item.label} {item.confidence:.2f}",
                (item.xmin, max(14, item.ymin - 4)),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.42,
                (20, 100, 20),
                1,
                cv2.LINE_AA,
            )
        return cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)

    def evaluate(
        self,
        archive: LegacyArchive,
        split: str = "test",
        center_tolerance: float = 25.0,
    ) -> dict[str, object]:
        true_positive = false_positive = false_negative = 0
        shape_correct = shape_total = 0
        per_image: list[dict[str, object]] = []
        for sample in archive.station_samples(split):
            image = archive.read_image(sample.image_member)
            predictions = self.detect(image)
            available = set(range(len(sample.boxes)))
            matches = 0
            for prediction in predictions:
                px, py = prediction.center
                nearest = None
                nearest_distance = float("inf")
                for index in available:
                    gx, gy = sample.boxes[index].center
                    distance = hypot(px - gx, py - gy)
                    if distance < nearest_distance:
                        nearest, nearest_distance = index, distance
                if nearest is not None and nearest_distance <= center_tolerance:
                    available.remove(nearest)
                    matches += 1
                    shape_total += 1
                    shape_correct += int(
                        prediction.label == sample.boxes[nearest].label
                        or prediction.label == "station"
                    )
            true_positive += matches
            false_positive += len(predictions) - matches
            false_negative += len(available)
            per_image.append(
                {
                    "image": sample.image_member,
                    "truth": len(sample.boxes),
                    "predicted": len(predictions),
                    "matched": matches,
                }
            )
        precision = true_positive / max(1, true_positive + false_positive)
        recall = true_positive / max(1, true_positive + false_negative)
        return {
            "split": split,
            "images": len(per_image),
            "true_positive": true_positive,
            "false_positive": false_positive,
            "false_negative": false_negative,
            "precision": precision,
            "recall": recall,
            "f1": 2 * precision * recall / max(1e-9, precision + recall),
            "shape_accuracy": shape_correct / max(1, shape_total),
            "per_image": per_image,
        }
