"""TCP test client for the ClipTool Godot app.

Usage:
    from test_client import AppClient

    client = AppClient()
    client.ping()
    client.import_file("/path/to/video.mp4")
    print(client.get_tracks())
    client.call("OnGenerateCaptions")
    client.click_button("EXPORT")
    client.quit()

Commands: ping, import_file, get_tracks, set_selection, set_timeline_pos,
          call (Undo/Redo/SplitAtPlayhead/...), click_button, get_property,
          get_ui_state, list_buttons, reset, screenshot, quit
"""

import json
import socket
import time
from dataclasses import dataclass
from typing import Any, Optional


class AppClient:
    """Connects to ClipTool's TestServer on localhost:18765 to send commands."""

    def __init__(self, host: str = "127.0.0.1", port: int = 18765, timeout: float = 10.0):
        self._host = host
        self._port = port
        self._timeout = timeout
        self._sock: Optional[socket.socket] = None
        self._buf = ""

    # ── Connection management ────────────────────────────────────────────────

    def connect(self) -> None:
        """Open TCP connection to the Godot app."""
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.settimeout(self._timeout)
        self._sock.connect((self._host, self._port))
        self._buf = ""

    def close(self) -> None:
        if self._sock:
            try:
                self._sock.close()
            except Exception:
                pass
            self._sock = None

    def __enter__(self):
        self.connect()
        return self

    def __exit__(self, *args):
        self.close()

    def __del__(self):
        self.close()

    # ── Low-level send/receive ───────────────────────────────────────────────

    def _send(self, cmd: str, params: Optional[dict] = None) -> dict:
        """Send a JSON command line and return the parsed response."""
        if not self._sock:
            raise RuntimeError("Not connected. Call connect() first.")

        payload = {"cmd": cmd}
        if params is not None:
            payload["params"] = params
        line = json.dumps(payload, ensure_ascii=False) + "\n"
        self._sock.sendall(line.encode("utf-8"))

        # Read response lines until we get a complete JSON object
        while True:
            if "\n" in self._buf:
                raw, self._buf = self._buf.split("\n", 1)
                data = json.loads(raw.strip())
                return data
            chunk = self._sock.recv(4096).decode("utf-8")
            if not chunk:
                raise ConnectionError("Server closed connection")
            self._buf += chunk

    def _check(self, resp: dict) -> Any:
        """Check response for success and return result."""
        if not resp.get("ok"):
            raise RuntimeError(f"Command failed: {resp.get('error', 'unknown error')}")
        return resp.get("result")

    # ── Commands ─────────────────────────────────────────────────────────────

    def ping(self) -> str:
        """Check if the app is running. Returns 'pong'."""
        return self._check(self._send("ping"))

    def reset(self) -> None:
        """Reset the project to clean state."""
        self._send("reset")

    def quit(self) -> None:
        """Tell the app to quit."""
        self._send("quit")

    def screenshot(self) -> str:
        """Take a screenshot. Returns absolute path to PNG."""
        return self._check(self._send("screenshot"))["path"]

    def import_file(self, path: str) -> str:
        """Import a video file into the project bin."""
        return self._check(self._send("import_file", {"path": path}))

    def get_tracks(self) -> list:
        """Returns list of track objects."""
        return self._check(self._send("get_tracks"))

    def set_selection(self, track: int, clip: int) -> None:
        """Select a clip on a track."""
        self._send("set_selection", {"track": track, "clip": clip})

    def set_timeline_pos(self, pos: float) -> None:
        """Set the timeline playhead/cursor position."""
        self._send("set_timeline_pos", {"pos": pos})

    def call(self, method: str) -> None:
        """Call a named method on MainWindow.
        
        Supported: OnAddTextClip, OnGenerateCaptions, OnExportPressed,
                   OnAutoFrame, Undo, Redo, OpenStickerBrowser,
                   SplitAtPlayhead, DeleteSelected
        """
        self._send("call", {"method": method})

    def click_button(self, text: str) -> str:
        """Find a Button by text and press it."""
        return self._check(self._send("click_button", {"text": text}))

    def get_property(self, name: str) -> Any:
        """Read a known property value.
        
        Supported: _videoPath, _tracks_count, _currentState,
                   _selTrackIdx, _selClipIdx, _isPlaying
        """
        return self._check(self._send("get_property", {"name": name}))

    def get_ui_state(self) -> dict:
        """Get full UI state: state, tracks, selection, visible buttons."""
        return self._check(self._send("get_ui_state"))

    def list_buttons(self) -> list:
        """List all visible button texts in the UI."""
        return self._check(self._send("list_buttons"))

    # ── Convenience ──────────────────────────────────────────────────────────

    def wait(self, seconds: float) -> None:
        """Sleep for a duration (for async operations to settle)."""
        time.sleep(seconds)

    def wait_for_tracks(self, min_count: int = 1, timeout: float = 30.0) -> list:
        """Poll get_tracks until we have at least min_count tracks."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            tracks = self.get_tracks()
            if len(tracks) >= min_count:
                return tracks
            time.sleep(0.5)
        raise TimeoutError(f"Timed out waiting for {min_count} tracks")

    def assert_state(self, prop: str, expected: Any) -> None:
        """Assert a property equals the expected value."""
        actual = self.get_property(prop)
        assert actual == str(expected) if not isinstance(expected, str) else actual == expected, \
            f"Expected {prop}={expected}, got {actual}"


# ── Quick smoke test ────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys

    with AppClient() as app:
        try:
            pong = app.ping()
            print(f"[ok] ping -> {pong}")

            state = app.get_property("_currentState")
            print(f"[ok] state -> {state}")

            tracks = app.get_tracks()
            print(f"[ok] tracks -> {len(tracks)} track(s)")

            if len(sys.argv) > 1:
                path = sys.argv[1]
                app.import_file(path)
                print(f"[ok] importing {path}")
                app.wait(1)
                tracks = app.get_tracks()
                print(f"[ok] after import: {len(tracks)} track(s)")

            print("[ok] all smoke tests passed")

        except Exception as e:
            print(f"[FAIL] {e}")
            sys.exit(1)
