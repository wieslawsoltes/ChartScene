"""Drive the redesigned compiled Uno browser through actual input; diagnostic state stays read-only."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright
from uno_input import UnoInput

parser=argparse.ArgumentParser()
parser.add_argument('--url',default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output',default='artifacts/browser-tests-ui')
args=parser.parse_args()
out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
results,logs=[],[]
with sync_playwright() as playwright:
    browser=playwright.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
    page=browser.new_page(viewport={'width':1440,'height':1000},accept_downloads=True)
    page.set_default_timeout(20_000)
    page.on('pageerror',lambda e:logs.append({'type':'pageerror','text':str(e)}))
    page.on('console',lambda e:logs.append({'type':e.type,'text':e.text}))
    inputs=UnoInput(page,out)
    def state():return page.evaluate('window.chartScene || {}')
    def chrome():return page.evaluate('window.chartSceneBrowser || {}')
    def ready(identifier=None):
        page.wait_for_function('''id=>{const s=window.chartScene;if(s?.error||s?.fatalError)throw new Error(s.error||s.fatalError);return s?.ready&&window.chartSceneBrowser&&(!id||s.exampleId===id);}''',arg=identifier,timeout=120_000)
    def bounds(name):
        page.wait_for_function('n=>window.chartScene?.controls?.[n]?.width>0',arg=name)
        return state()['controls'][name]
    def click(name):
        inputs.click('browser-'+name,bounds(name),resolve=lambda:state()['controls'].get(name))
    def module(name):
        key='gallery-'+name
        resolve=lambda:page.evaluate('n=>window.chartSceneGallery.controls[n]',key)
        inputs.click(key,resolve(),resolve=resolve)
        page.wait_for_function('v=>window.chartSceneGallery.view===v',arg=name)
    def query(text):
        click('search');page.keyboard.press('Control+A');page.keyboard.insert_text(text)
        page.wait_for_timeout(100)
    def choose(identifier,text):
        query(text);bounds('example:'+identifier);click('example:'+identifier);ready(identifier)
        page.wait_for_function("window.chartSceneBrowser.document==='Example'")
    def point(x=.4,y=.65):
        s=state();c=s['controls']['chart'];p=s['plot']
        return c['x']+p['X']+p['Width']*x,c['y']+p['Y']+p['Height']*y
    def test(name,action):
        started=time.perf_counter()
        try:
            action();assert not state().get('error'),state().get('error')
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000});print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()});print('FAIL',name,error,flush=True)
            page.screenshot(path=str(out/(name.replace(' ','-')+'-failure.png')))
            (out/'failure-state.json').write_text(json.dumps({'chart':state(),'browser':chrome()},indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?inputTrace=1',wait_until='domcontentloaded')
        test('examples workbench startup',lambda:ready('overview'))
        if not state().get('ready'):raise RuntimeError('Workbench did not start')
        def layout():
            s=state();assert s['controls']['chart']['x']<80,s['controls']['chart']
            assert s['controls']['chart']['width']>1000,s['controls']['chart']
            assert s['controls']['navigation-panel'] is None
            assert s['controls']['inspector-panel']['width']<=300
            assert bounds('pan')['width']<40 and bounds('source-tab')['y']<150
            page.screenshot(path=str(out/'examples-workbench.png'))
            with Image.open(out/'examples-workbench.png') as image:
                p=s['plot'];c=s['controls']['chart']
                crop=image.convert('RGB').crop((int(c['x']+p['X']),int(c['y']+p['Y']),int(c['x']+p['X']+p['Width']),int(c['y']+p['Y']+p['Height'])))
                assert sum(max(pixel)-min(pixel)>45 and max(pixel)>100 for pixel in crop.resize((300,180)).getdata())>50
        test('compact vector rail and original-style chart layout',layout)
        def search_code():
            query('FifoCapacity');page.wait_for_function("window.chartScene.controls['example:live']!==null && window.chartSceneBrowser.resultCount===1")
            before=chrome();query('  FIFOCAPACITY ')
            page.wait_for_timeout(150)
            assert chrome()['searchExecutions']==before['searchExecutions']
            assert chrome()['navigationBuilds']==before['navigationBuilds']
            click('example:live');ready('live');assert state()['controls']['navigation-panel'] is None
        test('source keyword search and unchanged-result reuse',search_code)
        def history():
            choose('financial','Candlesticks and Bollinger bands')
            click('browser-back');ready('live');click('browser-forward');ready('financial')
            assert chrome()['canBack'] and not chrome()['canForward']
        test('example back and forward navigation',history)
        def source():
            before=chrome()['modelLoads'];click('source-tab');page.wait_for_function("window.chartSceneBrowser.document==='Source'&&window.chartSceneBrowser.sourceLoaded")
            assert chrome()['modelLoads']==before
            assert state()['controls']['chart'] is None and bounds('source-view')['height']>300
            page.screenshot(path=str(out/'example-source.png'))
            with page.expect_download(timeout=30000) as info:click('download-source')
            download=info.value;path=out/download.suggested_filename;download.save_as(path)
            text=path.read_text();assert 'public static class SampleCatalog' in text and 'private static ChartModel Financial()' in text
            click('example-tab');ready('financial');page.wait_for_function("window.chartSceneBrowser.document==='Example'")
            assert chrome()['modelLoads']==before
        test('full factory source view and actual CSharp download',source)
        def catalogue():
            click('browser-home');page.wait_for_function("window.chartSceneBrowser.document==='Catalogue'")
            assert state()['controls']['chart'] is None and state()['controls']['inspector-panel'] is None
            page.screenshot(path=str(out/'example-catalogue.png'))
            click('tile:overview');ready('overview');page.wait_for_function("window.chartSceneBrowser.document==='Example'")
        test('home catalogue tiles navigate to real examples',catalogue)
        def info():
            assert bounds('inspector-panel')['width']>0
            click('info-toggle');page.wait_for_function("window.chartScene.controls['inspector-panel']===null")
            click('info-toggle');page.wait_for_function("window.chartScene.controls['inspector-panel']!==null")
        test('information panel toggle preserves available chart area',info)
        def reattach():
            page.mouse.move(*point());before=state()['ranges']['x'];page.mouse.wheel(0,-240)
            page.wait_for_function('span=>window.chartScene.ranges.x.max-window.chartScene.ranges.x.min<span',arg=before['max']-before['min'])
            saved=state()['ranges']['x'];loads=chrome()['modelLoads'];module('linked');module('2d');ready('overview')
            assert state()['ranges']['x']==saved,(state()['ranges']['x'],saved)
            assert chrome()['modelLoads']==loads
        test('module reattachment retains selected example and zoom',reattach)
        def pause():
            click('play');ready('live');page.wait_for_function('window.chartScene.streaming===true')
            revision=state()['series'][0]['revision'];page.wait_for_function('v=>window.chartScene.series[0].revision>v+2',arg=revision)
            loads=chrome()['modelLoads'];module('linked');module('2d');ready('live')
            page.wait_for_function('window.chartScene.streaming===false')
            revision=state()['series'][0]['revision'];page.wait_for_timeout(250)
            assert chrome()['modelLoads']==loads and state()['series'][0]['revision']==revision
        test('hidden streaming view pauses without losing FIFO data',pause)
        def shortcut():
            page.mouse.click(*point());page.keyboard.press('Control+f');page.keyboard.insert_text('FifoCapacity')
            page.wait_for_function("window.chartSceneBrowser.resultCount===1&&window.chartScene.controls['example:live']!==null")
            page.keyboard.press('Escape');page.wait_for_function("window.chartScene.controls['navigation-panel']===null")
        test('search keyboard shortcut and escape dismissal',shortcut)
        def responsive():
            page.set_viewport_size({'width':800,'height':700});page.wait_for_function('window.chartScene.width===800')
            # User explicitly opened Info above: close it before measuring narrow chart navigation.
            if state()['controls'].get('inspector-panel'):click('info-toggle');page.wait_for_function("window.chartScene.controls['inspector-panel']===null")
            assert bounds('chart')['width']>600
            page.screenshot(path=str(out/'workbench-compact.png'))
            for name in ['search','source-tab','example-tab','theme','pan','home','play']:
                b=bounds(name);assert b['x']>=0 and b['x']+b['width']<=801,(name,b)
            page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartScene.width===1440')
        test('responsive commands remain reachable',responsive)
        def clean():assert not [entry for entry in logs if entry['type']=='pageerror'],logs
        test('examples workbench has no unhandled browser errors',clean)
        (out/'final-state.json').write_text(json.dumps({'chart':state(),'browser':chrome()},indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} workbench UI checks passed',flush=True)
raise SystemExit(1 if failed else 0)
