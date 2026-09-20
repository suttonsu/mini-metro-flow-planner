from __future__ import annotations

import argparse
import json
import os
import threading
import time
from pathlib import Path

import numpy as np
from PIL import Image

from .capture import WindowFrameSource
from .control import publish_control_state
from .context_classifier import PrototypeContextClassifier
from .dataset import LegacyArchive
from .dataset_v2 import build_dataset, package_dataset, validate_dataset
from .native_log import latest_snapshot
from .model_bundle import validate_model_bundle
from .runtime_models import (
    DEFAULT_ACTIVATION,
    RuntimeModelError,
    activate_model_bundle,
    load_runtime_vision,
    read_activation,
)
from .station_detector import StationDetector


PROJECT_DIR = Path(__file__).resolve().parents[1]
DEFAULT_ARCHIVE = Path(os.environ.get("MINI_METRO_VISION_ARCHIVE", "mini-metro-master.zip"))
DEFAULT_MODEL = PROJECT_DIR / "artifacts" / "context_prototypes.npz"


def print_json(value: object) -> None:
    print(json.dumps(value, indent=2, ensure_ascii=False))


def default_player_log() -> Path:
    local = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    return local.parent / "LocalLow" / "Dinosaur Polo Club" / "Mini Metro" / "Player.log"


def default_control_state() -> Path:
    return default_player_log().parent / "vision-control-state.txt"


def require_model(path: Path, archive: LegacyArchive) -> PrototypeContextClassifier:
    if path.is_file():
        return PrototypeContextClassifier.load(path)
    model = PrototypeContextClassifier.train(archive)
    model.save(path)
    return model


def command_audit(args: argparse.Namespace) -> int:
    print_json(LegacyArchive(args.archive).audit())
    return 0


def command_train_context(args: argparse.Namespace) -> int:
    archive = LegacyArchive(args.archive)
    model = PrototypeContextClassifier.train(archive)
    model.save(args.model)
    result = model.evaluate(archive)
    result["model"] = str(args.model)
    print_json(result)
    return 0


def command_evaluate(args: argparse.Namespace) -> int:
    archive = LegacyArchive(args.archive)
    classifier = require_model(args.model, archive)
    context = classifier.evaluate(archive)
    station = StationDetector(args.minimum_confidence).evaluate(archive)
    result = {"context": context, "stations": station}
    print_json(result)
    if context["accuracy"] < args.minimum_context_accuracy:
        return 2
    if station["f1"] < args.minimum_station_f1:
        return 3
    return 0


def command_detect(args: argparse.Namespace) -> int:
    rgb = np.asarray(Image.open(args.image).convert("RGB"), dtype=np.uint8)
    runtime = load_runtime_vision(
        LegacyArchive(args.archive),
        args.model,
        args.minimum_confidence,
        activation_path=args.activation,
        requested_backend=args.vision_backend,
        strict_trained=args.strict_trained,
    )
    context = runtime.classifier.predict(rgb)
    detections = runtime.detector.detect(rgb)
    output = {
        "image": str(args.image),
        "vision_backend": runtime.backend,
        "vision_backend_detail": runtime.detail,
        "context": {
            "label": context.label,
            "confidence": context.confidence,
            "scores": context.scores,
        },
        "station_count": len(detections),
        "stations": [item.to_dict() for item in detections],
    }
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(runtime.detector.annotate(rgb, detections)).save(args.output)
        output["annotated"] = str(args.output)
    print_json(output)
    return 0


def command_build_dataset(args: argparse.Namespace) -> int:
    manifest = build_dataset(
        args.archive,
        args.output,
        current_observations=args.current_observations,
        current_frames=args.current_frames,
    )
    validation = validate_dataset(args.output)
    package = package_dataset(args.output, args.package) if args.package else None
    print_json(
        {
            "manifest": manifest,
            "validation": validation,
            "package": str(package) if package else None,
        }
    )
    return 0


def command_validate_dataset(args: argparse.Namespace) -> int:
    print_json(validate_dataset(args.dataset))
    return 0


