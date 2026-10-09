"""Stage the actual Uno publish output, preserving relative URLs for repository Pages hosting."""
from pathlib import Path
import json
import shutil
import subprocess

root = Path('artifacts/publish')
candidates = list(root.rglob('index.html'))
if not candidates:
    raise SystemExit('The Uno publish output contains no index.html')
# The generated browser entry point references Uno; reject unrelated dependency documents.
entries = [p for p in candidates if 'uno' in p.read_text(errors='replace').lower() or '_framework' in p.read_text(errors='replace')]
if not entries:
    raise SystemExit(f'No recognizable Uno entry point among {candidates}')
entry = min(entries, key=lambda p: len(p.parts))
site = Path('artifacts/site')
if site.exists():
    shutil.rmtree(site)
shutil.copytree(entry.parent, site)
(site / '.nojekyll').write_text('')
index = site / 'index.html'
html = index.read_text()
if 'chartscene.js' not in html:
    html = html.replace('</head>', '<script src="./chartscene.js"></script>\n</head>')
index.write_text(html)
shutil.copyfile('src/ChartScene.App/chartscene.js', site / 'chartscene.js')
commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
(site / 'build-info.json').write_text(json.dumps({'commit': commit, 'application': 'ChartScene', 'framework': 'Uno Platform', 'entryPoint': str(entry)}, indent=2))
print(f'Staged {entry.parent} at {site} ({sum(1 for p in site.rglob("*") if p.is_file())} files)')
