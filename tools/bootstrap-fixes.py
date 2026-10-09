from pathlib import Path

def replace(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text()
    if old not in text:
        if new in text:
            return
        raise RuntimeError(f'Expected source fragment not found: {path}')
    file.write_text(text.replace(old, new))

replace('src/ChartScene.Samples/SampleCatalog.cs',
        'public static IReadOnlyList<ChartExample> Examples { get; }=Build();',
        'private static readonly Lazy<IReadOnlyList<ChartExample>> Catalog = new(Build);\n    public static IReadOnlyList<ChartExample> Examples => Catalog.Value;')
replace('src/ChartScene.Samples/SampleCatalog.cs', 'origin+i*86_400_000', 'origin+i*86_400_000.0')
replace('src/ChartScene.Charting/ChartModel.cs',
        'if(x) { Add(snapshot.Extents(0,null,positive)); continue; }',
        'if(x != s.IsHorizontal) { Add(snapshot.Extents(0,null,positive)); continue; }')
replace('src/ChartScene.Charting/ChartModel.cs',
        'foreach(var axis in XAxes) FitAxis(axis,true,null);',
        'if(!visibleYOnly) foreach(var axis in XAxes) FitAxis(axis,true,null);')
replace('src/ChartScene.App/MainPage.cs', '_model.ZoomExtents();', '_model.AutoRangeAxes();')
replace('src/ChartScene.Controls/ChartSurface.cs', 'using Windows.Devices.Input;', 'using Microsoft.UI.Input;')
replace('src/ChartScene.Controls/ChartSurface.cs', 'public void Dispose()', 'public new void Dispose()')
replace('src/ChartScene.Controls/ChartSurface.cs', 'base.GetPatternCore(pattern);', 'base.GetPatternCore(pattern)!;')
replace('src/ChartScene.Controls/ChartSurface.cs',
        '_resources?.Dispose(); _resources=null;\n    }\n}',
        '_resources?.Dispose(); _resources=null;\n        base.Dispose();\n    }\n}')
replace('src/ChartScene.Controls/ChartLegend.cs', 'public void Dispose()=>Detach();', 'public new void Dispose() { Detach(); base.Dispose(); }')
