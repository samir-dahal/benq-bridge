# Changelog

Versions follow [Semantic Versioning](https://semver.org/).

## 0.1.0

First release.

- Controls any monitor that answers DDC/CI, building its controls from the monitor's own capability string.
- Mobile web UI with live updates over WebSocket, token pairing and a monitor picker.
- Daily controls up top: brightness, input source, picture mode, low blue light.
- Hold-to-confirm factory and colour reset, shown only when the monitor advertises them.
- Self-contained `win-x64` build, so the .NET runtime does not need to be installed.
