"""Distribution contracts: VPM roots, Unity GUID records, source metadata, hashes."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path, PurePosixPath
import shutil
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('launcher_packaging', ROOT / 'tools/build_package.py')
builder = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = builder
spec.loader.exec_module(builder)


class PackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='Launcher packaging tests ')
        self.root = Path(self.temp.name)
        self.package = self.root / builder.PACKAGE_ID
        shutil.copytree(builder.PACKAGE, self.package)
        self.package_patch = patch.object(builder, 'PACKAGE', self.package)
        self.package_patch.start()
        self.manifest = json.loads((self.package / 'package.json').read_text(encoding='utf-8'))

    def tearDown(self):
        self.package_patch.stop()
        self.temp.cleanup()

    def test_legacy_local_build_generates_both_archives_without_hosted_listing(self):
        output = self.root / 'local output'
        original = (self.package / 'package.json').read_bytes()
        result = builder.build(output)
        self.assertTrue(Path(result['archive']).is_file())
        self.assertTrue(Path(result['unitypackage']).is_file())
        self.assertEqual(Path(result['unitypackage']).name,
                         '{}-{}.unitypackage'.format(builder.PACKAGE_ID, self.manifest['version']))
        self.assertFalse((output / 'index.json').exists())
        self.assertFalse(result['hostedListing'])
        self.assertEqual((self.package / 'package.json').read_bytes(), original)
        staged = json.loads((output / 'package.json').read_text())
        self.assertEqual(staged['url'], Path(result['archive']).as_uri())
        self.assertEqual(staged, json.loads((Path(result['userPackage']) / 'package.json').read_text()))
        self.assertNotIn('sourceRevision', staged)

    def test_checksums_match_actual_both_archives_and_hosted_zip_hash(self):
        output = self.root / 'hosted'
        url = 'https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases/download/{}/{}-{}.zip'.format(
            self.manifest['version'], builder.PACKAGE_ID, self.manifest['version'])
        result = builder.build(output, url, 'VRCLearn/MCP-For-Unity-Launcher', 'a1' * 20)
        entries = {}
        for line in (output / 'SHA256SUMS.txt').read_text().splitlines():
            checksum, name = line.split('  ')
            self.assertEqual(hashlib.sha256((output / name).read_bytes()).hexdigest(), checksum)
            entries[name] = checksum
        self.assertEqual(set(entries), {Path(result['archive']).name, Path(result['unitypackage']).name})
        self.assertEqual(result['sha256'], entries[Path(result['archive']).name])
        self.assertEqual(result['unitypackageSha256'], entries[Path(result['unitypackage']).name])
        listing = json.loads((output / 'index.json').read_text())
        version = listing['packages'][builder.PACKAGE_ID]['versions'][self.manifest['version']]
        self.assertEqual(version['zipSHA256'], result['sha256'])
        self.assertEqual(version['url'], url)
        staged = json.loads((output / 'package.json').read_text())
        self.assertEqual(version, dict(staged, zipSHA256=result['sha256']))
        self.assertEqual(staged['sourceRevision'], 'a1' * 20)
        self.assertEqual(staged['repository'], {
            'type': 'git', 'url': 'https://github.com/VRCLearn/MCP-For-Unity-Launcher.git',
            'revision': 'a1' * 20})
        self.assertEqual(staged['changelogUrl'],
                         'https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases/tag/' + self.manifest['version'])

    def test_zip_entries_stay_relative_and_unitypackage_preserves_source_guids(self):
        cache = self.package / 'Editor/Supervisor/__pycache__'
        cache.mkdir(exist_ok=True)
        (cache / 'supervisor.pyc').write_bytes(b'exclude bytecode')
        (cache / 'supervisor.pyc.meta').write_text('exclude cached asset metadata')
        cache.with_name(cache.name + '.meta').write_text('exclude cache folder metadata')
        bytecode = self.package / 'Editor/Supervisor/supervisor.pyc'
        bytecode.write_bytes(b'exclude legacy bytecode')
        bytecode.with_name(bytecode.name + '.meta').write_text('exclude bytecode metadata')
        result = builder.build(self.root / 'output')
        with zipfile.ZipFile(result['archive']) as archive:
            names = archive.namelist()
            self.assertIn('package.json', names)
            self.assertIn('Editor/Supervisor/supervisor.py', names)
            self.assertNotIn('__pycache__', '\n'.join(names))
            self.assertNotIn('.pyc', '\n'.join(names))
            self.assertEqual(len(names), len(set(names)))
            for name in names:
                self.assertFalse(name.startswith('/'))
                self.assertNotIn('\\', name)
                self.assertNotIn('..', PurePosixPath(name).parts)
            manifest = json.loads(archive.read('package.json'))
        assets = builder.validate_unitypackage(Path(result['unitypackage']), manifest)
        user_files = [path.relative_to(result['userPackage']).as_posix()
                      for path in Path(result['userPackage']).rglob('*')]
        for paths in (names, assets, user_files):
            self.assertNotIn('__pycache__', '\n'.join(paths))
            self.assertNotIn('.pyc', '\n'.join(paths))
        for source in self.package.rglob('*'):
            if source.suffix == '.meta' or '__pycache__' in source.parts or source.suffix == '.pyc':
                continue
            pathname = builder.UNITY_ROOT + '/' + source.relative_to(self.package).as_posix()
            record = assets[pathname]
            metadata = source.with_name(source.name + '.meta').read_bytes()
            self.assertEqual(record['asset.meta'], metadata)
            self.assertEqual(record['guid'], builder.metadata_guid(metadata))
            self.assertEqual(record['pathname'].decode(), pathname)
            if source.is_dir():
                self.assertNotIn('asset', record)
            elif source.name != 'package.json':
                self.assertEqual(record['asset'], source.read_bytes())
        self.assertNotIn('asset', assets[builder.UNITY_ROOT])

    def test_assets_fallback_root_resolves_manifest_and_supervisor(self):
        result = builder.build(self.root / 'output')
        manifest = json.loads((self.root / 'output/package.json').read_text())
        assets = builder.validate_unitypackage(Path(result['unitypackage']), manifest)
        bootstrap = PurePosixPath(builder.UNITY_ROOT + '/Editor/LauncherBootstrap.cs')
        # ResolveScript uses the directory above the discovered Editor script.
        root = bootstrap.parent.parent
        self.assertEqual(root.as_posix(), builder.UNITY_ROOT)
        self.assertEqual(json.loads(assets[(root / 'package.json').as_posix()]['asset']), manifest)
        self.assertIn((root / 'Editor/Supervisor/supervisor.py').as_posix(), assets)

    def test_unitypackage_reproducible_and_existing_folder_meta_preserved(self):
        root_guid = 'bc' * 16
        root_meta = ('fileFormatVersion: 2\nguid: ' + root_guid + '\nfolderAsset: yes\n'
                     'DefaultImporter:\n    externalObjects: {}\n    userData: retained\n').encode()
        self.package.with_name(self.package.name + '.meta').write_bytes(root_meta)
        url = 'https://example.org/launcher.zip'
        first = builder.build(self.root / 'first', url)
        second = builder.build(self.root / 'second', url)
        self.assertEqual(Path(first['unitypackage']).read_bytes(), Path(second['unitypackage']).read_bytes())
        manifest = json.loads((self.root / 'first/package.json').read_text())
        root = builder.validate_unitypackage(Path(first['unitypackage']), manifest)[builder.UNITY_ROOT]
        self.assertEqual(root['guid'], root_guid)
        self.assertEqual(root['asset.meta'], root_meta)

    def test_invalid_source_metadata_and_duplicate_guids_are_rejected(self):
        first = self.package / 'Editor/LauncherBootstrap.cs.meta'
        other = self.package / 'Editor/LauncherServerService.cs.meta'
        other.write_bytes(first.read_bytes())
        with self.assertRaisesRegex(ValueError, 'duplicate GUID'):
            builder.build(self.root / 'duplicate')
        other.write_text('fileFormatVersion: 2\nguid: invalid\n')
        with self.assertRaisesRegex(ValueError, 'GUID'):
            builder.build(self.root / 'invalid')

    def test_hosted_metadata_requires_repository_and_explicit_full_sha(self):
        for repository, revision in (('VRCLearn/MCP-For-Unity-Launcher', None),
                                     (None, 'a1' * 20), ('../bad', 'a1' * 20),
                                     ('VRCLearn/repo', 'main'), ('VRCLearn/repo', '1234567')):
            with self.subTest(repository=repository, revision=revision):
                with self.assertRaises(ValueError):
                    builder.build(self.root / 'invalid', repository=repository, revision=revision)
        with self.assertRaises(ValueError):
            builder.build(self.root / 'http', 'http://example.org/launcher.zip')

    def test_unitypackage_validation_rejects_traversal_and_guid_mismatch(self):
        result = builder.build(self.root / 'output')
        manifest = json.loads((self.root / 'output/package.json').read_text())
        original = Path(result['unitypackage'])
        for modification in ('traversal', 'guid'):
            corrupted = self.root / (modification + '.unitypackage')
            changed = False
            with tarfile.open(original, 'r:gz') as source, tarfile.open(corrupted, 'w:gz') as target:
                for member in source.getmembers():
                    data = source.extractfile(member).read() if member.isfile() else None
                    if not changed and member.isfile() and member.name.endswith('/pathname') and modification == 'traversal':
                        data = b'Assets/MCPForUnityLauncher/../../escape'
                        changed = True
                    elif not changed and member.isfile() and member.name.endswith('/asset.meta') and modification == 'guid':
                        data = data.replace(builder.metadata_guid(data).encode(), ('ff' * 16).encode())
                        changed = True
                    if data is not None:
                        member.size = len(data)
                    target.addfile(member, io.BytesIO(data) if data is not None else None)
            self.assertTrue(changed)
            with self.assertRaises(ValueError):
                builder.validate_unitypackage(corrupted, manifest)


if __name__ == '__main__':
    unittest.main()
