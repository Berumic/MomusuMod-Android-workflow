"""Recompress native libraries before zipalign/signing; verify all payload bytes."""
import argparse
import copy
import hashlib
from pathlib import Path
import re
import shutil
import subprocess
import zipfile


def signature_entry(name):
    upper = name.upper()
    return upper.startswith('META-INF/') and (upper == 'META-INF/MANIFEST.MF' or upper.endswith(('.SF', '.RSA', '.DSA', '.EC')))


def digest(archive, entry):
    h = hashlib.sha256()
    with archive.open(entry) as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(data)
    return h.digest()


def optimize(source, destination, extract_native_libs):
    source, destination = Path(source), Path(destination)
    if destination.exists() or source.resolve() == destination.resolve():
        raise ValueError('Output must be a new file')
    try:
        with zipfile.ZipFile(source) as src:
            entries = src.infolist()
            if len({i.filename for i in entries}) != len(entries):
                raise ValueError('Duplicate ZIP paths are not supported')
            kept = [i for i in entries if not signature_entry(i.filename)]
            with zipfile.ZipFile(destination, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as dst:
                for entry in kept:
                    info = copy.copy(entry)
                    # Old alignment fields are invalid after recompression. zipalign runs next.
                    info.extra = b''
                    is_native = entry.filename.startswith('lib/') and entry.filename.endswith('.so')
                    is_loader = entry.filename.startswith('assets/LemonLoader/') and entry.filename.endswith('.so')
                    if is_native:
                        info.compress_type = zipfile.ZIP_DEFLATED if extract_native_libs else zipfile.ZIP_STORED
                    elif is_loader:
                        info.compress_type = zipfile.ZIP_DEFLATED
                    info._compresslevel = 9
                    with src.open(entry) as inp, dst.open(info, 'w') as out:
                        shutil.copyfileobj(inp, out, 1024 * 1024)
            with zipfile.ZipFile(destination) as dst:
                if set(dst.namelist()) != {i.filename for i in kept}:
                    raise ValueError('APK entry list changed')
                for entry in kept:
                    if digest(src, entry) != digest(dst, entry.filename):
                        raise ValueError('APK payload changed: ' + entry.filename)
        print(f'APK compression: {source.stat().st_size} -> {destination.stat().st_size} bytes; all payload SHA256 checks passed')
    except Exception:
        destination.unlink(missing_ok=True)
        raise


def extraction_enabled(aapt, apk):
    result = subprocess.run([aapt, 'dump', 'xmltree', str(apk), 'AndroidManifest.xml'], check=True, capture_output=True, text=True)
    if 'E: application' not in result.stdout:
        raise ValueError('Cannot read APK application manifest')
    matches = re.findall(r'A: android:extractNativeLibs[^=]*=\(type 0x12\)(0x[0-9a-fA-F]+)', result.stdout)
    if 'android:extractNativeLibs' in result.stdout and len(matches) != 1:
        raise ValueError('Cannot interpret extractNativeLibs')
    # Absent attribute retains Android PackageParser legacy extraction behavior.
    return not matches or int(matches[0], 16) != 0


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--aapt', required=True)
    args = parser.parse_args()
    enabled = extraction_enabled(args.aapt, args.input)
    print('Native library extraction enabled:', enabled)
    optimize(args.input, args.output, enabled)
