# MCP for Unity Launcher

Version 0.4.0 also recovers projects whose own connection remains disconnected after the server has recovered. Launcher allows 60 seconds for upstream reconnection, then rebuilds only that project’s client. Connection attempts have a 45-second deadline; readiness requires its session, enabled tools, and a lightweight Editor round trip. The window shows recovery progress and the last verification. Reopen each project after updating so the managed transport is installed before its first connection. This update only requires a new Launcher package.

Install this Unity package through VPM or UPM in every participating project alongside MCP for Unity 10.3.x. On opening a project, Launcher automatically starts a shared local HTTP server and connects the Editor bridge. An independent supervisor keeps the server alive while registered editors remain and restarts owned processes after failures.

Requirements: Unity 2021.3 or later and an installed `uv`/`uvx` runtime configured in MCP for Unity. Initial Python and server downloads may require network access. Desktop platform targets are Windows, macOS, and Linux; see the repository's verification record for native Unity validation limits.

If MCP for Unity is missing or incompatible, Launcher opens installation instructions and keeps its integration disabled until a compatible package is installed. **Window > MCP for Unity Launcher** reopens the instructions. Choose VPM to add `https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json` in VCC/ALCOMD and install MCP for Unity, or use Unity Package Manager's **Add package from git URL** with `https://github.com/VRCLearn/unity-mcp.git?path=/MCPForUnity#10.3.0`. UPM users install this dependency separately.

Open **Window > MCP for Unity Launcher** for status, logs, and the current project's management switch. Remote HTTP remains under its existing management. Configure your AI client to use the HTTP `/mcp` endpoint, normally `http://127.0.0.1:8080/mcp`.

The Language selector in both Launcher windows supports English, Japanese, Korean, Traditional Chinese, and Simplified Chinese. The initial language follows your system language, with English as the fallback. The choice is saved in this user's EditorPrefs and applies to both Launcher windows across projects, independently of MCP for Unity's own choice. Diagnostic logs and runtime error messages remain in English.

Each project must install Launcher to participate reliably. Projects with only MCP for Unity retain the original lifecycle behavior. External healthy MCP servers are reused but never terminated by Launcher.

The package uses MCP for Unity's public service interfaces and Python's standard library. It includes no separately installed tray app or login task. See `CHANGELOG.md` for changes and `LICENSE` for the MIT license.
