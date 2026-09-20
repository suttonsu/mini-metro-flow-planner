"""Serpent-inspired, read-only computer-vision research for Mini Metro."""

from .frames import FramePipeline, GameFrame, GameFrameBuffer
from .station_detector import StationDetection, StationDetector

__all__ = [
    "FramePipeline",
    "GameFrame",
    "GameFrameBuffer",
    "StationDetection",
    "StationDetector",
]
