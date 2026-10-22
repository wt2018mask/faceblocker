using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var settings = new AppSettings();
settings.Detection.EnabledRegions = new() { BodyRegion.Face };
settings.Performance.UseDirectML = false;
settings.Performance.InferenceThreads = int.TryParse(args.ElementAtOrDefault(1), out var threads) ? threads : 2;
var options = VisionPipelineOptions.ForSettings(Path.Combine(root, "models"), settings);
Check(options.EnableFace && !options.EnablePerson && !options.EnableNude && !options.EnableSegmentation, "Face-only session selection");
// Absence of unrelated files must not prevent a face-only session.
var isolated = Path.Combine(root, "artifacts", "face-only-models");
Directory.CreateDirectory(isolated);
File.Copy(options.FaceModelPath, Path.Combine(isolated, Path.GetFileName(options.FaceModelPath)), true);
using var models = VisionModels.Load(VisionPipelineOptions.ForSettings(isolated, settings));
using var pipeline = new VisionPipeline(options, models);
var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.Combine(root, "assets/face-fixtures/astronaut.png")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
var src = new byte[512 * 512 * 4]; bitmap.CopyPixels(src, 512 * 4, 0);
var cases = new List<(string Name, int W, int H, (int X,int Y,double Scale)[] Copies)>
{
    ("one", 640, 640, new[] { (30,30,1.0) }),
    ("multiple", 1280,720, new[] { (0,0,1.0), (700,180,0.8) }),
    ("small", 1280,720, new[] { (150,150,0.35), (900,200,0.5) }),
    ("partial", 640,640, new[] { (-205,30,1.0) }),
    ("none", 1280,720, Array.Empty<(int,int,double)>())
};
var latencies = new List<double>(); int expected = 0, hits = 0, falsePositives = 0;
foreach (var c in cases)
{
    using var frame = Make(c.W,c.H,c.Copies);
    var input = new float[3*640*640]; YuNetDetector.Preprocess(frame,input);
    var sw=Stopwatch.StartNew();
    var found=YuNetDetector.Decode(models.RunFace(input),c.W,c.H,settings.Detection.FaceConfidence,settings.Detection.NmsIou);
    latencies.Add(sw.Elapsed.TotalMilliseconds);
    var truths=c.Copies.Select(v=>new PixelRect(v.X+178*v.Scale,v.Y+64*v.Scale,94*v.Scale,112*v.Scale).Clip(frame.LocalBounds)).Where(b=>!b.IsEmpty).ToList();
    var matched=new HashSet<int>(); var caseHits=0;
    foreach(var truth in truths)
    {
        expected++;
        var index=Enumerable.Range(0,found.Count).Where(i=>!matched.Contains(i) && found[i].Box.IoU(truth)>=0.4).DefaultIfEmpty(-1).First();
        if(index>=0) { matched.Add(index); hits++; caseHits++; }
    }
    falsePositives+=found.Count-matched.Count;
    Console.WriteLine($"CASE {c.Name}: expected={truths.Count} matched={caseHits} detections={found.Count} ms={sw.Elapsed.TotalMilliseconds:F1} boxes={string.Join(';',found.Select(f=>$"{f.Box}@{f.Confidence:F3}"))}");
    Check(caseHits==truths.Count && found.Count==truths.Count,$"fixture {c.Name}");
}
// Warm baseline: same licensed fixture, no WGC/render/display timing.
using (var frame = Make(1280,720,new[] { (100,60,1.0) }))
{
    var input = new float[3*640*640];
    for(var i=0;i<55;i++)
    {
        var sw=Stopwatch.StartNew();
        YuNetDetector.Preprocess(frame,input);
        _=YuNetDetector.Decode(models.RunFace(input),1280,720,0.6,0.45);
        if(i>=5) latencies.Add(sw.Elapsed.TotalMilliseconds);
    }
}
var sorted=latencies.Skip(cases.Count).OrderBy(v=>v).ToArray();
Console.WriteLine($"BASELINE threads={settings.Performance.InferenceThreads} passes={sorted.Length} preprocess+inference+decode P50={sorted[24]:F1} P95={sorted[47]:F1} P99={sorted[49]:F1} ms");
var videoDirectory=Path.Combine(root,"artifacts/browser-face-fixture");
var videoFrames=Directory.Exists(videoDirectory) ? Directory.GetFiles(videoDirectory,"video-frame-*.png").OrderBy(f=>f).ToArray() : Array.Empty<string>();
var videoHits=0;
foreach(var file in videoFrames)
{
    var b=new FormatConvertedBitmap(BitmapFrame.Create(new Uri(file),BitmapCreateOptions.None,BitmapCacheOption.OnLoad),PixelFormats.Bgra32,null,0);
    using var frame=VideoFrame.Rent(b.PixelWidth,b.PixelHeight,b.PixelWidth*4,1,Environment.TickCount64,"fixture-video",new PixelRect(0,0,b.PixelWidth,b.PixelHeight));
    b.CopyPixels(frame.Pixels,frame.Stride,0);
    var input=new float[3*640*640]; YuNetDetector.Preprocess(frame,input);
    var detections=YuNetDetector.Decode(models.RunFace(input),frame.Width,frame.Height,0.6,0.45);
    if(detections.Count==1)videoHits++;
}
if(videoFrames.Length>0) Check(videoFrames.Length==8 && videoHits==8,"decoded local VP8 fixture samples: 8/8 face detections");
else Console.WriteLine("NOT RUN: optional VP8 samples; run tools/prepare_face_fixture.py to prepare.");
// Real inference through the production pipeline: twenty translated frames plus blank misses.
pipeline.StartProtection(settings, Environment.TickCount64);
var covered=0; int motionHits=0;
for(var i=0;i<20;i++)
{
    using var frame=Make(1280,720,new[] { (100+i*12,60+i*3,1.0) });
    pipeline.ReportFrame(frame.TimestampMs);
    var result=pipeline.Analyze(frame,settings,Environment.TickCount64);
    Check(result.Error is null && result.NudeDetections.Count==0 && result.PersonDetections.Count==0,"production Face-only pipeline");
    var truth=new PixelRect(278+i*12,124+i*3,94,112);
    if(result.FaceDetections.Any(f=>f.Box.IoU(truth)>=0.4)) motionHits++;
    if(result.Regions.Any(r=>r.Source==BodyRegion.Face && r.Box.IntersectionArea(truth)/truth.Area>=0.95)) covered++;
}
Check(motionHits==20 && covered==20,"translated fixture detections and mask coverage");
var stoppedAt=Environment.TickCount64;
for(var i=0;i<35;i++)
{
    using var frame=Make(1280,720,Array.Empty<(int,int,double)>());
    pipeline.ReportFrame(frame.TimestampMs);
    var result=pipeline.Analyze(frame,settings,Environment.TickCount64);
    Check(result.Error is null,"blank inference succeeds");
}
var held=pipeline.GetRegions(settings,new PixelRect(0,0,1280,720),stoppedAt+500);
Check(held.Any(r=>r.Source==BodyRegion.Face),"35 missed passes retain face mask for configured hold");
var absence=Stopwatch.StartNew();
while(absence.ElapsedMilliseconds<3200)
{
    using var frame=Make(1280,720,Array.Empty<(int,int,double)>());
    pipeline.ReportFrame(frame.TimestampMs);
    Check(pipeline.Analyze(frame,settings,Environment.TickCount64).Error is null,"blank inference succeeds");
}
Check(pipeline.GetRegions(settings,new PixelRect(0,0,1280,720),Environment.TickCount64).Count==0,
    "fresh no-face analyses release expired local guards without a whole-screen pause");
