using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VneGif.App
{
    public sealed class Clip
    {
        public readonly Guid Id = Guid.NewGuid();
        public string Path;
        public MediaInfo Info;
        public double Duration => Info.Duration;
        public double W => Info.Width;
        public double H => Info.Height;
        public double FrameDur = 1.0 / 30;           // from the stream's frame rate
        public List<double> Cuts = new List<double>(); // scene cut times (source seconds, frame-aligned)
        public double? SceneProgress;                  // 0…1 while analyzing
        public bool ScenesAnalyzed;
        public BitmapSource[] Thumbs = new BitmapSource[0];
        public BitmapSource CardThumb;
        public double CardThumbTime = -1;
        public double In, Out;
        public ClipEdit Edit = new ClipEdit();
        public double Length => Out - In;              // source seconds
        public double OutLength => Length / Edit.Speed; // seconds in the GIF
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string FileName => System.IO.Path.GetFileName(Path);
        public bool IsVertical => H > W;
    }

    public enum StatusKind { Idle, Encoding, Done, Failed }

    /// <summary>Port of AppState.swift (minus AVPlayer specifics; playback lives in PreviewEngine).</summary>
    public sealed class AppState
    {
        public static readonly int[] WidthOptions = { 480, 600, 680, 800, 1000, 1200 };
        public static readonly int[] FpsOptions = { 10, 12, 15, 20, 25 };
        public const double MinLength = 0.3;

        public readonly List<Clip> Clips = new List<Clip>();
        public Guid? SelectedId { get; private set; }
        public Settings S;
        public readonly PreviewEngine Preview;
        public readonly TimelineZoom Timeline = new TimelineZoom();
        readonly Dispatcher ui;

        public StatusKind Status { get; private set; }
        public string StatusMessage { get; private set; } = "";
        public GifResult Result { get; private set; }
        public string Preparing { get; private set; }
        public SavedSession SavedSession;

        /// <summary>Anything shown changed (coalesced to one call per dispatcher pass).</summary>
        public event Action Changed;
        /// <summary>Clip list, order, thumbnails or selection changed (sequence strip).</summary>
        public event Action ClipsChanged;
        public event Action<string, string> Error;

        public AppState(Dispatcher dispatcher)
        {
            ui = dispatcher;
            S = Store.Load<Settings>("settings.json") ?? new Settings();
            Preview = new PreviewEngine(this, dispatcher);
            Timeline.Changed += () => Notify();
        }

        // MARK: Change notification

        bool notifyPending, clipsPending;
        public void Notify(bool clips = false)
        {
            if (clips) clipsPending = true;
            if (notifyPending) return;
            notifyPending = true;
            ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                notifyPending = false;
                bool c = clipsPending;
                clipsPending = false;
                if (c) ClipsChanged?.Invoke();
                Changed?.Invoke();
            }));
            ScheduleSave();
        }

        DispatcherTimer saveTimer;
        void ScheduleSave()
        {
            if (restoring) return;
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (s, e) =>
                {
                    saveTimer.Stop();
                    SaveNow();
                }, ui);
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        string lastSaved;
        public void SaveNow()
        {
            Store.Save("settings.json", S);
            if (restoring) return;
            var s = new SavedSession
            {
                Clips = Clips.Select(c => new SavedClip { Path = c.Path, InPoint = c.In, OutPoint = c.Out, Edit = c.Edit }).ToList(),
                Selected = SelectedIndex,
            };
            var key = string.Join("\n", s.Clips.Select(c => c.Path + c.InPoint + c.OutPoint + c.Edit.Key())) + s.Selected;
            if (key == lastSaved) return;
            lastSaved = key;
            Store.Save("session.json", s);
        }

        // MARK: Derived

        public int SelectedIndex => Clips.FindIndex(c => c.Id == SelectedId);
        public Clip Selected => Clips.FirstOrDefault(c => c.Id == SelectedId);
        public double TotalDuration => Clips.Sum(c => c.OutLength);
        public bool IsEncoding => Status == StatusKind.Encoding;
        public double CurrentTime => Preview.Time;

        public Aspect Aspect => VneGif.Aspect.FromId(S.Aspect) ?? VneGif.Aspect.Landscape[4];

        /// <summary>Output aspect (w/h). "Gốc" = aspect of the first clip.</summary>
        public double OutputAspect
        {
            get
            {
                var a = Aspect;
                if (a.Ratio.HasValue) return a.Ratio.Value;
                if (a == VneGif.Aspect.Custom) return Math.Min(Math.Max(S.CustomW / Math.Max(S.CustomH, 0.01), 0.2), 5);
                if (Clips.Count > 0 && Clips[0].H > 0) return Clips[0].W / Clips[0].H;
                return 16.0 / 9.0;
            }
        }
        public string AspectLabel => Aspect == VneGif.Aspect.Custom ? $"{Num.Fmt(S.CustomW, 2)}:{Num.Fmt(S.CustomH, 2)}" : Aspect.Label;
        public (int W, int H) OutputSize => GifEncoder.Size(S.Width, OutputAspect);

        /// <summary>e.g. "2 đoạn · 5,2 giây · 680 × 408 px".</summary>
        public string Summary
        {
            get
            {
                if (Clips.Count == 0) return "";
                var (w, h) = OutputSize;
                return $"{Clips.Count} đoạn · {Num.Fmt(TotalDuration, 1)} giây · {w} × {h} px";
            }
        }

        public void SettingsChanged()
        {
            Preview.OutputChanged();
            Notify();
        }

        // MARK: Loading

        Task loadChain = Task.CompletedTask;
        bool restoring;

        public void Add(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            Enqueue(async () => { foreach (var p in list) await AddOne(p, null); });
        }

        /// <summary>Loads run one after another, so restored and newly opened clips keep their order.</summary>
        void Enqueue(Func<Task> work)
        {
            var prev = loadChain;
            loadChain = Run();
            async Task Run() { try { await prev; } catch { } await work(); }
        }

        public void OfferSession()
        {
            var s = Store.Load<SavedSession>("session.json");
            if (s != null && s.Clips != null && s.Clips.Count > 0) SavedSession = s;
        }

        /// <summary>Reopens the clips, trims and edits from the last session.</summary>
        public void RestoreSession()
        {
            var saved = SavedSession;
            if (saved == null) return;
            SavedSession = null;
            restoring = true;
            Notify();
            Enqueue(async () =>
            {
                var missing = new List<string>();
                var ids = new List<Guid>();
                foreach (var c in saved.Clips)
                {
                    if (!File.Exists(c.Path)) { missing.Add(System.IO.Path.GetFileName(c.Path)); continue; }
                    var id = await AddOne(c.Path, c);
                    if (id.HasValue) ids.Add(id.Value);
                }
                if (saved.Selected >= 0 && saved.Selected < ids.Count) Select(ids[saved.Selected], false);
                else if (ids.Count > 0) Select(ids[0], false);
                restoring = false;
                ScheduleSave();
                if (missing.Count > 0)
                    Error?.Invoke("Không mở được tệp", $"Không tìm thấy {missing.Count} video của phiên trước (ổ đĩa chưa kết nối hoặc tệp đã bị di chuyển): "
                                  + string.Join(", ", missing));
            });
        }

        async Task<Guid?> AddOne(string path, SavedClip restore)
        {
            try
            {
                Preparing = $"Đang đọc “{System.IO.Path.GetFileName(path)}”…";
                Notify();
                MediaInfo info;
                try { info = await Probe.ReadAsync(path); }
                finally { Preparing = null; Notify(); }
                double dur = info.Duration;
                double fd = info.Fps > 1 ? 1 / info.Fps : 1.0 / 30;
                // Default selection: whole clip if short, else first 3 s.
                double outP = dur <= 6 ? dur : 3;
                var clip = new Clip { Path = path, Info = info, FrameDur = fd, In = 0, Out = Math.Floor(outP / fd) * fd };
                if (clip.Out < MinLength) clip.Out = Math.Min(dur, MinLength);
                clip.Edit.Fit = info.IsVertical;   // vertical → fit + blurred background by default
                if (restore != null)
                {
                    double a = Math.Min(Math.Max(0, restore.InPoint), dur), b = Math.Min(Math.Max(a, restore.OutPoint), dur);
                    if (b - a >= MinLength) { clip.In = a; clip.Out = b; }
                    if (restore.Edit != null) clip.Edit = restore.Edit;
                }
                Clips.Add(clip);
                if (restore == null)
                {
                    SavedSession = null;
                    if (Status != StatusKind.Idle && !IsEncoding) SetStatus(StatusKind.Idle, "");
                    Select(clip.Id, false);
                }
                Notify(true);
                MakeThumbnails(clip);
                AnalyzeScenes(clip.Id);
                return clip.Id;
            }
            catch (Exception ex)
            {
                Error?.Invoke("Không mở được tệp", $"“{System.IO.Path.GetFileName(path)}”: {ex.Message}");
                return null;
            }
        }

        async void MakeThumbnails(Clip c)
        {
            const int n = 16;
            int th = 54, tw = Math.Max(2, (int)Math.Round(th * c.W / Math.Max(c.H, 1)) / 2 * 2);
            var imgs = new BitmapSource[n];
            var tasks = Enumerable.Range(0, n).Select(async i =>
            {
                imgs[i] = await Thumbs.GetAsync(c.Path, c.Duration * (i + 0.5) / n, tw, th, true);
                c.Thumbs = imgs.Where(x => x != null).ToArray();
                Notify(true);
            });
            await Task.WhenAll(tasks);
            c.Thumbs = imgs.Where(x => x != null).ToArray();
            Notify(true);
        }

        DispatcherTimer cardTimer;
        /// <summary>Keeps each card's picture on the middle of its selected range (debounced while dragging).</summary>
        void ScheduleCardThumbs()
        {
            if (cardTimer == null)
                cardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (s, e) =>
                {
                    cardTimer.Stop();
                    foreach (var c in Clips.ToList())
                    {
                        double t = (c.In + c.Out) / 2;
                        if (Math.Abs(c.CardThumbTime - t) <= 0.001) continue;
                        int th = 72, tw = Math.Max(2, (int)Math.Round(th * c.W / Math.Max(c.H, 1)) / 2 * 2);
                        var img = await Thumbs.GetAsync(c.Path, t, tw, th, false);
                        c.CardThumb = img ?? c.CardThumb;
                        c.CardThumbTime = t;
                        Notify(true);
                    }
                }, ui);
            cardTimer.Stop();
            cardTimer.Start();
        }

        public void Remove(Guid id)
        {
            int i = Clips.FindIndex(c => c.Id == id);
            if (i < 0) return;
            sceneQueue.Remove(id);
            if (sceneClip == id) sceneCts?.Cancel();
            Clips.RemoveAt(i);
            Preview.Forget(id);
            if (SelectedId == id)
            {
                if (Clips.Count == 0)
                {
                    SelectedId = null;
                    Preview.Detach();
                    Timeline.Detach();
                }
                else Select(Clips[Math.Min(i, Clips.Count - 1)].Id, Preview.Playing);
            }
            Notify(true);
        }

        /// <summary>
        /// Adds another segment of the selected clip's video right after it, starting where the
        /// current one ends (at the next scene cut when scenes are known), same edits.
        /// </summary>
        public void AddSegmentFromSelected()
        {
            int i = SelectedIndex;
            if (i < 0) return;
            var src = Clips[i];
            double len = src.Length, a = src.Out;
            var cut = src.Cuts.Where(x => x >= src.Out - src.FrameDur / 2).Cast<double?>().FirstOrDefault();
            if (cut.HasValue) a = cut.Value;
            double b = Math.Min(a + len, src.Duration);
            var next = src.Cuts.Where(x => x > a + src.FrameDur / 2).Cast<double?>().FirstOrDefault();
            if (next.HasValue && next.Value < b && next.Value - a >= 1) b = next.Value;
            if (b - a < MinLength) { a = 0; b = Math.Min(len, src.Duration); }
            var c = new Clip
            {
                Path = src.Path, Info = src.Info, FrameDur = src.FrameDur, In = SnapFrame(a, src), Out = SnapFrame(b, src),
                Cuts = new List<double>(src.Cuts), ScenesAnalyzed = src.ScenesAnalyzed, Thumbs = src.Thumbs, Edit = src.Edit.Clone(),
            };
            Clips.Insert(i + 1, c);
            Select(c.Id, false);
            if (!c.ScenesAnalyzed) AnalyzeScenes(c.Id);
            Notify(true);
        }

        public void RemoveAll()
        {
            sceneQueue.Clear();
            sceneCts?.Cancel();
            foreach (var c in Clips) Preview.Forget(c.Id);
            Clips.Clear();
            SelectedId = null;
            Preview.Detach();
            Timeline.Detach();
            SetStatus(StatusKind.Idle, "");
            Notify(true);
        }

        public void Move(Guid id, Guid before)
        {
            if (id == before) return;
            int from = Clips.FindIndex(c => c.Id == id), to = Clips.FindIndex(c => c.Id == before);
            if (from < 0 || to < 0) return;
            var c = Clips[from];
            Clips.RemoveAt(from);
            Clips.Insert(to, c);
            Notify(true);
        }

        public void Select(Guid id, bool? autoplay = null)
        {
            var c = Clips.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            bool changed = SelectedId != id;
            SelectedId = id;
            Timeline.Attach(c);
            if (changed) Preview.Attach(c);
            bool play = autoplay ?? Preview.Playing;
            Preview.Seek(c.In);
            if (play) Preview.Play(); else Preview.Pause();
            Notify(true);
        }

        // MARK: Per-clip edits (selected)

        void MutateSelected(Action<Clip> f)
        {
            var c = Selected;
            if (c == null) return;
            f(c);
            Preview.RangeChanged();
            ScheduleCardThumbs();
            Notify(true);
        }

        public void MutateEdit(Action<ClipEdit> f) => MutateSelected(c => f(c.Edit));

        public void Rotate(int deg) => MutateEdit(e => { e.Rotation = ((e.Rotation + deg) % 360 + 360) % 360; e.ResetFraming(); });

        public void ResetEdit()
        {
            var c = Selected;
            if (c == null) return;
            MutateEdit(e => { var n = new ClipEdit { Fit = c.IsVertical }; c.Edit = n; });
        }

        public void ApplyLookToAll()
        {
            var src = Selected?.Edit;
            if (src == null) return;
            foreach (var c in Clips) if (c.Edit != src) c.Edit.CopyLook(src);
            Preview.AllRangesChanged();
            Notify(true);
        }

        // MARK: Frame-accurate times
        //
        // In/out points sit on frame boundaries: the range covers frames whose start time is in
        // [in, out). Seeks land in the middle of a frame and exports are offset by half a frame.

        static double SnapFrame(double t, Clip c) => Math.Round(t / c.FrameDur, MidpointRounding.AwayFromZero) * c.FrameDur;
        static double FrameStart(double t, Clip c) => Math.Floor(t / c.FrameDur + 0.01) * c.FrameDur;
        public static double MidFrame(double t, Clip c) => Math.Min(FrameStart(t, c) + c.FrameDur / 2, Math.Max(0, c.Duration - c.FrameDur / 2));

        public void SetIn(double t) => MutateSelected(c => c.In = Math.Min(Math.Max(0, SnapFrame(t, c)), c.Out - MinLength));
        public void SetOut(double t) => MutateSelected(c => c.Out = Math.Max(Math.Min(c.Duration, SnapFrame(t, c)), c.In + MinLength));

        /// <summary>Move the whole range so it starts at t.</summary>
        public void MoveRange(double t) => MutateSelected(c =>
        {
            double len = c.Length, s = Math.Min(Math.Max(0, SnapFrame(t, c)), c.Duration - len);
            c.In = s; c.Out = s + len;
        });

        public void JumpToIn() { var c = Selected; if (c != null) Preview.Seek(c.In); }

        /// <summary>I: in point at the playhead; past the out point, move the selection there (same length).</summary>
        public void MarkIn()
        {
            var cur = Selected;
            if (cur == null) return;
            double t = FrameStart(CurrentTime, cur);
            MutateSelected(c =>
            {
                if (t > c.Out - MinLength)
                {
                    double len = c.Length;
                    c.In = Math.Min(Math.Max(0, t), c.Duration - MinLength);
                    c.Out = Math.Min(c.Duration, c.In + len);
                }
                else c.In = Math.Max(0, t);
            });
            Timeline.Reveal(cur.In, cur.Out);
        }

        /// <summary>O: out point at the playhead; before the in point, move the selection so it ends there.</summary>
        public void MarkOut()
        {
            var cur = Selected;
            if (cur == null) return;
            double t = FrameStart(CurrentTime, cur) + cur.FrameDur;   // include the frame on screen
            MutateSelected(c =>
            {
                if (t < c.In + MinLength)
                {
                    double len = c.Length;
                    c.Out = Math.Max(Math.Min(c.Duration, t), MinLength);
                    c.In = Math.Max(0, c.Out - len);
                }
                else c.Out = Math.Min(c.Duration, t);
            });
            Timeline.Reveal(cur.In, cur.Out);
        }

        // MARK: Playback

        public bool PlayAll { get; private set; }

        public void TogglePlay()
        {
            var c = Selected;
            if (c == null) return;
            if (Preview.Playing)
            {
                Preview.Pause();
                PlayAll = false;
            }
            else
            {
                if (CurrentTime < c.In || CurrentTime >= c.Out - c.FrameDur) Preview.Seek(c.In);
                Preview.Play();
            }
            Notify();
        }

        public void TogglePlayAll()
        {
            if (PlayAll)
            {
                PlayAll = false;
                Preview.Pause();
                Notify();
                return;
            }
            if (Clips.Count == 0) return;
            PlayAll = true;
            Preview.PrepareAll();
            Select(Clips[0].Id, true);
        }

        /// <summary>Called by the preview at the end of the selected range: loop, or next clip in "Phát toàn bộ".</summary>
        public void ReachedEnd()
        {
            int i = SelectedIndex;
            if (i < 0) return;
            if (PlayAll && Clips.Count > 1) Select(Clips[(i + 1) % Clips.Count].Id, true);
            else Preview.Loop();
        }

        bool scrubbing, wasPlaying;
        /// <summary>Scrub to a time while dragging (pauses playback until EndScrub).</summary>
        public void Scrub(double t)
        {
            var c = Selected;
            if (c == null) return;
            if (!scrubbing)
            {
                scrubbing = true;
                wasPlaying = Preview.Playing;
                Preview.Pause();
            }
            Preview.Seek(Math.Min(Math.Max(0, t), c.Duration));
        }

        public void EndScrub(bool resumeFromIn)
        {
            if (!scrubbing) return;
            scrubbing = false;
            if (wasPlaying)
            {
                if (resumeFromIn && Selected != null) Preview.Seek(Selected.In);
                Preview.Play();
            }
            Notify();
        }

        /// <summary>Step by whole frames (or seconds).</summary>
        public void Step(int frames = 0, double seconds = 0)
        {
            var c = Selected;
            if (c == null) return;
            if (Preview.Playing) TogglePlay();
            double b = FrameStart(CurrentTime, c);
            Preview.Seek(Math.Min(Math.Max(0, b + frames * c.FrameDur + seconds), c.Duration));
        }

        // MARK: Export

        CancellationTokenSource exportCts;

        void SetStatus(StatusKind k, string msg, GifResult r = null)
        {
            Status = k; StatusMessage = msg; Result = r;
            Notify();
        }
        public void DismissStatus() { if (!IsEncoding) SetStatus(StatusKind.Idle, ""); }

        public async void StartExport(string output)
        {
            S.ExportDir = System.IO.Path.GetDirectoryName(output);
            CancelExport();
            // Half-frame offsets: include the frame starting at in, exclude the one starting at out.
            var specs = Clips.Select(c =>
            {
                double start = Math.Max(0, c.In - c.FrameDur / 2), end = c.Out - c.FrameDur / 2;
                return new ClipSpec { Path = c.Path, Start = start, Duration = Math.Max(c.FrameDur, end - start), SrcW = c.W, SrcH = c.H, Edit = c.Edit.Clone() };
            }).ToList();
            var req = new GifRequest
            {
                Clips = specs, Aspect = OutputAspect, Width = S.Width, Fps = S.Fps,
                Dither = Enum.TryParse<Dither>(S.Dither, out var d) ? d : Dither.Balanced,
                LimitBytes = (long)(S.LimitMB * 1_000_000), AutoFit = S.AutoFit,
                Priority = S.FitPriority == "KeepSharpness" ? FitPriority.KeepSharpness : FitPriority.KeepSize,
                MinFps = S.MinFps, MinWidth = S.MinWidth, Force = S.ForceLimit,
            };
            var cts = exportCts = new CancellationTokenSource();
            SetStatus(StatusKind.Encoding, "Đang xuất GIF…");
            try
            {
                var r = await GifEncoder.MakeGifAsync(req, output, msg => ui.BeginInvoke(new Action(() =>
                {
                    if (exportCts == cts) SetStatus(StatusKind.Encoding, msg);
                })), cts.Token);
                if (exportCts == cts) SetStatus(StatusKind.Done, "", r);
            }
            catch (OperationCanceledException) { if (exportCts == cts) SetStatus(StatusKind.Idle, ""); }
            catch (Exception ex) { if (exportCts == cts) SetStatus(StatusKind.Failed, "Xuất GIF thất bại. " + ex.Message); }
            finally { if (exportCts == cts) exportCts = null; }
        }

        public void CancelExport()
        {
            exportCts?.Cancel();
            exportCts = null;
            if (IsEncoding) SetStatus(StatusKind.Idle, "");
        }

        // MARK: Scenes

        readonly List<Guid> sceneQueue = new List<Guid>();
        Guid? sceneClip;
        CancellationTokenSource sceneCts;
        bool sceneRunning;

        public void AnalyzeScenes(Guid id)
        {
            if (sceneQueue.Contains(id) || sceneClip == id) return;
            sceneQueue.Add(id);
            RunSceneQueue();
        }

        async void RunSceneQueue()
        {
            if (sceneRunning) return;
            sceneRunning = true;
            try
            {
                while (sceneQueue.Count > 0)
                {
                    var id = sceneQueue[0];
                    sceneQueue.RemoveAt(0);
                    var c = Clips.FirstOrDefault(x => x.Id == id);
                    if (c == null) continue;
                    double fd = c.FrameDur;
                    List<double> Align(List<double> cuts) => cuts.Select(x => Math.Round(x / fd) * fd).ToList();
                    // Every segment of the same file shares the cuts.
                    void Apply(Action<Clip> f) { foreach (var x in Clips.Where(x => x.Path == c.Path)) f(x); Notify(); }
                    var cached = SceneDetector.Cached(c.Path);
                    if (cached != null)
                    {
                        Apply(x => { x.Cuts = Align(cached); x.ScenesAnalyzed = true; x.SceneProgress = null; });
                        continue;
                    }
                    sceneClip = id;
                    sceneCts = new CancellationTokenSource();
                    Apply(x => x.SceneProgress = 0);
                    try
                    {
                        var cuts = await SceneDetector.DetectAsync(c.Path, c.Duration, (cs, p) => ui.BeginInvoke(new Action(() =>
                        {
                            Apply(x => { x.Cuts = Align(cs); if (p >= 0) x.SceneProgress = p < 1 ? p : (double?)null; });
                        })), sceneCts.Token);
                        SceneDetector.Store(cuts, c.Path);
                        Apply(x => { x.Cuts = Align(cuts); x.ScenesAnalyzed = true; x.SceneProgress = null; });
                    }
                    catch (Exception)
                    {
                        Apply(x => x.SceneProgress = null);
                    }
                    sceneClip = null;
                }
            }
            finally { sceneRunning = false; }
        }

        /// <summary>The scene containing time t: (previous cut or 0, next cut or end).</summary>
        public (double, double) SceneBounds(double t, Clip c)
        {
            double start = c.Cuts.Where(x => x <= t + 0.001).DefaultIfEmpty(0).Last();
            double end = c.Cuts.Where(x => x > t + 0.001).DefaultIfEmpty(c.Duration).First();
            return (start, end);
        }

        /// <summary>X: select the whole scene under time t (default: playhead).</summary>
        public void SelectScene(double? at = null)
        {
            var c = Selected;
            if (c == null) return;
            var (a, b) = SceneBounds(at ?? CurrentTime, c);
            if (b - a < MinLength) return;
            MutateSelected(x => { x.In = a; x.Out = b; });
            Timeline.Reveal(a, b);
            if (!Preview.Playing) Preview.Seek(a);
        }

        /// <summary>↑/↓: move the playhead to the previous/next scene cut.</summary>
        public void JumpScene(int direction)
        {
            var c = Selected;
            if (c == null) return;
            double t = CurrentTime;
            double target = direction > 0
                ? c.Cuts.Where(x => x > t + 0.02).DefaultIfEmpty(c.Duration).First()
                : c.Cuts.Where(x => x < t - 0.05).DefaultIfEmpty(0).Last();
            if (Preview.Playing) TogglePlay();
            Preview.Seek(target);
            Timeline.Reveal(target, target);
        }

        /// <summary>Snap a time to the nearest scene cut (or clip start/end) within tolerance seconds.</summary>
        public double Snapped(double t, double tolerance)
        {
            var c = Selected;
            if (!S.SnapToScenes || c == null) return t;
            double best = t, bestD = tolerance;
            foreach (var x in c.Cuts.Concat(new[] { 0, c.Duration }))
                if (Math.Abs(x - t) < bestD) { best = x; bestD = Math.Abs(x - t); }
            return best;
        }
    }
}
