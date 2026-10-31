# Changelog

Versions follow [Semantic Versioning](https://semver.org/).

## Unreleased

- Pair a phone by scanning a QR code. The first run opens a pairing page in the browser; the token travels in the link fragment and is removed from the address bar once stored.
- Sliders only respond to a touch that grabs the handle, so scrolling the page no longer changes brightness or other settings.
- New `--pair` and `--no-browser` options.
- `scripts/release.ps1` builds the release from a clean clone and verifies the zip contents.
- Adds one dependency, QRCoder (MIT), to draw the QR code.

## 0.1.0

First release.

- Controls any monitor that answers DDC/CI, building its controls from the monitor's own capability string.
- Mobile web UI with live updates over WebSocket, token pairing and a monitor picker.
- Daily controls up top: brightness, input source, picture mode, low blue light.
- Hold-to-confirm factory and colour reset, shown only when the monitor advertises them.
- Self-contained `win-x64` build, so the .NET runtime does not need to be installed.
