using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace VneGif
{
    /// <summary>
    /// Per-clip edits (port of ClipEdit in Edit.swift). The same values drive the live
    /// preview and the export: both go through <see cref="EditMath.FfmpegParts"/>.
    /// </summary>
    [DataContract]
    public sealed class ClipEdit
    {
        // framing
        [DataMember(Name = "fit")] public bool Fit;              // false: fill (crop); true: fit, blurred background
        [DataMember(Name = "zoom")] public double Zoom = 1;      // 1...4
        [DataMember(Name = "panX")] public double PanX;          // -1...1 (positive = content moves right)
        [DataMember(Name = "panY")] public double PanY;          // -1...1 (positive = content moves down)
        // edge crop, fractions of the source (0...0.45)
        [DataMember(Name = "cropL")] public double CropL;
        [DataMember(Name = "cropT")] public double CropT;
        [DataMember(Name = "cropR")] public double CropR;
        [DataMember(Name = "cropB")] public double CropB;
        // orientation
        [DataMember(Name = "rotation")] public int Rotation;     // 0, 90, 180, 270 clockwise
        [DataMember(Name = "flipH")] public bool FlipH;
        [DataMember(Name = "flipV")] public bool FlipV;
        // time
        [DataMember(Name = "speed")] public double Speed = 1;    // 0.25...2
        // look
        [DataMember(Name = "brightness")] public double Brightness;   // -0.3...0.3
        [DataMember(Name = "contrast")] public double Contrast = 1;   // 0.5...1.5
        [DataMember(Name = "saturation")] public double Saturation = 1; // 0...2
        [DataMember(Name = "sharpen")] public double Sharpen;         // 0...1.5
        [DataMember(Name = "denoise")] public bool Denoise;

        public bool HasColor => Brightness != 0 || Contrast != 1 || Saturation != 1;
        public bool HasEdgeCrop => CropL > 0 || CropT > 0 || CropR > 0 || CropB > 0;

        public void ResetFraming() { Zoom = 1; PanX = 0; PanY = 0; }

        public void CopyLook(ClipEdit o)
        {
            Brightness = o.Brightness; Contrast = o.Contrast; Saturation = o.Saturation;
            Sharpen = o.Sharpen; Denoise = o.Denoise;
        }

        public ClipEdit Clone() => (ClipEdit)MemberwiseClone();

        /// <summary>Old session files may miss fields; the deserializer skips constructors.</summary>
        [OnDeserializing]
        void Defaults(StreamingContext _) { Zoom = 1; Speed = 1; Contrast = 1; Saturation = 1; }

        /// <summary>Stable text form, used as a cache key for rendered preview frames.</summary>
        public string Key()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join("|", Fit, Zoom.ToString("R", c), PanX.ToString("R", c), PanY.ToString("R", c),
                CropL.ToString("R", c), CropT.ToString("R", c), CropR.ToString("R", c), CropB.ToString("R", c),
                Rotation, FlipH, FlipV, Speed.ToString("R", c), Brightness.ToString("R", c),
                Contrast.ToString("R", c), Saturation.ToString("R", c), Sharpen.ToString("R", c), Denoise);
        }
    }

    public struct EditLayout
    {
        public double Iw, Ih;     // size after edge crop + rotation (source px)
        public double S;          // scale from that image into the frame
        public double Dw, Dh;     // scaled size
        public double Tlx, Tly;   // top-left of the scaled image inside the frame
    }

    public static class EditMath
    {
        public static EditLayout Layout(ClipEdit e, double srcW, double srcH, double W, double H)
        {
            double cw = Math.Max(2, srcW * (1 - e.CropL - e.CropR));
            double ch = Math.Max(2, srcH * (1 - e.CropT - e.CropB));
            bool rotated = e.Rotation % 180 != 0;
            double iw = rotated ? ch : cw, ih = rotated ? cw : ch;
            double s0 = e.Fit ? Math.Min(W / iw, H / ih) : Math.Max(W / iw, H / ih);
            double s = s0 * Math.Max(1, e.Zoom);
            double dw = iw * s, dh = ih * s;
            return new EditLayout
            {
                Iw = iw, Ih = ih, S = s, Dw = dw, Dh = dh,
                Tlx = (W - dw) / 2 + e.PanX * Math.Abs(W - dw) / 2,
                Tly = (H - dh) / 2 + e.PanY * Math.Abs(H - dh) / 2,
            };
        }

        static int Even(double v) => Math.Max(2, (int)Math.Floor(v) / 2 * 2);
        static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        /// <summary>
        /// ffmpeg filter graph parts for input <paramref name="i"/>, ending in label [v{i}] (rgb24, W×H).
        /// Same graph as the macOS app, minus the VideoToolbox-specific notes.
        /// </summary>
        public static List<string> FfmpegParts(int i, ClipEdit e, double srcW, double srcH, int W, int H, int fps)
        {
            // Edge crop in integer (even) source pixels so the following math is exact.
            double sw = Even(srcW), sh = Even(srcH);
            int cx = Even(sw * e.CropL), cy = Even(sh * e.CropT);
            int cw = Math.Min((int)sw - cx, Even(sw * (1 - e.CropL - e.CropR)));
            int ch = Math.Min((int)sh - cy, Even(sh * (1 - e.CropT - e.CropB)));
            string pre = $"[{i}:v]setpts=(PTS-STARTPTS)/{F(e.Speed)},fps={fps},format=yuv420p";
            if (e.HasEdgeCrop) pre += $",crop={cw}:{ch}:{cx}:{cy}";
            switch (e.Rotation)
            {
                case 90: pre += ",transpose=clock"; break;
                case 180: pre += ",hflip,vflip"; break;
                case 270: pre += ",transpose=cclock"; break;
            }
            if (e.FlipH) pre += ",hflip";
            if (e.FlipV) pre += ",vflip";

            string post = "";
            if (e.Denoise) post += ",hqdn3d=4:3:6:4";
            if (e.HasColor) post += $",eq=brightness={F(e.Brightness)}:contrast={F(e.Contrast)}:saturation={F(e.Saturation)}";
            if (e.Sharpen > 0.01) post += $",unsharp=5:5:{F(e.Sharpen)}:5:5:0";
            post += $",setsar=1,format=rgb24[v{i}]";

            var ee = e.Clone();
            ee.CropL = cx / sw; ee.CropT = cy / sh;
            ee.CropR = 1 - (cx + cw) / sw; ee.CropB = 1 - (cy + ch) / sh;
            var L = Layout(ee, sw, sh, W, H);
            int iw = Round(L.Iw), ih = Round(L.Ih);

            if (!e.Fit)
            {
                // Visible window of the (cropped, rotated) image, then scale to the frame.
                int w0 = Math.Min(iw, Even(W / L.S)), h0 = Math.Min(ih, Even(H / L.S));
                int x0 = Math.Min(Math.Max(0, Round(-L.Tlx / L.S)), iw - w0);
                int y0 = Math.Min(Math.Max(0, Round(-L.Tly / L.S)), ih - h0);
                return new List<string> { pre + $",crop={w0}:{h0}:{x0}:{y0},scale={W}:{H}:flags=lanczos" + post };
            }

            int bw = Math.Max(2, W / 4 / 2 * 2), bh = Math.Max(2, H / 4 / 2 * 2);
            int blur = Math.Max(1, Math.Min(6, bh / 8));
            return new List<string>
            {
                pre + $",split[f{i}][g{i}]",
                $"[g{i}]scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},"
                    + $"boxblur={blur}:2,scale={W}:{H},eq=brightness=-0.07[b{i}]",
                $"[f{i}]scale={Even(L.Dw)}:{Even(L.Dh)}:flags=lanczos[k{i}]",
                $"[b{i}][k{i}]overlay={Round(L.Tlx)}:{Round(L.Tly)}" + post,
            };
        }
    }
}
