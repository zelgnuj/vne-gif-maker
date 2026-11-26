using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace VneGif.App
{
    /// <summary>
    /// Checks GitHub Releases for a newer version. The installed app downloads the new setup and
    /// runs it silently (it closes this app and reopens the new one); a portable copy opens the page.
    /// Releases come from ./release.sh (assets: VnExpress-GIF-Maker-Windows-Setup.exe, …-Portable.zip).
    /// </summary>
#pragma warning disable CS0649 // assigned by the JSON deserializer
    public static class Updater
    {
        public const string Repo = "zelgnuj/vne-gif-maker";
        public const string SetupAsset = "VnExpress-GIF-Maker-Windows-Setup.exe";
        public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";
        static bool busy;

        [DataContract]
        sealed class Asset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "browser_download_url")] public string Url;
        }

        [DataContract]
        sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "body")] public string Body;
            [DataMember(Name = "assets")] public List<Asset> Assets;
            public string Version => Tag != null && Tag.StartsWith("v") ? Tag.Substring(1) : Tag;
        }

        /// <summary>At launch: at most once a day, silent unless there is an update.</summary>
        public static void CheckOnLaunch(MainWindow w)
        {
            var last = w.State.S.LastUpdateCheck;
            if (last != default && (DateTime.UtcNow - last).TotalHours < 20) return;
            Check(w, false);
        }

        static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VNEGifMaker-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static async void Check(MainWindow w, bool userInitiated)
        {
            if (busy) return;
            busy = true;
            try
            {
                Release rel;
                using (var c = Client())
                {
                    var resp = await c.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                    if ((int)resp.StatusCode == 404) rel = null;
                    else
                    {
                        resp.EnsureSuccessStatusCode();
                        var bytes = await resp.Content.ReadAsByteArrayAsync();
                        using (var ms = new MemoryStream(bytes))
                            rel = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(ms);
                    }
                }
                w.State.S.LastUpdateCheck = DateTime.UtcNow;
                w.State.SaveNow();
                var asset = rel?.Assets?.FirstOrDefault(a => a.Name == SetupAsset);
                if (rel == null || asset == null || !IsNewer(rel.Version, Brand.Version))
                {
                    if (userInitiated) MessageBox.Show(w, $"Phiên bản {Brand.Version}.", "Bạn đang dùng bản mới nhất", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!userInitiated && w.State.S.SkippedVersion == rel.Version) return;
                var notes = (rel.Body ?? "").Trim();
                var text = $"Đã có phiên bản {rel.Version}. Bạn đang dùng bản {Brand.Version}."
                           + (notes.Length > 0 ? "\n\n" + (notes.Length > 800 ? notes.Substring(0, 800) : notes) : "")
                           + "\n\nCập nhật ngay?\n(Có: cập nhật · Không: để sau · Huỷ: bỏ qua bản này)";
                var answer = MessageBox.Show(w, text, "Cập nhật " + Brand.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Cancel) { w.State.S.SkippedVersion = rel.Version; w.State.SaveNow(); return; }
                if (answer != MessageBoxResult.Yes) return;
                await Install(w, rel, asset);
            }
            catch (Exception ex)
            {
                if (userInitiated)
                    MessageBox.Show(w, "Kiểm tra kết nối mạng rồi thử lại.\n" + ex.Message, "Không kiểm tra được bản cập nhật",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { busy = false; }
        }

        /// <summary>Installed with the setup (per-user, %LOCALAPPDATA%\Programs) rather than unzipped somewhere.</summary>
        static bool IsInstalled()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            return File.Exists(Path.Combine(dir, "Uninstall.exe"));
        }

        static async Task Install(MainWindow w, Release rel, Asset asset)
        {
            if (w.State.IsEncoding)
            {
                MessageBox.Show(w, "Hãy đợi xuất xong rồi cập nhật lại (tab Xuất › Kiểm tra cập nhật…).", "Đang xuất GIF");
                return;
            }
            if (!IsInstalled())
            {
                // Portable copy: let the user replace the folder themselves.
                Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
                return;
            }
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"vnegif-setup-{rel.Version}.exe");
                using (var c = Client())
                using (var resp = await c.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var f = File.Create(tmp)) await resp.Content.CopyToAsync(f);
                }
                w.State.SaveNow();
                // /S = silent; /UPDATE = wait for this app to close, then relaunch it when done.
                Process.Start(new ProcessStartInfo(tmp, "/S /UPDATE") { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(w, ex.Message + "\n\nMở trang tải để cập nhật thủ công?", "Cập nhật không thành công",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
            }
        }

        /// <summary>"1.10" &gt; "1.9"; missing parts count as 0.</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] P(string s) => (s ?? "0").Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
            var pa = P(a); var pb = P(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }
    }
}
