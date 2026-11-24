using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FormsTray = System.Windows.Forms;
using WxWatch;
[assembly: AssemblyVersion("0.8.0.0")]
[assembly: AssemblyFileVersion("0.8.0.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Acceptance")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PrototypeTests")]

namespace NudgeWatch
{
    public sealed class AgendaRow
    {
        public Reminder Item;
        /// <summary>完整文字（读屏、导出与兼容旧逻辑用）。</summary>
        public string Label { get; set; }
        public string TimeText { get; set; }
        public string DayText { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public List<string> Tags { get; set; }
        public int Level { get; set; }
        public bool Emphasis { get; set; }
        public bool Inactive { get; set; }
        public bool HasTags { get { return Status != null || (Tags != null && Tags.Count > 0); } }
        public override string ToString() { return Label ?? ""; }
    }

    public sealed partial class Shell : IDisposable
    {
        public readonly Window Panel = Views.Load("Panel"), Ball = Views.Load("Ball");
        public readonly Alerts Alerts = new Alerts();
        public readonly Forms Forms;
        public readonly State State;
        readonly Store store;
        readonly MessageArchive archive;
        readonly ChangeDetector detector = new ChangeDetector();
        readonly Rules rules = new Rules();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly MonitorLoop monitor;
        readonly Func<DateTime> clock;
        readonly FormsTray.NotifyIcon tray;
        List<SessionInfo> rows = new List<SessionInfo>();
        string connection = "正在连接微信", treeKey, agendaMinute, hotkeyError = "";
        long lastQuietEnd;
        bool navigating, exiting;
        IntPtr hwnd, lastWeChatHwnd;
        public bool HasHotkey { get; private set; }
        public string NavigationStatus { get; private set; }

        public Shell(string directory, Func<Frame> read, Func<DateTime> clock)
        {
            this.clock = clock;
            store = new Store(directory);
            State = store.Load(clock());
            knownChats = new KnownChats(directory);
            archive = new MessageArchive(directory);
            Forms = new Forms(SaveReminder);
            SetupWorkItems();
            Alerts.Navigate = Jump;
            Alerts.QuietNow = delegate { QuietAllChats(60); };
            Alerts.QuietAll = QuietAllChats;
            Alerts.QuietChat = delegate(string chat, int minutes) { QuietChats(new[] { chat }, minutes); };
            Alerts.PickQuietChats = PickQuietChats;
            Alerts.SuggestQuietChats = SuggestQuietChats;
            Panel.Click("New", delegate {
                bool inTasks = Panel.Part<TabControl>("Tabs").SelectedItem == Panel.Part<TabItem>("TasksTab");
                if (inTasks)
                {
                    var branch = TaskBranch();
                    Forms.Editor(Panel, new Reminder { Kind = "task", GroupId = branch == null ? "" : branch.Id }).ShowDialog();
                }
                else Forms.Editor(Panel, null).ShowDialog();
            });
            var newMenu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            MenuItem(newMenu, "快速新建（Alt+N）", Forms.OpenQuick);
            newMenu.Items.Add(new Separator());
            MenuItem(newMenu, "新建提醒…", delegate { Forms.Editor(Panel, null).ShowDialog(); });
            MenuItem(newMenu, "新建任务…", delegate {
                var branch = TaskBranch();
                Forms.Editor(Panel, new Reminder { Kind = "task", GroupId = branch == null ? "" : branch.Id }).ShowDialog();
            });
            Panel.Click("NewMenu", delegate { newMenu.PlacementTarget = Panel.Part<Border>("NewSplit"); newMenu.IsOpen = true; });
            Panel.Click("Quiet", delegate { ToggleQuiet(); });
            var menu = QuietMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.Opened += delegate { menu.Items.Clear(); PopulateQuietMenuItems(menu.Items); };
            Panel.Click("QuietMenu", delegate { menu.PlacementTarget = Panel.Part<Border>("QuietSplit"); menu.IsOpen = true; });
            var mode = Panel.Part<ComboBox>("Default");
            mode.ItemsSource = new[] {"默认弱提醒","默认强提醒"};
            mode.SelectedIndex = State.DefaultLevel == 3 ? 1 : 0;
            mode.SelectionChanged += delegate { State.DefaultLevel = mode.SelectedIndex + 2; Persist(); };
            Panel.Closing += (s,e) => { if (!exiting) { e.Cancel = true; HidePanelToTray(); } };
            Panel.IsVisibleChanged += delegate { UpdateBallToolTip(); };
            Panel.StateChanged += delegate { UpdateBallToolTip(); };
            Panel.SourceInitialized += delegate {
                hwnd = new WindowInteropHelper(Panel).Handle;
                HwndSource.FromHwnd(hwnd).AddHook(Hotkey);
                HasHotkey = Native.RegisterHotKey(hwnd, 1, 0x4001, 0x4E);
                if (!Native.RegisterHotKey(hwnd,2,0x4001,0x57)) hotkeyError=" · Alt+W 已被占用";
                if (!HasHotkey) hotkeyError = " · Alt+N 已被占用，可点「新建 ▾」快速新建";
            };
            Ball.Left = SystemParameters.WorkArea.Right - Ball.Width;
            Ball.Top = SystemParameters.WorkArea.Top + 120;
            Ball.ContextMenu = new ContextMenu();
            Ball.Click("WeChatToggle", ToggleWeChatWindow);
            Point down = new Point(); bool dragged = false;
            Ball.MouseLeftButtonDown += (s,e) => {
                if (IsInsideButton(e.OriginalSource as DependencyObject, Ball)) return;
                down = e.GetPosition(Ball); dragged = false;
            };
            Ball.MouseMove += (s,e) => {
                if (IsInsideButton(e.OriginalSource as DependencyObject, Ball)) return;
                if (e.LeftButton != MouseButtonState.Pressed || dragged || (e.GetPosition(Ball)-down).Length < 5) return;
                dragged = true; Ball.DragMove();
                var area = SystemParameters.WorkArea;
                Ball.Left = Ball.Left < area.Left + area.Width / 2 ? area.Left : area.Right - Ball.Width;
                Ball.Top = Math.Max(area.Top, Math.Min(Ball.Top, area.Bottom - Ball.Height));
            };
            Ball.MouseLeftButtonUp += (s, e) => {
                if (IsInsideButton(e.OriginalSource as DependencyObject, Ball)) return;
                if (dragged) return;
                if (lost) {WakeWeChat(); return;}
                if (Alerts.Current != null && Alerts.Current.Chat != null) Jump(Alerts.Current.Chat);
                else TogglePanel();
            };
            Ball.MouseUp += (s, e) => {
                if (e.ChangedButton == MouseButton.Middle) ToggleWeChatWindow();
            };
            tray = new FormsTray.NotifyIcon { Text = "NudgeWatch 0.8.0", Icon = BrandArt.TrayIcon(BrandArt.TrayState.Normal) ?? System.Drawing.Icon.ExtractAssociatedIcon(typeof(Shell).Assembly.Location), Visible = true };
            var trayMenu = new FormsTray.ContextMenuStrip();
            trayMenu.Items.Add("打开主面板", null, delegate { ShowPanel(); });
            trayMenu.Items.Add("快速建提醒", null, delegate { Forms.OpenQuick(); });
            trayMenu.Items.Add("收起微信（最小化保活）", null, delegate { MinimizeWeChat(); });
            trayMenu.Items.Add("一键将微信列表回顶", null, delegate { ReturnWechatTop(); });
            trayMenu.Items.Add("退出", null, delegate { exiting = true; Application.Current.Shutdown(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowPanel(); };
            SetupExtras();
            monitor = new MonitorLoop(Panel.Dispatcher, read, Apply);
            timer.Tick += delegate { Tick(); };
        }

        IntPtr Hotkey(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x312 && w.ToInt32() == 1) { Forms.OpenQuick(); handled = true; }
            if(msg==0x312 && w.ToInt32()==2){ShowPanel(); handled=true;}
            return IntPtr.Zero;
        }
        public void Start()
        {
            Panel.Show(); Ball.Show(); RefreshAgenda(); Tick(); timer.Start(); monitor.Start();
        }
        public bool IsPanelOpen() { return Panel.IsVisible && Panel.WindowState != WindowState.Minimized; }
        public void ShowPanel() { Panel.Show(); Panel.WindowState = WindowState.Normal; Panel.Activate(); UpdateBallToolTip(); }
        public void HidePanelToTray() { Panel.Hide(); UpdateBallToolTip(); }
        public void TogglePanel()
        {
            if (IsPanelOpen())
            {
                foreach (Window owned in Panel.OwnedWindows)
                {
                    if (owned.IsVisible) { owned.Activate(); return; }
                }
                HidePanelToTray();
            }
            else ShowPanel();
        }
        void Persist() { store.Save(State); }
        public void SaveReminder(Reminder item)
        {
            if (String.IsNullOrEmpty(item.Status)) item.Status = item.Done ? "completed" : "active";
            int i = State.Reminders.FindIndex(r => r.Id == item.Id);
            if (i < 0) State.Reminders.Add(item); else State.Reminders[i] = item;
            if (i < 0 && String.IsNullOrEmpty(item.LinkedId)) SelectWorkTab(item.IsTask);
            Changed();
        }
        void Changed() { Persist(); RefreshAgenda(); Tick(); }
        public void ToggleQuiet()
        {
            if (State.QuietEnd(clock()) > 0) State.Release(clock());
            else State.TemporaryUntil = clock().AddHours(1).Ticks;
            Changed();
        }
        ContextMenu QuietMenu()
        {
            var menu = new ContextMenu();
            PopulateQuietMenuItems(menu.Items);
            return menu;
        }
        void PopulateQuietMenuItems(ItemCollection items)
        {
            foreach (int minutes in new[] { 30, 60, 120 })
            {
                int duration = minutes;
                var mi = new MenuItem { Header = "立即免打扰 " + minutes + " 分钟" };
                mi.Click += delegate { QuietAllChats(duration); };
                items.Add(mi);
            }
            var pick = new MenuItem { Header = "指定会话免打扰…" };
            pick.Click += delegate { PickQuietChats(null); };
            items.Add(pick);
            var now = clock();
            var activeChats = State.ActiveQuietChats(now);
            if (activeChats.Count > 0)
            {
                items.Add(new Separator());
                foreach (var chat in activeChats)
                {
                    string target = chat;
                    long endTicks = State.ChatQuietEnd(target, now);
                    var rel = new MenuItem { Header = "解除「" + target + "」免打扰（至 " + new DateTime(endTicks).ToString("HH:mm") + "）" };
                    rel.Click += delegate { ReleaseChatQuiet(target); };
                    items.Add(rel);
                }
                if (activeChats.Count > 1)
                {
                    var relAll = new MenuItem { Header = "解除全部指定会话免打扰" };
                    relAll.Click += delegate { ReleaseAllChatQuiets(); };
                    items.Add(relAll);
                }
            }
            items.Add(new Separator());
            var release = new MenuItem { Header = "提前解除" };
            release.Click += delegate { State.Release(clock()); Changed(); };
            items.Add(release);
        }
        string FormatQuietStatus(DateTime now, long until)
        {
            if (until > 0) return " · 免打扰至 " + new DateTime(until).ToString("HH:mm");
            var activeChats = State.ActiveQuietChats(now);
            if (activeChats.Count == 1)
                return " · 「" + activeChats[0] + "」免打扰至 " + new DateTime(State.ChatQuietEnd(activeChats[0], now)).ToString("HH:mm");
            if (activeChats.Count > 1)
                return " · " + activeChats.Count + " 个会话免打扰中";
            return "";
        }
        void UpdateBallToolTip()
        {
            var now = clock();
            string quiet = FormatQuietStatus(now, State.QuietEnd(now));
            string drift = !lost && viewportDrifted ? ViewportWarning : "";
            string panelHint = IsPanelOpen() ? " · 左键收入托盘" : " · 左键打开主面板";
            Ball.ToolTip = (lost ? "微信已进托盘并暂停提醒 · 左键唤醒，建议平时用「—」最小化" : connection + panelHint) + quiet + drift;
        }
        static void MenuItem(ContextMenu menu, string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }
        static string RuleLabel(int level)
        {
            if (level == 3) return " · 强提醒";
            if (level == 2) return " · 弱提醒";
            if (level == 1) return " · 仅记录";
            return " · 忽略";
        }
        /// <summary>微信会话行：标题 + 备注（灰），右侧为快捷归组胶囊（动态视图中）、规则胶囊与未读角标。</summary>
        FrameworkElement ChatLeafView(string title, KnownChat entry, int level, bool custom, int unread, bool showGroupPill)
        {
            var right = new List<UIElement>();
            long chatQuietEnd = State.ChatQuietEnd(title, clock());
            if (chatQuietEnd > 0 && !chatMultiMode)
            {
                string currentTitle = title;
                string endStr = new DateTime(chatQuietEnd).ToString("HH:mm");
                right.Add(Rows.PillButton("☾ " + endStr, "SubCardBrush", "QuietBrush",
                    "免打扰至 " + endStr + "，点击解除或调整",
                    btn => OpenChatMenu(BuildChatQuietMenu(currentTitle), btn)));
            }
            if (showGroupPill && !chatMultiMode)
            {
                string assigned = CustomGroup(title);
                string pillText = assigned.Length > 0 ? assigned + " ▾" : "归组 ▾";
                string bgKey = assigned.Length > 0 ? "SubCardBrush" : "AccentSoftBrush";
                string fgKey = assigned.Length > 0 ? "MutedBrush" : "AccentBrush";
                string tip = assigned.Length > 0 ? "已加入「" + assigned + "」，点击切换或移出分组" : "将该会话加入分组";
                string currentTitle = title;
                right.Add(Rows.PillButton(pillText, bgKey, fgKey, tip, btn => OpenChatMenu(BuildChatMoveMenu(new[] { currentTitle }), btn)));
            }
            bool strong = custom && level == 3;
            if (custom) right.Add(strong ? Rows.Pill("强提醒", "L3SoftBrush", "L3Brush") : Rows.Pill(RuleLabel(level).Substring(3), null, null));
            if (unread > 0) right.Add(Rows.Badge(unread, strong ? "L3Brush" : "L2Brush", strong ? "OnL3Brush" : "OnL2Brush"));
            string alias = entry == null || String.IsNullOrWhiteSpace(entry.Alias) ? null : entry.Alias;
            return Rows.Leaf(title, alias, unread > 0, right);
        }
        bool chatMultiMode, archivedUngroupedExpanded, buildingChatTree, chatMenuOpen, pendingTreeRefresh;
        readonly HashSet<string> selectedChats = new HashSet<string>(StringComparer.Ordinal);
        string lastClickedChat, chatDragTitle, chatActionNote;
        Point chatDragStart;
        DateTime chatActionNoteUntil;

        void SetChatActionNote(string note)
        {
            chatActionNote = note;
            chatActionNoteUntil = DateTime.UtcNow.AddSeconds(4);
            Tick();
        }

        void ClearChatSelection()
        {
            chatMultiMode = false;
            selectedChats.Clear();
            var tree = Panel.Part<TreeView>("Chats");
            var sel = tree.SelectedItem as TreeViewItem;
            if (sel != null) sel.IsSelected = false;
            UpdateChatMultiButton();
            TreeChanged();
        }

        void UpdateChatMultiButton()
        {
            Panel.Part<Button>("ChatMulti").Content = chatMultiMode
                ? (selectedChats.Count > 0 ? "完成多选 (" + selectedChats.Count + ")" : "完成多选")
                : "多选";
        }

        void UpdateChatExpandButton()
        {
            var tree = Panel.Part<TreeView>("Chats");
            var branches = tree.Items.OfType<TreeViewItem>().ToList();
            bool anyExpanded = branches.Any(b => b.IsExpanded);
            Panel.Part<Button>("ToggleChatExpand").Content = anyExpanded ? "全折" : "全展";
        }

        void ToggleAllChatGroups()
        {
            var tree = Panel.Part<TreeView>("Chats");
            var branches = tree.Items.OfType<TreeViewItem>().ToList();
            bool collapse = branches.Any(b => b.IsExpanded);
            State.CollapsedChatGroups.Clear();
            foreach (var b in branches)
            {
                var def = b.Tag as GroupDef;
                if (def == null) continue;
                if (def.Name == ArchivedUngroupedGroup) archivedUngroupedExpanded = !collapse;
                else if (collapse) State.CollapsedChatGroups.Add(def.Name);
            }
            Persist();
            TreeChanged();
        }

        void PopulateChatMoveItems(ItemCollection items, List<string> list)
        {
            foreach (var g in State.Groups)
            {
                string dest = g.Name;
                bool allIn = list.Count > 0 && list.All(t => CustomGroup(t) == dest);
                var item = new MenuItem { Header = dest, IsChecked = allIn };
                item.Click += delegate { AssignChats(list, dest); };
                items.Add(item);
            }
            if (list.Any(t => CustomGroup(t).Length > 0))
            {
                if (items.Count > 0) items.Add(new Separator());
                var unassign = new MenuItem { Header = "移出当前分组（取消归组）" };
                unassign.Click += delegate { RemoveChatsFromGroups(list); };
                items.Add(unassign);
            }
            if (items.Count > 0) items.Add(new Separator());
            var create = new MenuItem { Header = "＋ 新建分组…" };
            create.Click += delegate {
                string n = Prompt("新分组名称", "");
                if (!String.IsNullOrWhiteSpace(n) && !IsSystemChatGroup(n.Trim())) AssignChats(list, n.Trim());
            };
            items.Add(create);
        }

        ContextMenu BuildChatQuietMenu(string title)
        {
            var menu = new ContextMenu();
            PopulateChatQuietMenuItems(menu.Items, new List<string> { title });
            return menu;
        }

        void PopulateChatQuietMenuItems(ItemCollection items, List<string> targets)
        {
            DateTime now = clock();
            if (targets.Any(t => State.ChatQuietEnd(t, now) > 0))
            {
                var release = new MenuItem { Header = targets.Count > 1 ? "解除已选会话免打扰" : "解除免打扰" };
                release.Click += delegate { ReleaseChatsQuiet(targets); };
                items.Add(release);
                items.Add(new Separator());
            }
            foreach (var opt in new[] {
                new { Label = "免打扰 30 分钟", Minutes = 30 },
                new { Label = "免打扰 1 小时", Minutes = 60 },
                new { Label = "免打扰 2 小时", Minutes = 120 },
                new { Label = "今日内免打扰（至 24:00）", Minutes = 0 }
            })
            {
                int mins = opt.Minutes;
                var mi = new MenuItem { Header = opt.Label };
                mi.Click += delegate { QuietChats(targets, mins); };
                items.Add(mi);
            }
        }

        ContextMenu BuildChatMoveMenu(IEnumerable<string> targets)
        {
            var list = targets.Where(t => !String.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            var menu = new ContextMenu();
            PopulateChatMoveItems(menu.Items, list);
            return menu;
        }

        void OpenChatMenu(ContextMenu menu, UIElement target)
        {
            chatMenuOpen = true;
            menu.Closed += delegate { OnChatMenuClosed(); };
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        void OnChatMenuClosed()
        {
            if (!chatMenuOpen) return;
            chatMenuOpen = false;
            if (pendingTreeRefresh)
            {
                pendingTreeRefresh = false;
                treeKey = null;
                RefreshTree();
            }
        }

        void OnChatsContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var tree = Panel.Part<TreeView>("Chats");
            var menu = tree.ContextMenu;
            menu.Items.Clear();
            var item = tree.SelectedItem as TreeViewItem;
            if (item == null) { e.Handled = true; return; }
            string title = item.Tag as string;
            if (!String.IsNullOrEmpty(title)) FillChatLeafMenu(menu, title);
            else
            {
                var group = item.Tag as GroupDef;
                if (group != null) FillChatBranchMenu(menu, group);
            }
            if (menu.Items.Count == 0) { e.Handled = true; return; }
            chatMenuOpen = true;
        }

        void FillChatBranchMenu(ContextMenu menu, GroupDef group)
        {
            var titles = group.Chats.Distinct(StringComparer.Ordinal).ToList();
            MenuItem(menu, "导出该组记录", delegate { ShowArchiveSelection(titles); });
            if (titles.Count > 0)
            {
                MenuItem(menu, "多选该组全部", delegate {
                    foreach (var m in titles) selectedChats.Add(m);
                    UpdateChatMultiButton();
                    TreeChanged();
                });
            }
            if (chatMultiMode || selectedChats.Count > 0)
            {
                menu.Items.Add(new Separator());
                MenuItem(menu, "取消多选", ClearChatSelection);
            }
        }

        void FillChatLeafMenu(ContextMenu menu, string title)
        {
            var entryData = knownChats.Find(title);
            var batchTargets = selectedChats.Count > 1 && selectedChats.Contains(title) ? selectedChats.ToList() : new List<string> { title };
            MenuItem(menu, "打开会话", delegate { Jump(title); });
            var moveSub = new MenuItem { Header = batchTargets.Count > 1 ? "将已选 " + batchTargets.Count + " 项加入分组" : "加入分组" };
            PopulateChatMoveItems(moveSub.Items, batchTargets);
            menu.Items.Add(moveSub);
            MenuItem(menu, batchTargets.Count > 1 ? "导出已选会话记录" : "导出该会话记录", delegate { ShowArchiveSelection(batchTargets); });
            MenuItem(menu, "编辑会话备注…", delegate {
                var note = Prompt("会话备注", entryData == null ? "" : entryData.Alias ?? "");
                if (note != null) { knownChats.Annotate(title, note, Group(title), clock()); TreeChanged(); }
            });
            var quietSub = new MenuItem { Header = batchTargets.Count > 1 ? "临时免打扰（已选 " + batchTargets.Count + " 项）" : "临时免打扰" };
            PopulateChatQuietMenuItems(quietSub.Items, batchTargets);
            menu.Items.Add(quietSub);
            foreach (var entry in new[] { "忽略", "仅记录", "弱提醒", "强提醒" }.Select((t, i) => new { t, i }))
            {
                var level = entry.i;
                MenuItem(menu, entry.t, delegate {
                    foreach (var t in batchTargets) State.Levels[t] = level;
                    Persist(); TreeChanged();
                });
            }
            MenuItem(menu, "跟随默认", delegate {
                foreach (var t in batchTargets) State.Levels.Remove(t);
                Persist(); TreeChanged();
            });
            if (chatMultiMode || selectedChats.Count > 0)
            {
                menu.Items.Add(new Separator());
                MenuItem(menu, "取消多选", ClearChatSelection);
            }
        }

        void ApplyChatLeafHighlight(TreeViewItem leaf, string title)
        {
            if (selectedChats.Contains(title))
            {
                leaf.SetResourceReference(Control.BackgroundProperty, "SelectionBrush");
                leaf.SetResourceReference(Control.ForegroundProperty, "SelectionInkBrush");
            }
            else
            {
                leaf.ClearValue(Control.BackgroundProperty);
                leaf.ClearValue(Control.ForegroundProperty);
            }
        }

        void RefreshTree()
        {
            if (chatMenuOpen) { pendingTreeRefresh = true; return; }
            string filter = Panel.Part<TextBox>("Search").Text.Trim();
            var titles = State.Groups.SelectMany(g => g.Chats).Concat(rows.Select(r => r.Title)).Concat(knownChats.All.Select(c => c.Title));
            Panel.Part<WrapPanel>("SearchActions").Visibility = filter.Length > 0 && !titles.Any(t => Match(t, filter) && t.Length == filter.Length)
                ? Visibility.Visible : Visibility.Collapsed;
            string groupsKey = String.Join("|", State.Groups.Select(g => g.Name + ":" + String.Join(",", g.Chats)));
            string levelsKey = String.Join(",", State.Levels.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            string knownKey = String.Join(",", knownChats.All.Select(c => c.Title + ":" + (c.Alias ?? "")));
            string selKey = chatMultiMode + "|" + String.Join(",", selectedChats.OrderBy(x => x, StringComparer.Ordinal));
            string collapseKey = archivedUngroupedExpanded + "|" + String.Join(",", State.CollapsedChatGroups.OrderBy(x => x, StringComparer.Ordinal));
            long nowTicks = clock().Ticks;
            string chatQuietKey = State.ChatQuietUntil == null ? "" : String.Join(",",
                State.ChatQuietUntil.Where(kv => kv.Value > nowTicks).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            string key = filter + "\f" + groupsKey + "\f" + levelsKey + "\f" + knownKey + "\f" + selKey + "\f" + collapseKey + "\f" + chatQuietKey + "\f" +
                String.Join("\n", rows.Select(s => s.Title + "\t" + s.Unread));
            if (treeKey == key) return;
            treeKey = key;
            var tree = Panel.Part<TreeView>("Chats");
            var prevItem = tree.SelectedItem as TreeViewItem;
            string prevChatTitle = prevItem == null ? null : prevItem.Tag as string;
            string prevBranchName = null;
            if (prevItem != null)
            {
                var g = prevItem.Tag as GroupDef;
                if (g == null)
                {
                    var parentBranch = ItemsControl.ItemsControlFromItemContainer(prevItem) as TreeViewItem;
                    if (parentBranch != null) g = parentBranch.Tag as GroupDef;
                }
                if (g != null) prevBranchName = g.Name;
            }
            buildingChatTree = true;
            try
            {
                tree.Items.Clear();
                var liveActive = rows.Where(r => !String.IsNullOrWhiteSpace(r.Title))
                    .GroupBy(r => r.Title, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.Unread > 0)
                    .Select(r => r.Title)
                    .ToList();
                var liveSet = new HashSet<string>(liveActive, StringComparer.Ordinal);
                var archivedUngrouped = knownChats.All
                    .OrderByDescending(c => c.LastSeenAt)
                    .Select(c => c.Title)
                    .Where(t => !String.IsNullOrWhiteSpace(t) && CustomGroup(t) == "" && !liveSet.Contains(t))
                    .Distinct()
                    .ToList();

                var groups = new List<GroupDef>();
                groups.Add(new GroupDef { Name = NewMessagesGroup, Chats = liveActive });
                groups.AddRange(State.Groups);
                if (archivedUngrouped.Count > 0)
                    groups.Add(new GroupDef { Name = ArchivedUngroupedGroup, Chats = archivedUngrouped });

                foreach (var group in groups)
                {
                    var currentGroup = group;
                    bool isDynamicGroup = currentGroup.Name == NewMessagesGroup || currentGroup.Name == ArchivedUngroupedGroup;
                    bool isCustomGroup = !isDynamicGroup;
                    var members = currentGroup.Chats.Where(t => MatchChat(t, filter) || Match(currentGroup.Name, filter)).Distinct().ToList();
                    if (filter.Length > 0 && members.Count == 0 && !Match(currentGroup.Name, filter)) continue;
                    var memberRows = members.Select(t => rows.FirstOrDefault(r => r.Title == t) ?? new SessionInfo { Title = t }).ToList();
                    int groupUnread = memberRows.Sum(r => Math.Max(0, r.Unread));
                    string meta = memberRows.Count + (groupUnread > 0 ? " · " + groupUnread + " 未读" : "");
                    string branchText = currentGroup.Name + "  (" + meta + ")";
                    bool expanded = filter.Length > 0
                        ? true
                        : (currentGroup.Name == ArchivedUngroupedGroup
                            ? archivedUngroupedExpanded
                            : !State.CollapsedChatGroups.Contains(currentGroup.Name));

                    object branchHeader = Rows.Branch(currentGroup.Name, memberRows.Count.ToString(), groupUnread, "L2Brush", "OnL2Brush");
                    if (chatMultiMode)
                    {
                        var groupCheck = new CheckBox
                        {
                            Content = Rows.Branch(currentGroup.Name, memberRows.Count.ToString(), groupUnread, "L2Brush", "OnL2Brush"),
                            FontWeight = FontWeights.SemiBold,
                            IsChecked = members.Count > 0 && members.All(m => selectedChats.Contains(m))
                        };
                        var groupMembers = members.ToList();
                        groupCheck.Click += (s, e) => {
                            e.Handled = true;
                            if (groupCheck.IsChecked == true) foreach (var m in groupMembers) selectedChats.Add(m);
                            else foreach (var m in groupMembers) selectedChats.Remove(m);
                            UpdateChatMultiButton();
                            TreeChanged();
                        };
                        branchHeader = groupCheck;
                    }

                    var branch = new TreeViewItem { Header = branchHeader, Tag = currentGroup, IsExpanded = expanded, FontWeight = FontWeights.SemiBold };
                    Rows.Name(branch, branchText);
                    if (isCustomGroup)
                    {
                        branch.AllowDrop = true;
                        string dropTarget = currentGroup.Name;
                        branch.DragOver += (s, e) => {
                            if (e.Data.GetDataPresent(DataFormats.UnicodeText)) e.Effects = DragDropEffects.Move;
                            else e.Effects = DragDropEffects.None;
                            e.Handled = true;
                        };
                        branch.Drop += (s, e) => {
                            e.Handled = true;
                            var dropped = e.Data.GetData(DataFormats.UnicodeText) as string;
                            if (!String.IsNullOrWhiteSpace(dropped)) AssignChats(new[] { dropped }, dropTarget);
                        };
                    }
                    branch.Expanded += (s, e) => {
                        if (buildingChatTree || e.OriginalSource != branch || filter.Length > 0) return;
                        if (currentGroup.Name == ArchivedUngroupedGroup) archivedUngroupedExpanded = true;
                        else if (State.CollapsedChatGroups.Remove(currentGroup.Name)) Persist();
                        UpdateChatExpandButton();
                    };
                    branch.Collapsed += (s, e) => {
                        if (buildingChatTree || e.OriginalSource != branch || filter.Length > 0) return;
                        if (currentGroup.Name == ArchivedUngroupedGroup) archivedUngroupedExpanded = false;
                        else if (!State.CollapsedChatGroups.Contains(currentGroup.Name)) { State.CollapsedChatGroups.Add(currentGroup.Name); Persist(); }
                        UpdateChatExpandButton();
                    };
                    tree.Items.Add(branch);
                    if (!chatMultiMode && prevChatTitle == null && prevBranchName == currentGroup.Name) branch.IsSelected = true;

                    var branchLeafOrder = memberRows.Select(r => r.Title).ToList();
                    foreach (var row in memberRows)
                    {
                        string title = row.Title;
                        int customLevel;
                        string rule = State.Levels.TryGetValue(title, out customLevel) ? RuleLabel(customLevel) : "";
                        var entryData = knownChats.Find(title);
                        string alias = entryData == null || String.IsNullOrWhiteSpace(entryData.Alias) ? "" : " · " + entryData.Alias;
                        string leafText = title + alias + rule + (row.Unread > 0 ? "  (" + row.Unread + ")" : "");
                        object leafHeader = ChatLeafView(title, entryData, customLevel, State.Levels.ContainsKey(title), row.Unread, isDynamicGroup);
                        if (chatMultiMode)
                        {
                            var cb = new CheckBox { Content = ChatLeafView(title, entryData, customLevel, State.Levels.ContainsKey(title), row.Unread, false), IsChecked = selectedChats.Contains(title), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                            cb.Click += (s, e) => {
                                e.Handled = true;
                                if (cb.IsChecked == true) selectedChats.Add(title); else selectedChats.Remove(title);
                                lastClickedChat = title;
                                UpdateChatMultiButton();
                                TreeChanged();
                            };
                            leafHeader = cb;
                        }
                        var leaf = new TreeViewItem { Header = leafHeader, Tag = title };
                        Rows.Name(leaf, leafText);
                        ApplyChatLeafHighlight(leaf, title);
                        branch.Items.Add(leaf);
                        if (!chatMultiMode && prevChatTitle == title && (prevBranchName == null || prevBranchName == currentGroup.Name))
                            leaf.IsSelected = true;
                        leaf.PreviewMouseLeftButtonDown += (s, e) => {
                            if (chatMultiMode || IsInsideButton(e.OriginalSource as DependencyObject, leaf)) return;
                            if (Keyboard.Modifiers == ModifierKeys.Control)
                            {
                                chatDragTitle = null;
                                if (!selectedChats.Remove(title)) selectedChats.Add(title);
                                lastClickedChat = title;
                                UpdateChatMultiButton();
                                TreeChanged();
                                e.Handled = true;
                            }
                            else if (Keyboard.Modifiers == ModifierKeys.Shift && lastClickedChat != null)
                            {
                                chatDragTitle = null;
                                int a = branchLeafOrder.IndexOf(lastClickedChat), b = branchLeafOrder.IndexOf(title);
                                if (a >= 0 && b >= 0)
                                {
                                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                                    for (int idx = lo; idx <= hi; idx++) selectedChats.Add(branchLeafOrder[idx]);
                                    UpdateChatMultiButton();
                                    TreeChanged();
                                    e.Handled = true;
                                }
                            }
                            else
                            {
                                lastClickedChat = title;
                                chatDragStart = e.GetPosition(null);
                                chatDragTitle = title;
                                if (selectedChats.Count > 0) { selectedChats.Clear(); UpdateChatMultiButton(); TreeChanged(); }
                            }
                        };
                        leaf.PreviewMouseMove += (s, e) => {
                            if (e.LeftButton != MouseButtonState.Pressed || chatMultiMode || chatDragTitle != title) return;
                            Vector diff = e.GetPosition(null) - chatDragStart;
                            if (Math.Abs(diff.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) >= SystemParameters.MinimumVerticalDragDistance)
                            {
                                chatDragTitle = null;
                                DragDrop.DoDragDrop(leaf, new DataObject(DataFormats.UnicodeText, title), DragDropEffects.Move);
                            }
                        };
                        leaf.PreviewMouseLeftButtonUp += delegate { if (chatDragTitle == title) chatDragTitle = null; };
                        leaf.MouseDoubleClick += (s, e) => { chatDragTitle = null; e.Handled = true; Jump(title); };
                    }
                }
            }
            finally { buildingChatTree = false; }
            UpdateChatMultiButton();
            UpdateChatExpandButton();
        }
        public void Tick()
        {
            var now = clock();
            archive.Maintain(now, State.ArchiveRetentionDays);
            if (lastTrashDay != now.Date)
            {
                lastTrashDay = now.Date;
                if (State.CleanTrash(now) != 0) Persist();
            }
            long until = State.QuietEnd(now);
            if (until > 0 && lastQuietEnd == 0) Alerts.ClearChats(n => n.Chat != null);
            if (until == 0 && (lastQuietEnd > 0 || State.MutedChats.Count > 0))
            {
                int count = State.MutedChats.Count;
                State.MutedChats.Clear(); Persist();
                // Re-seed with the last observed frame so the next new message is still detected.
                detector.ResetBaseline(); detector.Process(rows, false, rules);
                Alerts.Push(new Notice { Level = 2, Title = "免打扰已结束", Body = count + " 个会话有新消息" });
            }
            lastQuietEnd = until;
            List<string> expiredWithMsg;
            var expiredChats = State.ExpireChatQuiets(now, out expiredWithMsg);
            if (expiredChats.Count > 0)
            {
                Persist();
                TreeChanged();
            }
            if (expiredWithMsg.Count > 0 && until == 0)
            {
                string body = expiredWithMsg.Count == 1
                    ? "「" + expiredWithMsg[0] + "」免打扰期间有新消息"
                    : expiredWithMsg.Count + " 个指定会话免打扰期间有新消息：" + String.Join("、", expiredWithMsg.Take(3)) + (expiredWithMsg.Count > 3 ? " 等" : "");
                Alerts.Push(new Notice { Level = 2, Title = "免打扰已结束", Body = body });
            }
            string quiet = FormatQuietStatus(now, until);
            string pinHint = (!lost && rows.Count > 0 && rows.All(r => r.IsPinned)) ? " · 仅见置顶" : "";
            string lostHint = lost ? "（已退至托盘，建议用最小化保活）" : "";
            string drift = !lost && viewportDrifted ? ViewportWarning : "";
            var statusText = Panel.Part<TextBlock>("Status");
            statusText.SetResourceReference(TextBlock.ForegroundProperty, drift.Length > 0 ? "WarningBrush" : "MutedBrush");
            string noteHint = (!String.IsNullOrEmpty(chatActionNote) && DateTime.UtcNow < chatActionNoteUntil) ? " · " + chatActionNote : "";
            statusText.Text = connection + lostHint + quiet + pinHint + drift +
                (knownChats.Error == null ? "" : " · " + knownChats.Error) +
                (archive.Error == null ? "" : " · " + archive.Error) + hotkeyError + noteHint;
            UpdateBallToolTip();
            Panel.Part<Button>("Quiet").Content = until == 0 ? "☾ 免打扰" : "☾ 解除 · 至 " + new DateTime(until).ToString("HH:mm");
            UpdateStatusDot(drift.Length > 0);
            UpdateBallAppearance();
            UpdateTrayIcon();
            bool fired = false;
            foreach (var r in State.Reminders)
            {
                if (!r.Active) continue;
                long at;
                if (r.IsTask)
                {
                    at = r.TaskDue(now);
                    if (at == 0 || at > now.Ticks) continue;
                    r.TaskState = "";
                    if (r.NextFollowUp <= now.Ticks) r.NextFollowUp = Reminder.Tomorrow(now);
                    Alerts.ClearChats(n => n.ReminderId == r.Id);
                }
                else
                {
                    at = r.Latest(now);
                    if (r.Snooze > 0) { if (now.Ticks < r.Snooze) continue; r.Snooze = 0; }
                    else if (at == 0 || at <= r.Delivered) continue;
                }
                r.Delivered = at;
                Persist();
                Alerts.Push(new Notice { ReminderId=r.Id, Title = r.Title, Level = r.Level, Task = r.IsTask, CanConvert = !r.IsTask && !r.Recurring,
                    Body = new DateTime(at).ToString("MM-dd HH:mm") + (r.IsTask ? "　任务待确认" : "　日程提醒") });
                fired = true;
            }
            string minute = now.ToString("yyyyMMddHHmm");
            if (fired || minute != agendaMinute) { RefreshAgenda(); agendaMinute = minute; }
        }

        void Apply(Frame frame)
        {
            if (skipViewportSamples) return;
            if (frame.MainHwnd != IntPtr.Zero) lastWeChatHwnd = frame.MainHwnd;
            TrackViewport(frame);
            connection = frame.Status;
            if (frame.Chats == null || frame.Chats.Count == 0)
            {
                if(++emptyReads==3){lost=true;wechatOpen=false;
                    tray.ShowBalloonTip(4000,"微信已关入托盘，消息监控暂停","建议平时点微信右上角「—」最小化以保持提醒不断线；点悬浮球可尝试从托盘唤回。",FormsTray.ToolTipIcon.Warning);}
                Tick();
                detector.ResetBaseline();
                return;
            }
            emptyReads=0;lost=false;
            wechatOpen = frame.WindowVisible && !frame.WindowIconic;
            rows = frame.Chats;
            if (knownChats.Observe(rows.Select(r => r.Title), clock(), Group)) treeKey = null;
            Tick();
            int unreadTotal = rows.Sum(s => Math.Max(0,s.Unread));
            var badge = Ball.Part<TextBlock>("Badge");
            string badgeText = unreadTotal > 99 ? "99+" : unreadTotal.ToString();
            if (badge.Text != badgeText) { badge.Text = badgeText; if (unreadTotal > 0) Motion.Bump(badge); }
            Panel.Part<TabItem>("WechatTab").Header = Rows.TabHeader("微信", unreadTotal, "L2Brush", false);
            UpdateBallAppearance();
            UpdateTrayIcon();
            RefreshTree();
            var change = detector.Process(rows, frame.Foreground, rules);
            var capturedAt = clock();
            bool quietActive = lastQuietEnd > 0;
            if (change.Events.Count > 0)
                archive.Add(change.Events.Select(e => new ArchiveEntry {
                    ts = capturedAt.Ticks, time = capturedAt.ToString("HH:mm:ss"), chat = e.Title, group = Group(e.Title),
                    preview = e.Preview, unread = e.Unread, level = State.ChatLevel(e),
                    muted = quietActive || State.ChatQuietEnd(e.Title, capturedAt) > 0 || rows.Any(r => r.Title == e.Title && r.Muted)
                }));
            foreach (string title in change.Read) Alerts.ClearChats(n => n.Chat == title);
            foreach (var e in change.Events)
            {
                int level = State.ChatLevel(e);
                if (level < 2) continue;
                if (lastQuietEnd > 0)
                {
                    if (!State.MutedChats.Contains(e.Title)) { State.MutedChats.Add(e.Title); Persist(); }
                }
                else if (State.ChatQuietEnd(e.Title, capturedAt) > 0)
                {
                    if (State.ChatQuietPending == null) State.ChatQuietPending = new List<string>();
                    if (!State.ChatQuietPending.Contains(e.Title)) { State.ChatQuietPending.Add(e.Title); Persist(); }
                }
                else Alerts.Push(new Notice { Title = e.Title, Chat = e.Title, Body = e.Preview, Level = level });
            }
        }
        void Jump(string title)
        {
            if (navigating || movingWechat || waking || String.IsNullOrWhiteSpace(title)) return;
            navigating = true;
            bool returnToTop = viewportDrifted;
            if(Alerts.Current!=null && Alerts.Current.Chat==title) Alerts.Window.Hide();
            NavigationStatus = "正在打开会话";
            var thread = new Thread(delegate() {
                string error;
                string step = "";
                try { error = Navigator.Go(new WeChatUia(), title, "auto", 350, message => step = message, returnToTop); }
                catch (Exception ex) { error = ex.Message; }
                Panel.Dispatcher.BeginInvoke(new Action(delegate {
                    navigating = false;
                    NavigationStatus = error ?? step;
                    if(error==null) Alerts.ClearChats(n=>n.Chat==title);
                    if (error != null) { Alerts.ClearChats(n => n.Chat == title); ChatTip("跳转未完成", error); }
                }));
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        public void Dispose()
        {
            exiting = true; timer.Stop(); undoTimer.Stop(); monitor.Dispose(); knownChats.Flush(clock()); archive.Flush(); tray.Visible = false; tray.Dispose();
            UnhookTheme();
            Native.UnregisterHotKey(hwnd,2);
            if (HasHotkey) Native.UnregisterHotKey(hwnd,1);
        }
    }

    static class Program
    {
        [STAThread] static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\NudgeWatch.Native.061", out first))
            {
                if (!first) return;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"NudgeWatch.Native");
                try
                {
                    using (var shell = new Shell(directory, MonitorLoop.LiveReader(), () => DateTime.Now))
                    { shell.SyncStartupOnLaunch(); shell.Start(); app.Run(); }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "NudgeWatch · 运行错误"); }
            }
        }
    }
}


