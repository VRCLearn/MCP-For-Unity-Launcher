import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('renderer_source', ROOT / 'tools/prepare_vpm_source.py')
renderer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(renderer)
sys.path.pop(0)


class RendererSourceTests(unittest.TestCase):
    def test_renderer_receives_only_release_urls_for_configured_package_ids(self):
        source_path = ROOT / '.github/vpm/source.json'
        original = json.loads(source_path.read_text())
        assets = {name: [{'url': 'https://example.test/' + name + '.zip'}] for name in original['packages']}
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'source.json'
            with patch.object(renderer, 'release_assets', side_effect=lambda repo, name, token: assets[name]) as releases:
                renderer.prepare(source_path, output, 'api-token')
            prepared = json.loads(output.read_text())
        self.assertNotIn('githubRepos', prepared)
        self.assertEqual(prepared['packages'], [{'id': name, 'releases': [asset['url'] for asset in assets[name]]}
                                               for name in original['packages']])
        self.assertEqual(releases.call_args_list[0].args,
                         (original['repository'], 'com.vrclearn.mcp-for-unity-launcher', 'api-token'))
        self.assertEqual({key: value for key, value in prepared.items() if key != 'packages'},
                         {key: value for key, value in original.items() if key not in ('packages', 'githubRepos')})
        self.assertEqual(json.loads(source_path.read_text()), original)

    def test_missing_required_release_does_not_write_renderer_configuration(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'source.json'
            with patch.object(renderer, 'release_assets', return_value=[]):
                with self.assertRaisesRegex(ValueError, 'No stable'):
                    renderer.prepare(ROOT / '.github/vpm/source.json', output)
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
