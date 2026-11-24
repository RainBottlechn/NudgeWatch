using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace WxWatch
{
    public partial class WeChatUia
    {
        AutomationElement nav;
        IntPtr navHwnd;
        DateTime navRefresh;
        static readonly Regex NavBadge = new Regex(@"(?:微信|聊天)\s*[:：,，(（]?\s*(\d+)\+?\s*(?:条\s*)?(?:未读|新消息)");

        ScrollPattern SessionScroll()
        {
            object value;
            return list != null && list.TryGetCurrentPattern(ScrollPattern.Pattern, out value) ? (ScrollPattern)value : null;
        }

        void ReadViewport(WatchStatus status)
        {
            try
            {
                var scroll = SessionScroll();
                if (scroll != null)
                {
                    status.ScrollPercent = scroll.Current.VerticallyScrollable ? scroll.Current.VerticalScrollPercent : 0;
                    status.ViewportDrifted = status.ScrollPercent > 0.5;
                }
                var now = DateTime.UtcNow;
                if (navHwnd != hwnd || now >= navRefresh)
                {
                    navHwnd = hwnd;
                    navRefresh = now.AddSeconds(10);
                    var root = AutomationElement.FromHandle(hwnd);
                    var bar = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MainView.main_tabbar"));
                    nav = bar == null ? null : bar.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "mmui::XTabBarItem"))
                        .Cast<AutomationElement>().FirstOrDefault(e => (e.Current.Name ?? "").StartsWith("微信") || (e.Current.Name ?? "").StartsWith("聊天"));
                }
                if (nav != null)
                {
                    var match = NavBadge.Match((nav.Current.Name ?? "") + " " + nav.Current.HelpText);
                    int count;
                    if (match.Success && Int32.TryParse(match.Groups[1].Value, out count)) status.NavTotalUnread = count;
                }
            }
            catch (ElementNotAvailableException) { nav = null; }
            catch (System.Runtime.InteropServices.COMException) { nav = null; }
        }

        internal bool ScrollToTop(bool allowForeground)
        {
            if (!allowForeground && IsWeChatForeground()) return false;
            try
            {
                var scroll = SessionScroll();
                if (scroll != null)
                {
                    if (!scroll.Current.VerticallyScrollable || scroll.Current.VerticalScrollPercent >= 0 && scroll.Current.VerticalScrollPercent <= 0.5)
                        return true;
                    if (!allowForeground && IsWeChatForeground()) return false;
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                    for (int n = 0; n < 4; n++)
                    {
                        Thread.Sleep(100);
                        var percent = scroll.Current.VerticalScrollPercent;
                        if (percent >= 0 && percent <= 0.5) return true;
                    }
                    return false;
                }
                if (list != null)
                {
                    var bar = list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar));
                    object rvObj;
                    if (bar != null && bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out rvObj))
                    {
                        var rv = (RangeValuePattern)rvObj;
                        if (!rv.Current.IsReadOnly)
                        {
                            double min = rv.Current.Minimum;
                            if (rv.Current.Value <= min + 0.5) return true;
                            if (!allowForeground && IsWeChatForeground()) return false;
                            rv.SetValue(min);
                            for (int n = 0; n < 4; n++)
                            {
                                Thread.Sleep(80);
                                if (rv.Current.Value <= min + 0.5) return true;
                            }
                        }
                    }
                }
            }
            catch (InvalidOperationException) { }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            return false;
        }

        internal bool Wheel(int delta)
        {
            if (list == null || Native.GetForegroundWindow() != hwnd || Native.IsIconic(hwnd)) return false;
            var rect = list.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width < 45 || rect.Height < 35) return false;
            int x = (int)(rect.Left + rect.Width / 2), y = (int)(rect.Top + rect.Height / 2);
            var target = new Native.POINT { X = x, Y = y };
            if (Native.GetAncestor(Native.WindowFromPoint(target), 2) != hwnd) return false;
            var node = AutomationElement.FromPoint(new Point(x, y));
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            for (int depth = 0; node != null && depth < 12; node = TreeWalker.RawViewWalker.GetParent(node), depth++)
                if (node.Current.ClassName == ListClass && node.Current.ProcessId == pid) return Native.ScrollPhysical(hwnd, x, y, delta);
            return false;
        }

        internal bool NextPage()
        {
            if (Native.GetForegroundWindow() != hwnd) return false;
            try
            {
                var scroll = SessionScroll();
                if (scroll != null && scroll.Current.VerticallyScrollable)
                {
                    if (Native.GetForegroundWindow() != hwnd) return false;
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    return true;
                }
            }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            return Wheel(-720);
        }

        internal List<SessionInfo> ManualTop(bool fast = false)
        {
            var parser = new SessionParser();
            if (ScrollToTop(true)) return ReadSessions(parser, new WatchStatus());
            int wheelDelta = fast ? 2880 : 1800;
            int stepDelayMs = fast ? 110 : 160;
            int maxSteps = fast ? 10 : 15;
            int settleNeed = fast ? 1 : 2;
            string[] last = null;
            int settled = 0;
            for (int step = 0; step < maxSteps; step++)
            {
                if (Native.GetForegroundWindow() != hwnd) return null;
                if (!Wheel(wheelDelta)) return null;
                Thread.Sleep(stepDelayMs);
                var rows = ReadSessions(parser, new WatchStatus());
                if (rows == null || rows.Count == 0) return null;
                var head = rows.Take(3).Select(s => s.Title + "\t" + s.Rect.Top.ToString("F0")).ToArray();
                settled = last != null && last.SequenceEqual(head) ? settled + 1 : 0;
                if (settled >= settleNeed && step >= 1) return rows;
                last = head;
            }
            return null;
        }
    }
}
