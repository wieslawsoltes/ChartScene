"""Real Uno point editing: only pointer/keyboard input, never writes diagnostic app state."""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright
from uno_input import UnoInput

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-editing')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
    page = browser.new_page(viewport={'width':1440,'height':1000})
    page.set_default_timeout(20_000)
    page.on('pageerror', lambda e: logs.append({'type':'pageerror','text':str(e)}))
    page.on('console', lambda e: logs.append({'type':e.type,'text':e.text}))
    inputs = UnoInput(page, out)
    def state(): return page.evaluate('window.chartSceneEditing || {}')
    def ready():
        page.wait_for_function("()=>{const s=window.chartSceneEditing;if(s?.error)throw new Error(s.error);return s?.ready&&!document.querySelector('#loading.uno-persistent-loader')}",timeout=120_000)
    def command(name):
        before = state()['actions']
        inputs.click('editing-'+name, state()['controls'][name], resolve=lambda: state()['controls'].get(name))
        page.wait_for_function('p=>window.chartSceneEditing.actions>p.before&&window.chartSceneEditing.lastAction===p.name',arg={'before':before,'name':name})
        ready()
    def wait_value(value):
        page.wait_for_function('v=>window.chartSceneEditing.ready&&Math.abs(window.chartSceneEditing.points[8].y-v)<1e-7',arg=value)
    def screen_for_y(s, y):
        point=s['points'][8]; plot=s['plot']
        fraction=math.log10(y)/2 if s['logarithmic'] else y/100
        if s['valueVertical'] != s['flipped']: fraction=1-fraction
        if s['valueVertical']: return point['screenX'], plot['y']+fraction*plot['height']
        return plot['x']+fraction*plot['width'], point['screenY']
    def begin():
        ready(); point=state()['points'][8]
        page.mouse.move(point['screenX'],point['screenY'])
        page.wait_for_function('p=>(window.chartSceneInputEvents||[]).some(e=>e.target==="editing-chart"&&e.kind==="move"&&Math.abs(e.x-p.x)<1&&Math.abs(e.y-p.y)<1)',arg={'x':point['screenX'],'y':point['screenY']})
        page.mouse.down()
        page.wait_for_function('window.chartSceneEditing.editing===true')
    def drag(y):
        before=state(); target=screen_for_y(before,y); begin()
        try:
            page.mouse.move(*target,steps=7); wait_value(y)
        finally: page.mouse.up()
        page.wait_for_function('window.chartSceneEditing.editing===false');ready()
        assert state()['commits']==before['commits']+1
        assert [q['x'] for q in state()['points']]==[q['x'] for q in before['points']]
        assert all(abs(q['y']-before['points'][i]['y'])<1e-12 for i,q in enumerate(state()['points']) if i!=8)
    def pixels(name):
        ready(); page.screenshot(path=str(out/(name+'.png')));b=state()['plot']
        with Image.open(out/(name+'.png')) as im:
            crop=im.convert('RGB').crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
            count=sum(max(c)-min(c)>45 and max(c)>100 for c in crop.resize((300,180)).getdata())
            assert count>50,count
    def test(name, action):
        started=time.perf_counter()
        try:
            action();assert not state().get('error'),state()
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000});print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()});print('FAIL',name,error,flush=True)
            page.mouse.up()
            page.screenshot(path=str(out/(name.replace(' ','-')+'-failure.png')))
            (out/(name.replace(' ','-')+'-state.json')).write_text(json.dumps(state(),indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?view=editing&inputTrace=1',wait_until='domcontentloaded')
        test('native point editing startup',ready)
        test('editing plot contains visible source geometry',lambda:pixels('editing-dark'))
        def basic():
            command('reset');drag(70);command('undo');wait_value(50);command('redo');wait_value(70)
            pixels('editing-dragged')
        test('original source drag preserves X and supports shared undo redo',basic)
        def cancel():
            command('reset');before=state();target=screen_for_y(before,65);begin()
            try:
                page.mouse.move(*target,steps=4);wait_value(65);page.keyboard.press('Escape');wait_value(50)
            finally: page.mouse.up()
            assert not state()['editing'] and not state()['canUndo'] and state()['commits']==before['commits']
        test('Escape rolls back the live preview without history',cancel)
        def stationary():
            command('reset');before=state();begin();page.mouse.up()
            page.wait_for_function('window.chartSceneEditing.editing===false');ready()
            assert state()['dataRevision']==before['dataRevision'] and not state()['canUndo']
        test('stationary marker clicks preserve exact data revision',stationary)
        def history():
            command('reset');drag(60);drag(70)
            command('undo');wait_value(60);command('undo');wait_value(50)
            command('redo');wait_value(60);command('redo');wait_value(70)
        test('separate gestures remain undoable in the correct order',history)
        def transforms():
            command('reset');command('log');drag(65);command('undo');wait_value(50)
            command('rotate');drag(70);command('undo');wait_value(50)
            command('flip');drag(60);command('undo');wait_value(50);pixels('editing-rotated-log')
            command('flip');command('rotate');command('log')
        test('logarithmic rotated and flipped axes edit the correct value coordinate',transforms)
        def conflict():
            command('reset');before=state();target=screen_for_y(before,65);begin()
            try:
                page.mouse.move(*target,steps=4);wait_value(65)
                page.keyboard.press('F6');wait_value(82)
            finally: page.mouse.up()
            page.wait_for_function('window.chartSceneEditing.editing===false');ready()
            assert state()['outcome']=='Conflict' and not state()['canUndo']
            assert state()['commits']==before['commits'];wait_value(82)
        test('external update during captured drag supersedes the stale gesture',conflict)
        def stale_undo():
            command('reset');drag(70);command('external');wait_value(82);revision=state()['dataRevision']
            command('undo');command('redo');wait_value(82)
            assert state()['dataRevision']==revision and state()['outcome']=='Conflict'
        test('guarded history does not overwrite newer source data',stale_undo)
        def kinds():
            for kind in ['Spline','Scatter','Column','Bar','Impulse','Line']:
                command('kind');assert state()['kind']==kind;drag(65);command('undo');wait_value(50)
                if kind=='Bar':pixels('editing-bars')
        test('all editing gallery series expose draggable original endpoints',kinds)
        def pan():
            command('reset');command('edit');assert not state()['enabled'];before=state();b=before['plot']
            x=b['x']+b['width']*.35;y=b['y']+b['height']*.2
            page.mouse.move(x,y);page.mouse.down();page.mouse.move(x+45,y+25,steps=7);page.mouse.up();ready()
            page.wait_for_function('window.chartSceneEditing.canUndo')
            assert state()['dataRevision']==before['dataRevision'] and not state()['editing']
            command('undo');command('edit');assert state()['enabled']
        test('explicit pan mode navigates without changing source values',pan)
        def theme():
            before=state()['dataRevision'];command('theme');assert state()['theme']=='Light';pixels('editing-light')
            command('theme');assert state()['theme']=='Dark' and state()['dataRevision']==before
        test('native editing tools inspector and plot support both themes',theme)
        def inspector():
            before=state()['plot']['width'];command('info')
            page.wait_for_function('w=>!window.chartSceneEditing.controls.inspector&&window.chartSceneEditing.plot.width>w',arg=before)
            command('info');page.wait_for_function('window.chartSceneEditing.controls.inspector!==null')
        test('collapsible point inspector releases actual chart layout',inspector)
        def compact():
            page.set_viewport_size({'width':920,'height':740})
            page.wait_for_function('!window.chartSceneEditing.controls.inspector&&window.chartSceneEditing.controls.chart.width<930');ready()
            assert state()['plot']['width']>700;drag(65);command('undo');wait_value(50);pixels('editing-compact')
            page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartSceneEditing.controls.inspector!==null');ready()
        test('compact editing keeps the chart and commands reachable',compact)
        def reattach():
            drag(70);before=state()
            for name in ['linked','editing']:
                key='gallery-'+name;resolve=lambda key=key:page.evaluate('k=>window.chartSceneGallery.controls[k]',key)
                inputs.click(key,resolve(),resolve=resolve)
                page.wait_for_function('v=>window.chartSceneGallery.view===v',arg=name)
            ready();wait_value(70);assert state()['generation']==before['generation']
            command('undo');wait_value(50)
        test('editing view unload and reload retains data and undo history',reattach)
        def idle():
            before=state()['frames'];page.wait_for_timeout(1200)
            assert state()['frames']-before<=3,(before,state()['frames'])
        test('idle point inspector does not force continuous chart redraw',idle)
        def clean():assert not [e for e in logs if e['type']=='pageerror'],logs
        test('editing gallery has no unhandled browser errors',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} editing browser checks passed',flush=True)
raise SystemExit(1 if failed else 0)
