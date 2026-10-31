# Changelog

Versions follow [Semantic Versioning](https://semver.org/).

## 0.1.0

First release.

- Controls any monitor that answers DDC/CI, building its controls from the monitor's own capability string.
- Mobile web UI with live updates over WebSocket and a monitor picker.
- Daily controls up top: brightness, input source, picture mode, low blue light. Sliders only respond to a touch that grabs the handle, so scrolling the page does not change settings.
- Hold-to-confirm factory and colour reset, shown only when the monitor advertises them.
- Pair a phone by scanning a QR code. The first run opens a pairing page in the browser; the token travels in the link fragment and is removed from the address bar once stored. Paired phones stay paired across restarts.
- Runs from a tray icon with no console window: Pair a phone, Open controls, Start with Windows and Quit. Launching the exe again while it runs opens the pairing page instead of starting a second server.
- Start with Windows, from the tray menu, the pairing page or `--install` / `--uninstall`. It uses the per-user startup list, so no admin rights are needed and the app runs in your desktop session, which monitor control requires.
- Options: `--port`, `--token`, `--pair`, `--no-browser`, `--install`, `--uninstall`, `--stop`.
- The token lives in `%LOCALAPPDATA%\BenqBridge`, so it survives a read-only install folder, an update or a re-extracted zip.
- Self-contained `win-x64` build, so the .NET runtime does not need to be installed.
- `scripts/release.ps1` builds the release from a clean clone and verifies the zip contents.
- One dependency, QRCoder (MIT), draws the QR code.
