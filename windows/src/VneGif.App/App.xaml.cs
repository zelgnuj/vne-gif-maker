using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace VneGif.App
{
    public partial class App : Application
    {
        /// <summary>Unattended CI run (see Smoke.cs): no update check, no saved session.</summary>
        public static bool Smoking;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            DispatcherUnhandledException += OnUnhandled;
            Smoking = e.Args.Length >= 2 && e.Args[0] == "--smoke";
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
            if (Smoking) { Smoke.Run(w, e.Args[1], e.Args.Skip(2).ToArray()); return; }
            foreach (var a in e.Args)
                if (File.Exists(a)) w.State.Add(new[] { a });
        }

        /// <summary>Last-resort handler: show the error instead of vanishing, and log it.</summary>
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (Smoking)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "vnegif-smoke-errors.log"), e.Exception + "\n"); } catch { }
                Environment.Exit(4);
            }
            try { File.AppendAllText(Path.Combine(Store.Dir, "errors.log"), $"{DateTime.Now:u} {e.Exception}\n\n"); } catch { }
            MessageBox.Show("Đã xảy ra lỗi không mong muốn:\n" + e.Exception.Message
                            + "\n\nChi tiết được ghi trong %APPDATA%\\VNEGifMaker\\errors.log.",
                            Brand.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
