import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('apk_compression', Path(__file__).parents[1] / 'Optimize-Apk.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CompressionTests(unittest.TestCase):
    def test_both_native_install_modes_preserve_bytes(self):
        for extraction in (True, False):
            with self.subTest(extraction=extraction), tempfile.TemporaryDirectory() as tmp:
                src, dst = Path(tmp) / 'in.apk', Path(tmp) / 'out.apk'
                files = {'AndroidManifest.xml': b'manifest', 'lib/arm64-v8a/game.so': b'library' * 10000,
                         'assets/LemonLoader/runtime/runtime.so': b'runtime' * 10000,
                         'resources.arsc': b'resources', 'META-INF/services/keep': b'keep'}
                with zipfile.ZipFile(src, 'w') as z:
                    for name, data in files.items():
                        z.writestr(name, data)
                    z.writestr('META-INF/CERT.RSA', b'old signature')
                module.optimize(src, dst, extraction)
                with zipfile.ZipFile(dst) as z:
                    self.assertEqual(set(z.namelist()), set(files))
                    for name, data in files.items():
                        self.assertEqual(z.read(name), data)
                    self.assertEqual(z.getinfo('lib/arm64-v8a/game.so').compress_type, 8 if extraction else 0)
                    self.assertEqual(z.getinfo('assets/LemonLoader/runtime/runtime.so').compress_type, 8)
                    self.assertEqual(z.getinfo('resources.arsc').compress_type, 0)
                self.assertLess(dst.stat().st_size, src.stat().st_size)

    def test_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            dst = Path(tmp) / 'existing.apk'
            dst.write_bytes(b'preserve')
            with self.assertRaises(ValueError):
                module.optimize(Path(tmp) / 'missing.apk', dst, True)
            self.assertEqual(dst.read_bytes(), b'preserve')
