"""Exercise the real Uno GLCanvas control; never write application diagnostic state."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-3d')
args = parser.parse_args()
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
    context = browser.new_context(viewport={'width':1440,'height':1000}, device_scale_factor=1)
    page = context.new_page(); page.set_default_timeout(20_000)
    page.on('pageerror',lambda e: logs.append({'type':'pageerror','text':str(e)}))
    page.on('console',lambda e: logs.append({'type':e.type,'text':e.text}))
    def state(): return page.evaluate('window.chartScene3D || {}')
    def ready(identifier=None):
        page.wait_for_function('''id=>{const s=window.chartScene3D;if(s?.error)throw new Error(s.error);return s?.ready&&(!id||s.exampleId===id)}''',arg=identifier,timeout=120_000)
    def click(name):
        page.wait_for_function('n=>window.chartScene3D?.controls?.[n]?.width>0',arg=name)
        b=state()['controls'][name];page.mouse.click(b['x']+b['width']/2,b['y']+b['height']/2);page.wait_for_timeout(180)
    def point(x=.5,y=.5):
        b=state()['controls']['chart'];return (int(b['x']+x*b['width']),int(b['y']+y*b['height']))
    def frame_after(old): page.wait_for_function('n=>window.chartScene3D.frames>n',arg=old)
    def screenshot(name):
        path=out/(name+'.png');page.screenshot(path=str(path));return path
    def rendered_pixels(name):
        path=screenshot(name); b=state()['controls']['chart']
        image=Image.open(path).convert('RGB').crop((int(b['x']+20),int(b['y']+80),int(b['x']+b['width']-20),int(b['y']+b['height']-30)))
        colors=image.resize((240,160)).getdata()
        count=sum(max(c)-min(c)>45 and max(c)>95 for c in colors)
        assert count>30, (name,'no colored geometry visible',count)
    def test(name,action):
        start=time.perf_counter()
        try:
            action();assert not state().get('error'),state().get('error')
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-start)*1000});print('PASS',name,flush=True)
        except Exception as e:
            results.append({'name':name,'passed':False,'error':str(e),'trace':traceback.format_exc()});print('FAIL',name,str(e),flush=True)
            screenshot(name.replace(' ','-')+'-failure')
            (out/'failure-state.json').write_text(json.dumps(state(),indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not t['passed'] for t in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?view=3d',wait_until='domcontentloaded')
        test('3D GPU startup',lambda:ready('surfacemesh'))
        if not state().get('ready'):raise RuntimeError('GPU startup failed; no following results are meaningful')
        test('3D surface visible pixels',lambda:rendered_pixels('surface-initial'))
        def orbit():
            before=state();page.mouse.move(*point(.45,.6));page.mouse.down();page.mouse.move(*point(.56,.67),steps=10);page.mouse.up();frame_after(before['frames'])
            after=state();assert abs(after['camera']['yaw']-before['camera']['yaw'])>5
            assert after['uploadedBytes']==0,after
            assert after['canUndo'];rendered_pixels('surface-orbit')
        test('3D orbit reuses GPU buffers',orbit)
        def undo_redo():
            yaw=state()['camera']['yaw'];click('undo');page.wait_for_function('v=>Math.abs(window.chartScene3D.camera.yaw-v)>1',arg=yaw)
            click('redo');page.wait_for_function('v=>Math.abs(window.chartScene3D.camera.yaw-v)<0.001',arg=yaw)
        test('3D camera undo redo',undo_redo)
        def pan():
            click('fit');before=state();page.keyboard.down('Control');page.mouse.move(*point(.45,.6));page.mouse.down();page.mouse.move(*point(.5,.65),steps=8);page.mouse.up();page.keyboard.up('Control');frame_after(before['frames'])
            after=state()['camera'];assert sum(abs(after[k]-before['camera'][k]) for k in ['targetX','targetY','targetZ'])>.01
            click('undo')
        test('3D control-drag pan',pan)
        def zoom():
            before=state();page.mouse.move(*point());page.mouse.wheel(0,-240);page.wait_for_function('d=>window.chartScene3D.camera.distance<d',arg=before['camera']['distance'])
            assert state()['uploadedBytes']==0
        test('3D wheel zoom',zoom)
        def projection():
            click('projection');page.wait_for_function("window.chartScene3D.camera.projection==='Orthographic'");rendered_pixels('orthographic')
            click('projection');page.wait_for_function("window.chartScene3D.camera.projection==='Perspective'")
        test('3D perspective orthographic',projection)
        def grid():
            click('grid');page.wait_for_function('window.chartScene3D.grid===false');rendered_pixels('surface-no-grid');click('grid');page.wait_for_function('window.chartScene3D.grid===true')
        test('3D grid toggle',grid)
        def wireframe():
            click('surface');page.keyboard.press('Home');page.keyboard.press('ArrowDown');page.keyboard.press('Enter');page.wait_for_function("window.chartScene3D.surfaceMode==='Wireframe'")
            screenshot('wireframe')
            click('surface');page.keyboard.press('Home');page.keyboard.press('Enter');page.wait_for_function("window.chartScene3D.surfaceMode==='Solid'")
        test('3D solid wireframe modes',wireframe)
        def theme():
            click('theme');page.keyboard.press('Home');page.keyboard.press('ArrowDown');page.keyboard.press('Enter');page.wait_for_function("window.chartScene3D.theme==='Light'");rendered_pixels('surface-light')
            click('theme');page.keyboard.press('Home');page.keyboard.press('Enter');page.wait_for_function("window.chartScene3D.theme==='Dark'")
        test('3D theme control',theme)
        def selection():
            click('fit');page.mouse.click(*point(.5,.52));page.wait_for_function('window.chartScene3D.selected>0');click('undo');page.wait_for_function('window.chartScene3D.selected===0')
        test('3D mesh selection and undo',selection)
        for _ in range(10):
            identifier=state()['exampleId']
            def sample(identifier=identifier):
                ready(identifier);s=state();assert s['vertices']>0 and s['drawCalls']>0 and s['sourcePoints']>0
                rendered_pixels('sample-'+identifier)
            test('3D sample '+identifier,sample)
            click('next');ready()
        def resize():
            page.set_viewport_size({'width':900,'height':700});page.wait_for_function('window.chartScene3D.width===900');ready();rendered_pixels('3d-compact')
            page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartScene3D.width===1440');ready()
        test('3D framebuffer resize',resize)
        def reattach():
            previous=state()['frames']
            for name in ['gallery-2d','gallery-3d']:
                b=page.evaluate('n=>window.chartSceneGallery.controls[n]',name);page.mouse.click(b['x']+b['width']/2,b['y']+b['height']/2)
                page.wait_for_timeout(700)
            ready();frame_after(previous);rendered_pixels('context-reattached')
        test('3D GL context detach reattach',reattach)
        def clean():assert not [e for e in logs if e['type']=='pageerror'],logs
        test('3D no unhandled browser errors',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not t['passed'] for t in results);print(f'{len(results)-failed}/{len(results)} 3D browser checks passed',flush=True)
raise SystemExit(1 if failed else 0)
