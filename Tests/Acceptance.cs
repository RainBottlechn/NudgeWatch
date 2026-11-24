using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NudgeWatch;
using WxWatch;
using Frame = NudgeWatch.Frame;

static class Acceptance
{
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    static int checks;
    static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++;
    }
    static async Task Wait(Func<bool> accept, int ms, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!accept() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
        Check(accept(), what);
    }
    static void Click(Window w, string part)
    { w.Part<Button>(part).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    static bool Visible(Window w)
    { return w.IsVisible && IsWindowVisible(new WindowInteropHelper(w).Handle); }

    static void Units(string folder)
    {
        var start = new DateTime(2026,11,18,14,0,0);
        var r = Capture.Parse("15:00 评审 #避免1h @强 @每周",start);
        Check(r.Title == "评审" && r.Quiet && r.Duration == 60 && r.Level == 3 && r.RepeatDays == 7,"capture");
        var at = new DateTime(r.Start);
        var state = new State(); state.Reminders.Add(r);
        Check(state.QuietEnd(at.AddMinutes(2.99)) == 0,"buffer passes");
        Check(state.QuietEnd(at.AddMinutes(3)) == at.AddMinutes(63).Ticks,"inclusive start");
        Check(state.QuietEnd(at.AddMinutes(63)) == 0,"exclusive end");
        Check(state.QuietEnd(at.AddDays(7).AddMinutes(10)) > 0,"weekly inherited");
        state.TemporaryUntil = at.AddMinutes(90).Ticks;
        Check(state.QuietEnd(at.AddMinutes(4)) == state.TemporaryUntil,"union");
        state.Release(at.AddMinutes(5));
        Check(state.QuietEnd(at.AddMinutes(6)) == 0 && state.QuietEnd(at.AddDays(7).AddMinutes(4)) > 0,"release only occurrence");
        r.Start = at.AddHours(2).Ticks;
        Check(state.QuietEnd(at.AddMinutes(6)) == 0 && state.QuietEnd(at.AddHours(2).AddMinutes(6)) > 0,"reschedule");
        r.Done = true;
        Check(state.QuietEnd(at.AddHours(2).AddMinutes(6)) == 0,"cancel");
        var store = new Store(folder);
        store.Save(state); store.Save(state);
        Check(store.Load().Reminders.Single().Released == r.Released,"atomic save and roundtrip");
        File.WriteAllText(Path.Combine(folder,"state.json"),"{\"Groups\":{\"测试群\":\"工作\"}}");
        var migrated=store.Load();
        Check(migrated.Groups.Single().Name=="工作" && migrated.Groups.Single().Chats.Single()=="测试群","0.6.1 persisted group upgrade");
        store.Save(migrated);Check(store.Load().Groups.Count==1,"group list roundtrip");
        var midnight = Capture.Parse("23:55 次日 #避免30m @每天",start);
        Check(midnight.QuietEnd(at.Date.AddDays(1).AddMinutes(2)) > 0,"overnight");
        Check(Capture.Parse("15分钟后 复核",start).Start == start.AddMinutes(15).Ticks,"relative time");
        foreach(string k in RecurrenceRule.Kinds) {
            var v=k=="weekly"?"1,3":k=="monthly"?"15,31":k=="yearly"?"02-29":k=="dates"?"2026-12-01,2027-01-03":"";
            var rule=RecurrenceRule.Parse(k,v,start);
            var x=new Reminder{Start=start.Ticks,Rule=rule};long next=x.Next(start);
            Check(next>=start.Ticks,"next "+k);x.Complete(start);
            Check(k=="once"?x.Done:!x.Done && x.Start>start.Ticks,"complete "+k);
            if(k=="yearly")Check(new DateTime(next).Year==2028,"skip invalid leap dates");
        }
        bool bad = false;
        try { Capture.Parse("25:90 无效",start); } catch (FormatException) { bad = true; }
        Check(bad,"invalid time");
    }

    static async Task Desktop(string directory)
    {
        int count = 0;
        bool mta = false;
        DateTime now = DateTime.Now;
        Func<Frame> read = delegate {
            mta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
            return new Frame { Status = "测试采集已连接", Chats = new List<SessionInfo> {
                new SessionInfo { Title = "测试会话", Unread = Volatile.Read(ref count), Preview = "测试" } } };
        };
        using (var app = new Shell(directory,read,() => now))
        {
            app.Alerts.Sound = false; app.Start();
            await Wait(() => app.Panel.Part<TreeView>("Chats").Items.Count == 1,3000,"baseline");
            Check(mta,"poller MTA");
            foreach (bool hidden in new[]{false,true})
            {
                if (hidden) { app.Panel.WindowState = WindowState.Normal; app.Panel.Close(); }
                else app.Panel.WindowState = WindowState.Minimized;
                foreach (int level in new[]{2,3})
                {
                    app.State.Levels["测试会话"] = level;
                    var watch = Stopwatch.StartNew();
                    Interlocked.Increment(ref count);
                    await Wait(() => Visible(app.Ball) && app.Ball.Part<TextBlock>("Badge").Text == count.ToString()
                        && Visible(app.Alerts.Window) && app.Alerts.Current.Level == level
                        && app.Alerts.Current != null && app.Alerts.Current.Level == level,1990,"badge and alert");
                    Check(watch.ElapsedMilliseconds <= 2000,"two seconds");
                    Check(hidden ? !Visible(app.Panel) : IsIconic(new WindowInteropHelper(app.Panel).Handle),"panel stays background");
                    Console.WriteLine("{0} L{1} {2} ms",hidden ? "tray" : "minimized",level,watch.ElapsedMilliseconds);
                    Click(app.Alerts.Window,"Ack");
                }
            }
            if(app.HasHotkey) {
                keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x4E,0,0,UIntPtr.Zero);
                keybd_event(0x4E,0,2,UIntPtr.Zero);keybd_event(0x12,0,2,UIntPtr.Zero);
            } else {Console.WriteLine("Alt+N in use: testing capture opener");app.Forms.OpenQuick();}
            var quick = app.Forms.Quick;
            await Wait(() => Visible(quick),2000,"system hotkey opens capture");
            Check(quick.ActualWidth == 440 && quick.ActualHeight == 84,"capture dimensions");
            quick.Part<TextBox>("Input").Text = "10分钟后 周会 #避免1h @每周";
            quick.Activate(); quick.Part<TextBox>("Input").Focus();
            if(!app.HasHotkey) Click(quick,"Save"); else { keybd_event(0x0D,0,0,UIntPtr.Zero); keybd_event(0x0D,0,2,UIntPtr.Zero); }
            await Wait(() => !Visible(quick) && app.State.Reminders.Count == 1,2000,"enter saves");
            var meeting = app.State.Reminders.Single();
            Check(!Visible(app.Panel), "capture restored panel");
            Check(Math.Abs(app.Panel.Width - 380) < 1,"panel width " + app.Panel.Width);
            Check(meeting.Quiet, "missing quiet tag in capture " + meeting.Title);
            Check(meeting.RepeatDays == 7, "missing weekly tag in capture " + meeting.Title);
            now = new DateTime(meeting.Start).AddMinutes(1);
            app.Tick();
            Check(app.Alerts.Current.Title == meeting.Title,"schedule delivered");
            Click(app.Alerts.Window,"Ack");
            Interlocked.Increment(ref count);
            await Wait(() => Visible(app.Alerts.Window),2000,"buffer allows chat");
            now = now.AddMinutes(3); app.Tick();
            Check(!Visible(app.Alerts.Window),"quiet removes open chat popup");
            Interlocked.Increment(ref count);
            await Wait(() => app.Ball.Part<TextBlock>("Badge").Text == count.ToString(),2000,"quiet badge");
            Check(!Visible(app.Alerts.Window) && app.State.MutedChats.Count == 1,"quiet suppresses chat");
            now = new DateTime(meeting.Start).AddMinutes(63); app.Tick();
            Check(app.Alerts.Current.Title == "免打扰已结束" && app.Alerts.Current.Level == 2 && app.State.MutedChats.Count == 0,"L2 summary");
            Click(app.Alerts.Window,"Ack"); app.Tick();
            Check(!Visible(app.Alerts.Window),"summary once");
            Interlocked.Increment(ref count);
            await Wait(() => Visible(app.Alerts.Window) && app.Alerts.Current.Chat != null,2000,"first post-quiet message");
            Click(app.Panel,"Quiet");
            Check(app.State.QuietEnd(now) == now.AddHours(1).Ticks,"one click default");
            Click(app.Panel,"Quiet");
            Check(app.State.QuietEnd(now) == 0,"one click release");
            var edit = app.Forms.Editor(app.Panel,meeting);
            edit.Show();
            edit.Part<TextBox>("Delay").Text = "0";
            Click(edit,"Half"); Click(edit,"Save");
            Check(app.State.Reminders.Single().Duration == 30 && app.State.Reminders.Single().Delay == 0,"edit form presets");
        }
    }

    static async Task Live(string directory, string marker)
    {
        string version = FileVersionInfo.GetVersionInfo(@"C:\Program Files\Tencent\Weixin\Weixin.exe").FileVersion;
        Check(version == "4.1.8.107","WeChat version " + version);
        var read = MonitorLoop.LiveReader();
        DateTime now = DateTime.Now;
        int found = 0, unread = -1;
        using (var app = new Shell(directory, delegate {
            var frame = read();
            if (frame.Chats != null)
            {
                Interlocked.Exchange(ref found,frame.Chats.Count);
                frame.Chats = frame.Chats.Where(s => s.Title == "文件传输助手").ToList();
                if(frame.Chats.Count>0){int value=Math.Max(0,frame.Chats[0].Unread);
                    if(Interlocked.Exchange(ref unread,value)!=value)Console.WriteLine("live badge "+value);}
                else if (found > 0) frame.Chats.Add(new SessionInfo { Title = "基线", Unread = 0 });
            }
            return frame;
        },() => now))
        {
            app.State.Levels["文件传输助手"] = 3;
            app.Alerts.Sound = false; app.Start(); app.Panel.Hide();
            await Task.Delay(1200);
            if (Volatile.Read(ref found) == 0 && marker == "--badge")
            {
                string error = await Task.Run(() => Navigator.Go(new WeChatUia(),"文件传输助手","auto",350,delegate { }));
                if (error != null) throw new Exception(error);
            }
            await Wait(() => Volatile.Read(ref found) > 0,20000,"real WeChat session list");
            Console.WriteLine("LIVE WeChat {0}, rows {1}, panel hidden",version,Volatile.Read(ref found));
            Console.WriteLine(marker == "--badge" ? "READY for real WeChat unread transition" : "READY send " + marker + " from phone to File Transfer");
            Reminder meeting = null;
            if (marker == "--badge")
            {
                if(app.HasHotkey) {
                keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x4E,0,0,UIntPtr.Zero);
                keybd_event(0x4E,0,2,UIntPtr.Zero);keybd_event(0x12,0,2,UIntPtr.Zero);
            } else {Console.WriteLine("Alt+N in use: testing capture opener");app.Forms.OpenQuick();}
                var quick = app.Forms.Quick;
                await Wait(() => Visible(quick),2000,"live quick capture opens");
                quick.Part<TextBox>("Input").Text = "10分钟后 冒烟例会 #避免1h @每周";
                Click(quick,"Save");
                meeting = app.State.Reminders.Single();
                Check(meeting.Quiet && meeting.RepeatDays == 7 && !Visible(quick),"live meeting created");
                now = new DateTime(meeting.Start).AddMinutes(1);
                app.Tick(); Click(app.Alerts.Window,"Ack");
                await Task.Run((Action)WeChatFixture.MarkFileTransferUnread);
            }
            await Wait(() => Visible(app.Alerts.Window) && app.Alerts.Current.Chat == "文件传输助手"
                && (marker == "--badge" ? Volatile.Read(ref unread) > 0 : app.Alerts.Current.Body.Contains(marker)),marker=="--badge"?14000:600000,"waiting for incoming marker " + marker);
            Check(!Visible(app.Panel) && int.Parse(app.Ball.Part<TextBlock>("Badge").Text) > 0,"real background badge");
            app.Alerts.Window.Activate();
            await Task.Delay(150);
            var alertHwnd=new WindowInteropHelper(app.Alerts.Window).Handle;
            await Task.Run(delegate {
                var root=System.Windows.Automation.AutomationElement.FromHandle(alertHwnd);
                var click=root.FindFirst(System.Windows.Automation.TreeScope.Descendants,new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty,"Jump"));
                ((System.Windows.Automation.InvokePattern)click.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern)).Invoke();
            });
            await Wait(() => app.NavigationStatus != null,2500,"UIA invoked visible Jump button");
            try { await Wait(() => Volatile.Read(ref unread) == 0 && !Visible(app.Alerts.Window),15000,"real jump and read clear"); }
            catch { Console.WriteLine("Navigation: " + app.NavigationStatus); Console.WriteLine("Observed unread={0}, visible={1}, current={2}, status={3}", unread, Visible(app.Alerts.Window), app.Alerts.Current == null ? "none" : app.Alerts.Current.Title == "跳转未完成" ? app.Alerts.Current.Body : "chat", app.Panel.Part<TextBlock>("Status").Text); throw; }
            Console.WriteLine("PASS real " + (marker == "--badge" ? "unread transition" : "incoming marker") + ", L3, jump and read clear");
            if (meeting != null)
            {
                now = new DateTime(meeting.Start).AddMinutes(3); app.Tick();
                await Task.Run((Action)WeChatFixture.MarkFileTransferUnread);
                await Wait(() => app.State.MutedChats.Count == 1,5000,"live quiet captures real unread");
                Check(!Visible(app.Alerts.Window) && app.Ball.Part<TextBlock>("Badge").Text == "1","real quiet badge only");
                now = new DateTime(meeting.Start).AddMinutes(63); app.Tick();
                Check(Visible(app.Alerts.Window) && app.Alerts.Current.Level == 2 && app.Alerts.Current.Body.StartsWith("1 个"),"real quiet summary");
                Click(app.Alerts.Window,"Ack"); app.Tick();
                Check(!Visible(app.Alerts.Window),"real single summary");
                Click(app.Panel,"Quiet");
                Check(app.State.QuietEnd(now) == now.AddHours(1).Ticks,"live temporary quiet");
                Click(app.Panel,"Quiet");
                Check(app.State.QuietEnd(now) == 0,"live early release");
                if(app.Alerts.Current!=null) Click(app.Alerts.Window,"Ack");
                string error = await Task.Run(() => Navigator.Go(new WeChatUia(),"文件传输助手","auto",350,delegate { }));
                if(error!=null) {
                    var reader=new WeChatUia();
                    bool restored=await Task.Run(()=>reader.Discover(true,new WatchStatus()) && WeChatFixture.SetRead(reader,"文件传输助手",true,Console.WriteLine));
                    Check(restored,"restore test unread after quiet case: "+error);
                }
                await Wait(() => Volatile.Read(ref unread) == 0,5000,"real test unread restored");
                Console.WriteLine("PASS real UIA buffer, quiet badge, single summary, one-click release (controlled meeting clock)");
            }
            var archived = new MessageArchive(directory);
            await Wait(() => archived.Read(now.Date, now.Date).Any(e => e.chat == "文件传输助手"), 4000, "real WeChat unread captured and archived");
        }
    }
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static async Task WeChatViewport(string directory)
    {
        var uia = new WeChatUia();
        var status = new WatchStatus();
        Check(await Task.Run(() => uia.Discover(true, status)), "discover real WeChat");
        var hwnd = uia.MainHwnd;
        var parser = new SessionParser();
        using (var app = new Shell(directory, MonitorLoop.LiveReader(), () => DateTime.Now))
        {
            app.State.DefaultLevel = 1;
            app.Alerts.Sound = false;
            app.Start();
            await Wait(() => app.Panel.Part<TextBlock>("Status").Text.Contains("已连接"), 8000, "live session rows");
            Check(new KnownChats(directory).All.Any(), "real visible sessions stored in known_chats");
            Check(await Task.Run(() => Navigator.WithSessionList(hwnd, reader => reader.ManualTop() != null)), "real manual top");
            var top = await Task.Run(() => uia.ReadSessions(parser, new WatchStatus()));
            var shifted = await Task.Run(() => Navigator.WithSessionList(hwnd, reader => {
                if (!reader.NextPage()) return false;
                Thread.Sleep(400);
                var next = reader.ReadSessions(parser, new WatchStatus());
                Console.WriteLine("Scan sample visible={0}, new={1}", next == null ? 0 : next.Count,
                    next == null ? 0 : next.Select(r => r.Title).Except(top.Select(r => r.Title)).Count());
                return next != null && next.Select(r => r.Title).Except(top.Select(r => r.Title)).Any();
            }));
            Check(shifted, "scan step discovers real names outside initial viewport");
            if (top.Take(3).Any(r => r.IsPinned))
                await Wait(() => app.Panel.Part<TextBlock>("Status").Text.Contains("未回顶"), 6000, "real drift status from pinned anchors");
            Click(app.Panel, "ReturnWechatTop");
            await Wait(() => !app.Panel.Part<TextBlock>("Status").Text.Contains("未回顶") && app.NavigationStatus == null, 6500, "manual top from panel");
            await Task.Delay(1100);
            var after = await Task.Run(() => uia.ReadSessions(parser, new WatchStatus()));
            Check(after != null && after.Take(3).Select(r => r.Title).SequenceEqual(top.Take(3).Select(r => r.Title)), "manual top returns original head");
            var fail = await Task.Run(() => Navigator.Go(new WeChatUia(), "NudgeWatch_验收无匹配_070", "search", 550, delegate {}));
            Check(fail == SearchResults.NeedsConfirmation, "unmatched search remains for confirmation");
            string step = "";
            var error = await Task.Run(() => Navigator.Go(new WeChatUia(), "文件传输助手", "search", 550, value => step = value));
            Check(error == null && step.Contains("唯一精确"), "exact real search result opens: " + error);
            Console.WriteLine("PASS real WeChat pool, viewport scan step, manual top, exact search, and no-match rejection");
        }
    }
    static async Task WorkDesktop(string directory)
    {
        var now = DateTime.Now;
        using (var shell = new Shell(directory, () => new Frame { Status = "桌面验收" }, () => now))
        {
            shell.Start();
            var r = Capture.Parse("30分钟后 桌面撤销 @每天", now);
            shell.SaveReminder(r);
            long start = r.Start;
            var list = shell.Panel.Part<ListBox>("Agenda");
            list.SelectedIndex = 0;
            Click(shell.Panel, "Done");
            var undo = shell.Panel.Part<DockPanel>("UndoBar");
            Check(undo.Visibility == Visibility.Visible && r.Start != start, "completion shows five-second undo");
            var elapsed = Stopwatch.StartNew();
            await Task.Delay(4200);
            Check(undo.Visibility == Visibility.Visible, "undo available during five seconds");
            await Wait(() => undo.Visibility == Visibility.Collapsed, 2300, "undo hides automatically");
            shell.Panel.Activate();
            list.Focus();
            keybd_event(0x11,0,0,UIntPtr.Zero); keybd_event(0x5A,0,0,UIntPtr.Zero);
            keybd_event(0x5A,0,2,UIntPtr.Zero); keybd_event(0x11,0,2,UIntPtr.Zero);
            await Wait(() => shell.State.Reminders.Single().Start == start, 2000, "Ctrl+Z restores recurring reminder after toast hides");
            Console.WriteLine("PASS undo visibility and Ctrl+Z, observed {0} ms", elapsed.ElapsedMilliseconds);
        }
    }

    static async Task ArchiveDesktop(string directory)
    {
        var now = DateTime.Now;
        var groups = new Dictionary<string, string> { { "验收群一", "工作" }, { "验收群二", "工作" }, { "验收群三", "其他" } };
        using (var shell = new Shell(directory, () => new Frame(), () => now))
        {
            shell.Alerts.Sound = false;
            shell.State.Groups.Add(new GroupDef { Name = "工作", Chats = groups.Where(p => p.Value == "工作").Select(p => p.Key).ToList() });
            Func<int, int, int, Frame> sample = (a,b,c) => new Frame { Chats = new List<SessionInfo> {
                new SessionInfo { Title = "验收群一", Unread = a, Preview = "日常摘要" },
                new SessionInfo { Title = "验收群二", Unread = b, Preview = "待确认 @我" },
                new SessionInfo { Title = "验收群三", Unread = c, Preview = "另一组记录" }
            }};
            var apply = typeof(Shell).GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            apply.Invoke(shell, new object[] { sample(0, 0, 0) });
            apply.Invoke(shell, new object[] { sample(1, 0, 0) });
            shell.State.TemporaryUntil = now.AddHours(1).Ticks;
            apply.Invoke(shell, new object[] { sample(1, 1, 1) });
            Check(shell.Alerts.Current == null, "quiet events archive with no popup");
        }
        var archive = new MessageArchive(directory);
        var records = archive.Read(now.Date, now.Date);
        Check(records.Count == 3 && records.Count(r => r.muted) == 2, "WPF pipeline archives normal and quiet injected frames");
        var owner = new Window { Title = "NudgeWatch 归档验收", Width = 420, Height = 250 };
        owner.Show();
        var viewer = new ArchiveWindow(owner, archive, now, groups, groups.Where(p => p.Value == "工作").Select(p => p.Key));
        var window = viewer.View;
        window.Show();
        IDataObject prior = null;
        bool copied = false;
        try
        {
            var matched = window.Part<TextBlock>("Matched");
            await Wait(() => matched.Text == "2 条", 3500, "archive group preselected");
            window.Part<TextBox>("Keyword").Text = "待确认";
            window.Part<ComboBox>("Importance").SelectedIndex = 2;
            Check(matched.Text == "1 条" && window.Part<TextBox>("Preview").Text.Contains("待确认"), "archive filters and preview");
            prior = Clipboard.GetDataObject();
            Click(window, "Copy");
            copied = matched.Text == "已复制 1 条";
            Check(copied && Clipboard.GetText().Contains("待确认"), "archive button copies to Windows clipboard");
            window.Part<TextBox>("Keyword").Clear();
            window.Part<ComboBox>("Importance").SelectedIndex = 0;
            Click(window, "SelectAll");
            Check(matched.Text == "3 条", "archive select all chats");
            Click(window, "Invert");
            Check(matched.Text == "0 条", "archive invert selection");
            foreach (string theme in ThemeManager.Keys)
            {
                ThemeManager.Apply(theme);
                Check(Object.ReferenceEquals(window.Background, Application.Current.Resources["BgBrush"]), "archive follows theme " + theme);
            }
            var formats = new[] { "md", "csv", "txt" };
            for (int i = 0; i < formats.Length; i++)
            {
                string path = Path.Combine(directory, "archive-test." + formats[i]);
                ArchiveWindow.WriteExport(path, records, i, true);
                Check(File.ReadAllText(path).Contains("待确认"), formats[i] + " export file includes selected content");
            }
            Console.WriteLine("PASS archive preview, selection, clipboard, three file formats and seven themes");
        }
        finally
        {
            if (copied) { if (prior == null) Clipboard.Clear(); else Clipboard.SetDataObject(prior, true); }
            window.Close(); owner.Close();
        }
    }

    static async Task TestTray(string directory)
    {
        var uia=new WeChatUia();var st=new WatchStatus();
        Check(await Task.Run(()=>uia.Discover(true,st)),"discover WeChat");
        var h=uia.MainHwnd;
        using(var app=new Shell(directory,MonitorLoop.LiveReader(),()=>DateTime.Now)){
            app.Start();app.Panel.Hide();
            await Wait(()=>app.Panel.Part<TextBlock>("Status").Text.Contains("已连接"),8000,"connected");
            try{
                Check(PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero),"close WeChat to tray");
                await Wait(()=>((System.Windows.Media.SolidColorBrush)app.Ball.Part<Border>("Surface").Background).Color==System.Windows.Media.Colors.Firebrick,14000,"three misses and red ball");
                Check(!IsWindowVisible(h),"actual WeChat window hidden");
                app.Ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
                await Wait(()=>app.Panel.Part<TextBlock>("Status").Text.Contains("已连接"),12000,"ball restores WeChat and reconnects");
            }
            finally
            {
                if (!IsWindowVisible(h))
                {
                    var error = Task.Run(() => Navigator.WakeWeChat(h, new WeChatUia(), null)).GetAwaiter().GetResult();
                    if (error != null) Console.WriteLine("托盘验收后恢复微信：" + error);
                }
            }
        }
    }
    static async Task Cover(string directory)
    {
        using(var proc=Process.Start(new ProcessStartInfo(typeof(Acceptance).Assembly.Location,"cover-window"){UseShellExecute=false,CreateNoWindow=true}))
        try{
            await Wait(()=>{proc.Refresh();return proc.MainWindowHandle!=IntPtr.Zero;},6000,"external cover window");
            int count=0;
            using(var app=new Shell(directory,()=>new Frame{Chats=new List<SessionInfo>{new SessionInfo{Title="前景覆盖测试",Unread=Volatile.Read(ref count)}}},()=>DateTime.Now)){
                app.Start();app.Panel.Hide();await Task.Delay(650);
                foreach(int lv in new[]{2,3}){
                    app.State.DefaultLevel=lv;
                    Check(Navigator.ForceForeground(proc.MainWindowHandle),"external foreground");
                    Interlocked.Increment(ref count);
                    await Wait(()=>Visible(app.Alerts.Window) && app.Alerts.Current.Level==lv,2000,"foreground covered alert");
                    var heading=app.Alerts.Window.Part<TextBlock>("Heading");
                    var p=heading.PointToScreen(new Point(5,5));
                    var hit=System.Windows.Automation.AutomationElement.FromPoint(p);
                    Check(hit.Current.ProcessId==Process.GetCurrentProcess().Id,"alert above external cover");
                    Check(!Visible(app.Panel),"panel stays hidden");
                    Console.WriteLine("PASS foreground cover L"+lv+" foreground="+(GetForegroundWindow()==new WindowInteropHelper(app.Alerts.Window).Handle?"alert":"other"));
                    Click(app.Alerts.Window,"Ack");
                }
            }
        }finally{if(!proc.HasExited)proc.Kill();}
    }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Bounds bounds);
    [StructLayout(LayoutKind.Sequential)] struct Bounds { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    static async Task Center()
    {
        var foreground=new Window {Width=240,Height=130,Title="居中验收 · 活动窗口"};
        foreground.Show();
        var hwnd=new WindowInteropHelper(foreground).Handle;
        var alerts=new Alerts {Sound=false};
        try {
            foreach(var screen in System.Windows.Forms.Screen.AllScreens) {
                var area=screen.WorkingArea;
                foreach(bool reuse in new[]{false,true}) {
                    if(reuse)alerts.Push(new Notice{Level=2,Title="L2"});
                    Check(SetWindowPos(hwnd,IntPtr.Zero,area.Left+20,area.Top+20,0,0,1),"position foreground");
                    foreground.Activate();
                    Check(Navigator.ForceForeground(hwnd),"select test monitor");
                    alerts.Push(new Notice {Level=3,Title="L3 居中验收"});
                    await Task.Delay(350);
                    Bounds b;
                    Check(GetWindowRect(new WindowInteropHelper(alerts.Window).Handle,out b),"actual alert bounds");
                    double dx=(b.Left+b.Right-area.Left-area.Right)/2.0;
                    double dy=(b.Top+b.Bottom-area.Top-area.Bottom)/2.0;
                    Console.WriteLine("{0} {1} offset=({2},{3}) px",screen.DeviceName,reuse?"L2->L3":"L3",dx,dy);
                    Check(Math.Abs(dx)<=1 && Math.Abs(dy)<=1,"L3 centered on active work area");
                    Check(alerts.Window.ActualWidth==440 && alerts.Window.ActualHeight==240,"440 x 240 DIP");
                    alerts.Next(); if(reuse)alerts.Next();
                }
            }
        } finally { foreground.Close(); }
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length==1 && args[0]=="cover-window"){
            var w=new Window{Title="NudgeWatch 全屏覆盖验收",WindowStyle=WindowStyle.None,WindowState=WindowState.Maximized};
            return new Application().Run(w);
        }
        string root = Path.Combine(Path.GetTempPath(),"NudgeWatch-accept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int result = 0;
        try
        {
            Units(Path.Combine(root,"unit"));
            if (args.Length == 1 && args[0] == "unit") { Console.WriteLine("PASS " + checks + " unit checks"); return 0; }
            var loop = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            loop.Startup += async delegate {
                try {
                    if (args.Length == 2 && args[0] == "live") await Live(Path.Combine(root,"live"),args[1]);
                    else if (args.Length == 1 && args[0] == "viewport") await WeChatViewport(Path.Combine(root,"viewport"));
                    else if (args.Length == 1 && args[0] == "work-items") await WorkDesktop(Path.Combine(root,"work-items"));
                    else if (args.Length == 1 && args[0] == "archive") await ArchiveDesktop(Path.Combine(root,"archive"));
                    else if(args.Length==1 && args[0]=="tray")await TestTray(Path.Combine(root,"tray"));
                    else if(args.Length==1 && args[0]=="center")await Center();
                    else if(args.Length==1 && args[0]=="cover")await Cover(Path.Combine(root,"cover"));
                    else await Desktop(Path.Combine(root,"desktop"));
                    Console.WriteLine("PASS " + checks + " checks");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
                finally { WeChatFixture.Restore(); loop.Shutdown(); }
            };
            loop.Run();
        }
        finally { Directory.Delete(root,true); }
        return result;
    }
}










