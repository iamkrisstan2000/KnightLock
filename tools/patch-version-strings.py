#!/usr/bin/env python3
"""
Byte-patches the published KnightLock.exe so its Win32 version resource reports
KnightLock.exe (rather than KnightLock.dll) as OriginalFilename / InternalName.

`dotnet publish` names the payload assembly KnightLock.dll and copies that string into
the resource; Explorer's Details tab then shows "KnightLock.dll" as the original filename.
Both strings are the same length, so the patch is a straight in-place swap of the UTF-16
code units -- no offsets, sizes or checksums change.

Occurrences: 2 in the .rsrc of the host executable, plus 2 in the copy of the assembly
embedded in the single-file bundle.

Usage:  python3 patch-version-strings.py <path-to-KnightLock.exe>
"""

import sys

OLD = "KnightLock.dll".encode("utf-16-le")
NEW = "KnightLock.exe".encode("utf-16-le")
assert len(OLD) == len(NEW)


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    path = sys.argv[1]
    data = bytearray(open(path, "rb").read())

    hits, start = 0, 0
    while True:
        at = data.find(OLD, start)
        if at == -1:
            break
        data[at:at + len(OLD)] = NEW
        hits += 1
        start = at + len(NEW)

    if hits == 0:
        print("no occurrences found -- already patched?", file=sys.stderr)
        return 1

    open(path, "wb").write(data)
    print(f"patched {hits} occurrence(s) of KnightLock.dll -> KnightLock.exe in {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
