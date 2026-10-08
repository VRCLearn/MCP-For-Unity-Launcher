# Development and publishing

The Python supervisor uses the standard library. The Editor assembly references MCP for Unity's public service interfaces. See [architecture](architecture.md) and [verification](verification.md) for implementation details and validation limits.

## Repository layout

- `Packages/com.vrclearn.mcp-for-unity-launcher/`: the installable Unity package, including its manifest, Editor code, supervisor, documentation, license, and stable Unity `.meta` files.
- `tests/`: supervisor, distribution, and VPM listing regression tests.
- `tools/`: local verification, package builds, and VPM publishing helpers.
- `.github/`: CI workflows and the combined VPM repository configuration.
- `docs/`: development, architecture, validation, and translated installation guidance.

Only the Unity package directory is copied into release archives and local user packages. Repository tests, tools, workflows, and top-level docs are development resources. Python caches and their Unity-generated `.meta` files are excluded from distributions and ignored by Git; normal Unity metadata must be retained to preserve asset GUIDs.

## Local checks and builds

Run from the repository root:

```powershell
dotnet run --project tests/transport-probe/TransportProbe.csproj --configuration Release
python -m unittest discover -s tests -v
python tools/build_package.py --output dist
```

To verify optional dependency compilation and the Launcher menu using a licensed Unity Editor, prepare a local MCP for Unity 10.3.0 package folder and run:

```powershell
python tools/verify_unity.py --unity-executable "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe" --mcp-package "C:\path\to\unity-mcp\MCPForUnity" --output .compile\dependency-check
```

The output directory must be new. This creates isolated projects with and without MCP for Unity, checks compilation, assembly activation, the menu window, and missing-dependency prompt deduplication, and preserves logs. Both scenarios validate the five-language UI, all translation entries and format placeholders, system defaults, saved and invalid choices, immediate setup-window title changes, and generated English diagnostics. The present-dependency scenario also uses upstream bridge and transport services with test transport clients to check automatic project connection, retry delays, stopped-session recovery, endpoint changes, and preservation of upstream reconnection, bounded hung operations, late completion isolation, registration failures, session changes, and configuration transitions. It restores changed EditorPrefs. It modifies no existing Unity project. The supplied MCP package's own dependencies use Unity's normal package resolution. These batch checks do not start Launcher services, open a real WebSocket, or validate the installation buttons and dropdown visually.

To exercise real two-Editor recovery on an independent server port:

```powershell
python tools/verify_recovery.py --unity-executable "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe" --mcp-package "C:\path\to\MCPForUnity" --server-executable "C:\path\to\mcp-for-unity.exe" --output .compile\two-editor-recovery
```

Use the published MCP for Unity 10.3.0 client and a prepared 10.3.0 server environment. The new output directory receives two generated batch projects, isolated server logs, and `result.json`. A starts before the server; the default 5-second delay includes an unrelated HTTP service that claims to be healthy. The check requires a health probe but no WebSocket request to that service, no premature Ready, and no MCP errors or premature verification warnings. Add `--cold-start-delay 55` to cross the first 45-second deadline and verify automatic retry. Once the real server starts, the check retires A's client while verifying B's session remains intact, then restarts only its own test server and confirms both clients. Generated MCP client copies get a unique EditorPrefs namespace so their temporary URL cannot redirect existing user projects. Production Launcher source and the server protocol remain unchanged. The script never publishes Launcher leases or starts the shared supervisor, and cleans up only its own process trees. Native Unity licensing and package dependency resolution are required.

The .NET lifecycle probe compiles the actual adapter with minimal upstream substitutes. It runs on all CI platforms without Unity licensing; it verifies lifecycle and generation behavior, while the licensed batch checks verify real Unity APIs and WebSockets. Neither substitutes for interactive acceptance on every supported Editor platform.

The build creates a VPM ZIP, a UnityPackage, a staged `package.json`, SHA-256 checksums, and `dist/UserPackages/com.vrclearn.mcp-for-unity-launcher`. Add that folder as a local user package in VCC, or install it with the [VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/):

```powershell
vpm add package "C:\path\to\dist\UserPackages\com.vrclearn.mcp-for-unity-launcher" --project "C:\path\to\UnityProject"
```

## Releases

Stable tags must exactly match the version in `Packages/com.vrclearn.mcp-for-unity-launcher/package.json`, such as `0.3.0`. Publishing a new tag runs Windows, macOS, and Linux tests on Python 3.10 and 3.12, including the production C# identity helper's native interoperability check with .NET 8. All six jobs must pass before packaging and publication. The workflow builds from that tagged commit, creates a Release, and refreshes the default-branch VPM site. If the Release already exists, the workflow leaves its published assets unchanged and refreshes the listing.

Hosted builds take a real HTTPS download URL. Source metadata additionally requires the repository name and full source commit SHA:

```powershell
python tools/build_package.py --output dist --download-url "https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases/download/0.1.0/com.vrclearn.mcp-for-unity-launcher-0.1.0.zip" --repository "VRCLearn/MCP-For-Unity-Launcher" --revision "<full-source-commit-sha>"
```

The hosted build writes a listing for the individual build. The Pages workflow creates the canonical combined listing from the stable Releases of both repositories: Launcher assets remain in this repository and MCP for Unity assets remain in `VRCLearn/unity-mcp`. The combined listing uses the pinned official VRChat template and renderer. The renderer receives explicit release URLs selected by the configured package IDs, so historical archives for other IDs are excluded from the website as well as the index. Unity metadata GUIDs remain stable across builds; existing `.meta` files are preserved.

Launcher releases refresh the combined listing immediately through the publishing workflow. A scheduled check every 15 minutes discovers new MCP for Unity releases without cross-repository credentials; the scheduler may delay runs. Unchanged listings skip the renderer and deployment. A keepalive commit after 45 days of repository inactivity keeps GitHub's scheduled workflow active. The Launcher repository is the only VPM listing publisher.
