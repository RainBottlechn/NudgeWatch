using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WxWatch
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FLASHWINFO fi);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, string l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
        public const ushort VK_CONTROL = 0x11, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_HOME = 0x24, VK_A = 0x41, VK_F = 0x46;

        public static TimeSpan IdleTime()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
            uint elapsed = unchecked((uint)Environment.TickCount - lii.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>枚举属于指定进程的顶层窗口，可见窗口排在前面。</summary>
        public static List<IntPtr> TopWindowsOf(ICollection<uint> pids, bool includeHidden)
        {
            var visible = new List<IntPtr>();
            var hidden = new List<IntPtr>();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid))
                {
                    if (IsWindowVisible(h)) visible.Add(h);
                    else if (includeHidden) hidden.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            visible.AddRange(hidden);
            return visible;
        }

        static INPUT Key(ushort vk, bool up)
        {
            var i = new INPUT();
            i.type = 1;
            i.U.ki.wVk = vk;
            i.U.ki.dwFlags = up ? 2u : 0u;
            return i;
        }

        static void Send(List<INPUT> list)
        {
            if (list.Count == 0) return;
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>按下组合键（按顺序按下，逆序抬起）。</summary>
        public static void KeyCombo(params ushort[] vks)
        {
            var list = new List<INPUT>();
            foreach (var vk in vks) list.Add(Key(vk, false));
            for (int i = vks.Length - 1; i >= 0; i--) list.Add(Key(vks[i], true));
            Send(list);
        }

        /// <summary>用 KEYEVENTF_UNICODE 直接输入文字，不经过剪贴板和输入法。</summary>
        public static void TypeUnicode(string text)
        {
            var list = new List<INPUT>();
            foreach (char c in text)
            {
                var down = new INPUT();
                down.type = 1;
                down.U.ki.wScan = c;
                down.U.ki.dwFlags = 0x0004;
                var up = down;
                up.U.ki.dwFlags = 0x0004 | 0x0002;
                list.Add(down);
                list.Add(up);
            }
            Send(list);
        }

        [DllImport("user32.dll")] static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out POINT point);

        public static bool ClickPhysical(int x, int y, bool right, IntPtr expected = default(IntPtr))
        {
            POINT old, current;
            GetPhysicalCursorPos(out old);
            if (!SetPhysicalCursorPos(x, y)) return false;
            System.Threading.Thread.Sleep(50);
            if (!GetPhysicalCursorPos(out current) || current.X != x || current.Y != y) return false;
            if (expected != IntPtr.Zero && GetForegroundWindow() != expected)
            {
                SetPhysicalCursorPos(old.X, old.Y);
                return false;
            }
            var list = new List<INPUT>();
            var d = new INPUT(); d.type = 0; d.U.mi.dwFlags = right ? 8u : 2u;
            var u = new INPUT(); u.type = 0; u.U.mi.dwFlags = right ? 16u : 4u;
            list.Add(d); Send(list);
            System.Threading.Thread.Sleep(65);
            list.Clear(); list.Add(u); Send(list);
            System.Threading.Thread.Sleep(60);
            if (GetPhysicalCursorPos(out current) && current.X == x && current.Y == y) SetPhysicalCursorPos(old.X, old.Y);
            return true;
        }

        public static bool ScrollPhysical(IntPtr hwnd, int x, int y, int delta)
        {
            POINT previous, current;
            if (GetForegroundWindow() != hwnd || !GetPhysicalCursorPos(out previous) || !SetPhysicalCursorPos(x, y))
                return false;
            try
            {
                if (GetForegroundWindow() != hwnd) return false;
                if (GetForegroundWindow() != hwnd || !GetPhysicalCursorPos(out current) || current.X != x || current.Y != y)
                    return false;
                var input = new INPUT();
                input.U.mi.dwFlags = 0x0800;
                input.U.mi.mouseData = unchecked((uint)delta);
                bool sent = SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
                if (sent) System.Threading.Thread.Sleep(110);
                return sent;
            }
            finally
            {
                if (GetPhysicalCursorPos(out current) && current.X == x && current.Y == y) SetPhysicalCursorPos(previous.X, previous.Y);
            }
        }

        public static void FlashTaskbar(IntPtr h)
        {
            var fi = new FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            fi.hwnd = h;
            fi.dwFlags = 3 | 12; // FLASHW_ALL | FLASHW_TIMERNOFG
            fi.uCount = 0;
            fi.dwTimeout = 0;
            FlashWindowEx(ref fi);
        }
    }
}

