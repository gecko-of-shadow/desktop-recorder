"""Region selector (X11): a still of the screen; drag a rectangle. Space = whole screen, Esc = no recording.

The Windows selector does more (click a window, keys C, M, P); this is the part that the Folder for AI needs.
"""

from __future__ import annotations

from typing import Callable

MIN_SIZE = 16   # a click or a shorter drag is no region

Region = tuple[int, int, int, int]
WHOLE_SCREEN: Region = (0, 0, 0, 0)   # the settings value for "no region"


def drag_region(x0: float, y0: float, x1: float, y1: float, width: int, height: int) -> Region | None:
    """The dragged rectangle inside the screen, with an even size; None for a click or a tiny drag."""
    left, right = sorted(int(round(max(0, min(v, width)))) for v in (x0, x1))
    top, bottom = sorted(int(round(max(0, min(v, height)))) for v in (y0, y1))
    w, h = right - left, bottom - top
    w, h = w - w % 2, h - h % 2
    if w < MIN_SIZE or h < MIN_SIZE:
        return None
    return left, top, w, h


def select_region(done: Callable[[Region | None], None]) -> str | None:
    """Open the selector; done(region) runs after it is closed and the screen shows the apps again.

    done(None): Esc. done(WHOLE_SCREEN): Space. Returns an error text when the screen cannot be read.
    """
    import gi

    gi.require_version("Gdk", "3.0")
    gi.require_version("Gtk", "3.0")
    from gi.repository import Gdk, GLib, Gtk

    root = Gdk.get_default_root_window()
    width, height = root.get_width(), root.get_height()
    shot = Gdk.pixbuf_get_from_window(root, 0, 0, width, height)
    if shot is None:
        return "The screen could not be read (the region selector needs X11)."
    state: dict = {"start": None, "end": None, "closed": False}
    window = Gtk.Window()
    window.set_decorated(False)
    window.set_keep_above(True)
    window.set_skip_taskbar_hint(True)
    window.move(0, 0)
    window.set_default_size(width, height)
    area = Gtk.DrawingArea()
    area.add_events(Gdk.EventMask.BUTTON_PRESS_MASK | Gdk.EventMask.BUTTON_RELEASE_MASK
                    | Gdk.EventMask.POINTER_MOTION_MASK)
    window.add(area)

    def current() -> Region | None:
        start, end = state["start"], state["end"]
        if start is None or end is None:
            return None
        return drag_region(start[0], start[1], end[0], end[1], width, height)

    def draw(_w, cr) -> bool:
        Gdk.cairo_set_source_pixbuf(cr, shot, 0, 0)
        cr.paint()
        cr.set_source_rgba(0, 0, 0, 0.45)   # the screen goes darker, the region keeps its colors
        cr.paint()
        region = current()
        if region is not None:
            x, y, w, h = region
            cr.save()
            cr.rectangle(x, y, w, h)
            cr.clip()
            Gdk.cairo_set_source_pixbuf(cr, shot, 0, 0)
            cr.paint()
            cr.restore()
            cr.set_source_rgb(1.0, 0.25, 0.25)
            cr.set_line_width(2)
            cr.rectangle(x - 1, y - 1, w + 2, h + 2)
            cr.stroke()
            label = f"{w} x {h}"
        else:
            label = "Drag a rectangle. Space: whole screen. Esc: cancel."
        cr.set_font_size(16)
        tx, ty = (region[0], max(18, region[1] - 8)) if region is not None else (12, 28)
        cr.set_source_rgb(0, 0, 0)
        cr.rectangle(tx - 4, ty - 17, 8 + cr.text_extents(label).x_advance, 23)
        cr.fill()
        cr.set_source_rgb(1, 1, 1)
        cr.move_to(tx, ty)
        cr.show_text(label)
        return False

    def finish(region: Region | None) -> None:
        if state["closed"]:
            return
        state["closed"] = True
        seat = Gdk.Display.get_default().get_default_seat()
        seat.ungrab()
        window.destroy()

        # Give the apps under the still a moment to draw again before the recording starts.
        def later() -> bool:
            done(region)
            return False

        GLib.timeout_add(200, later)

    def press(_w, event) -> bool:
        if event.button == 1:
            state["start"] = state["end"] = (event.x_root, event.y_root)
        return True

    def motion(_w, event) -> bool:
        if state["start"] is not None:
            state["end"] = (event.x_root, event.y_root)
            area.queue_draw()
        return True

    def release(_w, event) -> bool:
        if event.button != 1 or state["start"] is None:
            return True
        state["end"] = (event.x_root, event.y_root)
        region = current()
        if region is None:   # a click: start again
            state["start"] = state["end"] = None
            area.queue_draw()
            return True
        finish(region)
        return True

    def key(_w, event) -> bool:
        if event.keyval == Gdk.KEY_Escape:
            finish(None)
        elif event.keyval == Gdk.KEY_space:
            finish(WHOLE_SCREEN)
        return True

    def mapped(_w, _e) -> bool:
        # Without the grab, a window manager could keep the keys on another window.
        seat = Gdk.Display.get_default().get_default_seat()
        seat.grab(window.get_window(), Gdk.SeatCapabilities.ALL, True, None, None, None, None)
        return False

    area.connect("draw", draw)
    area.connect("button-press-event", press)
    area.connect("motion-notify-event", motion)
    area.connect("button-release-event", release)
    window.connect("key-press-event", key)
    window.connect("map-event", mapped)

    def closed(_w, _e) -> bool:
        finish(None)
        return True

    window.connect("delete-event", closed)
    window.show_all()
    window.fullscreen()
    window.present()
    return None
