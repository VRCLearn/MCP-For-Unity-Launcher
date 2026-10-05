# Changelog

## 0.3.0

- Add native macOS process identity checks using libproc, with matching microsecond timestamps in the Unity integration and Python supervisor.
- Clean up owned Linux and macOS server process groups when the supervisor exits or is forcibly terminated, using a pipe-connected guardian.
- Preserve descendants until group cleanup completes when the server leader exits, and resume a paused guardian when stopping an owned service.
- Validate native process management, C#/Python identity interoperability, and release packaging on Windows, macOS, and Linux with Python 3.10 and 3.12.
- Update all five installation guides with platform scope and validation limits.

## 0.2.0

- Add English, Japanese, Korean, Traditional Chinese, and Simplified Chinese UI selection to the Launcher and dependency setup windows. Remember the selection independently and keep diagnostic logs and error messages in English.
- Connect the Unity project through the actual WebSocket handshake without a blocking TCP pre-check; retry initial failures and resume stopped sessions while preserving upstream socket reconnection.
- Attempt project connection even when supervisor launch fails, and display project connection and session ID separately from server status.
- Show VPM and UPM installation instructions when MCP for Unity is missing or incompatible, with no missing-dependency compilation errors.
- Enable the service integration only when a compatible MCP for Unity package is installed.
- Document UPM installation for any Unity project in all five languages and exclude Python cache metadata from release packages.

## 0.1.0

- Automatic local MCP startup from a VPM-installed Unity Editor integration.
- Shared server ownership independent of the first Unity editor.
- Process-identity registrations that survive domain reloads.
- Server and supervisor recovery with restart delays.
- A project management switch and service status window.
