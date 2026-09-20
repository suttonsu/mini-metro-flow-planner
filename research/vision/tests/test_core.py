from __future__ import annotations

import os
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from mini_metro_vision.dataset import Box, LegacyArchive
from mini_metro_vision.control import build_control_values, publish_control_state
from mini_metro_vision.frames import FramePipeline, GameFrame, GameFrameBuffer
from mini_metro_vision.native_log import latest_snapshot
from mini_metro_vision.model_bundle import validate_model_bundle
from mini_metro_vision.runtime_models import (
    RuntimeModelError,
    activate_model_bundle,
    read_activation,
)


class FrameTests(unittest.TestCase):
    def test_buffer_keeps_newest_frames(self):
        buffer = GameFrameBuffer(2)
        for sequence in range(3):
            buffer.append(GameFrame.from_pixels(np.zeros((4, 5, 3), np.uint8), sequence))
        self.assertEqual([1, 2], [frame.sequence for frame in buffer])
        self.assertEqual(2, buffer.latest.sequence)

    def test_pipeline_preserves_frame_contract(self):
        frame = GameFrame.from_pixels(np.zeros((4, 5, 3), np.uint8), 7)
        pipeline = FramePipeline([lambda pixels: np.full_like(pixels, 12)])
        transformed = pipeline.apply(frame)
        self.assertEqual(7, transformed.sequence)
        self.assertEqual(12, int(transformed.pixels[0, 0, 0]))

    def test_box_center_uses_absolute_coordinates(self):
        self.assertEqual((20.0, 35.0), Box(10, 20, 30, 50).center)


class LegacyArchiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        archive_path = os.environ.get("MINI_METRO_LEGACY_ARCHIVE")
        if not archive_path or not Path(archive_path).is_file():
            raise unittest.SkipTest("MINI_METRO_LEGACY_ARCHIVE is not available")
        cls.archive = LegacyArchive(archive_path)

    def test_dataset_has_both_tasks_and_splits(self):
        audit = self.archive.audit()
        self.assertGreaterEqual(audit["context_images"], 90)
        self.assertIn("train", audit["context_by_split"])
        self.assertIn("test", audit["context_by_split"])
        self.assertGreaterEqual(audit["station_images"], 100)
        self.assertGreaterEqual(audit["station_boxes"], 500)

    def test_station_images_match_annotation_dimensions(self):
        sample = self.archive.station_samples("test")[0]
        image = self.archive.read_image(sample.image_member)
        self.assertEqual((sample.height, sample.width), image.shape[:2])


class NativeLogTests(unittest.TestCase):
    def test_latest_snapshot_uses_latest_planner_line_and_types_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Player.log"
            path.write_text(
                "[Auto Planner] city=london stations=3 crossingWait=True\n"
                "noise city=ignored stations=99\n"
                "[Auto Planner] city=paris stations=5 crossingWait=False score=-2\n",
                encoding="utf-8",
            )
            snapshot = latest_snapshot(path)

        self.assertIsNotNone(snapshot)
        self.assertEqual("paris", snapshot["city"])
        self.assertEqual(5, snapshot["stations"])
        self.assertFalse(snapshot["crossingWait"])
        self.assertEqual(-2, snapshot["score"])


class VisionControlContractTests(unittest.TestCase):
    def test_control_contract_contains_gating_signals(self):
        record = {
            "sequence": 8,
            "timestamp": 12.5,
            "width": 800,
            "height": 600,
            "context": {"label": "game_play", "confidence": 0.8},
            "stations": [
                {"label": "circle", "confidence": 0.9},
                {"label": "triangle", "confidence": 0.7},
            ],
            "native": {"stations": 2},
            "station_count_delta": 0,
        }
        values = build_control_values(record, enforce=True)
        self.assertEqual("1", values["schema"])
        self.assertEqual("true", values["control_enabled"])
        self.assertEqual("game_play", values["context_label"])
        self.assertEqual("2", values["station_count"])
        self.assertEqual("0.800000", values["mean_station_confidence"])
        self.assertEqual("circle:1,triangle:1", values["shape_counts"])

    def test_control_contract_is_atomically_published(self):
        record = {
            "sequence": 1,
            "timestamp": 1.0,
            "context": {"label": "pause_menu", "confidence": 0.9},
            "stations": [],
            "native": {"stations": 3},
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vision-control-state.txt"
            publish_control_state(path, record, enforce=True)
            text = path.read_text(encoding="utf-8")
            leftovers = list(path.parent.glob("*.tmp"))
        self.assertIn("context_label=pause_menu\n", text)
        self.assertIn("control_enabled=true\n", text)
        self.assertEqual([], leftovers)


class ModelBundleTests(unittest.TestCase):
    @staticmethod
    def write_bundle(root: Path) -> Path:
        run = root / "scene-light"
        run.mkdir(parents=True)
        weight = run / "best.pt"
        weight.write_bytes(b"weight-bytes")
        detector_run = root / "detector-light"
        detector_run.mkdir(parents=True)
        detector_weight = detector_run / "last.pt"
        detector_weight.write_bytes(b"detector-weight-bytes")
        manifest = {
            "schema": "mini-metro-model-bundle-v1",
            "framework": {"torch": "test", "torchvision": "test"},
            "scene_results": [{"id": "scene-light", "macro_f1": 0.97}],
            "detection_results": [
                {"id": "detector-light", "f1_iou50": 0.96, "count_mae": 0.4}
            ],
            "promotion_evidence": {
                "current_session_evaluations": [
                    {"session_id": "a"}, {"session_id": "b"}, {"session_id": "c"}
                ]
            },
            "files": {
                "scene-light/best.pt": hashlib.sha256(weight.read_bytes()).hexdigest(),
                "detector-light/last.pt": hashlib.sha256(
                    detector_weight.read_bytes()
                ).hexdigest(),
            },
        }
        (root / "release-manifest.json").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        return weight

    def test_valid_bundle_is_promotion_eligible_with_all_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_bundle(root)
            result = validate_model_bundle(root)
        self.assertTrue(result["integrity_valid"])
        self.assertTrue(result["promotion_eligible"])
        self.assertEqual(["scene-light"], result["passing_scene_candidates"])

    def test_hash_tampering_blocks_integrity_and_promotion(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            weight = self.write_bundle(root)
            weight.write_bytes(b"tampered")
            result = validate_model_bundle(root)
        self.assertFalse(result["integrity_valid"])
        self.assertFalse(result["promotion_eligible"])
        self.assertTrue(any("hash mismatch" in item for item in result["errors"]))

    def test_unpromoted_runtime_activation_requires_explicit_override(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "bundle"
            root.mkdir()
            self.write_bundle(root)
            manifest_path = root / "release-manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["scene_results"][0]["macro_f1"] = 0.8
            manifest["detection_results"][0]["f1_iou50"] = 0.8
            manifest["promotion_evidence"]["current_session_evaluations"] = []
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            activation_path = Path(directory) / "active-model.json"
            with self.assertRaises(RuntimeModelError):
                activate_model_bundle(root, activation_path)
            activated = activate_model_bundle(
                root, activation_path, allow_unpromoted=True
            )
            loaded = read_activation(activation_path)
        self.assertTrue(activated["approved_unpromoted"])
        self.assertEqual("pytorch", loaded["backend"])
        self.assertEqual("scene-light", loaded["scene_id"])


if __name__ == "__main__":
    unittest.main()
