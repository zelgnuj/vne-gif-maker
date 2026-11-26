// Self-test for VneGif.Core, run on the Mac with Homebrew ffmpeg/gifsicle (windows/test.sh).
// Exercises the same code paths the Windows app uses: probing, scene detection, preview frames,
// GIF export with the size optimizer, paths with spaces and Vietnamese characters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VneGif;

static class Program
{
    static int failures;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) failures++;
    }

    static void Sh(string exe, params string[] args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, Proc.Join(args)) { RedirectStandardError = true, UseShellExecute = false });
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception(exe + " failed: " + err);
    }

    static List<string> ParityCases()
    {
        var list = new List<(ClipEdit, double, double, int, int)>();
        var e = new ClipEdit(); list.Add((e, 1920, 1080, 680, 408));
        e = new ClipEdit { Fit = true }; list.Add((e, 1080, 1920, 680, 408));
        e = new ClipEdit
        {
            Zoom = 1.7, PanX = -0.4, PanY = 0.3, CropL = 0.1, CropT = 0.05, CropR = 0.02, CropB = 0.13, Rotation = 90, FlipH = true,
            Speed = 0.75, Brightness = 0.1, Contrast = 1.2, Saturation = 0.8, Sharpen = 0.6, Denoise = true,
        };
        list.Add((e, 1281, 721, 800, 450));
        e = new ClipEdit { Fit = true, Zoom = 1.3, PanX = 1, Rotation = 270, FlipV = true, Speed = 2 }; list.Add((e, 3840, 2160, 600, 600));
        e = new ClipEdit { Rotation = 180, PanY = -1, Zoom = 4 }; list.Add((e, 720, 1280, 1200, 514));
        return list.Select((c, i) => string.Join(";", EditMath.FfmpegParts(i, c.Item1, c.Item2, c.Item3, c.Item4, c.Item5, 12))).ToList();
    }

    static async Task<int> Main(string[] argv)
    {
        if (argv.Length > 0 && argv[0] == "--parity")
        {
            foreach (var l in ParityCases()) Console.WriteLine(l);
            return 0;
        }
        var brewFF = Environment.GetEnvironmentVariable("SRC_FFMPEG") ?? "ffmpeg";
        var root = Path.Combine(Path.GetTempPath(), "vnegif selftest", "thử nghiệm ổ đĩa");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Console.WriteLine("ffmpeg:   " + Tools.FFmpeg);
        Console.WriteLine("gifsicle: " + Tools.Gifsicle);
        Console.WriteLine("work dir: " + root);

        // Test footage: three visually distinct 3 s scenes (cuts at 3 s and 6 s), 1280×720 @ 30.
        var land = Path.Combine(root, "cảnh ngang 1.mp4");
        Sh(brewFF, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=3",
            "-f", "lavfi", "-i", "smptebars=s=1280x720:r=30:d=3",
            "-f", "lavfi", "-i", "mandelbrot=s=1280x720:r=30,trim=duration=3",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", land);
        // Vertical clip stored landscape with a 90° display-rotation flag (like phone footage).
        var rotSrc = Path.Combine(root, "tmp-rot.mp4");
        Sh(brewFF, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=960x540:r=25:d=4",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", rotSrc);
        var vert = Path.Combine(root, "dọc xoay.mp4");
        Sh(brewFF, "-hide_banner", "-loglevel", "error", "-y", "-display_rotation:v:0", "90", "-i", rotSrc, "-c", "copy", vert);
        var cts = CancellationToken.None;

        Console.WriteLine("Parity with Edit.swift");
        var swiftOut = Environment.GetEnvironmentVariable("SWIFT_PARITY");
        if (swiftOut != null && File.Exists(swiftOut))
        {
            var expected = File.ReadAllLines(swiftOut).Where(l => l.Length > 0).ToList();
            var mine = ParityCases();
            Check(expected.Count == mine.Count, $"{mine.Count} cases");
            for (int i = 0; i < Math.Min(expected.Count, mine.Count); i++)
            {
                Check(expected[i] == mine[i], $"case {i} identical");
                if (expected[i] != mine[i]) Console.WriteLine("    swift: " + expected[i] + "\n    c#:    " + mine[i]);
            }
        }
        else Console.WriteLine("  (skipped: SWIFT_PARITY not set)");

        Console.WriteLine("Probe");
        var mi = await Probe.ReadAsync(land, cts);
        Check(mi.Width == 1280 && mi.Height == 720, $"landscape size {mi.Width}×{mi.Height}");
        Check(Math.Abs(mi.Duration - 9) < 0.1, $"duration {mi.Duration}");
        Check(Math.Abs(mi.Fps - 30) < 0.01, $"fps {mi.Fps}");
        var mv = await Probe.ReadAsync(vert, cts);
        Check(mv.Width == 540 && mv.Height == 960, $"rotated clip display size {mv.Width}×{mv.Height}");
        Check(Math.Abs(mv.Fps - 25) < 0.01, $"fps {mv.Fps}");
        try { await Probe.ReadAsync(Path.Combine(root, "không có.mp4"), cts); Check(false, "missing file throws"); }
        catch (EncodeException) { Check(true, "missing file throws EncodeException"); }

        Console.WriteLine("Scene detection");
        double lastProgress = 0;
        var cuts = await SceneDetector.DetectAsync(land, mi.Duration, (c, p) => { if (p >= 0) lastProgress = p; }, cts);
        Check(cuts.Count == 2 && Math.Abs(cuts[0] - 3) < 0.05 && Math.Abs(cuts[1] - 6) < 0.05, "cuts at 3 s and 6 s: " + string.Join(", ", cuts));
        Check(lastProgress > 0.3, $"progress reported ({lastProgress:0.00})");

        Console.WriteLine("Preview frames");
        var thumb = await Frames.GrabAsync(land, 4.5, 160, 90, true, cts);
        Check(thumb != null && thumb.Length == 160 * 90 * 3, "fast thumbnail 160×90");
        var (W, H) = GifEncoder.Size(680, 5.0 / 3.0);
        Check(W == 680 && H == 408, $"output size {W}×{H}");
        var edit = new ClipEdit { Fit = true, Zoom = 1.2, Rotation = 90, Brightness = 0.1, Sharpen = 0.5, Denoise = true };
        var graph = Frames.PreviewGraph(edit, mi.Width, mi.Height, W, H, 12);
        var still = await Frames.ReadAsync(Frames.RenderArgs(land, 1.0, 0, graph, 1), W * H * 3, 1, cts);
        Check(still.Count == 1, "edited still frame");
        int n = 0;
        var sw = Stopwatch.StartNew();
        await Frames.StreamAsync(Frames.RenderArgs(land, 0, 3, Frames.PreviewGraph(new ClipEdit(), mi.Width, mi.Height, W, H, 12), 0),
            W * H * 3, (i, b) => n++, cts);
        Check(n == 36, $"3 s range at 12 fps → {n} frames ({sw.ElapsedMilliseconds} ms)");
        var vgraph = Frames.PreviewGraph(new ClipEdit { Fit = true }, mv.Width, mv.Height, W, H, 12);
        Check((await Frames.ReadAsync(Frames.RenderArgs(vert, 2, 0, vgraph, 1), W * H * 3, 1, cts)).Count == 1, "vertical fit frame");
        using (var c = new CancellationTokenSource(150))
        {
            try
            {
                await Frames.StreamAsync(Frames.RenderArgs(land, 0, 0, graph, 0), W * H * 3, (i, b) => { }, c.Token);
                Check(false, "cancel stops rendering");
            }
            catch (OperationCanceledException) { Check(true, "cancel stops rendering"); }
        }

        Console.WriteLine("GIF export");
        GifRequest Req(long limit, FitPriority pr = FitPriority.KeepSize, bool force = true) => new GifRequest
        {
            Clips = new List<ClipSpec>
            {
                new ClipSpec { Path = land, Start = 0.5 - 1.0 / 60, Duration = 2.5, SrcW = mi.Width, SrcH = mi.Height, Edit = new ClipEdit() },
                new ClipSpec { Path = vert, Start = 1 - 1.0 / 50, Duration = 2, SrcW = mv.Width, SrcH = mv.Height,
                    Edit = new ClipEdit { Fit = true, Speed = 1.5, Saturation = 1.3 } },
                new ClipSpec { Path = land, Start = 6.2, Duration = 1.5, SrcW = mi.Width, SrcH = mi.Height,
                    Edit = new ClipEdit { Zoom = 1.5, PanX = 0.5, CropB = 0.1, FlipH = true, Contrast = 1.2 } },
            },
            Aspect = 5.0 / 3.0, Width = 680, Fps = 12, Dither = Dither.Balanced, LimitBytes = limit,
            AutoFit = true, Priority = pr, MinFps = 10, MinWidth = 600, Force = force,
        };
        var outDir = Path.Combine(root, "kết quả");
        Directory.CreateDirectory(outDir);
        var msgs = new List<string>();
        var r1 = await GifEncoder.MakeGifAsync(Req(8_000_000), Path.Combine(outDir, "ảnh 8MB.gif"), msgs.Add, cts);
        Check(File.Exists(r1.Path) && r1.UnderLimit && r1.Width == 680 && r1.Height == 408 && r1.Fps == 12,
            $"8 MB: {r1.Bytes / 1e6:0.00} MB {r1.Width}×{r1.Height} {r1.Fps} fps, attempts {r1.Attempts}");
        var info = await Probe.ReadAsync(r1.Path, cts);
        double expectDur = 2.5 + 2 / 1.5 + 1.5;
        Check(Math.Abs(info.Duration - expectDur) < 0.25, $"GIF duration {info.Duration:0.00} s (expected ≈ {expectDur:0.00})");

        long tight = r1.Bytes * 45 / 100;
        var r2 = await GifEncoder.MakeGifAsync(Req(tight), Path.Combine(outDir, "ảnh chặt.gif"), msgs.Add, cts);
        Check(r2.UnderLimit, $"tight limit {tight / 1e6:0.00} MB → {r2.Bytes / 1e6:0.00} MB, {r2.Width} px {r2.Fps} fps, level {r2.SqueezeLevel}, attempts {r2.Attempts}, {string.Join(" · ", r2.Notes)}");
        var r3 = await GifEncoder.MakeGifAsync(Req(tight, FitPriority.KeepSharpness), Path.Combine(outDir, "ảnh nét.gif"), msgs.Add, cts);
        Check(r3.UnderLimit, $"keep sharpness → {r3.Bytes / 1e6:0.00} MB, {r3.Width} px {r3.Fps} fps, level {r3.SqueezeLevel}");
        var r4 = await GifEncoder.MakeGifAsync(Req(60_000, force: true), Path.Combine(outDir, "ảnh tí hon.gif"), msgs.Add, cts);
        Check(r4.BelowFloor && r4.Width >= GifEncoder.HardMinWidth && r4.Fps >= GifEncoder.HardMinFps,
            $"impossible limit → {r4.Bytes / 1e6:0.00} MB, {r4.Width} px {r4.Fps} fps, under={r4.UnderLimit}, below floor={r4.BelowFloor}");
        Check(msgs.Any(m => m.Contains("nén thêm")), "progress messages in Vietnamese");
        using (var c = new CancellationTokenSource(300))
        {
            try { await GifEncoder.MakeGifAsync(Req(8_000_000), Path.Combine(outDir, "huỷ.gif"), null, c.Token); Check(false, "cancel export"); }
            catch (OperationCanceledException) { Check(true, "cancel export"); }
        }
        Check(!Directory.GetDirectories(Path.GetTempPath(), "vnegif-*").Any(), "temp folders cleaned up");

        Console.WriteLine("Formatting / session");
        Check(Num.Fmt(5.25, 1) == "5,3" || Num.Fmt(5.25, 1) == "5,2", "Num.Fmt " + Num.Fmt(5.25, 1));
        Check(Num.Fmt(2, 2) == "2" && Num.Fmt(0.5, 2, true) == "0,50", "Num.Fmt fixed/unfixed");
        Check(Num.Time(3725.4) == "1:02:05,40" && Num.Time(3.25) == "00:03,25", "Num.Time " + Num.Time(3.25));
        Check(Num.ParseTime("1:02:29,4") == 3749.4 && Num.ParseTime("149,4") == 149.4, "Num.ParseTime");
        Check(Proc.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"" && Proc.Quote("x\"y") == "\"x\\\"y\"" && Proc.Quote("") == "\"\"", "argument quoting");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        Console.WriteLine("GIFs: " + outDir);
        return failures == 0 ? 0 : 1;
    }
}
