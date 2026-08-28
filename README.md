<h1 align="center">Dimensions Mod Loader (RPCS3)</h1>

A mod manager for **LEGO Dimensions** running on the **RPCS3** emulator
(PS3, `BLES02105` / `BLUS31473`). It injects modified files directly into the
game's TT Games `.DAT` archives **in place** — no repacking, no growing
archives, and everything is reversible with one click.

Thanks [LEGO Dimensions Discord](https://discord.gg/PuXpBMFE4P) for support!

## Features

- **Safe in-place injection** — modded files overwrite the original bytes
  inside `PATCH.DAT` / `DLC*.DAT`; the archive layout stays untouched (the
  game's streaming engine requires entries to stay sorted by offset, so
  append-style injection crashes it — this loader learned that the hard way).
- **One-click Restore Vanilla** — original bytes and headers are backed up on
  first apply (`.vanilla_backup\` inside the game folder) and fully restored.
- **Load order** — later mods win file conflicts; reorder with Up/Down.
- **Loose file support** — mods can also copy plain files into the game
  folder (with backup/restore handled the same way).
- Parses both TT Games archive generations: the classic disc format and the
  newer `.CC40TAD` format used by the update/DLC archives, including
  embedded-header `.DAT`s.

## Requirements

- Windows with the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- RPCS3 with LEGO Dimensions (disc dump) **and the game update installed**
  (the update's `PATCH.DAT` is what most mods target)

## Setup

1. Unpack the release anywhere and run `DimensionsModManager.exe`.
2. **Game folder** → point it at the *installed update* folder of RPCS3:
   `...\RPCS3\dev_hdd0\game\BLES02105\USRDIR`
   (US version: `BLUS31473`). This is the folder that contains `PATCH.DAT`,
   `PATCH.HDR` and the `DLC*.DAT` files.
3. **Mods folder** → point it at the bundled `mods` folder (or your own).
4. Tick the mods you want, click **Apply Mods**. Done — launch the game.
5. To go back to a clean game, click **Restore Vanilla**.

> ⚠️ Close RPCS3 (or at least the game) before applying/restoring — the
> loader warns you if the emulator is running.

## Bundled mods

| Mod | What it does |
|-----|--------------|
| **Quick Startup** | Cuts the boot splash screens (Legal / IPHolders / Loading) from ~9 s each to 0.1 s. |
| **RPCS3 Text Test** | Proof-of-concept: replaces the title-screen "Press START button" prompt with "MODS WORK ON RPCS3". Handy to verify your setup works. |
| **Super Sonic Infinite** | Removes the ring/stud meter from Sonic's Super transform so it never runs out. Requires the Sonic Level Pack DLC installed. |

The bundled mods were built against the **EU (BLES02105) update (patch
2.4.1)**. Other regions/versions may have slightly different files — if a mod
refuses to apply or misbehaves, just Restore Vanilla.

## How it works (short version)

TT Games archives are a `.DAT` (raw data) + `.HDR` (file table + CRC table)
pair. The CRC table is the authoritative index: `CRC[i]` of the UPPERCASE
backslash path (FNV hash) belongs to file-table entry `i`. The loader finds a
mod file's entry by CRC (with a name-tree fallback), then overwrites the
entry's data in place when the new file fits the existing slot (equal or
smaller size — smaller updates the entry's size fields). Original bytes are
saved to `.vanilla_backup\` before the first write, and `modmanager_state.json`
records everything so Restore Vanilla can put every byte back.

## How to make your own mods

See [how-to-mod.txt](how-to-mod.txt) — the same file ships inside the
release archive. The short version:

```
mods\
  MyCoolMod\
    mod.json                        <- metadata
    datfiles\
      PATCH\STUFF\TEXT\TEXT.CSV     <- goes INTO PATCH.DAT at STUFF\TEXT\TEXT.CSV
      DLC16\...\something.ability   <- goes INTO DLC16.DAT
    files\
      some\loose\file.txt           <- copied into the game folder as-is
```

`mod.json`:

```json
{
  "name": "My Cool Mod",
  "author": "you",
  "version": "1.0",
  "platform": "ps3",
  "description": "What it does and anything the user should know."
}
```

**The golden rule:** a file injected into a `.DAT` must be **the same size or
smaller** than the original. Same-size edits are the safest (the archive is
byte-identical in layout afterwards). For text files you can pad with spaces,
trailing newlines or comment characters to hit the exact size.

## Credits

- Archive format knowledge builds on the TT Games community research
  (QuickBMS `ttgames.bms` by aluigi/linterniGamer, DATManager by connorh315).
- Not affiliated with TT Games, WB Games or LEGO. Bring your own game copy.

## License

MIT — see [LICENSE](LICENSE).
