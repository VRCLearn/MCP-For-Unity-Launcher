"""Build VPM/UnityPackage archives, a local user package, and hosted metadata."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tarfile
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PACKAGE_ID = 'com.vrclearn.mcp-for-unity-launcher'
PACKAGE = ROOT / 'Packages' / PACKAGE_ID
GUID_NAMESPACE = uuid.UUID('25d53c5a-8fd3-4818-a943-178cf04b1559')
UNITY_ROOT = 'Assets/MCPForUnityLauncher'


def is_python_cache(path: Path) -> bool:
    """Exclude bytecode, cache directories, and their Unity-generated metadata."""
    return ('__pycache__' in path.parts or path.name == '__pycache__.meta'
            or path.name.endswith(('.pyc', '.pyc.meta')))


def ensure_metadata() -> None:
    """Stable GUIDs survive fresh builds; preserve every existing meta file."""
    for entry in sorted(PACKAGE.rglob('*')):
        if entry.suffix == '.meta' or is_python_cache(entry):
            continue
        meta = entry.with_name(entry.name + '.meta')
        if meta.exists():
            continue
        guid = uuid.uuid5(GUID_NAMESPACE, entry.relative_to(PACKAGE).as_posix()).hex
        importer = ('folderAsset: yes\nDefaultImporter:\n    externalObjects: {}\n'
                    if entry.is_dir() else
                    'MonoImporter:\n    externalObjects: {}\n    serializedVersion: 2\n'
                    '    defaultReferences: []\n    executionOrder: 0\n    icon: {instanceID: 0}\n'
                    if entry.suffix == '.cs' else
                    'AssemblyDefinitionImporter:\n    externalObjects: {}\n'
                    if entry.suffix == '.asmdef' else
                    'DefaultImporter:\n    externalObjects: {}\n')
        meta.write_text(f'fileFormatVersion: 2\nguid: {guid}\n{importer}', encoding='utf-8')


def metadata_guid(metadata: bytes) -> str:
    matches = re.findall(r'^guid:\s*([a-fA-F0-9]{32})\s*$', metadata.decode('utf-8-sig'), re.MULTILINE)
    if len(matches) != 1:
        raise ValueError('Unity metadata must contain one valid 32-digit GUID.')
    return matches[0].lower()


def validate_unitypackage(path: Path, manifest: dict) -> dict:
    """Read archive records without extraction and validate paths/GUIDs/manifest."""
    records = {}
    with tarfile.open(path, 'r:gz') as archive:
        names = set()
        for member in archive.getmembers():
            if member.name in names:
                raise ValueError('UnityPackage contains duplicate archive entries.')
            names.add(member.name)
            parts = member.name.rstrip('/').split('/')
            if not re.fullmatch(r'[a-f0-9]{32}', parts[0]):
                raise ValueError('UnityPackage archive entries must start with a GUID.')
            if len(parts) == 1 and member.isdir():
                continue
            if (len(parts) != 2 or parts[1] not in ('asset', 'asset.meta', 'pathname') or
                    not member.isfile()):
                raise ValueError('UnityPackage contains an invalid asset record.')
            records.setdefault(parts[0], {})[parts[1]] = archive.extractfile(member).read()
    assets = {}
    for guid, record in records.items():
        if 'asset.meta' not in record or 'pathname' not in record:
            raise ValueError('UnityPackage is missing metadata or pathname.')
        if metadata_guid(record['asset.meta']) != guid:
            raise ValueError('UnityPackage GUID directory differs from its metadata.')
        pathname = record['pathname'].decode('utf-8')
        parts = pathname.split('/')
        if (pathname != UNITY_ROOT and not pathname.startswith(UNITY_ROOT + '/')) or any(
                part in ('', '.', '..') for part in parts) or '\\' in pathname or '\0' in pathname:
            raise ValueError('UnityPackage pathname must remain beneath its Assets folder.')
        if pathname in assets:
            raise ValueError('UnityPackage contains duplicate asset paths.')
        is_folder = re.search(rb'^folderAsset:\s*yes\s*$', record['asset.meta'], re.MULTILINE) is not None
        if is_folder == ('asset' in record):
            raise ValueError('UnityPackage folder and file records are inconsistent.')
        assets[pathname] = {'guid': guid, **record}
    metadata = assets.get(UNITY_ROOT + '/package.json', {}).get('asset')
    if metadata is None or json.loads(metadata) != manifest:
        raise ValueError('UnityPackage staged package.json differs from its VPM manifest.')
    for relative in ('Editor/LauncherBootstrap.cs', 'Editor/Supervisor/supervisor.py',
                     'Editor/Setup/LauncherDependencyWindow.cs',
                     'Editor/Setup/MCPForUnityLauncher.Setup.Editor.asmdef'):
        if UNITY_ROOT + '/' + relative not in assets:
            raise ValueError('UnityPackage is missing required Launcher asset: ' + relative)
    return assets


def build_unitypackage(path: Path, encoded_manifest: str, files: list[Path]) -> None:
    """Unity folders omit asset data; files use GUID/asset, asset.meta, pathname."""
    root_meta_path = PACKAGE.with_name(PACKAGE.name + '.meta')
    root_guid = uuid.uuid5(GUID_NAMESPACE, UNITY_ROOT).hex
    root_meta = (root_meta_path.read_bytes() if root_meta_path.exists() else
                 f'fileFormatVersion: 2\nguid: {root_guid}\nfolderAsset: yes\n'
                 'DefaultImporter:\n    externalObjects: {}\n'.encode('utf-8'))
    entries = [(UNITY_ROOT, None, root_meta)]
    for source in sorted(PACKAGE.rglob('*')):
        if source.is_dir() and not is_python_cache(source):
            entries.append((UNITY_ROOT + '/' + source.relative_to(PACKAGE).as_posix(),
                            None, source.with_name(source.name + '.meta').read_bytes()))
    for source in files:
        if source.suffix == '.meta':
            continue
        relative = source.relative_to(PACKAGE).as_posix()
        content = encoded_manifest.encode('utf-8') if relative == 'package.json' else source.read_bytes()
        entries.append((UNITY_ROOT + '/' + relative, content,
                        source.with_name(source.name + '.meta').read_bytes()))
    seen = set()
    # Stable gzip/tar metadata makes these archives reproducible across build hosts.
    with path.open('wb') as stream, gzip.GzipFile(filename='', fileobj=stream, mode='wb', mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w', format=tarfile.USTAR_FORMAT) as archive:
            for pathname, content, metadata in entries:
                guid = metadata_guid(metadata)
                if guid in seen:
                    raise ValueError('UnityPackage source contains duplicate GUIDs.')
                seen.add(guid)
                directory = tarfile.TarInfo(guid)
                directory.type = tarfile.DIRTYPE
                directory.mode = 0o755
                archive.addfile(directory)
                record = {'asset.meta': metadata, 'pathname': pathname.encode('utf-8')}
                if content is not None:
                    record['asset'] = content
                for name, data in record.items():
                    info = tarfile.TarInfo(guid + '/' + name)
                    info.size, info.mode = len(data), 0o644
                    archive.addfile(info, io.BytesIO(data))
    validate_unitypackage(path, json.loads(encoded_manifest))


def build(output: Path, download_url: str | None = None,
          repository: str | None = None, revision: str | None = None) -> dict:
    manifest = json.loads((PACKAGE / 'package.json').read_text(encoding='utf-8'))
    if manifest['name'] != PACKAGE_ID or not manifest.get('vpmDependencies'):
        raise ValueError('The package must declare its identity and MCP for Unity dependency.')
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?', manifest['version']):
        raise ValueError('Package version must be a safe semantic version.')
    if repository is not None or revision is not None:
        if not repository or not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or any(
                part in ('.', '..') for part in repository.split('/')):
            raise ValueError('Repository must be a GitHub owner/name.')
        if not revision or not re.fullmatch(r'[a-fA-F0-9]{40}', revision):
            raise ValueError('Revision must be the actual full 40-digit Git commit SHA supplied by the caller.')
        repository_url = f'https://github.com/{repository}'
        manifest['repository'] = {'type': 'git', 'url': repository_url + '.git',
                                  'revision': revision.lower()}
        manifest['sourceRevision'] = revision.lower()
        manifest['changelogUrl'] = f'{repository_url}/releases/tag/{manifest["version"]}'
    required = [PACKAGE / 'Editor' / 'Supervisor' / 'supervisor.py']
    if not all(path.is_file() for path in required):
        raise ValueError('Supervisor implementation is missing.')
    ensure_metadata()
    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / f'{PACKAGE_ID}-{manifest["version"]}.zip'
    # This is a real local artifact URI. Hosted publishing passes its actual HTTPS URL.
    manifest['url'] = download_url or archive_path.resolve().as_uri()
    if download_url and not download_url.startswith('https://'):
        raise ValueError('Hosted VPM downloads must use HTTPS.')
    encoded_manifest = json.dumps(manifest, ensure_ascii=False, indent=4) + '\n'
    files = [p for p in sorted(PACKAGE.rglob('*')) if p.is_file()
             and not is_python_cache(p)]
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in files:
            relative = path.relative_to(PACKAGE).as_posix()
            archive.writestr(relative, encoded_manifest) if relative == 'package.json' else archive.write(path, relative)
    with zipfile.ZipFile(archive_path) as archive:
        embedded = json.loads(archive.read('package.json'))
        if embedded != manifest or archive.testzip() is not None:
            raise ValueError('Archive failed validation.')
        if any(name.startswith('/') or '\\' in name or '..' in PurePosixPath(name).parts
               for name in archive.namelist()):
            raise ValueError('VPM archive contains an unsafe entry path.')
    unitypackage_path = output / f'{PACKAGE_ID}-{manifest["version"]}.unitypackage'
    build_unitypackage(unitypackage_path, encoded_manifest, files)
    # Copy into the local user-package directory without deleting user data.
    user_package = output / 'UserPackages' / PACKAGE_ID
    user_package.mkdir(parents=True, exist_ok=True)
    for path in files:
        destination = user_package / path.relative_to(PACKAGE)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if path.relative_to(PACKAGE).as_posix() == 'package.json':
            destination.write_text(encoded_manifest, encoding='utf-8')
        else:
            shutil.copy2(path, destination)
    sha256 = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    unitypackage_sha256 = hashlib.sha256(unitypackage_path.read_bytes()).hexdigest()
    (output / 'package.json').write_text(encoded_manifest, encoding='utf-8')
    (output / 'SHA256SUMS.txt').write_text(
        f'{sha256}  {archive_path.name}\n{unitypackage_sha256}  {unitypackage_path.name}\n', encoding='utf-8')
    if download_url:
        listing_manifest = dict(manifest, zipSHA256=sha256)
        listing = {'name': 'MCP for Unity Launcher', 'id': PACKAGE_ID,
                   'author': 'VRCLearn', 'packages': {PACKAGE_ID: {
                       'versions': {manifest['version']: listing_manifest}}}}
        (output / 'index.json').write_text(json.dumps(listing, ensure_ascii=False, indent=4) + '\n', encoding='utf-8')
    return {'version': manifest['version'], 'archive': str(archive_path.resolve()),
            'unitypackage': str(unitypackage_path.resolve()), 'unitypackageSha256': unitypackage_sha256,
            'userPackage': str(user_package.resolve()), 'sha256': sha256,
            'hostedListing': bool(download_url)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'dist')
    parser.add_argument('--download-url', help='Actual HTTPS release-asset URL for a hosted VPM listing.')
    parser.add_argument('--repository', help='GitHub repository owner/name for release source metadata.')
    parser.add_argument('--revision', help='Actual full 40-digit Git source commit SHA; requires --repository.')
    options = parser.parse_args()
    print(json.dumps(build(options.output.resolve(), options.download_url,
                           options.repository, options.revision), indent=2))
