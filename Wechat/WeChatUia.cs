using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace WxWatch
{
    public enum WatchState { NoWeChat, NoList, Ok, Error }

    public class WatchStatus
    {
        public WatchState State = WatchState.NoList;
        public string Message = "";
        public bool WindowVisible, WindowIconic;
        public bool ViewportDrifted;
        public double ScrollPercent = -1;
        public int NavTotalUnread = -1;
        public int CellCount;
        public double ReadMs;
    }

    /// <summary>微信 4.x 界面访问层：定位主窗口和会话列表，读取会话单元。</summary>
    public partial class WeChatUia
    {
        public const string ListClass = "mmui::ChatSessionList";
        public const string CellClass = "mmui::ChatSessionCell";
        static readonly Condition ListCond = new PropertyCondition(AutomationElement.ClassNameProperty, ListClass);
        static readonly Condition CellCond = new PropertyCondition(AutomationElement.ClassNameProperty, CellClass);

        AutomationElement list;
        IntPtr hwnd = IntPtr.Zero;
        DateTime nextDiscover = DateTime.MinValue;

        public IntPtr MainHwnd { get { return hwnd; } }

        public static HashSet<uint> Pids()
        {
            var set = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("Weixin")) { set.Add((uint)p.Id); p.Dispose(); }
            return set;
        }

        public static bool IsWeChatForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            return Pids().Contains(pid);
        }

        public void Invalidate() { list = null; }

        public bool SelectVisible(string title)
        {
            if (list == null || Native.GetForegroundWindow() != hwnd) return false;
            AutomationElement target = null;
            var parser = new SessionParser();
            foreach (AutomationElement cell in list.FindAll(TreeScope.Descendants, CellCond))
            {
                if (parser.Parse(cell.Current.Name).Title != title) continue;
                if (target != null) return false;
                target = cell;
            }
            if (target == null) return false;
            object pattern;
            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            { ((InvokePattern)pattern).Invoke(); return true; }
            if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
            { ((SelectionItemPattern)pattern).Select(); return true; }
            return false;
        }

        /// <summary>查找含有会话列表的微信窗口。force=false 时限速每 3 秒一次。</summary>
        public bool Discover(bool force, WatchStatus st)
        {
            if (!force && DateTime.Now < nextDiscover) return false;
            nextDiscover = DateTime.Now.AddSeconds(3);
            var pids = Pids();
            if (pids.Count == 0) { st.State = WatchState.NoWeChat; st.Message = "未检测到 Weixin.exe"; return false; }
            foreach (var h in Native.TopWindowsOf(pids, true))
            {
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    var l = root.FindFirst(TreeScope.Descendants, ListCond);
                    if (l != null) { list = l; hwnd = h; return true; }
                }
                catch (Exception) { }
            }
            st.State = WatchState.NoList;
            st.Message = "找到微信进程，但读不到会话列表（窗口可能已关闭到托盘）";
            return false;
        }

        public List<SessionInfo> ReadSessions(SessionParser parser, WatchStatus st)
        {
            var sw = Stopwatch.StartNew();
            if (hwnd != IntPtr.Zero && !Native.IsWindow(hwnd)) { list = null; hwnd = IntPtr.Zero; }
            if (list == null && !Discover(false, st)) { FillWindow(st); return null; }
            FillWindow(st);
            if(!st.WindowVisible && !st.WindowIconic){list=null;st.State=WatchState.NoList;return null;}
            AutomationElementCollection cells;
            var cacheReq = new CacheRequest();
            cacheReq.Add(AutomationElement.NameProperty);
            cacheReq.Add(AutomationElement.BoundingRectangleProperty);
            try
            {
                using (cacheReq.Activate())
                {
                    cells = list.FindAll(TreeScope.Children, CellCond);
                    if (cells.Count == 0) cells = list.FindAll(TreeScope.Descendants, CellCond);
                }
            }
            catch (ElementNotAvailableException)
            {
                list = null;
                st.State = WatchState.NoList;
                st.Message = "会话列表元素失效，重新查找中";
                return null;
            }

            var result = new List<SessionInfo>();
            int idx = 0;
            foreach (AutomationElement c in cells)
            {
                string name;
                System.Windows.Rect r;
                try { name = c.Cached.Name; r = c.Cached.BoundingRectangle; }
                catch (Exception)
                {
                    try { name = c.Current.Name; r = c.Current.BoundingRectangle; }
                    catch (Exception) { continue; }
                }
                if (string.IsNullOrEmpty(name)) name = ChildText(c);
                var s = parser.Parse(name);
                s.Rect = r;
                s.Index = idx++;
                result.Add(s);
            }
            st.CellCount = result.Count;
            st.State = result.Count > 0 ? WatchState.Ok : WatchState.NoList;
            if (result.Count == 0) st.Message = "会话列表为空";
            st.ReadMs = sw.Elapsed.TotalMilliseconds;
            ReadViewport(st);
            return result;
        }

        void FillWindow(WatchStatus st)
        {
            if (hwnd == IntPtr.Zero) return;
            st.WindowVisible = Native.IsWindowVisible(hwnd);
            st.WindowIconic = Native.IsIconic(hwnd);
        }

        static string ChildText(AutomationElement e)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (AutomationElement ch in e.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string n = ch.Current.Name;
                    if (!string.IsNullOrEmpty(n)) sb.Append(n).Append('\n');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        // ---------------- 诊断导出 ----------------

        public static string Dump(bool includeWindowSkeleton)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WxWatch 诊断导出 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("!! 本文件包含会话名称和最新消息摘要，发给他人前请自行打码 !!");
            sb.AppendLine();
            foreach (var p in Process.GetProcessesByName("Weixin"))
            {
                string ver = "?";
                try { ver = p.MainModule.FileVersionInfo.ProductVersion; } catch (Exception) { }
                sb.AppendLine("进程 Weixin pid=" + p.Id + " 版本=" + ver);
                p.Dispose();
            }
            var pids = Pids();
            sb.AppendLine();
            sb.AppendLine("== 顶层窗口 ==");
            foreach (var h in Native.TopWindowsOf(pids, true))
            {
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                bool vis = Native.IsWindowVisible(h);
                if (!vis && Native.TitleOf(h).Length == 0) continue;
                sb.AppendLine(string.Format("hwnd=0x{0:X} class={1} title=\"{2}\" visible={3} iconic={4} rect=({5},{6})-({7},{8})",
                    h.ToInt64(), Native.ClassOf(h), Native.TitleOf(h), vis, Native.IsIconic(h), r.Left, r.Top, r.Right, r.Bottom));
            }

            var parser = new SessionParser();
            foreach (var h in Native.TopWindowsOf(pids, true))
            {
                AutomationElement root, l;
                try { root = AutomationElement.FromHandle(h); l = root.FindFirst(TreeScope.Descendants, ListCond); }
                catch (Exception) { continue; }
                if (l == null) continue;

                sb.AppendLine();
                sb.AppendLine(string.Format("== 主窗口 hwnd=0x{0:X} ==", h.ToInt64()));
                object spObj;
                if (l.TryGetCurrentPattern(ScrollPattern.Pattern, out spObj))
                {
                    var sp = (ScrollPattern)spObj;
                    sb.AppendLine(string.Format("ScrollPattern: 支持 | VerticallyScrollable={0} VerticalScrollPercent={1:0.##} VerticalViewSize={2:0.##}",
                        sp.Current.VerticallyScrollable, sp.Current.VerticalScrollPercent, sp.Current.VerticalViewSize));
                }
                else
                {
                    sb.AppendLine("ScrollPattern: 不支持");
                }
                sb.AppendLine("-- 左侧栏/导航未读候选节点 --");
                try
                {
                    var navCond = new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement nb in root.FindAll(TreeScope.Descendants, navCond))
                    {
                        string nn = Esc(nb.Current.Name);
                        if (nn.Contains("聊天") || nn.Contains("微信") || nn.Contains("未读"))
                            sb.AppendLine(string.Format("  {0} class={1} name=\"{2}\" rect={3}",
                                nb.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                                nb.Current.ClassName, nn, nb.Current.BoundingRectangle));
                    }
                }
                catch (Exception) { }
                if (includeWindowSkeleton)
                {
                    sb.AppendLine("-- 窗口控件骨架（深度≤5，最多 400 个节点）--");
                    int budget = 400;
                    DumpTree(sb, root, 0, 5, ref budget);
                }
                sb.AppendLine("-- 会话列表子树（深度≤4）--");
                int b2 = 600;
                DumpTree(sb, l, 0, 4, ref b2);
                sb.AppendLine("-- 会话单元 Name 原文（⏎ = 换行）及启发式解析结果 --");
                int i = 0;
                foreach (AutomationElement c in l.FindAll(TreeScope.Descendants, CellCond))
                {
                    string n = "";
                    try { n = c.Current.Name; } catch (Exception) { }
                    if (string.IsNullOrEmpty(n)) n = ChildText(c);
                    var s = parser.Parse(n);
                    s.Index = i++;
                    sb.AppendLine("RAW  " + Esc(n));
                    sb.AppendLine("PARSE" + s);
                }
                break;
            }
            return sb.ToString();
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("\r", "").Replace("\n", "⏎");
        }

        static void DumpTree(StringBuilder sb, AutomationElement e, int depth, int maxDepth, ref int budget)
        {
            if (budget-- <= 0) return;
            string line;
            try
            {
                var c = e.Current;
                string name = Esc(c.Name);
                if (name.Length > 80) name = name.Substring(0, 80) + "…";
                line = string.Format("{0}{1} class={2} name=\"{3}\" rect={4}{5}",
                    new string(' ', depth * 2), c.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                    c.ClassName, name, c.BoundingRectangle, c.IsOffscreen ? " offscreen" : "");
            }
            catch (Exception ex) { line = new string(' ', depth * 2) + "<读取失败 " + ex.GetType().Name + ">"; }
            sb.AppendLine(line);
            if (depth >= maxDepth) return;
            AutomationElement ch = null;
            try { ch = TreeWalker.RawViewWalker.GetFirstChild(e); } catch (Exception) { }
            while (ch != null && budget > 0)
            {
                DumpTree(sb, ch, depth + 1, maxDepth, ref budget);
                try { ch = TreeWalker.RawViewWalker.GetNextSibling(ch); } catch (Exception) { break; }
            }
        }
    }
}
