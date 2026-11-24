using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WxWatch;
namespace NudgeWatch {
 public sealed partial class Shell {
    int emptyReads;
    bool lost, waking, themeHooked, wechatOpen;
    const string NewMessagesGroup = "新消息";
    const string ArchivedUngroupedGroup = "已收录 · 待归组";
    static bool IsSystemChatGroup(string name) { return name == NewMessagesGroup || name == ArchivedUngroupedGroup || name == "未分组"; }
    string CustomGroup(string title)
    {
        if (String.IsNullOrWhiteSpace(title)) return "";
        string trimmed = title.Trim();
        var g = State.Groups.FirstOrDefault(x => x.Chats.Any(c => c == title || (c != null && c.Trim() == trimmed)));
        return g == null ? "" : g.Name;
    }
    string Group(string title) { string g = CustomGroup(title); return g.Length > 0 ? g : NewMessagesGroup; }
    static bool Match(string value,string filter){return value.IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0;}
    void TreeChanged(){treeKey=null;RefreshTree();}
    string Prompt(string label,string initial){
        var box=new TextBox{Text=initial};
        var dlg=PromptWindow(label,box);
        dlg.Loaded+=delegate{box.Focus();box.SelectAll();};
        return dlg.ShowDialog()==true?box.Text.Trim():null;
    }
    Window PromptWindow(string label,Control input){
        var dlg=new Window{Owner=Panel.IsVisible?Panel:null,Title=label,Width=340,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,
            WindowStartupLocation=Panel.IsVisible?WindowStartupLocation.CenterOwner:WindowStartupLocation.CenterScreen};
        dlg.SetResourceReference(Window.BackgroundProperty,"BgBrush");
        dlg.SetResourceReference(Window.ForegroundProperty,"InkBrush");
        var head=new TextBlock{Text=label,FontSize=14,FontWeight=FontWeights.Bold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
        head.SetResourceReference(TextBlock.ForegroundProperty,"InkBrush");
        var ok=new Button{Content="确定",IsDefault=true,MinWidth=72};
        ok.SetResourceReference(Button.StyleProperty,"PrimaryButton");
        var cancel=new Button{Content="取消",IsCancel=true,MinWidth=72,Margin=new Thickness(0,0,8,0)};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        buttons.Children.Add(cancel);buttons.Children.Add(ok);
        var layout=new StackPanel{Margin=new Thickness(18,16,18,16)};layout.Children.Add(head);layout.Children.Add(input);layout.Children.Add(buttons);dlg.Content=layout;
        ok.Click+=delegate{dlg.DialogResult=true;};
        cancel.Click+=delegate{dlg.DialogResult=false;};
        return dlg;
    }
    GroupDef FindGroup(string name){
        var g=State.Groups.Find(x=>x.Name==name);
        if(g==null){g=new GroupDef{Name=name};State.Groups.Add(g);}return g;
    }
    void EditGroup(string title){
        var targets = selectedChats.Count > 1 && selectedChats.Contains(title) ? selectedChats.ToArray() : new[] { title };
        string current = CustomGroup(title);
        if (current.Length == 0) current = State.Groups.Count > 0 ? State.Groups[0].Name : "常用";
        var combo = new ComboBox { IsEditable = true, ItemsSource = State.Groups.Select(g => g.Name).ToList(), Text = current };
        if (PromptWindow(targets.Length > 1 ? "将 " + targets.Length + " 个会话加入分组" : "加入分组", combo).ShowDialog() != true) return;
        string name = combo.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || IsSystemChatGroup(name)) return;
        AssignChats(targets, name);
    }
    GroupDef SelectedGroup(){
        var item=Panel.Part<TreeView>("Chats").SelectedItem as TreeViewItem;
        if(item!=null && !(item.Tag is GroupDef))item=ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
        return item==null?null:item.Tag as GroupDef;
    }
    void SetupExtras(){
        Panel.Part<TextBox>("Search").TextChanged+=delegate{TreeChanged();};
        Panel.Click("SyncChats", ScanChats);
        Panel.Click("ReturnWechatTop", ReturnWechatTop);
        Panel.Click("SearchWechat", delegate { Jump(Panel.Part<TextBox>("Search").Text.Trim()); });
        Panel.Click("SaveChat", delegate { string t = Panel.Part<TextBox>("Search").Text.Trim(); if(t.Length > 0) EditGroup(t); });
        Panel.Click("OpenArchive", delegate { ShowArchive(null, null); });
        Panel.Click("ToggleChatExpand", ToggleAllChatGroups);
        var chatsTree = Panel.Part<TreeView>("Chats");
        chatsTree.PreviewMouseRightButtonDown += SelectOnRightClick;
        chatsTree.ContextMenu = new ContextMenu();
        chatsTree.ContextMenuOpening += OnChatsContextMenuOpening;
        chatsTree.ContextMenu.Closed += delegate { OnChatMenuClosed(); };
        Panel.Click("ChatMulti", delegate {
            chatMultiMode = !chatMultiMode;
            if (!chatMultiMode) selectedChats.Clear();
            UpdateChatMultiButton();
            TreeChanged();
        });
        var groups=new ContextMenu();
        MenuItem(groups,"新建分组",delegate{var n=Prompt("分组名称","");if(!string.IsNullOrWhiteSpace(n)&&!IsSystemChatGroup(n)){FindGroup(n);Persist();TreeChanged();}});
        MenuItem(groups,"重命名选中分组",delegate{var g=SelectedGroup();if(!State.Groups.Contains(g))return;
            var n=Prompt("新的名称",g.Name);if(!string.IsNullOrWhiteSpace(n)&&!IsSystemChatGroup(n)&&!State.Groups.Any(x=>x.Name==n)){
                if (State.CollapsedChatGroups.Remove(g.Name)) State.CollapsedChatGroups.Add(n);
                g.Name=n; Persist(); UpdateChatGroups(g); TreeChanged(); }});
        MenuItem(groups,"删除选中分组",delegate{var g=SelectedGroup();if(State.Groups.Remove(g)){State.CollapsedChatGroups.Remove(g.Name);Persist();UpdateChatGroups(g);TreeChanged();}});
        MenuItem(groups,"手动添加群名",delegate{
            var g=SelectedGroup();
            var nameBox=new TextBox();
            var groupBox=new ComboBox{IsEditable=true,Margin=new Thickness(0,8,0,0),ItemsSource=State.Groups.Select(x=>x.Name).ToList(),
                Text=State.Groups.Contains(g)?g.Name:(State.Groups.Count>0?State.Groups[0].Name:"常用")};
            var host=new ContentControl();
            var stack=new StackPanel();
            stack.Children.Add(new TextBlock{Text="会话名称"});
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock{Text="目标分组（可输入新组名）",Margin=new Thickness(0,8,0,0)});
            stack.Children.Add(groupBox);
            host.Content=stack;
            var dlg=PromptWindow("手动添加会话",host);
            dlg.Loaded+=delegate{nameBox.Focus();};
            if(dlg.ShowDialog()!=true)return;
            string n=nameBox.Text.Trim(), target=groupBox.Text.Trim();
            if(string.IsNullOrWhiteSpace(n)||string.IsNullOrWhiteSpace(target)||IsSystemChatGroup(target))return;
            AssignChats(new[]{n}, target);
        });
        MenuItem(groups,"批量添加群名",AddManyChats);
        MenuItem(groups,"强提醒关键词",delegate{var n=Prompt("强提醒关键词（逗号分隔）",string.Join(",",State.Keywords));if(n==null)return;
            State.Keywords=n.Split(new[]{',','，'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct().ToList();SetKeywords();Persist();});
        Panel.Click("Groups",delegate{groups.PlacementTarget=Panel.Part<Button>("Groups");groups.IsOpen=true;});
        SetKeywords();
        var options=new ContextMenu();
        var autoTop = new MenuItem { Header = "微信失焦后自动回顶", IsCheckable = true, IsChecked = State.AutoReturnToTop };
        autoTop.Click += delegate { State.AutoReturnToTop = autoTop.IsChecked; Persist(); };
        options.Items.Add(autoTop);
        var retention = new MenuItem { Header = "消息归档保留期" };
        foreach (int days in new[] { 7, 30, 90, 0 })
        {
            int value = days;
            var choice = new MenuItem { Header = days == 0 ? "永久" : days + " 天", IsCheckable = true };
            retention.Items.Add(choice);
            options.Opened += delegate { choice.IsChecked = State.ArchiveRetentionDays == value; };
            choice.Click += delegate {
                if (value != 0 && (State.ArchiveRetentionDays == 0 || value < State.ArchiveRetentionDays) &&
                    !ThemedDialog.Confirm(Panel, "消息归档保留期", "缩短保留期会清理超过 " + value + " 天的消息归档，是否继续？", "继续清理", true)) return;
                State.ArchiveRetentionDays = value; Persist(); Tick();
            };
        }
        options.Items.Add(retention);
        var startup=new MenuItem{Header="随 Windows 登录启动",IsCheckable=true};
        options.Items.Add(startup);
        options.Opened+=delegate{
            var st = StartupManager.QueryStatus(StartupManager.CurrentExePath());
            startup.IsChecked = st.Enabled;
            if (st.Enabled)
                startup.ToolTip = "已生效（" + (st.Method == "startup_folder" ? "启动文件夹" : "系统登录项") + "）：" + st.RegisteredTarget;
            else if (st.DisabledBySystem)
                startup.ToolTip = "已被 Windows 任务管理器/启动设置禁用，点击以重新启用";
            else if (st.PointsToOtherExe)
                startup.ToolTip = "当前启动项指向旧路径（" + st.RegisteredTarget + "），点击以更新为当前程序";
            else
                startup.ToolTip = "登录 Windows 时自动运行当前位置的 NudgeWatch";
        };
        startup.Click+=delegate{
            string detail;
            bool want = startup.IsChecked;
            if (StartupManager.SetEnabled(want, StartupManager.CurrentExePath(), out detail))
            {
                State.AutoStart = want;
                Persist();
                SetChatActionNote(detail);
            }
            else
            {
                startup.IsChecked = StartupManager.QueryStatus(StartupManager.CurrentExePath()).Enabled;
                ThemedDialog.Info(Panel, "随 Windows 登录启动设置失败", detail);
            }
        };
        var themes=new MenuItem{Header="界面主题"};options.Items.Add(themes);
        var themeItems=new List<MenuItem>();
        foreach(var id in ThemeManager.Keys){
            string value=id;
            var item=new MenuItem{Header=ThemeHeader(value),Tag=value,IsCheckable=true,Padding=new Thickness(8,4,10,4)};
            themes.Items.Add(item);
            themeItems.Add(item);
            item.Click+=delegate{State.Theme=value;ApplyTheme();Persist();};
        }
        options.Opened+=delegate{
            string cur=string.IsNullOrEmpty(State.Theme)?"system":State.Theme;
            foreach(var mi in themeItems)mi.IsChecked=((string)mi.Tag==cur);
        };
        HookTheme();
        ApplyTheme();
        Panel.Click("Options",delegate{options.PlacementTarget=Panel.Part<Button>("Options");options.IsOpen=true;});
        Ball.ContextMenu.Opened += delegate {
            var menu = Ball.ContextMenu;
            menu.Items.Clear();
            PopulateQuietMenuItems(menu.Items);
            menu.Items.Add(new Separator());
            MenuItem(menu, IsPanelOpen() ? "收入托盘（隐藏主面板）" : "打开主面板", TogglePanel);
            MenuItem(menu, "快速建提醒", Forms.OpenQuick);
            MenuItem(menu, "收起微信（最小化保活）", MinimizeWeChat);
            MenuItem(menu, "一键将微信列表回顶", ReturnWechatTop);
            var pinnedRows = rows.Where(r => r.IsPinned).ToList();
            if (State.Groups.Count > 0 || pinnedRows.Count > 0) menu.Items.Add(new Separator());
            foreach (var g in State.Groups)
            {
                var sub = new MenuItem { Header = g.Name };
                menu.Items.Add(sub);
                foreach (var name in g.Chats)
                {
                    string target = name;
                    var child = new MenuItem { Header = target };
                    child.Click += delegate { Jump(target); };
                    sub.Items.Add(child);
                }
            }
            foreach (var row in pinnedRows)
            {
                string target = row.Title;
                MenuItem(menu, "置顶 · " + target, delegate { Jump(target); });
            }
        };
    }
    internal void QuietAllChats(int minutes)
    {
        State.TemporaryUntil = clock().AddMinutes(minutes).Ticks;
        Changed();
    }
    internal void QuietChats(IEnumerable<string> titles, int minutes)
    {
        if (titles == null) return;
        var list = titles.Where(t => !String.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0) return;
        DateTime now = clock();
        long until = State.QuietUntilForMinutes(now, minutes);
        foreach (var title in list) State.SetChatQuiet(title, until);
        var targetSet = new HashSet<string>(list, StringComparer.Ordinal);
        Alerts.ClearChats(n => n.Chat != null && targetSet.Contains(n.Chat));
        string untilText = new DateTime(until).ToString("HH:mm");
        SetChatActionNote(list.Count == 1
            ? "已免打扰「" + list[0] + "」至 " + untilText
            : "已免打扰 " + list.Count + " 个指定会话至 " + untilText);
        TreeChanged();
        Changed();
    }
    internal void ReleaseChatQuiet(string title)
    {
        if (!State.ReleaseChatQuiet(title)) return;
        SetChatActionNote("已解除「" + title + "」免打扰");
        TreeChanged();
        Changed();
    }
    internal void ReleaseChatsQuiet(IEnumerable<string> titles)
    {
        if (titles == null) return;
        bool removed = false;
        int count = 0;
        string last = null;
        foreach (var title in titles.Where(t => !String.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal))
        {
            if (State.ReleaseChatQuiet(title)) { removed = true; count++; last = title; }
        }
        if (!removed) return;
        SetChatActionNote(count == 1 ? "已解除「" + last + "」免打扰" : "已解除 " + count + " 个会话免打扰");
        TreeChanged();
        Changed();
    }
    internal void ReleaseAllChatQuiets()
    {
        if (!State.ReleaseAllChatQuiets()) return;
        SetChatActionNote("已解除全部指定会话免打扰");
        TreeChanged();
        Changed();
    }
    internal List<string> SuggestQuietChats()
    {
        DateTime now = clock();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r != null && !String.IsNullOrWhiteSpace(r.Title))
            .OrderByDescending(r => r.Unread > 0))
        {
            if (State.ChatQuietEnd(row.Title, now) > 0) continue;
            if (seen.Add(row.Title)) result.Add(row.Title);
            if (result.Count >= 5) break;
        }
        return result;
    }
    void PickQuietChats(string initialChat)
    {
        DateTime now = clock();
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Action<string> addCandidate = title => {
            if (String.IsNullOrWhiteSpace(title)) return;
            string normalized = title.Trim();
            if (seen.Add(normalized)) candidates.Add(normalized);
        };
        if (!String.IsNullOrWhiteSpace(initialChat)) addCandidate(initialChat);
        foreach (var row in rows.Where(r => r != null && r.Unread > 0)) addCandidate(row.Title);
        foreach (var row in rows.Where(r => r != null)) addCandidate(row.Title);
        foreach (var g in State.Groups)
            foreach (var c in g.Chats) addCandidate(c);
        foreach (var kc in knownChats.All.OrderByDescending(c => c.LastSeenAt)) addCandidate(kc.Title);

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        if (!String.IsNullOrWhiteSpace(initialChat)) chosen.Add(initialChat.Trim());

        var dlg = new Window
        {
            Owner = Panel.IsVisible ? Panel : null,
            Title = "指定会话免打扰",
            Width = 370,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = Panel.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = !Panel.IsVisible && Alerts.Window.IsVisible
        };
        dlg.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        dlg.SetResourceReference(Window.ForegroundProperty, "InkBrush");

        var head = new TextBlock { Text = "选择要免打扰的微信会话", FontSize = 14, FontWeight = FontWeights.Bold };
        head.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var subHead = new TextBlock { Text = "仅静音所选会话的新消息弹窗，其他消息与日程提醒照常提示。", FontSize = 11.5, Margin = new Thickness(0, 3, 0, 8), TextWrapping = TextWrapping.Wrap };
        subHead.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var searchBox = new TextBox { Tag = "搜索会话、备注、拼音首字母，或输入会话名…" };
        var addCustomBtn = new Button { Content = "", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        addCustomBtn.SetResourceReference(FrameworkElement.StyleProperty, "ChipButton");

        var listBox = new ListBox { Height = 210, Margin = new Thickness(0, 8, 0, 0) };
        var countText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        countText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var selectVisibleBtn = new Button { Content = "全选当前", Margin = new Thickness(0, 0, 6, 0) };
        selectVisibleBtn.SetResourceReference(FrameworkElement.StyleProperty, "ChipButton");
        var clearSelBtn = new Button { Content = "清空", Margin = new Thickness(0, 0, 6, 0) };
        clearSelBtn.SetResourceReference(FrameworkElement.StyleProperty, "ChipButton");
        var actionRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(selectVisibleBtn, Dock.Left);
        DockPanel.SetDock(clearSelBtn, Dock.Left);
        DockPanel.SetDock(countText, Dock.Right);
        actionRow.Children.Add(selectVisibleBtn);
        actionRow.Children.Add(clearSelBtn);
        actionRow.Children.Add(countText);

        var durationBox = new ComboBox { MinWidth = 170 };
        durationBox.Items.Add(new ComboBoxItem { Content = "30 分钟", Tag = 30 });
        durationBox.Items.Add(new ComboBoxItem { Content = "1 小时", Tag = 60 });
        durationBox.Items.Add(new ComboBoxItem { Content = "2 小时", Tag = 120 });
        durationBox.Items.Add(new ComboBoxItem { Content = "今日内（至 24:00）", Tag = 0 });
        durationBox.SelectedIndex = 1;
        var durationRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var durLabel = new TextBlock { Text = "免打扰时长", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        durLabel.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        DockPanel.SetDock(durLabel, Dock.Left);
        durationRow.Children.Add(durLabel);
        durationRow.Children.Add(durationBox);

        var ok = new Button { Content = "确定免打扰", IsDefault = true, MinWidth = 88 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var visibleTitles = new List<string>();
        Action updateFooter = delegate {
            countText.Text = "已选 " + chosen.Count + " 个会话";
            ok.IsEnabled = chosen.Count > 0;
        };
        Action rebuildList = null;
        rebuildList = delegate {
            string filter = searchBox.Text.Trim();
            listBox.Items.Clear();
            visibleTitles.Clear();
            foreach (var title in candidates)
            {
                if (filter.Length > 0 && !MatchChat(title, filter)) continue;
                visibleTitles.Add(title);
                string currentTitle = title;
                long activeEnd = State.ChatQuietEnd(currentTitle, now);
                string suffix = activeEnd > 0 ? "（免打扰至 " + new DateTime(activeEnd).ToString("HH:mm") + "）" : "";
                var cb = new CheckBox
                {
                    Content = currentTitle + suffix,
                    IsChecked = chosen.Contains(currentTitle),
                    Margin = new Thickness(2, 3, 2, 3),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                cb.Click += delegate {
                    if (cb.IsChecked == true) chosen.Add(currentTitle);
                    else chosen.Remove(currentTitle);
                    updateFooter();
                };
                listBox.Items.Add(cb);
            }
            if (filter.Length > 0 && !seen.Contains(filter))
            {
                addCustomBtn.Content = "＋ 添加并勾选「" + filter + "」";
                addCustomBtn.Visibility = Visibility.Visible;
            }
            else addCustomBtn.Visibility = Visibility.Collapsed;
            updateFooter();
        };

        searchBox.TextChanged += delegate { rebuildList(); };
        addCustomBtn.Click += delegate {
            string custom = searchBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(custom)) return;
            addCandidate(custom);
            chosen.Add(custom);
            searchBox.Clear();
            rebuildList();
        };
        selectVisibleBtn.Click += delegate {
            foreach (var t in visibleTitles) chosen.Add(t);
            rebuildList();
        };
        clearSelBtn.Click += delegate {
            chosen.Clear();
            rebuildList();
        };
        ok.Click += delegate { dlg.DialogResult = true; };
        cancel.Click += delegate { dlg.DialogResult = false; };

        var layout = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        layout.Children.Add(head);
        layout.Children.Add(subHead);
        layout.Children.Add(searchBox);
        layout.Children.Add(addCustomBtn);
        layout.Children.Add(listBox);
        layout.Children.Add(actionRow);
        layout.Children.Add(durationRow);
        layout.Children.Add(buttons);
        dlg.Content = layout;
        dlg.Loaded += delegate { searchBox.Focus(); };
        rebuildList();

        if (dlg.ShowDialog() == true && chosen.Count > 0)
        {
            var selectedItem = durationBox.SelectedItem as ComboBoxItem;
            int mins = selectedItem != null && selectedItem.Tag is int ? (int)selectedItem.Tag : 60;
            QuietChats(chosen, mins);
        }
    }
    void UpdateChatGroups(GroupDef group)
    {
        foreach (var title in group.Chats) knownChats.Annotate(title, null, Group(title), clock(), false);
        knownChats.Flush(clock());
    }
    void ShowArchive(string group, string title)
    {
        IEnumerable<string> selected = title != null ? new[] { title } : null;
        if (selected == null && group != null)
        {
            var map = BuildArchiveGroupMap();
            selected = map.Where(p => p.Value == group).Select(p => p.Key).ToList();
        }
        ShowArchiveSelection(selected);
    }
    void ShowArchiveSelection(IEnumerable<string> selected)
    {
        new ArchiveWindow(Panel, archive, clock(), BuildArchiveGroupMap(), selected).Show();
    }
    Dictionary<string, string> BuildArchiveGroupMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var def in State.Groups)
            foreach (var chat in def.Chats)
                if (!map.ContainsKey(chat)) map.Add(chat, def.Name);
        foreach (var chat in knownChats.All)
            if (!map.ContainsKey(chat.Title)) map.Add(chat.Title, String.IsNullOrEmpty(chat.LastGroup) || chat.LastGroup == "未分组" ? NewMessagesGroup : chat.LastGroup);
        foreach (var row in rows.Where(r => !String.IsNullOrEmpty(r.Title)))
            if (!map.ContainsKey(row.Title)) map.Add(row.Title, NewMessagesGroup);
        return map;
    }
    /// <summary>主题菜单项：名画缩略图或色板 + 名称，让油画主题在选择时就被看到。</summary>
    static object ThemeHeader(string key){
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        var swatch=new Border{Width=48,Height=27,CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Center};
        swatch.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
        if(key=="system"){
            Palette a=ThemeManager.Presets[0],b=ThemeManager.Presets[1];
            swatch.Background=Split(a.Bg,b.Bg,45);
        }else{
            var p=ThemeManager.Resolve(key);
            var img=ThemeManager.LoadEmbeddedImage(p.ArtworkThumb);
            if(img!=null)swatch.Background=new ImageBrush(img){Stretch=Stretch.UniformToFill};
            else swatch.Background=Split(p.Bg,p.Accent,0);
        }
        row.Children.Add(swatch);
        row.Children.Add(new TextBlock{Text=ThemeManager.DisplayName(key),Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center});
        return row;
    }
    static Brush Split(string first,string second,double angle){
        var g=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=angle==0?new Point(1,0):new Point(1,1)};
        Color a=ThemeManager.ParseColor(first),b=ThemeManager.ParseColor(second);
        g.GradientStops.Add(new GradientStop(a,0));g.GradientStops.Add(new GradientStop(a,0.55));
        g.GradientStops.Add(new GradientStop(b,0.55));g.GradientStops.Add(new GradientStop(b,1));
        g.Freeze();return g;
    }
    void SetKeywords(){rules.StrongKeywords.Clear();rules.StrongKeywords.AddRange(State.Keywords);}
    void ApplyTheme(){
        ThemeManager.Apply(State.Theme);
        UpdateBallAppearance();
        if(Alerts!=null && Alerts.Current!=null)ThemeManager.StyleAlert(Alerts.Window,Alerts.Current.Level);
    }
    void UpdateBallAppearance(){
        bool quiet=lastQuietEnd>0;
        Ball.Part<Border>("Surface").Background=ThemeManager.BallBackground(lost,quiet);
        var badge=Ball.Part<TextBlock>("Badge");
        bool none=badge.Text=="0"||string.IsNullOrEmpty(badge.Text);
        badge.Visibility=none?Visibility.Collapsed:Visibility.Visible;
        badge.FontSize=badge.Text.Length>=3?13:badge.Text.Length==2?15:17;
        Ball.Part<FrameworkElement>("LogoMark").Visibility=none?Visibility.Visible:Visibility.Collapsed;
        Ball.Part<FrameworkElement>("QuietMark").Visibility=quiet&&!lost?Visibility.Visible:Visibility.Collapsed;
        Ball.Part<FrameworkElement>("LostMark").Visibility=lost?Visibility.Visible:Visibility.Collapsed;
        var toggle = Ball.FindName("WeChatToggle") as Button;
        if (toggle != null)
        {
            var bubble = Ball.FindName("WeChatBubble") as System.Windows.Shapes.Path;
            var minIcon = Ball.FindName("WeChatMinIcon") as System.Windows.Shapes.Path;
            var openIcon = Ball.FindName("WeChatOpenIcon") as System.Windows.Shapes.Path;
            var stateDot = Ball.FindName("WeChatStateDot") as System.Windows.Shapes.Ellipse;
            string brushKey = lost ? "LostBrush" : wechatOpen ? "SuccessBrush" : "MutedBrush";
            string borderKey = lost ? "LostBrush" : wechatOpen ? "SuccessBrush" : "BorderBrush";
            toggle.SetResourceReference(Control.BorderBrushProperty, borderKey);
            if (bubble != null) bubble.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
            if (stateDot != null) stateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
            if (minIcon != null)
            {
                minIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
                minIcon.Visibility = !lost && wechatOpen ? Visibility.Visible : Visibility.Collapsed;
            }
            if (openIcon != null)
            {
                openIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
                openIcon.Visibility = lost || !wechatOpen ? Visibility.Visible : Visibility.Collapsed;
            }
            toggle.ToolTip = lost
                ? "微信已进托盘 · 点击唤醒微信（也可中键点悬浮球）"
                : wechatOpen
                    ? "微信已打开 · 点击收起并自动回顶（也可中键点悬浮球）"
                    : "微信已最小化保活 · 点击打开微信（也可中键点悬浮球）";
        }
    }
    void UpdateStatusDot(bool drift){
        var dot=Panel.Part<System.Windows.Shapes.Ellipse>("StatusDot");
        string key=lost?"LostBrush":drift?"WarningBrush":(connection??"").StartsWith("微信已连接")?"SuccessBrush":"MutedBrush";
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,key);
    }
    BrandArt.TrayState trayState=BrandArt.TrayState.Normal;
    void UpdateTrayIcon(){
        if(tray==null)return;
        int unread=rows.Sum(s=>Math.Max(0,s.Unread));
        var state=lost?BrandArt.TrayState.Lost:lastQuietEnd>0?BrandArt.TrayState.Quiet:unread>0?BrandArt.TrayState.Unread:BrandArt.TrayState.Normal;
        string text="NudgeWatch 0.8.0"+(lost?" · 微信已进托盘":lastQuietEnd>0?" · 免打扰中":unread>0?" · "+unread+" 条未读":"");
        if(text.Length>63)text=text.Substring(0,63);
        if(tray.Text!=text)tray.Text=text;
        if(state==trayState)return;
        var icon=BrandArt.TrayIcon(state);
        if(icon==null)return;
        var old=tray.Icon;
        tray.Icon=icon;
        trayState=state;
        if(old!=null)old.Dispose();
    }
    void HookTheme(){
        if(themeHooked)return;
        SystemEvents.UserPreferenceChanged+=OnSystemThemeChanged;
        themeHooked=true;
    }
    void UnhookTheme(){
        if(!themeHooked)return;
        SystemEvents.UserPreferenceChanged-=OnSystemThemeChanged;
        themeHooked=false;
    }
    void OnSystemThemeChanged(object sender,UserPreferenceChangedEventArgs e){
        if(e.Category==UserPreferenceCategory.General && (string.IsNullOrEmpty(State.Theme)||State.Theme=="system")){
            Panel.Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }
    }
    void MinimizeWeChat(){
        if (movingWechat || navigating || waking) return;
        var known = lastWeChatHwnd;
        var top = State.AutoReturnToTop;
        var anchorsSnapshot = pinnedAtTop.ToArray();
        List<SessionInfo> topRows = null;
        WeChatWorker(() => Navigator.MinimizeWeChat(known, top, true, anchorsSnapshot, out topRows), delegate(bool ok, string error) {
            if (ok)
            {
                if (topRows != null && topRows.Count > 0) RememberTop(topRows);
                if (top) viewportDrifted = false;
                wechatOpen = false;
                NavigationStatus = top ? "已最小化微信到任务栏（自动回顶保活）" : "已最小化微信到任务栏（继续后台提醒）";
                UpdateBallAppearance();
            }
        }, top);
    }
    void ToggleWeChatWindow(){
        if (movingWechat || navigating || waking) return;
        var known = lastWeChatHwnd;
        var top = State.AutoReturnToTop;
        var anchorsSnapshot = pinnedAtTop.ToArray();
        List<SessionInfo> topRows = null;
        string action = null;
        WeChatWorker(delegate {
            IntPtr target;
            var state = Navigator.InspectWeChatWindow(known, out target);
            if (state == WeChatWindowState.Visible)
            {
                action = "minimized";
                return Navigator.MinimizeWeChat(target, top, true, anchorsSnapshot, out topRows);
            }
            if (state == WeChatWindowState.Minimized)
            {
                action = "restored";
                return Navigator.RestoreWeChatWindow(target);
            }
            action = "wake";
            return true;
        }, delegate(bool ok, string error) {
            if (action == "wake") { WakeWeChat(); return; }
            if (!ok) return;
            if (action == "minimized")
            {
                if (topRows != null && topRows.Count > 0) RememberTop(topRows);
                if (top) viewportDrifted = false;
                wechatOpen = false;
                NavigationStatus = top ? "已最小化微信到任务栏（自动回顶保活）" : "已最小化微信到任务栏（继续后台提醒）";
            }
            else if (action == "restored")
            {
                wechatOpen = true;
                lost = false;
                NavigationStatus = "已打开微信窗口";
            }
            UpdateBallAppearance();
        }, top);
    }
    void WakeWeChat(){
        if(waking || movingWechat || navigating)return;
        waking=true;
        NavigationStatus="正在唤醒微信";
        IntPtr known=lastWeChatHwnd;
        var thread=new Thread(delegate(){
            string err;
            try{err=Navigator.WakeWeChat(known,new WeChatUia(),null);}
            catch(Exception ex){err=ex.Message;}
            Panel.Dispatcher.BeginInvoke(new Action(delegate{
                waking=false;
                if(err!=null){
                    NavigationStatus=err;
                    tray.ShowBalloonTip(3500,"未能唤醒微信",err,System.Windows.Forms.ToolTipIcon.Info);
                }else{
                    wechatOpen = true;
                    lost = false;
                    NavigationStatus="已唤醒微信";
                    UpdateBallAppearance();
                }
            }));
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    internal void SyncStartupOnLaunch(){
        if (!State.AutoStart) return;
        string exe = StartupManager.CurrentExePath();
        var st = StartupManager.QueryStatus(exe);
        if (!st.Enabled && !st.DisabledBySystem)
        {
            string detail;
            StartupManager.SetEnabled(true, exe, out detail);
        }
    }
 }
}
