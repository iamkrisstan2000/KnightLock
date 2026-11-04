# KnightLock

**A native Windows 11 encrypted file vault.** Point it at a folder, choose a master password, and
KnightLock compresses and seals that folder into a single `.vault` container. Unlock it later, work
with the files in Explorer, lock it again — the container is re-sealed and the decrypted copy is
overwritten on disk.

## Screenshots 

<img width="1919" height="1079" alt="Screenshot 2026-11-04 142755" src="https://github.com/user-attachments/assets/92dcda9f-ab99-4d8b-aba8-d7ceef01b3ba" />
<img width="1919" height="1079" alt="Screenshot 2026-11-04 142746" src="https://github.com/user-attachments/assets/0f338470-754d-47f1-b6a8-f29854eb9144" />
<img width="1919" height="1079" alt="Screenshot 2026-11-04 142730" src="https://github.com/user-attachments/assets/b2b598c8-9b42-4056-abc2-f949b5c66e1a" />
<img width="1919" height="1079" alt="Screenshot 2026-11-04 142721" src="https://github.com/user-attachments/assets/05e598e7-f84b-4704-acff-12050d66833f" />
<img width="1919" height="1079" alt="Screenshot 2026-11-04 142711" src="https://github.com/user-attachments/assets/63a382c6-7e87-4179-a3d8-1ece811a5011" />



No Electron, no browser engine, no cloud, no telemetry, no network code at all. It is a WPF app on
.NET 8 with a hand-built Fluent (WinUI 3) shell: real Mica/Acrylic backdrop, NavigationView sidebar,
ContentDialogs, and automatic light/dark.

