"""Text annotations in the compiled Uno app; observation plus real native input only."""
from __future__ import annotations
import argparse,json,math,time,traceback
from pathlib import Path
from PIL import Image
from playwright.sync_api import sync_playwright
from uno_input import UnoInput
parser=argparse.ArgumentParser();parser.add_argument('--url',default='http://127.0.0.1:8765/ChartScene/');parser.add_argument('--output',default='artifacts/browser-tests-text');args=parser.parse_args()
out=Path(args.output);out.mkdir(parents=True,exist_ok=True);results=[];logs=[]
with sync_playwright() as p:
    browser=p.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
    page=browser.new_page(viewport={'width':1440,'height':1000});page.set_default_timeout(20000);inputs=UnoInput(page,out)
    page.on('pageerror',lambda e:logs.append({'type':'pageerror','text':str(e)}));page.on('console',lambda e:logs.append({'type':e.type,'text':e.text}))
    def state():return page.evaluate('window.chartSceneText||{}')
    def ready():page.wait_for_function("()=>{const s=window.chartSceneText;if(s?.error)throw new Error(s.error);return s?.ready&&!document.querySelector('#loading.uno-persistent-loader')}",timeout=120000)
    def click(name):
        page.wait_for_function('n=>!!window.chartSceneText.controls[n]',arg=name)
        inputs.click('text-'+name,state()['controls'][name],resolve=lambda:state()['controls'].get(name))
    def command(name):
        before=state()['actions'];click(name)
        page.wait_for_function('p=>window.chartSceneText.actions>p.before&&window.chartSceneText.lastAction===p.name',arg={'before':before,'name':name});ready()
    def choose(name,index,section='typography'):
        field=name+'Choice';key={'section':section,'field':field}
        if not state()[section][field]['open']:click(name)
        page.wait_for_function('k=>{const c=window.chartSceneText[k.section][k.field];return c.open&&c.focusedIndex>=0}',arg=key)
        page.keyboard.press('Home');page.wait_for_function('k=>window.chartSceneText[k.section][k.field].focusedIndex===0',arg=key)
        for position in range(index):
            page.keyboard.press('ArrowDown')
            page.wait_for_function('k=>window.chartSceneText[k.section][k.field].focusedIndex===k.index',arg={**key,'index':position+1})
        page.keyboard.press('Enter')
        page.wait_for_function('k=>{const c=window.chartSceneText[k.section][k.field];return !c.open&&c.selectedIndex===k.index}',arg={**key,'index':index});ready()
    def canonical(text):return text.replace('\r\n','\n').replace('\r','\n')
    def near(a,b):assert abs(a-b)<1e-7,(a,b)
    def pixels(name):
        ready();page.screenshot(path=str(out/(name+'.png')));b=state()['body'];assert b
        with Image.open(out/(name+'.png')) as im:
            crop=im.convert('RGB').crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
            assert sum(max(c)-min(c)>45 and max(c)>100 for c in crop.getdata())>30
    def body():
        s=state();c=s['corners'];return sum(q['x'] for q in c)/4,sum(q['y'] for q in c)/4
    def enter(name,text):click(name);page.keyboard.press('Control+a');page.keyboard.insert_text(text)
    def test(name,action):
        started=time.perf_counter()
        try:action();assert not state().get('error');results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000});print('PASS',name,flush=True)
        except Exception as e:
            page.mouse.up();results.append({'name':name,'passed':False,'error':str(e),'trace':traceback.format_exc()});print('FAIL',name,e,flush=True)
            page.screenshot(path=str(out/(name.replace(' ','-')+'-failure.png')));(out/(name.replace(' ','-')+'-state.json')).write_text(json.dumps(state(),indent=2))
        finally:(out/'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not t['passed'] for t in results),'tests':results},indent=2));(out/'console.json').write_text(json.dumps(logs,indent=2))
    try:
        page.goto(args.url+'?view=text&inputTrace=1',wait_until='domcontentloaded');test('native annotation page starts without input',ready)
        test('rendered annotation text contains visible glyph pixels',lambda:pixels('text-dark'))
        def unchanged():
            before=state()
            for _ in range(3):command('apply')
            assert state()['text']==before['text'] and not state()['canUndo']
            assert canonical(state()['editorText'])==before['text']
        test('unchanged native multiline input creates no phantom history',unchanged)
        def anchors():
            start=state()['anchor']
            for h in ['Center','Right','Left']:
                command('horizontal');s=state();assert s['horizontal']==h
                near(s['anchor']['x'],start['x']);near(s['body']['x'],start['x']-(s['body']['width']/2 if h=='Center' else s['body']['width'] if h=='Right' else 0))
            for v in ['Center','Bottom','Top']:
                command('vertical');s=state();assert s['vertical']==v
                near(s['anchor']['y'],start['y']);near(s['body']['y'],start['y']-(s['body']['height']/2 if v=='Center' else s['body']['height'] if v=='Bottom' else 0))
        test('native anchor options align to a stable control point',anchors)
        def rotation():
            before=state();a=before['anchor'];corners=before['corners']
            for step in range(1,13):
                command('rotate');s=state();angle=(step*30)%360;near(s['rotation'],angle);t=angle*math.pi/180
                for original,actual in zip(corners,s['corners']):
                    x=original['x']-a['x'];y=original['y']-a['y'];near(actual['x'],a['x']+x*math.cos(t)-y*math.sin(t));near(actual['y'],a['y']+x*math.sin(t)+y*math.cos(t))
                if step==1:pixels('text-rotated')
        test('rotated label corners use the anchor as pivot',rotation)
        def typography():
            before=state()['body'];command('bold');assert state()['bold'];command('font');assert state()['fontSize']==24 and state()['body']['width']>before['width']
            assert state()['typography']['input']['fontSize']==24
            command('undo');near(state()['fontSize'],20);assert state()['typography']['input']['fontSize']==20;command('undo');assert not state()['bold']
            for align in ['Center','Right','Left']:
                command('align');assert state()['alignment']==align and state()['typography']['input']['alignment']==align
        test('font size weight and multiline alignment are live and undoable',typography)
        def family_choice():
            choose('family',1);assert state()['typography']['family']=='Georgia';pixels('text-georgia')
            command('undo');assert state()['typography']['family']=='Arial' and state()['typography']['familyChoice']['selectedIndex']==0
            command('redo');assert state()['typography']['family']=='Georgia';command('undo')
        test('native font family selection renders and synchronizes undo redo',family_choice)
        def weights():
            choose('weight',3);assert state()['typography']['weight']==600 and not state()['bold']
            command('bold');assert state()['typography']['weight']==700
            command('undo');assert state()['typography']['weight']==600 and state()['typography']['weightChoice']['selectedIndex']==3
            command('undo');assert state()['typography']['weight']==400
        test('bold toolbar undo restores the exact custom weight',weights)
        def slants():
            choose('slant',2);assert state()['typography']['slant']=='Oblique'
            command('italic');assert state()['typography']['slant']=='Italic';pixels('text-italic')
            command('undo');assert state()['typography']['slant']=='Oblique'
            command('undo');assert state()['typography']['slant']=='Normal'
        test('native slant choices and italic command share precise history',slants)
        def typography_draft():
            original=state()['text'];draft='Keep this typography draft'
            enter('text',draft);choose('weight',4);choose('slant',1)
            assert canonical(state()['editorText'])==draft and state()['text']==original and state()['hasDraft']
            command('undo');command('undo');assert canonical(state()['editorText'])==draft
            command('revert');assert not state()['hasDraft']
        test('font controls preserve separate unapplied annotation text',typography_draft)
        def soft_wrap():
            before=state();assert not before['wrapping']['widthEnabled']
            choose('wrap',1,'wrapping');s=state()
            assert s['wrapping']['mode']=='Wrap' and s['wrapping']['widthEnabled'] and s['text']==before['text']
            assert s['lines']>before['lines'];near(s['body']['width'],176);assert s['anchor']==before['anchor'];pixels('text-wrapped')
            command('undo');assert state()['wrapping']['mode']=='NoWrap';assert state()['body']==before['body']
            command('redo');near(state()['body']['width'],176);command('undo')
        test('native wrapping preserves source anchor and exact undo redo',soft_wrap)
        def wrap_width():
            choose('wrap',1,'wrapping');before=state();choose('wrapWidth',0,'wrapping');s=state()
            near(s['wrapping']['width'],80);near(s['body']['width'],96);assert s['lines']>before['lines']
            command('rotate');assert state()['lines']==s['lines'] and state()['anchor']==s['anchor'];pixels('text-wrapped-rotated')
            command('undo');command('undo');near(state()['wrapping']['width'],160);command('undo')
        test('native wrap width relayout survives rotation and undo',wrap_width)
        def wrap_overflow():
            original=state()['text'];text='Supercalifragilisticexpialidocious sample'
            enter('text',text);command('apply');choose('wrap',2,'wrapping');choose('wrapWidth',0,'wrapping')
            assert state()['wrapping']['mode']=='WrapWithOverflow' and state()['body']['width']>96 and state()['lines']==2
            choose('wrap',1,'wrapping');near(state()['body']['width'],96);assert state()['lines']>2 and state()['text']==text
            command('undo');command('undo');command('undo');command('undo');assert state()['text']==original and state()['wrapping']['mode']=='NoWrap'
        test('native overflow mode keeps words while grapheme wrapping fits them',wrap_overflow)
        def wrap_draft():
            original=state()['text'];draft='Keep my wrap settings draft'
            enter('text',draft);choose('wrap',1,'wrapping');choose('wrapWidth',1,'wrapping')
            assert state()['text']==original and canonical(state()['editorText'])==draft and state()['hasDraft']
            command('undo');command('undo');assert canonical(state()['editorText'])==draft
            command('revert');assert not state()['hasDraft']
        test('wrapping and width history preserve an independent native draft',wrap_draft)
        def text_edit():
            original=state()['text'];enter('text','First line\nSecond line\nThird line');command('apply');assert state()['text']=='First line\nSecond line\nThird line' and state()['lines']==3
            command('undo');assert state()['text']==original and canonical(state()['editorText'])==original;command('redo');assert state()['lines']==3 and canonical(state()['editorText'])==state()['text'];command('undo')
        test('native multiline text input applies undo and redo',text_edit)
        def empty():
            original=state()['text'];enter('text','');command('apply');assert state()['body'] is None and state()['lines']==0
            command('undo');assert state()['text']==original and state()['body'] is not None
        test('empty labels release hit geometry and restore through undo',empty)
        def drag():
            before=state();x,y=body();page.mouse.move(x,y);page.mouse.down();page.wait_for_function('window.chartSceneText.selected===true')
            page.mouse.move(x+40,y+20,steps=6);page.mouse.up();page.wait_for_function('s=>window.chartSceneText.ready&&window.chartSceneText.x1!==s.x1',arg=before)
            assert state()['canUndo'];command('undo');near(state()['x1'],before['x1']);near(state()['y1'],before['y1'])
        test('dragging measured body away from anchor moves the annotation',drag)
        def dialog():
            x,y=body();original=state()['text'];page.mouse.dblclick(x,y,delay=40);page.wait_for_function('window.chartSceneText.dialog===true');assert state()['typography']['dialogInput']['fontSize']==state()['fontSize'];assert state()['typography']['dialogInput']['alignment']==state()['alignment'];enter('dialog-text','Edited with native dialog')
            click('dialog-apply');page.wait_for_function('!window.chartSceneText.dialog&&window.chartSceneText.text==="Edited with native dialog"');ready();command('undo');assert state()['text']==original
        test('double click opens native editor instead of zoom extents',dialog)
        def cancel_dialog():
            x,y=body();original=state()['text'];page.mouse.dblclick(x,y,delay=40);page.wait_for_function('window.chartSceneText.dialog===true');enter('dialog-text','Must not apply');page.keyboard.press('Escape');page.wait_for_function('window.chartSceneText.dialog===false');assert state()['text']==original
        test('native annotation dialog cancellation preserves text',cancel_dialog)
        def preserve_draft():
            original=state()['text'];draft='Unapplied inspector draft\nKeep both lines'
            enter('text',draft);page.wait_for_function('window.chartSceneText.hasDraft&&window.chartSceneText.revertEnabled')
            command('bold');command('undo');assert canonical(state()['editorText'])==draft and state()['text']==original
            x,y=body();page.mouse.dblclick(x,y,delay=40);page.wait_for_function('window.chartSceneText.dialog===true')
            enter('dialog-text','Discard this dialog edit');page.keyboard.press('Escape')
            page.wait_for_function('window.chartSceneText.dialog===false');ready()
            assert canonical(state()['editorText'])==draft and state()['text']==original
            assert state()['hasDraft'] and state()['revertEnabled']
            pixels('text-unapplied-draft')
            command('revert');assert canonical(state()['editorText'])==original and not state()['hasDraft'] and not state()['revertEnabled']
        test('cancelled dialog and style changes preserve unapplied inspector text',preserve_draft)
        def revert_history():
            original=state()['text'];command('bold');command('undo');assert state()['canRedo']
            enter('text','Discard only the draft');page.wait_for_function('window.chartSceneText.revertEnabled')
            command('revert');assert state()['text']==original and canonical(state()['editorText'])==original and state()['canRedo']
            command('redo');assert state()['bold'];command('undo');assert not state()['bold']
        test('native draft revert preserves the existing redo branch',revert_history)
        def background():
            before=state()['body'];command('background');assert state()['background'];pixels('text-panel');command('undo');assert not state()['background'];assert state()['body']==before
        test('optional text panel does not change label layout',background)
        def flip():
            before=state()['anchor'];command('flip');assert state()['anchor']==before;command('undo')
        test('relative labels stay anchored while data axis flips',flip)
        def theme():
            command('theme');assert state()['theme']=='Light';pixels('text-light');command('theme');assert state()['theme']=='Dark'
        test('chart text and native inspector switch light and dark together',theme)
        def compact():
            page.set_viewport_size({'width':920,'height':740});page.wait_for_function('!window.chartSceneText.controls.inspector&&window.chartSceneText.controls.chart.width<930');ready();assert state()['body']['x']+state()['body']['width']<920;pixels('text-compact');page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartSceneText.controls.inspector!==null');ready()
        test('compact label gallery retains chart and reachable toolbar',compact)
        def reattach():
            before=state()['text'];command('bold')
            for name in ['linked','text']:
                key='gallery-'+name;resolve=lambda key=key:page.evaluate('k=>window.chartSceneGallery.controls[k]',key);inputs.click(key,resolve(),resolve=resolve);page.wait_for_function('n=>window.chartSceneGallery.view===n',arg=name)
            ready();assert state()['text']==before and state()['bold'];command('undo');assert not state()['bold']
        test('annotation view reattachment preserves model and history',reattach)
        def idle():
            before=state()['frames'];page.wait_for_timeout(1100);assert state()['frames']-before<=3
        test('idle annotation diagnostics do not continuously redraw chart',idle)
        def clean():assert not [e for e in logs if e['type']=='pageerror' or 'FRAMEWORK_FATAL' in e['text']],logs
        test('annotation gallery has no unhandled errors',clean)
        (out/'final-state.json').write_text(json.dumps(state(),indent=2))
    finally:browser.close()
failed=sum(not t['passed'] for t in results);print(f'{len(results)-failed}/{len(results)} annotation browser checks passed',flush=True);raise SystemExit(bool(failed))
