using System.Text.Json;
using ChartScene.App;
using ChartScene.Core;
public static class DiagnosticTests
{
    public static void Register(TestSuite suite)
    {
        suite.Run("generated diagnostics serialize initial frame without reflection", () =>
        {
            var value = new DiagnosticSnapshot { ready=true, exampleId="overview", plot=new RectD(1,2,300,400),
                controls=new(){["chart"]=new(1,2,300,400),["collapsed"]=null},
                ranges=new(new(0,1),new(-1,1)), series=[new("id","Signal","Line",true,0,100,1)],
                annotations=[new("a","Text",0,1,2,3,false)],catalog=[new("overview","Zażółć \"quotes\"","Scientific")] };
            string json=JsonSerializer.Serialize(value,DiagnosticJsonContext.Default.DiagnosticSnapshot);
            var restored=JsonSerializer.Deserialize(json,DiagnosticJsonContext.Default.DiagnosticSnapshot)!;
            TestSuite.True(restored.ready); TestSuite.Equal(restored.series[0].count,100);
            TestSuite.Equal(restored.catalog[0].Title,value.catalog[0].Title);
            TestSuite.Equal(restored.plot,value.plot); TestSuite.True(restored.controls["collapsed"] is null);
        });
        suite.Run("generated diagnostic string encoding rejects script injection", () =>
        {
            var input="</script>\"'\n";
            string json=JsonSerializer.Serialize(input,DiagnosticJsonContext.Default.String);
            TestSuite.True(!json.Contains("</script>"));
            TestSuite.Equal(JsonSerializer.Deserialize(json,DiagnosticJsonContext.Default.String),input);
        });
    }
}
