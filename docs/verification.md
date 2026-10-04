# Verification

## Optional dependency checks — 2026-10-05

Unity 2022.3.22f1 compiled isolated projects with the current Launcher package and with or without the actual MCP for Unity 10.3.0 source package:

- Without MCP for Unity, only `MCPForUnityLauncher.Setup.Editor` compiled; there were no missing-dependency compiler errors.
- With MCP for Unity 10.3.0, both Setup and the original integration assembly compiled successfully.
- A test copy declaring MCP for Unity 10.2.0 left only Setup enabled, confirming the supported version range.
- The Launcher menu created the installation window when the dependency was absent and the original status window when present.
- Invoking the missing-dependency prompt twice, closing its window between calls, opened it only once in the same Editor session.
- After removing an embedded dependency test copy, Unity finished reimporting with only Setup enabled and no final compilation failure. The first import encountered transient stale-cache errors for removed upstream source paths, so that filesystem-removal probe was not an entirely error-free import.

The 62 Python tests and both package builds also passed. Use the native verification command in [development](development.md) to reproduce the missing/present checks. These batch checks create real Editor windows but do not verify their visual appearance, installation-button interaction, or Git/VPM installation end to end. Launcher service behavior remains covered by the existing process tests and prior validation below.

## Initial release validation

The latest recorded validation for version 0.1.0 was performed on Windows on 2026-10-04:

- **62 Python tests passed.** Coverage includes editor registrations and release, singleton startup, process identity and PID reuse, reload tolerance, restart delays, health checks, configuration conflicts, external service preservation, atomic status updates, argument fidelity, and owned process-tree cleanup. Distribution and VPM tests cover archive integrity, stable Unity GUIDs, manifests, checksums, the two-package mapping, metadata updates, and filtering releases by package ID for both the index and official renderer.
- **Editor assembly compiled successfully** with Unity 2022.3.22f1's Roslyn compiler and reference assemblies plus the actual MCP for Unity 10.3.0 Editor assembly. There were no compiler warnings or errors.
- **The actual `uv run --no-project --python ">=3.10"` bootstrap command completed** with a state directory containing spaces. The idle supervisor published valid status and exited cleanly. The sandbox check redirected uv cache/install directories into the workspace and exposed the existing Python runtime on PATH.
- **A real MCP for Unity 10.3.0 server passed the lifecycle smoke check**, with two ordinary processes standing in for Editor registrations. Closing A retained B and the original server PID; terminating the owned server produced an automatic healthy replacement; killing the supervisor cleaned up its Windows Job; starting a replacement supervisor recovered B; closing B stopped the owned service and supervisor after the grace period. This check used the existing local server environment on an isolated free port and modified no user project.
- **Publishing checks passed.** GitHub workflow syntax was checked with actionlint, the compatibility patch passed against the pinned official template, and a live listing build verified Launcher 0.1.0 plus MCP for Unity 10.1.2, 10.2.0, and 10.3.0.

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
