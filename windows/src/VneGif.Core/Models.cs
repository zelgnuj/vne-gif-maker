using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VneGif
{
    public sealed class Aspect
    {
        public string Id { get; }
        public string Label => Id.Replace(".", ",");
        /// <summary>w/h; null for "Gốc" and "Tuỳ chỉnh".</summary>
        public double? Ratio { get; }
        Aspect(string id, double? ratio) { Id = id; Ratio = ratio; }

        public static readonly Aspect Original = new Aspect("Gốc", null);
        public static readonly Aspect Custom = new Aspect("Tuỳ chỉnh", null);
        public static readonly Aspect[] Landscape =
            new[] { "21:9", "2:1", "1.91:1", "16:9", "5:3", "3:2", "4:3", "5:4" }.Select(Make).ToArray();
        public static readonly Aspect[] SquareAndPortrait =
            new[] { "1:1", "4:5", "3:4", "2:3", "9:16" }.Select(Make).ToArray();
        public static IEnumerable<Aspect> All => Landscape.Concat(SquareAndPortrait).Concat(new[] { Original, Custom });

        static Aspect Make(string s) => new Aspect(s, Parse(s));
        public static Aspect FromId(string id) => All.FirstOrDefault(a => a.Id == id);

        /// <summary>Parses "W:H" (dot or comma decimals) into w/h.</summary>
        public static double? Parse(string s)
        {
            var p = (s ?? "").Replace(",", ".").Split(':');
            if (p.Length != 2) return null;
            if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return null;
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return null;
            return w > 0 && h > 0 ? w / h : (double?)null;
        }
    }

    public enum Dither { Smooth, Balanced, Light }

    public static class DitherInfo
    {
        public static string Name(Dither d) => d == Dither.Smooth ? "Mịn" : d == Dither.Balanced ? "Cân bằng" : "Gọn nhẹ";
        public static string Note(Dither d) => d == Dither.Smooth ? "Chuyển màu mượt nhất, dung lượng lớn nhất."
            : d == Dither.Balanced ? "Cân bằng giữa chất lượng và dung lượng." : "Dung lượng nhỏ nhất, có thể thấy vân màu.";
        public static string Filter(Dither d) => d == Dither.Smooth ? "dither=sierra2_4a"
            : d == Dither.Balanced ? "dither=bayer:bayer_scale=3" : "dither=bayer:bayer_scale=5";
    }

    /// <summary>What to sacrifice first when the GIF is over the size limit.</summary>
    public enum FitPriority { KeepSize, KeepSharpness }

    public sealed class ClipSpec
    {
        public string Path;
        public double Start;       // source seconds
        public double Duration;    // source seconds (output = duration / edit.speed)
        public double SrcW, SrcH;  // display-oriented
        public ClipEdit Edit;
    }

    public sealed class GifRequest
    {
        public List<ClipSpec> Clips = new List<ClipSpec>();
        public double Aspect;      // output w/h
        public int Width;
        public int Fps;
        public Dither Dither = Dither.Balanced;
        public long LimitBytes;
        public bool AutoFit = true;
        public FitPriority Priority = FitPriority.KeepSize;
        public int MinFps = 10;
        public int MinWidth = 600;
        /// <summary>If still over the limit at the floors, keep lowering fps/width (down to the hard floors).</summary>
        public bool Force = true;
    }

    public sealed class GifResult
    {
        public string Path;
        public long Bytes;
        public int Width, Height, Fps, Attempts;
        public long LimitBytes;
        /// <summary>Lossy compression step applied (0 = lossless optimization only).</summary>
        public int SqueezeLevel;
        /// <summary>fps/width had to go below the user's minimums to fit.</summary>
        public bool BelowFloor;
        public bool UnderLimit => Bytes <= LimitBytes;

        public List<string> Notes
        {
            get
            {
                var n = new List<string>();
                if (SqueezeLevel > 0) n.Add($"nén mức {SqueezeLevel}/{GifEncoder.SqueezeSteps.Length}");
                if (BelowFloor) n.Add("dưới mức tối thiểu");
                return n;
            }
        }
    }

    public sealed class EncodeException : Exception
    {
        public EncodeException(string message) : base(message) { }
        public static EncodeException NoFFmpeg() =>
            new EncodeException("Không tìm thấy bộ mã hoá ffmpeg. Vui lòng cài lại ứng dụng.");
    }
}
