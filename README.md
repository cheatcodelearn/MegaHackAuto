# MegaHackAuto

Small installer I made because every "free mega hack" download I found was either a
repack with god knows what inside, or a Geode-only tutorial that didn't work on my setup.

This one doesn't contain Mega Hack. It's just a downloader + patcher. You run it, it pulls
the real Mega Hack from absolllute's own CDN, patches the license check, drops it into your
Geometry Dash folder and starts the game. The exe is ~19 KB because there's nothing in it.

## Why it's like this

I didn't want to upload a cracked DLL and ask people to trust a random file. So the repo is
the actual source - you can read every line and compile it yourself. The only thing it does
is HTTP requests, byte pattern matching, and file copies.

If you don't trust the build in Releases, build it yourself. That's the point.

## What it does

- reads the vendor manifest at `absolllute.com/api/mega_hack/v9/install.json`
- takes the newest standalone Windows bundle from it
- downloads the zip and checks the SHA-256 against the manifest
- patches three license checks inside `hackpro.dll` (all found by pattern, not offsets)
- copies everything into the GD folder, backs up the old DLL as `.orig`
- launches Geometry Dash
- next time you run it, it checks the manifest again and updates itself if needed

Patched spots, if you care:

| what | found by | change |
| --- | --- | --- |
| signature check branch | `"signature invalid"` string + xref | 8 nops |
| id / HWID compare | prologue `56 57 48 83 EC 48 48 83 79 10 40` | `mov eax,1; ret` |
| login gate | `"data empty"` string + calls to the loader | 8 nops |

No hardcoded addresses, so a Mega Hack update that keeps its strings still gets patched
without me touching the code.

## Requirements

- Geometry Dash 2.2091 (Steam, Windows)
- close the game before running it

## How to use

1. download `MegaHackAuto.zip` from Releases
2. close Geometry Dash
3. run `MegaHackAuto.exe`
4. wait for `Installed Mega Hack`
5. game opens, press TAB

If Windows SmartScreen complains (it will, the exe is unsigned), More info then Run anyway.

## Notes

- it keeps a backup of your original DLL as `hackpro.dll.orig`
- if you already have Mega Hack installed it just updates / re-patches it
- it refuses to run while the game is open, so it doesn't half-copy files
- no telemetry, no auto-start, no services, no drivers. read the code.

## Build

```
csc /optimize+ /target:exe /out:MegaHackAuto.exe ^
    /r:System.Web.Extensions.dll ^
    /r:System.IO.Compression.dll ^
    /r:System.IO.Compression.FileSystem.dll ^
    /r:System.Net.Http.dll ^
    MegaHackAuto.cs
```

.NET Framework 4.x, Windows.

## Disclaimer

Educational use, patching software you're supposed to pay for is your own call and your own
risk. I'm not the author of Mega Hack and this repo has no affiliation with absolllute.
