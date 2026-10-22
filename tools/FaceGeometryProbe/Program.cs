using System.Text.Json;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;

if(args.Length!=3) { Console.Error.WriteLine("usage: FaceGeometryProbe <log-root> <settings.json> <output.json>; recorded 2560x1600 monitor only"); return 2; }
var settings=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[1]),SettingsStore.JsonOptions) ?? throw new Exception("settings missing");
SettingsValidator.ValidateAndClamp(settings);
var bounds=new PixelRect(0,0,2560,1600);
var seen=new HashSet<string>();
var rows=new List<Sample>();
foreach(var path in Directory.EnumerateFiles(args[0],"app.log*",SearchOption.AllDirectories))
foreach(var line in File.ReadLines(path))
{
    if(!line.Contains("[acceptance-analysis] ") || !seen.Add(line))continue;
    using var doc=JsonDocument.Parse(line.Split("[acceptance-analysis] ",2)[1]);
    var e=doc.RootElement;
    if(!e.TryGetProperty("tracks",out var tracks))continue;
    var byId=tracks.EnumerateArray().ToDictionary(t=>t.GetProperty("Id").GetInt32());
    foreach(var mask in e.GetProperty("masks").EnumerateArray())
    {
        if(mask.GetProperty("Source").GetInt32()!=101 || mask.GetProperty("Kind").GetInt32()==2)continue;
        if(!mask.TryGetProperty("TrackId",out var id) || id.ValueKind==JsonValueKind.Null || !byId.TryGetValue(id.GetInt32(),out var track))continue;
        var raw=Box(track.GetProperty("Box"));
        var old=Box(mask.GetProperty("Box"));
        var uncertain=mask.GetProperty("Kind").GetInt32()==3;
        var margin=settings.Detection.FacePaddingFraction+(uncertain ? .12 : 0);
        var next=FaceMaskGeometry.Predict(raw,track.GetProperty("VelocityX").GetDouble(),track.GetProperty("VelocityY").GetDouble(),settings.Censoring.PredictionHorizonMs,margin,bounds)
            .Expand(settings.Censoring.MaskExpansion).Clip(bounds);
        var visible=raw.Clip(bounds);
        if(next.IntersectionArea(visible)<visible.Area-.001)throw new Exception("New mask lost part of its observed face.");
        rows.Add(new Sample(e.GetProperty("frame").GetInt64(),id.GetInt32(),uncertain,raw,old,next));
    }
}
if(rows.Count==0)throw new Exception("No linked face masks; log must contain track diagnostics.");
var biggest=rows.OrderByDescending(r=>r.Before.Area).Take(8).ToArray();
var report=new { recordedMonitor="2560x1600",samples=rows.Count,observedFaceCoverageFailures=0,
    maximumBeforeFraction=rows.Max(r=>r.Before.Area)/bounds.Area,
    maximumAfterFraction=rows.Max(r=>r.After.Area)/bounds.Area,
    note="Recorded track geometry only. Original detections, velocities and uncertainty are held constant. Not a pixel/display/exposure test. Area fractions are per mask, not union.",biggest };
File.WriteAllText(args[2],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS recorded geometry masks={rows.Count} observed-face coverage failures=0 max-mask-screen-fraction {report.maximumBeforeFraction:P1} -> {report.maximumAfterFraction:P1}");
foreach(var r in biggest.Take(3))Console.WriteLine($"raw {r.Raw.Width:F0}x{r.Raw.Height:F0}, mask {r.Before.Width:F0}x{r.Before.Height:F0} -> {r.After.Width:F0}x{r.After.Height:F0}, uncertain={r.Uncertain}");
return 0;
static PixelRect Box(JsonElement e)=>new(e.GetProperty("X").GetDouble(),e.GetProperty("Y").GetDouble(),e.GetProperty("Width").GetDouble(),e.GetProperty("Height").GetDouble());
record Sample(long Frame,int TrackId,bool Uncertain,PixelRect Raw,PixelRect Before,PixelRect After);
