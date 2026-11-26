using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace VneGif.App
{
    public partial class MainWindow : Window
    {
        public readonly AppState State;

        public MainWindow()
        {
            InitializeComponent();
            State = new AppState(Dispatcher);
            BuildToolbar();
            BuildEmpty();
            BuildPreview();
            BuildTransport();
            BuildTimeline();
            BuildStrip();
            BuildInspector();

            State.Changed += Refresh;
            State.ClipsChanged += RebuildStrip;
            State.Preview.BitmapChanged += () => previewImage.Source = State.Preview.Bitmap;
            State.Preview.TimeChanged += UpdateTime;
            State.Error += (title, msg) => MessageBox.Show(this, msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SourceInitialized += (s, e) => Brand.DarkTitleBar(this);
            PreviewKeyDown += OnKey;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
            Drop += OnDrop;
            Closing += (s, e) => { State.CancelExport(); State.SaveNow(); };
            if (!App.Smoking)
            {
                Loaded += (s, e) => Updater.CheckOnLaunch(this);
                State.OfferSession();
            }
            Refresh();
            RebuildStrip();
        }

        // MARK: Toolbar

        Button exportButton, aspectButton;
        TextBlock summaryText;

        void BuildToolbar()
        {
            var d = ToolbarDock;
            var mark = Brand.Mark(22);
            mark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(mark, Dock.Left);
            d.Children.Add(mark);
            var title = UI.Text("GIF Maker", 14.5, "Text", FontWeights.SemiBold);
            title.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(title, Dock.Left);
            d.Children.Add(title);
            summaryText = UI.Text("", 12.5, "Text2");
            summaryText.Margin = new Thickness(12, 1, 0, 0);
            DockPanel.SetDock(summaryText, Dock.Left);
            d.Children.Add(summaryText);
            var add = UI.Button(UI.IconLabel("", "Thêm video"), OpenFiles, "Thêm video (Ctrl+O)");
            add.Margin = new Thickness(18, 0, 0, 0);
            add.Height = 28;
            add.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(add, Dock.Left);
            d.Children.Add(add);

            exportButton = UI.Button(UI.IconLabel("", "Xuất GIF…"), Export, "Xuất GIF (Ctrl+Enter)", "Accent");
            exportButton.Height = 30;
            exportButton.Padding = new Thickness(14, 0, 14, 0);
            exportButton.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(exportButton, Dock.Right);
            d.Children.Add(exportButton);

            aspectButton = UI.Button("", ShowAspectMenu, "Tỉ lệ khung hình GIF (áp dụng cho mọi đoạn)");
            aspectButton.Height = 28;
            aspectButton.Margin = new Thickness(0, 0, 10, 0);
            aspectButton.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(aspectButton, Dock.Right);
            d.Children.Add(aspectButton);
        }

        void ShowAspectMenu()
        {
            var m = new ContextMenu { PlacementTarget = aspectButton, Placement = PlacementMode.Bottom };
            void Header(string t) => m.Items.Add(new MenuItem { Header = t, IsEnabled = false, Foreground = UI.Res("Text2") });
            void Item(Aspect a, string label)
            {
                var it = new MenuItem { Header = label, IsChecked = State.Aspect == a };
                it.Click += (s, e) => { State.S.Aspect = a.Id; State.SettingsChanged(); };
                m.Items.Add(it);
            }
            Header("Ngang");
            foreach (var a in Aspect.Landscape) Item(a, a.Label);
            m.Items.Add(new Separator());
            Header("Vuông & dọc");
            foreach (var a in Aspect.SquareAndPortrait) Item(a, a.Label);
            m.Items.Add(new Separator());
            Item(Aspect.Original, "Gốc (theo đoạn đầu)");
            var custom = new MenuItem { Header = "Tuỳ chỉnh…", IsChecked = State.Aspect == Aspect.Custom };
            custom.Click += (s, e) => { State.S.Aspect = Aspect.Custom.Id; State.SettingsChanged(); ShowCustomAspect(); };
            m.Items.Add(custom);
            m.IsOpen = true;
        }

        void ShowCustomAspect()
        {
            var w = new NumBox { Min = 0.1, Max = 100, Step = 1, Decimals = 2, Width = 60, Value = State.S.CustomW };
            var h = new NumBox { Min = 0.1, Max = 100, Step = 1, Decimals = 2, Width = 60, Value = State.S.CustomH };
            w.Committed += v => { State.S.CustomW = v; State.SettingsChanged(); };
            h.Committed += v => { State.S.CustomH = v; State.SettingsChanged(); };
            w.Refresh(); h.Refresh();
            var swap = UI.Button(UI.Icon("", 12), () =>
            {
                var t = State.S.CustomW; State.S.CustomW = State.S.CustomH; State.S.CustomH = t;
                w.Value = State.S.CustomW; h.Value = State.S.CustomH; w.Refresh(); h.Refresh();
                State.SettingsChanged();
            }, "Đổi chiều ngang ↔ dọc", "Ghost");
            var colon = UI.Text(":", 12.5, "Text2");
            colon.Margin = new Thickness(6, 0, 6, 0);
            var label = UI.Text("Tỉ lệ riêng", 12.5, "Text2");
            label.Margin = new Thickness(0, 0, 8, 0);
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)), BorderBrush = UI.Res("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                Child = UI.Row(label, w, colon, h, UI.Gap(6), swap),
            };
            new Popup { PlacementTarget = aspectButton, Placement = PlacementMode.Bottom, StaysOpen = false, Child = panel, IsOpen = true, AllowsTransparency = true };
        }

        // MARK: Empty state

        StackPanel sessionHost;
        TextBlock ffmpegMissing;

        void BuildEmpty()
        {
            var bg = new RadialGradientBrush(Color.FromArgb(42, Brand.Red.R, Brand.Red.G, Brand.Red.B), Colors.Transparent)
            {
                Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.6, RadiusY = 0.8,
            };
            EmptyView.Background = bg;
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 40) };
            var mark = Brand.Mark(64);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Brand.Red, BlurRadius = 28, ShadowDepth = 4, Direction = 270, Opacity = 0.5 };
            col.Children.Add(mark);
            var title = UI.Text("GIF Maker", 30, "Text", FontWeights.Bold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 14, 0, 6);
            col.Children.Add(title);
            var sub = new TextBlock
            {
                Text = $"Ghép một hoặc nhiều đoạn video, ngang hoặc dọc,\nthành ảnh GIF ngang dung lượng dưới {Num.Fmt(State.S.LimitMB, 1)} MB.",
                TextAlignment = TextAlignment.Center, Foreground = UI.Res("Text2"), FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center,
            };
            col.Children.Add(sub);

            var box = new Grid { Width = 460, Margin = new Thickness(0, 30, 0, 0) };
            box.Children.Add(new Rectangle
            {
                RadiusX = 16, RadiusY = 16, Stroke = new SolidColorBrush(Color.FromArgb(115, Brand.Bright.R, Brand.Bright.G, Brand.Bright.B)),
                StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 5, 4 },
                Fill = new SolidColorBrush(Color.FromArgb(16, Brand.Red.R, Brand.Red.G, Brand.Red.B)),
            });
            var inner = new StackPanel { Margin = new Thickness(0, 28, 0, 28), HorizontalAlignment = HorizontalAlignment.Center };
            var arrow = UI.Icon("", 26);
            arrow.Foreground = Brand.BrightBrush;
            inner.Children.Add(arrow);
            var drop = UI.Text("Thả video vào đây", 15, "Text", FontWeights.SemiBold);
            drop.HorizontalAlignment = HorizontalAlignment.Center;
            drop.Margin = new Thickness(0, 10, 0, 12);
            inner.Children.Add(drop);
            var choose = UI.Button("Chọn video…", OpenFiles, "Ctrl+O", "Accent");
            choose.Height = 34;
            choose.Padding = new Thickness(22, 0, 22, 0);
            choose.FontSize = 13.5;
            choose.HorizontalAlignment = HorizontalAlignment.Center;
            inner.Children.Add(choose);
            var formats = UI.Text("MP4, MOV, M4V, MKV, AVI · H.264, HEVC, ProRes, AV1, VP9", 11.5, "Text3");
            formats.HorizontalAlignment = HorizontalAlignment.Center;
            formats.Margin = new Thickness(0, 12, 0, 0);
            inner.Children.Add(formats);
            box.Children.Add(inner);
            col.Children.Add(box);

            sessionHost = new StackPanel { Width = 460, Margin = new Thickness(0, 14, 0, 0) };
            col.Children.Add(sessionHost);
            ffmpegMissing = UI.Text("⚠ Không tìm thấy bộ mã hoá ffmpeg. Vui lòng cài lại ứng dụng.", 12.5, "Warn");
            ffmpegMissing.HorizontalAlignment = HorizontalAlignment.Center;
            ffmpegMissing.Margin = new Thickness(0, 14, 0, 0);
            col.Children.Add(ffmpegMissing);
            EmptyView.Children.Add(col);

            var credit = UI.Text(Brand.Credit + " · phiên bản " + Brand.Version, 11, "Text3");
            credit.VerticalAlignment = VerticalAlignment.Bottom;
            credit.HorizontalAlignment = HorizontalAlignment.Center;
            credit.Margin = new Thickness(0, 0, 0, 12);
            EmptyView.Children.Add(credit);
        }

        void RefreshSessionOffer()
        {
            sessionHost.Children.Clear();
            var saved = State.SavedSession;
            if (saved == null) return;
            var names = saved.Clips.Select(c => System.IO.Path.GetFileName(c.Path)).Distinct().OrderBy(n => n).ToList();
            var g = new DockPanel();
            var icon = UI.Icon("", 20);
            icon.Foreground = Brand.BrightBrush;
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, Dock.Left);
            g.Children.Add(icon);
            var open = UI.Button("Mở lại", State.RestoreSession, null, "Accent");
            open.Height = 28;
            open.Padding = new Thickness(14, 0, 14, 0);
            DockPanel.SetDock(open, Dock.Right);
            var skip = UI.Button("Bỏ qua", () => { State.SavedSession = null; Refresh(); });
            skip.Height = 28;
            skip.Margin = new Thickness(8, 0, 8, 0);
            DockPanel.SetDock(skip, Dock.Right);
            g.Children.Add(open);
            g.Children.Add(skip);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(UI.Text($"Phiên làm việc trước · {saved.Clips.Count} đoạn", 12.5, "Text", FontWeights.SemiBold));
            text.Children.Add(UI.Text(string.Join(", ", names), 11.5, "Text2"));
            g.Children.Add(text);
            sessionHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), Child = g,
            });
        }

        // MARK: Preview

        Grid frameBox;
        Image previewImage;
        Border issueOverlay;
        TextBlock issueText;
        Point panStart;
        double panX0, panY0;
        bool panning;

        void BuildPreview()
        {
            var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x13)), CornerRadius = new CornerRadius(8), ClipToBounds = true };
            frameBox = new Grid { Background = Brushes.Black, Cursor = Cursors.SizeAll };
            previewImage = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(previewImage, BitmapScalingMode.HighQuality);
            frameBox.Children.Add(previewImage);
            var vb = new Viewbox { Stretch = Stretch.Uniform, Child = frameBox };
            var outline = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10), Child = vb,
                ToolTip = "Kéo để di chuyển · cuộn chuột để thu phóng · nhấp đúp để căn giữa · giữ Alt để chỉnh tinh",
            };
            ToolTipService.SetInitialShowDelay(outline, 1500);
            issueText = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, Opacity = 0.8 };
            var issueCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var eye = UI.Icon("", 28);
            eye.Foreground = Brushes.White;
            issueCol.Children.Add(eye);
            var it = UI.Text("Không thể hiển thị video này", 14, "Text", FontWeights.SemiBold);
            it.HorizontalAlignment = HorizontalAlignment.Center;
            it.Margin = new Thickness(0, 10, 0, 8);
            issueCol.Children.Add(it);
            issueCol.Children.Add(issueText);
            issueOverlay = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 0, 0, 0)), Child = issueCol, Visibility = Visibility.Collapsed };
            var g = new Grid();
            g.Children.Add(outline);
            g.Children.Add(issueOverlay);
            host.Child = g;
            Grid.SetRow(host, 0);
            LeftPane.Children.Add(host);

            frameBox.MouseLeftButtonDown += (s, e) =>
            {
                var c = State.Selected;
                if (c == null) return;
                if (e.ClickCount == 2) { State.MutateEdit(x => x.ResetFraming()); RefreshInspector(); return; }
                panStart = e.GetPosition(frameBox);
                panX0 = c.Edit.PanX; panY0 = c.Edit.PanY;
                panning = true;
                frameBox.CaptureMouse();
            };
            frameBox.MouseMove += (s, e) =>
            {
                var c = State.Selected;
                if (!panning || c == null) return;
                var p = e.GetPosition(frameBox);
                var (W, H) = State.OutputSize;
                var L = EditMath.Layout(c.Edit, c.W, c.H, W, H);
                double freeX = Math.Abs(W - L.Dw) / 2, freeY = Math.Abs(H - L.Dh) / 2;
                double fine = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? 0.1 : 1;
                State.MutateEdit(x =>
                {
                    if (freeX > 0.5) x.PanX = Math.Min(1, Math.Max(-1, panX0 + (p.X - panStart.X) * fine / freeX));
                    if (freeY > 0.5) x.PanY = Math.Min(1, Math.Max(-1, panY0 + (p.Y - panStart.Y) * fine / freeY));
                });
                RefreshInspector();
            };
            frameBox.MouseLeftButtonUp += (s, e) => { panning = false; frameBox.ReleaseMouseCapture(); };
            frameBox.MouseWheel += (s, e) =>
            {
                if (State.Selected == null) return;
                double f = Math.Pow(1.08, e.Delta / 120.0);
                State.MutateEdit(x => x.Zoom = Math.Min(4, Math.Max(1, x.Zoom * f)));
                RefreshInspector();
                e.Handled = true;
            };
        }

        // MARK: Transport

        Button playButton;
        TextBlock timeText, durText, sceneText;
        ProgressBar sceneBar;
        ToggleButton snapToggle;
        FrameworkElement sceneControls;

        void BuildTransport()
        {
            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 6), LastChildFill = false };
            Grid.SetRow(bar, 1);
            LeftPane.Children.Add(bar);
            Button Small(object content, Action a, string tip, string style = null)
            {
                var b = UI.Button(content, a, tip, style);
                b.Height = 26;
                b.Margin = new Thickness(0, 0, 6, 0);
                return b;
            }
            var left = UI.Row(
                Small(UI.Icon("", 12), State.JumpToIn, "Về điểm vào"),
                playButton = Small(UI.Icon("", 12), State.TogglePlay, "Phát / tạm dừng (Space)"));
            playButton.Width = 34;
            timeText = UI.Text("00:00,00", 12.5);
            timeText.FontFamily = new FontFamily("Consolas");
            timeText.Margin = new Thickness(6, 0, 4, 0);
            durText = UI.Text("", 12.5, "Text3");
            durText.FontFamily = new FontFamily("Consolas");
            left.Children.Add(timeText);
            left.Children.Add(durText);
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);

            var right = UI.Row(
                Small(UI.IconLabel("", "Điểm vào"), State.MarkIn, "Đặt điểm vào tại vị trí hiện tại (I)"),
                Small(UI.IconLabel("", "Điểm ra"), State.MarkOut, "Đặt điểm ra tại vị trí hiện tại (O)"));
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var div = new Border { Width = 1, Height = 16, Background = UI.Res("Line"), Margin = new Thickness(6, 0, 12, 0) };
            DockPanel.SetDock(div, Dock.Right);
            bar.Children.Add(div);

            sceneBar = new ProgressBar { Width = 70, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sceneText = UI.Text("", 11.5, "Text2");
            sceneText.Margin = new Thickness(0, 0, 10, 0);
            snapToggle = new ToggleButton { Content = UI.Icon("", 12), Height = 26, Width = 32, ToolTip = "Bắt dính vào điểm cắt cảnh khi kéo (S) · giữ Ctrl để tạm tắt" };
            snapToggle.Click += (s, e) => { State.S.SnapToScenes = snapToggle.IsChecked == true; State.SettingsChanged(); };
            var scenes = UI.Row(sceneBar, sceneText,
                Small(UI.Icon("", 11), () => State.JumpScene(-1), "Về điểm cắt cảnh trước (↑)"),
                Small(UI.Icon("", 11), () => State.JumpScene(1), "Tới điểm cắt cảnh sau (↓)"),
                Small("Chọn cảnh", () => State.SelectScene(), "Chọn trọn cảnh tại vị trí hiện tại (X) — hoặc nhấp đúp lên timeline"),
                snapToggle);
            sceneControls = scenes;
            DockPanel.SetDock(scenes, Dock.Right);
            bar.Children.Add(scenes);
        }

        void UpdateTime()
        {
            timeText.Text = Num.Time(State.CurrentTime);
        }

        // MARK: Timeline

        TimelineView timeline;
        NumBox inBox, outBox, lenBox;
        TextBlock speedNote;
        FineSlider zoomSlider;

        void BuildTimeline()
        {
            timeline = new TimelineView(State) { Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(timeline, 2);
            LeftPane.Children.Add(timeline);

            var row = new DockPanel { Margin = new Thickness(12, 6, 12, 0), LastChildFill = false };
            Grid.SetRow(row, 3);
            LeftPane.Children.Add(row);
            FrameworkElement Field(string label, NumBox box)
            {
                var t = UI.Text(label, 12.5, "Text2");
                t.Margin = new Thickness(0, 0, 6, 0);
                var p = UI.Row(t, box);
                p.Margin = new Thickness(0, 0, 14, 0);
                return p;
            }
            inBox = new NumBox { Timecode = true, Width = 88, Step = 1.0 / 30 };
            outBox = new NumBox { Timecode = true, Width = 88, Step = 1.0 / 30 };
            lenBox = new NumBox { Width = 66, Step = 1.0 / 30, Decimals = 2, FixedDecimals = true, Unit = " s", Min = AppState.MinLength };
            inBox.Committed += v => { State.SetIn(v); Reveal(); };
            outBox.Committed += v => { State.SetOut(v); Reveal(); };
            lenBox.Committed += v => { var c = State.Selected; if (c != null) { State.SetOut(c.In + v); Reveal(); } };
            speedNote = UI.Text("", 12.5, "Warn");
            var left = UI.Row(Field("Điểm vào", inBox), Field("Điểm ra", outBox), Field("Thời lượng", lenBox), speedNote);
            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(left);

            double Anchor()
            {
                var z = State.Timeline;
                double p = State.CurrentTime;
                return p >= z.Vs && p <= z.Vs + z.Vl ? p : z.Vs + z.Vl / 2;
            }
            zoomSlider = new FineSlider { Width = 90, Min = 0, Max = 1, Margin = new Thickness(4, 0, 4, 0) };
            zoomSlider.ValueChanged += v => State.Timeline.SetLevel(v, Anchor());
            Button Z(object content, Action a, string tip, string style = "Ghost")
            {
                var b = UI.Button(content, a, tip, style);
                b.Height = 24;
                b.Margin = new Thickness(4, 0, 0, 0);
                b.FontSize = 12;
                return b;
            }
            var right = UI.Row(
                Z(UI.Icon("", 12), () => State.Timeline.Zoom(1 / 1.6, Anchor()), "Thu nhỏ timeline (-)"),
                zoomSlider,
                Z(UI.Icon("", 12), () => State.Timeline.Zoom(1.6, Anchor()), "Phóng to timeline (=)"),
                Z("Vừa đoạn chọn", () => { var c = State.Selected; if (c != null) State.Timeline.Fit(c.In, c.Out); }, "Thu phóng để vừa đoạn đang chọn", null),
                Z("Toàn bộ", State.Timeline.ShowAll, "Hiển thị toàn bộ video", null));
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }

        void Reveal() { var c = State.Selected; if (c != null) State.Timeline.Reveal(c.In, c.Out); }

        // MARK: Sequence strip

        StackPanel cardsPanel;
        TextBlock stripInfo;
        TextBlock stripWarn;
        Button playAllButton, addSegButton;

        void BuildStrip()
        {
            var col = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
            var sep = new Border { Height = 1, Background = UI.Res("Line"), Margin = new Thickness(-12, 0, -12, 12) };
            col.Children.Add(sep);
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            var title = UI.Text("Trình tự", 13.5, "Text", FontWeights.SemiBold);
            stripInfo = UI.Text("", 12.5, "Text2");
            stripInfo.Margin = new Thickness(10, 0, 4, 0);
            stripWarn = UI.Icon("", 12);
            stripWarn.Foreground = UI.Res("Warn");
            stripWarn.ToolTip = "GIF thumbnail nên dài 3–6 giây";
            var l = UI.Row(title, stripInfo, stripWarn);
            DockPanel.SetDock(l, Dock.Left);
            head.Children.Add(l);
            Button B(object c, Action a, string tip = null) { var b = UI.Button(c, a, tip); b.Height = 26; b.Margin = new Thickness(6, 0, 0, 0); return b; }
            playAllButton = B("", State.TogglePlayAll);
            addSegButton = B(UI.IconLabel("", "Lấy thêm đoạn"), State.AddSegmentFromSelected, "Thêm một đoạn khác từ cùng video đang chọn (Ctrl+D)");
            var clear = B("Xoá tất cả", () =>
            {
                if (MessageBox.Show(this, "Xoá tất cả đoạn khỏi trình tự?\nCác tệp video gốc không bị ảnh hưởng.", "Xoá tất cả",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) State.RemoveAll();
            });
            var r = UI.Row(playAllButton, addSegButton, clear);
            DockPanel.SetDock(r, Dock.Right);
            head.Children.Add(r);
            col.Children.Add(head);

            cardsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = cardsPanel,
            };
            scroll.PreviewMouseWheel += (s, e) => { scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; };
            col.Children.Add(scroll);
            Grid.SetRow(col, 4);
            LeftPane.Children.Add(col);
        }

        const string ClipFormat = "vnegif-clip";

        void RebuildStrip()
        {
            cardsPanel.Children.Clear();
            for (int i = 0; i < State.Clips.Count; i++) cardsPanel.Children.Add(Card(State.Clips[i], i + 1));
            var add = new Button
            {
                Width = 96, Height = 72, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent,
                ToolTip = "Thêm video (Ctrl+O) — hoặc thả tệp vào bất kỳ đâu trong cửa sổ",
            };
            var g = new Grid();
            g.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Stroke = UI.Res("Text3"), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 } });
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var plus = UI.Icon("", 16); plus.Foreground = UI.Res("Text2");
            st.Children.Add(plus);
            var t = UI.Text("Thêm video", 11.5, "Text2"); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 4, 0, 0);
            st.Children.Add(t);
            g.Children.Add(st);
            add.Content = g;
            add.Padding = new Thickness(0);
            add.Click += (s, e) => OpenFiles();
            cardsPanel.Children.Add(add);
        }

        FrameworkElement Card(Clip clip, int index)
        {
            bool selected = clip.Id == State.SelectedId;
            var g = new Grid { Width = 124, Height = 72, Background = UI.Res("Raised"), Cursor = Cursors.Hand, AllowDrop = true };
            var img = clip.CardThumb ?? clip.Thumbs.FirstOrDefault();
            if (img != null) g.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
            g.Children.Add(new Border
            {
                Background = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(205, 0, 0, 0), new Point(0, 0.45), new Point(0, 1)),
            });
            var num = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = UI.Text(index.ToString(), 10.5, "Text", FontWeights.Bold),
            };
            g.Children.Add(num);
            var info = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 5) };
            var dur = UI.Text($"{Num.Fmt(clip.OutLength, 1)} s", 11, "Text", FontWeights.Bold);
            dur.Foreground = Brushes.White;
            DockPanel.SetDock(dur, Dock.Left);
            info.Children.Add(dur);
            if (clip.Edit.Speed != 1)
            {
                var sp = UI.Text($" {Num.Fmt(clip.Edit.Speed, 2)}×", 11);
                sp.Foreground = Brushes.White;
                DockPanel.SetDock(sp, Dock.Left);
                info.Children.Add(sp);
            }
            var badges = UI.Row();
            badges.HorizontalAlignment = HorizontalAlignment.Right;
            if (clip.Edit.Fit) { var b = UI.Icon("", 11); b.Foreground = Brushes.White; b.ToolTip = "Vừa khung, nền mờ"; b.Margin = new Thickness(4, 0, 0, 0); badges.Children.Add(b); }
            if (clip.IsVertical) { var b = UI.Icon("", 11); b.Foreground = Brushes.White; b.ToolTip = "Video dọc"; b.Margin = new Thickness(4, 0, 0, 0); badges.Children.Add(b); }
            info.Children.Add(badges);
            g.Children.Add(info);

            var close = new Button
            {
                Content = UI.Icon("", 9), Width = 20, Height = 20, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Xoá đoạn",
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), Visibility = Visibility.Collapsed,
            };
            close.Click += (s, e) => State.Remove(clip.Id);
            g.Children.Add(close);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2.5), Margin = new Thickness(0, 0, 8, 0),
                BorderBrush = selected ? Brand.BrightBrush : Brushes.Transparent, Child = g, ToolTip = clip.FileName,
            };
            g.Clip = new RectangleGeometry(new Rect(0, 0, 124, 72), 6, 6);
            border.MouseEnter += (s, e) => close.Visibility = Visibility.Visible;
            border.MouseLeave += (s, e) => close.Visibility = Visibility.Collapsed;

            Point down = default;
            bool pressed = false;
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsIn(d, close)) return;
                down = e.GetPosition(this); pressed = true;
            };
            border.PreviewMouseMove += (s, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) { pressed = false; return; }
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - down.X) < 6 && Math.Abs(p.Y - down.Y) < 6) return;
                pressed = false;
                DragDrop.DoDragDrop(border, new DataObject(ClipFormat, clip.Id.ToString()), DragDropEffects.Move);
            };
            border.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (pressed && !(e.OriginalSource is DependencyObject d && IsIn(d, close))) State.Select(clip.Id);
                pressed = false;
            };
            border.AllowDrop = true;
            border.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(ClipFormat) && Guid.TryParse((string)e.Data.GetData(ClipFormat), out var id)) State.Move(id, clip.Id);
            };
            var menu = new ContextMenu();
            var more = new MenuItem { Header = "Lấy thêm đoạn từ video này" };
            more.Click += (s, e) => { State.Select(clip.Id); State.AddSegmentFromSelected(); };
            var del = new MenuItem { Header = "Xoá đoạn" };
            del.Click += (s, e) => State.Remove(clip.Id);
            menu.Items.Add(more);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            border.ContextMenu = menu;
            return border;
        }

        static bool IsIn(DependencyObject d, DependencyObject root)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == root) return true;
            return false;
        }

        // MARK: Files

        static readonly string[] VideoExt = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".mts", ".m2ts", ".ts", ".mpg", ".mpeg", ".wmv", ".flv", ".3gp", ".mxf" };

        void OpenFiles()
        {
            var d = new OpenFileDialog
            {
                Title = "Chọn video", Multiselect = true,
                Filter = "Video|" + string.Join(";", VideoExt.Select(e => "*" + e)) + "|Tất cả tệp|*.*",
            };
            if (d.ShowDialog(this) == true) State.Add(d.FileNames);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(ClipFormat)) { e.Effects = DragDropEffects.Move; e.Handled = true; return; }
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(ClipFormat)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var list = new List<string>();
                foreach (var p in paths)
                {
                    if (Directory.Exists(p))
                        list.AddRange(Directory.GetFiles(p).Where(f => VideoExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f));
                    else list.Add(p);
                }
                if (list.Count > 0) State.Add(list);
            }
        }

        void Export()
        {
            if (State.Clips.Count == 0 || State.IsEncoding) return;
            var first = State.Clips[0];
            var d = new SaveFileDialog
            {
                Title = "Xuất GIF", Filter = "Ảnh GIF (*.gif)|*.gif", DefaultExt = ".gif", AddExtension = true, OverwritePrompt = true,
                FileName = first.Name + ".gif",
                InitialDirectory = !string.IsNullOrEmpty(State.S.ExportDir) && Directory.Exists(State.S.ExportDir)
                    ? State.S.ExportDir : System.IO.Path.GetDirectoryName(first.Path),
            };
            if (d.ShowDialog(this) != true) return;
            if (State.Clips.Any(c => string.Equals(System.IO.Path.GetFullPath(c.Path), System.IO.Path.GetFullPath(d.FileName), StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Không thể ghi đè lên video gốc. Hãy chọn tên khác.", "Xuất GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            State.StartExport(d.FileName);
        }

        // MARK: Keyboard

        void OnKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;
            var m = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (m == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.O: OpenFiles(); e.Handled = true; return;
                    case Key.D: State.AddSegmentFromSelected(); e.Handled = true; return;
                    case Key.Enter: Export(); e.Handled = true; return;
                }
                return;
            }
            if (State.Selected == null) return;
            if ((m & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            bool big = (m & ModifierKeys.Shift) != 0;
            switch (key)
            {
                case Key.Space: State.TogglePlay(); break;
                case Key.Left: if (big) State.Step(seconds: -1); else State.Step(frames: -1); break;
                case Key.Right: if (big) State.Step(seconds: 1); else State.Step(frames: 1); break;
                case Key.I: State.MarkIn(); break;
                case Key.O: State.MarkOut(); break;
                case Key.Up: State.JumpScene(-1); break;
                case Key.Down: State.JumpScene(1); break;
                case Key.X: State.SelectScene(); break;
                case Key.S: State.S.SnapToScenes = !State.S.SnapToScenes; State.SettingsChanged(); break;
                case Key.OemPlus: case Key.Add: State.Timeline.Zoom(1.6, State.CurrentTime); break;
                case Key.OemMinus: case Key.Subtract: State.Timeline.Zoom(1 / 1.6, State.CurrentTime); break;
                case Key.Delete: State.Remove(State.Selected.Id); break;
                default: return;
            }
            e.Handled = true;
        }

        // MARK: Refresh

        void Refresh()
        {
            bool empty = State.Clips.Count == 0;
            EmptyView.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EditorView.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            summaryText.Text = State.Summary;
            Title = empty ? Brand.Name : $"{Brand.Name} — {State.Summary}";
            if (empty) { RefreshSessionOffer(); ffmpegMissing.Visibility = Tools.FFmpeg == null ? Visibility.Visible : Visibility.Collapsed; }

            exportButton.IsEnabled = !empty && !State.IsEncoding && Tools.FFmpeg != null;
            exportButton.Content = UI.IconLabel(State.IsEncoding ? "" : "", State.IsEncoding ? "Đang xuất…" : "Xuất GIF…");
            aspectButton.Content = UI.IconLabel("", "Tỉ lệ " + State.AspectLabel);
            PreparingToast.Visibility = State.Preparing != null ? Visibility.Visible : Visibility.Collapsed;
            PreparingText.Text = State.Preparing ?? "";

            var c = State.Selected;
            var (W, H) = State.OutputSize;
            frameBox.Width = W;
            frameBox.Height = H;
            var issue = State.Preview.Issue;
            issueOverlay.Visibility = c != null && issue != null ? Visibility.Visible : Visibility.Collapsed;
            if (c != null && issue != null)
                issueText.Text = $"ffmpeg không giải mã được “{c.FileName}”.\n{issue}";

            playButton.Content = UI.Icon(State.Preview.Playing && !State.PlayAll ? "" : "", 12);
            UpdateTime();
            if (c != null)
            {
                durText.Text = "/ " + Num.Time(c.Duration);
                if (c.SceneProgress.HasValue)
                {
                    sceneBar.Visibility = Visibility.Visible;
                    sceneBar.Value = c.SceneProgress.Value;
                    sceneText.Text = $"Đang phân tích cảnh {(int)(c.SceneProgress.Value * 100)}%";
                }
                else
                {
                    sceneBar.Visibility = Visibility.Collapsed;
                    sceneText.Text = c.ScenesAnalyzed ? $"{c.Cuts.Count + 1} cảnh" : "";
                }
                sceneControls.IsEnabled = c.Cuts.Count > 0 || c.SceneProgress.HasValue || c.ScenesAnalyzed;
                snapToggle.IsChecked = State.S.SnapToScenes;

                inBox.Max = c.Duration; inBox.Value = c.In; inBox.Refresh();
                outBox.Max = c.Duration; outBox.Value = c.Out; outBox.Refresh();
                lenBox.Max = c.Duration; lenBox.Value = c.Length; lenBox.Refresh();
                speedNote.Text = c.Edit.Speed != 1 ? $"→ {Num.Fmt(c.OutLength, 2)} s trong GIF" : "";
                zoomSlider.Value = State.Timeline.Level;
                zoomSlider.InvalidateVisual();
            }
            timeline.InvalidateVisual();

            double total = State.TotalDuration;
            bool ok = total >= 2.5 && total <= 6.5;
            stripInfo.Text = $"{State.Clips.Count} đoạn · {Num.Fmt(total, 1)} giây";
            stripInfo.Foreground = UI.Res(ok ? "Text2" : "Warn");
            stripWarn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            playAllButton.Content = State.PlayAll ? UI.IconLabel("", "Dừng") : UI.IconLabel("", "Phát toàn bộ");
            playAllButton.IsEnabled = State.Clips.Count >= 2;
            addSegButton.IsEnabled = c != null;

            RefreshInspector();
            RefreshExportPanel();
        }
    }
}
