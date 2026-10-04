"""Validate the stable tag and prepare release artifacts from this exact checkout."""
import json
import os
from pathlib import Path

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

Install **MCP for Unity Launcher** in each project from the [VPM repository](https://vrclearn.github.io/MCP-For-Unity-Launcher/). MCP for Unity is included as a dependency.

Requires Unity 2021.3+, MCP for Unity 10.3.x, and uv. Windows is the initial supported platform. Configure your AI client's local HTTP connection once.

Open **Window → MCP for Unity Launcher** for service status and settings. See the [README](https://github.com/VRCLearn/MCP-For-Unity-Launcher#installation) for setup and platform validation details.
'''
    (output / 'release-notes.md').write_text(notes, encoding='utf-8')
    return result


if __name__ == '__main__':
    print(json.dumps(prepare(), indent=4))
