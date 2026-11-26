using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VneGif.App
{
    /// <summary>The visible part of the selected clip's timeline, plus thumbnails for that part (port of TimelineZoom).</summary>
    public sealed class TimelineZoom
    {
        public sealed class Tile { public double Start, Dur; public BitmapSource Image; }

        public const double MinWindow = 0.5;     // shortest visible window, seconds
        public double Vs { get; private set; }   // visible start
        public double Vl { get; private set; } = 1; // visible length
        public List<Tile> Tiles { get; private set; } = new List<Tile>();
        public double Duration { get; private set; } = 1;
        Clip clip;
        public event Action Changed;
        CancellationTokenSource thumbCts;
        DispatcherTimer thumbTimer;
        double trackWidth = 600;

        public double TrackWidth
        {
            get => trackWidth;
            set { bool big = Math.Abs(value - trackWidth) > 40; trackWidth = value; if (big) ScheduleThumbs(); }
        }

        public void Attach(Clip c)
        {
            bool isNew = c != clip;
            clip = c;
            Duration = Math.Max(c.Duration, 0.1);
            if (isNew)
            {
                Tiles = new List<Tile>();
                if (Duration <= 20) { Vs = 0; Vl = Duration; }
                else
                {
                    double len = Math.Min(Duration, Math.Max(c.Length * 5, 15));
                    Vl = len;
                    Vs = Math.Min(Math.Max((c.In + c.Out) / 2 - len / 2, 0), Duration - len);
                }
                ScheduleThumbs();
            }
            Changed?.Invoke();
        }

        public void Detach()
        {
            clip = null;
            Tiles = new List<Tile>();
            thumbCts?.Cancel();
        }

        public void SetWindow(double start, double length)
        {
            double len = Math.Min(Math.Max(length, Math.Min(MinWindow, Duration)), Duration);
            double st = Math.Min(Math.Max(start, 0), Duration - len);
            if (Math.Abs(st - Vs) <= 1e-6 && Math.Abs(len - Vl) <= 1e-6) return;
            Vs = st; Vl = len;
            ScheduleThumbs();
            Changed?.Invoke();
        }

        /// <summary>factor &gt; 1 zooms in, keeping time t under the same screen position.</summary>
        public void Zoom(double factor, double t)
        {
            double len = Math.Min(Math.Max(Vl / factor, Math.Min(MinWindow, Duration)), Duration);
            double frac = Vl > 0 ? (t - Vs) / Vl : 0.5;
            SetWindow(t - frac * len, len);
        }

        public void Pan(double seconds) => SetWindow(Vs + seconds, Vl);

        /// <summary>Make sure [a, b] is visible (pans, or zooms out if it doesn't fit).</summary>
        public void Reveal(double a, double b)
        {
            if (a >= Vs && b <= Vs + Vl) return;
            if (b - a > Vl) Fit(a, b); else SetWindow((a + b) / 2 - Vl / 2, Vl);
        }

        public void Fit(double a, double b)
        {
            double len = Math.Max((b - a) * 1.6, MinWindow);
            SetWindow((a + b) / 2 - len / 2, len);
        }

        public void ShowAll() => SetWindow(0, Duration);

        /// <summary>0 = whole clip, 1 = closest zoom (log scale).</summary>
        public double Level => Duration > MinWindow ? Math.Log(Vl / Duration) / Math.Log(MinWindow / Duration) : 0;

        public void SetLevel(double z, double t)
        {
            if (Duration <= MinWindow) return;
            double len = Duration * Math.Pow(MinWindow / Duration, Math.Min(Math.Max(z, 0), 1));
            Zoom(Vl / len, t);
        }

        public double X(double t, double w) => (t - Vs) / Math.Max(Vl, 1e-6) * w;
        public double T(double x, double w) => Vs + x / Math.Max(w, 1) * Vl;

        void ScheduleThumbs()
        {
            if (thumbTimer == null)
            {
                thumbTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (s, e) => { thumbTimer.Stop(); MakeThumbs(); },
                    Dispatcher.CurrentDispatcher);
            }
            thumbTimer.Stop();
            thumbTimer.Start();
        }

        async void MakeThumbs()
        {
            var c = clip;
            if (c == null) return;
            thumbCts?.Cancel();
            var cts = thumbCts = new CancellationTokenSource();
            int n = Math.Max(4, (int)(trackWidth / 80));
            double start = Vs, tileDur = Vl / n;
            int th = 56, tw = Math.Max(2, (int)Math.Round(th * c.W / Math.Max(c.H, 1)) / 2 * 2);
            bool progressive = Tiles.Count == 0;
            var fresh = new List<Tile>();
            // Keyframe-only decoding is much faster but repeats one picture per GOP: only when tiles are long.
            bool fast = tileDur > 2;
            var times = Enumerable.Range(0, n).Select(i => start + tileDur * (i + 0.5)).ToList();
            var jobs = times.Select(t => Thumbs.GetAsync(c.Path, t, tw, th, fast, cts.Token)).ToList();
            for (int i = 0; i < n; i++)
            {
                double t = times[i];
                var img = await jobs[i];
                if (cts.IsCancellationRequested || c != clip) return;
                if (img != null) fresh.Add(new Tile { Start = t - tileDur / 2, Dur = tileDur, Image = img });
                if (progressive) { Tiles = new List<Tile>(fresh); Changed?.Invoke(); }
            }
            Tiles = fresh;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Overview bar, time ruler and the zoomable trim track with thumbnails, scene marks,
    /// selection handles and playhead (port of Timeline.swift's TrimTimeline).
    /// </summary>
    public sealed class TimelineView : FrameworkElement
    {
        readonly AppState s;
        const double Grip = 12, OverH = 16, RulerY = 22, RulerH = 18, TrackY = 46, TrackH = 56;
        enum Mode { None, InHandle, OutHandle, Move, Scrub, Overview }
        Mode mode;
        double grab;

        static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
        static readonly Brush TrackBg = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x37)));
        static readonly Brush Tick = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90)));
        static readonly Pen WhitePen = FrozenPen(Brushes.White, 1.5);
        static readonly Pen ShadowPen = FrozenPen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), 3);
        static readonly Pen ThinWhite = FrozenPen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1);
        static readonly Pen WindowPen = FrozenPen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
        static readonly Pen SelPen = FrozenPen(Brand.BrightBrush, 3);
        static readonly Pen TickPen = FrozenPen(new SolidColorBrush(Color.FromArgb(200, 0x8A, 0x8A, 0x90)), 1);
        static readonly Pen MinorPen = FrozenPen(new SolidColorBrush(Color.FromArgb(100, 0x8A, 0x8A, 0x90)), 1);
        static readonly Typeface Face = new Typeface("Segoe UI");

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        static Pen FrozenPen(Brush b, double w) => Frozen(new Pen(b, w));

        public TimelineView(AppState state)
        {
            s = state;
            Height = TrackY + TrackH + 6;
            Focusable = false;
            ClipToBounds = false;
            s.Preview.TimeChanged += InvalidateVisual;
            s.Timeline.Changed += InvalidateVisual;
            ToolTip = "Kéo hai mép đoạn chọn để cắt · kéo phần giữa để dời đoạn · nhấp bên ngoài để tua · nhấp đúp để chọn cả cảnh\n"
                    + "Ctrl + cuộn để thu phóng · cuộn để di chuyển · giữ Ctrl khi kéo để tạm tắt bắt dính";
            ToolTipService.SetInitialShowDelay(this, 1500);
        }

        double W => Math.Max(1, ActualWidth - 2 * Grip);

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            s.Timeline.TrackWidth = W;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = s.Selected;
            if (c == null) return;
            var z = s.Timeline;
            double w = W, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

            // Overview: whole clip, selection, visible window, playhead.
            dc.PushTransform(new TranslateTransform(Grip, 0));
            double d = Math.Max(c.Duration, 0.001);
            var over = new Rect(0, 0, w, OverH);
            dc.PushClip(new RectangleGeometry(over, 3, 3));
            dc.DrawRectangle(TrackBg, null, over);
            if (c.Thumbs.Length > 0)
            {
                dc.PushOpacity(0.45);
                double tw = w / c.Thumbs.Length;
                for (int i = 0; i < c.Thumbs.Length; i++) DrawFill(dc, c.Thumbs[i], new Rect(i * tw, 0, tw + 0.5, OverH));
                dc.Pop();
            }
            Marks(dc, c.Cuts, 0, d, w, OverH, false);
            dc.DrawRectangle(Brand.BrightBrush, null, new Rect(c.In / d * w, 0, Math.Max(3, c.Length / d * w), OverH));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), WindowPen,
                new Rect(z.Vs / d * w, 0.75, Math.Max(6, z.Vl / d * w), OverH - 1.5), 3, 3);
            dc.DrawRectangle(Brushes.White, null, new Rect(s.CurrentTime / d * w - 1, 0, 2, OverH));
            dc.Pop();

            // Ruler for the visible window.
            if (z.Vl > 0)
            {
                double step = NiceStep(z.Vl * 90 / w), minor = step / 5;
                double t = Math.Floor(z.Vs / minor) * minor, end = z.Vs + z.Vl;
                while (t <= end + 1e-9)
                {
                    double x = (t - z.Vs) / z.Vl * w;
                    bool major = Math.Abs(Math.Round(t / step) * step - t) < minor / 10;
                    dc.DrawLine(major ? TickPen : MinorPen, new Point(x, RulerY + RulerH), new Point(x, RulerY + RulerH - (major ? 7 : 3)));
                    if (major && x >= 0)
                    {
                        var ft = new FormattedText(Label(t, step, c.Duration), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Tick, dpi);
                        dc.DrawText(ft, new Point(x + 3, RulerY - 1));
                    }
                    t += minor;
                }
            }

            // Detail track.
            double y = TrackY, h = TrackH;
            double x0 = z.X(c.In, w), x1 = z.X(c.Out, w);
            dc.PushClip(new RectangleGeometry(new Rect(-Grip, y - 6, w + 2 * Grip, h + 12)));
            dc.PushClip(new RectangleGeometry(new Rect(0, y, w, h)));
            dc.DrawRectangle(TrackBg, null, new Rect(0, y, w, h));
            foreach (var tile in z.Tiles)
                DrawFill(dc, tile.Image, new Rect(z.X(tile.Start, w), y, Math.Max(1, tile.Dur / z.Vl * w + 1), h));
            if (x0 > 0) dc.DrawRectangle(Dim, null, new Rect(0, y, Math.Min(x0, w), h));
            if (x1 < w) dc.DrawRectangle(Dim, null, new Rect(Math.Max(0, x1), y, Math.Min(w, w - x1), h));
            dc.Pop();
            dc.PushTransform(new TranslateTransform(0, y));
            Marks(dc, c.Cuts, z.Vs, z.Vl, w, h, true);
            dc.Pop();
            // Selection with grips.
            var sel = new Rect(x0 - Grip, y, Math.Max(Grip * 2, x1 - x0 + Grip * 2), h);
            dc.DrawRoundedRectangle(null, SelPen, new Rect(sel.X + 1.5, sel.Y + 1.5, sel.Width - 3, sel.Height - 3), 5, 5);
            foreach (double gx in new[] { sel.Left, sel.Right - Grip })
            {
                dc.DrawRoundedRectangle(Brand.BrightBrush, null, new Rect(gx, y, Grip, h), 3, 3);
                dc.DrawRoundedRectangle(Dim, null, new Rect(gx + Grip / 2 - 1, y + h / 2 - 8, 2, 16), 1, 1);
            }
            // Playhead.
            double px = z.X(s.CurrentTime, w);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(px - 2, y - 4, 4, h + 8));
            dc.DrawRectangle(Brushes.White, null, new Rect(px - 1, y - 4, 2, h + 8));
            dc.Pop();
            dc.Pop();
        }

        static void DrawFill(DrawingContext dc, BitmapSource img, Rect r)
        {
            if (img == null) return;
            // aspect fill, centered, clipped to r
            double s = Math.Max(r.Width / img.PixelWidth, r.Height / img.PixelHeight);
            double iw = img.PixelWidth * s, ih = img.PixelHeight * s;
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawImage(img, new Rect(r.X + (r.Width - iw) / 2, r.Y + (r.Height - ih) / 2, iw, ih));
            dc.Pop();
        }

        static void Marks(DrawingContext dc, List<double> cuts, double start, double length, double w, double h, bool prominent)
        {
            if (length <= 0) return;
            double lastX = -10;
            foreach (var t in cuts)
            {
                if (t < start || t > start + length) continue;
                double x = (t - start) / length * w;
                if (!prominent && x - lastX < 1.5) continue;   // avoid smearing when dense
                lastX = x;
                if (prominent)
                {
                    dc.DrawLine(ShadowPen, new Point(x, 0), new Point(x, h));
                    dc.DrawLine(WhitePen, new Point(x, 0), new Point(x, h));
                    var tri = new StreamGeometry();
                    using (var g = tri.Open())
                    {
                        g.BeginFigure(new Point(x - 4, 0), true, true);
                        g.LineTo(new Point(x + 4, 0), false, false);
                        g.LineTo(new Point(x, 5), false, false);
                    }
                    dc.DrawGeometry(Brushes.White, null, tri);
                }
                else dc.DrawLine(ThinWhite, new Point(x, 0), new Point(x, h));
            }
        }

        public static double NiceStep(double raw)
        {
            double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
            foreach (var st in steps) if (st >= raw) return st;
            return 7200;
        }

        public static string Label(double t, double step, double total)
        {
            double s = Math.Max(0, t);
            int h = (int)s / 3600, m = (int)s % 3600 / 60, sec = (int)s % 60;
            string o = total >= 3600 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
            if (step < 1) o += "," + ((int)Math.Round((s - Math.Floor(s)) * 10) % 10);
            return o;
        }

        // MARK: Mouse

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var c = s.Selected;
            if (c == null) return;
            var p = e.GetPosition(this);
            double x = p.X - Grip, w = W;
            if (e.ClickCount == 2 && p.Y >= TrackY - 6)
            {
                s.SelectScene(s.Timeline.T(x, w));
                e.Handled = true;
                return;
            }
            if (p.Y < RulerY)
            {
                mode = Mode.Overview;
                OverviewTo(x);
            }
            else
            {
                double x0 = s.Timeline.X(c.In, w), x1 = s.Timeline.X(c.Out, w);
                double dIn = Math.Abs(x - x0), dOut = Math.Abs(x - x1);
                if (Math.Min(dIn, dOut) <= Grip + 4) mode = dIn < dOut ? Mode.InHandle : Mode.OutHandle;
                else if (x > x0 && x < x1) { mode = Mode.Move; grab = s.Timeline.T(x, w) - c.In; }
                else mode = Mode.Scrub;
                Drag(x);
            }
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (mode == Mode.None)
            {
                Cursor = p.Y < RulerY ? Cursors.Hand : NearGrip(p.X - Grip) ? Cursors.SizeWE : Cursors.Arrow;
                return;
            }
            if (mode == Mode.Overview) OverviewTo(p.X - Grip); else Drag(p.X - Grip);
        }

        bool NearGrip(double x)
        {
            var c = s.Selected;
            if (c == null) return false;
            double w = W;
            return Math.Min(Math.Abs(x - s.Timeline.X(c.In, w)), Math.Abs(x - s.Timeline.X(c.Out, w))) <= Grip + 4;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (mode == Mode.None) return;
            bool wasTrack = mode != Mode.Overview;
            mode = Mode.None;
            ReleaseMouseCapture();
            if (wasTrack) s.EndScrub(true);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (mode != Mode.None && mode != Mode.Overview) s.EndScrub(true);
            mode = Mode.None;
        }

        void OverviewTo(double x)
        {
            var z = s.Timeline;
            z.SetWindow(x / W * z.Duration - z.Vl / 2, z.Vl);
        }

        void Drag(double x)
        {
            var c = s.Selected;
            if (c == null) return;
            var z = s.Timeline;
            double w = W;
            // Auto-scroll when dragging past the edges.
            if (x < 0) z.Pan(-z.Vl * 0.02); else if (x > w) z.Pan(z.Vl * 0.02);
            double raw = z.T(Math.Min(Math.Max(x, 0), w), w);
            double tol = 8 / Math.Max(w, 1) * z.Vl;   // 8 px
            bool noSnap = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Func<double, double> snap = v => noSnap ? v : s.Snapped(v, tol);
            double t = snap(raw);
            switch (mode)
            {
                case Mode.InHandle:
                    s.SetIn(t);
                    s.Scrub(s.Selected?.In ?? t);
                    break;
                case Mode.OutHandle:
                    s.SetOut(t);
                    s.Scrub(s.Selected?.Out ?? t);
                    break;
                case Mode.Move:
                    {
                        // Snap whichever edge lands closer to a cut.
                        double newIn = raw - grab, len = c.Length;
                        double si = snap(newIn), so = snap(newIn + len);
                        bool inSnapped = Math.Abs(si - newIn) > 1e-9, outSnapped = Math.Abs(so - newIn - len) > 1e-9;
                        double start = inSnapped && (!outSnapped || Math.Abs(si - newIn) <= Math.Abs(so - newIn - len)) ? si
                                     : outSnapped ? so - len : newIn;
                        s.MoveRange(start);
                        s.Scrub(s.Selected?.In ?? t);
                        break;
                    }
                default:
                    s.Scrub(t);
                    break;
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var z = s.Timeline;
            double notches = e.Delta / 120.0;
            var mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            {
                double t = z.T(e.GetPosition(this).X - Grip, W);
                z.Zoom(Math.Pow(1.25, notches), t);
            }
            else z.Pan(-notches * z.Vl * 0.1);
            e.Handled = true;
        }
    }
}
