# MCP for Unity Launcher

**English** | [日本語](docs/i18n/README-ja.md) | [한국어](docs/i18n/README-ko.md) | [繁體中文](docs/i18n/README-zh-TW.md) | [简体中文](docs/i18n/README-zh.md)

Version 0.4.0 also recovers projects whose own connection remains disconnected after the server has recovered. Launcher allows 60 seconds for upstream reconnection, then rebuilds only that project’s client. Connection attempts have a 45-second deadline; readiness requires its session, enabled tools, and a lightweight Editor round trip. The window shows recovery progress and the last verification. Reopen each project after updating so the managed transport is installed before its first connection. This update only requires a new Launcher package.

A Unity Editor addon for [MCP for Unity](https://github.com/VRCLearn/unity-mcp), installable through VPM or UPM, that starts its local server when you open a Unity project and keeps it running across multiple editors.

- **Automatic startup:** open a project to start the server and connect its Unity bridge.
- **Shared service:** when A and B use the same server, closing A keeps B's server running.
- **Recovery:** restart an owned server after it exits or stops responding, and relaunch the supervisor if it fails.

**Install Launcher in every project that needs this behavior.** Projects with only MCP for Unity retain their original startup and shutdown behavior.

## Installation

Requirements: Unity 2021.3 or later and [uv](https://docs.astral.sh/uv/getting-started/installation/). Launcher depends on MCP for Unity 10.3.x and targets Windows, macOS, and Linux desktop Editors.

### VPM

1. Install uv if it is not already available. Restart Unity after installing it so the Editor can find `uv` and `uvx`.
2. Open the [VPM installation page](https://vrclearn.github.io/MCP-For-Unity-Launcher/) and click **Add to VCC/ALCOMD**, or add this repository URL manually:

   ```text
   https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json
   ```

   This single repository provides **MCP for Unity** and **MCP for Unity Launcher**.
3. In each project's package manager, install **MCP for Unity Launcher** (`com.vrclearn.mcp-for-unity-launcher`). VPM also installs its MCP for Unity dependency from the same repository.
4. Open the project. Use **HTTP Local** in **Window → MCP for Unity** and configure your AI client's HTTP connection once. The default endpoint is `http://127.0.0.1:8080/mcp`.

Launcher then starts the local server and connects the Editor automatically. Initial setup may take longer while uv downloads Python and server dependencies. Your AI client still needs its own MCP connection configuration.

### UPM (any Unity project)

VRChat, VCC, and ALCOMD are not required for UPM installation.

1. Install uv and restart Unity so the Editor can find `uv` and `uvx`.
2. Install **MCP for Unity 10.3.x** in the same project, following the [MCP for Unity installation instructions](https://github.com/VRCLearn/unity-mcp). UPM does not resolve this package's `vpmDependencies`, so install the dependency separately before Launcher.
3. Open **Window → Package Manager**, select **+ → Add package from git URL**, and enter:

   ```text
   https://github.com/VRCLearn/MCP-For-Unity-Launcher.git?path=/Packages/com.vrclearn.mcp-for-unity-launcher#main
   ```

4. Use **HTTP Local** in **Window → MCP for Unity** and configure your AI client's HTTP connection. The default endpoint is `http://127.0.0.1:8080/mcp`.

Install Launcher in each participating project. The Git URL selects the package subdirectory on `main`; use one installation method per project.

If MCP for Unity is missing or its version is incompatible, Launcher opens an installation window with VPM and UPM instructions. It waits for a compatible dependency instead of causing missing-dependency compilation errors. Reopen the instructions from **Window → MCP for Unity Launcher**; Launcher enables automatically after installation and recompilation.

## Using multiple editors

Install both packages in projects A and B and use the same local server address. Open both projects; closing A leaves the shared server available to B. When the last managed editor closes, Launcher stops the server it started after a 10-second grace period.

Open **Window → MCP for Unity Launcher** to view service status, participating editors, and logs. Its management switch applies to the current project.

Use the **Language** selector in either Launcher window to choose English, Japanese, Korean, Traditional Chinese, or Simplified Chinese. The default follows your system language, with English as the fallback; your selection is remembered independently of MCP for Unity across projects. Diagnostic logs and runtime error messages stay in English.

## Scope and current validation

Launcher manages local HTTP services. Remote HTTP configurations stay under their existing management, and existing external servers are reused without taking ownership of their processes.

Version 0.3.0 adds native macOS process identities and Linux/macOS cleanup after supervisor crashes. CI checks process management, C#/Python identity interoperability, and release packaging on all three systems. Unity Editor checks use Windows and Unity 2022.3.22f1; interactive multi-Editor acceptance and native Unity behavior on macOS/Linux remain unverified. See the [verification record](docs/verification.md).

## Downloads and documentation

- [Releases](https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases): VPM ZIP, UnityPackage, and package manifest. Prefer VPM; the UnityPackage requires MCP for Unity to be installed separately. Use one distribution format per project.
- [Development and publishing](docs/development.md)
- [Architecture](docs/architecture.md)
- [Report a problem](https://github.com/VRCLearn/MCP-For-Unity-Launcher/issues)

## License

[MIT](LICENSE). MCP for Unity is a separate dependency maintained by its own contributors.
