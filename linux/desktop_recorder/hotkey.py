"""Hotkey text shared with the Windows build.

Modifiers: 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win (Super).
The key is a letter, a digit, or F1-F24. Shift alone cannot take a letter or a digit.
"""

from __future__ import annotations


def parse_key(name: str) -> str | None:
    if name is None:
        return None
    text = name.strip()
    if len(text) == 1:
        if text.isalpha():
            return text.upper()
        if text.isdigit():
            return text
        return None
    if len(text) == 2 and text[0] in "Dd" and text[1].isdigit():
        return text[1]
    if text[:1] in "Ff" and text[1:].isdigit():
        number = int(text[1:])
        if 1 <= number <= 24:
            return "F" + str(number)
    return None


def is_valid(modifiers: int, key: str | None) -> bool:
    if modifiers == 0 or not key:
        return False
    if modifiers & (1 | 2 | 8):
        return True
    return key.startswith("F")


def try_set(text: str | None, current: tuple[int, str]) -> tuple[int, str] | None:
    """Return the new hotkey, or None when text is not usable."""
    if text is None:
        return None
    modifiers = 0
    found = None
    for raw in text.split("+"):
        part = raw.strip()
        if not part:
            return None
        lower = part.lower()
        if lower == "win":
            modifiers |= 8
        elif lower == "ctrl":
            modifiers |= 2
        elif lower == "alt":
            modifiers |= 1
        elif lower == "shift":
            modifiers |= 4
        else:
            key = parse_key(part)
            if key is None or found is not None:
                return None
            found = key
    if not is_valid(modifiers, found):
        return None
    return modifiers, found or current[1]


def text_of(modifiers: int, key: str) -> str:
    return (
        ("Win+" if modifiers & 8 else "")
        + ("Ctrl+" if modifiers & 2 else "")
        + ("Alt+" if modifiers & 1 else "")
        + ("Shift+" if modifiers & 4 else "")
        + key
    )


def gtk_accelerator(modifiers: int, key: str) -> str:
    """GNOME custom-shortcut binding, such as <Super><Shift>d."""
    parts = []
    if modifiers & 8:
        parts.append("<Super>")
    if modifiers & 2:
        parts.append("<Control>")
    if modifiers & 1:
        parts.append("<Alt>")
    if modifiers & 4:
        parts.append("<Shift>")
    if len(key) == 1 and key.isalpha():
        parts.append(key.lower())
    else:
        parts.append(key)
    return "".join(parts)
