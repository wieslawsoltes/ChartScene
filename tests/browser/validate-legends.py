"""Exercise actual Uno legend checkboxes, templates, placement and shared history."""
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
parser.add_argument('--output', default='artifacts/browser-tests-legends')
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
    def state(): return page.evaluate('window.chartSceneLegends || {}')
    def ready():
        page.wait_for_function("()=>{const s=window.chartSceneLegends;if(s?.error)throw new Error(s.error);return s?.ready&&!document.querySelector('#loading.uno-persistent-loader')}", timeout=120_000)
    def command(name):
        before = state()['actions']
        inputs.click('legends-'+name, state()['controls'][name], resolve=lambda: state()['controls'].get(name))
        page.wait_for_function('p=>window.chartSceneLegends.actions>p.before&&window.chartSceneLegends.lastAction===p.name', arg={'before':before,'name':name})
        ready()
    def entries(): return {e['id']:e for e in state()['entries']}
    def identities(): return {e['id']:e['identity'] for e in state()['entries']}
    def visible(identifier, value):
        page.wait_for_function('p=>window.chartSceneLegends.series.find(s=>s.id===p.id)?.visible===p.value&&window.chartSceneLegends.ready',arg={'id':identifier,'value':value})
    def toggle(identifier):
        # Model readiness can precede reattachment/layout of retained native rows.
        # Observe the actual checkbox bounds before issuing one input action; never
        # force layout or click an old rectangle from a filtered-out generation.
        page.wait_for_function("""id => {
            const s = window.chartSceneLegends;
            if (s?.error) throw new Error(s.error);
            const row = s?.entries.find(e => e.id === id);
            return s?.ready && row?.checkbox?.width > 0 && row.checkbox.height > 0;
        }""", arg=identifier)
        row = entries()[identifier]; old = row['visible']; b = row['checkbox']
        inputs.click('legends-legend', b, resolve=lambda: entries().get(identifier, {}).get('checkbox'))
        visible(identifier, not old)
    def module(name):
        key='gallery-'+name
        resolve=lambda: page.evaluate('key=>window.chartSceneGallery.controls[key]',key)
        inputs.click(key,resolve(),resolve=resolve)
        page.wait_for_function('name=>window.chartSceneGallery.view===name',arg=name)
    def pixels(filename):
        page.screenshot(path=str(out/(filename+'.png')))
        b=state()['controls']['legend']
        with Image.open(out/(filename+'.png')) as im:
            crop=im.convert('RGB').crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
            colored=sum(max(c)-min(c)>45 and max(c)>100 for c in crop.getdata())
            assert colored>15, colored
    def test(name, action):
        started=time.perf_counter()
        try:
            action(); assert not state().get('error'),state()
            results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000});print('PASS',name,flush=True)
        except Exception as error:
            results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc()}); print('FAIL',name,error,flush=True)
            page.screenshot(path=str(out/(name.replace(' ','-')+'-failure.png')))
            (out/(name.replace(' ','-')+'-state.json')).write_text(json.dumps(state(),indent=2))
        finally:
            (out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
            (out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?view=legends&inputTrace=1',wait_until='domcontentloaded')
        test('native legends startup',ready)
        if not state().get('ready'): raise RuntimeError('Legend gallery did not start')
        test('legend series swatches produce visible pixels',lambda:pixels('legend-right-dark'))
        def visibility():
            before=identities();rows=state()['rowsCreated'];toggle('sine');assert state()['undoEnabled'];command('undo');visible('sine',True);assert state()['redoEnabled']
            command('redo');visible('sine',False);command('undo');visible('sine',True)
            assert identities()==before and state()['rowsCreated']==rows
        test('native visibility checkboxes share chart undo redo',visibility)
        def keyboard():
            toggle('sine');page.keyboard.press('Space');visible('sine',True)
        test('focused native legend checkbox responds to Space',keyboard)
        def filters():
            before=identities();rows=state()['rowsCreated'];command('select')
            command('filter');assert state()['filter']=='Visible' and len(entries())==4
            command('filter');assert set(entries())=={'cosine','area'}
            command('filter');assert identities()==before and state()['rowsCreated']==rows
            command('select')
        test('legend filters preserve entry and native row identities',filters)
        def inclusion():
            before=identities();rows=state()['rowsCreated'];command('exclude');assert 'cosine' not in entries()
            assert next(s for s in state()['series'] if s['id']=='cosine')['visible']
            command('exclude');assert identities()==before and state()['rowsCreated']==rows
        test('legend inclusion does not hide plotted data',inclusion)
        def style():
            before=identities();rows=state()['rowsCreated'];passes=state()['membershipPasses'];command('style')
            assert entries()['sine']['name']=='Sine wave / updated';assert identities()==before
            assert state()['rowsCreated']==rows and state()['membershipPasses']==passes;pixels('legend-restyled')
        test('live metadata and markers refresh without rebuilding rows',style)
        def reorder():
            before=identities();rows=state()['rowsCreated'];command('reorder')
            assert [e['id'] for e in state()['entries']]==['cosine','area','scatter','sine']
            assert identities()==before and state()['rowsCreated']==rows
        test('legend order follows series collection moves',reorder)
        def membership():
            before=identities();command('remove');assert 'scatter' not in entries()
            command('remove');after=identities();assert after['scatter']!=before['scatter']
            assert all(after[k]==v for k,v in before.items() if k!='scatter')
        test('removed series detach and reinsert with a fresh entry',membership)
        def filtered_removal():
            command('filter');command('filter');assert state()['filter']=='Selected'
            assert 'scatter' not in entries();before=state()['retainedRows']
            command('remove');assert state()['retainedRows']==before-1
            command('remove');assert state()['retainedRows']==before-1
            command('filter');assert 'scatter' in entries() and state()['retainedRows']==before
        test('removed filtered series release retained native rows',filtered_removal)
        def append():
            before=state();command('append');after=state()
            assert after['appends']==before['appends']+1
            assert after['rowsCreated']==before['rowsCreated'] and after['membershipPasses']==before['membershipPasses']
            assert [s['points'] for s in after['series']]==[s['points']+100 for s in before['series']]
        test('data appends never rebuild legend membership or controls',append)
        def placements():
            before=identities();rows=state()['rowsCreated']
            for placement in ['Bottom','Left','Top','Inside','Right']:
                command('placement');assert state()['placement']==placement
                page.wait_for_timeout(120)
                s=state();c=s['controls']['chart'];l=s['controls']['legend']
                if placement=='Bottom':assert l['y']>=c['y']+c['height']
                elif placement=='Top':assert l['y']+l['height']<=c['y']
                elif placement=='Left':assert l['x']+l['width']<=c['x']
                elif placement=='Right':assert l['x']>=c['x']+c['width']
                else:assert c['x']<=l['x']<c['x']+c['width'] and c['y']<=l['y']<c['y']+c['height']
                pixels('legend-'+placement.lower())
            assert identities()==before and state()['rowsCreated']==rows
        test('legend supports all five placements with retained rows',placements)
        def options():
            command('checks');assert not state()['checkboxes'] and all(e['checkbox'] is None for e in state()['entries'])
            command('markers');assert not state()['markers'];command('checks');command('markers')
            assert state()['checkboxes'] and state()['markers']
        test('legend marker and visibility control options',options)
        def templates():
            before=identities();command('rowTemplate');assert state()['rowTemplate']
            page.wait_for_function('window.chartSceneLegends.entries.every(e=>e.checkbox)');toggle('sine');toggle('sine');pixels('legend-custom-row')
            command('rowTemplate');command('markerTemplate');assert state()['markerTemplate'];pixels('legend-custom-marker')
            command('markerTemplate');assert identities()==before
        test('compiled XAML row and marker templates are interactive',templates)
        def theme():
            before=identities();rows=state()['rowsCreated'];command('theme');assert state()['theme']=='Light';pixels('legend-light')
            command('theme');assert state()['theme']=='Dark';assert identities()==before and state()['rowsCreated']==rows
        test('legend and chart chrome switch light and dark together',theme)
        def replacement():
            before=state();command('replace');assert state()['generation']==before['generation']+1
            assert not set(e['identity'] for e in before['entries'])&set(identities().values())
            snapshot=state();command('old');after=state()
            assert after['oldEdits']==snapshot['oldEdits']+1
            assert after['entries']==snapshot['entries'] and after['rowsCreated']==snapshot['rowsCreated']
            assert after['membershipPasses']==snapshot['membershipPasses']
        test('replacement model detaches live old-model notifications',replacement)
        def empty():
            before = identities(); created = state()['rowsCreated']
            for cycle in range(3):
                command('filter');assert state()['filter']=='Visible'
                for identifier in list(entries()):toggle(identifier)
                assert state()['entries']==[] and state()['ready']
                command('filter');command('filter');assert len(entries())==4
                for identifier in list(entries()):toggle(identifier)
                assert all(e['visible'] for e in state()['entries'])
                assert identities()==before and state()['rowsCreated']==created
        test('empty filtered legends remain valid and recover',empty)
        def reattach():
            before=state(); assert all(e['visible'] for e in before['entries'])
            module('linked');module('legends');ready();after=state()
            assert after['generation']==before['generation'] and after['series']==before['series']
            toggle('sine');command('undo');visible('sine',True)
        test('legend unload reload restores bindings and history',reattach)
        def resize():
            page.set_viewport_size({'width':920,'height':740});page.wait_for_timeout(300);ready()
            c=state()['controls']['chart'];l=state()['controls']['legend'];assert c['width']>400 and l['x']+l['width']<=920
            pixels('legend-compact');page.set_viewport_size({'width':1440,'height':1000})
        test('legend responsive sidebar keeps chart and rows reachable',resize)
        def typography():
            before=state(); identity=identities(); width=before['controls']['legend']['width']
            command('text')
            page.wait_for_function("""width => {
                const s=window.chartSceneLegends,t=s.typography;
                return t?.fontSize===18 && t.fontWeight===700 && t.fontStyle==='Italic'
                    && t.foreground==='#FFF09B4B' && s.controls.legend.width>width;
            }""",arg=width)
            after=state(); assert identities()==identity and after['rowsCreated']==before['rowsCreated']
            assert after['membershipPasses']==before['membershipPasses']
            assert after['markerInvalidations']==before['markerInvalidations']
            page.screenshot(path=str(out/'legend-typography.png'))
            command('text')
            page.wait_for_function("window.chartSceneLegends.typography?.fontSize===12 && window.chartSceneLegends.typography.fontStyle==='Normal' && window.chartSceneLegends.typography.fontWeight===400")
        test('legend fonts and foreground style native text without marker invalidation',typography)
        def large():
            before=state(); command('many')
            page.wait_for_function('window.chartSceneLegends.entries.length===256 && window.chartSceneLegends.ready')
            after=state()
            assert after['rowsCreated']-before['rowsCreated']==256
            assert after['rowSynchronizations']-before['rowSynchronizations']<=2, (before,after)
            assert after['retainedRows']==256
            page.screenshot(path=str(out/'legend-256-channels.png'))
            command('many'); assert len(entries())==4 and state()['retainedRows']==4
        test('large legend binding synchronizes native rows once per settled projection',large)
        def clean():assert not [e for e in logs if e['type']=='pageerror'],logs
        test('legend gallery has no unhandled errors',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
    finally:
        (out/'console.json').write_text(json.dumps(logs,indent=2));browser.close()
failed=sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} legend browser checks passed',flush=True)
raise SystemExit(1 if failed else 0)
