"""Interact with the actual Uno linked-chart page; diagnostics are read-only observations."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright
from uno_input import UnoInput

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-linked')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(args=['--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
    context = browser.new_context(viewport={'width': 1440, 'height': 1000})
    page = context.new_page(); page.set_default_timeout(20_000)
    page.on('pageerror', lambda e: logs.append({'type': 'pageerror', 'text': str(e)}))
    page.on('console', lambda e: logs.append({'type': e.type, 'text': e.text}))
    input_driver = UnoInput(page, out)
    def state(): return page.evaluate('window.chartSceneLinked || {}')
    def ready():
        page.wait_for_function('''() => { const s=window.chartSceneLinked; if(s?.error) throw new Error(s.error); return s?.ready; }''', timeout=120_000)
    def click(name):
        b = state()['controls'][name]
        input_driver.click('linked-'+name,b)
    def move(view, x=.4, y=.6):
        b = state()[view]['plot']
        page.mouse.move(b['x']+b['width']*x, b['y']+b['height']*y)
    def linked_cursors():
        page.wait_for_function('''() => {const s=window.chartSceneLinked; return s.upper.cursorX!==null && s.lower.cursorX!==null && Math.abs(s.upper.cursorX-s.lower.cursorX/1000)<1e-8;}''')
    def linked_ranges():
        page.wait_for_function('''() => {const s=window.chartSceneLinked; return Math.abs(s.upper.min-s.lower.min/1000)<1e-8 && Math.abs(s.upper.max-s.lower.max/1000)<1e-8;}''')
    def test(name, action):
        start = time.perf_counter()
        try:
            action(); assert not state().get('error'), state().get('error')
            results.append({'name':name, 'passed':True, 'milliseconds':(time.perf_counter()-start)*1000})
            print('PASS', name, flush=True)
        except Exception as error:
            results.append({'name':name, 'passed':False, 'error':str(error), 'trace':traceback.format_exc()})
            print('FAIL', name, str(error), flush=True)
            page.screenshot(path=str(out/(name.replace(' ', '-')+'-failure.png')))
            (out/'failure-state.json').write_text(json.dumps(state(), indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results), 'failed':sum(not r['passed'] for r in results), 'tests':results}, indent=2))
            (out/'console.json').write_text(json.dumps(logs, indent=2))
    try:
        page.goto(args.url+'?view=linked&inputTrace=1', wait_until='domcontentloaded')
        test('linked page startup', ready)
        if not state().get('ready'): raise RuntimeError('Linked page failed to start')
        def cursors():
            before = state()
            move('upper', .35); linked_cursors()
            assert state()['upper']['hits'] and state()['lower']['hits']
            move('lower', .73); linked_cursors()
            assert state()['upper']['dataRevision'] == before['upper']['dataRevision']
            assert state()['lower']['dataRevision'] == before['lower']['dataRevision']
        test('linked cursor unit conversion and handoff', cursors)
        def zoom():
            before = state()['upper']; move('lower'); page.mouse.wheel(0, -240)
            page.wait_for_function('old=>window.chartSceneLinked.upper.max-window.chartSceneLinked.upper.min<old', arg=before['max']-before['min'])
            linked_ranges()
        test('linked wheel zoom in canonical units', zoom)
        def cursor_toggle():
            click('cursors'); page.wait_for_function('window.chartSceneLinked.cursorsLinked===false')
            move('upper', .6); page.wait_for_timeout(250)
            assert state()['upper']['cursorX'] is not None and state()['lower']['cursorX'] is None
            click('cursors'); page.wait_for_function('window.chartSceneLinked.cursorsLinked===true')
            move('upper', .45); linked_cursors()
        test('linked cursor toggle restores independent views', cursor_toggle)
        def range_toggle():
            click('fit'); click('ranges'); page.wait_for_function('window.chartSceneLinked.rangesLinked===false')
            before = state()['lower']; initial = state()['upper']; move('upper'); page.mouse.wheel(0, -240)
            page.wait_for_function('old=>window.chartSceneLinked.upper.max-window.chartSceneLinked.upper.min<old', arg=initial['max']-initial['min'])
            assert state()['lower']['min'] == before['min'] and state()['lower']['max'] == before['max']
            click('ranges'); page.wait_for_function('window.chartSceneLinked.rangesLinked===true'); linked_ranges()
        test('linked range toggle isolates and rejoins viewports', range_toggle)
        def replace():
            generation = state()['generation']; click('replace')
            page.wait_for_function('n=>window.chartSceneLinked.generation===n+1 && window.chartSceneLinked.ready', arg=generation)
            linked_ranges(); move('upper', .7); linked_cursors()
        test('linked lower model replacement rebinds subscriptions', replace)
        def exit():
            move('upper'); linked_cursors(); page.mouse.move(1400, 20)
            page.wait_for_function('window.chartSceneLinked.upper.cursorX===null && window.chartSceneLinked.lower.cursorX===null')
        test('linked cursor exit clears both overlays', exit)
        def render():
            click('fit'); move('upper', .45)
            path = out/'linked-charts.png'; page.screenshot(path=str(path)); image = Image.open(path).convert('RGB')
            for name in ['upper', 'lower']:
                b = state()[name]['plot']
                crop = image.crop((int(b['x']), int(b['y']), int(b['x']+b['width']), int(b['y']+b['height'])))
                count = sum(max(c)-min(c)>50 and max(c)>110 for c in crop.getdata())
                assert count > 100, (name, count)
        test('linked chart geometry is visible', render)
        def resize():
            page.set_viewport_size({'width':1000, 'height':800}); page.wait_for_function('window.chartSceneLinked.controls.upper.width===1000')
            ready(); move('lower', .3); linked_cursors()
            page.screenshot(path=str(out/'linked-compact.png'))
            page.set_viewport_size({'width':1440, 'height':1000}); page.wait_for_function('window.chartSceneLinked.controls.upper.width===1440')
            move('upper', .6); linked_cursors()
        test('linked chart resize reprojection', resize)
        def reattach():
            old = state()['upper']['frames']
            for name in ['gallery-2d', 'gallery-linked']:
                b = page.evaluate('name=>window.chartSceneGallery.controls[name]', name)
                page.mouse.click(b['x']+b['width']/2, b['y']+b['height']/2); page.wait_for_timeout(500)
            ready(); page.wait_for_function('old=>window.chartSceneLinked.upper.frames>old', arg=old)
            move('lower', .65); linked_cursors(); linked_ranges()
        test('linked group detaches and reattaches', reattach)
        def errors(): assert not [entry for entry in logs if entry['type']=='pageerror'], logs
        test('linked page has no unhandled errors', errors)
        (out/'final-state.json').write_text(json.dumps(state(), indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs, indent=2)); browser.close()
failed = sum(not test['passed'] for test in results)
print(f'{len(results)-failed}/{len(results)} linked browser tests passed', flush=True)
raise SystemExit(1 if failed else 0)
