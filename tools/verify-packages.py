"""Compile the actual application outside the repository using only packed ChartScene libraries.

Source mapping and a fresh package cache prevent fallback to project references or older packages.
The desktop and WebAssembly targets must both restore ChartScene dependencies as package assets.
"""
from __future__ import annotations
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
FEED = ROOT / 'artifacts/packages'
REPORT = ROOT / 'artifacts/package-consumer'
EXPECTED = {
    'ChartScene.' + name for name in (
        'Core', 'Data', 'Numerics', 'Charting', 'Rendering', 'Rendering.Skia', 'Interaction',
        'ThreeD', 'Rendering.OpenGL', 'Controls', 'Samples', 'Samples3D')
}


def main() -> None:
    REPORT.mkdir(parents=True, exist_ok=True)
    packages: dict[str, str] = {}
    for file in sorted(FEED.glob('*.nupkg')):
        with zipfile.ZipFile(file) as archive:
            specs = [path for path in archive.namelist() if path.endswith('.nuspec')]
            if len(specs) != 1:
                raise RuntimeError(f'{file.name}: expected one NuGet specification')
            root = ET.fromstring(archive.read(specs[0]))
            metadata = next(node for node in root if node.tag.split('}')[-1] == 'metadata')
            fields = {node.tag.split('}')[-1]: node.text for node in metadata}
            identifier, version = fields['id'], fields['version']
            if not identifier or not version or identifier in packages:
                raise RuntimeError(f'{file.name}: invalid or duplicate package identity')
            if not any(path.endswith(identifier + '.dll') for path in archive.namelist()):
                raise RuntimeError(f'{file.name}: no compiled library assembly')
            packages[identifier] = version
    if set(packages) != EXPECTED:
        raise RuntimeError(f'Package set mismatch: expected {sorted(EXPECTED)}, got {sorted(packages)}')

    builds = []
    with tempfile.TemporaryDirectory(prefix='chartscene-package-consumer-') as directory:
        consumer = Path(directory)
        for name in ('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'README.md', 'LICENSE'):
            source = ROOT / name
            if source.is_file(): shutil.copy2(source, consumer / name)
        application = consumer / 'src/ChartScene.App'
        shutil.copytree(ROOT / 'src/ChartScene.App', application,
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        project = application / 'ChartScene.App.csproj'
        tree = ET.parse(project)
        replaced = []
        for node in tree.getroot().iter('ProjectReference'):
            identifier = Path(node.attrib['Include']).stem
            if identifier not in packages:
                raise RuntimeError(f'Unpacked project dependency: {identifier}')
            node.tag = 'PackageReference'
            node.attrib.clear()
            node.set('Include', identifier); node.set('Version', packages[identifier])
            replaced.append(identifier)
        if not replaced or any(True for _ in tree.getroot().iter('ProjectReference')):
            raise RuntimeError('Package-only consumer still has project references')
        tree.write(project, encoding='unicode')

        configuration = ET.Element('configuration')
        sources = ET.SubElement(configuration, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='chartscene-local', value=str(FEED))
        ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
        mappings = ET.SubElement(configuration, 'packageSourceMapping')
        local = ET.SubElement(mappings, 'packageSource', key='chartscene-local')
        ET.SubElement(local, 'package', pattern='ChartScene.*')
        public = ET.SubElement(mappings, 'packageSource', key='nuget.org')
        ET.SubElement(public, 'package', pattern='*')
        ET.ElementTree(configuration).write(consumer / 'NuGet.Config', encoding='unicode')
        env = os.environ.copy()
        env['NUGET_PACKAGES'] = str(consumer / 'isolated-package-cache')
        env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        for target in ('net10.0-desktop', 'net10.0-browserwasm'):
            command = ['dotnet', 'build', str(project), '-c', 'Release', '-f', target]
            print('Compiling package-only consumer:', target, flush=True)
            result = subprocess.run(command, cwd=consumer, env=env, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=900)
            (REPORT / (target + '.log')).write_text(result.stdout)
            print(result.stdout, flush=True)
            if result.returncode:
                raise RuntimeError(f'Package-only {target} build failed')
            assets = json.loads((application / 'obj/project.assets.json').read_text())
            resolved = {
                key.split('/')[0]: value['type'] for key, value in assets['libraries'].items()
                if key.split('/')[0].startswith('ChartScene.')
            }
            if set(resolved) != EXPECTED or any(kind != 'package' for kind in resolved.values()):
                raise RuntimeError(f'{target}: ChartScene dependencies were not resolved entirely from NuGet: {resolved}')
            builds.append({'target': target, 'passed': True, 'resolvedPackages': sorted(resolved)})
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    (REPORT / 'report.json').write_text(json.dumps({'commit': commit, 'packages': packages, 'builds': builds}, indent=2))
    print(f'PASS: {len(packages)} packages consumed by both Uno targets without project references', flush=True)


if __name__ == '__main__':
    main()
