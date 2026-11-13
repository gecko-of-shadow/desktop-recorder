"""xdg-desktop-portal ScreenCast.

Ubuntu's ffmpeg 6.1 package has no PipeWire input, so the portal file
descriptor is handed to GStreamer and the pictures are encoded by ffmpeg.
"""

from __future__ import annotations

import os
import uuid
from dataclasses import dataclass
from typing import Any, Protocol


class PortalError(RuntimeError):
    pass


@dataclass
class PortalStream:
    session: str
    node_id: int
    serial: str | None
    fd: int
    restore_token: str
    width: int | None = None
    height: int | None = None


class PortalTransport(Protocol):
    def request(self, method: str, options: dict[str, Any]) -> dict[str, Any]:
        """Run one portal method and return the Response results dictionary."""

    def open_pipewire(self, session: str) -> int:
        """Return an owned file descriptor for the PipeWire remote."""

    def close_session(self, session: str) -> None:
        """End the portal session."""


class ScreenCast:
    """Chooses sources and reads the stream list. The transport does the D-Bus calls."""

    def __init__(self, transport: PortalTransport) -> None:
        self.transport = transport

    def open(self, restore_token: str = "") -> PortalStream:
        session_token = "dr" + uuid.uuid4().hex[:16]
        created = self.transport.request(
            "CreateSession",
            {"session_handle_token": session_token},
        )
        session = str(created.get("session_handle") or "")
        if not session:
            raise PortalError("The screen cast session did not start.")
        select: dict[str, Any] = {
            "session": session,
            "types": 1,  # monitors, the same idea as one whole desktop
            "multiple": True,
            "cursor_mode": 2,  # draw the pointer into the picture
            "persist_mode": 2,
        }
        if restore_token:
            select["restore_token"] = restore_token
        self.transport.request("SelectSources", select)
        started = self.transport.request("Start", {"session": session, "parent": ""})
        streams = started.get("streams") or []
        if not streams:
            self.transport.close_session(session)
            raise PortalError("No screen was shared.")
        node_id, props = streams[0]
        props = props or {}
        serial = props.get("pipewire-serial")
        size = props.get("size")
        width = height = None
        if isinstance(size, (tuple, list)) and len(size) >= 2:
            width, height = int(size[0]), int(size[1])
        fd = self.transport.open_pipewire(session)
        return PortalStream(
            session=session,
            node_id=int(node_id),
            serial=str(serial) if serial else None,
            fd=fd,
            restore_token=str(started.get("restore_token") or restore_token or ""),
            width=width,
            height=height,
        )


class GioTransport:
    """Real portal client. Needs a session bus and xdg-desktop-portal."""

    def __init__(self) -> None:
        import gi

        gi.require_version("Gio", "2.0")
        gi.require_version("GLib", "2.0")
        from gi.repository import Gio, GLib

        self._Gio = Gio
        self._GLib = GLib
        self._bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)

    def request(self, method: str, options: dict[str, Any]) -> dict[str, Any]:
        token = "dr" + uuid.uuid4().hex[:16]
        path = _request_path(self._bus.get_unique_name(), token)
        payload = dict(options)
        payload["handle_token"] = token
        session = payload.pop("session", None)
        parent = payload.pop("parent", None)
        variant_options = {key: _variant(self._GLib, value) for key, value in payload.items()}
        if method == "CreateSession":
            parameters = self._GLib.Variant("(a{sv})", (variant_options,))
        elif method == "SelectSources":
            parameters = self._GLib.Variant("(oa{sv})", (session, variant_options))
        elif method == "Start":
            parameters = self._GLib.Variant("(osa{sv})", (session, parent or "", variant_options))
        else:
            raise PortalError("Unknown portal method " + method)
        return self._call(method, path, parameters)

    def _call(self, method: str, path: str, parameters: Any) -> dict[str, Any]:
        Gio = self._Gio
        GLib = self._GLib
        box: dict[str, Any] = {}
        loop = GLib.MainLoop()

        def on_signal(_conn, _sender, object_path, _interface, signal, params):
            if signal != "Response" or object_path != path or params is None:
                return
            code, details = params.unpack()
            box["code"] = code
            box["details"] = details
            loop.quit()

        subscription = self._bus.signal_subscribe(
            "org.freedesktop.portal.Desktop",
            "org.freedesktop.portal.Request",
            "Response",
            path,
            None,
            Gio.DBusSignalFlags.NONE,
            on_signal,
        )
        try:
            self._bus.call_sync(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.ScreenCast",
                method,
                parameters,
                GLib.VariantType.new("(o)"),
                Gio.DBusCallFlags.NONE,
                120000,
                None,
            )
            GLib.timeout_add(120000, lambda: loop.quit() or False)
            loop.run()
        finally:
            self._bus.signal_unsubscribe(subscription)
        if "code" not in box:
            raise PortalError("The screen share dialog timed out.")
        if box["code"] != 0:
            raise PortalError("Screen sharing was cancelled.")
        details = box["details"] or {}
        return dict(details)

    def open_pipewire(self, session: str) -> int:
        GLib = self._GLib
        parameters = GLib.Variant("(oa{sv})", (session, {}))
        result, fds = self._bus.call_with_unix_fd_list_sync(
            "org.freedesktop.portal.Desktop",
            "/org/freedesktop/portal/desktop",
            "org.freedesktop.portal.ScreenCast",
            "OpenPipeWireRemote",
            parameters,
            GLib.VariantType.new("(h)"),
            self._Gio.DBusCallFlags.NONE,
            30000,
            None,
            None,
        )
        index = result.unpack()[0]
        raw = fds.get(index)
        owned = os.dup(raw)
        os.close(raw)
        return owned

    def close_session(self, session: str) -> None:
        try:
            self._bus.call_sync(
                "org.freedesktop.portal.Desktop",
                session,
                "org.freedesktop.portal.Session",
                "Close",
                None,
                None,
                self._Gio.DBusCallFlags.NONE,
                5000,
                None,
            )
        except Exception:
            return


def _request_path(unique_name: str, token: str) -> str:
    sender = unique_name[1:].replace(".", "_") if unique_name.startswith(":") else unique_name.replace(".", "_")
    return f"/org/freedesktop/portal/desktop/request/{sender}/{token}"


def _variant(glib: Any, value: Any) -> Any:
    if isinstance(value, bool):
        return glib.Variant("b", value)
    if isinstance(value, int):
        return glib.Variant("u", value)
    if isinstance(value, str):
        return glib.Variant("s", value)
    raise PortalError("Cannot send portal option of type " + type(value).__name__)
