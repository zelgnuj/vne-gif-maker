using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace VneGif.App
{
    /// <summary>Brand colors and the app mark (white "E" on a brand-red rounded square; no VnExpress logo).</summary>
    public static class Brand
    {
        public const string Name = "VnExpress GIF Maker";
        public const string Credit = "Phát triển bởi Lâm Nguyễn · Ban Video, VnExpress";
        public static readonly Color Red = Color.FromRgb(0x9F, 0x22, 0x4E);
        public static readonly Color Bright = Color.FromRgb(0xE2, 0x42, 0x7C);
        public static readonly SolidColorBrush RedBrush = Frozen(new SolidColorBrush(Red));
        public static readonly SolidColorBrush BrightBrush = Frozen(new SolidColorBrush(Bright));

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        /// <summary>Same geometry as BrandMark.swift (there y-up; here y-down).</summary>
        public static Geometry EGeometry(double side)
        {
            double s = side, w = s * 0.44, h = s * 0.54, t = s * 0.115;
            double x = (s - w) / 2 + s * 0.01, top = (s - h) / 2;
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            g.Children.Add(new RectangleGeometry(new Rect(x, top, t, h)));                   // spine
            g.Children.Add(new RectangleGeometry(new Rect(x, top, w, t)));                   // top bar
            g.Children.Add(new RectangleGeometry(new Rect(x, top + (h - t) / 2, w * 0.82, t))); // middle bar
            g.Children.Add(new RectangleGeometry(new Rect(x, top + h - t, w, t)));           // bottom bar
            g.Freeze();
            return g;
        }

        public static FrameworkElement Mark(double side)
        {
            var c = new Canvas { Width = side, Height = side, IsHitTestVisible = false };
            c.Children.Add(new Border { Width = side, Height = side, CornerRadius = new CornerRadius(side * 0.22), Background = RedBrush });
            c.Children.Add(new System.Windows.Shapes.Path { Data = EGeometry(side), Fill = Brushes.White });
            return c;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark title bar on Windows 10 20H1+ and 11 (ignored elsewhere).</summary>
        public static void DarkTitleBar(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            }
            catch { }
        }
    }
}
