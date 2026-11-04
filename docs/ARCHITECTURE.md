# KnightLock — architecture & design notes

This is the long version: container format, crypto construction, plaintext handling, the shell's
design decisions, and the honest limitations. For the short version, see the [README](../README.md).

---

## Container format (`KnightLock Vault v1`)

```
offset  size  field
0       16    magic "KNIGHTLOCK.VAULT"
16      2     format version (=1)
18      2     flags (reserved)
20      4     header length
24      N     header — UTF-8 JSON (id, name, timestamps, KDF params, nonce prefix,
              key verifier, file count, source bytes). Not secret, fully authenticated.
24+N    …     chunk stream:
                4    ciphertext length (uint32 LE)
                N    ciphertext
                16   AES-GCM tag
              repeated, terminated by the sentinel 0xFFFFFFFF
```

- **Cipher** — AES-256-GCM, 1 MiB plaintext chunks, so multi-gigabyte folders encrypt in constant
  memory (the ZIP writer streams directly into the encryptor — no second copy on disk).
- **Nonce** — 12 bytes: a random 8-byte per-container prefix from the CSPRNG plus a 32-bit chunk
  counter. A fresh prefix is generated on every seal, so a nonce is never reused under the same key.
- **Associated data** — every chunk's GCM AAD is `SHA-256(header) || chunkIndex`. Flipping one byte
  of ciphertext, dropping a chunk, truncating the file, or editing the header (e.g. lowering the
  iteration count) all fail authentication.
- **Key derivation** — PBKDF2-HMAC-SHA256, 128-bit random salt, 600,000 iterations by default. The
  password is UTF-8 encoded into a byte buffer that is zeroed after use; the derived key is zeroed
  when the session ends.
- **Password verifier** — a truncated `HMAC-SHA256(key, label||salt)` stored in the header lets
  KnightLock say *"incorrect master password"* immediately instead of a generic authentication error.
  It does not weaken the container: offline guessing was already possible against the GCM tags.
- **Compression** — Deflate inside the encrypted stream (container ≈ compressed size; typically
  30–70 % smaller than the source folder).

## Plaintext handling

- Decrypted files only ever live under `%LOCALAPPDATA%\KnightLock\Sessions\<random>\<vault name>`.
- Locking (or auto-lock, or exit) overwrites each file in place with CSPRNG data, then zeros, flushes
  to disk, renames and deletes it, and removes the folder tree.
- On startup, any session folder left by a crash or power loss is wiped before the UI appears.
- Ordinary (non-elevated) user token; a vault manager has no business running as admin.

## Honest limitations

- **Secure deletion is best-effort.** On SSDs, wear levelling and the drive's own FTL mean no
  user-mode software can guarantee a physical block is gone. Full-disk encryption (BitLocker) is the
  real complement to this.
- **Unlocked means unlocked.** While a vault is open, its plaintext exists on disk and can be read by
  anything running as you. Lock promptly; use auto-lock.
- **Memory is not a vault.** WPF strings for passwords can be moved by the GC before the buffer is
  zeroed. .NET gives no `mlock` guarantees.
- **Not audited.** The format and implementation are straightforward and the engine is covered by a
  17-check test suite, but this is a focused, well-built tool — not a replacement for VeraCrypt or
  BitLocker when your threat model includes targeted forensic attack.
- **Encrypted, not hidden.** The header reveals a vault's name, timestamps and file count. There is
  no plausible deniability or hidden volume.

---

## Test suite

17 checks, all of which run without Windows because the engine has no UI dependencies:

nested/unicode/empty-folder round trips, a 24 MB multi-chunk payload verified by SHA-256, wrong
password, tampered ciphertext, truncated container, KDF-downgrade attack on the header, foreign
files, password gating, seal-back of edits, nonce rotation per seal, progress phases, secure wipe,
library persistence/pruning, session isolation, and password verification.

```bash
dotnet run -c Release --project tests/KnightLock.CoreTests
```

---

## The shell

- **One chrome surface.** The navigation pane runs from the very top of the window and owns the brand
  row, the hamburger, the navigation items and the session footer; the content column carries only the
  minimise/maximise/close buttons. Both the brand row and the thin strip above the content are drag
  areas, with Explorer-style double-click-to-maximise.
