using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NudgeWatch
{
    /// <summary>品牌形象：徽标为「圆角方块 + 小写 n + 右上角提醒圆点」，托盘、悬浮球、面板头部、EXE 图标共用同一套几何。</summary>
    public static class BrandArt
    {
        public enum TrayState { Normal, Unread, Quiet, Lost }

        static readonly Color Brand = Color.FromRgb(0x24, 0x53, 0x43);
        static readonly Color Cream = Color.FromRgb(0xF7, 0xF2, 0xE6);
        static readonly Color Dot = Color.FromRgb(0xF0, 0xA0, 0x4B);
        static readonly Color Alert = Color.FromRgb(0xE5, 0x48, 0x4D);
        static readonly Color Quiet = Color.FromRgb(0x6E, 0x58, 0x9C);
        static readonly Color LostGray = Color.FromRgb(0x6B, 0x72, 0x78);
        static readonly Geometry N = Geometry.Parse("M 7.5 18 L 7.5 10.6 C 7.5 8.4 9.2 7 11.3 7 C 13.4 7 15 8.4 15 10.6 L 15 18");
        static readonly Geometry Moon = Geometry.Parse("M 6 0.5 A 5.5 5.5 0 1 0 11.5 6 A 4.2 4.2 0 0 1 6 0.5 Z");

        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>在 24×24 单位坐标系中绘制徽标及状态徽章。</summary>
        public static void Draw(DrawingContext dc, TrayState state)
        {
            bool lost = state == TrayState.Lost;
            dc.DrawRoundedRectangle(B(lost ? LostGray : Brand), null, new Rect(0.5, 0.5, 23, 23), 6, 6);
            var pen = new Pen(B(Cream), 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, N);
            switch (state)
            {
                case TrayState.Normal:
                    dc.DrawEllipse(B(Dot), null, new Point(18.6, 6.2), 2.4, 2.4);
                    break;
                case TrayState.Unread:
                    dc.DrawEllipse(B(Alert), new Pen(B(Cream), 1.4), new Point(18.2, 6), 4.6, 4.6);
                    break;
                case TrayState.Quiet:
                    dc.DrawEllipse(B(Quiet), new Pen(B(Cream), 1.2), new Point(17.6, 17.6), 6, 6);
                    dc.PushTransform(new TranslateTransform(14.1, 14.1));
                    dc.PushTransform(new ScaleTransform(0.60, 0.60));
                    dc.DrawGeometry(B(Cream), null, Moon);
                    dc.Pop(); dc.Pop();
                    break;
                case TrayState.Lost:
                    dc.DrawEllipse(B(Alert), new Pen(B(Cream), 1.2), new Point(17.6, 17.6), 6, 6);
                    var bang = new Pen(B(Cream), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(bang, new Point(17.6, 14.4), new Point(17.6, 18));
                    dc.DrawEllipse(B(Cream), null, new Point(17.6, 20.6), 1.1, 1.1);
                    break;
            }
        }

        public static BitmapSource Render(int px, TrayState state)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(px / 24.0, px / 24.0));
                Draw(dc, state);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        /// <summary>按系统小图标尺寸实时生成托盘图标（随 DPI 变化），失败时返回 null 由调用方回退。</summary>
        public static System.Drawing.Icon TrayIcon(TrayState state)
        {
            try
            {
                int px = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Render(px, state)));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    ms.Position = 0;
                    using (var bmp = new System.Drawing.Bitmap(ms))
                    {
                        IntPtr h = bmp.GetHicon();
                        try { using (var tmp = System.Drawing.Icon.FromHandle(h)) return (System.Drawing.Icon)tmp.Clone(); }
                        finally { DestroyIcon(h); }
                    }
                }
            }
            catch { return null; }
        }
    }

    /// <summary>主题化确认框，替代系统 MessageBox（深色与油画主题下不再弹出白色系统框）。</summary>
    public static class ThemedDialog
    {
        public static bool Confirm(Window owner, string title, string message, string okText, bool danger)
        {
            return Show(owner, title, message, okText, "取消", danger);
        }

        public static void Info(Window owner, string title, string message)
        {
            Show(owner, title, message, "知道了", null, false);
        }

        static bool Show(Window owner, string title, string message, string okText, string cancelText, bool danger)
        {
            var dlg = new Window
            {
                Title = title, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };
            if (owner != null && owner.IsVisible) dlg.Owner = owner;
            dlg.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            dlg.SetResourceReference(Window.ForegroundProperty, "InkBrush");
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
            head.SetResourceReference(TextBlock.ForegroundProperty, danger ? "L3Brush" : "InkBrush");
            root.Children.Add(head);
            var body = new TextBlock { Text = message, FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            body.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            root.Children.Add(body);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            if (cancelText != null)
            {
                var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
                cancel.Click += delegate { dlg.DialogResult = false; };
                buttons.Children.Add(cancel);
            }
            var ok = new Button { Content = okText, IsDefault = true, MinWidth = 72 };
            ok.SetResourceReference(FrameworkElement.StyleProperty, danger ? "DangerSolidButton" : "PrimaryButton");
            ok.Click += delegate { dlg.DialogResult = true; };
            if (cancelText == null) ok.IsCancel = true;
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            dlg.Content = root;
            dlg.Loaded += delegate { ok.Focus(); };
            return dlg.ShowDialog() == true;
        }
    }
}
