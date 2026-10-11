"""Real Uno overview/scrollbar input and visible output. Diagnostic state is read-only."""
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
parser.add_argument('--output', default='artifacts/browser-tests-overview')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs, metrics = [], [], []
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(args=['--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
    page = browser.new_page(viewport={'width': 1440, 'height': 1000}, device_scale_factor=1)
    page.set_default_timeout(20_000)
    page.on('pageerror', lambda e: logs.append({'type': 'pageerror', 'text': str(e)}))
    page.on('console', lambda e: logs.append({'type': e.type, 'text': e.text}))
    def state(): return page.evaluate('window.chartSceneOverview || {}')
    def ready(preset=None):
        page.wait_for_function('''id=>{const s=window.chartSceneOverview;if(s?.error)throw new Error(s.error);return s?.ready&&(!id||s.preset===id)}''', arg=preset, timeout=120_000)
    def wait_range_changed(before, dimension='x'):
        page.wait_for_function('''a=>{const s=window.chartSceneOverview;return a.dimension==='x'?(s.min!==a.min||s.max!==a.max):(s.yMin!==a.yMin||s.yMax!==a.yMax);}''', arg=dict(before, dimension=dimension))
    def click(name):
        b = state()['controls'][name]
        page.mouse.click(b['x']+b['width']/2, b['y']+b['height']/2)
    def at(x, y=.5, view='overview'):
        r = state()[view]['track']
        return (r['x']+x*r['width'], r['y']+y*r['height'])
    def center(view='overview'):
        r = state()[view]['selection']
        return (r['x']+r['width']/2, r['y']+r['height']/2)
    def drag(a, b, shift=False):
        if shift: page.keyboard.down('Shift')
        try:
            page.mouse.move(*a); page.mouse.down(); page.mouse.move(*b, steps=12); page.mouse.up()
        finally:
            if shift: page.keyboard.up('Shift')
    def selection(a=.2, b=.5):
        old = state(); drag(at(a), at(b), shift=True)
        wait_range_changed(old); page.wait_for_timeout(250)
    def choose(index):
        # Input can be queued before Uno has opened and focused the popup. Await its
        # actual state instead of letting Enter reopen it after premature key events.
        if not state()['presetOpen']: click('preset')
        page.wait_for_function('window.chartSceneOverview.presetOpen===true')
        page.keyboard.press('Home')
        for _ in range(index): page.keyboard.press('ArrowDown')
        page.keyboard.press('Enter')
        identifier = ['million','logarithmic','streaming','heatmap'][index]
        ready(identifier)
        page.wait_for_function('window.chartSceneOverview.presetOpen===false')
        page.wait_for_timeout(350)
    def near(a, b, tolerance=1e-8):
        assert math.isfinite(a) and abs(a-b) <= tolerance*max(1, abs(b)), (a, b)
    def stable():
        page.wait_for_timeout(350); before = state()
        page.wait_for_timeout(350); after = state()
        assert before['previewBuilds'] == after['previewBuilds'], ('idle miniature keeps rebuilding', before, after)
        return after
    def screenshot(name):
        path=out/(name+'.png'); page.screenshot(path=str(path)); return path
    def pixels(name):
        # Exclude the grips: the center of the thumbnail must contain actual signal/image geometry.
        path = screenshot(name); r = state()['overview']['track']
        with Image.open(path) as source:
            image = source.convert('RGB').crop((int(r['x']+12), int(r['y']+5), int(r['x']+r['width']-12), int(r['y']+r['height']-5)))
            count = sum(max(c)-min(c)>35 and max(c)>90 for c in image.getdata())
        assert count > 100, (name, count)
    def test(name, action):
        started=time.perf_counter()
        try:
            action(); assert not state().get('error'), state().get('error')
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000})
            print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()})
            print('FAIL',name,str(error),flush=True)
            screenshot(name.replace(' ','-')+'-failure'); (out/'failure-state.json').write_text(json.dumps(state(),indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?view=overview',wait_until='domcontentloaded')
        test('overview compiled Uno startup',lambda:ready('million'))
        if not state().get('ready'): raise RuntimeError('Overview did not render')
        def initial():
            s=stable(); assert s['sourcePoints']==1_000_000 and s['previewBuilds']>0
            near(s['fullMin'],1); near(s['fullMax'],1_000_000); pixels('million-overview')
        test('overview million original samples and visible miniature',initial)
        def body():
            before=stable(); start=center(); r=before['overview']['track']
            drag(start,(start[0]+r['width']*.15,start[1])); wait_range_changed(before); after=stable()
            near(after['max']-after['min'],before['max']-before['min'])
            assert after['min']>before['min'] and after['dataRevision']==before['dataRevision']
            assert after['previewBuilds']==before['previewBuilds']
            metrics.append({'operation':'million-sample overview drag','previewBuildsBefore':before['previewBuilds'],'previewBuildsAfter':after['previewBuilds'],'sourceRevisionBefore':before['dataRevision'],'sourceRevisionAfter':after['dataRevision']})
        test('overview body drag reuses preview and preserves span',body)
        def history():
            before=state(); click('undo'); wait_range_changed(before); undone=state(); click('redo'); wait_range_changed(undone)
            near(state()['min'],before['min']); near(state()['max'],before['max'])
        test('overview shared undo redo',history)
        def grips():
            old=state(); g=old['overview']; drag((g['minimumX'],g['minimumY']),at(.15)); wait_range_changed(old)
            near(state()['max'],old['max']); assert state()['min']<old['min']
            old=state(); g=old['overview']; drag((g['maximumX'],g['maximumY']),at(.8)); wait_range_changed(old)
            near(state()['min'],old['min']); assert state()['max']>old['max']
        test('overview both grips resize the requested endpoint',grips)
        def new_range():
            selection(.3,.6); s=state(); r=s['fullMax']-s['fullMin']
            near((s['min']-s['fullMin'])/r,.3,.002); near((s['max']-s['fullMin'])/r,.6,.002)
        test('overview shift drag creates a new viewport',new_range)
        def cancel():
            before=state(); a=center(); page.mouse.move(*a); page.mouse.down(); page.mouse.move(*at(.7),steps=8)
            wait_range_changed(before); page.keyboard.press('Escape'); page.mouse.up()
            page.wait_for_function('b=>Math.abs(window.chartSceneOverview.min-b.min)<1e-6&&Math.abs(window.chartSceneOverview.max-b.max)<1e-6',arg=before)
        test('overview Escape cancels active drag',cancel)
        def keyboard():
            page.mouse.click(*center()); before=state(); page.keyboard.press('ArrowRight'); wait_range_changed(before)
            assert state()['min']>before['min']; near(state()['max']-state()['min'],before['max']-before['min'])
            page.keyboard.press('Control+z'); page.wait_for_function('v=>Math.abs(window.chartSceneOverview.min-v)<1e-6',arg=before['min'])
            page.keyboard.press('Home'); page.wait_for_function('''()=>{const s=window.chartSceneOverview;return s.min===s.fullMin&&s.max===s.fullMax}''')
        test('overview keyboard navigation fit and undo',keyboard)
        def external():
            selection(.2,.7); before=stable(); r=before['controls']['chart']
            page.mouse.move(r['x']+r['width']*.5,r['y']+r['height']*.6); page.mouse.wheel(0,-240)
            wait_range_changed(before); after=stable()
            assert after['max']-after['min']<before['max']-before['min']
            assert after['overview']['selection']['width']<before['overview']['selection']['width']
            assert after['previewBuilds']==before['previewBuilds']
        test('overview follows main chart zoom without rebuilding',external)
        def vertical():
            before=state(); a=center('scrollbar'); r=before['scrollbar']['track']
            drag(a,(a[0],a[1]-r['height']*.1)); wait_range_changed(before,'y'); after=state()
            assert after['yMin']>before['yMin']; near(after['yMax']-after['yMin'],before['yMax']-before['yMin'])
            near(after['min'],before['min']); near(after['max'],before['max'])
            click('undo'); page.wait_for_function('v=>Math.abs(window.chartSceneOverview.yMin-v)<1e-7',arg=before['yMin'])
        test('independent vertical scrollbar changes only Y',vertical)
        def flipped():
            before=state(); click('flip'); page.wait_for_function('window.chartSceneOverview.flipped===true'); page.wait_for_timeout(300)
            assert state()['overview']['minimumX']>state()['overview']['maximumX']
            before=stable(); a=center(); drag(a,(a[0]+50,a[1])); wait_range_changed(before)
            assert state()['min']<before['min']; near(state()['max']-state()['min'],before['max']-before['min'])
            click('flip'); page.wait_for_function('window.chartSceneOverview.flipped===false')
        test('overview flipped coordinates reverse screen direction',flipped)
        def replace():
            old=stable(); click('replace'); page.wait_for_function('g=>window.chartSceneOverview.generation===g+1',arg=old['generation'])
            page.wait_for_function('n=>window.chartSceneOverview.previewBuilds>n',arg=old['previewBuilds']); after=stable()
            near(after['min'],old['min']); near(after['max'],old['max']); assert after['sourcePoints']==old['sourcePoints']
            a=center(); drag(a,(a[0]+30,a[1])); wait_range_changed(after); assert stable()['previewBuilds']==after['previewBuilds']
        test('overview source replacement invalidates miniature once',replace)
        def log():
            choose(1); before=stable(); near(before['fullMin'],.001); near(before['fullMax'],1e6)
            a=center(); r=before['overview']['track']; drag(a,(a[0]+r['width']*.1,a[1])); wait_range_changed(before); after=stable()
            near(after['max']/after['min'],before['max']/before['min'],1e-7)
            assert after['previewBuilds']==before['previewBuilds']; pixels('logarithmic-overview')
        test('overview logarithmic panning preserves ratios',log)
        def log_grips():
            before=state(); g=before['overview']; drag((g['minimumX'],g['minimumY']),at(.25)); wait_range_changed(before)
            near(state()['max'],before['max'],1e-7); assert 0<state()['min']<before['min']
        test('overview logarithmic grip resizing',log_grips)
        def log_noop():
            before=stable(); page.mouse.click(*center()); after=stable()
            assert after['min']==before['min'] and after['max']==before['max']
            assert after['previewBuilds']==before['previewBuilds']
        test('overview stationary log click preserves exact range',log_noop)
        def theme():
            before=state(); click('theme'); page.wait_for_function("window.chartSceneOverview.theme==='Light'")
            page.wait_for_function('n=>window.chartSceneOverview.previewBuilds>n',arg=before['previewBuilds']); stable(); pixels('overview-light')
            click('theme'); page.wait_for_function("window.chartSceneOverview.theme==='Dark'")
        test('overview themes invalidate picture resources',theme)
        def stream():
            choose(2); before=state(); click('stream'); page.wait_for_function('window.chartSceneOverview.streaming===true')
            page.wait_for_function('r=>window.chartSceneOverview.dataRevision>r+21',arg=before['dataRevision'])
            current=state(); assert current['sourcePoints']==4000
            assert current['fullMin']>before['fullMin'] and current['fullMax']>before['fullMax']
            assert current['previewBuilds']>before['previewBuilds']
            click('stream'); page.wait_for_function('window.chartSceneOverview.streaming===false'); after=stable()
            assert state()['dataRevision']==after['dataRevision']; pixels('streaming-overview')
        test('overview FIFO source changes update full-domain preview',stream)
        def heatmap():
            choose(3); s=stable(); assert s['sourcePoints']==24000
            near(s['fullMin'],0); near(s['fullMax'],1000); pixels('heatmap-overview')
            before=state(); a=center(); drag(a,(a[0]+50,a[1])); wait_range_changed(before)
            assert stable()['previewBuilds']==before['previewBuilds']
        test('overview nonuniform heatmap uses cached miniature',heatmap)
        def resize():
            before=state(); page.set_viewport_size({'width':1050,'height':780})
            page.wait_for_function('window.chartSceneOverview.controls.overview.width===1050'); ready()
            page.wait_for_function('n=>window.chartSceneOverview.previewBuilds>n',arg=before['previewBuilds']); pixels('overview-compact')
            r=state()['overview']['track']; near(r['width'],1050-24)
            page.set_viewport_size({'width':1440,'height':1000}); page.wait_for_function('window.chartSceneOverview.controls.overview.width===1440'); ready()
        test('overview resize records correctly sized miniature',resize)
        def reattach():
            before=state()
            for name in ['gallery-2d','gallery-overview']:
                b=page.evaluate('n=>window.chartSceneGallery.controls[n]',name)
                page.mouse.click(b['x']+b['width']/2,b['y']+b['height']/2); page.wait_for_timeout(500)
            ready(); page.wait_for_function('n=>window.chartSceneOverview.overviewFrames>n',arg=before['overviewFrames'])
            a=center(); old=state(); drag(a,(a[0]+20,a[1])); wait_range_changed(old); pixels('overview-reattached')
        test('overview detaches and reattaches subscriptions',reattach)
        def clean(): assert not [e for e in logs if e['type']=='pageerror'], logs
        test('overview no unhandled browser errors',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
        (out/'performance.json').write_text(json.dumps({'scope':'Observed reuse counts during actual browser interaction; not FPS or GPU timing','operations':metrics},indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2)); browser.close()
failed=sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} overview browser checks passed',flush=True)
raise SystemExit(1 if failed else 0)
