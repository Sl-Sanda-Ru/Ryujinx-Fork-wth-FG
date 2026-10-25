## BOTW 4K HD Texture Pack (v1.1) — Compatibility Notes

**Pack:** The Legend of Zelda: Breath of the Wild — 4k Texture Pack **v1.1**
(HDPacks.com, ~17.7 GiB — full terrain + vegetation overhaul vs v1.0)

### Requirements
- Game update **1.6.0** (DLC optional, recommended)
- Switch `prod.keys` + firmware
- Emulator with **8 GB extended DRAM**

### Critical setting — REQUIRED
Without this, the game crashes on loading screens (including **shrine entry**):
- **Ryujinx:** Options → Settings → System → **Use alternative memory layout**
- **Yuzu:** Emulation → Configure → System → **Unsafe extended memory layout (8GB DRAM)**

### Install
1. Extract the `4kTextures` folder into the game's mod directory:
   `…\Ryujinx\mods\contents\01007EF00011E000\4kTextures`
   (the `4kTextures` folder goes directly there — no extra nested folder)
2. Remove any older version (e.g. v1.0). v1.1 **replaces** it — do not stack both.

### Notes
- The full 17.7 GiB download already bundles all bug-fix patches up to **2024-12-18**.
  The separate 310 MiB "Patch Only" is for people who grabbed v1.1 *before* that date —
  it strips a bad Resource Size Table (RSTB) that crashed the loading screen on stock 8 GB Ryujinx.
- The author states v1.1 was "minimally tested" at release.

### Verified
- Emulator: Ryujinx Roofer Edition v1.1.2
- Game update: 1.6.0
- Shrine / loading-screen transition: no crash
- FPS: 80–90
