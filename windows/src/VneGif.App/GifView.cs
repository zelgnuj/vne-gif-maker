using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VneGif.App
{
    /// <summary>Plays an exported GIF. GDI+ composites the frames (gifsicle writes partial frames).</summary>
    public sealed class GifView : Image
    {
        readonly List<BitmapSource> frames = new List<BitmapSource>();
        readonly List<int> delays = new List<int>();
        readonly DispatcherTimer timer = new DispatcherTimer();
        int index;

        public GifView(string path, int maxWidth)
        {
            Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            try { Load(path, maxWidth); } catch { }
            if (frames.Count > 0) Source = frames[0];
            timer.Tick += (s, e) =>
            {
                index = (index + 1) % frames.Count;
                Source = frames[index];
                timer.Interval = TimeSpan.FromMilliseconds(delays[index]);
            };
            Loaded += (s, e) => { if (frames.Count > 1) { timer.Interval = TimeSpan.FromMilliseconds(delays[0]); timer.Start(); } };
            Unloaded += (s, e) => timer.Stop();
        }

        void Load(string path, int maxWidth)
        {
            using (var img = System.Drawing.Image.FromFile(path))
            {
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                int n = img.GetFrameCount(dim);
                int[] d = null;
                try
                {
                    var prop = img.GetPropertyItem(0x5100);   // frame delays, 1/100 s each
                    d = Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(prop.Value, i * 4) * 10).ToArray();
                }
                catch { }
                double scale = Math.Min(1, maxWidth / (double)img.Width);
                int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
                for (int i = 0; i < n && i < 600; i++)
                {
                    img.SelectActiveFrame(dim, i);
                    using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.DrawImage(img, 0, 0, w, h);
                        }
                        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, bmp.PixelFormat);
                        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * h, data.Stride);
                        bmp.UnlockBits(data);
                        src.Freeze();
                        frames.Add(src);
                    }
                    int delay = d != null && i < d.Length ? d[i] : 100;
                    delays.Add(delay < 20 ? 100 : delay);   // browsers treat tiny delays as 100 ms
                }
            }
        }
    }
}