[![Licence: GPL v3](https://img.shields.io/badge/licence-GPL--3.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%7C%2011%20x64-0078D4)
![Version](https://img.shields.io/badge/version-1.0.0-brightgreen)

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N7D7292WUD)

---

## Download and run

1. Grab **`KnightLock.exe`** from the [Releases](../../releases) page and run it. It is fully
   self-contained: no installer, no .NET runtime, nothing to set up. First launch takes a couple of
   seconds while the single-file host unpacks itself to a temp folder.
2. Windows SmartScreen will warn you, because the binary is **unsigned**. Choose
   **More info → Run anyway**. (Code signing needs a certificate the platform actually trusts — see
   [Code signing](#code-signing).)
3. Optional smoke test: **My Vaults → Browse for .vault…**, pick
   **`KnightLock-Demo.vault`**, password:

   ```
   KnightLock-Demo-2026!
   ```

   It decrypts to a private temp folder and opens in Explorer. Press **Lock vault** when you are done.

**Verify your download.** In PowerShell:

```powershell
Get-FileHash .\KnightLock.exe -Algorithm SHA256
```

It should print exactly this for v1.0.0:

```
f867a8124e431716337dccc070bbc4595d78d45990e36ef3ee6a162614276a72
```

66,452,115 bytes · PE32+ x64 GUI · self-contained (no .NET runtime required).
If the hash differs, the file was altered in transit — delete it and download again.

---

## What it does

**My Vaults** — a library of your containers with size, file count and last-opened time, each card
showing *Locked* or *Unlocked*. Unlock with the master password and the folder opens in Explorer.
Lock again and KnightLock detects what you changed, re-encrypts it, then overwrites and removes the
decrypted copy. **Lock all vaults** is one click; auto-lock can seal everything after an idle period.

**Create Vault** — pick a source folder (live file/size count), pick where the `.vault` goes, choose
a master password with a strength meter, and optionally wipe the source folder *after* the container
has been written and verified. Progress is a real percentage with a working Cancel button.

**Settings** — theme (System / Light / Dark), window material (Mica / Acrylic / Solid) with live
status of what your build actually supports, accent colour, PBKDF2 work factor, wipe passes, auto-lock
interval, `.vault` file association, and a one-click "wipe decrypted sessions now".

---

## How it protects you

- **AES-256-GCM**, 1 MiB authenticated chunks, so multi-gigabyte folders encrypt in constant memory.
- **PBKDF2-HMAC-SHA256**, 600,000 iterations by default (300k / 600k / 1.2M selectable), 128-bit salt.
- **Everything is authenticated** — the header is bound into every chunk's associated data, so
  tampering, a dropped chunk, truncation, or a lowered iteration count all fail to open.
- **Plaintext only ever lives** in a private temp folder, and is overwritten with random data before
  deletion on lock, auto-lock, exit, and at startup if a crash left one behind.
- **Runs as you, never elevated.** A vault manager has no business being an administrator.
- **No network code in the binary.** Nothing is uploaded, ever.

Honest limits: secure deletion is best-effort on SSDs (BitLocker is the real complement), and while a
vault is *unlocked* its contents are ordinary files on disk. The full design — container format,
nonce/AAD construction, threat model — is in **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

> ⚠️ **Your master password is the only key.** No recovery, no reset, no backdoor. If you lose it,
> the data is gone. That is the design, not a bug.

---

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```cmd
:: Windows — build, run the 17-check test suite, publish and patch the version resource
build.cmd
```

```bash
# any OS — cross-compiles the Windows exe (EnableWindowsTargeting is already set)
dotnet publish src/KnightLock/KnightLock.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o release

# the vault engine tests (no UI, runs anywhere .NET 8 runs)
dotnet run -c Release --project tests/KnightLock.CoreTests
```

Output: a single self-contained `KnightLock.exe`. The UI needs Windows; the engine and its tests do
not.

---

## Project layout

```
KnightLock/
├─ src/KnightLock/
│  ├─ Core/          the vault engine — no UI dependencies, unit tested on Linux
│  ├─ Interop/       DWM materials, rounded corners, accent colour, monitor/work-area
│  ├─ Services/      settings, theme, sessions, shell hand-off, file association
│  ├─ Controls/      ContentDialogHost, ProgressRing, SecretInput, IconPresenter, PathField
│  ├─ Views/         MyVaultsPage, CreateVaultPage, SettingsPage
│  ├─ Themes/        Fluent palettes (light/dark), typography, control styles, icon geometry
│  ├─ Assets/        official artwork + KnightLock.ico (10 sizes) + icon pipeline
│  └─ MainWindow.xaml  the NavigationView shell and custom title bar
├─ tests/KnightLock.CoreTests/   17 checks, no Windows required
├─ tools/            icon sheet, resource audit, version-resource patcher, signing helper
├─ cert/             KnightWin.cer (public certificate)
└─ docs/             architecture notes + icon sheet
```

Nothing is written next to your `.vault` files:

| What | Where |
|------|-------|
| Vault library | `%APPDATA%\KnightLock\vaults.json` |
| Settings | `%APPDATA%\KnightLock\settings.json` |
| Decrypted sessions (wiped on lock/exit) | `%LOCALAPPDATA%\KnightLock\Sessions\` |
| Diagnostic log (never contains secrets) | `%LOCALAPPDATA%\KnightLock\logs\knightlock.log` |

---

## Code signing

The certificate identity for this project is **KnightWin** (`cert/KnightWin.cer`, public half only —
a private key can never be published). To sign a build with your own key:

```powershell
# needs the .pfx that holds the private key, and signtool from the Windows SDK
tools\sign.ps1 -Pfx C:\path\to\KnightWin.pfx
```

Worth knowing: a *self-signed* certificate brands the binary but does not stop the SmartScreen
warning — only a certificate from a trusted CA does. And because the source is here in full, you can
always verify, build and sign it yourself.

---

## Support the project

KnightLock is free, GPL-licensed, and built in evenings. There are no ads, no accounts, no
"pro" tier, no telemetry, and no plan to add any. If it saved you a scare, some time, or just
pleased you, you can buy me a coffee — it goes straight back into more of this.

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N7D7292WUD)

Other ways to help that cost nothing: star the repo, tell someone who keeps a folder of tax
receipts in a zip file, or [open an issue](../../issues) with what confused you — confusing parts are
bugs too.

---

## Licence

**GNU General Public License v3.0** — see [LICENSE](LICENSE).

You may use, study, share and modify KnightLock. If you distribute a modified version, it must stay
under the GPL and you must make the source available. That is the whole point: a tool that guards
your files should never be a black box you have to trust.

```
Copyright (C) 2026 KnightWin

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
General Public License for more details.
```

Built with care. Verify before you trust it with anything you cannot afford to lose.
