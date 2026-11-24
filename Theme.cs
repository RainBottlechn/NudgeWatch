using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace NudgeWatch
{
    public sealed class Palette
    {
        public string Key, Name, Bg, Card, SubCard, Border, Ink, Muted, Accent, AccentHover, AccentText;
        public string Ball, Quiet, L2, L3, Warning, L4Bg, L4SubBg, L4Ink, Selection, SelectionInk;
        // 0.8.0 新增语义令牌：L4 强调色（与 L4Bg 对比 ≥ 4.5，文字用 L4Bg）、色块上的文字色、成功态、失联色
        public string L4Accent, OnL2, OnL3, Success, Lost = "#B22222";
        public string OilStripeA, OilStripeB, ArtworkResource, ArtworkCaption;
        public bool IsOil, IsDark;
        public string ArtworkThumb { get { return String.IsNullOrEmpty(ArtworkResource) ? "" : ArtworkResource.Replace(".jpg", "-thumb.jpg"); } }
    }

    public static class ThemeManager
    {
        public static readonly Palette[] Presets = new[]
        {
            new Palette {
                Key = "day", Name = "白天经典", IsOil = false, IsDark = false,
                Bg = "#F3F0E8", Card = "#FFFFFF", SubCard = "#EAE5DA", Border = "#D4CEC0",
                Ink = "#1A221E", Muted = "#4D5750", Accent = "#245343", AccentHover = "#1B4033", AccentText = "#FFFFFF",
                Ball = "#245343", Quiet = "#5E4687", L2 = "#266A65", L3 = "#B53826", Warning = "#8A4E08",
                L4Bg = "#17201C", L4SubBg = "#222E29", L4Ink = "#FBF8F0", L4Accent = "#F06A50",
                OnL2 = "#FFFFFF", OnL3 = "#FFFFFF", Success = "#2F7D52",
                Selection = "#D1E4D8", SelectionInk = "#12261D",
                OilStripeA = "#245343", OilStripeB = "#4A7D68",
                ArtworkResource = "", ArtworkCaption = ""
            },
            new Palette {
                Key = "night", Name = "夜晚经典", IsOil = false, IsDark = true,
                Bg = "#12161B", Card = "#1B2129", SubCard = "#232B35", Border = "#384452",
                Ink = "#F2F0EA", Muted = "#AEB9C6", Accent = "#2A7D68", AccentHover = "#226856", AccentText = "#FFFFFF",
                Ball = "#276B5A", Quiet = "#7D62B8", L2 = "#4BB5AA", L3 = "#F27966", Warning = "#F4C152",
                L4Bg = "#0B0E12", L4SubBg = "#161C24", L4Ink = "#F8F5EE", L4Accent = "#F27966",
                OnL2 = "#12161B", OnL3 = "#12161B", Success = "#4CC38A",
                Selection = "#29453E", SelectionInk = "#F4FBF8",
                OilStripeA = "#3E9B84", OilStripeB = "#6EC2AA",
                ArtworkResource = "", ArtworkCaption = ""
            },
            new Palette {
                Key = "oil-monet-lilies", Name = "油画 · 莫奈睡莲", IsOil = true, IsDark = false,
                Bg = "#DCE8EC", Card = "#F6FAFA", SubCard = "#E5F0F2", Border = "#B3CBD2",
                Ink = "#13252C", Muted = "#3B555E", Accent = "#2A5F75", AccentHover = "#204B5E", AccentText = "#FFFFFF",
                Ball = "#2A5F75", Quiet = "#644E82", L2 = "#2A6D69", L3 = "#AE3A42", Warning = "#854D0A",
                L4Bg = "#15232B", L4SubBg = "#20333E", L4Ink = "#F2F8F9", L4Accent = "#F2806F",
                OnL2 = "#FFFFFF", OnL3 = "#FFFFFF", Success = "#3A8459",
                Selection = "#CBE0E8", SelectionInk = "#10252E",
                OilStripeA = "#2A5F75", OilStripeB = "#5A8B84",
                ArtworkResource = "oil-monet-lilies.jpg", ArtworkCaption = "Monet · 睡莲 (1906)"
            },
            new Palette {
                Key = "oil-vangogh-starry", Name = "油画 · 梵高星夜", IsOil = true, IsDark = true,
                Bg = "#0E172A", Card = "#16233E", SubCard = "#1F3052", Border = "#375080",
                Ink = "#F7F1DF", Muted = "#B8C8E2", Accent = "#E3B63D", AccentHover = "#F0C752", AccentText = "#101828",
                // Warning 偏橙、L3 偏红，与金色 Accent 拉开色相
                Ball = "#23437B", Quiet = "#7B63B8", L2 = "#5CB4D6", L3 = "#F2766B", Warning = "#F2A25A",
                L4Bg = "#0A101E", L4SubBg = "#141F36", L4Ink = "#FFF7E2", L4Accent = "#F2766B",
                OnL2 = "#0E172A", OnL3 = "#0E172A", Success = "#6CCB9A",
                Selection = "#2C4475", SelectionInk = "#FAF6E8",
                OilStripeA = "#3367A8", OilStripeB = "#E3B63D",
                ArtworkResource = "oil-vangogh-starry.jpg", ArtworkCaption = "Van Gogh · 星夜 (1889)"
            },
            new Palette {
                Key = "oil-vermeer-pearl", Name = "油画 · 维米尔珍珠", IsOil = true, IsDark = false,
                Bg = "#EAE3D4", Card = "#FAF7F0", SubCard = "#EDE6D6", Border = "#C9BDA4",
                Ink = "#211A12", Muted = "#544838", Accent = "#1F4675", AccentHover = "#17365C", AccentText = "#FFFFFF",
                Ball = "#1F4675", Quiet = "#634B7B", L2 = "#296370", L3 = "#A5392A", Warning = "#834E0C",
                L4Bg = "#1A202B", L4SubBg = "#263040", L4Ink = "#FCF8ED", L4Accent = "#EE7E63",
                OnL2 = "#FFFFFF", OnL3 = "#FFFFFF", Success = "#3B7B53",
                Selection = "#D2DFEE", SelectionInk = "#142742",
                OilStripeA = "#1F4675", OilStripeB = "#B88A3B",
                ArtworkResource = "oil-vermeer-pearl.jpg", ArtworkCaption = "Vermeer · 戴珍珠耳环的少女 (1665)"
            },
            new Palette {
                Key = "oil-sunrise-wheat", Name = "油画 · 日出印象", IsOil = true, IsDark = false,
                Bg = "#E3EAE9", Card = "#FAF7F2", SubCard = "#E9EFEF", Border = "#BDCCCB",
                // Accent 取画中灰蓝，橙红只留给悬浮球（太阳）与 L3
                Ink = "#1C262C", Muted = "#465760", Accent = "#3F5F7A", AccentHover = "#334E65", AccentText = "#FFFFFF",
                Ball = "#BD431F", Quiet = "#694E78", L2 = "#2F6B68", L3 = "#B5381C", Warning = "#864A0D",
                L4Bg = "#21262B", L4SubBg = "#2D343B", L4Ink = "#FFF5EA", L4Accent = "#F07A55",
                OnL2 = "#FFFFFF", OnL3 = "#FFFFFF", Success = "#3A7D5B",
                Selection = "#F0D8CE", SelectionInk = "#3B190E",
                OilStripeA = "#4A5C6E", OilStripeB = "#E65F33",
                ArtworkResource = "oil-sunrise-wheat.jpg", ArtworkCaption = "Monet · 日出印象 (1872)"
            }
        };

        public static readonly string[] Keys = { "system", "day", "night", "oil-monet-lilies", "oil-vangogh-starry", "oil-vermeer-pearl", "oil-sunrise-wheat" };
        static readonly Dictionary<string, BitmapImage> imageCache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        static bool stylesLoaded, chromeHooked;
        static Palette current = Presets[0];

        public static Palette Current { get { return current; } }

        public static string DisplayName(string key)
        {
            if (key == "system") return "跟随系统";
            var p = Array.Find(Presets, x => x.Key == key);
            return p == null ? key : p.Name;
        }

        public static Palette Resolve(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "system")
            {
                bool dark = false;
                try { dark = Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)) == 0; }
                catch { }
                return Presets[dark ? 1 : 0];
            }
            return Array.Find(Presets, x => x.Key == key) ?? Presets[0];
        }

        public static Color ParseColor(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static SolidColorBrush BrushOf(string hex) { var b = new SolidColorBrush(ParseColor(hex)); b.Freeze(); return b; }
        static SolidColorBrush BrushOf(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        static double LinearChannel(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        public static double ContrastRatio(string hexA, string hexB)
        {
            Color a = ParseColor(hexA), b = ParseColor(hexB);
            double la = 0.2126 * LinearChannel(a.R) + 0.7152 * LinearChannel(a.G) + 0.0722 * LinearChannel(a.B);
            double lb = 0.2126 * LinearChannel(b.R) + 0.7152 * LinearChannel(b.G) + 0.0722 * LinearChannel(b.B);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        /// <summary>两种颜色的色相差（0–180°），用于校验主操作色与危险色不撞色。</summary>
        public static double HueDistance(string hexA, string hexB)
        {
            double d = Math.Abs(Hue(ParseColor(hexA)) - Hue(ParseColor(hexB))) % 360;
            return d > 180 ? 360 - d : d;
        }

        static double Hue(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d < 1e-6) return 0;
            double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }

        public static BitmapImage LoadEmbeddedImage(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return null;
            BitmapImage cached;
            if (imageCache.TryGetValue(resourceName, out cached)) return cached;
            try
            {
                using (var s = typeof(ThemeManager).Assembly.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return null;
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    imageCache[resourceName] = img;
                    return img;
                }
            }
            catch { return null; }
        }

        static Brush BuildOilBannerBrush(Palette p, Brush fallback)
        {
            if (!p.IsOil || string.IsNullOrEmpty(p.ArtworkResource)) return fallback;
            var img = LoadEmbeddedImage(p.ArtworkResource);
            if (img == null) return fallback;
            var brush = new ImageBrush(img)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            return brush;
        }

        static Brush BuildCanvasOverlay(Palette p)
        {
            if (!p.IsOil) return Brushes.Transparent;
            var grain = LoadEmbeddedImage("canvas-grain.png");
            if (grain == null) return Brushes.Transparent;
            double size = grain.PixelWidth > 0 ? grain.PixelWidth : 96;
            var tile = new ImageBrush(grain)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, size, size),
                ViewportUnits = BrushMappingMode.Absolute,
                Opacity = p.IsDark ? 0.5 : 0.75
            };
            tile.Freeze();
            return tile;
        }

        public static void EnsureResources()
        {
            if (Application.Current == null) return;
            if (!stylesLoaded)
            {
                stylesLoaded = true;
                try
                {
                    using (var s = typeof(ThemeManager).Assembly.GetManifestResourceStream("Styles.xaml"))
                    {
                        if (s != null)
                        {
                            var rd = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(s);
                            Application.Current.Resources.MergedDictionaries.Add(rd);
                        }
                    }
                }
                catch { }
                HookChrome();
                Apply("system");
            }
        }

        public static void Apply(string key)
        {
            current = Resolve(key);
            if (Application.Current == null) return;
            if (!stylesLoaded) EnsureResources();
            var res = Application.Current.Resources;
            res["BgBrush"] = BrushOf(current.Bg);
            res["CardBrush"] = BrushOf(current.Card);
            res["SubCardBrush"] = BrushOf(current.SubCard);
            res["BorderBrush"] = BrushOf(current.Border);
            res["InkBrush"] = BrushOf(current.Ink);
            res["MutedBrush"] = BrushOf(current.Muted);
            res["AccentBrush"] = BrushOf(current.Accent);
            res["AccentHoverBrush"] = BrushOf(current.AccentHover);
            res["AccentTextBrush"] = BrushOf(current.AccentText);
            res["BallBrush"] = BrushOf(current.Ball);
            res["QuietBrush"] = BrushOf(current.Quiet);
            res["L2Brush"] = BrushOf(current.L2);
            res["L3Brush"] = BrushOf(current.L3);
            res["WarningBrush"] = BrushOf(current.Warning);
            res["L4BgBrush"] = BrushOf(current.L4Bg);
            res["L4SubBgBrush"] = BrushOf(current.L4SubBg);
            res["L4InkBrush"] = BrushOf(current.L4Ink);
            res["L4AccentBrush"] = BrushOf(current.L4Accent);
            res["OnL2Brush"] = BrushOf(current.OnL2);
            res["OnL3Brush"] = BrushOf(current.OnL3);
            res["SuccessBrush"] = BrushOf(current.Success);
            res["LostBrush"] = BrushOf(current.Lost);
            res["SelectionBrush"] = BrushOf(current.Selection);
            res["SelectionInkBrush"] = BrushOf(current.SelectionInk);
            var accent = ParseColor(current.Accent);
            res["AccentSoftBrush"] = BrushOf(Color.FromArgb(0x26, accent.R, accent.G, accent.B));
            var l3 = ParseColor(current.L3);
            res["L3SoftBrush"] = BrushOf(Color.FromArgb(0x22, l3.R, l3.G, l3.B));
            var warn = ParseColor(current.Warning);
            res["WarningSoftBrush"] = BrushOf(Color.FromArgb(0x24, warn.R, warn.G, warn.B));
            res[SystemColors.HighlightBrushKey] = res["SelectionBrush"];
            res[SystemColors.HighlightTextBrushKey] = res["SelectionInkBrush"];
            res[SystemColors.InactiveSelectionHighlightBrushKey] = res["SelectionBrush"];
            res[SystemColors.InactiveSelectionHighlightTextBrushKey] = res["SelectionInkBrush"];
            res[SystemColors.WindowBrushKey] = res["CardBrush"];
            res[SystemColors.WindowTextBrushKey] = res["InkBrush"];
            res[SystemColors.ControlTextBrushKey] = res["InkBrush"];

            var stripe = new LinearGradientBrush(ParseColor(current.OilStripeA), ParseColor(current.OilStripeB), 0);
            stripe.Freeze();
            res["OilStripeBrush"] = stripe;
            res["OilBannerBrush"] = BuildOilBannerBrush(current, stripe);
            res["OilBannerHeight"] = current.IsOil ? 46.0 : 4.0;
            res["OilDialogBannerHeight"] = current.IsOil ? 30.0 : 3.0;
            res["OilBannerCaption"] = current.IsOil ? current.ArtworkCaption : "";
            res["OilBannerCaptionVisibility"] = current.IsOil ? Visibility.Visible : Visibility.Collapsed;
            res["CanvasOverlayBrush"] = BuildCanvasOverlay(current);

            var scrim = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 10, 12, 16), 90);
            scrim.Freeze();
            res["OilBannerScrimBrush"] = scrim;
            // 横幅底部渐隐到窗口底色，让画面自然融入界面
            var bg = ParseColor(current.Bg);
            var fade = new LinearGradientBrush(Color.FromArgb(0, bg.R, bg.G, bg.B), bg, 90);
            fade.Freeze();
            res["BannerFadeBrush"] = fade;
            res["BannerFadeVisibility"] = current.IsOil ? Visibility.Visible : Visibility.Collapsed;
            // 悬浮球高光
            var gloss = new LinearGradientBrush(Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            gloss.Freeze();
            res["BallGlossBrush"] = gloss;
            // 徽标提醒圆点：金底主题（星夜）改用深朱红，避免橙点融入金底
            res["LogoDotBrush"] = BrushOf(HueDistance(current.Accent, "#F0A04B") < 35 ? "#C83E2B" : "#F0A04B");

            foreach (Window w in Application.Current.Windows) ApplyChrome(w);
        }

        public static Brush BallBackground(bool lost, bool quiet)
        {
            // 免打扰不再整球换色，保留品牌色并叠加月牙徽章；失联色走 Lost 令牌（默认 Firebrick）
            if (lost) return BrushOf(current.Lost);
            return BrushOf(current.Ball);
        }

        public static string LevelName(int level)
        {
            if (level >= 4) return "紧急";
            if (level == 3) return "重要";
            if (level == 2) return "普通";
            return "记录";
        }

        public static void StyleAlert(Window window, int level)
        {
            var card = window.FindName("CardBorder") as Border;
            var bar = window.FindName("LevelBar") as Border;
            var tag = window.FindName("LevelTag") as Border;
            var tagText = window.FindName("LevelText") as TextBlock;
            var head = window.FindName("Heading") as TextBlock;
            var body = window.FindName("Body") as TextBlock;
            var bodyCard = window.FindName("BodyCard") as Border;
            var art = window.FindName("ArtLine") as Border;
            if (card == null) return;
            bool l4 = level >= 4, l3 = level == 3;
            string severity = l4 ? current.L4Accent : l3 ? current.L3 : current.L2;
            card.Background = BrushOf(l4 ? current.L4Bg : current.Card);
            card.BorderBrush = BrushOf(l4 ? current.L4Accent : l3 ? current.L3 : current.Border);
            card.BorderThickness = new Thickness(l4 ? 2 : 1);
            if (bodyCard != null) bodyCard.Background = BrushOf(l4 ? current.L4SubBg : current.SubCard);
            // 严重度优先于装饰：色轨始终使用等级色；名画只作为 L2 顶部细线出现
            if (bar != null) bar.Background = BrushOf(severity);
            if (art != null)
            {
                bool showArt = current.IsOil && level < 3;
                art.Visibility = showArt ? Visibility.Visible : Visibility.Collapsed;
                if (showArt && Application.Current != null) art.Background = Application.Current.Resources["OilBannerBrush"] as Brush;
            }
            if (tag != null) tag.Background = BrushOf(severity);
            if (tagText != null)
            {
                tagText.Foreground = BrushOf(l4 ? current.L4Bg : l3 ? current.OnL3 : current.OnL2);
                tagText.Text = l4 ? "紧急" : LevelName(level);
            }
            if (head != null) head.Foreground = BrushOf(l4 ? current.L4Ink : current.Ink);
            if (body != null) body.Foreground = BrushOf(l4 ? current.L4Ink : current.Ink);
            foreach (var name in new[] { "Source", "Meta" })
            {
                var t = window.FindName(name) as TextBlock;
                if (t == null) continue;
                if (l4) t.Foreground = BrushOf(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)); else t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            }
            foreach (var name in new[] { "Jump", "Complete" })
            {
                var b = window.FindName(name) as Button;
                if (b != null) b.SetResourceReference(FrameworkElement.StyleProperty, l4 ? "DangerPrimaryButton" : "PrimaryButton");
            }
            foreach (var name in new[] { "Snooze", "QuietNow", "Ack", "More", "Convert" })
            {
                var b = window.FindName(name) as Button;
                if (b == null) continue;
                if (l4) b.SetResourceReference(FrameworkElement.StyleProperty, "GhostOnDarkButton"); else b.ClearValue(FrameworkElement.StyleProperty);
            }
            var shadow = card.Effect as DropShadowEffect;
            if (shadow != null)
            {
                if (shadow.IsFrozen) { shadow = shadow.Clone(); card.Effect = shadow; }
                shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                shadow.Color = l4 ? ParseColor(current.L4Accent) : l3 ? ParseColor(current.L3) : Colors.Black;
                shadow.ShadowDepth = l4 || l3 ? 0 : 4;
                shadow.BlurRadius = l4 ? 26 : l3 ? 20 : 18;
                shadow.Opacity = l4 ? 0.55 : l3 ? 0.30 : 0.20;
                if (l4 && Motion.Enabled)
                {
                    // L4 外发光呼吸：1.6 s 一次，最多 3 次，避免持续打扰
                    var pulse = new DoubleAnimation(0.55, 0.15, TimeSpan.FromMilliseconds(800))
                    { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                    shadow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
                }
            }
        }

        // ── 窗口标题栏：深色主题启用沉浸式深色模式，Win11 下同步标题栏底色 ──
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static void HookChrome()
        {
            if (chromeHooked) return;
            chromeHooked = true;
            try { EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => ApplyChrome(s as Window))); }
            catch { }
        }

        public static void ApplyChrome(Window w)
        {
            if (w == null || w.WindowStyle == WindowStyle.None) return;
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = current.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
                int caption = ColorRef(current.Bg), text = ColorRef(current.Ink), border = ColorRef(current.Border);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            }
            catch { }
        }

        static int ColorRef(string hex) { var c = ParseColor(hex); return c.R | (c.G << 8) | (c.B << 16); }
    }

    /// <summary>统一动效：时长、缓动，并遵循系统「减少动画」设置。</summary>
    public static class Motion
    {
        public static bool Enabled
        {
            get { try { return SystemParameters.ClientAreaAnimation; } catch { return true; } }
        }

        /// <summary>淡入并从 dy 方向轻微位移进入。</summary>
        public static void Reveal(FrameworkElement element, double dy, int ms)
        {
            if (element == null) return;
            if (!Enabled) { element.Opacity = 1; element.RenderTransform = Transform.Identity; return; }
            var shift = new TranslateTransform(0, dy);
            element.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        /// <summary>以中心缩放 from→1 进入（L3/L4 居中弹窗）。</summary>
        public static void Pop(FrameworkElement element, double from, int ms)
        {
            if (element == null) return;
            if (!Enabled) { element.Opacity = 1; element.RenderTransform = Transform.Identity; return; }
            var scale = new ScaleTransform(from, from);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        /// <summary>数字变化时的轻弹（悬浮球未读数）。</summary>
        public static void Bump(FrameworkElement element)
        {
            if (element == null || !Enabled) return;
            var scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            var anim = new DoubleAnimation(1, 1.15, TimeSpan.FromMilliseconds(100)) { AutoReverse = true, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
