"""Real-input width-grip and responsive-inspector cases, called by validate-text.py."""
from __future__ import annotations
import math

def run_resize_cases(page,out,state,ready,command,choose,enter,test,pixels,near):
    def canonical(text):return text.replace('\r\n','\n').replace('\r','\n')
    def grip():
        page.wait_for_function('window.chartSceneText.ready&&window.chartSceneText.resize.grip!==null')
        return state()['resize']['grip']
    def drag(local_dx,local_dy=0,*,cancel=False):
        before=state();p=grip();angle=before['rotation']*math.pi/180
        x=p['x']+local_dx*math.cos(angle)-local_dy*math.sin(angle)
        y=p['y']+local_dx*math.sin(angle)+local_dy*math.cos(angle)
        sequence=page.evaluate('window.chartSceneInput?.sequence||0')
        page.mouse.move(p['x'],p['y']);page.mouse.down()
        page.wait_for_function('window.chartSceneText.resize.active')
        if local_dx or local_dy:
            page.mouse.move(x,y,steps=6)
            page.wait_for_function('s=>window.chartSceneText.wrapping.width!==s.wrapping.width',arg=before)
        if cancel:page.keyboard.press('Escape')
        page.mouse.up();page.wait_for_function('!window.chartSceneText.resize.active');ready()
        if cancel:return before
        # Verify against the actual routed pointer positions, not assumptions about
        # browser-to-Uno fractional-coordinate quantization or the resulting model width.
        events=page.evaluate('window.chartSceneInputEvents||[]')
        delivered=[e for e in events if e['sequence']>sequence and e['target']=='text-chart']
        down=next(e for e in delivered if e['kind']=='down')
        up=next(e for e in reversed(delivered) if e['kind']=='up')
        delta=(up['x']-down['x'])*math.cos(angle)+(up['y']-down['y'])*math.sin(angle)
        near(state()['wrapping']['width'],before['wrapping']['width']+delta*before['resize']['widthPerPixel'])
        assert state()['anchor']==before['anchor'] and state()['text']==before['text']
        assert state()['fontSize']==before['fontSize']
        return before

    def selected_grip():
        choose('wrap',1,'wrapping')
        if not state()['selected']:command('selection')
        assert grip() and state()['wrapping']['mode']=='Wrap'
        pixels('text-selected-width-grip')
    test('selected wrapped annotation exposes a painted width grip',selected_grip)

    def width_history():
        before=drag(43);resized=state()['wrapping']['width']
        near(state()['body']['width'],resized+16)
        near(state()['wrapping']['selectedWidth'],resized)
        assert state()['wrapping']['wrapWidthChoice']['selectedIndex']>=0
        pixels('text-resized-width')
        command('undo');near(state()['wrapping']['width'],before['wrapping']['width']);assert state()['body']==before['body']
        command('redo');near(state()['wrapping']['width'],resized);command('undo')
    test('native width drag displays custom width and shares exact undo redo',width_history)

    def rotated_draft():
        command('horizontal');command('rotate')
        assert state()['horizontal']=='Center' and state()['rotation']==30
        draft='Independent draft survives a rotated resize'
        enter('text',draft);before=drag(24,13)
        assert canonical(state()['editorText'])==draft and state()['hasDraft']
        pixels('text-resized-rotated')
        command('undo');near(state()['wrapping']['width'],before['wrapping']['width'])
        command('undo');command('undo');command('revert')
    test('rotated center anchored width drag preserves native text draft',rotated_draft)

    def cancel_resize():
        before=drag(37,cancel=True)
        near(state()['wrapping']['width'],before['wrapping']['width']);assert state()['anchor']==before['anchor']
        assert state()['body']==before['body'] and not state()['resize']['active']
    test('Escape rolls back a live native width preview',cancel_resize)

    def stationary():
        command('bold');command('undo');assert state()['canRedo']
        before=drag(0);near(state()['wrapping']['width'],before['wrapping']['width']);assert state()['canRedo']
    test('stationary width grip click preserves the existing redo branch',stationary)

    def double_grip():
        before=state();p=grip();sequence=page.evaluate('window.chartSceneInput?.sequence||0')
        page.mouse.dblclick(p['x'],p['y'],delay=40)
        page.wait_for_function("s=>(window.chartSceneInputEvents||[]).some(e=>e.sequence>s&&e.target==='text-chart'&&e.kind==='doubletap')",arg=sequence)
        page.wait_for_function('!window.chartSceneText.resize.active');ready()
        assert not state()['dialog'] and state()['body']==before['body'] and state()['anchor']==before['anchor']
        near(state()['wrapping']['width'],before['wrapping']['width'])
    test('native width grip double tap does not open body editor or zoom',double_grip)

    def compact_properties():
        original=state()['text'];draft='Compact inspector keeps this draft'
        page.set_viewport_size({'width':920,'height':740})
        page.wait_for_function('!window.chartSceneText.controls.inspector');ready();chart=state()['controls']['chart']
        command('properties');page.wait_for_function('window.chartSceneText.controls.inspector!==null');ready()
        assert state()['resize']['floatingInspector'] and state()['resize']['inspectorOpen']
        assert state()['controls']['chart']==chart
        enter('text',draft);choose('weight',3);assert state()['typography']['weight']==600
        command('undo');assert canonical(state()['editorText'])==draft and state()['text']==original
        panel=state()['controls']['inspector'];page.mouse.move(panel['x']+panel['width']/2,panel['y']+panel['height']/2)
        page.mouse.wheel(0,450)
        page.wait_for_function('()=>{const c=window.chartSceneText.controls.wrapWidth,p=window.chartSceneText.controls.inspector;return c&&c.y>=p.y&&c.y+c.height<p.y+p.height}')
        choose('wrapWidth',3,'wrapping');near(state()['wrapping']['width'],240);command('undo')
        # The pane header stays reachable while the property contents are scrolled.
        command('close-properties');page.wait_for_function('!window.chartSceneText.controls.inspector')
        assert canonical(state()['editorText'])==draft and state()['controls']['chart']==chart
        command('properties');page.wait_for_function('window.chartSceneText.controls.inspector!==null')
        page.mouse.move(panel['x']+panel['width']/2,panel['y']+panel['height']/2);page.mouse.wheel(0,-450)
        page.wait_for_function('()=>{const c=window.chartSceneText.controls.text,p=window.chartSceneText.controls.inspector;return c&&c.y>=p.y&&c.y+c.height<p.y+p.height}')
        enter('text',draft);page.keyboard.press('Escape');page.wait_for_function('!window.chartSceneText.controls.inspector')
        page.set_viewport_size({'width':1440,'height':1000});page.wait_for_function('window.chartSceneText.controls.inspector!==null');ready()
        assert canonical(state()['editorText'])==draft and state()['text']==original
        command('revert');pixels('text-properties-restored')
    test('compact native properties stay reachable without resizing the chart',compact_properties)

    def desktop_pane():
        before=state()['text'];command('properties');page.wait_for_function('!window.chartSceneText.controls.inspector');ready()
        pixels('text-properties-hidden');command('properties');page.wait_for_function('window.chartSceneText.controls.inspector!==null');ready()
        assert state()['text']==before and not state()['resize']['floatingInspector']
        command('undo') # undo the wrapping setup, after undoing each resize and formatting operation
        assert state()['wrapping']['mode']=='NoWrap'
        if state()['selected']:command('selection')
    test('desktop properties toggle retains the annotation and restores automatic compact mode',desktop_pane)
