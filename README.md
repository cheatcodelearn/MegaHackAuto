# MegaHackAuto

Installer for Mega Hack v9 on Windows. Downloads the genuine bundle from the
vendor CDN, verifies the SHA-256 from the vendor manifest, patches the local
license checks, installs it into Geometry Dash, and launches the game.

Nothing is bundled in this repo — every run pulls the current build straight
from `absolllute.com` and verifies it before touching it.

## Requirements

- Windows, .NET Framework 4.x
- Geometry Dash 2.2091 (Steam)
- Geode v6 for the Geode build (`-geode` / `MegaHackAutoGeode.exe`)
- Game closed before running

## Usage

| Command | What it does |
|---|---|
| `MegaHackAuto.exe` | installs the standalone `hackpro.dll` build |
| `MegaHackAutoGeode.exe` | installs the Geode mod |
| `MegaHackAuto.exe -geode` | installs the Geode build from the standalone binary |
| `-standalone` | forces the standalone build from `MegaHackAutoGeode.exe` |
| `-install-only` | install without launching the game |
| `-strict` | abort instead of installing when no patch pattern matches |
| `-dump` | extract + patch a copy to `.\mh-geode-dump`, install nothing |
| `-check` | dry run: chosen bundles, hashes, URL status |
| `-where` | list every GD install, Geode version, mods dir, megahack/node-ids state |
| `-dir=PATH` | pin the game folder |
| `-license=DIR` | pin the license stub folder |

## Geode notes

Geode v6 reads mods from `<GD>\geode\mods`; older versions read `<GD>\mods`.
The tool detects the loader version from `geode/logs` and installs into the
right folder, removing a stale copy from the old location if one exists.

Mega Hack declares hard dependencies (`geode.node-ids`, `absolllute.installer`)
and Geode silently skips the mod when one is missing. The tool reads the
dependencies out of the bundle's own `mod.json`, pulls anything missing from the
Geode index, hash-verifies it, patches its DLLs the same way, and installs it.
Mods already present are left alone.

Dependency archives that .NET cannot unpack (node-ids ships LZMA) are installed
untouched — they carry no license code to patch.

A full game restart is required after installing — Geode only scans mods at
boot.

## Patches

All patches are pattern based, never hardcoded addresses.

| Target | Anchor | Replacement |
|---|---|---|
| ID/HWID compare | prologue `56 57 48 83 EC 48 48 83 79 10 40` | `mov eax,1; ret` |
| Signature check | `"signature invalid"` + nearby `test al,al; jz` | 8 × `90` |
| Login gate | `"data empty"` + nearby `test al,al; jz` | 8 × `90` |
| Geode login state | cached login accessor prologue + fast path | `mov eax,1; ret` |

The ID and login-state patches only apply when their signature appears exactly
once in the binary, so a vendor update can never turn them into a blind patch of
the wrong function. The two string-based branches are absent from current 9.2.0
builds (they are kept for older versions) — the per-file report shows which
anchors hit.

## Build

Two sources, one per target.

```bat
:: standalone
csc /optimize+ /target:exe /out:MegaHackAuto.exe ^
  /r:System.Web.Extensions.dll ^
  /r:System.IO.Compression.dll ^
  /r:System.IO.Compression.FileSystem.dll ^
  /r:System.Net.Http.dll ^
  MegaHackAuto.cs

:: geode
csc /optimize+ /target:exe /out:MegaHackAutoGeode.exe ^
  /r:System.Web.Extensions.dll ^
  /r:System.IO.Compression.dll ^
  /r:System.IO.Compression.FileSystem.dll ^
  /r:System.Net.Http.dll ^
  MegaHackAutoGeode.cs
```

## Network use

Only two hosts are contacted:

- `absolllute.com` — vendor manifest and bundle CDN
- `api.geode-sdk.org` — node-ids dependency (only when it is missing)

No telemetry, no auto-start, no services, no drivers, no registry writes.

## Notes

- A license stub is written to `%LOCALAPPDATA%\absolllute.megahack\license` if
  no license file exists.
- The previous install is kept as `*.orig` next to the new file.
- The game is launched detached (`cmd /c start`), so closing the terminal that
  ran the installer does not close Geometry Dash.
- Unaffiliated with absolllute, Mega Hack, Geode, or RobTop. Patching paid
  software is your own call.
