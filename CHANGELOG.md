# Changelog

All notable changes to KnightLock. This project uses [semantic versioning](https://semver.org).

## 1.0.0 — first public release

The first stable release: a native Windows 11 encrypted file vault you can point at a folder.

**Vault engine**
- AES-256-GCM in 1 MiB authenticated chunks, PBKDF2-HMAC-SHA256 key derivation (300k / 600k / 1.2M
  rounds), 128-bit salt, fresh per-container nonce prefix on every seal.
- Header bound into every chunk's associated data, so tampering, dropped chunks, truncation and
  KDF-downgrade attacks all fail to open.
- Deflate compression inside the encrypted stream; multi-gigabyte folders encrypt in constant memory.
- Decrypted plaintext lives only in a private session folder and is overwritten with random data on
  lock, auto-lock, exit, and at startup if a crash left one behind.
- 17-check test suite covering round trips (nested, unicode, empty folders, 24 MB multi-chunk),
  wrong password, tampering, truncation, seal-back of edits, nonce rotation, secure wipe and library
  persistence.

**Shell**
- WinUI 3-style Fluent shell: real Mica/Acrylic/Solid materials with live status, rounded window
  corners, custom title bar, NavigationView rail that expands into a labelled pane, ContentDialogs and
  a circular progress ring.
- Automatic light/dark following Windows, or pinned in Settings; optional system accent colour.
- `.vault` file association (per-user, no elevation) and single-instance hand-off, so double-clicking
  a container opens the running window.
- Maximising respects the taskbar; per-monitor DPI aware; long-path aware.

**Security posture**
- No network code, no telemetry, no accounts, no third-party packages.
- Runs as the invoking user, never elevated.
- Licensed under the GNU General Public License v3.0.
