from pathlib import Path

def replace(path,old,new):
    p=Path(path);s=p.read_text()
    if old not in s:raise RuntimeError('Unexpected source: '+path+' '+old)
    p.write_text(s.replace(old,new))
replace('src/ChartScene.Rendering.OpenGL/OpenGlChartRenderer.cs','gl.GetFloat(GLEnum.AliasedPointSizeRange, pointRange);','// ES ALIASED_POINT_SIZE_RANGE (0x846D) is not exposed by the desktop binding enum.\n        gl.GetFloat((GLEnum)(_es ? 0x846D : 0x0B12), pointRange);')
replace('src/ChartScene.App/MainPage.cs','TransformToVisual(_root)','TransformToVisual(null)')
replace('tests/browser/validate.py',"before = snapshot()['ranges']['x']\n            page.mouse.move(*plot_point(.35, .6))", "state = snapshot()\n            before = state['ranges']['x']\n            screen_x, screen_y = map(int, plot_point(.35, .6))\n            anchor = (screen_x-state['controls']['chart']['x']-state['plot']['X'])/state['plot']['Width']\n            page.mouse.move(screen_x, screen_y)")
replace('tests/browser/validate.py',".35 * (before['max']-before['min'])","anchor * (before['max']-before['min'])")
replace('tests/browser/validate.py',".35 * (after['max']-after['min'])","anchor * (after['max']-after['min'])")
