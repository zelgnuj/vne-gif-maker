using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VneGif
{
    /// <summary>Vietnamese number formatting (decimal comma), as in the macOS app.</summary>
    public static class Num
    {
        public static string Fmt(double v, int decimals, bool fixedDecimals = false)
        {
            var fmt = "0" + (decimals > 0 ? "." + new string(fixedDecimals ? '0' : '#', decimals) : "");
            return v.ToString(fmt, CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>Parses "1,5", "1.5", "45%", "-0,2×" …</summary>
        public static double? Parse(string s)
        {
            var cleaned = new string((s ?? "").Replace(',', '.').Where(c => "0123456789.-+".IndexOf(c) >= 0).ToArray());
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        /// <summary>00:03,25 — or 1:02:29,40 from one hour up.</summary>
        public static string Time(double t)
        {
            long cs = (long)Math.Floor(Math.Max(0, t) * 100 + 1e-6);
            long h = cs / 360000, m = cs / 6000 % 60, s = cs / 100 % 60, c = cs % 100;
            return h > 0 ? $"{h}:{m:00}:{s:00},{c:00}" : $"{m:00}:{s:00},{c:00}";
        }

        /// <summary>Accepts "1:02:29,4", "2:29.4", "149,4" or "149.4 s".</summary>
        public static double? ParseTime(string str)
        {
            var cleaned = new string((str ?? "").Replace(',', '.').Where(c => "0123456789.:".IndexOf(c) >= 0).ToArray());
            var parts = cleaned.Split(':');
            if (parts.Length == 0 || parts.Length > 3) return null;
            double total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p.Length == 0 ? "0" : p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
                total = total * 60 + v;
            }
            return total;
        }
    }

    [DataContract]
    public sealed class SavedClip
    {
        [DataMember(Name = "path")] public string Path;
        [DataMember(Name = "inPoint")] public double InPoint;
        [DataMember(Name = "outPoint")] public double OutPoint;
        [DataMember(Name = "edit")] public ClipEdit Edit;
    }

    [DataContract]
    public sealed class SavedSession
    {
        [DataMember(Name = "clips")] public List<SavedClip> Clips = new List<SavedClip>();
        [DataMember(Name = "selected")] public int Selected = -1;
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string Aspect = "5:3";
        [DataMember] public double CustomW = 5;
        [DataMember] public double CustomH = 3;
        [DataMember] public int Width = 680;
        [DataMember] public int Fps = 12;
        [DataMember] public string Dither = "Balanced";
        [DataMember] public double LimitMB = 8;
        [DataMember] public bool AutoFit = true;
        [DataMember] public bool SnapToScenes = true;
        [DataMember] public string FitPriority = "KeepSize";
        [DataMember] public int MinFps = 10;
        [DataMember] public int MinWidth = 600;
        [DataMember] public bool ForceLimit = true;
        [DataMember] public string ExportDir;
        [DataMember] public string SkippedVersion;
        [DataMember] public DateTime LastUpdateCheck;

        [OnDeserializing]
        void Defaults(StreamingContext _)
        {
            Aspect = "5:3"; CustomW = 5; CustomH = 3; Width = 680; Fps = 12; Dither = "Balanced"; LimitMB = 8;
            AutoFit = true; SnapToScenes = true; FitPriority = "KeepSize"; MinFps = 10; MinWidth = 600; ForceLimit = true;
        }
    }

    /// <summary>JSON files in %APPDATA%\VNEGifMaker.</summary>
    public static class Store
    {
        public static string Dir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VNEGifMaker");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static T Load<T>(string name) where T : class
        {
            try
            {
                var p = Path.Combine(Dir, name);
                if (!File.Exists(p)) return null;
                using (var f = File.OpenRead(p)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(f);
            }
            catch { return null; }
        }

        public static void Save<T>(string name, T value)
        {
            try
            {
                var p = Path.Combine(Dir, name);
                var tmp = p + ".tmp";
                using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(T)).WriteObject(f, value);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch { }
        }
    }
}
