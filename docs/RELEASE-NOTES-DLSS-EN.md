# The Beast Roofer Edition, DLSS update

**NVIDIA DLSS comes to The Beast Roofer Edition.**

This update brings a full DLSS pipeline to the Vulkan renderer, with real settings in the interface. Everything is optional, the default behavior stays 100 % stock.

## What you can enable in Settings, Graphics

DLSS with four modes: DLAA (maximum quality), Quality, Balanced and Performance. The game renders at a lower internal resolution and DLSS reconstructs the image, big FPS gains at near native quality.

Frame Generation (RTX 40 and 50 series): off, 2x or 3x. Extra generated frames for a display that is two or three times smoother.

The DLSS model preset, your choice: K, M, L, J or F.

Runtime mipmap generation: many Switch games stream their textures without mipmaps, which shimmers as soon as you upscale. The emulator builds the missing chains in memory, automatically, for any game. No pack, no files, nothing per game.

Real motion vectors: the emulator computes camera accurate motion vectors and feeds them to DLSS and to Frame Generation, with dedicated handling for sky and clouds.

A DLSS setting change applies when the application restarts: one click on Yes in the window that appears and it relaunches itself.

## An honest word

I am not selling you a dream. All of this is still experimental. It works on my machine and on the games I tested, but every game is different and there is work left. If something looks wrong on your side, tell me, that is how this moves forward.

## Requirements and notes

NVIDIA RTX GPU. Frame Generation needs an RTX 40 or 50 series card.

The package ships the `dlss` folder, next to the executable, with NVIDIA's six Streamline files (MIT license): sl.interposer.dll, sl.common.dll, sl.dlss.dll, sl.dlss_g.dll, sl.pcl.dll and sl.reflex.dll.

The DLSS engine DLLs are not included. In that same `dlss` folder, add, taken from a DLSS game you own: nvngx_dlss.dll for upscaling, nvngx_dlssg.dll for Frame Generation, and NvLowLatencyVk.dll if your driver does not already provide it.

If a mod offers its own resolution or upscaling option (for example TOTK Optimizer), set it to native, 1x. One upscaler in the chain: the emulator and DLSS handle resolution.

With Frame Generation and HDR both active, the display encoding switches automatically to PQ: same light, an encoding Frame Generation supports.

Set graphics backend logging to None for full performance.

Compatible with the HDR output from previous releases.

## Camera jitter

We also got camera jitter working, the technique that shifts the image by a fraction of a pixel every frame so DLSS can reconstruct even more detail. It works, but it makes some distant textures shimmer, especially trees. I am still looking for the right way to fix that, so it ships disabled for now. If anyone has an idea, come talk to me.

## Note

If you enable DLSS, set anti-aliasing to None (TAA and DLSS together can crash a game at launch, an automatic guard will come in a next version).

In games with dynamic resolution, Frame Generation may disengage in Quality or Performance mode. Lock the game resolution if its mod allows it.
