"""Range drafts and the native properties pane: actual mouse/keyboard input, read-only observations."""
from __future__ import annotations


def register(test, page, inputs, module, output):
    def state(): return inputs.state()
    def browser(): return page.evaluate('window.chartSceneBrowser')
    def draft(): return browser()['rangeEditor']
    def settled():
        page.wait_for_function('''()=>{const s=window.chartScene,d=window.chartSceneBrowser?.rangeEditor;
            return s?.ready&&d&&!d.dirty&&Number(d.xMin)===s.ranges.x.min&&Number(d.xMax)===s.ranges.x.max&&
                Number(d.yMin)===s.ranges.y.min&&Number(d.yMax)===s.ranges.y.max;}''')
    def choose(identifier, query):
        inputs.click('search'); page.keyboard.press('Control+a'); page.keyboard.insert_text(query)
        page.wait_for_function('id=>!!window.chartScene.controls["example:"+id]', arg=identifier)
        inputs.click('example:'+identifier)
        page.wait_for_function('id=>window.chartScene.exampleId===id&&window.chartScene.ready', arg=identifier)
    def show():
        if not draft()['inspectorOpen']: inputs.click('info-toggle')
        page.wait_for_function('window.chartSceneBrowser.rangeEditor.inspectorOpen&&!!window.chartScene.controls["inspector-panel"]')
    def reveal(name):
        # Navigate the native ScrollViewer, not DOM scroll/state injection. A clipped child
        # still has a diagnostic rectangle, so acknowledge the scroller's visible viewport.
        for _ in range(12):
            s=state(); b=s['controls'][name]; v=s['controls']['properties-scroll']
            if b and v and b['y']>=v['y'] and b['y']+b['height']<=v['y']+v['height']:
                return
            assert b and v, (name, b, v)
            page.mouse.move(v['x']+v['width']/2, v['y']+v['height']/2)
            page.mouse.wheel(0, 160 if b['y']>=v['y'] else -160)
            page.wait_for_function('p=>window.chartScene.controls[p.name]?.y!==p.y', arg={'name':name,'y':b['y']})
        raise AssertionError(('Range control cannot be reached by scrolling',name,state()))
    def click(name):
        reveal(name); inputs.click(name)
        if name in ('x-min','x-max','y-min','y-max'):
            # Pointer dispatch is not keyboard-focus completion, particularly while
            # streaming. Observe actual managed focus before delivering any keys.
            page.wait_for_function('id=>window.chartSceneFocus?.control===id',arg=name)
    def enter(name, value):
        click(name); page.keyboard.press('Control+a'); page.keyboard.insert_text(value)
        key={'x-min':'xMin','x-max':'xMax','y-min':'yMin','y-max':'yMax'}[name]
        page.wait_for_function('p=>window.chartSceneBrowser.rangeEditor[p.key]===p.value', arg={'key':key,'value':value})
    def revert(): click('revert-ranges'); settled()
    def initial():
        choose('overview','Scientific signals'); show(); settled()
        before=state(); assert not draft()['applyEnabled']
        click('x-min'); page.keyboard.press('Enter')
        page.wait_for_timeout(250)
        assert state()['ranges']==before['ranges'] and state()['canUndo']==before['canUndo']
        for axis in ['x','y']:
            assert float(draft()[axis+'Min'])==state()['ranges'][axis]['min']
            assert float(draft()[axis+'Max'])==state()['ranges'][axis]['max']
        page.screenshot(path=str(output/'viewport-properties.png'))
    test('native viewport fields round trip exact ranges without phantom edits',initial)
    def invalid():
        before=state()['ranges']; undo=state()['canUndo']
        enter('x-min','1e-'); enter('y-max','2.7')
        assert draft()['xError'] and not draft()['applyEnabled']
        page.keyboard.press('Enter'); page.wait_for_timeout(250)
        assert state()['ranges']==before and state()['canUndo']==undo
        inputs.click('legend-toggle'); inputs.click('legend-toggle')
        assert draft()['xMin']=='1e-' and draft()['yMax']=='2.7'
        revert()
    test('invalid range input and redraw never partially apply either axis',invalid)
    def precision():
        before=state()['ranges']; xmin='0.12345678901234567'; ymax='1.9876543210987654'
        enter('x-min',xmin); enter('y-max',ymax); assert draft()['applyEnabled']; click('apply-ranges')
        page.wait_for_function('p=>window.chartScene.ranges.x.min===p.x&&window.chartScene.ranges.y.max===p.y',arg={'x':float(xmin),'y':float(ymax)})
        settled(); inputs.click('undo'); settled(); assert state()['ranges']==before
        inputs.click('redo'); settled(); assert state()['ranges']['x']['min']==float(xmin) and state()['ranges']['y']['max']==float(ymax)
        inputs.click('undo'); settled()
    test('precise native two-axis apply shares one undo and redo operation',precision)
    def conflict():
        enter('x-min','0.25'); before=state()['ranges']; inputs.click('zoom-in')
        page.wait_for_function('r=>JSON.stringify(window.chartScene.ranges)!==JSON.stringify(r)',arg=before)
        page.wait_for_function('window.chartSceneBrowser.rangeEditor.conflict')
        assert draft()['xMin']=='0.25' and not draft()['applyEnabled']; live=state()['ranges']
        click('x-min'); page.keyboard.press('Enter'); page.wait_for_timeout(250)
        assert state()['ranges']==live and draft()['xMin']=='0.25'; revert()
    test('external zoom preserves range drafts and requires explicit conflict resolution',conflict)
    def clean_navigation():
        before=state()['ranges']; loads=browser()['modelLoads']; inputs.click('zoom-out')
        page.wait_for_function('r=>JSON.stringify(window.chartScene.ranges)!==JSON.stringify(r)',arg=before); settled()
        assert browser()['modelLoads']==loads and not draft()['revertEnabled']
    test('clean native range fields follow navigation without loading a model',clean_navigation)
    def retained():
        enter('y-max','2.5'); loads=browser()['modelLoads']; inputs.click('source-tab')
        page.wait_for_function('window.chartSceneBrowser.document==="Source"'); assert draft()['yMax']=='2.5'
        inputs.click('example-tab'); module('linked'); module('2d')
        page.wait_for_function('window.chartScene.ready&&window.chartSceneBrowser.document==="Example"')
        assert draft()['yMax']=='2.5' and browser()['modelLoads']==loads; revert()
    test('range draft survives source view and native module reattachment',retained)
    def live_axis():
        choose('live','Real-time'); show(); inputs.click('play')
        page.wait_for_function('window.chartScene.streaming&&window.chartScene.ranges.y.min===-1.5&&window.chartScene.ranges.y.max===1.5')
        for text in ('2.5','3.25','4.125'):
            enter('y-max',text); x=state()['ranges']['x']['max']
            page.wait_for_function('x=>window.chartScene.ranges.x.max>x+.1',arg=x)
            assert draft()['yMax']==text and not draft()['conflict'] and draft()['applyEnabled']
        inputs.click('play'); page.wait_for_function('!window.chartScene.streaming'); revert()
    test('independent Y draft survives live X updates without stale X input',live_axis)
    def live_conflict():
        choose('live','Real-time'); show()
        if not state()['streaming']: inputs.click('play')
        page.wait_for_function('window.chartScene.streaming')
        enter('x-min','-100.25'); x=state()['ranges']['x']['max']
        page.wait_for_function('x=>window.chartScene.ranges.x.max>x+.1&&window.chartSceneBrowser.rangeEditor.conflict',arg=x)
        assert draft()['xMin']=='-100.25' and draft()['dirty'] and not draft()['applyEnabled']
        assert state()['ranges']['x']['min']!=-100.25
        inputs.click('play'); page.wait_for_function('!window.chartScene.streaming'); revert()
    test('native input on a moving axis stays intact and reports a range conflict',live_conflict)
    def compact():
        choose('overview','Scientific signals'); page.set_viewport_size({'width':900,'height':700})
        page.wait_for_function('window.chartScene.width===900&&window.chartScene.controls.chart.width>850')
        before=state()['controls']['chart']; show()
        page.wait_for_function('window.chartSceneBrowser.rangeEditor.floatingInspector')
        assert state()['controls']['chart']==before
        enter('x-min','0.33'); v=state()['controls']['properties-scroll']; header=state()['controls']['close-info']
        page.mouse.move(v['x']+v['width']/2,v['y']+v['height']/2); page.mouse.wheel(0,700); page.wait_for_timeout(250)
        assert state()['controls']['close-info']==header
        page.screenshot(path=str(output/'viewport-properties-compact.png'))
        inputs.click('close-info'); page.wait_for_function('!window.chartSceneBrowser.rangeEditor.inspectorOpen')
        assert draft()['xMin']=='0.33' and state()['controls']['chart']==before; show(); click('x-min'); page.keyboard.press('Escape'); settled()
        assert draft()['inspectorOpen']  # Range Escape reverts; it must not bubble into pane dismissal.
        inputs.click('close-info'); page.set_viewport_size({'width':1440,'height':1000})
        page.wait_for_function('window.chartScene.width===1440'); show(); settled()
    test('compact properties overlay preserves chart geometry drafts and pinned close',compact)
    def wide():
        enter('y-max','3.5'); loads=browser()['modelLoads']; inputs.click('close-info')
        page.wait_for_function('window.chartScene.controls["inspector-panel"]===null&&window.chartScene.controls.chart.width>1300')
        show(); assert draft()['yMax']=='3.5' and browser()['modelLoads']==loads; revert()
    test('wide properties close and reopen retain the same native range draft',wide)
