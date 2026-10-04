# MCP for Unity Launcher

Install this VPM package in every participating Unity project alongside MCP for Unity 10.3.x. On opening a project, Launcher automatically starts a shared local HTTP server and connects the Editor bridge. An independent supervisor keeps the server alive while registered editors remain and restarts owned processes after failures.

Requirements: Unity 2021.3 or later and an installed `uv`/`uvx` runtime configured in MCP for Unity. Initial Python and server downloads may require network access. Windows is the initial validation platform.

Open **Window > MCP for Unity Launcher** for status, logs, and the current project's management switch. Remote HTTP remains under its existing management. Configure your AI client to use the HTTP `/mcp` endpoint, normally `http://127.0.0.1:8080/mcp`.

Each project must install Launcher to participate reliably. Projects with only MCP for Unity retain the original lifecycle behavior. External healthy MCP servers are reused but never terminated by Launcher.

The package uses MCP for Unity's public service interfaces and Python's standard library. It includes no separately installed tray app or login task. See `CHANGELOG.md` for changes and `LICENSE` for the MIT license.
