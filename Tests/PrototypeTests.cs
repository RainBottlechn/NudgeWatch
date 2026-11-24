using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NudgeWatch;
using WxWatch;

internal static class PrototypeTests
{
    static int passed, failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { passed++; }
        else { failed++; Console.WriteLine("FAIL: " + name); }
    }

    static void TestParser()
    {
        var p = new SessionParser();

        var digits=p.Parse("测试\n已置顶\n123\n10:15\n文本消息");
        Assert(digits.Unread<=0 && digits.Preview.Contains("123"),"numeric preview is not an unread badge");
        var s1 = p.Parse("产品研发群\n3\n张三: 下午两点评审\n10:15\n已置顶");
        Assert(s1.Title == "产品研发群", "s1 title");
        Assert(s1.Unread == 3, "s1 unread=3");
        Assert(s1.Time == "10:15", "s1 time");
        Assert(s1.IsPinned, "s1 pinned");
        Assert(s1.Preview == "张三: 下午两点评审", "s1 preview strips 已置顶");

        var s2 = p.Parse("广告群\n免打扰\n[5条] 优惠券来了\n昨天 22:10\n置顶");
        Assert(s2.Muted, "s2 muted");
        Assert(s2.IsPinned, "s2 pinned");
        Assert(s2.Unread == 5, "s2 unread from [5条]");
        Assert(s2.Preview == "优惠券来了", "s2 preview stripped");
        Assert(s2.Time == "昨天 22:10", "s2 time");

        var s3 = p.Parse("客户VIP群\n12条未读\n[有人@我] 李四: 请确认报价\n星期二");
        Assert(s3.Unread == 12, "s3 unread 12条未读");
        Assert(s3.Preview.Contains("[有人@我]"), "s3 @me preserved");
        Assert(s3.Time == "星期二", "s3 time");
    }

    static void TestDualModeRulesAndDetector()
    {
        // Mode A: 全部非强提醒模式
        var rulesNormal = new Rules { Default = Level.Normal };
        rulesNormal.Strong.Add("老板");
        rulesNormal.Strong.Add("*核心*");
        rulesNormal.Ignore.Add("*广告*");
        rulesNormal.Normal.Add("普通群");

        string why;
        Assert(rulesNormal.Resolve("普通群", "[有人@我] hi", out why) == Level.Normal, "explicit normal stays normal");
        Assert(rulesNormal.Resolve("其他群", "[有人@我] hi", out why) == Level.Strong, "unmarked @me elevates to Strong");
        Assert(rulesNormal.Resolve("老板", "hi", out why) == Level.Strong, "explicit strong");
        Assert(rulesNormal.Resolve("广告群", "[有人@我] hi", out why) == Level.Ignore, "ignore overrides all");

        // Mode B: 全部强提醒模式
        var rulesStrong = new Rules { Default = Level.Strong };
        rulesStrong.Normal.Add("闲聊群");
        rulesStrong.Ignore.Add("*广告*");

        Assert(rulesStrong.Resolve("工作群A", "普通消息", out why) == Level.Strong, "AllStrong defaults to Strong");
        Assert(rulesStrong.Resolve("闲聊群", "普通消息", out why) == Level.Normal, "AllStrong explicit Normal downgrades");
        Assert(rulesStrong.Resolve("广告群", "促销", out why) == Level.Ignore, "AllStrong ignore still ignores");

        var det = new ChangeDetector();
        var b = new List<SessionInfo>
        {
            new SessionInfo { Title = "普通群", Unread = 0, Preview = "hi" },
            new SessionInfo { Title = "核心项目群", Unread = 1, Preview = "old" },
            new SessionInfo { Title = "免打扰群", Unread = -1, Preview = "p1", Muted = true }
        };
        var d0 = det.Process(b, false, rulesNormal);
        Assert(d0.Events.Count == 0, "baseline has 0 events");
        Assert(det.BadgesSeen, "BadgesSeen detected");

        var n1 = new List<SessionInfo>
        {
            new SessionInfo { Title = "核心项目群", Unread = 2, Preview = "上线问题" },
            new SessionInfo { Title = "免打扰群", Unread = -1, Preview = "p2", Muted = true },
            new SessionInfo { Title = "老板", Unread = 1, Preview = "来一下" }
        };
        var d1 = det.Process(n1, false, rulesNormal);
        Assert(d1.Events.Count == 3, "3 events");
        Assert(d1.Events[0].Level == Level.Strong, "wildcard strong rule");
        Assert(d1.Events[1].Level == Level.Normal, "muted preview fallback");
        Assert(d1.Events[2].Level == Level.Strong, "new offscreen unread");

        // ResetBaseline (simulating recovery from tray disconnect) suppresses stale jump burst
        det.ResetBaseline();
        var dReset = det.Process(new List<SessionInfo>
        {
            new SessionInfo { Title = "核心项目群", Unread = 5, Preview = "missed while in tray" }
        }, false, rulesNormal);
        Assert(dReset.Events.Count == 0, "ResetBaseline rebuilds baseline silently");
    }

    static void TestConfigRoundtrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "wxwatch_test_cfg.json");
        if (File.Exists(tmp)) File.Delete(tmp);
        var cfg = AppConfig.LoadOrCreate(tmp);
        cfg.rules.defaultLevel = "Strong";
        cfg.rules.normal.Add("摸鱼群");
        AppConfig.Save(tmp, cfg);

        var loaded = AppConfig.LoadOrCreate(tmp);
        Assert(loaded.rules.defaultLevel == "Strong", "config roundtrip defaultLevel");
        Assert(loaded.rules.normal.Contains("摸鱼群"), "config roundtrip normal list");
        var r = loaded.ToRules();
        Assert(r.Default == Level.Strong && r.Normal.Contains("摸鱼群"), "ToRules maps AllStrong + Normal");
        File.Delete(tmp);
    }

    static void TestThemes()
    {
        Assert(NudgeWatch.ThemeManager.Keys.Length == 7, "7 themes registered (3 classic + 4 oil)");
        Assert(NudgeWatch.ThemeManager.DisplayName("system") == "跟随系统", "system display name");
        Assert(NudgeWatch.ThemeManager.DisplayName("oil-monet-lilies").Contains("莫奈"), "monet display name");
        Assert(NudgeWatch.ThemeManager.Resolve("day").Key == "day", "resolve day");
        Assert(NudgeWatch.ThemeManager.Resolve("night").Key == "night", "resolve night");
        Assert(NudgeWatch.ThemeManager.Resolve("oil-vangogh-starry").Key == "oil-vangogh-starry", "resolve oil theme");
        Assert(NudgeWatch.ThemeManager.Resolve("unknown-theme").Key == "day", "unknown theme falls back cleanly");
        var lostBrush = NudgeWatch.ThemeManager.BallBackground(true, false) as System.Windows.Media.SolidColorBrush;
        Assert(lostBrush != null && lostBrush.Color == System.Windows.Media.Colors.Firebrick, "lost ball brush stays Firebrick");

        Assert(NudgeWatch.ThemeManager.LoadEmbeddedImage("canvas-grain.png") != null, "embedded linen canvas grain loads");
        foreach (var p in NudgeWatch.ThemeManager.Presets)
        {
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Ink, p.Bg) >= 7.0, p.Key + " Ink/Bg >= 7.0");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Ink, p.Card) >= 7.0, p.Key + " Ink/Card >= 7.0");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Ink, p.SubCard) >= 7.0, p.Key + " Ink/SubCard >= 7.0");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.SelectionInk, p.Selection) >= 7.0, p.Key + " SelectionInk/Selection >= 7.0");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Muted, p.Card) >= 4.5, p.Key + " Muted/Card >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Muted, p.SubCard) >= 4.5, p.Key + " Muted/SubCard >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.AccentText, p.Accent) >= 4.5, p.Key + " AccentText/Accent >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Warning, p.Card) >= 4.5, p.Key + " Warning/Card >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.Warning, p.SubCard) >= 4.5, p.Key + " Warning/SubCard >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.L3, p.Card) >= 4.5, p.Key + " L3/Card >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.L4Ink, p.L4Bg) >= 7.0, p.Key + " L4Ink/L4Bg >= 7.0");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.L4Ink, p.L4SubBg) >= 7.0, p.Key + " L4Ink/L4SubBg >= 7.0");
            if (p.IsOil)
            {
                var art = NudgeWatch.ThemeManager.LoadEmbeddedImage(p.ArtworkResource);
                Assert(art != null && art.PixelWidth == 1520 && art.PixelHeight == 280, p.Key + " embedded museum artwork loads 1520x280 (200% DPI)");
                Assert(NudgeWatch.ThemeManager.LoadEmbeddedImage(p.ArtworkThumb) != null, p.Key + " theme picker thumbnail loads");
            }
            // 0.8.0 语义色：L4 强调色、色块文字、主操作与危险色不撞色
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.L4Accent, p.L4Bg) >= 4.5, p.Key + " L4Accent/L4Bg >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.OnL3, p.L3) >= 4.5, p.Key + " OnL3/L3 >= 4.5");
            Assert(NudgeWatch.ThemeManager.ContrastRatio(p.OnL2, p.L2) >= 4.5, p.Key + " OnL2/L2 >= 4.5");
            Assert(NudgeWatch.ThemeManager.HueDistance(p.Accent, p.L3) >= 30, p.Key + " Accent vs L3 hue >= 30");
            Assert(NudgeWatch.ThemeManager.HueDistance(p.Accent, p.Warning) >= 12, p.Key + " Accent vs Warning hue >= 12");
        }

        Assert(!Navigator.IsChatMainWindowCandidate("WxTrayIconMessageWindow", "Qt51514QWindowIcon", 640, 480), "never show WxTrayIconMessageWindow");
        Assert(!Navigator.IsChatMainWindowCandidate("", "Qt51514QWindowIcon", 800, 600), "never show untitled Qt helper window");
        Assert(Navigator.IsChatMainWindowCandidate("微信", "Qt51514QWindowIcon", 900, 700), "accept real WeChat window");
        Assert(Navigator.IsChatMainWindowCandidate("WeChat", "mmui::MainWindow", 900, 700), "accept English WeChat window");
        Assert(!Navigator.IsTrayNotificationElement("微信", "Appid:Tencent.WeChat", "Taskbar.TaskListButtonAutomationPeer", false), "skip pinned taskbar shortcut");
        Assert(!Navigator.IsTrayNotificationElement("NudgeWatch 0.6.2", "NotifyItemIcon", "SystemTray.Normal", true), "skip NudgeWatch own tray icon");
        Assert(Navigator.IsTrayNotificationElement("微信", "NotifyItemIcon_1", "SystemTray.Normal", false), "match Win11 tray notify icon");
        Assert(Navigator.IsTrayNotificationElement("微信", "", "Button", true), "match overflow tray icon");
    }

    static void TestWpfUiAndShellSta()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "nw_proto_ui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var frames = new List<NudgeWatch.Frame>
            {
                new NudgeWatch.Frame
                {
                    Status = "测试连接",
                    Chats = new List<SessionInfo>
                    {
                        new SessionInfo { Title = "核心群", Unread = 2, Preview = "发布完成", IsPinned = true },
                        new SessionInfo { Title = "闲聊群", Unread = 1, Preview = "hi", IsPinned = false }
                    }
                },
                new NudgeWatch.Frame { Status = "等待微信会话列表", Chats = new List<SessionInfo>() },
                new NudgeWatch.Frame { Status = "等待微信会话列表", Chats = new List<SessionInfo>() },
                new NudgeWatch.Frame { Status = "等待微信会话列表", Chats = new List<SessionInfo>() }
            };
            using (var shell = new NudgeWatch.Shell(tmp, () => frames[0], () => new DateTime(2026, 11, 18, 16, 0, 0)))
            {
                shell.State.Groups.Add(new NudgeWatch.GroupDef { Name = "工作", Chats = new List<string> { "核心群" } });
                shell.State.Levels["核心群"] = 3;
                shell.Start();

                Assert(Math.Abs(shell.Panel.Width - 380) < 1, "Panel width stays 380");
                shell.Forms.OpenQuick();
                var quick = shell.Forms.Quick;
                Assert(Math.Abs(quick.Width - 440) < 1 && Math.Abs(quick.Height - 84) < 1, "Capture is 440x84");
                quick.Hide();

                var editor = shell.Forms.Editor(shell.Panel, null);
                Assert(editor != null && editor.FindName("Save") != null, "Editor loads cleanly with Save button");
                editor.Close();

                var applyMethod = typeof(NudgeWatch.Shell).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                applyMethod.Invoke(shell, new object[] { frames[0] });
                shell.Panel.Show();
                var chatsTree = shell.Panel.FindName("Chats") as System.Windows.Controls.TreeView;
                Assert(chatsTree != null && chatsTree.Items.Count >= 2, "Tree populated with groups");
                var branchList = chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().ToList();
                Assert(NudgeWatch.Rows.NameOf(branchList[0]).StartsWith("新消息"), "新消息动态视图置顶显示");
                var workBranch = branchList.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("工作"));
                var liveBranch = branchList.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("新消息"));
                var firstLeaf = workBranch.Items[0] as System.Windows.Controls.TreeViewItem;
                Assert(workBranch != null && NudgeWatch.Rows.NameOf(workBranch).Contains("2 未读"), "Group header shows unread sum");
                Assert(NudgeWatch.Rows.NameOf(firstLeaf).Contains("强提醒"), "Leaf item shows explicit rule tag");
                Assert(workBranch.Header is System.Windows.FrameworkElement && firstLeaf.Header is System.Windows.FrameworkElement, "Tree rows render structured visuals");
                Assert(liveBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().Any(n => (string)n.Tag == "核心群") &&
                    liveBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().Any(n => (string)n.Tag == "闲聊群"),
                    "新消息同时保留已归组与未归组活跃会话");

                var buildMove = typeof(NudgeWatch.Shell).GetMethod("BuildChatMoveMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var moveMenu = (System.Windows.Controls.ContextMenu)buildMove.Invoke(shell, new object[] { new[] { "核心群" } });
                var moveHeaders = moveMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                Assert(!moveHeaders.Contains("新消息") && moveHeaders.Any(h => h != null && h.Contains("移出当前分组")),
                    "分组菜单不包含新消息且支持移出分组");

                var assignChats = typeof(NudgeWatch.Shell).GetMethod("AssignChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                assignChats.Invoke(shell, new object[] { new[] { "闲聊群" }, "工作" });
                branchList = chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().ToList();
                workBranch = branchList.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("工作"));
                liveBranch = branchList.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("新消息"));
                Assert(workBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().Any(n => (string)n.Tag == "闲聊群") &&
                    liveBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().Any(n => (string)n.Tag == "闲聊群"),
                    "将闲聊群加入工作组后，新消息中的闲聊群不会消失");

                var removeChats = typeof(NudgeWatch.Shell).GetMethod("RemoveChatsFromGroups", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                removeChats.Invoke(shell, new object[] { new[] { "闲聊群" } });
                workBranch = chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("工作"));
                Assert(!workBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().Any(n => (string)n.Tag == "闲聊群"),
                    "移出分组后会话仅从目标分组移除");

                var statusTb = shell.Panel.FindName("Status") as System.Windows.Controls.TextBlock;
                Assert(shell.Ball.ToolTip != null && statusTb != null && statusTb.Text.Contains("测试连接"), "Status text updated");

                foreach (var key in NudgeWatch.ThemeManager.Keys)
                {
                    NudgeWatch.ThemeManager.Apply(key);
                    Assert(app.Resources["BgBrush"] != null && app.Resources["OilStripeBrush"] != null && app.Resources["OilBannerBrush"] != null, "Theme applied resources: " + key);
                    Assert(app.Resources[System.Windows.SystemColors.HighlightBrushKey] != null, "System HighlightBrushKey overridden: " + key);
                    if (NudgeWatch.ThemeManager.Resolve(key).IsOil)
                        Assert(app.Resources["OilBannerBrush"] is System.Windows.Media.ImageBrush && app.Resources["CanvasOverlayBrush"] is System.Windows.Media.ImageBrush, "Oil theme applies real painting + canvas brushes: " + key);
                }

                // Verify 新消息 + 已收录 · 待归组 (collapsed by default) + ToggleChatExpand + Multi-select
                var knownField = typeof(NudgeWatch.Shell).GetField("knownChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var knownPool = (NudgeWatch.KnownChats)knownField.GetValue(shell);
                knownPool.Observe(new[] { "离线录入群A", "离线录入群B" }, new DateTime(2026, 11, 20, 10, 0, 0), t => "新消息");
                var treeChanged = typeof(NudgeWatch.Shell).GetMethod("TreeChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                treeChanged.Invoke(shell, null);
                var allBranches = chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().ToList();
                var newMsgBranch = allBranches.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("新消息"));
                var archivedBranch = allBranches.FirstOrDefault(b => NudgeWatch.Rows.NameOf(b).StartsWith("已收录 · 待归组"));
                Assert(newMsgBranch != null && newMsgBranch.IsExpanded, "Live ungrouped chats appear under 新消息 (expanded)");
                Assert(archivedBranch != null && !archivedBranch.IsExpanded && archivedBranch.Items.Count == 2, "Recorded ungrouped chats appear under 已收录 · 待归组 (collapsed by default)");

                ClickButton(shell.Panel, "ToggleChatExpand");
                Assert(chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().All(b => !b.IsExpanded), "ToggleChatExpand collapses all groups");
                ClickButton(shell.Panel, "ToggleChatExpand");
                Assert(chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().All(b => b.IsExpanded), "ToggleChatExpand expands all groups");

                Assert(shell.Panel.FindName("ChatBatchBar") == null, "ChatBatchBar removed in favor of context menu");
                ClickButton(shell.Panel, "ChatMulti");
                Assert(((string)shell.Panel.Part<System.Windows.Controls.Button>("ChatMulti").Content).StartsWith("完成多选") &&
                    ((System.Windows.Controls.TreeViewItem)chatsTree.Items[0]).Header is System.Windows.Controls.CheckBox, "ChatMulti enables checkbox multi-select mode");
                var selField = typeof(NudgeWatch.Shell).GetField("selectedChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var selSet = (HashSet<string>)selField.GetValue(shell);
                selSet.Add("核心群");
                selSet.Add("闲聊群");
                var leafMenu = new System.Windows.Controls.ContextMenu();
                var fillChatMenu = typeof(NudgeWatch.Shell).GetMethod("FillChatLeafMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                fillChatMenu.Invoke(shell, new object[] { leafMenu, "核心群" });
                var chatMenuHeaders = leafMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                Assert(chatMenuHeaders.Contains("将已选 2 项加入分组") &&
                    chatMenuHeaders.Contains("导出已选会话记录") &&
                    chatMenuHeaders.Contains("强提醒") &&
                    chatMenuHeaders.Contains("取消多选"),
                    "Right-click leaf menu carries all batch actions including cancel multi-select");
                var cancelMulti = leafMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "取消多选");
                cancelMulti.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert((string)shell.Panel.Part<System.Windows.Controls.Button>("ChatMulti").Content == "多选" &&
                    !(((System.Windows.Controls.TreeViewItem)chatsTree.Items[0]).Header is System.Windows.Controls.CheckBox) &&
                    selSet.Count == 0, "Context menu 取消多选 exits multi-select cleanly");

                shell.Alerts.Push(new NudgeWatch.Notice { Level = 2, Title = "L2 Test", Body = "Weak" });
                Assert(shell.Alerts.Window.Visibility == System.Windows.Visibility.Visible, "L2 alert shown");
                shell.Alerts.Next();

                shell.Alerts.Push(new NudgeWatch.Notice { Level = 3, Title = "L3 Test", Body = "Strong" });
                Assert(Math.Abs(shell.Alerts.Window.Width - 440) < 1 && Math.Abs(shell.Alerts.Window.Height - 240) < 1, "L3 alert dimensions 440x240");
                shell.Alerts.Next();

                NudgeWatch.ThemeManager.Apply("day");
                NudgeWatch.ThemeManager.StyleAlert(shell.Alerts.Window, 4);
                var cardBorder = shell.Alerts.Window.FindName("CardBorder") as System.Windows.Controls.Border;
                var l4Brush = cardBorder != null ? cardBorder.Background as System.Windows.Media.SolidColorBrush : null;
                Assert(l4Brush != null && l4Brush.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(NudgeWatch.ThemeManager.Resolve("day").L4Bg), "L4 alert styles CardBorder with L4Bg");

                shell.Panel.Hide();
                shell.Ball.Hide();
            }

            using (var emptyShell = new NudgeWatch.Shell(tmp, () => frames[1], () => DateTime.Now))
            {
                var m = typeof(NudgeWatch.Shell).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                m.Invoke(emptyShell, new object[] { frames[1] });
                m.Invoke(emptyShell, new object[] { frames[2] });
                m.Invoke(emptyShell, new object[] { frames[3] });
                var surface = emptyShell.Ball.FindName("Surface") as System.Windows.Controls.Border;
                var bg = surface != null ? surface.Background as System.Windows.Media.SolidColorBrush : null;
                Assert(bg != null && bg.Color == System.Windows.Media.Colors.Firebrick, "3 empty frames turn Ball Surface to Firebrick immediately");
                var lostStatus = emptyShell.Panel.FindName("Status") as System.Windows.Controls.TextBlock;
                Assert(lostStatus != null && lostStatus.Text.Contains("托盘"), "Status shows tray wake hint when lost");
            }
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [STAThread]
    static int Main()
    {
        TestParser();
        TestDualModeRulesAndDetector();
        TestConfigRoundtrip();
        TestThemes();
        TestWpfUiAndShellSta();
        TestWorkLifecycle();
        TestViewportAndKnownChats();
        TestSyncChatProtection();
        TestArchive();
        TestUiAndArtV080();
        TestStartupManager();
        TestTargetedChatQuiet();
        TestBallTogglePanelToTray();
        Console.WriteLine("Tests finished: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    static void TestWorkLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "nw_work_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = new DateTime(2026, 11, 20, 16, 0, 0);
        try
        {
            File.WriteAllText(Path.Combine(root, "state.json"),
                "{\"Reminders\":[{\"Id\":\"old\",\"Title\":\"Legacy\",\"Start\":" + now.AddHours(1).Ticks +
                "},{\"Id\":\"done\",\"Title\":\"Completed legacy\",\"Done\":true}],\"Groups\":{\"会话\":\"工作\"}}");
            using (var shell = new NudgeWatch.Shell(root, () => new NudgeWatch.Frame(), () => now))
            {
                Assert(shell.State.Reminders[0].Status == "active" && shell.State.Reminders[1].Status == "completed" &&
                    shell.State.Reminders.All(r => r.Kind == "reminder") && shell.State.Groups[0].Chats[0] == "会话", "Legacy native state loads");
                shell.Alerts.Sound = false;
                var periodic = NudgeWatch.Capture.Parse("30分钟后 周期 @每天", now);
                shell.SaveReminder(periodic);
                long original = periodic.Start;
                var list = shell.Panel.Part<System.Windows.Controls.ListBox>("Agenda");
                list.SelectedItem = list.Items.Cast<NudgeWatch.AgendaRow>().Single(x => x.Item.Id == periodic.Id);
                ClickButton(shell.Panel, "Done");
                Assert(periodic.Start == original + TimeSpan.TicksPerDay && !periodic.Done &&
                    shell.Panel.Part<System.Windows.Controls.DockPanel>("UndoBar").Visibility == System.Windows.Visibility.Visible, "Recurring completion advances and shows undo");
                ClickButton(shell.Panel, "Undo");
                var restored = shell.State.Reminders.Single(r => r.Id == periodic.Id);
                Assert(restored.Start == original && restored.Status == "active" && !restored.Done, "Undo restores original recurrence time");
                list.SelectedItem = list.Items.Cast<NudgeWatch.AgendaRow>().Single(x => x.Item.Id == periodic.Id);
                ClickButton(shell.Panel, "Delete");
                Assert(restored.Status == "deleted" && new NudgeWatch.Store(root).Load().Reminders.Any(r => r.Id == restored.Id), "Soft delete keeps persisted item");
                shell.Panel.Part<System.Windows.Controls.ComboBox>("Scope").SelectedIndex = 3;
                list.SelectedIndex = 0;
                ClickButton(shell.Panel, "Done");
                Assert(restored.Status == "active" && !restored.Done, "Trash restore returns active item");

                var untimed = NudgeWatch.Capture.Parse("#任务 核对流水", now);
                Assert(untimed.Kind == "task" && untimed.Start == 0 && untimed.Title == "核对流水", "Untimed quick task");
                shell.SaveReminder(untimed);
                var task = NudgeWatch.Capture.Parse("0分钟后 提交周报 #任务", now);
                shell.SaveReminder(task);
                Assert(shell.Alerts.Current != null && shell.Alerts.Current.Task && task.NextFollowUp == now.Date.AddDays(1).AddHours(9.5).Ticks, "Task schedules next day 09:30");
                ClickButton(shell.Alerts.Window, "Ack");
                Assert(!task.Done && shell.Panel.Part<System.Windows.Controls.TextBlock>("PendingTasks").Text.Contains("1 项"), "Task remains pending after dismiss");
                now = new DateTime(task.NextFollowUp);
                shell.Tick();
                shell.Alerts.ScheduleAction(task.Id, 30);
                Assert(!task.Done && task.NextFollowUp == now.AddMinutes(30).Ticks && !task.Awaiting(now), "Task manual follow-up overrides next day and exits awaiting");
                now = new DateTime(task.NextFollowUp);
                shell.Tick();
                Assert(task.Awaiting(now), "Task re-enters awaiting when deferred follow-up arrives");
                shell.Alerts.ScheduleAction(task.Id, 0);
                Assert(task.Done && task.Status == "completed" && shell.State.Reminders.Any(r => r.Id == untimed.Id && !r.Done), "Confirmation completes only selected task");

                // 测试待确认任务切换至「进行中（停止追催）」与「暂缓」及撤销
                var stateTask = NudgeWatch.Capture.Parse("0分钟后 状态流转测试 #任务", now);
                shell.SaveReminder(stateTask);
                Assert(stateTask.Awaiting(now), "New due task enters awaiting");
                shell.Alerts.ScheduleAction(stateTask.Id, -2);
                Assert(stateTask.TaskState == "in_progress" && !stateTask.Awaiting(now) && stateTask.NextFollowUp == 0, "Task transitions to in_progress and stops follow-up");
                shell.UndoWork();
                stateTask = shell.State.Reminders.Single(r => r.Id == stateTask.Id);
                Assert(stateTask.Awaiting(now), "Undo restores task awaiting state");
                shell.Alerts.ScheduleAction(stateTask.Id, -3);
                Assert(stateTask.IsPaused && !stateTask.Awaiting(now) && stateTask.TaskDue(now) == 0, "Task transitions to paused");
                long prevDelivered = stateTask.Delivered;
                now = now.AddDays(2);
                shell.Tick();
                Assert((shell.Alerts.Current == null || shell.Alerts.Current.ReminderId != stateTask.Id) && stateTask.Delivered == prevDelivered, "Paused task does not trigger alerts");
                if (shell.Alerts.Current != null) ClickButton(shell.Alerts.Window, "Ack");
                shell.Alerts.ScheduleAction(stateTask.Id, 0);
                Assert(stateTask.Done, "Paused task can still be completed");

                // 测试提醒右键菜单包含完成入口
                var oneShotRem = NudgeWatch.Capture.Parse("30分钟后 单次提醒", now);
                shell.SaveReminder(oneShotRem);
                var agendaMenu = new System.Windows.Controls.ContextMenu();
                var fillMenu = typeof(NudgeWatch.Shell).GetMethod("FillWorkMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                fillMenu.Invoke(shell, new object[] { agendaMenu, oneShotRem });
                var remCompleteMenuItem = agendaMenu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => (m.Header as string ?? "").Contains("完成"));
                Assert(remCompleteMenuItem != null, "Reminder context menu includes Complete action");
                remCompleteMenuItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert(oneShotRem.Done && oneShotRem.Status == "completed", "Reminder context menu Complete marks reminder completed");
                shell.UndoWork();
                Assert(!shell.State.Reminders.Single(r => r.Id == oneShotRem.Id).Done, "Undo restores completed reminder");
                shell.State.Reminders.RemoveAll(r => r.Id == oneShotRem.Id);
                shell.SaveReminder(untimed);
                var workTree = shell.Panel.Part<System.Windows.Controls.TreeView>("Tasks");
                var openTask = workTree.Items.Cast<System.Windows.Controls.TreeViewItem>().SelectMany(n => n.Items.Cast<System.Windows.Controls.TreeViewItem>())
                    .Single(n => ((Reminder)n.Tag).Id == untimed.Id);
                var derive = openTask.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "派生提醒副本");
                derive.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                var copy = shell.State.Reminders.Single(r => r.LinkedId == untimed.Id);
                Assert(copy.Kind == "reminder" && copy.Start == now.AddMinutes(10).Ticks, "Derived reminder links to original task");
                shell.Alerts.ScheduleAction(copy.Id, 0);
                Assert(copy.Done && !untimed.Done, "Completing the derived reminder leaves the task active");
                shell.UndoWork();
                shell.UndoWork();
                Assert(shell.State.Reminders.All(r => r.Id != copy.Id) && shell.State.Reminders.Single(r => r.Id == untimed.Id).Status == "active",
                    "Undo removes derived copy and restores work item");

                // Test task quick group switch via context submenu + Undo + ToggleTaskExpand + TaskMulti
                var projGroup = new NudgeWatch.TaskGroupDef { Id = "proj1", Name = "专项" };
                shell.State.TaskGroups.Add(projGroup);
                shell.SaveReminder(untimed);
                var taskNode = workTree.Items.Cast<System.Windows.Controls.TreeViewItem>()
                    .SelectMany(n => n.Items.Cast<System.Windows.Controls.TreeViewItem>())
                    .Single(n => ((Reminder)n.Tag).Id == untimed.Id);
                var moveSub = taskNode.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>().Single(m => ((string)m.Header) == "移动到分组");
                var targetItem = moveSub.Items.Cast<System.Windows.Controls.MenuItem>().Single(m => ((string)m.Header) == "专项");
                targetItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert(shell.State.Reminders.Single(r => r.Id == untimed.Id).GroupId == "proj1", "Task quick group switch updates GroupId in one click");
                shell.UndoWork();
                Assert(shell.State.Reminders.Single(r => r.Id == untimed.Id).GroupId == "", "Undo reverts quick task group switch");

                ClickButton(shell.Panel, "ToggleTaskExpand");
                Assert(workTree.Items.Cast<System.Windows.Controls.TreeViewItem>().All(b => !b.IsExpanded), "ToggleTaskExpand collapses all task groups");
                ClickButton(shell.Panel, "ToggleTaskExpand");
                Assert(workTree.Items.Cast<System.Windows.Controls.TreeViewItem>().All(b => b.IsExpanded), "ToggleTaskExpand expands all task groups");
                ClickButton(shell.Panel, "TaskMulti");
                Assert(shell.Panel.Part<System.Windows.Controls.Border>("TaskBatchBar").Visibility == System.Windows.Visibility.Visible, "TaskMulti shows batch bar");
                ClickButton(shell.Panel, "TaskBatchClear");
                Assert(shell.Panel.Part<System.Windows.Controls.Border>("TaskBatchBar").Visibility == System.Windows.Visibility.Collapsed, "TaskBatchClear exits task multi-select");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static void ClickButton(System.Windows.Window window, string name)
    {
        window.Part<System.Windows.Controls.Button>(name).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    static void TestViewportAndKnownChats()
    {
        var frame = new NudgeWatch.Frame { ScrollPercent = 23, Chats = new List<SessionInfo> { new SessionInfo { Title = "工作群", Unread = 2 } } };
        Assert(Shell.IsViewportDrifted(frame, new[] { "工作群" }, false), "ScrollPattern takes precedence for drift");
        frame.ScrollPercent = 0;
        Assert(!Shell.IsViewportDrifted(frame, new[] { "不在视口" }, true), "Confirmed top overrides heuristic");
        frame.ScrollPercent = -1;
        Assert(Shell.IsViewportDrifted(frame, new[] { "不在视口" }, false), "Missing pinned anchors indicate drift");
        frame.NavTotalUnread = 5;
        Assert(Shell.IsViewportDrifted(frame, new[] { "工作群" }, false), "Navigation total over visible unread indicates drift");
        Assert(SearchResults.IsExact("工作群", "群聊", "mmui::SearchContentCellView", "search_item_工作群", "工作群\n预览"), "Exact confirmed group row");
        Assert(!SearchResults.IsExact("工作群", "聊天记录", "mmui::SearchContentCellView", "search_item_工作群", "工作群"), "History result never opens");
        Assert(!SearchResults.IsExact("工作群", "群聊", "mmui::SearchContentCellView", "search_item_工作群1", "工作群"), "Similar result never opens");
        Assert(KnownChats.PinyinInitials("核心工作群") == "HXGZQ", "Pinyin initials filter common Hanzi");

        string root = Path.Combine(Path.GetTempPath(), "nw_chats_" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.Now;
            var chats = new KnownChats(root);
            Assert(chats.Observe(new[] { "工作群", "工作群", "" }, now, t => "工作") && chats.All.Count() == 1, "Known pool deduplicates captured names");
            Assert(!chats.Observe(new[] { "工作群" }, now.AddSeconds(5), t => "工作"), "Repeated observations are not new records");
            Assert(new KnownChats(root).Find("工作群").SeenCount == 1, "Known pool throttles writes for repeat frames");
            chats.Annotate("工作群", "核心组", "重点", now.AddSeconds(10));
            var entry = new KnownChats(root).Find("工作群");
            Assert(entry.Alias == "核心组" && entry.LastGroup == "重点" && entry.SeenCount == 2 && entry.FirstSeenAt == now.Ticks,
                "Group, alias and count persist together");
        }
        finally { Directory.Delete(root, true); }
    }

    static void TestSyncChatProtection()
    {
        var state = new NudgeWatch.State();
        state.Groups.Add(new NudgeWatch.GroupDef { Name = "工作", Chats = new List<string> { "核心群", " 产品群 " } });
        string poolRoot = Path.Combine(Path.GetTempPath(), "nw_sync_pool_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pool = new NudgeWatch.KnownChats(poolRoot);
            var t0 = new DateTime(2026, 11, 20, 9, 0, 0);
            pool.Observe(new[] { "历史群" }, t0, t => "新消息");
            var synced = NudgeWatch.Shell.BuildSyncedChatSet(state, pool);
            Assert(synced.Contains("核心群") && synced.Contains("产品群") && synced.Contains("历史群"),
                "BuildSyncedChatSet collects grouped and known chats");

            int skipped;
            var filtered = NudgeWatch.Shell.FilterUnsyncedChats(
                new[] { "核心群", " 核心群 ", "历史群", "新群A", "新群A", "", "  ", "新群B" },
                synced, out skipped);
            Assert(filtered.Count == 2 && filtered[0] == "新群A" && filtered[1] == "新群B" && skipped == 2,
                "FilterUnsyncedChats skips synced items, trims whitespace, and deduplicates");
        }
        finally { if (Directory.Exists(poolRoot)) Directory.Delete(poolRoot, true); }

        string shellRoot = Path.Combine(Path.GetTempPath(), "nw_sync_shell_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shellRoot);
        try
        {
            var now = new DateTime(2026, 11, 20, 10, 0, 0);
            using (var shell = new NudgeWatch.Shell(shellRoot, () => new NudgeWatch.Frame(), () => now))
            {
                shell.State.Groups.Add(new NudgeWatch.GroupDef { Name = "工作", Chats = new List<string> { "核心群" } });
                var knownField = typeof(NudgeWatch.Shell).GetField("knownChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var knownPool = (NudgeWatch.KnownChats)knownField.GetValue(shell);
                knownPool.Observe(new[] { "历史群" }, now.AddHours(-1), t => "新消息");
                knownPool.Annotate("历史群", "老备注", "新消息", now.AddHours(-1));
                long prevSeenCount = knownPool.Find("历史群").SeenCount;

                List<string> newTitles;
                int skipped;
                int added = shell.ApplyScannedChats(new[] { "核心群", " 核心群 ", "历史群", "新群A", "新群B", "新群A" }, out newTitles, out skipped);
                Assert(added == 2 && newTitles.Count == 2 && newTitles.Contains("新群A") && newTitles.Contains("新群B") && skipped == 2,
                    "ApplyScannedChats only returns unsynced chats and counts skipped synced items");
                Assert(knownPool.Find("历史群").SeenCount == prevSeenCount && knownPool.Find("历史群").Alias == "老备注",
                    "ApplyScannedChats protects existing synced chat metadata from repeated observation");
                Assert(shell.State.Groups.Single(g => g.Name == "工作").Chats.SequenceEqual(new[] { "核心群" }),
                    "ApplyScannedChats preserves existing group membership");
                Assert(knownPool.Find("新群A") != null && knownPool.Find("新群B") != null,
                    "ApplyScannedChats records newly discovered chats into knownChats");

                List<string> secondNew;
                int secondSkipped;
                int secondAdded = shell.ApplyScannedChats(new[] { "核心群", " 核心群 ", "历史群", "新群A", "新群B" }, out secondNew, out secondSkipped);
                Assert(secondAdded == 0 && secondNew.Count == 0 && secondSkipped == 4,
                    "Second scan skips all previously synced chats");
            }
        }
        finally { if (Directory.Exists(shellRoot)) Directory.Delete(shellRoot, true); }
    }

    static void TestArchive()
    {
        string root = Path.Combine(Path.GetTempPath(), "nw_archive_" + Guid.NewGuid().ToString("N"));
        string parts = Path.Combine(root, "archive");
        Directory.CreateDirectory(parts);
        var day = new DateTime(2026, 11, 20, 8, 0, 0);
        try
        {
            string expired = Path.Combine(parts, "messages-" + day.AddDays(-30).ToString("yyyy-MM-dd") + ".jsonl");
            string retained = Path.Combine(parts, "messages-" + day.AddDays(-29).ToString("yyyy-MM-dd") + ".jsonl");
            string unrelated = Path.Combine(parts, "messages-not-data.jsonl");
            foreach (var file in new[] { expired, retained, unrelated }) File.WriteAllText(file, "");
            var archive = new MessageArchive(root);
            archive.Maintain(day, 30);
            archive.Add(new[] {
                ArchiveSample(day, "核心群", "第一条", false), ArchiveSample(day.AddMinutes(2), "核心群", "第一条", true),
                ArchiveSample(day.AddMinutes(2), "其他群", "第一条", true), ArchiveSample(day.AddMinutes(5), "核心群", "第一条", true),
            });
            archive.Flush();
            Assert(!File.Exists(expired) && File.Exists(retained) && File.Exists(unrelated), "Archive retention keeps 30 calendar days and ignores unrelated files");
            var read = archive.Read(day, day);
            Assert(read.Count == 3 && !read[0].muted && read[1].muted && read.All(e => e.hash.Length > 20), "Five-minute dedup isolates by chat and keeps muted events");
            archive = new MessageArchive(root);
            archive.Maintain(day, 30);
            archive.Add(new[] { ArchiveSample(day.AddMinutes(6), "核心群", "第一条", false) });
            archive.Flush();
            Assert(archive.Read(day, day).Count == 3, "Archive seeds dedup from disk on restart");
            archive.Maintain(day.AddDays(1), 30);
            archive.Flush();
            Assert(!File.Exists(retained), "Archive evicts expiring partition at date rollover");
            archive.Maintain(day.AddDays(170), 0);
            archive.Flush();
            Assert(archive.Read(day, day).Count == 3, "Permanent retention preserves partitions");
            var row = ArchiveSample(day, "核心群", "=1+2, \"复核\" @我", false);
            Assert(ArchiveWindow.Matches(row, "复核", 2) && !ArchiveWindow.Matches(row, "不存在", 2), "Archive keyword and mention filters compose");
            string csv = Path.Combine(root, "test.csv");
            ArchiveWindow.WriteExport(csv, new[] { row }, 1, true);
            var bytes = File.ReadAllBytes(csv);
            Assert(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && File.ReadAllText(csv).Contains("\"'=1+2, \"\"复核\"\" @我\""),
                "CSV has BOM, quotes and spreadsheet formula escaping");
            Assert(MessageArchive.Export(new[] { row }, 0, true).Contains("### 核心群") &&
                MessageArchive.Export(new[] { row }, 0, false).Contains("**核心群**") &&
                MessageArchive.Export(new[] { row }, 2, true).Contains("[核心群]"), "Exports grouped Markdown, timeline Markdown and TXT");
        }
        finally { Directory.Delete(root, true); }
    }

    static ArchiveEntry ArchiveSample(DateTime at, string title, string preview, bool muted)
    {
        return new ArchiveEntry { ts = at.Ticks, time = at.ToString("HH:mm:ss"), chat = title,
            group = "测试组", preview = preview, unread = 1, level = 3, muted = muted };
    }

    static void TestUiAndArtV080()
    {
        // 1. 快捕实时解析预览
        var now = new DateTime(2026, 11, 20, 14, 0, 0);
        var rem = NudgeWatch.Capture.Parse("15:00 周会评审 #避免1h @强 @每周", now);
        string desc = NudgeWatch.Forms.Describe(rem, now);
        Assert(desc.Contains("今天 15:00") && desc.Contains("周会评审") && desc.Contains("每周") && desc.Contains("免打扰1h") && desc.Contains("重要"),
            "Capture live preview describes time, title, repeat, quiet and level");
        var untimed = NudgeWatch.Capture.Parse("#任务 核对流水", now);
        string taskDesc = NudgeWatch.Forms.Describe(untimed, now);
        Assert(taskDesc.Contains("任务") && taskDesc.Contains("未定时") && taskDesc.Contains("核对流水"),
            "Capture live preview handles untimed tasks");

        // 2. 品牌徽标与四态托盘图标
        foreach (NudgeWatch.BrandArt.TrayState st in Enum.GetValues(typeof(NudgeWatch.BrandArt.TrayState)))
        {
            var bmp = NudgeWatch.BrandArt.Render(32, st);
            Assert(bmp != null && bmp.PixelWidth == 32 && bmp.PixelHeight == 32, "BrandArt renders 32px bitmap for " + st);
            using (var ico = NudgeWatch.BrandArt.TrayIcon(st))
                Assert(ico != null && ico.Width >= 16, "BrandArt generates tray icon for " + st);
        }

        // 3. 弹窗分级视觉（油画主题 L3/L4 保持严重度色轨；L2 保留油画细线）+ 悬浮球四态 + 空状态帧健壮性
        string root = Path.Combine(Path.GetTempPath(), "nw_v080_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var shell = new NudgeWatch.Shell(root, () => new NudgeWatch.Frame(), () => now))
            {
                shell.Alerts.Sound = false;
                var apply = typeof(NudgeWatch.Shell).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                // Frame.Status == null 不应引起 UpdateStatusDot 空引用
                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = null, Chats = new List<SessionInfo> { new SessionInfo { Title = "群A", Unread = 120 } } } });
                Assert(shell.Ball.Part<System.Windows.Controls.TextBlock>("Badge").Text == "99+", "Ball caps unread display at 99+");
                Assert(shell.Ball.Part<System.Windows.FrameworkElement>("LogoMark").Visibility == System.Windows.Visibility.Collapsed, "Ball hides logo when unread > 0");

                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 1 个可见会话", Chats = new List<SessionInfo> { new SessionInfo { Title = "群A", Unread = 0 } } } });
                Assert(shell.Ball.Part<System.Windows.FrameworkElement>("LogoMark").Visibility == System.Windows.Visibility.Visible, "Ball shows brand logo when unread == 0");

                // 悬浮球微信开关按钮三态可视化（打开中 / 最小化保活 / 已进托盘）
                var toggleBtn = shell.Ball.FindName("WeChatToggle") as System.Windows.Controls.Button;
                var bubblePath = shell.Ball.FindName("WeChatBubble") as System.Windows.Shapes.Path;
                var minIcon = shell.Ball.FindName("WeChatMinIcon") as System.Windows.Shapes.Path;
                var openIcon = shell.Ball.FindName("WeChatOpenIcon") as System.Windows.Shapes.Path;
                var stateDot = shell.Ball.FindName("WeChatStateDot") as System.Windows.Shapes.Ellipse;
                Assert(toggleBtn != null && bubblePath != null && minIcon != null && openIcon != null && stateDot != null,
                    "Ball renders WeChatToggle pill and vector icons");

                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 1 个可见会话", WindowVisible = true, WindowIconic = false, Chats = new List<SessionInfo> { new SessionInfo { Title = "群A", Unread = 0 } } } });
                Assert(minIcon.Visibility == System.Windows.Visibility.Visible && openIcon.Visibility == System.Windows.Visibility.Collapsed,
                    "WeChatToggle shows minimize icon when WeChat is visible");

                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 1 个可见会话", WindowVisible = true, WindowIconic = true, Chats = new List<SessionInfo> { new SessionInfo { Title = "群A", Unread = 0 } } } });
                Assert(openIcon.Visibility == System.Windows.Visibility.Visible && minIcon.Visibility == System.Windows.Visibility.Collapsed,
                    "WeChatToggle shows open icon when WeChat is minimized");

                for (int miss = 0; miss < 3; miss++)
                    apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "等待微信会话列表", Chats = new List<SessionInfo>() } });
                Assert(openIcon.Visibility == System.Windows.Visibility.Visible && minIcon.Visibility == System.Windows.Visibility.Collapsed &&
                    ((string)toggleBtn.ToolTip).Contains("唤醒"),
                    "WeChatToggle shows wake state when WeChat enters tray");
                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 1 个可见会话", WindowVisible = true, WindowIconic = false, Chats = new List<SessionInfo> { new SessionInfo { Title = "群A", Unread = 0 } } } });

                // 免打扰：保留品牌色 + 显示月牙徽章
                NudgeWatch.ThemeManager.Apply("day");
                shell.State.TemporaryUntil = now.AddHours(1).Ticks;
                shell.Tick();
                var ballBg = shell.Ball.Part<System.Windows.Controls.Border>("Surface").Background as System.Windows.Media.SolidColorBrush;
                Assert(ballBg != null && ballBg.Color == NudgeWatch.ThemeManager.ParseColor(NudgeWatch.ThemeManager.Resolve("day").Ball),
                    "Quiet mode preserves brand ball color");
                Assert(shell.Ball.Part<System.Windows.FrameworkElement>("QuietMark").Visibility == System.Windows.Visibility.Visible,
                    "Quiet mode shows crescent badge on ball");
                shell.State.Release(now);
                shell.Tick();

                // 油画主题 L2/L3/L4 样式层级
                NudgeWatch.ThemeManager.Apply("oil-vangogh-starry");
                var starry = NudgeWatch.ThemeManager.Resolve("oil-vangogh-starry");
                NudgeWatch.ThemeManager.StyleAlert(shell.Alerts.Window, 2);
                Assert(shell.Alerts.Window.Part<System.Windows.Controls.Border>("ArtLine").Visibility == System.Windows.Visibility.Visible &&
                    shell.Alerts.Window.Part<System.Windows.Controls.TextBlock>("LevelText").Text == "普通", "Oil L2 shows art accent line and 普通 pill");
                NudgeWatch.ThemeManager.StyleAlert(shell.Alerts.Window, 3);
                var l3Bar = shell.Alerts.Window.Part<System.Windows.Controls.Border>("LevelBar").Background as System.Windows.Media.SolidColorBrush;
                Assert(shell.Alerts.Window.Part<System.Windows.Controls.Border>("ArtLine").Visibility == System.Windows.Visibility.Collapsed &&
                    l3Bar != null && l3Bar.Color == NudgeWatch.ThemeManager.ParseColor(starry.L3) &&
                    shell.Alerts.Window.Part<System.Windows.Controls.TextBlock>("LevelText").Text == "重要", "Oil L3 prioritizes severity rail over artwork");
                NudgeWatch.ThemeManager.StyleAlert(shell.Alerts.Window, 4);
                var l4Bar = shell.Alerts.Window.Part<System.Windows.Controls.Border>("LevelBar").Background as System.Windows.Media.SolidColorBrush;
                Assert(l4Bar != null && l4Bar.Color == NudgeWatch.ThemeManager.ParseColor(starry.L4Accent) &&
                    shell.Alerts.Window.Part<System.Windows.Controls.TextBlock>("LevelText").Text == "紧急", "L4 uses high-contrast L4Accent rail and 紧急 pill");

                // 结构化 AgendaRow 与范围胶囊双向同步
                shell.SaveReminder(rem);
                var agenda = shell.Panel.Part<System.Windows.Controls.ListBox>("Agenda");
                var row = agenda.Items.Cast<NudgeWatch.AgendaRow>().Single(x => x.Item.Id == rem.Id);
                Assert(row.TimeText == "15:00" && row.DayText == "今天" && row.Title == "周会评审" && row.Level == 3 && row.Tags.Count >= 2,
                    "AgendaRow populates structured fields");
                shell.Panel.Part<System.Windows.Controls.RadioButton>("ScopeChip2").IsChecked = true;
                Assert(shell.Panel.Part<System.Windows.Controls.ComboBox>("Scope").SelectedIndex == 2, "ScopeChip syncs with Scope ComboBox");

                var newSplit = shell.Panel.Part<System.Windows.Controls.Border>("NewSplit");
                var quietSplit = shell.Panel.Part<System.Windows.Controls.Border>("QuietSplit");
                var newBtn = shell.Panel.Part<System.Windows.Controls.Button>("New");
                var newMenuBtn = shell.Panel.Part<System.Windows.Controls.Button>("NewMenu");
                var quietBtn = shell.Panel.Part<System.Windows.Controls.Button>("Quiet");
                var quietMenuBtn = shell.Panel.Part<System.Windows.Controls.Button>("QuietMenu");
                var splitLeftStyle = System.Windows.Application.Current.FindResource("SplitButtonLeft");
                var splitRightStyle = System.Windows.Application.Current.FindResource("SplitButtonRight");
                Assert(newSplit != null && quietSplit != null && newBtn != null && newMenuBtn != null && quietBtn != null && quietMenuBtn != null &&
                    newBtn.Style == splitLeftStyle && quietBtn.Style == splitLeftStyle &&
                    newMenuBtn.Style == splitRightStyle && quietMenuBtn.Style == splitRightStyle &&
                    newBtn.Style != System.Windows.Application.Current.FindResource("PrimaryButton"),
                    "Header New and Quiet controls merged into unified split buttons");
                Assert(WxWatch.Native.IdleTime() >= TimeSpan.Zero, "Native.IdleTime returns non-negative duration");

                // 编辑器分段等级与隐藏 Level 同步
                var ed = shell.Forms.Editor(null, rem);
                Assert(ed.Part<System.Windows.Controls.RadioButton>("LevelSeg3").IsChecked == true, "Editor selects matching level segment");
                ed.Part<System.Windows.Controls.RadioButton>("LevelSeg4").IsChecked = true;
                Assert(ed.Part<System.Windows.Controls.ComboBox>("Level").SelectedIndex == 3, "Editor level segment updates Level selection");
                ed.Close();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void TestStartupManager()
    {
        Assert(NudgeWatch.StartupManager.ExtractExecutablePath("\"C:\\App With Space\\NudgeWatch.exe\" --hidden") == "C:\\App With Space\\NudgeWatch.exe",
            "ExtractExecutablePath handles quoted path with args");
        Assert(NudgeWatch.StartupManager.ExtractExecutablePath("C:\\Program Files\\NudgeWatch.exe --minimized") == "C:\\Program Files\\NudgeWatch.exe",
            "ExtractExecutablePath handles unquoted path with args");
        Assert(NudgeWatch.StartupManager.ExtractExecutablePath("C:\\NudgeWatch\\NudgeWatch.exe") == "C:\\NudgeWatch\\NudgeWatch.exe",
            "ExtractExecutablePath handles plain path");

        Assert(NudgeWatch.StartupManager.IsApprovedEnabled(null), "IsApprovedEnabled treats null as enabled");
        Assert(NudgeWatch.StartupManager.IsApprovedEnabled(new byte[0]), "IsApprovedEnabled treats empty array as enabled");
        Assert(NudgeWatch.StartupManager.IsApprovedEnabled(new byte[] { 2, 0, 0, 0 }), "IsApprovedEnabled treats 0x02 as enabled");
        Assert(NudgeWatch.StartupManager.IsApprovedEnabled(new byte[] { 6, 0, 0, 0 }), "IsApprovedEnabled treats 0x06 as enabled");
        Assert(!NudgeWatch.StartupManager.IsApprovedEnabled(new byte[] { 3, 0, 0, 0 }), "IsApprovedEnabled treats 0x03 as disabled");
        Assert(!NudgeWatch.StartupManager.IsApprovedEnabled(new byte[] { 1, 0, 0, 0 }), "IsApprovedEnabled treats 0x01 as disabled");

        string baseSubKey = @"Software\NudgeWatch.Test." + Guid.NewGuid().ToString("N");
        string runKey = baseSubKey + @"\Run";
        string approvedKey = baseSubKey + @"\Approved";
        string folderApprovedKey = baseSubKey + @"\FolderApproved";
        string tmpRoot = Path.Combine(Path.GetTempPath(), "nw_startup_" + Guid.NewGuid().ToString("N"));
        string startupDir = Path.Combine(tmpRoot, "Startup");
        Directory.CreateDirectory(startupDir);
        string exeA = Path.Combine(tmpRoot, "NudgeWatchA.exe");
        string exeB = Path.Combine(tmpRoot, "NudgeWatchB.exe");
        File.WriteAllText(exeA, "");
        File.WriteAllText(exeB, "");

        try
        {
            string detail;
            Assert(NudgeWatch.StartupManager.SetEnabled(true, exeA, runKey, approvedKey, folderApprovedKey, startupDir, out detail),
                "SetEnabled(true) registers startup entry");
            var st1 = NudgeWatch.StartupManager.QueryStatus(exeA, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(st1.Enabled && st1.Method == "registry", "QueryStatus confirms registry startup enabled");

            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(approvedKey))
                k.SetValue(NudgeWatch.StartupManager.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
            var stDisabled = NudgeWatch.StartupManager.QueryStatus(exeA, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(!stDisabled.Enabled && stDisabled.DisabledBySystem, "QueryStatus detects StartupApproved disabled state");

            Assert(NudgeWatch.StartupManager.SetEnabled(true, exeA, runKey, approvedKey, folderApprovedKey, startupDir, out detail),
                "SetEnabled(true) clears StartupApproved disabled state");
            var stRecovered = NudgeWatch.StartupManager.QueryStatus(exeA, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(stRecovered.Enabled && !stRecovered.DisabledBySystem, "StartupApproved recovers to enabled");

            var stOther = NudgeWatch.StartupManager.QueryStatus(exeB, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(!stOther.Enabled && stOther.PointsToOtherExe, "QueryStatus flags stale path pointing to another exe");
            Assert(NudgeWatch.StartupManager.SetEnabled(true, exeB, runKey, approvedKey, folderApprovedKey, startupDir, out detail),
                "SetEnabled(true, exeB) updates path to current exe");
            var stUpdated = NudgeWatch.StartupManager.QueryStatus(exeB, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(stUpdated.Enabled && !stUpdated.PointsToOtherExe, "Updated exe path becomes enabled");

            Assert(NudgeWatch.StartupManager.SetEnabled(false, exeB, runKey, approvedKey, folderApprovedKey, startupDir, out detail),
                "SetEnabled(false) disables startup");
            var stOff = NudgeWatch.StartupManager.QueryStatus(exeB, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(!stOff.Enabled, "QueryStatus confirms disabled after cleanup");

            string lnkPath = Path.Combine(startupDir, NudgeWatch.StartupManager.ShortcutName);
            NudgeWatch.StartupManager.CreateShortcut(lnkPath, exeB);
            Assert(NudgeWatch.StartupManager.ReadShortcutTarget(lnkPath).Equals(exeB, StringComparison.OrdinalIgnoreCase),
                "CreateShortcut and ReadShortcutTarget roundtrip");
            var stFolder = NudgeWatch.StartupManager.QueryStatus(exeB, runKey, approvedKey, folderApprovedKey, startupDir);
            Assert(stFolder.Enabled && stFolder.Method == "startup_folder", "QueryStatus recognizes startup folder shortcut");

            Assert(NudgeWatch.StartupManager.SetEnabled(false, exeB, runKey, approvedKey, folderApprovedKey, startupDir, out detail),
                "SetEnabled(false) cleans startup folder shortcut as well");
            Assert(!File.Exists(lnkPath), "Shortcut file removed on disable");
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(baseSubKey, false); } catch { }
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        }
    }

    static bool ContainsButtonText(System.Windows.DependencyObject root, string needle)
    {
        if (root == null) return false;
        var btn = root as System.Windows.Controls.Button;
        if (btn != null && ((btn.Content as string) ?? "").Contains(needle)) return true;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (ContainsButtonText(System.Windows.Media.VisualTreeHelper.GetChild(root, i), needle)) return true;
        }
        return false;
    }

    static void TestTargetedChatQuiet()
    {
        var now = new DateTime(2026, 11, 24, 14, 0, 0);
        var state = new NudgeWatch.State();
        long until60 = NudgeWatch.State.QuietUntilForMinutes(now, 60);
        long untilDay = NudgeWatch.State.QuietUntilForMinutes(now, 0);
        Assert(until60 == now.AddMinutes(60).Ticks, "QuietUntilForMinutes(60) adds 60 minutes");
        Assert(untilDay == now.Date.AddDays(1).Ticks, "QuietUntilForMinutes(0) targets midnight");

        state.SetChatQuiet("核心群", until60);
        Assert(state.ChatQuietEnd("核心群", now) == until60 && state.IsChatMuted("核心群", now) && !state.IsChatMuted("闲聊群", now),
            "ChatQuietEnd and IsChatMuted isolate target chat");
        Assert(state.ActiveQuietChats(now).SequenceEqual(new[] { "核心群" }), "ActiveQuietChats returns active muted chat");

        state.ChatQuietPending.Add("核心群");
        List<string> withMsg;
        var expired = state.ExpireChatQuiets(now.AddMinutes(61), out withMsg);
        Assert(expired.SequenceEqual(new[] { "核心群" }) && withMsg.SequenceEqual(new[] { "核心群" }) &&
            state.ChatQuietEnd("核心群", now.AddMinutes(61)) == 0 && state.ChatQuietPending.Count == 0,
            "ExpireChatQuiets cleans expired chat and reports pending messages");

        string root = Path.Combine(Path.GetTempPath(), "nw_chat_quiet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new NudgeWatch.Store(root);
            state.SetChatQuiet("临时群", now.AddMinutes(30).Ticks);
            store.Save(state);
            var reloaded = store.Load(now);
            Assert(reloaded.ChatQuietEnd("临时群", now) > 0, "Store persists ChatQuietUntil across reload");

            File.WriteAllText(Path.Combine(root, "state.json"), "{\"Reminders\":[],\"Groups\":{}}");
            var legacy = store.Load(now);
            Assert(legacy.ChatQuietUntil != null && legacy.ChatQuietPending != null, "Legacy state.json initializes ChatQuiet fields");
            File.Delete(Path.Combine(root, "state.json"));

            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            DateTime simNow = new DateTime(2026, 11, 24, 14, 0, 0);
            using (var shell = new NudgeWatch.Shell(root, () => new NudgeWatch.Frame(), () => simNow))
            {
                shell.Alerts.Sound = false;
                shell.Start();
                var apply = typeof(NudgeWatch.Shell).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 2 个可见会话", Chats = new List<SessionInfo> {
                    new SessionInfo { Title = "核心群", Unread = 0, Preview = "old" },
                    new SessionInfo { Title = "闲聊群", Unread = 0, Preview = "old" }
                } } });

                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 2 个可见会话", Chats = new List<SessionInfo> {
                    new SessionInfo { Title = "核心群", Unread = 1, Preview = "请查看" },
                    new SessionInfo { Title = "闲聊群", Unread = 0, Preview = "old" }
                } } });
                Assert(shell.Alerts.Current != null && shell.Alerts.Current.Chat == "核心群", "Incoming message triggers alert for 核心群");
                Assert(((string)shell.Alerts.Window.Part<System.Windows.Controls.Button>("QuietNow").Content).Contains("免打扰"),
                    "Alert QuietNow button displays dropdown label");

                var popQuiet = typeof(NudgeWatch.Alerts).GetMethod("PopulateQuietActions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var testChatQuietMenu = new System.Windows.Controls.ContextMenu();
                popQuiet.Invoke(shell.Alerts, new object[] { testChatQuietMenu.Items, "核心群" });
                var chatQuietHeaders = testChatQuietMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                Assert(chatQuietHeaders.Any(h => h != null && h.Contains("仅此会话免打扰 1 小时")) &&
                    chatQuietHeaders.Any(h => h != null && h.Contains("指定其他会话免打扰")) &&
                    chatQuietHeaders.Any(h => h != null && h.Contains("全部消息免打扰 1 小时")),
                    "Chat alert QuietNow menu provides current-chat, pick-other-chats, and mute-all actions");

                shell.Alerts.QuietChat("核心群", 60);
                Assert(shell.Alerts.Current == null && shell.State.ChatQuietEnd("核心群", simNow) > 0,
                    "QuietChat closes matching popup and activates per-chat quiet");

                apply.Invoke(shell, new object[] { new NudgeWatch.Frame { Status = "微信已连接 · 2 个可见会话", Chats = new List<SessionInfo> {
                    new SessionInfo { Title = "核心群", Unread = 2, Preview = "静音期间消息" },
                    new SessionInfo { Title = "闲聊群", Unread = 1, Preview = "正常弹出消息" }
                } } });
                Assert(shell.Alerts.Current != null && shell.Alerts.Current.Chat == "闲聊群" && shell.State.ChatQuietPending.Contains("核心群"),
                    "Target chat is muted while other chat still pops alert");
                ClickButton(shell.Alerts.Window, "Ack");

                var archiveField = typeof(NudgeWatch.Shell).GetField("archive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var arch = (NudgeWatch.MessageArchive)archiveField.GetValue(shell);
                arch.Flush();
                var archivedRows = arch.Read(simNow.Date, simNow.Date);
                Assert(archivedRows.Any(e => e.chat == "核心群" && e.preview == "静音期间消息" && e.muted) &&
                    archivedRows.Any(e => e.chat == "闲聊群" && e.preview == "正常弹出消息" && !e.muted),
                    "Archive marks target quiet chat as muted and other chat as unmuted");

                var chatsTree = shell.Panel.Part<System.Windows.Controls.TreeView>("Chats");
                var newBranch = chatsTree.Items.Cast<System.Windows.Controls.TreeViewItem>().First(b => NudgeWatch.Rows.NameOf(b).StartsWith("新消息"));
                var coreLeaf = newBranch.Items.Cast<System.Windows.Controls.TreeViewItem>().First(n => (string)n.Tag == "核心群");
                Assert(ContainsButtonText(coreLeaf.Header as System.Windows.DependencyObject, "☾"),
                    "Chat tree leaf renders quiet pill while chat is muted");

                simNow = simNow.AddMinutes(61);
                shell.Tick();
                Assert(shell.Alerts.Current != null && shell.Alerts.Current.Title == "免打扰已结束" && shell.Alerts.Current.Body.Contains("核心群") &&
                    shell.State.ChatQuietEnd("核心群", simNow) == 0,
                    "Expiring target chat quiet pushes L2 summary");
                ClickButton(shell.Alerts.Window, "Ack");

                var rem = NudgeWatch.Capture.Parse("0分钟后 周期提醒 @每天", simNow);
                shell.SaveReminder(rem);
                var moreBtn = shell.Alerts.Window.Part<System.Windows.Controls.Button>("More");
                Assert(shell.Alerts.Current != null && shell.Alerts.Current.ReminderId == rem.Id && moreBtn.Visibility == System.Windows.Visibility.Visible,
                    "Recurring reminder exposes More button for quiet options");
                var testRemQuietMenu = new System.Windows.Controls.ContextMenu();
                popQuiet.Invoke(shell.Alerts, new object[] { testRemQuietMenu.Items, null });
                var remQuietHeaders = testRemQuietMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                Assert(remQuietHeaders.Any(h => h != null && h.Contains("指定会话免打扰")) &&
                    remQuietHeaders.Any(h => h != null && h.Contains("全部消息免打扰 1 小时")),
                    "Reminder alert quiet menu includes targeted chat picker and mute-all options");
                ClickButton(shell.Alerts.Window, "Ack");

                shell.Panel.Hide();
                shell.Ball.Hide();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void TestBallTogglePanelToTray()
    {
        string root = Path.Combine(Path.GetTempPath(), "nw_ball_toggle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            using (var shell = new NudgeWatch.Shell(root, () => new NudgeWatch.Frame { Status = "微信已连接 · 0 个可见会话" }, () => DateTime.Now))
            {
                shell.Alerts.Sound = false;
                shell.Start();
                Assert(shell.IsPanelOpen() && shell.Panel.IsVisible, "Panel starts open");
                shell.Ball.ContextMenu.IsOpen = true;
                var openBallHeaders = shell.Ball.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                shell.Ball.ContextMenu.IsOpen = false;
                Assert(openBallHeaders.Contains("收入托盘（隐藏主面板）") && openBallHeaders.Contains("指定会话免打扰…"),
                    "Ball context menu shows collapse-to-tray and targeted quiet entry when panel is open");

                shell.Ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });
                Assert(!shell.Panel.IsVisible && !shell.IsPanelOpen(), "Clicking ball when panel is open hides panel to tray");
                shell.Ball.ContextMenu.IsOpen = true;
                var hiddenBallHeaders = shell.Ball.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                shell.Ball.ContextMenu.IsOpen = false;
                Assert(hiddenBallHeaders.Contains("打开主面板"),
                    "Ball context menu shows reopen-panel entry when panel is in tray");

                shell.Ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });
                Assert(shell.Panel.IsVisible && shell.Panel.WindowState == System.Windows.WindowState.Normal && shell.IsPanelOpen(),
                    "Clicking ball when panel is in tray reopens panel");

                shell.Panel.WindowState = System.Windows.WindowState.Minimized;
                Assert(!shell.IsPanelOpen(), "Minimized panel is not considered open");
                shell.Ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });
                Assert(shell.Panel.IsVisible && shell.Panel.WindowState == System.Windows.WindowState.Normal && shell.IsPanelOpen(),
                    "Clicking ball when panel is minimized restores panel");

                shell.Panel.Hide();
                shell.Ball.Hide();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