- **Collapsed and expanded share one icon axis.** 48 px icon rail by default; expanding widens the
  pane and reveals labels without moving the icon column (24 px from the pane edge in both states).
- **One geometry per rounded corner.** The content well's rounded top-left corner is defined by a
  clip alone — `CornerRadius` is deliberately 0. Rounding the border *and* clipping it put two
  anti-aliased edges on the same arc, multiplying their partial alphas into a hairline seam that the
  bright Acrylic backdrop showed through.
- **The corner notch is sealed, not cut.** That rounded corner is a cut-out, so the sliver outside the
  arc is a hole onto whatever is behind the window. Over Mica and Solid that backing surface is dark
  and reads as a fillet; over Acrylic it was the wallpaper, measurably *brighter than the rail and the
  well alike*. The sliver is filled with the chrome brush under Acrylic only
  (`WellGeometry.CornerNotch`, the exact complement of the clip's quarter disc).
- **DWM writes are idempotent.** DWM repaints the non-client frame for every attribute write, and
  re-declaring the rounded corners or the system backdrop tears them down and rebuilds them — which
  showed as a one-frame flicker of the window edge when switching dark/light. `WindowMaterial`
  remembers what it has written and only talks to DWM when a value genuinely changed.
- **Real Mica.** `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` on 22H2+, `DWMWA_MICA_EFFECT` on
  21H2, Acrylic through `SetWindowCompositionAttribute` on Windows 10, and a painted Fluent surface
  when the OS can't composite anything — the app tells you which one is active in Settings. Acrylic
  additionally gets a 3 % veil, because DWM's transient backdrop lets a little too much of a bright
  wallpaper through at this window size.
- **Custom title bar** via `WindowChrome` (`GlassFrameThickness=-1`, `CaptionHeight=0`) so the
  material runs edge to edge, with Explorer-style drag, Aero snap, double-click maximise and a
  hover-red close button. Maximising answers `WM_GETMINMAXINFO` with the monitor work area, so the
  window never covers the taskbar.
- **Self-contained icon system.** Icons are hand-authored as path geometry on a 24×24 grid
  (`Themes/Icons.xaml`) and drawn by `IconPresenter`, which inherits `TextElement.Foreground` so
  glyphs pick up hover/pressed/disabled states automatically. No icon font, no missing-glyph boxes,
  identical rendering on Windows 10 and 11.
- **Official icon pipeline.** `src/KnightLock/Assets/icon-source.png` is the master artwork; the white
  matte around the navy tile is cropped away and the tile squircle is rebuilt as a clean alpha mask
  (`Assets/build-icon.py`), so the icon never ships a white square. 10 ICO sizes, with zoomed artwork
  ≤ 24 px. `docs/icons.png` is generated from the shipped `.ico`.
- **Typography & materials.** Every control is re-templated against the WinUI 3 resource vocabulary
  (`CardBackgroundFillColorDefaultBrush`, `TextFillColorSecondaryBrush`, `AccentFillColorDefaultBrush`,
  …) with Windows 11 metrics: 4/8 px radii, 1 px hairlines, translucent hover overlays, Segoe UI
  Variable type ramp, 32 px control heights.
- **No third-party packages.** The whole UI, spinner, dialog system and crypto pipeline are
  hand-written on the BCL — nothing to supply-chain.

## Runtime footprint

- Workstation, concurrent GC: the server GC's per-core heaps would reserve memory a single-window app
  can never use.
- `MemoryTrimmer` compacts the large object heap once and asks Windows to trim the working set after a
  heavy operation (the progress overlay closing, the window losing focus, everything locking). A large
  seal or unlock pushes the heap far above the idle footprint; the GC would happily keep that memory
  reserved, which is right for a server and wrong for a desktop app.
- Only artwork the app actually draws is compiled into the exe; larger icon sizes are generated from
  `icon-source.png` rather than embedded.
- ReadyToRun is *not* enabled by default. It trades roughly a third more download size for a slightly
  faster cold start, which is the wrong bargain for a self-contained 66 MB single file — pass
  `-p:PublishReadyToRun=true` if you would rather have the opposite.
