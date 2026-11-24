using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using WxWatch;

// Operates only on the local File Transfer conversation. No message is sent.
static class WeChatFixture
{
    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }
    [DllImport("user32.dll",EntryPoint="SetPhysicalCursorPos")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll",EntryPoint="GetPhysicalCursorPos")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int cmd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr arg);
    delegate bool EnumWindow(IntPtr window,IntPtr arg);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);

    public static IntPtr RestoreOnFinish;
    public static void Restore()
    {
        if (RestoreOnFinish != IntPtr.Zero) ShowWindow(RestoreOnFinish,6);
    }
    static void Click(System.Windows.Rect rect, bool right, int expectedPid = 0)
    {
        if (rect.IsEmpty) throw new Exception("WeChat test control has no bounds");
        var point = new Point { X = (int)(rect.Left + rect.Width / 2), Y = (int)(rect.Top + rect.Height / 2) };
        int pid; GetWindowThreadProcessId(WindowFromPoint(point),out pid);
        if (expectedPid == 0 ? !WeChatUia.Pids().Contains((uint)pid) : expectedPid != pid)
            throw new Exception("test control is covered");
        Point original; GetCursorPos(out original);
        try
        {
            SetCursorPos(point.X,point.Y);
            mouse_event(right ? 8u : 2u,0,0,0,UIntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(right ? 16u : 4u,0,0,0,UIntPtr.Zero);
            Thread.Sleep(80);
        }
        finally { SetCursorPos(original.X,original.Y); }
    }
    public static System.Threading.Tasks.Task ClickButton(System.Windows.Controls.Button button)
    {
        var location = button.PointToScreen(new System.Windows.Point(button.ActualWidth / 2,button.ActualHeight / 2));
        return System.Threading.Tasks.Task.Run(() =>
            Click(new System.Windows.Rect(location,new System.Windows.Size(1,1)),false,Process.GetCurrentProcess().Id));
    }
    public static void MarkFileTransferUnread()
    {
        const string target = "文件传输助手";
        var uia = new WeChatUia();
        var status = new WatchStatus();
        if (!uia.Discover(true,status)) throw new Exception("微信会话列表不可用");
        var h = uia.MainHwnd;
        if (IsIconic(h)) { RestoreOnFinish = h; ShowWindow(h,9); Thread.Sleep(350); }
        if (!Navigator.ForceForeground(h)) throw new Exception("无法置前微信");
        Thread.Sleep(200);
        var rows = uia.ReadSessions(new SessionParser(),status);
        var file = rows == null ? null : rows.SingleOrDefault(s => s.Title == target);
        if (file == null) throw new Exception("文件传输助手不在可见列表");
        // Select the target before opening its context menu. WeChat can reorder rows on restore.
        {
            string error = file.Unread>0 ? Navigator.Go(uia,target,"auto",350,delegate { }) : null;
            if (error != null) {Console.WriteLine(error); if(!SetRead(uia,target,true,Console.WriteLine)) throw new Exception(error); }
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(200);
                file = uia.ReadSessions(new SessionParser(),new WatchStatus()).Single(s => s.Title == target);
                if (file.Unread <= 0) break;
            }
            if (file.Unread > 0) throw new Exception("文件传输助手未读基线未归零");
            Thread.Sleep(250);
        }
        if (!SetRead(uia,target,false,Console.WriteLine)) throw new Exception("未能定位文件传输助手的标为未读命令");
    }
        public static bool SetRead(WeChatUia uia, string title, bool read, Action<string> trace = null)
        {
            if (trace == null) trace = delegate { };
            var rows = uia.ReadSessions(new SessionParser(), new WatchStatus());
            if (rows == null || rows.Count(r => r.Title == title) != 1) { trace("row missing"); return false; }
            var row = rows.First(r => r.Title == title);
            if ((row.Unread <= 0) == read) return true;
            if(GetForegroundWindow()!=uia.MainHwnd)Navigator.ForceForeground(uia.MainHwnd);
            if (row.Rect.IsEmpty || !WeChatUia.IsWeChatForeground()) { trace("target lost focus"); return false; }
            var point = new System.Windows.Point(row.Rect.Left + row.Rect.Width / 2,row.Rect.Top + row.Rect.Height / 2);
            if (!SessionAction.HitSession(point,title)) { trace("row hit test failed"); return false; }
            string label = read ? "已读" : "未读";
            Click(row.Rect,true);
            var pids = WeChatUia.Pids();
            var cond = new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"标为" + label),
                new PropertyCondition(AutomationElement.NameProperty,"标记为" + label));
            bool found = false;
            for (int i = 0; i < 18; i++)
            {
                foreach (var window in WindowsOf(pids))
                {
                    AutomationElement button;
                    try { button = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,cond); }
                    catch (ElementNotAvailableException) { continue; }
                    if (button == null || button.Current.IsOffscreen) continue;
                    found = true;
                    
                    
                    var box = button.Current.BoundingRectangle;
                    if (box.IsEmpty) continue;
                    var center = new System.Windows.Point(box.Left + box.Width / 2,box.Top + box.Height / 2);
                    var hit = AutomationElement.FromPoint(center);
                    if (hit == null || !pids.Contains((uint)hit.Current.ProcessId)) continue;
                    Click(box,false);
                    if(!read && RestoreOnFinish!=IntPtr.Zero)ShowWindow(uia.MainHwnd,6);
                    for(int step=0;step<10;step++){Thread.Sleep(100);
                        var sample=uia.ReadSessions(new SessionParser(),new WatchStatus());
                        if(sample!=null && sample.Any(r=>r.Title==title && (r.Unread<=0)==read))return true;
                    }
                    trace("menu was clicked but state did not change");return false;
                }
                Thread.Sleep(100);
            }
            trace(found ? "menu command covered" : "menu command missing");

            return false;
        }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static List<IntPtr> WindowsOf(HashSet<uint> pids)
    {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window,IntPtr arg) {
            int pid; GetWindowThreadProcessId(window,out pid);
            if (pids.Contains((uint)pid) && IsWindowVisible(window)) windows.Add(window);
            return true;
        },IntPtr.Zero);
        return windows;
    }
}
