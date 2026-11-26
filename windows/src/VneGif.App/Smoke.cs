using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VneGif.App
{
    /// <summary>
    /// Unattended run for CI on a Windows machine:
    ///   VnExpressGifMaker.exe --smoke &lt;out-dir&gt; &lt;video&gt;…
    /// Screenshots every screen, plays, edits, exports a GIF, writes smoke.txt, exits 0 on success.
    /// </summary>
    static class Smoke
    {
        public static async void Run(MainWindow w, string dir, string[] videos)
        {
            var log = new StringBuilder();
            void L(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
            int code = 1;
            try
            {
                Directory.CreateDirectory(dir);
                await Task.Delay(1500);
                Shot(w, dir, "1-empty.png");
                w.State.Add(videos);
                for (int i = 0; i < 60 && w.State.Clips.Count < videos.Length; i++) await Task.Delay(500);
                L($"clips loaded: {w.State.Clips.Count}/{videos.Length}");
                await Task.Delay(6000);   // thumbnails, scenes, preview range
                var c = w.State.Selected;
                L($"selected: {c?.FileName} {c?.W}x{c?.H} {c?.Info.Fps} fps, cuts={c?.Cuts.Count}, analyzed={c?.ScenesAnalyzed}, issue={w.State.Preview.Issue ?? "none"}");
                L($"bitmap: {w.State.Preview.Bitmap?.PixelWidth}x{w.State.Preview.Bitmap?.PixelHeight}");
                Shot(w, dir, "2-editor.png");
                w.State.TogglePlay();
                await Task.Delay(1500);
                L($"playing: {w.State.Preview.Playing}, time={w.State.CurrentTime:0.000}");
                w.State.TogglePlay();
                w.State.MutateEdit(e => { e.Zoom = 1.4; e.Saturation = 1.4; e.Rotation = 0; });
                w.State.SelectScene(0.1);
                await Task.Delay(2500);
                Shot(w, dir, "3-edited.png");
                if (w.State.Clips.Count > 0) w.State.AddSegmentFromSelected();
                await Task.Delay(1500);
                w.SmokeTab(1);
                await Task.Delay(800);
                Shot(w, dir, "4-export-tab.png");
                var gif = Path.Combine(dir, "smoke thử.gif");
                w.State.StartExport(gif);
                for (int i = 0; i < 240 && w.State.Status == StatusKind.Encoding; i++) await Task.Delay(500);
                await Task.Delay(1500);
                L($"export: {w.State.Status} {w.State.StatusMessage} {(w.State.Result != null ? $"{w.State.Result.Bytes} bytes {w.State.Result.Width}x{w.State.Result.Height} {w.State.Result.Fps} fps" : "")}");
                Shot(w, dir, "5-result.png");
                bool ok = w.State.Status == StatusKind.Done && File.Exists(gif) && w.State.Preview.Bitmap != null && w.State.Preview.Issue == null;
                code = ok ? 0 : 2;
            }
            catch (Exception ex) { L("EXCEPTION " + ex); code = 3; }
            L($"exit {code}");
            File.WriteAllText(Path.Combine(dir, "smoke.txt"), log.ToString());
            w.State.SaveNow();
            Environment.Exit(code);
        }

        static void Shot(Window w, string dir, string name)
        {
            w.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(w);
            var content = (FrameworkElement)w.Content;
            int pw = (int)(content.ActualWidth * dpi.DpiScaleX), ph = (int)(content.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(Math.Max(1, pw), Math.Max(1, ph), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(Path.Combine(dir, name))) enc.Save(f);
        }
    }
}
