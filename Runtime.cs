using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using WxWatch;

namespace NudgeWatch
{
    public static class Views
    {
        public static Window Load(string name)
        {
            ThemeManager.EnsureResources();
            using (var stream = typeof(Views).Assembly.GetManifestResourceStream(name + ".xaml"))
                return (Window)XamlReader.Load(stream);
        }
        public static T Part<T>(this Window w, string name) where T : class { return (T)w.FindName(name); }
        public static void Click(this Window w, string name, Action action)
        { w.Part<Button>(name).Click += delegate { action(); }; }
    }

    public sealed class Frame
    {
        public List<SessionInfo> Chats;
        public bool Foreground;
        public bool ViewportDrifted, WindowVisible, WindowIconic;
        public double ScrollPercent = -1;
        public int NavTotalUnread = -1;
        public string Status;
        public IntPtr MainHwnd;
    }

    public sealed class MonitorLoop : IDisposable
    {
        readonly Dispatcher ui;
        readonly Func<Frame> read;
        readonly Action<Frame> publish;
        volatile bool stopped;
        public MonitorLoop(Dispatcher ui, Func<Frame> read, Action<Frame> publish)
        { this.ui = ui; this.read = read; this.publish = publish; }
        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "WeChat UIA" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        void Run()
        {
            while (!stopped)
            {
                Frame frame;
                try { frame = read(); }
                catch (Exception ex) { frame = new Frame { Status = "微信读取失败 · " + ex.GetType().Name }; }
                if (stopped || ui.HasShutdownStarted) return;
                try { ui.Invoke(new Action(delegate { if (!stopped) publish(frame); })); }
                catch (OperationCanceledException) { return; }
                Thread.Sleep(450);
            }
        }
        public static Func<Frame> LiveReader()
        {
            WeChatUia uia = null;
            var parser = new SessionParser();
            return delegate {
                if (uia == null) uia = new WeChatUia();
                var status = new WatchStatus();
                var rows = uia.ReadSessions(parser, status);
                return new Frame { Chats = rows, Foreground = WeChatUia.IsWeChatForeground(),
                    MainHwnd = uia.MainHwnd,
                    ScrollPercent = status.ScrollPercent, ViewportDrifted = status.ViewportDrifted, NavTotalUnread = status.NavTotalUnread,
                    WindowVisible = status.WindowVisible, WindowIconic = status.WindowIconic,
                    Status = status.State == WatchState.Ok ? "微信已连接 · " + rows.Count + " 个可见会话" : "等待微信会话列表" };
            };
        }
        public void Dispose() { stopped = true; }
    }

    public sealed class Notice
    {
        public string Title, Body, Chat, ReminderId;
        public int Level;
        public bool Task, CanConvert;
    }

    public sealed class Alerts
    {
        readonly List<Notice> queued = new List<Notice>();
        public readonly Window Window = Views.Load("Alert");
        public Notice Current { get; private set; }
        public Action<string> Navigate;
        public Action<string,long> ScheduleAction;
        public Action<string> Convert;
        public Func<long> CustomFollowUp;
        public Action QuietNow;
        public Action<int> QuietAll;
        public Action<string, int> QuietChat;
        public Action<string> PickQuietChats;
        public Func<List<string>> SuggestQuietChats;
        public bool Sound = true;
        bool enteringFollowUp, enteringDialog;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };

