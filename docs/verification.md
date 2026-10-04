# Verification

Validated on Windows on 2026-10-04:

- **62 Python tests passed.** Launcher uses `com.vrclearn.mcp-for-unity-launcher` throughout its source folder, archives, CI, and VPM configuration. Tests cover excluding releases for other package IDs and providing the official renderer with only the configured release URLs.

- **27 Python tests passed** (Python 3.12, 6.576 seconds). Coverage includes two editor registrations, singleton startup races, exact process creation time, PID reuse, reload tolerance, explicit release and reenable, restart backoff, startup grace, health timeouts, endpoint and command conflicts, external service preservation, atomic status contention, argument fidelity, child-process tree cleanup, and supervisor crash cleanup.
- **Editor assembly compiled successfully** with Unity 2022.3.22f1's Roslyn compiler and reference assemblies plus the actual MCP for Unity 10.3.0 Editor assembly. There were no compiler warnings or errors.
- **The actual `uv run --no-project --python ">=3.10"` bootstrap command completed** with a state directory containing spaces. The idle supervisor published valid status and exited cleanly. The sandbox check redirected uv cache/install directories into the workspace and exposed the existing Python runtime on PATH.
- **A real MCP for Unity 10.3.0 server passed the lifecycle smoke check**, with two ordinary processes standing in for Editor registrations. Closing A retained B and the original server PID; terminating the owned server produced an automatic healthy replacement; killing the supervisor cleaned up its Windows Job; starting a replacement supervisor recovered B; closing B stopped the owned service and supervisor after the grace period. This check used the existing local server environment on an isolated free port and modified no user project.
- **VPM packaging passed archive integrity and embedded-manifest checks.** The build generated the zip, a local user-package folder, stable Unity metadata, and a SHA-256 checksum. Distribution tests additionally validate UnityPackage GUIDs, import paths, staged metadata, and checksums.
- **The complete publication test suite passed: 45 tests in 7.772 seconds.** It includes the original 27 supervisor tests plus 8 distribution tests and 10 VPM listing tests. GitHub workflow syntax was checked with actionlint; the official template compatibility patch also passed against the pinned template.
- **The unified VPM repository suite passed: 60 tests in 8.081 seconds.** Additional cases cover the two-package mapping, missing dependencies, identical version numbers in different packages, and metadata-only update detection. A live listing build verified Launcher 0.1.0 plus MCP for Unity 10.1.2, 10.2.0, and 10.3.0.

No interactive Unity acceptance test, VCC install, AI-client end-to-end call, or non-Windows runtime test has been run. Process stand-ins do not exercise Editor callbacks or Unity WebSocket bridge reconnection. The smoke check manually starts the replacement supervisor; Unity's replacement-launch watchdog is compiled but has not been exercised inside a live Editor.

Reproduce the real-server check with a prepared MCP for Unity server environment:

```powershell
python tools/smoke_server.py --server-executable "C:\path\to\Server\.venv\Scripts\mcp-for-unity.exe" --state-dir ".test-state\new-real-server-check"
```

The state directory must be new. The script reserves an isolated loopback port, starts only its own editor stand-ins and supervisor, and stops them on exit.

The initial 0.1.0 release retains these unverified interactive acceptance cases. Run them in two isolated Unity projects with both packages installed when validating Editor behavior:

1. Open A; its MCP server and bridge should start without opening the MCP window.
2. Open B; both editors should share the endpoint without creating a second server.
3. Close A; B should remain connected and the service should keep running.
4. Close B; the owned service and supervisor should exit after the grace period.
5. Open A and B again, kill only the owned server, and verify recovery and bridge reconnection.
6. Kill the supervisor; verify a live editor starts a replacement and no duplicate server survives.
7. Recompile scripts and enter/leave Play mode; registrations should survive domain reload.
8. Occupy the configured port with an unrelated process; Launcher should report the conflict and leave that process alone.
9. Select remote HTTP; Launcher should not redirect or manage that remote server.
10. Disable management for A while B remains enabled; B's service should remain alive.

No existing user Unity projects are modified by the development checks.
