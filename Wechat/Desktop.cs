using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class NudgeDesktop {
  delegate bool EnumProc(IntPtr window, IntPtr param);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
  [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
  [DllImport("user32.dll", EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
  [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
  public static Dictionary<string,int> Minimize(uint excludePid) {
    var targets = new List<IntPtr>();
    IntPtr shell = GetShellWindow();
    EnumWindows(delegate(IntPtr window, IntPtr param) {
      if (window == shell || !IsWindowVisible(window) || IsIconic(window)) return true;
      uint processId; GetWindowThreadProcessId(window, out processId);
      if (processId == excludePid) return true;
      var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
      if (name.ToString() == "Shell_TrayWnd" || name.ToString() == "Shell_SecondaryTrayWnd" || name.ToString() == "Progman" || name.ToString() == "WorkerW") return true;
      // Skip shell surfaces and owned tooltips; minimize ordinary app windows.
      if ((GetWindowLong(window, -16) & 0x00020000) == 0) return true;
      targets.Add(window); ShowWindowAsync(window, 6);
      return true;
    }, IntPtr.Zero);
    for (int i = 0; i < 12; i++) {
      bool waiting = targets.Exists(w => IsWindow(w) && IsWindowVisible(w) && !IsIconic(w));
      if (!waiting) break;
      Thread.Sleep(50);
    }
    int minimized = 0, failed = 0;
    foreach (var window in targets) {
      if (!IsWindow(window) || IsIconic(window) || !IsWindowVisible(window)) minimized++;
      else failed++;
    }
    return new Dictionary<string,int>{{"minimized",minimized},{"failed",failed}};
  }
}
