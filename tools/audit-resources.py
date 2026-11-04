#!/usr/bin/env python3
"""
Static resource audit for the KnightLock WPF shell.

Parses every XAML dictionary for x:Key definitions and every XAML/C# usage site
(StaticResource / DynamicResource / FindResource / SetResourceReference) and reports any
resource that is referenced but never defined. WPF only fails at run time on a missing key
(the app dies with a XamlParseException), and the shell cannot be launched on a Linux
build box, so this audit is the safety net.

Usage:  python3 tools/audit-resources.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "KnightLock"

DEF_RE = re.compile(r'x:Key="([^"]+)"')
USE_RE = re.compile(r'(?:StaticResource|DynamicResource)\s+([A-Za-z0-9_.]+)')
CS_RES_RE = re.compile(r'(?:FindResource|TryFindResource|SetResourceReference)\(\s*(?:"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))')

# Keys WPF provides itself / that are resolved by the system theme dictionary.
SYSTEM_KEYS = {"SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1"}


def main() -> int:
    defined: dict[str, Path] = {}
    for path in sorted(SRC.rglob("*.xaml")):
        for key in DEF_RE.findall(path.read_text(encoding="utf-8")):
            defined.setdefault(key, path)

    used: dict[str, Path] = {}
    for path in sorted(list(SRC.rglob("*.xaml")) + list(SRC.rglob("*.cs"))):
        text = path.read_text(encoding="utf-8")
        for key in USE_RE.findall(text):
            used.setdefault(key, path)
        for a, b in CS_RES_RE.findall(text):
            if a:
                used.setdefault(a, path)

    missing = {k: v for k, v in used.items() if k not in defined and k not in SYSTEM_KEYS}
    unused = sorted(k for k in defined if k not in used)

    print(f"defined: {len(defined)}   referenced: {len(used)}   missing: {len(missing)}")
    for key, path in sorted(missing.items()):
        print(f"  MISSING  {key}  (used in {path.relative_to(ROOT)})")
    if unused:
        print(f"  defined-but-unreferenced (ok for styles used via key in other dicts): {len(unused)}")
        for key in unused[:20]:
            print(f"    · {key}")

    # leftover icon-font glyphs would render as tofu squares
    tofu = []
    for path in sorted(SRC.rglob("*.xaml")):
        if "&#xE" in path.read_text(encoding="utf-8"):
            tofu.append(path)
    print("icon-font glyph escapes:", tofu if tofu else "none")

    return 1 if missing or tofu else 0


if __name__ == "__main__":
    sys.exit(main())
