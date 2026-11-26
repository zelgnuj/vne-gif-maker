using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VneGif.App
{
    static class UI
    {
        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        public static TextBlock Icon(string glyph, double size = 13) => new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };

        public static TextBlock Text(string s, double size = 12.5, string color = "Text", FontWeight? weight = null) => new TextBlock
        {
            Text = s, FontSize = size, Foreground = Res(color), FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        public static TextBlock Caption(string s) => new TextBlock
        {
            Text = s, FontSize = 11.5, Foreground = Res("Text2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(98, 0, 0, 0),
        };

        public static TextBlock RowLabel(string s) => new TextBlock
        {
            Text = s, Width = 88, FontSize = 12.5, Foreground = Res("Text2"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        /// <summary>Icon + label content for buttons.</summary>
        public static object IconLabel(string glyph, string label, double gap = 6)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(Icon(glyph, 12));
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return p;
        }

        public static Button Button(object content, Action click, string tip = null, string style = null)
        {
            var b = new Button { Content = content };
            if (style != null) b.Style = (Style)Application.Current.Resources[style];
            if (tip != null) b.ToolTip = tip;
            b.Click += (s, e) => click();
            return b;
        }

        public static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        public static FrameworkElement Gap(double w) => new Border { Width = w };
    }

    /// <summary>Thin slider; bipolar ones fill from their origin. Alt = fine.</summary>
    public sealed class FineSlider : FrameworkElement
    {
        public double Min, Max = 1, Value;
        public double? Origin;
        public event Action<double> ValueChanged;
        double dragStartX, dragStartV;

        public FineSlider() { Height = 20; MinWidth = 40; Cursor = Cursors.Hand; }

        double Frac(double v) => Max > Min ? (Math.Min(Math.Max(v, Min), Max) - Min) / (Max - Min) : 0;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, r = 6, y = h / 2;
            double x0 = r, x1 = w - r, span = Math.Max(1, x1 - x0);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            dc.DrawRoundedRectangle(UI.Res("Raised"), null, new Rect(x0, y - 2, span, 4), 2, 2);
            double vx = x0 + Frac(Value) * span;
            double ox = x0 + Frac(Origin ?? Min) * span;
            var fill = IsEnabled ? Brand.BrightBrush : UI.Res("Text3");
            dc.DrawRoundedRectangle(fill, null, new Rect(Math.Min(ox, vx), y - 2, Math.Abs(vx - ox), 4), 2, 2);
            if (Origin.HasValue) dc.DrawRectangle(UI.Res("Text3"), null, new Rect(ox - 0.5, y - 5, 1, 10));
            dc.DrawEllipse(IsEnabled ? Brushes.White : UI.Res("Text3"), new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), new Point(vx, y), r, r);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsEnabledProperty) InvalidateVisual();
        }

        void SetFromX(double x)
        {
            double r = 6, span = Math.Max(1, ActualWidth - 2 * r);
            double v = Min + Math.Min(Math.Max((x - r) / span, 0), 1) * (Max - Min);
            Set(v);
        }

        void Set(double v)
        {
            v = Math.Min(Math.Max(v, Min), Max);
            if (Origin.HasValue && Math.Abs(v - Origin.Value) < (Max - Min) * 0.008) v = Origin.Value;   // detent at the origin
            Value = v;
            InvalidateVisual();
            ValueChanged?.Invoke(v);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var x = e.GetPosition(this).X;
            dragStartX = x;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) SetFromX(x);
            dragStartV = Value;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!IsMouseCaptured) return;
            var x = e.GetPosition(this).X;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                double span = Math.Max(1, ActualWidth - 12);
                Set(dragStartV + (x - dragStartX) / span * (Max - Min) * 0.1);
            }
            else SetFromX(x);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
    }

    /// <summary>
    /// Number field in the style of Adobe "hot text": drag horizontally to change (Alt = fine,
    /// Shift = coarse), click to type; Enter commits, Esc cancels, ↑/↓ and the wheel step.
    /// </summary>
    public sealed class NumBox : TextBox
    {
        public double Value, Min, Max = 1, Step = 0.01, Scale = 1;
        public int Decimals;
        public string Unit = "";
        public bool FixedDecimals, Timecode;
        public event Action<double> Committed;
        bool highlighted;
        Point down;
        double downValue;
        bool dragging, pressed;

        public NumBox()
        {
            Style = (Style)Application.Current.FindResource(typeof(TextBox));
            Height = 22;
            TextAlignment = TextAlignment.Right;
            FontFamily = new FontFamily("Segoe UI");
            IsReadOnly = true;
            Cursor = Cursors.SizeWE;
            ToolTip = "Kéo ngang để chỉnh · giữ Alt để chỉnh tinh, Shift để chỉnh nhanh · nhấp để nhập số";
            ToolTipService.SetInitialShowDelay(this, 1200);
        }

        public bool Highlighted
        {
            get => highlighted;
            set { highlighted = value; Foreground = value ? Brand.BrightBrush : UI.Res("Text"); }
        }

        string Format(double v) => Timecode ? Num.Time(v) : Num.Fmt(v * Scale, Decimals, FixedDecimals) + Unit;
        double? ParseText(string s)
        {
            if (Timecode) return Num.ParseTime(s);
            var v = Num.Parse(s);
            return v.HasValue ? v.Value / Scale : (double?)null;
        }

        public void Refresh() { if (!IsKeyboardFocused) Text = Format(Value); }

        void Commit(double v)
        {
            v = Math.Min(Math.Max(v, Min), Max);
            Value = v;
            Committed?.Invoke(v);
            Refresh();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (IsKeyboardFocused) { base.OnPreviewMouseLeftButtonDown(e); return; }
            down = e.GetPosition(this);
            downValue = Value;
            pressed = true;
            dragging = false;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            if (!pressed) { base.OnPreviewMouseMove(e); return; }
            double dx = e.GetPosition(this).X - down.X;
            if (!dragging && Math.Abs(dx) < 3) return;
            dragging = true;
            var m = Keyboard.Modifiers;
            double k = (m & ModifierKeys.Alt) != 0 ? 0.1 : (m & ModifierKeys.Shift) != 0 ? 10 : 1;
            Commit(downValue + dx * Step * k);
            e.Handled = true;
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!pressed) { base.OnPreviewMouseLeftButtonUp(e); return; }
            pressed = false;
            ReleaseMouseCapture();
            if (!dragging)
            {
                IsReadOnly = false;
                Cursor = Cursors.IBeam;
                Focus();
                Text = Timecode ? Num.Time(Value) : Num.Fmt(Value * Scale, Decimals, FixedDecimals);
                SelectAll();
            }
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var v = ParseText(Text);
                EndEdit();
                if (v.HasValue) Commit(v.Value);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                var cur = IsReadOnly ? Value : ParseText(Text) ?? Value;
                double k = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                Commit(cur + (e.Key == Key.Up ? Step : -Step) * k);
                if (!IsReadOnly) { Text = Timecode ? Num.Time(Value) : Num.Fmt(Value * Scale, Decimals, FixedDecimals); SelectAll(); }
                e.Handled = true;
            }
            else base.OnPreviewKeyDown(e);
        }

        /// <summary>Wheel steps the value only while typing; otherwise it scrolls the panel
        /// (a TextBox's inner ScrollViewer would swallow it).</summary>
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!IsReadOnly) { Commit(Value + Math.Sign(e.Delta) * Step); Text = Timecode ? Num.Time(Value) : Num.Fmt(Value * Scale, Decimals, FixedDecimals); return; }
            if (VisualTreeHelper.GetParent(this) is UIElement parent)
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (!IsReadOnly)
            {
                var v = ParseText(Text);
                IsReadOnly = true;
                Cursor = Cursors.SizeWE;
                if (v.HasValue) Commit(v.Value); else Refresh();
            }
        }

        void EndEdit()
        {
            IsReadOnly = true;
            Cursor = Cursors.SizeWE;
            Keyboard.ClearFocus();
            Refresh();
        }
    }

    /// <summary>Label (double-click resets) · slider · number field.</summary>
    public sealed class ParamRow : Grid
    {
        readonly TextBlock label;
        public readonly FineSlider Slider;
        public readonly NumBox Box;
        readonly Func<double> get;
        readonly Action<double> set;
        readonly double reset;

        public ParamRow(string text, Func<double> get, Action<double> set, double min, double max, double reset,
                        string unit = "", double scale = 1, int decimals = 0, double step = 0.01, bool bipolar = false)
        {
            this.get = get; this.set = set; this.reset = reset;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            Margin = new Thickness(0, 2, 0, 2);
            label = UI.RowLabel(text);
            label.Background = Brushes.Transparent;
            label.ToolTip = "Nhấp đúp để đặt lại";
            label.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { set(reset); Refresh(); } };
            Slider = new FineSlider { Min = min, Max = max, Origin = bipolar ? reset : (double?)null, Margin = new Thickness(0, 0, 8, 0) };
            Slider.ValueChanged += v => { set(v); Box.Value = v; Box.Highlighted = Math.Abs(v - reset) > 1e-9; Box.Refresh(); UpdateLabel(v); };
            Box = new NumBox { Min = min, Max = max, Step = step, Scale = scale, Decimals = decimals, Unit = unit };
            Box.Committed += v => { set(v); Slider.Value = v; Slider.InvalidateVisual(); UpdateLabel(v); };
            SetColumn(Slider, 1);
            SetColumn(Box, 2);
            Children.Add(label);
            Children.Add(Slider);
            Children.Add(Box);
        }

        void UpdateLabel(double v)
        {
            bool modified = Math.Abs(v - reset) > 1e-9;
            label.Foreground = UI.Res(modified ? "Text" : "Text2");
            Box.Highlighted = modified;
        }

        public void Refresh()
        {
            double v = get();
            Slider.Value = v;
            Slider.InvalidateVisual();
            Box.Value = v;
            Box.Refresh();
            UpdateLabel(v);
        }
    }

    /// <summary>Segmented control (RadioButtons in a rounded well).</summary>
    public sealed class Segmented : Border
    {
        readonly List<RadioButton> buttons = new List<RadioButton>();
        public event Action<int> Selected;
        bool updating;
        static int group;

        public Segmented(IEnumerable<string> labels)
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
            CornerRadius = new CornerRadius(6);
            var grid = new UniformGrid { Rows = 1 };
            var name = "seg" + group++;
            int i = 0;
            foreach (var l in labels)
            {
                int index = i++;
                var b = new RadioButton { Content = l, GroupName = name, Style = (Style)Application.Current.Resources["Segment"] };
                b.Checked += (s, e) => { if (!updating) Selected?.Invoke(index); };
                buttons.Add(b);
                grid.Children.Add(b);
            }
            Child = grid;
        }

        public int Index
        {
            set
            {
                updating = true;
                for (int i = 0; i < buttons.Count; i++) buttons[i].IsChecked = i == value;
                updating = false;
            }
        }
    }

    /// <summary>Collapsible inspector section (state remembered per title).</summary>
    public sealed class Section : StackPanel
    {
        static readonly HashSet<string> Collapsed = new HashSet<string>();
        public readonly StackPanel Body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        public Section(string title, UIElement accessory = null)
        {
            Margin = new Thickness(14, 12, 14, 12);
            var chevron = UI.Icon("", 9);
            chevron.Foreground = UI.Res("Text2");
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var t = UI.Text(title, 12.5, "Text", FontWeights.SemiBold);
            t.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(chevron);
            head.Children.Add(t);
            if (accessory != null)
            {
                DockPanel.SetDock(accessory, Dock.Right);
                head.Children.Insert(0, accessory);
                if (accessory is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right;
            }
            DockPanel.SetDock(chevron, Dock.Left);
            DockPanel.SetDock(t, Dock.Left);
            void Apply()
            {
                bool open = !Collapsed.Contains(title);
                Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
            }
            head.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && accessory != null && IsInside(d, accessory)) return;
                if (!Collapsed.Remove(title)) Collapsed.Add(title);
                Apply();
            };
            Apply();
            Children.Add(head);
            Children.Add(Body);
        }

        static bool IsInside(DependencyObject d, UIElement root)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d == root) return true;
            return false;
        }

        public Section Add(UIElement e) { Body.Children.Add(e); return this; }
    }
}
