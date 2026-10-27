#!/usr/bin/env python3
# Zeiterfassung für Odoo
# Copyright (C) 2026 Baumgärtner Marketing GmbH
# SPDX-License-Identifier: AGPL-3.0-or-later

"""Erzeugt die Bilder des MSIX-Pakets und die ICO-Dateien aus den SVG-Quellen in design/symbole.

Die Quellen stammen aus dem Design System „Symbol Zeiterfassung für Odoo“ (Gruppen „Windows“ und
„Windows-Infobereich“). Braucht rsvg-convert (macOS: brew install librsvg). Die Ergebnisse liegen im Repo,
damit der Build unter Windows ohne rsvg-convert auskommt.

    python3 scripts/make-assets.py
"""

import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "design" / "symbole"
ASSETS = ROOT / "src" / "Zeiterfassung.App" / "Assets"
TRAY = ASSETS / "Tray"
STORE = ROOT / "docs" / "store"

TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]
APP_ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]
TRAY_NAMES = [
    "infobereich-hell", "infobereich-hell-eingestempelt", "infobereich-hell-abgemeldet",
    "infobereich-dunkel", "infobereich-dunkel-eingestempelt", "infobereich-dunkel-abgemeldet",
]


def render(svg: Path, size: int, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(svg), "-o", str(target)], check=True)


def write_ico(pngs: list[tuple[int, bytes]], target: Path) -> None:
    """ICO mit PNG-Bildern, wie Windows es seit Vista liest."""
    target.parent.mkdir(parents=True, exist_ok=True)
    header = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries = b""
    for size, data in pngs:
        side = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    target.write_bytes(header + entries + b"".join(data for _, data in pngs))


def main() -> int:
    if shutil.which("rsvg-convert") is None:
        print("rsvg-convert fehlt (brew install librsvg)", file=sys.stderr)
        return 1
    with tempfile.TemporaryDirectory() as temp:
        temp_dir = Path(temp)

        def png(name: str, size: int) -> Path:
            path = temp_dir / f"{name}-{size}.png"
            if not path.exists():
                render(SOURCE / f"{name}.svg", size, path)
            return path

        # Symbol der App in Taskleiste, Startmenü und Einstellungen; jede Größe unter drei Namen, sonst legt
        # Windows eine Platte hinter das Symbol.
        for size in TARGET_SIZES:
            for suffix in ["", "_altform-unplated", "_altform-lightunplated"]:
                shutil.copy(png("windows", size), ASSETS / f"Square44x44Logo.targetsize-{size}{suffix}.png")
        for scale, size in [(100, 44), (200, 88), (400, 176)]:
            shutil.copy(png("windows", size), ASSETS / f"Square44x44Logo.scale-{scale}.png")
        for scale, size in [(100, 50), (125, 63), (150, 75), (200, 100), (400, 200)]:
            shutil.copy(png("windows", size), ASSETS / f"StoreLogo.scale-{scale}.png")
        for scale, size in [(100, 150), (200, 300), (400, 600)]:
            shutil.copy(png("windows-kachel", size), ASSETS / f"Square150x150Logo.scale-{scale}.png")

        write_ico([(size, png("windows", size).read_bytes()) for size in APP_ICON_SIZES], ASSETS / "App.ico")
        for name in TRAY_NAMES:
            write_ico([(size, png(name, size).read_bytes()) for size in TRAY_SIZES], TRAY / f"{name}.ico")

        # App-Kachel für den Eintrag im Microsoft Store
        STORE.mkdir(parents=True, exist_ok=True)
        shutil.copy(png("windows", 300), STORE / "app-kachel-300.png")
    print(f"Bilder erzeugt in {ASSETS.relative_to(ROOT)} und {STORE.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
