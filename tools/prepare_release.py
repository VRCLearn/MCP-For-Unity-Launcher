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

This release fixes periodic Unity Editor stalls caused by synchronous uvx cache probes in Launcher 0.4.1. Launch settings are cached, and filesystem and process maintenance run on one background worker without blocking the Editor update callback. Status windows read cached results.

Project registration is verified when connecting or when the underlying WebSocket session changes, removing repeated verification while a confirmed connection remains healthy. Session changes during an in-flight verification cannot mark an obsolete connection as ready. Serialized lease and release writes also prevent late background work from reviving a disabled editor registration.

Multi-project sharing, automatic server recovery, and shutdown after the last managed Unity Editor exits are preserved. Server identity checks, external-server ownership, startup readiness, and busy-server tolerance remain in effect. MCP for Unity 10.3.x remains supported.

The fix was previously tested locally as unreleased 0.4.2; 1.0.0 is its public release. On Windows with Unity 2022.3.22f1, a 90-second low-overhead check of the repaired Launcher update callback recorded a maximum of 10.9 ms and no callback over 16 ms, with the project remaining connected. This measures Launcher work, not unrelated Editor plugins. See the [verification record](https://github.com/VRCLearn/MCP-For-Unity-Launcher/blob/main/docs/verification.md) for automated coverage and platform limits.

Refresh the VPM repository and update **MCP for Unity Launcher** to **1.0.0** in every participating project. Reopen Unity after updating to load the repaired Editor assembly.

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
