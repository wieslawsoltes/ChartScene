"""Real Uno input tests for stack caching, runtime modes and atomic batch mutations."""
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
parser.add_argument('--output', default='artifacts/browser-tests-stacks')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
    page = browser.new_page(viewport={'width': 1440, 'height': 1000})
    page.set_default_timeout(20_000)
    page.on('pageerror', lambda e: logs.append({'type': 'pageerror', 'text': str(e)}))
    page.on('console', lambda e: logs.append({'type': e.type, 'text': e.text}))
    input_driver = UnoInput(page, out)
    def state(): return page.evaluate('window.chartSceneStacks || {}')
    def ready():
        page.wait_for_function('''() => {const s=window.chartSceneStacks;if(s?.error)throw new Error(s.error);return s?.ready;}''', timeout=120_000)
    def click(name):
        b = state()['controls'][name]; old = state()['frames']
        input_driver.click('stacks-'+name,b,resolve=lambda: state()['controls'][name])
        page.wait_for_function('old => window.chartSceneStacks.frames>old && window.chartSceneStacks.ready', arg=old)
    def point(x=.5, y=.65):
        b = state()['plot']; return (b['x']+b['width']*x, b['y']+b['height']*y)
    def test(name, action):
        started = time.perf_counter()
        try:
            action(); assert not state().get('error'), state().get('error')
            results.append({'name': name, 'passed': True, 'milliseconds': (time.perf_counter()-started)*1000}); print('PASS', name, flush=True)
        except Exception as error:
            results.append({'name': name, 'passed': False, 'error': str(error), 'trace': traceback.format_exc()}); print('FAIL', name, error, flush=True)
            page.screenshot(path=str(out/(name.replace(' ', '-')+'-failure.png')))
            (out/'failure-state.json').write_text(json.dumps(state(), indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total': len(results), 'failed': sum(not r['passed'] for r in results), 'tests': results}, indent=2))
            (out/'console.json').write_text(json.dumps(logs, indent=2))
    try:
        page.goto(args.url+'?view=stacks&inputTrace=1', wait_until='domcontentloaded')
        test('stacked data startup', ready)
        if not state().get('ready'): raise RuntimeError('Stacked data page did not start')
        def pixels():
            samples=[]; consecutive=0; start=time.perf_counter()
            while True:
                path=out/'stacked-data.png'; page.screenshot(path=str(path)); b=state()['plot']
                with Image.open(path) as image:
                    crop=image.convert('RGB').crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
                    count=sum(max(c)-min(c)>45 and max(c)>100 for c in crop.resize((300,180)).getdata())
                samples.append({'elapsedMs':(time.perf_counter()-start)*1000,'coloredPixels':count})
                consecutive=consecutive+1 if count>500 else 0
                if consecutive>=2 or time.perf_counter()-start>8: break
                page.wait_for_timeout(100)
            (out/'presentation.json').write_text(json.dumps(samples,indent=2))
            assert consecutive>=2, samples
        test('stacked data visible geometry', pixels)
        def chrome_pixels(theme):
            # Model readiness and a chart callback precede native control-state presentation.
            # Preserve every contrast threshold, and observe two passing screenshots without
            # repeating a click, disabling animations, modifying styles or forcing a redraw.
            path=out/('stacked-chrome-'+theme.lower()+'.png')
            observations=[]; consecutive=0; started=time.perf_counter()
            while True:
                current=state()
                assert current['theme']==theme and not current.get('error'),current
                page.screenshot(path=str(path))
                readings={}; failures=[]
                with Image.open(path) as image:
                    gray=image.convert('L')
                    for name in ['mode','edit','replace','visibility','theme','fit','status']:
                        b=current['controls'][name]
                        crop=gray.crop((int(b['x']+4),int(b['y']+3),min(gray.width,int(b['x']+b['width']-4)),min(gray.height,int(b['y']+b['height']-3))))
                        lo,hi=crop.getextrema()
                        readings[name]={'min':lo,'max':hi}
                        if not (hi-lo>60 and (lo<80 and hi>100 if theme=='Dark' else lo<180 and hi>220)):
                            failures.append(name)
                observations.append({'elapsedMs':(time.perf_counter()-started)*1000,'controls':readings,'failing':failures})
                consecutive=consecutive+1 if not failures else 0
                if consecutive>=2 or time.perf_counter()-started>8: break
                page.wait_for_timeout(100)
            evidence=out/('stacked-chrome-'+theme.lower()+'-observations.json')
            prior=json.loads(evidence.read_text()) if evidence.exists() else []
            prior.append(observations);evidence.write_text(json.dumps(prior,indent=2))
            assert consecutive>=2,(theme,observations)
        test('stacked dark toolbar and status remain visible', lambda: chrome_pixels('Dark'))
        def rollover():
            before = state()
            page.mouse.move(*point(.2)); page.mouse.move(*point(.7), steps=15)
            page.wait_for_function('n=>window.chartSceneStacks.frames>n && window.chartSceneStacks.hits===3', arg=before['frames'])
            assert state()['stackBuild'] == before['stackBuild']
            assert state()['revisions'] == before['revisions']
            assert state()['sceneBuilds'] == before['sceneBuilds']
            assert state()['sceneReplays'] > before['sceneReplays']
        test('rollover reuses stacked generation', rollover)
        def zoom():
            before = state(); page.mouse.move(*point(.4)); page.mouse.wheel(0, -240)
            page.wait_for_function('span=>window.chartSceneStacks.max-window.chartSceneStacks.min<span', arg=before['max']-before['min'])
            ready(); assert state()['stackBuild'] == before['stackBuild']; click('fit')
            assert state()['stackBuild'] == before['stackBuild']
        test('zoom and fit reuse stack buffers', zoom)
        def mode():
            before = state(); click('mode'); after = state()
            assert after['mode'] == 'Percent' and after['stackBuild'] == before['stackBuild']+1
            assert after['batchNotifications'] == 1 and after['revisions'] == before['revisions']
            assert all(abs(v-100)<1e-8 for v in after['top'])
            pixels()
            page.screenshot(path=str(out/'percent-stacks.png'))
        test('runtime percent mode redraws and batches notifications', mode)
        def edit():
            before = state(); click('edit'); after = state()
            assert after['edits'] == before['edits']+1 and after['batchNotifications'] == 1
            assert after['stackBuild'] == before['stackBuild']+1
            assert after['revisions'] == [v+1 for v in before['revisions']]
            assert after['sourcePoints'] == 15000 and all(abs(v-100)<1e-8 for v in after['top'])
        test('atomic Y range edits rebuild one generation', edit)
        def replace():
            before = state(); click('replace'); after = state()
            assert after['generation'] == before['generation']+1 and after['batchNotifications'] == 1
            assert after['sourcePoints'] == 12000 and after['stackBuild'] == before['stackBuild']+1
            assert after['revisions'] == [v+1 for v in before['revisions']]
        test('whole data replacement remains coherent', replace)
        def visibility():
            before = state(); click('visibility'); after = state()
            assert not after['middleVisible'] and after['sourcePoints'] == 8000
            assert after['stackBuild'] == before['stackBuild']+1 and after['revisions'] == before['revisions']
            assert all(abs(v-100)<1e-8 for v in after['top'])
            click('visibility'); assert state()['sourcePoints'] == 12000
        test('series visibility invalidates cumulative values', visibility)
        def theme():
            before = state(); click('theme'); assert state()['theme'] == 'Light'
            assert state()['stackBuild'] == before['stackBuild'] and state()['revisions'] == before['revisions']
            chrome_pixels('Light')
            page.screenshot(path=str(out/'stacked-light.png')); click('theme'); chrome_pixels('Dark')
        test('theme redraw preserves stack generation', theme)
        def absolute():
            before = state(); click('mode'); after = state()
            assert after['mode'] == 'Absolute' and after['stackBuild'] == before['stackBuild']+1
            assert all(0<v<20 for v in after['top'])
        test('absolute mode restores source-scale cumulative values', absolute)
        def reattach():
            before = state()
            for name in ['gallery-2d', 'gallery-stacks']:
                b = page.evaluate('n=>window.chartSceneGallery.controls[n]', name)
                page.mouse.click(b['x']+b['width']/2,b['y']+b['height']/2)
                page.wait_for_function('v=>window.chartSceneGallery.view===v', arg='2d' if name=='gallery-2d' else 'stacks')
            page.wait_for_function('n=>window.chartSceneStacks.frames>n && window.chartSceneStacks.ready', arg=before['frames'])
            assert state()['stackBuild'] == before['stackBuild'] and state()['revisions'] == before['revisions']
        test('stacked view detach and reattach', reattach)
        def clean(): assert not [e for e in logs if e['type']=='pageerror'], logs
        test('stacked data has no unhandled errors', clean)
        (out/'final-state.json').write_text(json.dumps(state(), indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs, indent=2)); browser.close()
failed = sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} stacked browser tests passed', flush=True)
raise SystemExit(1 if failed else 0)
