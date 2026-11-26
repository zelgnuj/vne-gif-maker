using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VneGif
{
    public sealed class MediaInfo
    {
        public double Duration;
        public int Width, Height;      // display-oriented (rotation applied)
        public double Fps = 30;
        public string Codec = "";
        public bool IsVertical => Height > Width;
    }

    /// <summary>Reads duration/size/fps from ffmpeg's own banner (no ffprobe bundled).</summary>
    public static class Probe
    {
        static readonly Regex Dur = new Regex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        static readonly Regex Vid = new Regex(@"Stream #\d+:\d+.*?: Video: ([^\s,]+).*?, (\d{2,5})x(\d{2,5})");
        static readonly Regex FpsRx = new Regex(@", (\d+(?:\.\d+)?) fps");
        static readonly Regex TbrRx = new Regex(@", (\d+(?:\.\d+)?) tbr");
        static readonly Regex Rot = new Regex(@"rotation of (-?\d+(?:\.\d+)?) degrees");

        public static async Task<MediaInfo> ReadAsync(string path, CancellationToken ct = default)
        {
            var ffmpeg = Tools.FFmpeg ?? throw EncodeException.NoFFmpeg();
            var lines = new List<string>();
            try
            {
                await Proc.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", path }, ct, l => lines.Add(l));
            }
            catch (EncodeException) { /* "At least one output file must be specified" — expected */ }
            return Parse(lines, Path.GetFileName(path));
        }

        public static MediaInfo Parse(IList<string> lines, string name)
        {
            var info = new MediaInfo();
            bool haveVideo = false, haveDur = false;
            double rotation = 0;
            foreach (var l in lines)
            {
                var m = Dur.Match(l);
                if (m.Success && !haveDur)
                {
                    info.Duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    haveDur = true;
                }
                if (!haveVideo)
                {
                    var v = Vid.Match(l);
                    if (v.Success && !l.Contains("(attached pic)"))
                    {
                        haveVideo = true;
                        info.Codec = v.Groups[1].Value;
                        info.Width = int.Parse(v.Groups[2].Value);
                        info.Height = int.Parse(v.Groups[3].Value);
                        var f = FpsRx.Match(l);
                        if (!f.Success) f = TbrRx.Match(l);
                        if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 1 && fps < 241)
                            info.Fps = fps;
                        continue;
                    }
                }
                else
                {
                    var r = Rot.Match(l);
                    if (r.Success) rotation = double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                    else if (l.TrimStart().StartsWith("Stream #")) { /* later streams: stop looking for rotation */ }
                }
            }
            if (!haveVideo) throw new EncodeException($"Tệp “{name}” không chứa luồng video hoặc định dạng không được hỗ trợ.");
            if (!haveDur || info.Duration <= 0) throw new EncodeException($"Không đọc được thời lượng của “{name}”.");
            int rot = ((int)Math.Round(rotation) % 360 + 360) % 360;
            if (rot == 90 || rot == 270) { var t = info.Width; info.Width = info.Height; info.Height = t; }
            return info;
        }
    }

    /// <summary>Decodes frames with ffmpeg into raw BGR24 buffers (preview, thumbnails).</summary>
    public static class Frames
    {
        static string F(double v) => Math.Max(0, v).ToString("0.000000", CultureInfo.InvariantCulture);

        /// <summary>One frame at <paramref name="t"/>, scaled to w×h. fast = nearest keyframe (thumbnails).</summary>
        public static async Task<byte[]> GrabAsync(string path, double t, int w, int h, bool fast, CancellationToken ct)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (fast) args.AddRange(new[] { "-skip_frame", "nokey", "-noaccurate_seek" });
            args.AddRange(new[] { "-ss", F(t), "-i", path, "-map", "0:v:0", "-frames:v", "1",
                "-vf", $"scale={w}:{h}:flags=bilinear", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" });
            var frames = await ReadAsync(args, w * h * 3, 1, ct);
            return frames.Count > 0 ? frames[0] : null;
        }

        /// <summary>Graph for the preview: the export's per-clip chain, converted to BGR for WPF.</summary>
        public static string PreviewGraph(ClipEdit e, double srcW, double srcH, int W, int H, int fps) =>
            string.Join(";", EditMath.FfmpegParts(0, e, srcW, srcH, W, H, fps)) + ";[v0]format=bgr24[out]";

        public static List<string> RenderArgs(string path, double start, double duration, string graph, int maxFrames)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(start) };
            if (duration > 0) args.AddRange(new[] { "-t", F(duration) });
            args.AddRange(new[] { "-i", path, "-filter_complex", graph, "-map", "[out]" });
            if (maxFrames > 0) args.AddRange(new[] { "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgr24", "-" });
            return args;
        }

        /// <summary>Runs ffmpeg and reads fixed-size frames from stdout, calling back as they arrive.</summary>
        public static Task StreamAsync(IList<string> args, int frameBytes, Action<int, byte[]> onFrame, CancellationToken ct)
        {
            var ffmpeg = Tools.FFmpeg ?? throw EncodeException.NoFFmpeg();
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using (var p = new Process { StartInfo = Proc.Info(ffmpeg, args) })
                {
                    p.Start();
                    using (ct.Register(() => Proc.Kill(p)))
                    {
                        string err = "";
                        var errTask = Task.Run(() => { err = p.StandardError.ReadToEnd(); });
                        var s = p.StandardOutput.BaseStream;
                        int index = 0;
                        while (true)
                        {
                            var buf = new byte[frameBytes];
                            int got = 0;
                            while (got < frameBytes)
                            {
                                int n = s.Read(buf, got, frameBytes - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            if (got < frameBytes) break;
                            if (ct.IsCancellationRequested) break;
                            onFrame(index++, buf);
                        }
                        p.WaitForExit();
                        try { errTask.Wait(); } catch { }
                        ct.ThrowIfCancellationRequested();
                        if (p.ExitCode != 0 && index == 0)
                            throw new EncodeException(err.Trim().Length > 300 ? err.Trim().Substring(err.Trim().Length - 300) : err.Trim());
                    }
                }
            }, ct);
        }

        public static async Task<List<byte[]>> ReadAsync(IList<string> args, int frameBytes, int max, CancellationToken ct)
        {
            var list = new List<byte[]>();
            await StreamAsync(args, frameBytes, (i, b) => { if (list.Count < max) list.Add(b); }, ct);
            return list;
        }
    }

    /// <summary>Finds scene cuts with ffmpeg's scdet filter, cached per file.</summary>
    public static class SceneDetector
    {
        public const double Threshold = 10.0;   // scdet score (0–100); lower = more sensitive
        public const double MinGap = 0.25;      // drop cuts closer than this (flashes, fades)

        public static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNEGifMaker", "scenes");

        public static List<double> Cached(string src)
        {
            try
            {
                var p = Path.Combine(CacheDir, GifEncoder.CacheKey(src) + ".json");
                if (!File.Exists(p)) return null;
                using (var f = File.OpenRead(p))
                    return (List<double>)new DataContractJsonSerializer(typeof(List<double>)).ReadObject(f);
            }
            catch { return null; }
        }

        public static void Store(List<double> cuts, string src)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var p = Path.Combine(CacheDir, GifEncoder.CacheKey(src) + ".json");
                using (var f = File.Create(p))
                    new DataContractJsonSerializer(typeof(List<double>)).WriteObject(f, cuts);
            }
            catch { }
        }

        static readonly Regex TimeRx = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)");

        /// <summary>Analyzes the whole video. onUpdate(cuts, progress 0…1) is called from a worker thread.</summary>
        public static async Task<List<double>> DetectAsync(string src, double duration, Action<List<double>, double> onUpdate, CancellationToken ct)
        {
            var ffmpeg = Tools.FFmpeg ?? throw EncodeException.NoFFmpeg();
            var cuts = new List<double>();
            var last = DateTime.MinValue;
            var args = new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "info", "-stats_period", "0.5",
                "-i", src, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", "scale=256:-2:flags=fast_bilinear,format=rgb24,scdet=threshold=" + Threshold.ToString(CultureInfo.InvariantCulture),
                "-f", "null", "-",
            };
            void Line(string line)
            {
                int i = line.IndexOf("lavfi.scd.time: ", StringComparison.Ordinal);
                if (i >= 0)
                {
                    var num = new string(line.Substring(i + 16).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                    if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    {
                        if (t > MinGap && t < duration - 0.05 && (cuts.Count == 0 || t - cuts[cuts.Count - 1] >= MinGap))
                        {
                            cuts.Add(t);
                            onUpdate?.Invoke(new List<double>(cuts), -1);
                        }
                    }
                    return;
                }
                var m = TimeRx.Match(line);
                if (m.Success && duration > 0 && (DateTime.UtcNow - last).TotalSeconds > 0.25)
                {
                    last = DateTime.UtcNow;
                    double s = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    onUpdate?.Invoke(new List<double>(cuts), Math.Min(1, s / duration));
                }
            }
            await Proc.RunAsync(ffmpeg, args, ct, Line, failPrefix: "Phân tích cảnh thất bại. ");
            return cuts;
        }
    }
}
