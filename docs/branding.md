# Dalamud MCP logo

The cyan crystal represents the game client; three connection terminals form an M-shaped frame for the protocol bridge, with a crimson lunar arc behind the crystal. The midnight background and text-free silhouette keep the icon readable at plugin-list sizes.

- `assets/branding/logo-master.png`: original generated artwork, 1254×1254.
- `src/DalamudMCP/images/icon.png`: 512×512 PNG, downsampled for plugin use.
- The project manifest's `IconUrl` points to the committed PNG for repository distribution.
- Debug and Release copy `images/icon.png` beside the DLL for local development installs. DalamudPackager publishes `images/icon.png` beside the Release `latest.zip` as its normal image sidecar; the ZIP itself contains the embedded settings logo in the DLL. Keep the images directory when moving a dev build.
- The settings header reads the same PNG as the embedded `DalamudMCP.icon.png` resource through Dalamud's shared texture provider. Loading is asynchronous; status/configuration remain available before the texture is ready.

The design was generated with OpenAI image generation and mechanically resized for distribution; no existing game or protocol logo was copied. Keep the master when exporting further sizes.

Dalamud's [plugin image cache](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Interface/Internal/Windows/PluginImageCache.cs) expects square icons no larger than 512×512 and discovers a development plugin's local icon under `images/icon.png`. Settings textures are managed by Dalamud; their per-frame wraps are not disposed by this plugin.
