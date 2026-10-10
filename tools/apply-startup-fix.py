"""One-time migration from anonymous diagnostics to generated JSON contracts."""
from pathlib import Path
p=Path('src/ChartScene.App/MainPage.cs');s=p.read_text()
replacements={
 'private object? Bounds(':'private ControlBounds? Bounds(',
 'return new{x=position.X,y=position.Y,width=element.ActualWidth,height=element.ActualHeight};':'return new(position.X,position.Y,element.ActualWidth,element.ActualHeight);',
 'PlatformBridge.Publish(new\n':'PlatformBridge.Publish(new DiagnosticSnapshot\n',
 'ranges=new{x=new{min=_model.XAxis.VisibleRange.Min,max=_model.XAxis.VisibleRange.Max},y=new{min=_model.YAxis.VisibleRange.Min,max=_model.YAxis.VisibleRange.Max}},':'ranges=new(new(_model.XAxis.VisibleRange.Min,_model.XAxis.VisibleRange.Max),new(_model.YAxis.VisibleRange.Min,_model.YAxis.VisibleRange.Max)),',
 'Select(s=>new{s.Id,name=s.SeriesName,kind=s.Kind.ToString(),visible=s.IsVisible,selected=s.SelectedIndices.Count,count=s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0,revision=s.DataSeries?.Revision??0})':'Select(s=>new SeriesSnapshotDto(s.Id,s.SeriesName,s.Kind.ToString(),s.IsVisible,s.SelectedIndices.Count,s.DataSeries?.Count??s.HeatmapGrid?.Values.Length??0,s.DataSeries?.Revision??0)).ToArray()',
 'Select(a=>new{a.Id,kind=a.Kind.ToString(),a.X1,a.Y1,a.X2,a.Y2,a.IsSelected})':'Select(a=>new AnnotationSnapshotDto(a.Id,a.Kind.ToString(),a.X1,a.Y1,a.X2,a.Y2,a.IsSelected)).ToArray()',
 'Select(e=>new{e.Id,e.Title,e.Category})':'Select(e=>new CatalogEntryDto(e.Id,e.Title,e.Category)).ToArray()'
}
for old,new in replacements.items():
    if s.count(old)!=1:raise RuntimeError('Unexpected source: '+old)
    s=s.replace(old,new)
p.write_text(s)
p=Path('src/ChartScene.App/ChartScene.App.csproj');p.write_text(p.read_text().replace('<JsonSerializerIsReflectionEnabledByDefault>true','<JsonSerializerIsReflectionEnabledByDefault>false'))
