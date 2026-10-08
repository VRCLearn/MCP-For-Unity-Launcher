# Changelog

## 0.4.1

- Wait asynchronously for an identifying MCP health response before opening a managed local WebSocket. Keep the 45-second total connection deadline and automatic retries when server startup takes longer.
- Start periodic project verification only after a confirmed connection, avoiding misleading verification failures during cold startup. Show the startup wait in all five UI languages.
- Allow an owned running service 60 seconds of failed health checks before restarting it, with a 3-second request timeout. Preserve the process when temporary server work delays health responses; exited processes still recover immediately.
- Add cold-start, unrelated HTTP service, cancellation, and temporary server-busyness regressions alongside two-Editor recovery checks.
- Reopen updated projects to install the managed transport. To activate the supervisor changes, close all participating Editors and let the old supervisor exit before reopening them. Launcher remains compatible with MCP for Unity 10.3.x.

## 0.4.0

- Recover a single project after upstream reconnection makes no progress for 60 seconds, without restarting the shared server or other projects.
- Bound HTTP connection attempts to 45 seconds and verification to 10 seconds; retire stalled clients and isolate late callbacks from replacement connections.
- Confirm the project session, enabled tool registration, and a lightweight Editor request round trip before reporting a managed local connection as connected. Periodically refresh both Launcher and upstream transport status.
- Show registration, reconnection, retry delay, and last verification in all five UI languages.
- Add asynchronous Unity regressions and an isolated two-Editor real-server recovery check.
- Reopen each updated project to install the managed transport before its first connection. This release changes Launcher only and remains compatible with MCP for Unity 10.3.x.

## 0.3.1

- Preserve running servers when the upstream uv cache probe adds or removes its leading `--offline` flag. Apply the latest cache policy only when another launch is needed, without resetting health checks or recovery backoff.
- Allow editors with different cache probe results to share the same server while retaining conflict detection for actual server configuration changes.

## 0.3.0

- Use the same native Linux boot-time and clock-tick identity in C# and Python, avoiding runtime-dependent `Process.StartTime` offsets.

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
