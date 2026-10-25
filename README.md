# 🐺 The Beast Roofer Edition

A custom edition of the **Ryujinx** Nintendo Switch emulator, built for **large HD/4K texture packs**, image quality and smooth gameplay.

**🇫🇷 [Version française disponible ici](README.fr.md)**

> ⚠️ **Emulator only.** No games, no keys, no firmware.

---

## ⬇️ Download

Latest release:

👉 https://thebeastroofer-edition.com/rooferdev/the-beast-roofer-edition/releases

The app then updates itself automatically from the project's own update server.

---

## 🐤 Canary diagnostic build (temporary)

If the emulator crashes for you when DLSS is enabled (at game launch, or when
toggling fullscreen / changing resolution), a special diagnostic build is
available. It is v1.2.2 with extra logging that helps us find the cause — no
new features, and it never auto-updates.

👉 Download and instructions: https://thebeastroofer-edition.com/rooferdev/the-beast-roofer-edition/issues/2

Reminder: DLSS requires an NVIDIA RTX GPU. GTX cards, integrated graphics,
AMD and Intel GPUs cannot run DLSS at all.

---

## 💬 Community

Discussion, setups, questions: join the community on Reddit.

👉 https://www.reddit.com/r/BeastRooferEdition

---

## ✨ What this edition adds

### 🟢 NVIDIA DLSS, DLAA and Frame Generation (new, RTX only, opt-in)

A full DLSS pipeline, with real settings in **Settings, Graphics**:

DLSS with four modes: DLAA (maximum quality), Quality, Balanced and Performance.
Frame Generation (RTX 40 and 50 series): off, 2x or 3x.
The DLSS model preset, your choice: K, M, L, J or F.
A change applies when the application restarts: one click on Yes and it relaunches itself.

The package ships the `dlss` folder with NVIDIA's six Streamline files (MIT license). The DLSS engine DLLs are not included (legal reasons): in that same `dlss` folder, add, taken from a DLSS game you own, `nvngx_dlss.dll` for upscaling, `nvngx_dlssg.dll` for Frame Generation, and `NvLowLatencyVk.dll` if your driver does not already provide it.

I am not selling you a dream: all of this is still experimental. It works on my machine and on the games I tested, but every game is different and there is work left. If something looks wrong, tell me.

### 🧬 Runtime mipmap generation (new)

Many Switch games stream their textures without mipmaps, which shimmers as soon as you upscale. One checkbox in the graphics settings and the emulator builds the missing chains in memory, automatically, for any game. No pack, no files, nothing per game.

### 🎬 Native TAA (Temporal Anti-Aliasing)

Option in **Settings, Graphics, Anti-Aliasing**. Soft, cinematic, low flicker image. Works on all GPUs, no extra files needed. Looks best at higher internal resolution.

### 🧩 HD/4K texture pack fix

Fixes a crash that prevented large ASTC texture packs from loading (for example the Zelda BOTW and TOTK 4K packs). Large textures are decoded slice by slice instead of overflowing memory, far more stable with high resolution packs.

### 🖥️ VRR mode (Variable Refresh Rate)

Lets a G-Sync or FreeSync display follow the game's frame pacing, with a per game FPS cap (Off, 30, 60, 120). Smoother frame pacing on compatible displays.

### 🔄 Automatic updates

The app updates itself automatically from the project's own update server.

---

## 🖥️ Requirements

8 GB of RAM, a 6 core CPU recommended, a GPU from the last 10 years.
OpenGL 4.6 or Vulkan 1.4.
Windows 10 (20H1 or newer), macOS Big Sur (Apple Silicon).
For DLSS: an NVIDIA RTX GPU. For Frame Generation: an RTX 40 or 50 series card.

---

## 🙏 Credits and license

Based on **Ryujinx**, the open source emulator created by **gdkchan** and maintained by the **Ryubing** team.

Original project: https://git.ryujinx.app
**MIT** licensed, see `LICENSE.txt`.

Original code in this fork by **The Roofer Dev** (© 2026), MIT licensed: clean room integration of NVIDIA DLSS, DLAA and NIS, native TAA, native scRGB HDR, per game VRR cap, runtime mipmap generation. Code and integration only: the DLSS, DLAA and NIS technologies belong to NVIDIA. NVIDIA, DLSS, DLAA and NIS are trademarks of NVIDIA Corporation. Independent project, not affiliated with or endorsed by NVIDIA.

---

## ⚠️ Disclaimer

**Not affiliated with Nintendo in any way.** This project is not associated with, endorsed by, or sponsored by Nintendo. "Nintendo Switch" and all game names are trademarks of their respective owners.

---

## 🚫 Zero proprietary content

There are **no keys and no firmware here. Zero. Nada.** This emulator never contains, and never will contain, any games, keys, or firmware. You must provide your own, legally dumped from hardware you own.

**We do not condone or encourage piracy in any way**, and we will never share games, keys, or firmware. This project exists solely to run content you legally own.

---

## 🆓 Volunteer and free

This is a free, non commercial, **volunteer** project. It earns no money, takes no donations, and sells nothing. Personal time given freely for the community.

Questions or feedback? **therooferdev@gmail.com**
