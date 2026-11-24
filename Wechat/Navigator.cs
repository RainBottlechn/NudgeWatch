using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace WxWatch
{
    public enum WeChatWindowState { ClosedOrTray, Minimized, Visible }

    /// <summary>通过可见会话或经 UIA 验证的搜索结果打开微信会话。</summary>
    public static class Navigator
    {
        public static string Go(WeChatUia uia, string chat, string mode, int searchDelayMs, Action<string> log, bool returnToTop = false)
        {
            var st = new WatchStatus();
            if (!uia.Discover(true, st) || uia.MainHwnd == IntPtr.Zero || !Native.IsWindowVisible(uia.MainHwnd))
            {
                log("主窗口不可见，尝试唤醒已有微信窗口");
                string wakeErr = WakeWeChat(uia.MainHwnd, uia, log);
                if (wakeErr != null) return wakeErr;
                if (!uia.Discover(true, st)) return "找不到微信主窗口：" + st.Message;
            }
            IntPtr h = uia.MainHwnd;
            if (returnToTop && !WeChatUia.IsWeChatForeground()) uia.ScrollToTop(false);

            if (Native.IsIconic(h)) { Native.ShowWindow(h, Native.SW_RESTORE); Thread.Sleep(250); }

            bool fg = ForceForeground(h);
            Thread.Sleep(200);
            if (!fg || !VerifyWeChatForeground(h))
            {
                log("激活微信窗口失败，已中止跳转以防向其他窗口误输入");
                return "激活微信窗口失败，已中止跳转";
            }
            log("激活微信窗口：成功");

            if (mode != "search")
            {
                var list = uia.ReadSessions(new SessionParser(), new WatchStatus());
                Native.RECT wr;
                Native.GetWindowRect(h, out wr);
                if (list != null)
                {
                    int matchCount = 0;
                    SessionInfo firstMatch = null;
                    foreach (var s in list)
                    {
                        if (s.Title != chat) continue;
                        matchCount++;
                        if (firstMatch == null) firstMatch = s;
                    }
                    if (matchCount > 1)
                        return "列表中有重名会话，请在微信中选择目标";

                    if (firstMatch != null && !firstMatch.Rect.IsEmpty)
                    {
                        int x = (int)(firstMatch.Rect.Left + firstMatch.Rect.Width / 2);
                        int y = (int)(firstMatch.Rect.Top + firstMatch.Rect.Height / 2);
                        if (x >= wr.Left && x <= wr.Right && y >= wr.Top && y <= wr.Bottom)
                        {
                            if (!VerifyWeChatForeground(h)) return "点击前微信失去前台焦点，已中止";
                            log(string.Format("会话在可见列表中，直接点击 ({0},{1})", x, y));
                            var point = new Native.POINT { X = x, Y = y };
                            if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != h)
                                return "会话被其他窗口遮挡，请移开后重试";
                            if (!SessionAction.HitSession(new System.Windows.Point(x,y),chat))
                                return "会话位置已变化，请重试";
                            if (!Native.ClickPhysical(x,y,false,h)) return "会话位置或焦点已变化，请重试";
                            if (firstMatch.Unread > 0)
                            {
                                for (int attempt = 0; attempt < 6; attempt++)
                                {
                                    Thread.Sleep(200);
                                    var sampled = uia.ReadSessions(new SessionParser(),new WatchStatus());
                                    if (sampled != null && sampled.Exists(s => s.Title == chat && s.Unread <= 0))
                                        return null;
                                    if (attempt == 2) uia.SelectVisible(chat);
                                }
                                if (SessionAction.ConfirmRead(uia, chat)) return null;
                                return Search(uia,h,chat,searchDelayMs,log,true);
                            }
                            return null;
                        }
                    }
                }
                log("会话不在可见列表中，改用搜索");
            }

            return Search(uia,h,chat,searchDelayMs,log,false);
        }

        static string Search(WeChatUia uia,IntPtr h,string chat,int searchDelayMs,Action<string> log,bool verifyRead)
        {
            if (Native.GetForegroundWindow() != h) return "搜索前微信失去前台焦点，已中止";
            Native.KeyCombo(Native.VK_CONTROL, Native.VK_F);
            for (int i = 0; i < 7 && !SearchResults.Focused(h); i++) Thread.Sleep(100);
            if (!SearchResults.Focused(h)) return "微信搜索框未获得焦点，可重新跳转";
            Native.KeyCombo(Native.VK_CONTROL, Native.VK_A);
            if (!SearchResults.Focused(h)) return "输入前微信失去前台焦点，已中止";
            Native.TypeUnicode(chat);
            try
            {
                if (!SearchResults.OpenExact(h, chat, searchDelayMs)) return SearchResults.NeedsConfirmation;
            }
            catch (System.Windows.Automation.ElementNotAvailableException) { return SearchResults.NeedsConfirmation; }
            catch (System.Runtime.InteropServices.COMException) { return SearchResults.NeedsConfirmation; }
            log("已校验并点击唯一精确搜索结果");
            if(verifyRead) {
                for(int n=0;n<12;n++) {
                    Thread.Sleep(200);
                    var rows=uia.ReadSessions(new SessionParser(),new WatchStatus());
                    if(rows!=null && rows.Exists(r=>r.Title==chat && r.Unread<=0))return null;
                }
                if(!SessionAction.ConfirmRead(uia,chat))return "已打开会话，未读状态仍未归零";
            }
            return null;
        }

        public static bool VerifyWeChatForeground(IntPtr expectedHwnd)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == expectedHwnd) return true;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            return WeChatUia.Pids().Contains(pid);
        }

        public static bool ForceForeground(IntPtr h)
        {
            if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return true;
            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out pid);
            uint me = Native.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me) Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(h);
            Native.SetForegroundWindow(h);
            if (fgThread != 0 && fgThread != me) Native.AttachThreadInput(me, fgThread, false);
            return Native.GetForegroundWindow() == h;
        }

        /// <summary>唤醒托盘中已登录的微信主窗口；仅在微信进程完全未运行时才启动 Weixin.exe。</summary>
        public static string WakeWeChat(IntPtr knownHwnd, WeChatUia uia, Action<string> log)
        {
            var pids = WeChatUia.Pids();
            if (pids.Count == 0)
            {
                if (log != null) log("未检测到运行中的微信进程，启动 Weixin.exe");
                LaunchWeixin(log);
                Thread.Sleep(1500);
                return null;
            }

            var candidates = new System.Collections.Generic.List<IntPtr>(CandidateMainWindows(knownHwnd, pids));

            // 1. 若主窗口本就在桌面可见或仅是最小化到任务栏（非关闭进托盘），直接常规恢复前置即可
            foreach (var candidate in candidates)
            {
                if (!Native.IsWindowVisible(candidate)) continue;
                if (Native.IsIconic(candidate))
                {
                    Native.ShowWindow(candidate, Native.SW_RESTORE);
                    Thread.Sleep(150);
                }
                ForceForeground(candidate);
                if (WaitForWeChatReady(uia, candidate, 6))
                {
                    if (log != null) log("已恢复最小化的微信窗口");
                    return null;
                }
            }

            // 2. 微信已关闭进托盘时：仅走 Windows 系统托盘图标原生唤醒，不使用 ShowWindow 强拉隐藏句柄（避免 Qt 绘制冻结与焦点击活异常）
            if (InvokeWeChatTrayIcon())
            {
                if (WaitForWeChatReady(uia, IntPtr.Zero, 8))
                {
                    if (log != null) log("已通过系统托盘图标唤醒微信");
                    return null;
                }
            }

            return "微信已收进托盘并暂停消息拉取，未能自动点开，请点击右下角托盘微信图标（建议平时用「—」最小化保活）";
        }

        /// <summary>检查当前微信主窗口状态（桌面可见 / 已最小化 / 已关闭到托盘）。</summary>
        public static WeChatWindowState InspectWeChatWindow(IntPtr knownHwnd, out IntPtr targetHwnd)
        {
            targetHwnd = IntPtr.Zero;
            var pids = WeChatUia.Pids();
            if (pids.Count == 0) return WeChatWindowState.ClosedOrTray;
            IntPtr minimizedHwnd = IntPtr.Zero;
            foreach (var h in CandidateMainWindows(knownHwnd, pids))
            {
                if (!Native.IsWindowVisible(h)) continue;
                if (!Native.IsIconic(h))
                {
                    targetHwnd = h;
                    return WeChatWindowState.Visible;
                }
                if (minimizedHwnd == IntPtr.Zero) minimizedHwnd = h;
            }
            if (minimizedHwnd != IntPtr.Zero)
            {
                targetHwnd = minimizedHwnd;
                return WeChatWindowState.Minimized;
            }
            return WeChatWindowState.ClosedOrTray;
        }

        /// <summary>恢复已最小化的微信主窗口并置前。</summary>
        public static bool RestoreWeChatWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !Native.IsWindow(targetHwnd)) return false;
            if (Native.IsIconic(targetHwnd))
            {
                Native.ShowWindow(targetHwnd, Native.SW_RESTORE);
                Thread.Sleep(150);
            }
            ForceForeground(targetHwnd);
            return Native.IsWindowVisible(targetHwnd) && !Native.IsIconic(targetHwnd);
        }

        static bool HasTopAnchors(System.Collections.Generic.List<SessionInfo> rows, System.Collections.Generic.List<string> anchors)
        {
            if (rows == null || rows.Count == 0 || anchors == null || anchors.Count == 0) return false;
            int topCount = Math.Min(3, rows.Count);
            foreach (var anchor in anchors)
            {
                bool found = false;
                for (int i = 0; i < topCount; i++)
                {
                    if (rows[i].Title == anchor) { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>将当前处于桌面的微信主窗最小化到任务栏（不断开 UIA 拉取）。</summary>
        public static bool MinimizeWeChat(IntPtr knownHwnd, bool returnToTop = false)
        {
            System.Collections.Generic.List<SessionInfo> topRows;
            return MinimizeWeChat(knownHwnd, returnToTop, true, null, out topRows);
        }

        /// <summary>将当前处于桌面的微信主窗最小化到任务栏，并在最小化前尽量自动回顶。</summary>
        public static bool MinimizeWeChat(IntPtr knownHwnd, bool returnToTop, bool allowForegroundTop, System.Collections.Generic.IEnumerable<string> topAnchors, out System.Collections.Generic.List<SessionInfo> topRows)
        {
            topRows = null;
            var pids = WeChatUia.Pids();
            if (pids.Count == 0) return false;
            var anchors = new System.Collections.Generic.List<string>();
            if (topAnchors != null)
            {
                foreach (var a in topAnchors)
                    if (!String.IsNullOrEmpty(a)) anchors.Add(a);
            }
            foreach (var h in CandidateMainWindows(knownHwnd, pids))
            {
                if (!Native.IsWindowVisible(h)) continue;
                if (Native.IsIconic(h)) return true;
                if (returnToTop)
                {
                    if (Native.GetForegroundWindow() == h)
                    {
                        if (allowForegroundTop)
                        {
                            var uia = new WeChatUia();
                            if (uia.Discover(true, new WatchStatus()) && uia.MainHwnd == h)
                            {
                                var current = uia.ReadSessions(new SessionParser(), new WatchStatus());
                                if (HasTopAnchors(current, anchors)) topRows = current;
                                else topRows = uia.ManualTop(true);
                            }
                        }
                        Native.ShowWindow(h, Native.SW_MINIMIZE);
                        return true;
                    }
                    if (TryBackgroundTop(h))
                    {
                        var uia = new WeChatUia();
                        if (uia.Discover(true, new WatchStatus()) && uia.MainHwnd == h)
                            topRows = uia.ReadSessions(new SessionParser(), new WatchStatus());
                        Native.ShowWindow(h, Native.SW_MINIMIZE);
                        return true;
                    }
                    var previous = Native.GetForegroundWindow();
                    if (ForceForeground(h))
                    {
                        Thread.Sleep(140);
                        if (Native.GetForegroundWindow() == h)
                        {
                            var uia = new WeChatUia();
                            if (uia.Discover(true, new WatchStatus()) && uia.MainHwnd == h)
                            {
                                var current = uia.ReadSessions(new SessionParser(), new WatchStatus());
                                if (HasTopAnchors(current, anchors)) topRows = current;
                                else topRows = uia.ManualTop(true);
                            }
                        }
                    }
                    Native.ShowWindow(h, Native.SW_MINIMIZE);
                    if (previous != IntPtr.Zero && previous != h && Native.IsWindowVisible(previous))
                        ForceForeground(previous);
                    return true;
                }
                Native.ShowWindow(h, Native.SW_MINIMIZE);
                return true;
            }
            return false;
        }

        internal static bool TryBackgroundTop(IntPtr expected)
        {
            if (expected == IntPtr.Zero || WeChatUia.IsWeChatForeground() || !(Native.IsWindowVisible(expected) || Native.IsIconic(expected))) return false;
            var uia = new WeChatUia();
            return uia.Discover(true, new WatchStatus()) && uia.MainHwnd == expected && uia.ScrollToTop(false);
        }

        internal static bool TryAutoReturnTop(IntPtr expected, bool allowBriefRestore, out System.Collections.Generic.List<SessionInfo> topRows)
        {
            topRows = null;
            if (expected == IntPtr.Zero || !Native.IsWindow(expected) || WeChatUia.IsWeChatForeground()) return false;
            if (TryBackgroundTop(expected))
            {
                var uia = new WeChatUia();
                if (uia.Discover(true, new WatchStatus()) && uia.MainHwnd == expected)
                    topRows = uia.ReadSessions(new SessionParser(), new WatchStatus());
                return true;
            }
            if (!allowBriefRestore || Native.IdleTime() < TimeSpan.FromSeconds(3)) return false;
            System.Collections.Generic.List<SessionInfo> captured = null;
            bool ok = WithSessionList(expected, uia => (captured = uia.ManualTop(true)) != null);
            topRows = captured;
            return ok;
        }

        internal static bool WithSessionList(IntPtr expected, Func<WeChatUia, bool> action)
        {
            var uia = new WeChatUia();
            if (!uia.Discover(true, new WatchStatus())) return false;
            var h = uia.MainHwnd;
            if ((expected != IntPtr.Zero && h != expected) || !(Native.IsWindowVisible(h) || Native.IsIconic(h))) return false;
            var previous = Native.GetForegroundWindow();
            bool minimized = Native.IsIconic(h);
            try
            {
                if (minimized)
                {
                    Native.ShowWindow(h, Native.SW_RESTORE);
                    Thread.Sleep(180);
                }
                if (!ForceForeground(h)) return false;
                Thread.Sleep(160);
                return Native.GetForegroundWindow() == h && action(uia);
            }
            finally
            {
                if (Native.GetForegroundWindow() == h)
                {
                    if (minimized) Native.ShowWindow(h, Native.SW_MINIMIZE);
                    if (previous != IntPtr.Zero && previous != h && Native.IsWindowVisible(previous)) ForceForeground(previous);
                }
                else if (minimized && Native.IsWindowVisible(h) && !Native.IsIconic(h))
                {
                    Native.ShowWindow(h, Native.SW_MINIMIZE);
                }
            }
        }

        static bool WaitForWeChatReady(WeChatUia uia, IntPtr targetWindow, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                Thread.Sleep(200);
                bool visibleOk = targetWindow == IntPtr.Zero || Native.IsWindowVisible(targetWindow);
                if (visibleOk && (uia == null || uia.Discover(true, new WatchStatus())))
                    return true;
            }
            return targetWindow != IntPtr.Zero && Native.IsWindowVisible(targetWindow);
        }

        public static bool IsChatMainWindowCandidate(string title, string className, int width, int height)
        {
            if (string.IsNullOrEmpty(title)) return false;
            if ((className ?? "").IndexOf("TrayIcon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("TrayIcon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("MessageWindow", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (title != "微信" && title != "WeChat") return false;
            return width == 0 || (width >= 300 && height >= 200);
        }

        static bool VerifyMainWindow(IntPtr h, System.Collections.Generic.HashSet<uint> pids, out int score)
        {
            score = 0;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return false;
            string title = Native.TitleOf(h);
            string cls = Native.ClassOf(h);
            var wp = new Native.WINDOWPLACEMENT();
            wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
            int w = 0, ht = 0;
            if (Native.GetWindowPlacement(h, ref wp))
            {
                w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                ht = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
            }
            if (!IsChatMainWindowCandidate(title, cls, w, ht)) return false;
            score = 100;
            if (cls.IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("mmui", StringComparison.OrdinalIgnoreCase) >= 0) score += 40;
            if (w >= 400 && ht >= 300) score += 30;
            return true;
        }

        static System.Collections.Generic.IEnumerable<IntPtr> CandidateMainWindows(IntPtr knownHwnd, System.Collections.Generic.HashSet<uint> pids)
        {
            var seen = new System.Collections.Generic.HashSet<IntPtr>();
            int knownScore;
            if (VerifyMainWindow(knownHwnd, pids, out knownScore) && seen.Add(knownHwnd))
                yield return knownHwnd;

            var ranked = new System.Collections.Generic.List<System.Tuple<int, IntPtr>>();
            foreach (var h in Native.TopWindowsOf(pids, true))
            {
                if (!seen.Add(h)) continue;
                int score;
                if (VerifyMainWindow(h, pids, out score))
                    ranked.Add(System.Tuple.Create(score, h));
            }
            ranked.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            foreach (var item in ranked) yield return item.Item2;
        }

        public static bool IsTrayNotificationElement(string name, string automationId, string className, bool inNotifyContainer)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Contains("NudgeWatch") || name.Contains("WxWatch")) return false;
            if (!string.IsNullOrEmpty(automationId) && automationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(className) && className.IndexOf("TaskList", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!inNotifyContainer)
            {
                bool looksLikeTray =
                    (!string.IsNullOrEmpty(automationId) && automationId.IndexOf("Notify", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(className) && (className.IndexOf("Notify", StringComparison.OrdinalIgnoreCase) >= 0 || className.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0));
                if (!looksLikeTray) return false;
            }
            return name == "微信" || name.StartsWith("微信") || name.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool InvokeWeChatTrayIcon()
        {
            try
            {
                IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero)
                {
                    IntPtr notify = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (notify != IntPtr.Zero && ClickTrayButtonInHandle(notify, true)) return true;
                    if (ClickTrayButtonInHandle(taskbar, false)) return true;

                    var root = System.Windows.Automation.AutomationElement.FromHandle(taskbar);
                    if (root != null)
                    {
                        foreach (System.Windows.Automation.AutomationElement b in root.FindAll(
                            System.Windows.Automation.TreeScope.Descendants,
                            new System.Windows.Automation.PropertyCondition(
                                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                                System.Windows.Automation.ControlType.Button)))
                        {
                            string name = b.Current.Name ?? "";
                            if (!b.Current.IsOffscreen && (name.Contains("隐藏的图标") || name.IndexOf("hidden icons", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                if (!ClickTrayElement(b, taskbar)) continue;
                                for (int attempt = 0; attempt < 6; attempt++)
                                {
                                    Thread.Sleep(120);
                                    if (ClickTrayButtonIn("NotifyIconOverflowWindow") ||
                                        ClickTrayButtonIn("TopLevelWindowForOverflowXamlIsland")) return true;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        static bool ClickTrayButtonIn(string windowClass)
        {
            IntPtr hwnd = Native.FindWindow(windowClass, null);
            return ClickTrayButtonInHandle(hwnd, true);
        }

        static bool ClickTrayButtonInHandle(IntPtr hwnd, bool inNotifyContainer)
        {
            if (hwnd == IntPtr.Zero) return false;
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root == null) return false;
            var cond = new System.Windows.Automation.OrCondition(
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button),
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.ListItem));
            foreach (System.Windows.Automation.AutomationElement el in root.FindAll(System.Windows.Automation.TreeScope.Descendants, cond))
            {
                var cur = el.Current;
                if (IsTrayNotificationElement(cur.Name, cur.AutomationId, cur.ClassName, inNotifyContainer) && ClickTrayElement(el, hwnd))
                    return true;
            }
            return false;
        }

        static bool ClickTrayElement(System.Windows.Automation.AutomationElement el, IntPtr hwnd)
        {
            var state = el.Current;
            var r = state.BoundingRectangle;
            if (state.IsOffscreen || r.IsEmpty || r.Width < 1 || r.Height < 1) return false;
            int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
            var hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
            return Native.GetAncestor(hit, 2) == Native.GetAncestor(hwnd, 2) && Native.ClickPhysical(x, y, false);
        }

        static void LaunchWeixin(Action<string> log)
        {
            string path = null;
            foreach (var p in Process.GetProcessesByName("Weixin"))
            {
                try { if (path == null) path = p.MainModule.FileName; } catch (Exception) { }
                p.Dispose();
            }
            if (path == null) path = @"C:\Program Files\Tencent\Weixin\Weixin.exe";
            if (!File.Exists(path)) { if (log != null) log("找不到 " + path); return; }
            Process.Start(path);
        }
    }

}



