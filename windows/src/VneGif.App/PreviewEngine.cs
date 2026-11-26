using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VneGif.App
{
    /// <summary>Small decoded frames for the timeline, overview and sequence cards.</summary>
    public static class Thumbs
    {
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);
        static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
        static readonly LinkedList<string> Order = new LinkedList<string>();

        public static async Task<BitmapSource> GetAsync(string path, double t, int w, int h, bool fast, CancellationToken ct = default)
        {
            var key = $"{path}|{Math.Round(t * 100)}|{w}x{h}|{fast}";
            if (Cache.TryGetValue(key, out var hit)) return hit;
            try { await Gate.WaitAsync(ct); }
            catch (OperationCanceledException) { return null; }
            try
            {
                if (Cache.TryGetValue(key, out hit)) return hit;
                ct.ThrowIfCancellationRequested();
                var buf = await Frames.GrabAsync(path, t, w, h, fast, ct);
                if (buf == null) return null;
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, buf, w * 3);
                bmp.Freeze();
                Cache[key] = bmp;
                Order.AddLast(key);
                while (Order.Count > 800) { Cache.Remove(Order.First.Value); Order.RemoveFirst(); }
                return bmp;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception) { return null; }
            finally { Gate.Release(); }
        }
    }

    /// <summary>
    /// Live preview. Frames are rendered by ffmpeg with exactly the export's filter graph, so the
    /// picture is the GIF (framing, color, sharpening, speed, fps). The selected range is decoded
    /// once into memory and played from there: looping and "Phát toàn bộ" never stall.
    /// While paused, the exact frame under the playhead is rendered on demand.
    /// </summary>
    public sealed class PreviewEngine
    {
        readonly AppState s;
        readonly Dispatcher ui;
        readonly Stopwatch sw = Stopwatch.StartNew();
        readonly SemaphoreSlim renderGate = new SemaphoreSlim(2);

        public WriteableBitmap Bitmap { get; private set; }
        public event Action BitmapChanged;
        public event Action TimeChanged;
        public double Time { get; private set; }
        public bool Playing { get; private set; }
        /// <summary>Why there is no picture (ffmpeg message), or null.</summary>
        public string Issue { get; private set; }

        Clip clip;

        sealed class Range
        {
            public string Key;
            public Guid Id;
            public int W, H, Fps;
            public double Speed, In;
            public readonly List<byte[]> Frames = new List<byte[]>();
            public bool Done;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public long LastUse;
            public long Bytes => (long)Frames.Count * W * H * 3;
        }
        readonly Dictionary<Guid, Range> ranges = new Dictionary<Guid, Range>();
        Range stale;                 // previous range of the selected clip, shown until the new one catches up
        int playIndex;
        double playStart;
        Range shownRange;
        int shownIndex = -1;
        const long MemoryBudget = 700L * 1024 * 1024;
        const long RangeBudget = 240L * 1024 * 1024;

        public PreviewEngine(AppState state, Dispatcher dispatcher)
        {
            s = state;
            ui = dispatcher;
            CompositionTarget.Rendering += OnRendering;
            debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (o, e) =>
            {
                debounce.Stop();
                if (clip != null) EnsureRange(clip);
            }, ui);
            debounce.Stop();
        }

        // MARK: Sizes and keys

        /// <summary>Preview size: the output size, capped for speed and for memory on long selections.</summary>
        (int W, int H) PreviewSize(Clip c)
        {
            var (W, H) = s.OutputSize;
            double f = W > 960 ? 960.0 / W : 1;
            double frames = Math.Ceiling(c.Length / c.Edit.Speed * s.S.Fps) + 1;
            double bytes = frames * W * f * H * f * 3;
            if (bytes > RangeBudget) f *= Math.Sqrt(RangeBudget / bytes);
            int w = Math.Max(64, (int)(W * f) / 2 * 2), h = Math.Max(64, (int)(H * f) / 2 * 2);
            return (w, h);
        }

        string KeyFor(Clip c)
        {
            var (w, h) = PreviewSize(c);
            var ci = CultureInfo.InvariantCulture;
            return $"{c.Path}|{c.In.ToString("R", ci)}|{c.Out.ToString("R", ci)}|{c.Edit.Key()}|{w}x{h}|{s.S.Fps}";
        }

        // MARK: Range rendering

        readonly DispatcherTimer debounce;

        void EnsureRange(Clip c)
        {
            var key = KeyFor(c);
            ranges.TryGetValue(c.Id, out var old);
            if (old != null && old.Key == key) return;
            if (old != null)
            {
                old.Cts.Cancel();
                if (c == clip && old.Frames.Count > 0) stale = old;
            }
            var (w, h) = PreviewSize(c);
            var r = new Range { Key = key, Id = c.Id, W = w, H = h, Fps = s.S.Fps, Speed = c.Edit.Speed, In = c.In, LastUse = sw.ElapsedMilliseconds };
            ranges[c.Id] = r;
            Render(r, c.Path, c.In, c.Out, c.FrameDur, c.W, c.H, c.Edit.Clone());
        }

        async void Render(Range r, string path, double a, double b, double fd, double srcW, double srcH, ClipEdit edit)
        {
            try
            {
                await renderGate.WaitAsync(r.Cts.Token);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                double start = Math.Max(0, a - fd / 2), dur = Math.Max(fd, b - fd / 2 - start);
                var graph = Frames.PreviewGraph(edit, srcW, srcH, r.W, r.H, r.Fps);
                await Frames.StreamAsync(Frames.RenderArgs(path, start, dur, graph, 0), r.W * r.H * 3, (i, buf) =>
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (r.Cts.IsCancellationRequested) return;
                        r.Frames.Add(buf);
                        if (r.Frames.Count == 1 && clip != null && r.Id == clip.Id) SetIssue(null);
                    })), r.Cts.Token);
                r.Done = true;
                if (r.Frames.Count == 0 && clip != null && r.Id == clip.Id) SetIssue("Không giải mã được hình ảnh trong đoạn đã chọn.");
                if (stale != null && clip != null && r.Id == clip.Id) stale = null;
                Evict();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                r.Done = true;
                if (clip != null && r.Id == clip.Id) SetIssue(ex.Message);
            }
            finally { renderGate.Release(); }
        }

        void Evict()
        {
            long total = ranges.Values.Sum(x => x.Bytes) + (stale?.Bytes ?? 0);
            foreach (var r in ranges.Values.Where(x => clip == null || x.Id != clip.Id).OrderBy(x => x.LastUse).ToList())
            {
                if (total <= MemoryBudget) break;
                total -= r.Bytes;
                r.Cts.Cancel();
                ranges.Remove(r.Id);
            }
        }

        void SetIssue(string msg)
        {
            if (Issue == msg) return;
            Issue = msg;
            s.Notify();
        }

        // MARK: API used by AppState

        public void Attach(Clip c)
        {
            clip = c;
            stale = null;
            shownRange = null;
            shownIndex = -1;
            Issue = null;
            EnsureRange(c);
        }

        public void Detach()
        {
            clip = null;
            Playing = false;
            stale = null;
            Bitmap = null;
            BitmapChanged?.Invoke();
        }

        public void Forget(Guid id)
        {
            if (ranges.TryGetValue(id, out var r)) { r.Cts.Cancel(); ranges.Remove(id); }
        }

        /// <summary>The selected clip's in/out or edit changed.</summary>
        public void RangeChanged()
        {
            debounce.Stop();
            debounce.Start();
            if (!Playing) RequestStill(Time);
        }

        /// <summary>Output size or fps changed: every range is out of date.</summary>
        public void OutputChanged()
        {
            if (clip == null) return;
            RangeChanged();
        }

        public void AllRangesChanged() => RangeChanged();

        /// <summary>"Phát toàn bộ": render every clip's range (two at a time).</summary>
        public void PrepareAll()
        {
            foreach (var c in s.Clips) EnsureRange(c);
        }

        public void Play()
        {
            if (clip == null) return;
            if (Time < clip.In - 1e-6 || Time >= clip.Out - clip.FrameDur / 2) Time = clip.In;
            debounce.Stop();
            EnsureRange(clip);
            playIndex = IndexFor(Time);
            playStart = sw.Elapsed.TotalSeconds;
            Playing = true;
            s.Notify();
        }

        public void Pause()
        {
            if (!Playing) return;
            Playing = false;
            RequestStill(Time);
            s.Notify();
        }

        public void Loop()
        {
            playIndex = 0;
            playStart = sw.Elapsed.TotalSeconds;
        }

        public void Seek(double t)
        {
            if (clip == null) return;
            if (Playing)
            {
                Time = t;
                playIndex = IndexFor(t);
                playStart = sw.Elapsed.TotalSeconds;
                TimeChanged?.Invoke();
            }
            else RequestStill(t);
        }

        int IndexFor(double t) => clip == null ? 0 : Math.Max(0, (int)Math.Floor((t - clip.In) / clip.Edit.Speed * s.S.Fps + 1e-6));

        // MARK: Playback clock

        void OnRendering(object sender, EventArgs e)
        {
            if (!Playing || clip == null) return;
            if (!ranges.TryGetValue(clip.Id, out var r)) { EnsureRange(clip); return; }
            double now = sw.Elapsed.TotalSeconds;
            int idx = playIndex + (int)Math.Floor((now - playStart) * r.Fps);
            // A fresh range (after an edit) may not have reached this frame yet: keep showing the old one.
            if (idx >= r.Frames.Count && !r.Done && stale != null && stale.Id == r.Id && idx < stale.Frames.Count) r = stale;
            r.LastUse = sw.ElapsedMilliseconds;
            if (idx >= r.Frames.Count)
            {
                if (r.Done)
                {
                    if (r.Frames.Count == 0) { Playing = false; s.Notify(); return; }
                    s.ReachedEnd();
                    return;
                }
                // Still rendering: wait at the first missing frame.
                playIndex = r.Frames.Count;
                playStart = now;
                return;
            }
            if (r != shownRange || idx != shownIndex) Show(r.Frames[idx], r.W, r.H);
            shownRange = r;
            shownIndex = idx;
            double t = Math.Min(clip.Out, r.In + idx * r.Speed / r.Fps);
            if (Math.Abs(t - Time) > 1e-9) { Time = t; TimeChanged?.Invoke(); }
        }

        void Show(byte[] buf, int w, int h)
        {
            if (Bitmap == null || Bitmap.PixelWidth != w || Bitmap.PixelHeight != h)
            {
                Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                BitmapChanged?.Invoke();
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 3, 0);
        }

        // MARK: Stills (paused / scrubbing)

        sealed class Want { public Clip Clip; public double T; public string Key; }
        Want wanted;
        string shownStill;
        bool stillBusy;

        void RequestStill(double t)
        {
            if (clip == null) return;
            Time = t;
            TimeChanged?.Invoke();
            // Instant approximation from the rendered range; the exact frame follows.
            if (ranges.TryGetValue(clip.Id, out var r) && r.Key == KeyFor(clip) && t >= clip.In && t < clip.Out)
            {
                int idx = IndexFor(t);
                if (idx < r.Frames.Count) { Show(r.Frames[idx], r.W, r.H); shownRange = r; shownIndex = idx; }
            }
            wanted = new Want { Clip = clip, T = t, Key = KeyFor(clip) + "@" + AppState.MidFrame(t, clip).ToString("R", CultureInfo.InvariantCulture) };
            PumpStill();
        }

        async void PumpStill()
        {
            if (stillBusy) return;
            stillBusy = true;
            try
            {
                while (wanted != null && wanted.Key != shownStill)
                {
                    var w = wanted;
                    var c = w.Clip;
                    var (pw, ph) = PreviewSize(c);
                    var graph = Frames.PreviewGraph(c.Edit.Clone(), c.W, c.H, pw, ph, s.S.Fps);
                    List<byte[]> frames;
                    try
                    {
                        frames = await Frames.ReadAsync(Frames.RenderArgs(c.Path, AppState.MidFrame(w.T, c), 0, graph, 1), pw * ph * 3, 1, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        shownStill = w.Key;
                        if (c == clip) SetIssue(ex.Message);
                        continue;
                    }
                    shownStill = w.Key;
                    if (c == clip && !Playing && frames.Count > 0)
                    {
                        Show(frames[0], pw, ph);
                        shownRange = null;
                        SetIssue(null);
                    }
                }
            }
            finally { stillBusy = false; }
        }
    }
}
