using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VneGif
{
    /// <summary>Runs the bundled command-line tools (ffmpeg, gifsicle).</summary>
    public static class Proc
    {
        /// <summary>Quotes one argument by the Windows (MSVCRT) rules; .NET on macOS parses the same rules.</summary>
        public static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(c);
                bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        public static ProcessStartInfo Info(string exe, IEnumerable<string> args, string workDir = null)
        {
            var si = new ProcessStartInfo(exe, Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = false,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (workDir != null) si.WorkingDirectory = workDir;
            return si;
        }

        /// <summary>
        /// Runs to completion. stderr lines (split on \r and \n) go to <paramref name="onLine"/>.
        /// Throws <see cref="EncodeException"/> with the end of stderr on failure,
        /// <see cref="OperationCanceledException"/> when cancelled (the process is killed).
        /// </summary>
        public static Task RunAsync(string exe, IList<string> args, CancellationToken ct,
                                    Action<string> onLine = null, string workDir = null, string failPrefix = "")
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using (var p = new Process { StartInfo = Info(exe, args, workDir) })
                {
                    p.Start();
                    using (ct.Register(() => Kill(p)))
                    {
                        // stdout is not used; drain it so the child never blocks on a full pipe.
                        var drain = p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                        var tail = new StringBuilder();
                        ReadLines(p.StandardError, line =>
                        {
                            tail.Append(line).Append('\n');
                            if (tail.Length > 2000) tail.Remove(0, tail.Length - 1000);
                            onLine?.Invoke(line);
                        });
                        p.WaitForExit();
                        try { drain.Wait(); } catch { }
                        ct.ThrowIfCancellationRequested();
                        if (p.ExitCode != 0)
                        {
                            var msg = tail.ToString().Trim();
                            if (msg.Length > 400) msg = msg.Substring(msg.Length - 400);
                            throw new EncodeException(failPrefix + msg);
                        }
                    }
                }
            }, ct);
        }

        public static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// <summary>Reads text, calling back per line; \r counts as a line end (ffmpeg progress).</summary>
        public static void ReadLines(StreamReader r, Action<string> onLine)
        {
            var buf = new char[4096];
            var line = new StringBuilder();
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (line.Length > 0) { onLine(line.ToString()); line.Clear(); }
                    }
                    else line.Append(c);
                }
            }
            if (line.Length > 0) onLine(line.ToString());
        }
    }

    /// <summary>Finds the bundled tools: next to the app, or via VNE_FFMPEG / VNE_GIFSICLE (tests).</summary>
    public static class Tools
    {
        static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';
        static string Find(string name, string env)
        {
            var e = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(e) && File.Exists(e)) return e;
            var exe = IsWindows ? name + ".exe" : name;
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(dir, exe), Path.Combine(dir, "tools", exe) })
                if (File.Exists(c)) return c;
            return null;
        }

        static string ffmpeg, gifsicle;
        static bool looked;
        static void Look()
        {
            if (looked) return;
            ffmpeg = Find("ffmpeg", "VNE_FFMPEG");
            gifsicle = Find("gifsicle", "VNE_GIFSICLE");
            looked = true;
        }
        public static string FFmpeg { get { Look(); return ffmpeg; } }
        public static string Gifsicle { get { Look(); return gifsicle; } }
    }
}
