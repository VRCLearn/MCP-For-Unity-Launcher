"""Validate the stable tag and prepare release artifacts from this exact checkout."""
import json
import os

from build_package import build, PACKAGE, ROOT


def prepare():
    manifest = json.loads((PACKAGE / 'package.json').read_text(encoding='utf-8'))
    version = manifest['version']
    if os.environ['RELEASE_TAG'] != version:
        raise ValueError('Release tag must match the package version exactly.')
    repository = os.environ['RELEASE_REPOSITORY']
    revision = os.environ['RELEASE_REVISION']
    url = f'https://github.com/{repository}/releases/download/{version}/{manifest["name"]}-{version}.zip'
    output = ROOT / 'dist'
    result = build(output, url, repository=repository, revision=revision)
    notes = f'''MCP for Unity Launcher {version} starts and manages MCP for Unity's local HTTP service automatically.

This release waits for an identifying MCP health response before starting a managed local WebSocket. It avoids cold-start connection errors and starts periodic project verification only after a confirmed connection. The window displays the startup wait in all five languages. The 45-second total connection deadline and automatic retry remain in effect when startup takes longer.

An owned running server now gets 60 seconds of failed health checks before restart, with a 3-second request timeout. Temporary server work can delay health responses; this change reduces unnecessary restarts that disconnect every project. Exited processes still recover immediately, and external services remain unowned. This is a mitigation for delayed responses; upstream synchronous file scanning is unchanged.

Per-project recovery from 0.4.0 remains available. Managed local connections must confirm their project session, enabled tools, and a lightweight Editor request round trip before reporting Ready. Retiring one project's client does not restart the shared server.

After updating, close all participating Unity Editors, allow the old supervisor to exit, then reopen the projects. This activates both the managed transport and the new supervisor policy. This release updates Launcher only; MCP for Unity 10.3.x remains supported.

- Starts the service and connects Unity when a project opens.
- Keeps a shared service running when one of several participating editors closes.
- Recovers the service and supervisor after unexpected failures.
- Recovers stalled project connections without restarting the server or other projects.
- Supports English, Japanese, Korean, Traditional Chinese, and Simplified Chinese UI selection; technical diagnostics stay in English.
- Provides VPM and UPM installation guidance when MCP for Unity is missing, without dependency-related compilation errors.
- Uses native macOS process identities and cleans up Linux/macOS child processes after supervisor crashes.
- Gates publication on process, C#/Python identity, and packaging checks on Windows, macOS, and Linux with Python 3.10 and 3.12.

Install **MCP for Unity Launcher** in each project from the [VPM repository](https://vrclearn.github.io/MCP-For-Unity-Launcher/). MCP for Unity is included as a dependency.

UPM installation is also available for any Unity project. Install MCP for Unity separately, then add the Launcher Git package using the [UPM instructions](https://github.com/VRCLearn/MCP-For-Unity-Launcher#upm-any-unity-project).

Requires Unity 2021.3+, MCP for Unity 10.3.x, and uv. Targets Windows, macOS, and Linux desktop Editors. Native Unity validation has been performed on Windows; interactive Unity acceptance on macOS/Linux remains unverified. Configure your AI client's local HTTP connection once.

Open **Window → MCP for Unity Launcher** for service status and settings. See the [README](https://github.com/VRCLearn/MCP-For-Unity-Launcher#installation) for setup and platform validation details.
'''
    (output / 'release-notes.md').write_text(notes, encoding='utf-8')
    return result


if __name__ == '__main__':
    print(json.dumps(prepare(), indent=4))
