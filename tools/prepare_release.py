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

- Starts the service and connects Unity when a project opens.
- Keeps a shared service running when one of several participating editors closes.
- Recovers the service and supervisor after unexpected failures.
- Retries project connection and resumes stopped sessions, with connection status and session ID shown separately.
- Supports English, Japanese, Korean, Traditional Chinese, and Simplified Chinese UI selection; technical diagnostics stay in English.
- Provides VPM and UPM installation guidance when MCP for Unity is missing, without dependency-related compilation errors.
- Adds native macOS process identities and Linux/macOS child-process cleanup after supervisor crashes.
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