Check(pipeline.GetRegions(settings,new PixelRect(0,0,1280,720),Environment.TickCount64+1600)
    .Any(r=>r.Kind==VeilDesk.Core.Censoring.CensorRegionKind.FailClosed),
    "Face local guards never disable whole-screen protection for stale evidence");
pipeline.StopProtection();
Check(pipeline.GetRegions(settings,new PixelRect(0,0,1280,720),Environment.TickCount64).Count==0,"Stop removes all regions");
// Geometry under rapid scroll/reversal: latest observed box must always be covered.
var tracker=new SubjectTracker(); var bounds=new PixelRect(0,0,1280,720);
foreach(var (y,t) in new[] { (100,1000L),(250,1100L),(80,1200L),(400,1300L) })
{
    var raw=new PixelRect(300,y,90,110);
    var tracks=tracker.Update(new[] {new Detection(BodyRegion.Face,0.9,raw)},t,bounds);
    Check(tracks.Any(x=>x.PredictedBox(t,80,0.18,bounds).IntersectionArea(raw)>=raw.Area-0.01),"scroll latest-box coverage");
}
if(args.Contains("--replay"))
{
    var replayTracker=new SubjectTracker(); var gate=new VeilDesk.Core.Censoring.ConfidenceGate(); var guard=new VeilDesk.Core.Safety.CoverageGuard();
    var replayBounds=new PixelRect(0,0,2560,1600); int missingPasses=0, replayed=0;
    foreach(var line in File.ReadLines(args[^1]))
    {
        var marker=line.IndexOf(" [acceptance-analysis] ",StringComparison.Ordinal);
        if(marker<0)continue;
        using var doc=System.Text.Json.JsonDocument.Parse(line[(marker+23)..]);
        var e=doc.RootElement; if(e.GetProperty("FaceMs").GetDouble()<=0)continue;
        var now=DateTimeOffset.Parse(line[..marker]).ToUnixTimeMilliseconds();
        var stamp=now-e.GetProperty("frameAgeMs").GetInt64();
        var ds=e.GetProperty("faces").EnumerateArray().Select(f=>
        {
            var b=f.GetProperty("Box");
            return new Detection(BodyRegion.Face,f.GetProperty("Confidence").GetDouble(),new PixelRect(b.GetProperty("X").GetDouble(),b.GetProperty("Y").GetDouble(),b.GetProperty("Width").GetDouble(),b.GetProperty("Height").GetDouble()));
        }).ToArray();
        var ts=replayTracker.Update(ds,stamp,replayBounds);
        gate.RetainOnly(ts.Select(t=>t.Id));
        var rs=VeilDesk.Core.Censoring.CensorComposer.Build(ts,settings,replayBounds,now,false,gate);
        var missing=guard.RequiresCover(ts.Where(t=>!settings.Detection.LocalFaceUncertaintyCover || now-t.LastSeenMs<=settings.Censoring.TrackPersistenceMs).Select(t=>t.Id),rs.Select(r=>r.TrackId!.Value),ts.Where(t=>now-t.LastSeenMs<=settings.Censoring.TrackPersistenceMs).Select(t=>t.Id));
        if(missing)
        {
            missingPasses++;
            if(missingPasses<=3) Console.WriteLine($"REPLAY missing coverage {line[..marker]} tracks={ts.Count} masks={rs.Count} confidence={string.Join(',',ts.Select(t=>t.Confidence.ToString("F3")))}");
        }
        replayed++;
    }
    Console.WriteLine($"REPLAY passes={replayed} missingCoverage={missingPasses} (analysis-cadence approximation; render cadence not replayed)");
}
Console.WriteLine($"SUMMARY fixtureRecall={hits}/{expected} unmatchedDetections={falsePositives} motionRecall={motionHits}/20 motionCoverage95={covered}/20 memoryMiB={Process.GetCurrentProcess().PrivateMemorySize64/1048576.0:F1}");
Console.WriteLine("PASS: no screen capture, overlay windows, browser, or input injection performed.");
return 0;

void Check(bool pass,string name) { if(!pass) throw new Exception("FAIL: "+name); if(!name.StartsWith("production") && !name.StartsWith("blank")) Console.WriteLine("PASS: "+name); }
VideoFrame Make(int w,int h,(int X,int Y,double Scale)[] copies)
{
    var f=VideoFrame.Rent(w,h,w*4,1,Environment.TickCount64,"fixture",new PixelRect(0,0,w,h));
    Array.Fill(f.Pixels,(byte)230,0,w*h*4);
    foreach(var c in copies)
    {
        var size=(int)(512*c.Scale);
        for(var y=0;y<size;y++) for(var x=0;x<size;x++)
        {
            var dx=x+c.X;var dy=y+c.Y;if(dx<0||dy<0||dx>=w||dy>=h)continue;
            var si=((int)(y/c.Scale)*512+(int)(x/c.Scale))*4;
            Buffer.BlockCopy(src,si,f.Pixels,(dy*w+dx)*4,4);
        }
    }
    return f;
}

