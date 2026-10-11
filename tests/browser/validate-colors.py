"""Validate the actual Uno heatmap and standalone legend controls through real UI input."""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-colors')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(args=['--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
    context = browser.new_context(viewport={'width': 1440, 'height': 1000}, device_scale_factor=1)
    page = context.new_page(); page.set_default_timeout(20_000)
    page.on('pageerror', lambda e: logs.append({'type': 'pageerror', 'text': str(e)}))
    page.on('console', lambda e: logs.append({'type': e.type, 'text': e.text}))
    def state(): return page.evaluate('window.chartSceneColors || {}')
    def wait(condition='true', arg=None):
        page.wait_for_function('arg=>{const s=window.chartSceneColors;if(s?.error)throw new Error(s.error);return s?.ready && ('+condition+');}', arg=arg, timeout=120_000)
    def click(name):
        b = state()['controls'][name]
        page.mouse.click(b['x'] + b['width']/2, b['y'] + b['height']/2)
    def text(name, value):
        click(name); page.keyboard.press('Control+A'); page.keyboard.insert_text(value)
    def fixed(minimum='1', maximum='1000'):
        text('minimum', minimum); text('maximum', maximum); click('apply')
        wait('!s.automatic && s.minimum===arg[0] && s.maximum===arg[1] && !s.validationError', [float(minimum), float(maximum)])
    def assert_linked():
        s = state()
        for name in ['vertical', 'horizontal']:
            legend = s[name]
            assert legend['minimum'] == s['minimum'] and legend['maximum'] == s['maximum'], s
            assert legend['scale'] == s['scale'] and len(legend['tickLabels']) >= 2, s
            assert legend['orientation'].lower() == name and legend['frames'] > 0, s
    def hover(name, fraction=.4):
        legend = state()[name]; b = legend['bar']
        visual = 1-fraction if legend['flipped'] else fraction
        x = int(b['x'] + b['width']*(visual if name=='horizontal' else .5))
        y = int(b['y'] + b['height']*((1-visual) if name=='vertical' else .5))
        actual_fraction = (x-b['x'])/b['width'] if name=='horizontal' else (b['y']+b['height']-y)/b['height']
        if legend['flipped']: actual_fraction = 1-actual_fraction
        lo, hi = legend['minimum'], legend['maximum']
        expected = lo+(hi-lo)*actual_fraction if legend['scale']=='Linear' else math.exp(math.log(lo)+(math.log(hi)-math.log(lo))*actual_fraction)
        page.mouse.move(x, y)
        wait('s[arg.name].hovered!==null && Math.abs(s[arg.name].hovered-arg.expected)<Math.max(1,Math.abs(arg.expected))*1e-6', {'name': name, 'expected': expected})
    def pixels(name):
        page.mouse.move(2, 2)
        wait('s.vertical.hovered===null && s.horizontal.hovered===null')
        path = out/(name+'.png'); deadline = time.monotonic()+8
        errors = []
        while True:
            page.screenshot(path=str(path)); s = state(); errors = []
            with Image.open(path) as image:
                image = image.convert('RGB')
                for orientation in ['vertical', 'horizontal']:
                    legend = s[orientation]; b = legend['bar']; expected = tuple(bytes.fromhex(legend['midColor'][1:]))
                    actual = image.getpixel((int(b['x']+b['width']/2), int(b['y']+b['height']/2)))
                    if max(abs(a-e) for a,e in zip(actual, expected)) > 5: errors.append((orientation, actual, expected))
            if not errors or time.monotonic() >= deadline: break
            page.wait_for_timeout(100)
        assert not errors, errors
    def test(name, action):
        start = time.perf_counter()
        try:
            action(); assert not state().get('error'), state()
            results.append({'name': name, 'passed': True, 'milliseconds': (time.perf_counter()-start)*1000})
            print('PASS', name, flush=True)
        except Exception as error:
            results.append({'name': name, 'passed': False, 'error': str(error), 'trace': traceback.format_exc()})
            print('FAIL', name, str(error), flush=True)
            page.screenshot(path=str(out/(name.replace(' ', '-')+'-failure.png')))
            (out/'failure-state.json').write_text(json.dumps(state(), indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total': len(results), 'failed': sum(not r['passed'] for r in results), 'tests': results}, indent=2))
            (out/'console.json').write_text(json.dumps(logs, indent=2))
    try:
        page.goto(args.url+'?view=colors', wait_until='domcontentloaded')
        test('color legends startup', wait)
        if not state().get('ready'): raise RuntimeError('Color legend controls did not become ready')
        test('both legends share automatic data extents', assert_linked)
        test('legend gradient pixels match source palette', lambda: pixels('color-legends'))
        def linear_hover():
            hover('vertical', .32); hover('horizontal', .68)
        test('linear legend pointer value inspection', linear_hover)
        def range_edit():
            fixed(); assert_linked(); hover('horizontal', .23)
        test('manual color range updates both legends', range_edit)
        def logarithmic():
            click('scale'); wait("s.scale==='Logarithmic'"); assert_linked()
            hover('vertical', .5); hover('horizontal', .75); pixels('color-logarithmic')
        test('logarithmic color mapping and hover', logarithmic)
        def flip():
            click('flip'); wait('s.vertical?.flipped===true && s.horizontal?.flipped===true')
            hover('vertical', .3); hover('horizontal', .8); pixels('color-flipped')
            click('flip'); wait('s.vertical?.flipped===false && s.horizontal?.flipped===false')
        test('flipped color legends preserve data mapping', flip)
        def palette():
            for name in ['Thermal', 'Diverging', 'Viridis']:
                click('palette'); wait('s.palette===arg', name); pixels('color-'+name.lower())
        test('palette changes update both rendered gradients', palette)
        def invalid():
            before = state(); bounds = [before['minimum'], before['maximum']]
            for low, high in [('10', '1'), ('NaN', '1000'), ('0', '1000')]:
                text('minimum', low); text('maximum', high); click('apply'); wait('!!s.validationError')
                assert [state()['minimum'], state()['maximum']] == bounds, state()
            fixed(); assert_linked()
        test('invalid manual ranges preserve last valid mapping', invalid)
        def automatic():
            click('auto'); wait('s.automatic'); before = state(); click('data')
            wait('s.generation===arg.generation+1 && s.maximum>arg.maximum', before); assert_linked()
        test('automatic color extents follow replaced values', automatic)
        def nonuniform():
            before = state(); click('grid'); wait('s.nonUniform===true')
            assert state()['minimum']==before['minimum'] and state()['maximum']==before['maximum']
            pixels('color-nonuniform'); assert_linked()
            click('grid'); wait('s.nonUniform===false')
        test('uniform and nonuniform heatmaps share color mapping', nonuniform)
        def replace():
            fixed(); old = state()['seriesId']; click('series'); wait('s.seriesId!==arg', old)
            generation = state()['generation']; click('data'); wait('s.generation===arg+1', generation)
            assert state()['minimum']==1 and state()['maximum']==1000
            click('palette'); wait("s.palette==='Thermal'"); assert_linked(); pixels('color-rebound')
        test('legend source replacement rebinds observation', replace)
        def opacity():
            before = state(); click('opacity'); wait('s.opacity===0.45 && s.vertical.frames>arg.vertical.frames && s.horizontal.frames>arg.horizontal.frames', before)
            page.mouse.move(2, 2); page.screenshot(path=str(out/'color-alpha.png'))
            click('opacity'); wait('s.opacity===1'); pixels('color-opaque')
        test('legend opacity changes invalidate render cache', opacity)
        def labels():
            click('labels'); wait('s.vertical?.labels===false && s.horizontal?.labels===false')
            assert not state()['vertical']['tickLabels'] and not state()['horizontal']['tickLabels']
            click('labels'); wait('s.vertical?.labels===true && s.horizontal?.labels===true')
            click('format'); wait("s.vertical.tickLabels.includes('1.0') && s.horizontal.tickLabels.includes('1000.0')")
            assert_linked()
        test('legend label visibility and formatting', labels)
        def theme():
            click('theme'); wait("s.theme==='Light'"); pixels('color-light')
            click('theme'); wait("s.theme==='Dark'")
        test('color legend theme changes', theme)
        def resize():
            page.set_viewport_size({'width': 900, 'height': 700}); wait('s.controls.horizontal?.width===900 && s.horizontal.bar.width<900')
            hover('horizontal', .4); hover('vertical', .6); pixels('color-compact')
            page.set_viewport_size({'width': 1440, 'height': 1000}); wait('s.controls.horizontal?.width===1440')
        test('color legend responsive layout and hit projection', resize)
        def reattach():
            before = state()
            for name, view in [('gallery-2d', '2d'), ('gallery-colors', 'colors')]:
                b = page.evaluate('name=>window.chartSceneGallery.controls[name]', name)
                page.mouse.click(b['x']+b['width']/2, b['y']+b['height']/2)
                page.wait_for_function('view=>window.chartSceneGallery.view===view', arg=view)
            wait('s.vertical.frames>arg.vertical.frames && s.horizontal.frames>arg.horizontal.frames', before)
            assert state()['seriesId']==before['seriesId']; hover('vertical'); assert_linked(); pixels('color-reattached')
        test('color legend unload and reload restores rendering', reattach)
        def clean(): assert not [e for e in logs if e['type']=='pageerror'], logs
        test('color legends no unhandled errors', clean)
        (out/'final-state.json').write_text(json.dumps(state(), indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs, indent=2)); browser.close()
failed = sum(not test['passed'] for test in results)
print(f'{len(results)-failed}/{len(results)} color legend browser tests passed', flush=True)
raise SystemExit(1 if failed else 0)