def command_validate_model_bundle(args: argparse.Namespace) -> int:
    try:
        result = validate_model_bundle(
            args.bundle,
            minimum_scene_macro_f1=args.minimum_scene_macro_f1,
            minimum_detection_f1=args.minimum_detection_f1,
            maximum_count_mae=args.maximum_count_mae,
            minimum_current_sessions=args.minimum_current_sessions,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        result = {
            "source": str(args.bundle),
            "integrity_valid": False,
            "promotion_eligible": False,
            "errors": [str(exc)],
        }
    print_json(result)
    if not result["integrity_valid"]:
        return 4
    if args.require_promotable and not result["promotion_eligible"]:
        return 5
    return 0


def command_activate_model_bundle(args: argparse.Namespace) -> int:
    try:
        result = activate_model_bundle(
            args.bundle,
            args.activation,
            allow_unpromoted=args.allow_unpromoted,
        )
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print_json({"activated": False, "error": str(exc)})
        return 6
    print_json({"activated": True, "activation": str(args.activation), **result})
    return 0


def command_model_status(args: argparse.Namespace) -> int:
    try:
        activation = read_activation(args.activation)
        if activation is None:
            print_json({"active": False, "activation": str(args.activation)})
            return 0
        validation = validate_model_bundle(Path(str(activation["bundle"])))
        print_json(
            {
                "active": True,
                "activation": str(args.activation),
                "configuration": activation,
                "validation": validation,
            }
        )
        return 0 if validation["integrity_valid"] else 4
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print_json({"active": False, "activation": str(args.activation), "error": str(exc)})
        return 4


def command_publish_probe(args: argparse.Namespace) -> int:
    stations = [
        {"label": "circle", "confidence": args.station_confidence}
        for _ in range(args.station_count)
    ]
    record: dict[str, object] = {
        "sequence": args.sequence,
        "timestamp": time.monotonic(),
        "width": 800,
        "height": 600,
        "context": {"label": args.context, "confidence": args.context_confidence},
        "stations": stations,
        "native": {"stations": args.native_station_count},
        "station_count_delta": args.station_count - args.native_station_count,
    }
    publish_control_state(args.control_state, record, enforce=True)
    print_json(
        {
            "published": str(args.control_state),
            "context": args.context,
            "station_count": args.station_count,
            "native_station_count": args.native_station_count,
            "sequence": args.sequence,
        }
    )
    return 0


def _publish_loading_heartbeat(args: argparse.Namespace, stop: threading.Event) -> None:
    sequence = -1
    while not stop.is_set():
        native = latest_snapshot(args.player_log)
        publish_control_state(
            args.control_state,
            {
                "sequence": sequence,
                "timestamp": time.monotonic(),
                "width": 0,
                "height": 0,
                "vision_backend": "loading",
                "context": {"label": "model_loading", "confidence": 0.0},
                "vision_station_count": 0,
                "stations": [],
                "native": native,
            },
            enforce=True,
        )
        sequence -= 1
        stop.wait(0.75)


def command_watch(args: argparse.Namespace) -> int:
    archive = LegacyArchive(args.archive)
    loading_stop = None
    loading_thread = None
    if args.control_state and args.enforce_control:
        loading_stop = threading.Event()
        loading_thread = threading.Thread(
            target=_publish_loading_heartbeat,
            args=(args, loading_stop),
            name="vision-model-loading-heartbeat",
            daemon=True,
        )
        loading_thread.start()
    try:
        runtime = load_runtime_vision(
            archive,
            args.model,
            args.minimum_confidence,
            activation_path=args.activation,
            requested_backend=args.vision_backend,
            strict_trained=args.strict_trained,
        )
    finally:
        if loading_stop is not None:
            loading_stop.set()
        if loading_thread is not None:
            loading_thread.join(timeout=2.0)
    classifier = runtime.classifier
    detector = runtime.detector
    source = WindowFrameSource(args.window_title)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.annotated_dir:
        args.annotated_dir.mkdir(parents=True, exist_ok=True)
    if args.raw_dir:
        args.raw_dir.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + args.seconds
    observations = 0
    with args.output.open("w", encoding="utf-8") as stream:
        while time.monotonic() < deadline:
            frame = source.capture()
            context = classifier.predict(frame.pixels)
            # Always run detection during research capture. The native telemetry
            # tells us whether a game is active and lets us measure context drift
            # without suppressing the very observations needed to diagnose it.
            stations = detector.detect(frame.pixels)
            native = latest_snapshot(args.player_log)
            record: dict[str, object] = {
                "sequence": frame.sequence,
                "timestamp": frame.timestamp,
                "width": frame.width,
                "height": frame.height,
                "vision_backend": runtime.backend,
                "vision_backend_detail": runtime.detail,
                "context": {
                    "label": context.label,
                    "confidence": context.confidence,
                    "scores": context.scores,
                },
                "vision_station_count": len(stations),
                "stations": [item.to_dict() for item in stations],
                "native": native,
            }
            if native and isinstance(native.get("stations"), int):
                record["station_count_delta"] = len(stations) - int(native["stations"])
            if args.control_state:
                publish_control_state(
                    args.control_state,
                    record,
                    enforce=args.enforce_control,
                )
                record["control_state"] = str(args.control_state)
                record["control_enforced"] = bool(args.enforce_control)
            stream.write(json.dumps(record, ensure_ascii=False) + "\n")
            stream.flush()
            if args.annotated_dir:
                annotated = detector.annotate(frame.pixels, stations)
                Image.fromarray(annotated).save(
                    args.annotated_dir / f"frame-{frame.sequence:05d}.png"
                )
            if args.raw_dir:
                Image.fromarray(frame.pixels).save(
                    args.raw_dir / f"frame-{frame.sequence:05d}.png"
                )
            observations += 1
            if args.interval > 0:
                time.sleep(args.interval)
    print_json({"observations": observations, "output": str(args.output)})
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Serpent-inspired Mini Metro visual research sidecar"
    )
    parser.add_argument("--archive", type=Path, default=DEFAULT_ARCHIVE)
    subparsers = parser.add_subparsers(dest="command", required=True)

    audit = subparsers.add_parser("audit", help="audit legacy labelled data")
    audit.set_defaults(handler=command_audit)

    train = subparsers.add_parser("train-context", help="train safe context prototypes")
    train.add_argument("--model", type=Path, default=DEFAULT_MODEL)
    train.set_defaults(handler=command_train_context)

    evaluate = subparsers.add_parser("evaluate", help="evaluate both vision tasks")
    evaluate.add_argument("--model", type=Path, default=DEFAULT_MODEL)
    evaluate.add_argument("--minimum-confidence", type=float, default=0.48)
    evaluate.add_argument("--minimum-context-accuracy", type=float, default=0.75)
    evaluate.add_argument("--minimum-station-f1", type=float, default=0.35)
    evaluate.set_defaults(handler=command_evaluate)

    detect = subparsers.add_parser("detect", help="detect stations in one image")
    detect.add_argument("image", type=Path)
    detect.add_argument("--output", type=Path)
    detect.add_argument("--model", type=Path, default=DEFAULT_MODEL)
    detect.add_argument("--activation", type=Path, default=DEFAULT_ACTIVATION)
    detect.add_argument(
        "--vision-backend", choices=("auto", "classical", "trained"), default="auto"
    )
    detect.add_argument("--strict-trained", action="store_true")
    detect.add_argument("--minimum-confidence", type=float, default=0.48)
    detect.set_defaults(handler=command_detect)

    dataset = subparsers.add_parser(
        "build-dataset", help="build the versioned Colab-ready v2 dataset"
    )
    dataset.add_argument("--output", type=Path, required=True)
    dataset.add_argument("--current-observations", type=Path)
    dataset.add_argument("--current-frames", type=Path)
    dataset.add_argument("--package", type=Path)
    dataset.set_defaults(handler=command_build_dataset)

    validate = subparsers.add_parser(
        "validate-dataset", help="validate split isolation and COCO annotations"
    )
    validate.add_argument("dataset", type=Path)
    validate.set_defaults(handler=command_validate_dataset)

    bundle = subparsers.add_parser(
        "validate-model-bundle",
        help="verify Colab output hashes, metrics, weights, and promotion evidence",
    )
    bundle.add_argument("bundle", type=Path)
    bundle.add_argument("--minimum-scene-macro-f1", type=float, default=0.95)
    bundle.add_argument("--minimum-detection-f1", type=float, default=0.95)
    bundle.add_argument("--maximum-count-mae", type=float, default=0.5)
    bundle.add_argument("--minimum-current-sessions", type=int, default=3)
    bundle.add_argument("--require-promotable", action="store_true")
    bundle.set_defaults(handler=command_validate_model_bundle)

    activate = subparsers.add_parser(
        "activate-model-bundle",
        help="select an unpacked PyTorch Colab bundle for live inference",
    )
    activate.add_argument("bundle", type=Path)
    activate.add_argument("--activation", type=Path, default=DEFAULT_ACTIVATION)
    activate.add_argument(
        "--allow-unpromoted",
        action="store_true",
        help="record an explicit experimental override for a bundle below release gates",
    )
    activate.set_defaults(handler=command_activate_model_bundle)

    status = subparsers.add_parser(
        "model-status", help="show the active live model and integrity state"
    )
    status.add_argument("--activation", type=Path, default=DEFAULT_ACTIVATION)
    status.set_defaults(handler=command_model_status)

    probe = subparsers.add_parser(
        "publish-probe", help="publish a synthetic enforced state for gate testing"
    )
    probe.add_argument("--control-state", type=Path, default=default_control_state())
    probe.add_argument("--context", default="game_play")
    probe.add_argument("--context-confidence", type=float, default=0.99)
    probe.add_argument("--station-count", type=int, default=99)
    probe.add_argument("--station-confidence", type=float, default=0.99)
    probe.add_argument("--native-station-count", type=int, default=3)
    probe.add_argument("--sequence", type=int, default=999999)
    probe.set_defaults(handler=command_publish_probe)

    def add_watch_arguments(command: argparse.ArgumentParser) -> None:
        command.add_argument("--model", type=Path, default=DEFAULT_MODEL)
        command.add_argument("--activation", type=Path, default=DEFAULT_ACTIVATION)
        command.add_argument(
            "--vision-backend",
            choices=("auto", "classical", "trained"),
            default="auto",
        )
        command.add_argument(
            "--strict-trained",
            action="store_true",
            help="fail instead of using the classical fallback if trained weights cannot load",
        )
        command.add_argument("--window-title", default="Mini Metro")
        command.add_argument("--seconds", type=float, default=30.0)
        command.add_argument("--interval", type=float, default=1.0)
        command.add_argument("--minimum-confidence", type=float, default=0.48)
        command.add_argument("--player-log", type=Path, default=default_player_log())
        command.add_argument(
            "--output", type=Path, default=PROJECT_DIR / "artifacts" / "observations.jsonl"
        )
        command.add_argument("--annotated-dir", type=Path)
        command.add_argument("--raw-dir", type=Path)
        command.add_argument(
            "--control-state",
            type=Path,
            help="atomically publish the C# vision-control contract",
        )
        command.add_argument(
            "--enforce-control",
            action="store_true",
            help="allow fresh visual disagreement to gate planner topology actions",
        )
        command.set_defaults(handler=command_watch)

    watch = subparsers.add_parser("watch", help="observe a running game without input")
    add_watch_arguments(watch)

    control = subparsers.add_parser(
        "control", help="observe and publish an enforced visual decision signal"
    )
    add_watch_arguments(control)
    control.set_defaults(
        control_state=default_control_state(),
        enforce_control=True,
        output=PROJECT_DIR / "artifacts" / "control-observations.jsonl",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args()
    return int(args.handler(args))
