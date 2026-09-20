"""Read-only Windows client-area capture for a named Mini Metro window."""

from __future__ import annotations

import ctypes
from ctypes import wintypes

import mss
import numpy as np

from .frames import GameFrame


class WindowNotFoundError(RuntimeError):
    pass


def client_area(title: str) -> dict[str, int]:
    user32 = ctypes.windll.user32
    try:
        user32.SetProcessDPIAware()
    except AttributeError:
        pass
    handle = user32.FindWindowW(None, title)
    if not handle:
        raise WindowNotFoundError(f"window not found: {title!r}")
    rect = wintypes.RECT()
    if not user32.GetClientRect(handle, ctypes.byref(rect)):
        raise ctypes.WinError()
    point = wintypes.POINT(0, 0)
    if not user32.ClientToScreen(handle, ctypes.byref(point)):
        raise ctypes.WinError()
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if width <= 0 or height <= 0:
        raise RuntimeError(f"window client area is empty: {title!r}")
    return {"left": point.x, "top": point.y, "width": width, "height": height}


class WindowFrameSource:
    def __init__(self, title: str = "Mini Metro"):
        self.title = title
        self._sequence = 0

    def capture(self) -> GameFrame:
        region = client_area(self.title)
        with mss.mss() as grabber:
            bgra = np.asarray(grabber.grab(region), dtype=np.uint8)
        rgb = bgra[:, :, :3][:, :, ::-1]
        self._sequence += 1
        return GameFrame.from_pixels(rgb, self._sequence)
