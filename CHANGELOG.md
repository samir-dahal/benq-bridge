# Changelog

Versions follow [Semantic Versioning](https://semver.org/).

## Unreleased

- Pair a phone by scanning a QR code. The first run opens a pairing page in the browser; the token travels in the link fragment and is removed from the address bar once stored.
- Sliders only respond to a touch that grabs the handle, so scrolling the page no longer changes brightness or other settings.
- New `--pair` and `--no-browser` options.
- `scripts/release.ps1` builds the release from a clean clone and verifies the zip contents.
- Adds one dependency, QRCoder (MIT), to draw the QR code.
- No more console window: the app runs from a tray icon with Pair a phone, Open controls, Start with Windows and Quit.
- Start with Windows, from the tray menu, the pairing page or `--install` / `--uninstall`. It uses the per-user startup list, so no admin rights are needed and the app runs in your desktop session, which monitor control requires.
- Launching the exe again while it runs opens the pairing page instead of starting a second server. `--stop` asks the running copy to quit.
- The token now lives in `%LOCALAPPDATA%\BenqBridge`, so it survives a read-only install folder, an update or a re-extracted zip. A 0.1.0 `token.txt` next to the exe is carried over, so paired phones stay paired.

## 0.1.0

First release.

- Controls any monitor that answers DDC/CI, building its controls from the monitor's own capability string.
- Mobile web UI with live updates over WebSocket, token pairing and a monitor picker.
- Daily controls up top: brightness, input source, picture mode, low blue light.
- Hold-to-confirm factory and colour reset, shown only when the monitor advertises them.
- Self-contained `win-x64` build, so the .NET runtime does not need to be installed.