        public Alerts()
        {
            Window.Click("Ack", Next);
            var quietMenu = new ContextMenu();
            Window.Click("QuietNow", delegate {
                if (Current == null) return;
                quietMenu.Items.Clear();
                PopulateQuietActions(quietMenu.Items, Current.Chat);
                quietMenu.PlacementTarget = Window.Part<Button>("QuietNow");
                quietMenu.IsOpen = true;
            });
            Window.Click("Complete",delegate { Schedule(0); });
            Window.Click("Convert",delegate { if (Current != null) Convert(Current.ReminderId); });
            var menu=new ContextMenu();
            Window.Click("Snooze",delegate {
                if (Current == null) return;
                menu.Items.Clear();
                if (Current.Task) AddSchedule(menu, "次日 09:30", -1);
                foreach (int minutes in Current.Task ? new[] {30, 60} : new[] {5, 15, 30, 60})
                    AddSchedule(menu, minutes + " 分钟后", minutes);
                if (Current.Task)
                {
                    var entry = new MenuItem { Header = "自定义时间…" };
                    entry.Click += delegate {
                        var item = Current;
                        enteringFollowUp = true;
                        try {
                            var when = CustomFollowUp();
                            if (when != 0 && Current == item) Schedule(when);
                        }
                        finally { enteringFollowUp = false; }
                    };
                    menu.Items.Add(entry);
                }
                menu.PlacementTarget=Window.Part<Button>("Snooze"); menu.IsOpen=true;
            });
            Window.Click("Jump", delegate { if (Current != null && Current.Chat != null) Navigate(Current.Chat); });
            // 「⋯」更多：收纳低频操作，保证弹窗按钮不折行
            var more = new ContextMenu();
            Window.Click("More", delegate {
                if (Current == null) return;
                more.Items.Clear();
                if (Current.CanConvert)
                {
                    var convert = new MenuItem { Header = "转为任务" };
                    convert.Click += delegate { if (Current != null) Convert(Current.ReminderId); };
                    more.Items.Add(convert);
                }
                if (Current.Task && Current.ReminderId != null)
                {
                    var inProg = new MenuItem { Header = "转为进行中（停止追催）" };
                    inProg.Click += delegate { Schedule(-2); };
                    more.Items.Add(inProg);
                    var pause = new MenuItem { Header = "暂缓 / 等待中" };
                    pause.Click += delegate { Schedule(-3); };
                    more.Items.Add(pause);
                }
                if (Current.Chat == null)
                {
                    var quietSub = new MenuItem { Header = "免打扰" };
                    PopulateQuietActions(quietSub.Items, null);
                    more.Items.Add(quietSub);
                }
                if (more.Items.Count == 0) return;
                more.PlacementTarget = Window.Part<Button>("More"); more.IsOpen = true;
            });
            Window.Closing += (s,e) => { e.Cancel = true; Next(); };
            timer.Tick += delegate { if (!Window.IsMouseOver && !enteringFollowUp && !enteringDialog && !more.IsOpen && !menu.IsOpen && !quietMenu.IsOpen) {
                timer.Stop(); var current=Current;
                var fade=new System.Windows.Media.Animation.DoubleAnimation(1,0,TimeSpan.FromMilliseconds(180));
                fade.Completed+=delegate {if(Current==current)Next();}; Window.BeginAnimation(Window.OpacityProperty,fade);
            } };
        }
        void InvokeQuietAll(int minutes)
        {
            if (QuietAll != null) QuietAll(minutes);
            else if (QuietNow != null) QuietNow();
        }
        void OpenQuietPicker(string initialChat)
        {
            if (PickQuietChats == null) return;
            enteringDialog = true;
            try { PickQuietChats(initialChat); }
            finally { enteringDialog = false; }
        }
        void PopulateQuietActions(ItemCollection items, string currentChat)
        {
            if (!String.IsNullOrEmpty(currentChat))
            {
                foreach (var opt in new[] {
                    new { Label = "仅此会话免打扰 30 分钟", Minutes = 30 },
                    new { Label = "仅此会话免打扰 1 小时", Minutes = 60 },
                    new { Label = "仅此会话免打扰 2 小时", Minutes = 120 },
                    new { Label = "仅此会话今日内免打扰", Minutes = 0 }
                })
                {
                    int mins = opt.Minutes;
                    var mi = new MenuItem { Header = opt.Label };
                    mi.Click += delegate { if (QuietChat != null) QuietChat(currentChat, mins); };
                    items.Add(mi);
                }
                items.Add(new Separator());
                var pick = new MenuItem { Header = "指定其他会话免打扰…" };
                pick.Click += delegate { OpenQuietPicker(currentChat); };
                items.Add(pick);
                items.Add(new Separator());
            }
            else
            {
                var pick = new MenuItem { Header = "指定会话免打扰…" };
                pick.Click += delegate { OpenQuietPicker(null); };
                items.Add(pick);
                if (SuggestQuietChats != null)
                {
                    var suggestions = SuggestQuietChats();
                    if (suggestions != null && suggestions.Count > 0)
                    {
                        items.Add(new Separator());
                        foreach (var chat in suggestions.Take(5))
                        {
                            string target = chat;
                            var sItem = new MenuItem { Header = "仅免打扰「" + target + "」1 小时" };
                            sItem.Click += delegate { if (QuietChat != null) QuietChat(target, 60); };
                            items.Add(sItem);
                        }
                    }
                }
                items.Add(new Separator());
            }
            foreach (int minutes in new[] { 30, 60, 120 })
            {
                int mins = minutes;
                string label = minutes == 60 ? "全部消息免打扰 1 小时"
                    : minutes == 120 ? "全部消息免打扰 2 小时"
                    : "全部消息免打扰 30 分钟";
                var mi = new MenuItem { Header = label };
                mi.Click += delegate { InvokeQuietAll(mins); };
                items.Add(mi);
            }
        }
        void AddSchedule(ContextMenu menu, string title, long value)
        {
            var item = new MenuItem { Header = title };
            item.Click += delegate { Schedule(value); };
            menu.Items.Add(item);
        }
        void Schedule(long value)
        {
            if (Current == null || Current.ReminderId == null) return;
            var id = Current.ReminderId;
            Next();
            ScheduleAction(id, value);
        }
        public void Push(Notice item)
        {
            if (item.Level < 2) return;
            if (Current != null && item.Chat != null && item.Chat == Current.Chat) { Display(item); return; }
            if (item.Chat != null) queued.RemoveAll(n => n.Chat == item.Chat);
            if (Current == null) { Display(item); return; }
            if (item.Level > Current.Level)
            {
                queued.Insert(0, Current);
                Display(item);
            }
            else queued.Add(item);
        }
        async void Display(Notice item)
        {
            timer.Stop();
            Window.BeginAnimation(Window.OpacityProperty,null);
            Current = item;
            // 标题不再暴露「L3」内部分级术语；等级以胶囊文字（普通 / 重要 / 紧急）表达
            Window.Part<TextBlock>("Heading").Text = item.Title;
            Window.Part<TextBlock>("Body").Text = item.Body;
            Window.Part<TextBlock>("Body").Visibility = String.IsNullOrEmpty(item.Body) ? Visibility.Collapsed : Visibility.Visible;
            Window.Part<Border>("BodyCard").Visibility = String.IsNullOrEmpty(item.Body) ? Visibility.Collapsed : Visibility.Visible;
            Window.Part<TextBlock>("Source").Text = item.Chat != null ? (item.Level >= 3 ? "微信 · 强提醒会话" : "微信 · 新消息")
                : item.ReminderId == null ? "NudgeWatch" : item.Task ? "任务 · 待确认" : "日程提醒";
            Window.Part<TextBlock>("Meta").Text = DateTime.Now.ToString("HH:mm");
            Window.Part<Button>("Jump").Visibility = item.Chat == null ? Visibility.Collapsed : Visibility.Visible;
            Window.Part<Button>("Complete").Visibility=Window.Part<Button>("Snooze").Visibility=item.ReminderId==null?Visibility.Collapsed:Visibility.Visible;
            Window.Part<Button>("Convert").Visibility = Visibility.Collapsed;
            Window.Part<Button>("More").Visibility = (item.CanConvert || item.Task || item.ReminderId != null) ? Visibility.Visible : Visibility.Collapsed;
            Window.Part<Button>("Complete").Content = item.Task ? "确认已完成" : "完成";
            Window.Part<Button>("Snooze").Content = item.Task ? "延后追催 ▾" : "稍后 ▾";
            Window.Part<Button>("Snooze").ToolTip = item.Task ? "未完成，稍后再追催" : null;
            Window.Part<Button>("Ack").Content = item.Task ? "暂先收起" : "知道了";
            Window.Part<Button>("QuietNow").Visibility=item.Chat==null?Visibility.Collapsed:Visibility.Visible;
            bool center=item.Level>=3;
            var foreground=Native.GetForegroundWindow();
            var screen=center && foreground!=IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(foreground)
                : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            if(item.Level==4) {
                Window.Hide();
                uint pid=(uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var result=await System.Threading.Tasks.Task.Run(()=>NudgeDesktop.Minimize(pid));
                if(Current!=item)return;
                Window.Part<TextBlock>("Body").Text=item.Body+"　已收起 "+result["minimized"]+" 个窗口，失败 "+result["failed"];
            }
            Window.SizeToContent=center?SizeToContent.Manual:SizeToContent.Height;
            Window.Width=center?440:340; if(center)Window.Height=240;
            Window.ShowActivated=center;
            ThemeManager.StyleAlert(Window, item.Level);
            var hwnd=new WindowInteropHelper(Window).EnsureHandle();
            Action place=delegate {
                Native.RECT bounds;
                if(Current!=item || !Native.GetWindowRect(hwnd,out bounds))return;
                var area=screen.WorkingArea;
                int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
                int x=center?area.Left+(area.Width-width)/2:area.Right-width-12;
                int y=center?area.Top+(area.Height-height)/2:area.Bottom-height-12;
                Native.SetWindowPos(hwnd,IntPtr.Zero,x,y,0,0,Native.SWP_NOSIZE|Native.SWP_NOZORDER|Native.SWP_NOACTIVATE);
            };
            place();
            Window.Show(); Window.UpdateLayout(); place();
            var card = Window.Part<Border>("CardBorder");
            if (center) Motion.Pop(card, 0.96, 180); else Motion.Reveal(card, 12, 160);
            // A cross-monitor DPI transition can resize the HWND after the first move.
            var placement = Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,place);
            if(center) Window.Activate();
            if (Sound && item.Level >= 3) System.Media.SystemSounds.Exclamation.Play();
            if (item.Level == 2) timer.Start();
        }
        public void Next()
        {
            timer.Stop();
            Current = null;
            if (queued.Count == 0) { Window.Hide(); return; }
            var n = queued.OrderByDescending(x => x.Level).First();
            queued.Remove(n);
            Display(n);
        }
        public void ClearChats(Predicate<Notice> match)
        {
            queued.RemoveAll(match);
            if (Current != null && match(Current)) Next();
        }
    }
}

