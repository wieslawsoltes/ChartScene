"""Additional layout checks invoked after the original linked-chart regression sequence."""
from __future__ import annotations
import json

def validate_alignment(page, state, click, move, linked_cursors, test, out):
    def alignment(): return page.evaluate('window.chartSceneAlignment')
    def aligned():
        page.wait_for_function('''() => {
            const s=window.chartSceneLinked,a=window.chartSceneAlignment;
            return a?.enabled && a.members===2 && a.lowerVisible && s?.ready &&
              Math.abs(s.upper.plot.x-s.lower.plot.x)<.05 &&
              Math.abs(s.upper.plot.width-s.lower.plot.width)<.05;
        }''')
    def data_identity():
        s=state()
        return tuple((s[name]['dataRevision'],s[name]['min'],s[name]['max']) for name in ('upper','lower'))
    def ensure(name, field, expected):
        if alignment()[field] != expected: click(name)
        page.wait_for_function('p=>window.chartSceneAlignment[p.field]===p.expected',arg={'field':field,'expected':expected})
    def initial():
        aligned(); a=alignment()
        assert a['lowerNatural']['left']>a['upperNatural']['left']+10,a
        assert abs(state()['upper']['plot']['x']-a['lowerNatural']['left'])<.05
        page.screenshot(path=str(out/'aligned-wide-labels.png'))
    test('unequal axis labels share exact plot edges',initial)
    def toggle():
        before=data_identity();ensure('align-plots','enabled',False)
        page.wait_for_function('Math.abs(window.chartSceneLinked.upper.plot.x-window.chartSceneLinked.lower.plot.x)>10')
        ensure('align-plots','enabled',True);aligned();assert data_identity()==before
    test('layout alignment toggles without data or range changes',toggle)
    def shrink():
        before=data_identity();old=state()['upper']['plot']['x']
        ensure('wide-labels','wideLabels',False)
        page.wait_for_function('x=>window.chartSceneLinked.upper.plot.x<x-10',arg=old)
        aligned();assert data_identity()==before
        ensure('wide-labels','wideLabels',True);aligned()
    test('shared margins shrink when long labels disappear',shrink)
    def right_axis():
        before=data_identity();old=state()['upper']['plot']['width']
        ensure('right-axis','rightAxis',True)
        page.wait_for_function('w=>window.chartSceneLinked.upper.plot.width<w-30',arg=old);aligned()
        ensure('right-axis','rightAxis',False)
        page.wait_for_function('w=>Math.abs(window.chartSceneLinked.upper.plot.width-w)<.05',arg=old);aligned()
        assert data_identity()==before
    test('right-side axis reservations expand and release',right_axis)
    def reserve():
        before=data_identity();ensure('reserve-space','reserved',True)
        page.wait_for_function('Math.abs(window.chartSceneLinked.upper.plot.x-210)<.05');aligned()
        assert abs(state()['upper']['plot']['width']-(state()['controls']['upper']['width']-290))<.05
        ensure('reserve-space','reserved',False)
        page.wait_for_function('window.chartSceneLinked.upper.plot.x<200');aligned();assert data_identity()==before
    test('explicit per-view label reservations propagate and clear',reserve)
    def hidden():
        before=data_identity();ensure('hide-lower','lowerVisible',False)
        page.wait_for_function('window.chartSceneAlignment.members===1 && window.chartSceneLinked.controls.lower===null')
        page.wait_for_function('Math.abs(window.chartSceneLinked.upper.plot.x-window.chartSceneAlignment.upperNatural.left)<.05')
        ensure('hide-lower','lowerVisible',True);aligned();assert data_identity()==before
    test('hidden chart releases and reacquires layout membership',hidden)
    def stationary():
        aligned();move('upper',.55,.6);linked_cursors()
        # No second pointer event is sent after changing the viewport. The active source
        # remains under the pointer and both charts derive the same current data X.
        page.set_viewport_size({'width':1100,'height':850})
        page.wait_for_function('window.chartSceneLinked.controls.upper.width===1100');aligned();linked_cursors()
        page.set_viewport_size({'width':1440,'height':1000})
        page.wait_for_function('window.chartSceneLinked.controls.upper.width===1440');aligned();linked_cursors()
    test('stationary cursor remains synchronized through layout resize',stationary)
    def cache():
        aligned();move('upper',.3,.6);linked_cursors();page.wait_for_timeout(250)
        before=alignment();move('upper',.65,.6);linked_cursors();page.wait_for_timeout(250)
        after=alignment()
        assert after['passes']==before['passes'] and after['notifications']==before['notifications'],(before,after)
        assert after['upperSceneBuilds']==before['upperSceneBuilds'] and after['lowerSceneBuilds']==before['lowerSceneBuilds'],(before,after)
    test('cursor overlays reuse negotiated margins and cached scenes',cache)
    def replacement():
        before=state()['generation'];click('replace')
        page.wait_for_function('n=>window.chartSceneLinked.generation===n+1',arg=before);aligned()
        assert alignment()['members']==2
        move('lower',.6);linked_cursors()
    test('replacement model rejoins current layout exactly once',replacement)
    def light():
        before=data_identity();click('theme')
        page.wait_for_function("window.chartSceneAlignment.upperTheme==='Light'&&window.chartSceneAlignment.lowerTheme==='Light'&&window.chartSceneAlignment.themeCommandActive")
        aligned();page.screenshot(path=str(out/'aligned-light.png'))
        click('theme')
        page.wait_for_function("window.chartSceneAlignment.upperTheme==='Dark'&&window.chartSceneAlignment.lowerTheme==='Dark'&&!window.chartSceneAlignment.themeCommandActive")
        aligned();assert data_identity()==before
    test('linked vector commands and both panels switch themes',light)
    (out/'alignment-state.json').write_text(json.dumps({'alignment':alignment(),'charts':state()},indent=2))
