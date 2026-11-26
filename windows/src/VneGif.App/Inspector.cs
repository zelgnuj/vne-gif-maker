using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VneGif.App
{
    /// <summary>Right-hand inspector: per-clip edits and export settings (port of Inspector.swift).</summary>
    public partial class MainWindow
    {
        int tab;
        StackPanel editPage, exportPage;
        readonly List<ParamRow> rows = new List<ParamRow>();
        readonly List<Action> refreshers = new List<Action>();

        ClipEdit E => State.Selected?.Edit ?? new ClipEdit();

        void Edit(Action<ClipEdit> f) { State.MutateEdit(f); RefreshInspector(); }

        ParamRow P(string label, Func<ClipEdit, double> get, Action<ClipEdit, double> set, double min, double max, double reset,
                   string unit = "", double scale = 1, int decimals = 0, double step = 0.01, bool bipolar = false)
        {
            var r = new ParamRow(label, () => get(E), v => State.MutateEdit(e => set(e, v)), min, max, reset, unit, scale, decimals, step, bipolar);
            rows.Add(r);
            return r;
        }

        static DockPanel LabeledRow(string label, UIElement content)
        {
            var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = UI.RowLabel(label);
            l.Width = 98;
            DockPanel.SetDock(l, Dock.Left);
            d.Children.Add(l);
            d.Children.Add(content);
            return d;
        }

        static Button Small(string text, Action a, string tip = null)
        {
            var b = UI.Button(text, a, tip);
            b.Height = 24;
            b.MinHeight = 0;
            b.FontSize = 12;
            b.Padding = new Thickness(8, 0, 8, 0);
            b.Margin = new Thickness(0, 0, 4, 0);
            return b;
        }

        Segmented tabs;
        public void SmokeTab(int i) { tab = i; tabs.Index = i; RefreshInspector(); }

        void BuildInspector()
        {
            tabs = new Segmented(new[] { "Chỉnh sửa", "Xuất" }) { Height = 28 };
            tabs.Index = 0;
            tabs.Selected += i => { tab = i; RefreshInspector(); };
            TabHost.Child = tabs;
            editPage = BuildEditPage();
            exportPage = BuildExportPage();
            InspectorStack.Children.Add(editPage);
            InspectorStack.Children.Add(exportPage);
        }

        static Border Divider() => new Border { Height = 1, Background = UI.Res("Line") };

        StackPanel BuildEditPage()
        {
            var page = new StackPanel();
            // Header
            var head = new StackPanel { Margin = new Thickness(14, 14, 14, 14) };
            var top = new DockPanel();
            var title = UI.Text("", 14, "Text", FontWeights.SemiBold);
            var reset = Small("Đặt lại", () => { State.ResetEdit(); RefreshInspector(); }, "Đặt lại mọi chỉnh sửa của đoạn này");
            reset.Margin = new Thickness(0);
            DockPanel.SetDock(reset, Dock.Right);
            top.Children.Add(reset);
            top.Children.Add(title);
            head.Children.Add(top);
            var file = UI.Text("", 11.5, "Text2");
            file.Margin = new Thickness(0, 4, 0, 0);
            var dims = UI.Text("", 11.5, "Text2");
            head.Children.Add(file);
            head.Children.Add(dims);
            refreshers.Add(() =>
            {
                var c = State.Selected;
                if (c == null) return;
                title.Text = $"Đoạn {State.SelectedIndex + 1}";
                file.Text = c.FileName;
                dims.Text = $"{c.W} × {c.H} px · {(c.IsVertical ? "Dọc" : "Ngang")} · {Num.Fmt(c.Info.Fps, 2)} fps · {c.Info.Codec}";
            });
            page.Children.Add(head);
            page.Children.Add(Divider());

            // Bố cục
            var layout = new Section("Bố cục");
            var mode = new Segmented(new[] { "Lấp đầy", "Vừa khung" }) { Height = 28 };
            mode.Selected += i => Edit(e => e.Fit = i == 1);
            layout.Add(LabeledRow("Chế độ", mode));
            var fitNote = UI.Caption("Phần trống được lấp bằng nền mờ từ chính video.");
            fitNote.Margin = new Thickness(98, 0, 0, 4);
            layout.Add(fitNote);
            layout.Add(P("Thu phóng", e => e.Zoom, (e, v) => e.Zoom = v, 1, 4, 1, "%", 100, 0, 0.01));
            var panX = P("Vị trí ngang", e => e.PanX, (e, v) => e.PanX = v, -1, 1, 0, "%", 100, 0, 0.01, true);
            var panY = P("Vị trí dọc", e => e.PanY, (e, v) => e.PanY = v, -1, 1, 0, "%", 100, 0, 0.01, true);
            layout.Add(panX);
            layout.Add(panY);
            var alignHost = new ContentControl();
            layout.Add(LabeledRow("Căn nhanh", alignHost));
            var rotLabel = UI.Text("", 11.5, "Text2");
            rotLabel.Margin = new Thickness(6, 0, 0, 0);
            var flipH = new ToggleButton { Content = UI.Icon("", 12), Height = 24, MinHeight = 0, Width = 32, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Lật ngang" };
            var flipVIcon = UI.Icon("", 12);
            flipVIcon.LayoutTransform = new RotateTransform(90);
            var flipV = new ToggleButton { Content = flipVIcon, Height = 24, MinHeight = 0, Width = 32, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Lật dọc" };
            flipH.Click += (s, e) => Edit(x => x.FlipH = flipH.IsChecked == true);
            flipV.Click += (s, e) => Edit(x => x.FlipV = flipV.IsChecked == true);
            var rotLeftIcon = UI.Icon("", 12);
            rotLeftIcon.LayoutTransform = new ScaleTransform(-1, 1);
            var rotL = UI.Button(rotLeftIcon, () => { State.Rotate(-90); RefreshInspector(); }, "Xoay trái 90°");
            var rotR = UI.Button(UI.Icon("", 12), () => { State.Rotate(90); RefreshInspector(); }, "Xoay phải 90°");
            foreach (var b in new[] { rotL, rotR }) { b.Height = 24; b.MinHeight = 0; b.Width = 32; b.Padding = new Thickness(0); b.Margin = new Thickness(0, 0, 4, 0); }
            layout.Add(LabeledRow("Xoay & lật", UI.Row(rotL, rotR, flipH, flipV, rotLabel)));
            refreshers.Add(() =>
            {
                var c = State.Selected;
                if (c == null) return;
                var e = c.Edit;
                mode.Index = e.Fit ? 1 : 0;
                fitNote.Visibility = e.Fit ? Visibility.Visible : Visibility.Collapsed;
                var (ow, oh) = State.OutputSize;
                var L = EditMath.Layout(e, c.W, c.H, ow, oh);
                bool canX = Math.Abs(ow - L.Dw) > 1, canY = Math.Abs(oh - L.Dh) > 1;
                panX.IsEnabled = canX;
                panY.IsEnabled = canY;
                if (canY) alignHost.Content = UI.Row(Small("Trên", () => Edit(x => x.PanY = 1)), Small("Giữa", () => Edit(x => x.PanY = 0)), Small("Dưới", () => Edit(x => x.PanY = -1)));
                else if (canX) alignHost.Content = UI.Row(Small("Trái", () => Edit(x => x.PanX = 1)), Small("Giữa", () => Edit(x => x.PanX = 0)), Small("Phải", () => Edit(x => x.PanX = -1)));
                else alignHost.Content = UI.Text("Tăng thu phóng để di chuyển khung", 11.5, "Text2");
                flipH.IsChecked = e.FlipH;
                flipV.IsChecked = e.FlipV;
                rotLabel.Text = e.Rotation != 0 ? e.Rotation + "°" : "";
            });
            page.Children.Add(layout);
            page.Children.Add(Divider());

            // Cắt viền
            var crop = new Section("Cắt viền");
            crop.Add(P("Trái", e => e.CropL, (e, v) => e.CropL = v, 0, 0.45, 0, "%", 100, 1, 0.005));
            crop.Add(P("Phải", e => e.CropR, (e, v) => e.CropR = v, 0, 0.45, 0, "%", 100, 1, 0.005));
            crop.Add(P("Trên", e => e.CropT, (e, v) => e.CropT = v, 0, 0.45, 0, "%", 100, 1, 0.005));
            crop.Add(P("Dưới", e => e.CropB, (e, v) => e.CropB = v, 0, 0.45, 0, "%", 100, 1, 0.005));
            page.Children.Add(crop);
            page.Children.Add(Divider());

            // Tốc độ
            var speed = new Section("Tốc độ");
            speed.Add(P("Tốc độ phát", e => e.Speed, (e, v) => e.Speed = v, 0.25, 2, 1, "×", 1, 2, 0.05, true));
            var presets = UI.Row(new[] { 0.5, 0.75, 1.0, 1.5, 2.0 }.Select(v => (UIElement)Small(Num.Fmt(v, 2) + "×", () => Edit(e => e.Speed = v))).ToArray());
            presets.Margin = new Thickness(98, 2, 0, 4);
            speed.Add(presets);
            var outDur = UI.Caption("");
            speed.Add(outDur);
            refreshers.Add(() => { var c = State.Selected; if (c != null) outDur.Text = $"Thời lượng trong GIF: {Num.Fmt(c.OutLength, 2)} giây"; });
            page.Children.Add(speed);
            page.Children.Add(Divider());

            // Màu sắc
            var applyAll = Small("Áp dụng cho tất cả", () => { State.ApplyLookToAll(); }, "Áp dụng thiết lập màu sắc của đoạn này cho mọi đoạn trong trình tự");
            applyAll.Margin = new Thickness(0);
            applyAll.FontSize = 11;
            applyAll.Height = 22;
            var look = new Section("Màu sắc", applyAll);
            look.Add(P("Độ sáng", e => e.Brightness, (e, v) => e.Brightness = v, -0.3, 0.3, 0, "", 100, 0, 0.01, true));
            look.Add(P("Tương phản", e => e.Contrast, (e, v) => e.Contrast = v, 0.5, 1.5, 1, "%", 100, 0, 0.01, true));
            look.Add(P("Độ bão hoà", e => e.Saturation, (e, v) => e.Saturation = v, 0, 2, 1, "%", 100, 0, 0.01, true));
            look.Add(P("Độ nét", e => e.Sharpen, (e, v) => e.Sharpen = v, 0, 1.5, 0, "%", 100, 0, 0.01));
            var denoise = new CheckBox { Content = "Giảm nhiễu hạt", ToolTip = "Giảm nhiễu hạt của video gốc, đồng thời giúp GIF nhẹ hơn" };
            denoise.Click += (s, e) => Edit(x => x.Denoise = denoise.IsChecked == true);
            look.Add(LabeledRow("Khử nhiễu", denoise));
            refreshers.Add(() =>
            {
                applyAll.Visibility = State.Clips.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                denoise.IsChecked = E.Denoise;
            });
            page.Children.Add(look);
            return page;
        }

        TextBlock outputText, ditherNote, forceNote, priorityNote;

        StackPanel BuildExportPage()
        {
            var page = new StackPanel();
            var S = State.S;

            var size = new Section("Kích thước");
            var widths = new Segmented(AppState.WidthOptions.Select(w => w.ToString())) { Height = 28 };
            widths.Selected += i => { S.Width = AppState.WidthOptions[i]; State.SettingsChanged(); };
            size.Add(LabeledRow("Chiều rộng", widths));
            outputText = UI.Text("", 12.5);
            size.Add(LabeledRow("Đầu ra", outputText));
            page.Children.Add(size);
            page.Children.Add(Divider());

            var quality = new Section("Chất lượng");
            var fps = new Segmented(AppState.FpsOptions.Select(f => f.ToString())) { Height = 28 };
            fps.Selected += i => { S.Fps = AppState.FpsOptions[i]; State.SettingsChanged(); };
            quality.Add(LabeledRow("FPS", fps));
            var dithers = new[] { Dither.Smooth, Dither.Balanced, Dither.Light };
            var dither = new Segmented(dithers.Select(DitherInfo.Name)) { Height = 28 };
            dither.Selected += i => { S.Dither = dithers[i].ToString(); State.SettingsChanged(); };
            quality.Add(LabeledRow("Phối màu", dither));
            ditherNote = UI.Caption("");
            quality.Add(ditherNote);
            page.Children.Add(quality);
            page.Children.Add(Divider());

            var limit = new Section("Dung lượng");
            var maxBox = new NumBox { Min = 0.5, Max = 50, Step = 0.5, Decimals = 1, Unit = " MB", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            maxBox.Committed += v => { S.LimitMB = v; State.SettingsChanged(); };
            limit.Add(LabeledRow("Tối đa", maxBox));
            var auto = new CheckBox { Content = "Tự động tối ưu khi vượt giới hạn", Margin = new Thickness(98, 4, 0, 2) };
            auto.Click += (s, e) => { S.AutoFit = auto.IsChecked == true; State.SettingsChanged(); };
            limit.Add(auto);
            var force = new CheckBox { Content = "Bắt buộc đạt dưới giới hạn", Margin = new Thickness(98, 4, 0, 4) };
            force.Click += (s, e) => { S.ForceLimit = force.IsChecked == true; State.SettingsChanged(); };
            limit.Add(force);
            forceNote = UI.Caption("");
            limit.Add(forceNote);
            var advToggle = new TextBlock
            {
                Text = "▸ Tuỳ chọn nâng cao", Foreground = UI.Res("Text2"), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
            };
            var adv = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            advToggle.MouseLeftButtonDown += (s, e) =>
            {
                adv.Visibility = adv.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                advToggle.Text = (adv.Visibility == Visibility.Visible ? "▾" : "▸") + " Tuỳ chọn nâng cao";
            };
            var prio = new Segmented(new[] { "Kích thước & FPS", "Độ nét ảnh" }) { Height = 28 };
            prio.Selected += i => { S.FitPriority = i == 0 ? "KeepSize" : "KeepSharpness"; State.SettingsChanged(); };
            adv.Children.Add(LabeledRow("Ưu tiên giữ", prio));
            priorityNote = UI.Caption("");
            adv.Children.Add(priorityNote);
            var minFps = new NumBox { Min = 6, Max = 30, Step = 1, Unit = " fps", Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            minFps.Committed += v => { S.MinFps = (int)Math.Round(v); State.SettingsChanged(); };
            adv.Children.Add(LabeledRow("FPS tối thiểu", minFps));
            var minW = new NumBox { Min = 240, Max = 1200, Step = 10, Unit = " px", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            minW.Committed += v => { S.MinWidth = (int)Math.Round(v) / 2 * 2; State.SettingsChanged(); };
            adv.Children.Add(LabeledRow("Rộng tối thiểu", minW));
            var advBox = new StackPanel();
            advBox.Children.Add(advToggle);
            advBox.Children.Add(adv);
            limit.Add(advBox);
            page.Children.Add(limit);
            page.Children.Add(Divider());

            var about = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
            var ver = UI.Text($"{Brand.Name} · phiên bản {Brand.Version}", 11.5, "Text2");
            about.Children.Add(ver);
            var credit = UI.Text(Brand.Credit, 11, "Text3");
            credit.Margin = new Thickness(0, 2, 0, 8);
            about.Children.Add(credit);
            var check = Small("Kiểm tra cập nhật…", () => Updater.Check(this, true));
            check.HorizontalAlignment = HorizontalAlignment.Left;
            about.Children.Add(check);
            page.Children.Add(about);

            refreshers.Add(() =>
            {
                widths.Index = Array.IndexOf(AppState.WidthOptions, S.Width);
                var (w, h) = State.OutputSize;
                outputText.Text = $"{w} × {h} px · tỉ lệ {State.AspectLabel}";
                fps.Index = Array.IndexOf(AppState.FpsOptions, S.Fps);
                var d = Enum.TryParse<Dither>(S.Dither, out var dd) ? dd : Dither.Balanced;
                dither.Index = Array.IndexOf(dithers, d);
                ditherNote.Text = DitherInfo.Note(d);
                maxBox.Value = S.LimitMB; maxBox.Refresh();
                auto.IsChecked = S.AutoFit;
                force.IsChecked = S.ForceLimit;
                force.Visibility = forceNote.Visibility = advBox.Visibility = S.AutoFit ? Visibility.Visible : Visibility.Collapsed;
                forceNote.Text = S.ForceLimit
                    ? $"App tự cân đối mức nén, FPS và chiều rộng. Nếu đã chạm mức tối thiểu mà vẫn vượt, app giảm thêm (thấp nhất {GifEncoder.HardMinFps} fps · {GifEncoder.HardMinWidth} px) cho đến khi đạt."
                    : "App tự cân đối mức nén, FPS và chiều rộng nhưng không xuống dưới mức tối thiểu; có thể vẫn vượt giới hạn.";
                bool keepSize = S.FitPriority != "KeepSharpness";
                prio.Index = keepSize ? 0 : 1;
                priorityNote.Text = keepSize
                    ? "Giữ nguyên chiều rộng và FPS, nén mạnh dần (có thể thấy hạt nhiễu nhẹ). Chỉ giảm FPS rồi chiều rộng khi đã nén tối đa mà vẫn vượt."
                    : "Giảm FPS rồi chiều rộng trước để giữ ảnh sạch. Chỉ nén mạnh khi đã chạm mức tối thiểu.";
                minFps.Value = S.MinFps; minFps.Refresh();
                minW.Value = S.MinWidth; minW.Refresh();
            });
            return page;
        }

        TextBlock noSelection;

        void RefreshInspector()
        {
            if (editPage == null) return;
            bool hasClip = State.Selected != null;
            editPage.Visibility = tab == 0 && hasClip ? Visibility.Visible : Visibility.Collapsed;
            exportPage.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (noSelection == null)
            {
                noSelection = UI.Text("Chọn một đoạn trong trình tự để chỉnh sửa.", 12.5, "Text2");
                noSelection.Margin = new Thickness(14);
                InspectorStack.Children.Add(noSelection);
            }
            noSelection.Visibility = tab == 0 && !hasClip ? Visibility.Visible : Visibility.Collapsed;
            foreach (var r in rows) r.Refresh();
            foreach (var f in refreshers) f();
        }

        // MARK: Export status / result

        string shownResult;

        void RefreshExportPanel()
        {
            var host = ExportPanelHost;
            host.Visibility = State.Status == StatusKind.Idle ? Visibility.Collapsed : Visibility.Visible;
            switch (State.Status)
            {
                case StatusKind.Idle:
                    host.Child = null;
                    shownResult = null;
                    break;
                case StatusKind.Encoding:
                    {
                        shownResult = null;
                        var p = new StackPanel();
                        p.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 8) });
                        var d = new DockPanel();
                        var cancel = Small("Huỷ", State.CancelExport);
                        cancel.Margin = new Thickness(8, 0, 0, 0);
                        DockPanel.SetDock(cancel, Dock.Right);
                        d.Children.Add(cancel);
                        var t = UI.Text(State.StatusMessage, 12.5);
                        t.TextWrapping = TextWrapping.Wrap;
                        d.Children.Add(t);
                        p.Children.Add(d);
                        host.Child = p;
                        break;
                    }
                case StatusKind.Failed:
                    {
                        shownResult = null;
                        var d = new DockPanel();
                        var x = UI.Button(UI.Icon("", 10), State.DismissStatus, "Đóng", "Ghost");
                        DockPanel.SetDock(x, Dock.Right);
                        x.VerticalAlignment = VerticalAlignment.Top;
                        d.Children.Add(x);
                        var icon = UI.Icon("", 14);
                        icon.Foreground = UI.Res("Bad");
                        icon.VerticalAlignment = VerticalAlignment.Top;
                        icon.Margin = new Thickness(0, 2, 8, 0);
                        DockPanel.SetDock(icon, Dock.Left);
                        d.Children.Add(icon);
                        d.Children.Add(new TextBox
                        {
                            Text = State.StatusMessage, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
                            Padding = new Thickness(0), BorderThickness = new Thickness(0), MaxHeight = 160,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        });
                        host.Child = d;
                        break;
                    }
                case StatusKind.Done:
                    {
                        var r = State.Result;
                        if (shownResult == r.Path + r.Bytes) break;
                        shownResult = r.Path + r.Bytes;
                        host.Child = ResultView(r);
                        break;
                    }
            }
        }

        FrameworkElement ResultView(GifResult r)
        {
            var p = new StackPanel();
            var gif = new GifView(r.Path, 330) { MaxHeight = 160, Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand,
                ToolTip = "Kéo ảnh này thả thẳng vào CMS hoặc trình duyệt" };
            gif.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var data = new DataObject(DataFormats.FileDrop, new[] { r.Path });
                DragDrop.DoDragDrop(gif, data, DragDropEffects.Copy);
            };
            p.Children.Add(gif);
            var line = new DockPanel();
            var x = UI.Button(UI.Icon("", 10), State.DismissStatus, "Đóng", "Ghost");
            DockPanel.SetDock(x, Dock.Right);
            line.Children.Add(x);
            var icon = UI.Icon(r.UnderLimit ? "" : "", 14);
            icon.Foreground = UI.Res(r.UnderLimit ? "Ok" : "Warn");
            icon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var mb = UI.Text($"{Num.Fmt(r.Bytes / 1e6, 2)} MB", 12.5, "Text", FontWeights.Bold);
            DockPanel.SetDock(mb, Dock.Left);
            line.Children.Add(mb);
            var notes = r.Notes;
            line.Children.Add(UI.Text($" · {r.Width} × {r.Height} · {r.Fps} fps" + (notes.Count == 0 ? "" : " · " + string.Join(" · ", notes)), 12.5, "Text2"));
            p.Children.Add(line);
            if (!r.UnderLimit)
            {
                var warn = new TextBlock
                {
                    Text = r.BelowFloor
                        ? $"Vẫn vượt giới hạn {Num.Fmt(r.LimitBytes / 1e6, 1)} MB dù đã nén tối đa và giảm tới {GifEncoder.HardMinFps} fps · {GifEncoder.HardMinWidth} px. Hãy rút ngắn thời lượng."
                        : $"Vẫn vượt giới hạn {Num.Fmt(r.LimitBytes / 1e6, 1)} MB dù đã nén tối đa ở mức FPS và chiều rộng tối thiểu. Hãy rút ngắn thời lượng, hoặc bật “Bắt buộc đạt dưới giới hạn”.",
                    Foreground = UI.Res("Warn"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                };
                p.Children.Add(warn);
            }
            var name = UI.Text(System.IO.Path.GetFileName(r.Path), 11.5, "Text2");
            name.Margin = new Thickness(0, 6, 0, 8);
            p.Children.Add(name);
            var show = Small("Hiện trong thư mục", () =>
            {
                try { Process.Start("explorer.exe", "/select,\"" + r.Path + "\""); } catch { }
            });
            var copy = Small("Sao chép tệp", () =>
            {
                try
                {
                    var files = new System.Collections.Specialized.StringCollection { r.Path };
                    Clipboard.SetFileDropList(files);
                }
                catch { }
            }, "Dán vào thư mục, CMS hoặc ứng dụng chat");
            p.Children.Add(UI.Row(show, copy));
            return p;
        }
    }
}
