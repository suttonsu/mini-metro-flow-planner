"""Small maintained subset of SerpentAI's frame-processing concepts.

This intentionally keeps the useful GameFrame / frame buffer / transformation
pipeline separation without Redis, Crossbar, plugins, or input injection.
"""

from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from time import time
from typing import Callable, Iterable, Iterator, Sequence

import numpy as np


@dataclass(frozen=True, slots=True)
class GameFrame:
    pixels: np.ndarray
    timestamp: float
    sequence: int = 0

    @classmethod
    def from_pixels(cls, pixels: np.ndarray, sequence: int = 0) -> "GameFrame":
        array = np.asarray(pixels)
        if array.ndim != 3 or array.shape[2] not in (3, 4):
            raise ValueError("GameFrame pixels must have shape (height, width, 3|4)")
        if array.dtype != np.uint8:
            raise ValueError("GameFrame pixels must use uint8 channels")
        if array.shape[2] == 4:
            array = array[:, :, :3]
        return cls(np.ascontiguousarray(array), time(), sequence)

    @property
    def width(self) -> int:
        return int(self.pixels.shape[1])

    @property
    def height(self) -> int:
        return int(self.pixels.shape[0])


class GameFrameBuffer(Sequence[GameFrame]):
    def __init__(self, capacity: int = 8):
        if capacity < 1:
            raise ValueError("capacity must be positive")
        self._frames: deque[GameFrame] = deque(maxlen=capacity)

    @property
    def capacity(self) -> int:
        return int(self._frames.maxlen or 0)

    def append(self, frame: GameFrame) -> None:
        self._frames.append(frame)

    def clear(self) -> None:
        self._frames.clear()

    def __len__(self) -> int:
        return len(self._frames)

    def __getitem__(self, index: int) -> GameFrame:
        return tuple(self._frames)[index]

    def __iter__(self) -> Iterator[GameFrame]:
        return iter(tuple(self._frames))

    @property
    def latest(self) -> GameFrame | None:
        return self._frames[-1] if self._frames else None


Transform = Callable[[np.ndarray], np.ndarray]


class FramePipeline:
    def __init__(self, transforms: Iterable[Transform] = ()):
        self.transforms = tuple(transforms)

    def apply(self, frame: GameFrame) -> GameFrame:
        pixels = frame.pixels
        for transform in self.transforms:
            pixels = np.asarray(transform(pixels))
            if pixels.ndim != 3 or pixels.shape[2] != 3:
                raise ValueError("frame transform must return an RGB image")
        return GameFrame.from_pixels(pixels, sequence=frame.sequence)
