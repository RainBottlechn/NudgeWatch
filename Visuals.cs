using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NudgeWatch
{
    /// <summary>列表行、树节点、页签标题等结构化视觉片段（替代过去拼接出的纯字符串）。</summary>
    internal static class Rows
    {
        public static TextBlock Text(string text, double size, bool bold, string brushKey)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (brushKey != null) t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        public static Border Pill(string text, string bgKey, string fgKey)
        {
            var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, bgKey ?? "SubCardBrush");
            var t = new TextBlock { Text = text, FontSize = 10.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, fgKey ?? "MutedBrush");
            b.Child = t;
            return b;
        }

        public static Button PillButton(string text, string bgKey, string fgKey, string toolTip, Action<Button> onClick)
        {
            var b = new Button { Content = text, ToolTip = toolTip };
            b.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
            if (bgKey != null) b.SetResourceReference(Control.BackgroundProperty, bgKey);
            if (fgKey != null) b.SetResourceReference(Control.ForegroundProperty, fgKey);
            b.Click += (s, e) => { e.Handled = true; if (onClick != null) onClick(b); };
            return b;
        }

        public static Border Badge(int count, string bgKey, string fgKey)
        {
            var b = new Border { CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, bgKey);
            var t = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            b.Child = t;
            return b;
        }

        /// <summary>分组节点：组名（半粗）+ 右侧计数 / 未读角标。</summary>
        public static FrameworkElement Branch(string name, string meta, int unread, string badgeKey, string badgeInkKey)
        {
            var row = new DockPanel { LastChildFill = true };
            if (unread > 0) { var badge = Badge(unread, badgeKey, badgeInkKey); DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge); }
            if (!String.IsNullOrEmpty(meta)) { var m = Text(meta, 11, false, "MutedBrush"); m.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(m, Dock.Right); row.Children.Add(m); }
            row.Children.Add(Text(name, 12.5, true, null));
            return row;
        }

        /// <summary>叶子节点：标题 + 次要说明（灰），右侧放胶囊或角标。</summary>
        public static FrameworkElement Leaf(string title, string subtitle, bool bold, IEnumerable<UIElement> right)
        {
            var row = new DockPanel { LastChildFill = true };
            if (right != null)
                foreach (var r in right.Reverse()) { DockPanel.SetDock(r, Dock.Right); row.Children.Add(r); }
            var stack = new StackPanel();
            stack.Children.Add(Text(title, 12.5, bold, null));
            if (!String.IsNullOrEmpty(subtitle))
            {
                var sub = Text(subtitle, 10.5, false, null);
                sub.Opacity = 0.72;
                sub.Margin = new Thickness(0, 1, 0, 0);
                stack.Children.Add(sub);
            }
            row.Children.Add(stack);
            return row;
        }

        /// <summary>树节点无障碍名称：保留旧版完整文字，供读屏与验收脚本读取。</summary>
        public static void Name(TreeViewItem item, string text) { AutomationProperties.SetName(item, text ?? ""); }

        public static string NameOf(TreeViewItem item)
        {
            var name = AutomationProperties.GetName(item);
            if (!String.IsNullOrEmpty(name)) return name;
            return item.Header as string ?? "";
        }

        /// <summary>页签标题：标题 + 计数（可选红点）。</summary>
        public static object TabHeader(string title, int count, string brushKey, bool dot)
        {
            var t = new TextBlock();
            t.Inlines.Add(new Run(title));
            if (count > 0)
            {
                var r = new Run(" " + (dot ? "●" : "") + (count > 99 ? "99+" : count.ToString())) { FontWeight = FontWeights.SemiBold };
                r.SetResourceReference(TextElement.ForegroundProperty, brushKey);
                t.Inlines.Add(r);
            }
            return t;
        }
    }
}
