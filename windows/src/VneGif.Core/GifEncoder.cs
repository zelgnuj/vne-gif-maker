using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VneGif
{
    /// <summary>Port of GifEncoder.swift: palettegen/paletteuse, gifsicle passes, size optimizer.</summary>
    public static class GifEncoder
    {
        /// <summary>
        /// Lossy steps, mildest first: gifsicle --lossy amount and palette size. Fewer colors come
        /// from ffmpeg's palettegen (better choice and dithering than gifsicle --colors). A stronger
        /// step posterizes faces visibly, so dropping 1–2 fps is preferred beyond step 5.
        /// </summary>
        public static readonly (int Lossy, int Colors)[] SqueezeSteps =
            { (40, 256), (80, 256), (120, 256), (120, 192), (160, 128) };
        /// <summary>Size of each step relative to the lossless-optimized GIF (measured on 680×408 footage).</summary>
        public static readonly double[] SqueezeFactors = { 0.889, 0.858, 0.83, 0.75, 0.63 };
        public const int HardMinFps = 8;
        public const int HardMinWidth = 360;

        public static (int W, int H) Size(int width, double aspect)
        {
            int w = Math.Max(2, width / 2 * 2);
            int h = Math.Max(2, (int)Math.Round(w / Math.Max(aspect, 0.01) / 2, MidpointRounding.AwayFromZero) * 2);
            return (w, h);
        }

        static string F6(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

        /// <summary>One decode pass, one GIF per palette size.</summary>
        public static List<string> Arguments(GifRequest r, int W, int H, int fps, IList<(int Colors, string Path)> outputs)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            var parts = new List<string>();
            for (int i = 0; i < r.Clips.Count; i++)
            {
                var c = r.Clips[i];
                args.AddRange(new[] { "-ss", F6(c.Start), "-t", F6(c.Duration), "-i", c.Path });
                parts.AddRange(EditMath.FfmpegParts(i, c.Edit, c.SrcW, c.SrcH, W, H, fps));
            }
            var inputs = string.Concat(Enumerable.Range(0, r.Clips.Count).Select(i => $"[v{i}]"));
            int k = outputs.Count;
            parts.Add(inputs + $"concat=n={r.Clips.Count}:v=1:a=0,split={2 * k}"
                      + string.Concat(Enumerable.Range(0, k).Select(i => $"[a{i}][b{i}]")));
            for (int i = 0; i < k; i++)
            {
                parts.Add($"[a{i}]palettegen=stats_mode=full" + (outputs[i].Colors < 256 ? $":max_colors={outputs[i].Colors}" : "") + $"[p{i}]");
                parts.Add($"[b{i}][p{i}]paletteuse={DitherInfo.Filter(r.Dither)}:diff_mode=rectangle[o{i}]");
            }
            args.Add("-filter_complex");
            args.Add(string.Join(";", parts));
            for (int i = 0; i < k; i++) args.AddRange(new[] { "-map", $"[o{i}]", "-an", "-loop", "0", outputs[i].Path });
            return args;
        }

        sealed class Cand
        {
            public string Path; public long Size; public int W, H, Fps, Level; public bool Below;
        }

        static long Bytes(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

        /// <summary>
        /// Encodes; if over the limit and AutoFit, optimizes in the order set by Priority:
        ///  • KeepSize:      lossless optimize → lossy steps → lower fps (≥ MinFps) → lower width (≥ MinWidth)
        ///  • KeepSharpness: lossless optimize → lower fps → lower width → lossy steps
        /// With Force, goes on below the floors (to the hard floors) until it fits.
        /// </summary>
        public static async Task<GifResult> MakeGifAsync(GifRequest req, string output, Action<string> onProgress, CancellationToken ct)
        {
            var ffmpeg = Tools.FFmpeg ?? throw EncodeException.NoFFmpeg();
            var gs = Tools.Gifsicle;
            if (req.Clips.Count == 0) throw new EncodeException("Chưa có đoạn video nào.");
            var tmpDir = Path.Combine(Path.GetTempPath(), "vnegif-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                int n = 0;
                // gifsicle on Windows opens files with ANSI APIs: work with short relative names
                // inside the temp folder so a Vietnamese user name in %TEMP% can't break it.
                string Tmp() { n++; return Path.Combine(tmpDir, $"t{n}.gif"); }

                var bases = new Dictionary<int, string>();
                int baseW = 0, baseH = 0, baseFps = 0;

                async Task Prepare(IEnumerable<int> colors)
                {
                    var missing = colors.Distinct().OrderByDescending(c => c).Where(c => !bases.ContainsKey(c)).ToList();
                    if (missing.Count == 0) return;
                    var outs = missing.Select(c => (c, Tmp())).ToList();
                    await Proc.RunAsync(ffmpeg, Arguments(req, baseW, baseH, baseFps, outs), ct);
                    foreach (var o in outs) bases[o.Item1] = o.Item2;
                }
                async Task<string> Base(int colors) { if (!bases.ContainsKey(colors)) await Prepare(new[] { colors }); return bases[colors]; }

                Task Gifsicle(int lossy, string src, string dst)
                {
                    var a = new List<string> { "-O2", "--no-warnings" };
                    if (lossy > 0) a.Add("--lossy=" + lossy);
                    a.AddRange(new[] { "-o", Path.GetFileName(dst), Path.GetFileName(src) });
                    return Proc.RunAsync(gs, a, ct, workDir: tmpDir);
                }

                /// level 0 = lossless -O2, 1… = SqueezeSteps[level-1].
                async Task<string> Squeeze(int level)
                {
                    var step = level > 0 ? SqueezeSteps[level - 1] : (0, 256);
                    var src = await Base(step.Item2);
                    if (gs == null) return src;
                    var dst = Tmp();
                    await Gifsicle(step.Item1, src, dst);
                    return Bytes(dst) > 0 && Bytes(dst) < Bytes(src) ? dst : src;
                }

                int minFps = Math.Min(req.MinFps, req.Fps);
                int minWidth = Math.Min(req.MinWidth, req.Width);
                bool belowFloor = false;
                long limit = req.LimitBytes;
                int width = req.Width, fps = req.Fps;
                int attempt = 0, lastLevel = 0;
                Cand best = null;
                int S = SqueezeSteps.Length;

                GifResult Finish(Cand r)
                {
                    if (File.Exists(output)) File.Delete(output);
                    File.Move(r.Path, output);
                    return new GifResult
                    {
                        Path = output, Bytes = r.Size, Width = r.W, Height = r.H, Fps = r.Fps, Attempts = attempt,
                        LimitBytes = limit, SqueezeLevel = r.Level, BelowFloor = r.Below,
                    };
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    var (w, h) = Size(width, req.Aspect);
                    onProgress?.Invoke(attempt == 1 ? $"Đang xuất GIF ({w} px · {fps} fps)…" : $"Đang xuất lại ở {w} px · {fps} fps…");
                    bases.Clear(); baseW = w; baseH = h; baseFps = fps;
                    // The previous attempt needed the strongest step and this one is sized for it:
                    // skip the uncompressed encode and go straight to the two strongest steps.
                    bool deep = lastLevel >= S && gs != null;
                    int deepFirst = S - 1;
                    if (lastLevel > 0)
                    {
                        int from = deep ? deepFirst : Math.Max(1, lastLevel - 1);
                        var need = (deep ? new int[0] : new[] { 256 }).Concat(Enumerable.Range(from, S - from + 1).Select(l => SqueezeSteps[l - 1].Colors));
                        await Prepare(need);
                    }

                    string cur = deep ? bases[SqueezeSteps[deepFirst - 1].Colors] : await Squeeze(0);
                    int level = 0;
                    void Record()
                    {
                        long sz = Bytes(cur);
                        if (best == null || sz < best.Size || sz <= limit)
                            best = new Cand { Path = cur, Size = sz, W = w, H = h, Fps = fps, Level = level, Below = belowFloor };
                    }
                    if (!deep)
                    {
                        Record();
                        if (Bytes(cur) <= limit || !req.AutoFit) return Finish(best);
                    }

                    bool atFloor = fps <= minFps && width <= minWidth;
                    if (gs != null && (req.Priority == FitPriority.KeepSize || atFloor || deep))
                    {
                        // Jump to the mildest step expected to fit (with a little margin); if none is
                        // expected to, go straight to the strongest to learn how far off we are.
                        double needRatio = limit * 0.97 / Math.Max(Bytes(cur), 1);
                        int idx = Array.FindIndex(SqueezeFactors, f => f <= needRatio);
                        int guess = (idx >= 0 ? idx : SqueezeFactors.Length - 1) + 1;
                        int first = deep ? deepFirst : Math.Max(guess, lastLevel > 0 ? lastLevel - 1 : 1);
                        var levels = Enumerable.Range(first, S - first + 1).ToList();
                        await Prepare(levels.Select(l => SqueezeSteps[l - 1].Colors));
                        onProgress?.Invoke($"Vượt giới hạn — đang nén thêm (giữ {w} px · {fps} fps)…");
                        // gifsicle is single-threaded: run the candidate levels side by side,
                        // then take the mildest one that fits.
                        var jobs = levels.Select(l => (Level: l, Src: bases[SqueezeSteps[l - 1].Colors], Out: Tmp())).ToList();
                        await Task.WhenAll(jobs.Select(j => Gifsicle(SqueezeSteps[j.Level - 1].Lossy, j.Src, j.Out)));
                        foreach (var j in jobs)
                        {
                            cur = Bytes(j.Out) > 0 && Bytes(j.Out) < Bytes(j.Src) ? j.Out : j.Src;
                            level = j.Level;
                            lastLevel = j.Level;
                            Record();
                            if (Bytes(cur) <= limit) return Finish(best);
                        }
                    }

                    // Lower fps first, then width — never below the floors.
                    double curSize = Bytes(cur);
                    double remaining = limit * 0.95 / Math.Max(curSize, 1);
                    int oldW = width, oldFps = fps;
                    if (fps > minFps)
                    {
                        int nf = Math.Max(minFps, (int)Math.Floor(fps * Math.Sqrt(remaining)));
                        remaining *= (double)fps / Math.Max(nf, 1);
                        fps = nf;
                    }
                    if (remaining < 1 && width > minWidth)
                        width = Math.Max(minWidth, (int)(width * Math.Sqrt(remaining)) / 2 * 2);
                    if (width == oldW && fps == oldFps)
                    {
                        if (fps > minFps) fps -= 1;
                        else if (width > minWidth) width = Math.Max(minWidth, width - 40);
                        else if (req.Force && (minFps > HardMinFps || minWidth > HardMinWidth))
                        {
                            // At the user's floors and fully compressed: lower both a bit more, evenly.
                            minFps = Math.Min(minFps, HardMinFps);
                            minWidth = Math.Min(minWidth, HardMinWidth);
                            belowFloor = true;
                            double r = Math.Sqrt(limit * 0.95 / Math.Max(curSize, 1));
                            fps = Math.Max(minFps, Math.Min(fps - 1, (int)Math.Floor(fps * Math.Sqrt(r))));
                            width = Math.Max(minWidth, Math.Min(width - 20, (int)(width * Math.Sqrt(r)) / 2 * 2));
                        }
                        else return Finish(best);   // at the floors and fully compressed
                    }
                    if (attempt >= (req.Force ? 12 : 8)) return Finish(best);
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        /// <summary>Stable key for per-file caches (path + size + modification date).</summary>
        public static string CacheKey(string path)
        {
            long size = 0; double mtime = 0;
            try
            {
                var fi = new FileInfo(path);
                size = fi.Length;
                mtime = (fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            }
            catch { }
            var key = $"{path}|{size}|{mtime.ToString("R", CultureInfo.InvariantCulture)}";
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return string.Concat(h.Take(10).Select(b => b.ToString("x2")));
            }
        }
    }
}
