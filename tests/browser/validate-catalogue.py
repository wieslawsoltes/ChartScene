"""Drive the real Uno thumbnail catalogue. No writes to app diagnostics or synthetic chart data."""
from __future__ import annotations
import argparse
from io import BytesIO
import json
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright
from workbench_input import WorkbenchInput

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-catalogue')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle',
        '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
    page = browser.new_page(viewport={'width': 1440, 'height': 1000})
    page.set_default_timeout(25000)
    page.on('pageerror', lambda e: logs.append({'type':'pageerror','text':str(e)}))
    page.on('console', lambda e: logs.append({'type':e.type,'text':e.text}))
    inputs = WorkbenchInput(page, out)
    def state(): return page.evaluate('window.chartSceneCatalogue || {}')
    def main_state(): return inputs.state()
    def click(name):
        inputs.click(name)
    def preview(identifier):
        return next(p for p in state()['previews'] if p['id'] == identifier)
    def top():
        page.mouse.move(900, 400); page.mouse.wheel(0, -5000)
        page.wait_for_timeout(250)
    def visible(identifier):
        for _ in range(15):
            item = preview(identifier); b = item['bounds']
            if b and b['y'] >= 132 and b['y'] + b['height'] <= page.viewport_size['height'] - 34:
                page.wait_for_function('id=>window.chartSceneCatalogue.previews.some(p=>p.id===id&&p.ready&&!p.error)',arg=identifier)
                return preview(identifier)['bounds']
            page.mouse.move(min(900, page.viewport_size['width'] - 80), page.viewport_size['height'] / 2)
            page.mouse.wheel(0, 220 if b is None or b['y'] > 132 else -220)
            page.wait_for_timeout(150)
        raise AssertionError(('Preview did not become visible', identifier, preview(identifier)))
    def set_page(number):
        top()
        while state()['page'] != number:
            before = state()['page']
            click('catalogue-next' if number > before else 'catalogue-previous')
            page.wait_for_function('old=>window.chartSceneCatalogue.page!==old', arg=before)
        top()
    def pixels(identifier):
        b = visible(identifier); deadline = time.monotonic() + 8; consecutive = 0
        while time.monotonic() < deadline:
            with Image.open(BytesIO(page.screenshot())) as shot:
                crop = shot.convert('RGB').crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
                count = sum(max(c)-min(c)>45 and max(c)>100 for c in crop.resize((160,90)).getdata())
            consecutive = consecutive + 1 if count > 5 else 0
            if consecutive >= 2:
                if identifier in ['overview','financial','heatmap','polarline','ternaryscatter']: crop.save(out/(identifier+'.png'))
                return
            page.wait_for_timeout(100)
        raise AssertionError(('Real preview pixels missing', identifier, count, preview(identifier)))
    def test(name, action):
        start = time.perf_counter()
        try:
            action()
            assert not main_state().get('error'), main_state().get('error')
            assert not any(p['error'] for p in state().get('previews', [])), state()
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-start)*1000})
            print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()})
            print('FAIL',name,error,flush=True)
            page.screenshot(path=str(out/(name.replace(' ','-')+'-failure.png')))
            (out/'failure-state.json').write_text(json.dumps(state(),indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not t['passed'] for t in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?inputTrace=1',wait_until='domcontentloaded')
        page.wait_for_function('window.chartScene?.ready&&window.chartSceneCatalogue',timeout=120000)
        page.wait_for_function("!document.querySelector('#loading.uno-persistent-loader')")
        def lazy():
            assert not state()['populated'] and state()['builds'] == 0 and state()['entries'] == 0, state()
            assert state()['previews'] == []
        test('closed catalogue creates no preview models',lazy)
        click('browser-home')
        page.wait_for_function("window.chartSceneBrowser.document==='Catalogue'&&window.chartSceneCatalogue.previews.length===12")
        original_loads = page.evaluate('window.chartSceneBrowser.modelLoads')
        catalog = main_state()['catalog']
        for number in range(state()['pages']):
            set_page(number)
            expected = [e['Id'] for e in catalog[number*12:(number+1)*12]]
            def members(expected=expected):
                assert [p['id'] for p in state()['previews']] == expected
                assert len(expected) <= 12
            test('catalogue page '+str(number+1)+' membership',members)
            for identifier in expected:
                test('actual thumbnail '+identifier,lambda identifier=identifier:pixels(identifier))
            top()
            if number in [0,1,3]: page.screenshot(path=str(out/f'catalogue-page-{number+1}.png'))
            assert state()['pixelBytes'] <= 8*1024*1024 and state()['entries'] <= 32
        def reuse():
            set_page(0)
            for item in state()['previews']: visible(item['id'])
            set_page(1)
            for item in state()['previews']: visible(item['id'])
            before = state()['builds']
            set_page(0)
            for item in state()['previews']: visible(item['id'])
            assert state()['builds'] == before, (before,state())
            assert state()['hits'] > 0 and page.evaluate('window.chartSceneBrowser.modelLoads') == original_loads
        test('revisited pages reuse previews without replacing active chart',reuse)
        def resize():
            before = state()['builds']
            page.set_viewport_size({'width':800,'height':700})
            page.wait_for_function('window.chartScene.width===800')
            top(); pixels('overview')
            assert state()['builds'] == before, state()
            page.screenshot(path=str(out/'catalogue-compact.png'))
            page.set_viewport_size({'width':1440,'height':1000})
            page.wait_for_function('window.chartScene.width===1440')
        test('responsive thumbnail resizing reuses pixel buffers',resize)
        def theme():
            top(); before = state()['builds']; inputs.theme(1)
            page.wait_for_function("window.chartScene.theme==='Light'")
            pixels('overview')
            assert state()['builds'] > before, state()
            page.screenshot(path=str(out/'catalogue-light.png'))
            inputs.theme(0); page.wait_for_function("window.chartScene.theme==='Dark'"); pixels('overview')
        test('theme changes rebuild correct miniature colors',theme)
        def navigate():
            set_page(1); top(); identifier = state()['previews'][0]['id']; visible(identifier)
            click('tile:'+identifier)
            page.wait_for_function('id=>window.chartScene.exampleId===id&&window.chartSceneBrowser.document==="Example"',arg=identifier)
            assert page.evaluate('window.chartSceneBrowser.modelLoads') == original_loads + 1
            click('browser-back')
            page.wait_for_function("window.chartSceneBrowser.document==='Catalogue'")
            assert state()['page'] == 1
        test('preview card opens real example and preserves catalogue page',navigate)
        def clean():
            assert not [e for e in logs if e['type']=='pageerror'], logs
            assert state()['pixelBytes'] <= 8*1024*1024 and state()['entries'] <= 32
        test('catalogue has no unhandled errors or cache budget overflow',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not t['passed'] for t in results)
print(f'{len(results)-failed}/{len(results)} catalogue checks passed',flush=True)
raise SystemExit(1 if failed else 0)
