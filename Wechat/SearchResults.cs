using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace WxWatch
{
    public static class SearchResults
    {
        const string Popover = "mmui::SearchContentPopover";
        const string SearchField = "mmui::XSearchField";
        public const string NeedsConfirmation = "存在重名或未精确匹配，请在微信搜索结果中确认";

        public static bool IsExact(string target, string section, string controlClass, string id, string accessibleName)
        {
            if (String.IsNullOrEmpty(target) || String.IsNullOrEmpty(accessibleName)) return false;
            var title = accessibleName.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (title != target) return false;
            if (target == "文件传输助手" && section == "功能")
                return controlClass == "mmui::XTableCell" && id == "search_item_function_" + target;
            return (section == "联系人" || section == "群聊" || section == "最佳匹配" || section == "最近使用") &&
                controlClass == "mmui::SearchContentCellView" && id == "search_item_" + target;
        }

        internal static bool Focused(IntPtr hwnd)
        {
            if (Native.GetForegroundWindow() != hwnd) return false;
            var node = AutomationElement.FocusedElement;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            for (int depth = 0; node != null && depth < 6 && node.Current.ProcessId == pid; depth++, node = TreeWalker.RawViewWalker.GetParent(node))
                if (node.Current.ClassName == SearchField) return true;
            return false;
        }

        static List<AutomationElement> Matches(IntPtr hwnd, string chat, out string sample)
        {
            var results = new List<AutomationElement>();
            sample = "";
            var root = AutomationElement.FromHandle(hwnd);
            var overlay = root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, Popover));
            if (overlay == null || overlay.Current.IsOffscreen) return results;
            var table = overlay.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "search_list"));
            if (table == null || table.Current.ClassName != "mmui::XTableView") return results;
            var section = "";
            var signature = new List<string>();
            foreach (AutomationElement row in table.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                var current = row.Current;
                string name = current.Name, id = current.AutomationId, cls = current.ClassName;
                if (current.IsOffscreen) continue;
                if (cls == "mmui::XTableCell" && String.IsNullOrEmpty(id))
                    section = name;
                signature.Add(cls + "\t" + id + "\t" + name);
                if (IsExact(chat, section, cls, id, name)) results.Add(row);
            }
            sample = String.Join("\n", signature);
            return results;
        }

        internal static bool OpenExact(IntPtr hwnd, string chat, int initialDelay)
        {
            Thread.Sleep(Math.Max(450, initialDelay));
            string prior = null;
            int stable = 0;
            for (int i = 0; i < 10; i++)
            {
                if (!Focused(hwnd)) return false;
                string signature;
                var matches = Matches(hwnd, chat, out signature);
                stable = signature.Length > 0 && signature == prior ? stable + 1 : 0;
                prior = signature;
                if (stable >= 2 && matches.Count == 1)
                {
                    var result = matches[0];
                    var rect = result.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) return false;
                    int x = (int)(rect.Left + rect.Width / 2), y = (int)(rect.Top + rect.Height / 2);
                    var element = AutomationElement.FromPoint(new Point(x, y));
                    for (int depth = 0; element != null && depth < 8; depth++, element = TreeWalker.RawViewWalker.GetParent(element))
                    {
                        if (element != result) continue;
                        string refreshed;
                        var verified = Matches(hwnd, chat, out refreshed);
                        if (verified.Count != 1 || verified[0] != result || refreshed != signature || !Focused(hwnd)) return false;
                        return Native.ClickPhysical(x, y, false, hwnd);
                    }
                    return false;
                }
                Thread.Sleep(180);
            }
            return false;
        }
    }
}
