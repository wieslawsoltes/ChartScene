"""Drive the compiled Uno workbench with real input and measured presentation/layout observations."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import time
import traceback
from PIL import Image
from playwright.sync_api import sync_playwright
from uno_input import UnoInput
from workbench_input import WorkbenchInput

parser=argparse.ArgumentParser()
parser.add_argument('--url',default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output',default='artifacts/browser-tests-ui')
args=parser.parse_args()
out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
results,logs=[],[]
with sync_playwright() as playwright:
    browser=playwright.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
    page=browser.new_page(viewport={'width':1440,'height':1000},accept_downloads=True);page.set_default_timeout(20_000)
    page.on('pageerror',lambda e:logs.append({'type':'pageerror','text':str(e)}))
    page.on('console',lambda e:logs.append({'type':e.type,'text':e.text}))
    inputs=WorkbenchInput(page,out);module_input=UnoInput(page,out)
    def state():return inputs.state()
    def chrome():return page.evaluate('window.chartSceneBrowser || {}')
    def ready(identifier=None):
        page.wait_for_function('''id=>{const s=window.chartScene;if(s?.error||s?.fatalError)throw new Error(s.error||s.fatalError);return s?.ready&&window.chartSceneBrowser&&(!id||s.exampleId===id);}''',arg=identifier,timeout=120_000)
        inputs.bounds('chart')
    def bounds(name):return inputs.bounds(name)
    def click(name):inputs.click(name)
    def module(name):
        key='gallery-'+name;resolve=lambda:page.evaluate('n=>window.chartSceneGallery.controls[n]',key)
        module_input.click(key,resolve(),resolve=resolve);page.wait_for_function('v=>window.chartSceneGallery.view===v',arg=name)
    def query(text):
        click('search');page.keyboard.press('Control+A');page.keyboard.insert_text(text)
        page.wait_for_timeout(100)
    def choose(identifier,text):
        query(text);bounds('example:'+identifier);click('example:'+identifier);ready(identifier)
        page.wait_for_function("window.chartSceneBrowser.document==='Example'")
    def point(x=.4,y=.65):
        s=state();c=s['controls']['chart'];p=s['plot'];return c['x']+p['X']+p['Width']*x,c['y']+p['Y']+p['Height']*y
    def presented_chart(name):
        # A managed render callback precedes compositor presentation. Keep the same pixel
        # threshold and require two actual screenshots; never inject input or force redraw.
        started=time.perf_counter();deadline=started+8;observations=[];consecutive=0
        path=out/(name+'.png')
        while True:
            current=state();assert not current.get('error'),current.get('error')
            page.screenshot(path=str(path));p=current['plot'];c=current['controls']['chart']
            with Image.open(path) as image:
                crop=image.convert('RGB').crop((int(c['x']+p['X']),int(c['y']+p['Y']),int(c['x']+p['X']+p['Width']),int(c['y']+p['Y']+p['Height'])))
                colored=sum(max(pixel)-min(pixel)>45 and max(pixel)>100 for pixel in crop.resize((300,180)).getdata())
            observations.append({'milliseconds':(time.perf_counter()-started)*1000,'coloredPixels':colored,'managedFrames':current['frames']})
            consecutive=consecutive+1 if colored>50 else 0
            if consecutive>=2 or time.perf_counter()>=deadline:break
            page.wait_for_timeout(100)
        (out/(name+'-presentation.json')).write_text(json.dumps(observations,indent=2))
        assert consecutive>=2,observations
    def test(name,action):
        started=time.perf_counter()
        try:
            action();assert not state().get('error'),state().get('error')
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000});print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()});print('FAIL',name,error,flush=True)
            slug=name.replace(' ','-');page.screenshot(path=str(out/(slug+'-failure.png')))
            observed=json.dumps({'chart':state(),'browser':chrome()},indent=2)
            (out/'failure-state.json').write_text(observed);(out/(slug+'-state.json')).write_text(observed)
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
            (out/'managed-focus.json').write_text(json.dumps(page.evaluate('window.chartSceneFocusEvents || []'),indent=2))
    try:
        page.goto(args.url+'?inputTrace=1',wait_until='domcontentloaded')
        test('examples workbench startup',lambda:ready('overview'))
        if not state().get('ready'):raise RuntimeError('Workbench did not start')
        def layout():
            s=state();assert s['controls']['chart']['x']<80,s['controls']['chart'];assert s['controls']['chart']['width']>1000,s['controls']['chart']
            assert s['controls']['navigation-panel'] is None and s['controls']['inspector-panel']['width']<=300
            assert bounds('pan')['width']<40 and bounds('source-tab')['y']<150
            presented_chart('examples-workbench')
        test('compact vector rail and original-style chart layout',layout)
        def search_code():
            query('FifoCapacity');page.wait_for_function("window.chartScene.controls['example:live']!==null && window.chartSceneBrowser.resultCount===1")
            before=chrome();query('  FIFOCAPACITY ');page.wait_for_timeout(150)
            assert chrome()['searchExecutions']==before['searchExecutions'] and chrome()['navigationBuilds']==before['navigationBuilds']
            click('example:live');ready('live');page.wait_for_function("window.chartScene.controls['navigation-panel']===null")
        test('source keyword search and unchanged-result reuse',search_code)
        def history():
            choose('financial','Candlesticks and Bollinger bands')
            click('browser-back');ready('live');click('browser-forward');ready('financial')
            assert chrome()['canBack'] and not chrome()['canForward']
        test('example back and forward navigation',history)
        def reselection():
            loads=chrome()['modelLoads'];choose('financial','Candlesticks and Bollinger bands')
            page.wait_for_function("window.chartScene.controls['navigation-panel']===null")
            assert chrome()['modelLoads']==loads
            choose('overview','Scientific signals');choose('financial','Candlesticks and Bollinger bands')
            assert chrome()['modelLoads']==loads+2
        test('cached search rows activate repeatedly without redundant reloads',reselection)
        def source():
            before=chrome()['modelLoads'];click('source-tab')
            page.wait_for_function("window.chartSceneBrowser.document==='Source' && window.chartSceneBrowser.sourceLoaded && window.chartScene.controls.chart===null && window.chartScene.controls['source-view']?.height>300")
            assert chrome()['modelLoads']==before
            source_bounds=bounds('source-view');assert source_bounds['height']>300
            page.screenshot(path=str(out/'example-source.png'))
            with page.expect_download(timeout=30000) as info:click('download-source')
            download=info.value;path=out/download.suggested_filename;download.save_as(path)
            text=path.read_text();assert 'public static class SampleCatalog' in text and 'private static ChartModel Financial()' in text
            click('example-tab');ready('financial');page.wait_for_function("window.chartSceneBrowser.document==='Example'")
            assert chrome()['modelLoads']==before
        test('full factory source view and actual CSharp download',source)
        def catalogue():
            click('browser-home')
            page.wait_for_function("window.chartSceneBrowser.document==='Catalogue' && window.chartScene.controls.chart===null && window.chartScene.controls['inspector-panel']===null")
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
            assert state()['ranges']['x']==saved,(state()['ranges']['x'],saved);assert chrome()['modelLoads']==loads
        test('module reattachment retains selected example and zoom',reattach)
        def pause():
            click('play');ready('live');page.wait_for_function('window.chartScene.streaming===true')
            revision=state()['series'][0]['revision'];page.wait_for_function('v=>window.chartScene.series[0].revision>v+2',arg=revision)
            loads=chrome()['modelLoads'];module('linked');module('2d');ready('live');page.wait_for_function('window.chartScene.streaming===false')
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
            if state()['controls'].get('inspector-panel'):click('info-toggle');page.wait_for_function("window.chartScene.controls['inspector-panel']===null")
            page.wait_for_function("window.chartScene.controls.chart?.width>600")
            for name in ['search','source-tab','example-tab','theme','pan','home','play']:
                b=bounds(name);assert b['x']>=0 and b['x']+b['width']<=801 and b['y']+b['height']<=701,(name,b)
            page.screenshot(path=str(out/'workbench-compact.png'))
            page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartScene.width===1440');bounds('theme');bounds('chart')
        test('responsive commands remain reachable',responsive)
        def durable_dismissal():
            loads=chrome()['modelLoads']; example=state()['exampleId']; observations=[]
            for width,height in [(800,700),(1024,768),(1440,1000)]:
                query('FifoCapacity')
                page.wait_for_function("window.chartSceneBrowser.navigationOpen && window.chartSceneBrowser.resultCount===1")
                page.keyboard.press('Escape')
                page.wait_for_function("window.chartSceneBrowser.searchDismissed && !window.chartSceneBrowser.navigationOpen && window.chartScene.controls['navigation-panel']===null")
                page.set_viewport_size({'width':width,'height':height})
                page.wait_for_function('w=>window.chartScene.width===w',arg=width)
                if state()['controls'].get('inspector-panel'):
                    click('info-toggle')
                    page.wait_for_function("window.chartScene.controls['inspector-panel']===null")
                page.wait_for_function('w=>window.chartScene.controls.chart?.width>=w-40',arg=width)
                # Each observation is an assertion, not a retry of a failed dismissal.
                for sample in range(3):
                    page.wait_for_timeout(100)
                    observed={'width':width,'sample':sample,'browser':chrome(),'navigation':state()['controls']['navigation-panel']}
                    observations.append(observed)
                    assert observed['browser']['searchDismissed'] and not observed['browser']['navigationOpen'] and observed['navigation'] is None,observed
                click('search')
                page.wait_for_function("window.chartSceneBrowser.navigationOpen && !window.chartSceneBrowser.searchDismissed && window.chartScene.controls['navigation-panel']!==null")
                page.keyboard.press('Escape')
                page.wait_for_function("window.chartSceneBrowser.searchDismissed && window.chartScene.controls['navigation-panel']===null")
            assert chrome()['modelLoads']==loads and state()['exampleId']==example
            (out/'escape-resize-observations.json').write_text(json.dumps(observations,indent=2))
        test('Escape dismissal survives resize and explicit pointer reactivation',durable_dismissal)
        def keyboard_search_focus():
            loads=chrome()['modelLoads']; path=[]
            focus=lambda:page.evaluate('window.chartSceneFocus || {}')
            # Establish focus through real pointer input. The prior one-key fixture did
            # not establish its initial focus or observe the native traversal order.
            page.mouse.click(*point())
            page.wait_for_function("window.chartSceneFocus?.control==='chart'")
            visited={'chart'}
            for step in range(32):
                before=focus()
                page.keyboard.press('Shift+Tab')
                page.wait_for_function("p=>window.chartSceneFocus?.sequence>p.sequence && window.chartSceneFocus.control!==p.control",arg=before)
                observed={'focus':focus(),'browser':chrome()};path.append(observed)
                (out/'keyboard-focus-path.json').write_text(json.dumps(path,indent=2))
                identity=observed['focus'].get('control')
                if identity=='search': break
                assert identity and identity not in visited,('Focus trapped or left registered controls',path)
                visited.add(identity)
            else: raise AssertionError(('Search is not reachable by keyboard traversal',path))
            page.wait_for_function('window.chartSceneBrowser.searchFocused && window.chartSceneBrowser.navigationOpen && !window.chartSceneBrowser.searchDismissed')
            page.keyboard.insert_text('FifoCapacity')
            page.wait_for_function('window.chartSceneBrowser.resultCount===1')
            page.keyboard.press('Escape')
            page.wait_for_function("window.chartSceneBrowser.searchDismissed && window.chartScene.controls['navigation-panel']===null")
            assert chrome()['modelLoads']==loads
        test('keyboard traversal reopens dismissed search without loading a model',keyboard_search_focus)
        from range_inspector_cases import register
        register(test,page,inputs,module,out)
        def clean():assert not [entry for entry in logs if entry['type']=='pageerror'],logs
        test('examples workbench has no unhandled browser errors',clean)
        (out/'final-state.json').write_text(json.dumps({'chart':state(),'browser':chrome()},indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} workbench UI checks passed',flush=True)
raise SystemExit(1 if failed else 0)
