using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WxWatch;

namespace NudgeWatch
{
    public sealed partial class Shell
    {
        readonly KnownChats knownChats;
        readonly HashSet<string> pinnedAtTop = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> initials = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTime inactiveSinceUtc, nextAutoTopUtc;
        IntPtr viewportWindow;
        bool viewportDrifted, movingWechat;
        int autoTopFailCount;
        const string ViewportWarning = " · 微信列表未回顶（可能漏收新消息）";

        void TrackViewport(Frame frame)
        {
            if (frame.MainHwnd != viewportWindow)
            {
                pinnedAtTop.Clear();
                viewportWindow = frame.MainHwnd;
                inactiveSinceUtc = DateTime.MinValue;
                autoTopFailCount = 0;
            }
            var now = DateTime.UtcNow;
            if (frame.Foreground)
            {
                inactiveSinceUtc = DateTime.MinValue;
                autoTopFailCount = 0;
            }
            else if (inactiveSinceUtc == DateTime.MinValue) inactiveSinceUtc = now;
            if (frame.Chats == null || frame.Chats.Count == 0 || movingWechat) return;
            viewportDrifted = IsViewportDrifted(frame, pinnedAtTop, viewportDrifted);
            if (frame.ScrollPercent >= 0 && !viewportDrifted || frame.ScrollPercent < 0 && pinnedAtTop.Count == 0 && !viewportDrifted)
                RememberTop(frame.Chats);
            if (State.AutoReturnToTop && viewportDrifted && !frame.Foreground && (frame.WindowVisible || frame.WindowIconic) &&
                !navigating && !waking && lastQuietEnd == 0 && now >= nextAutoTopUtc && now - inactiveSinceUtc >= TimeSpan.FromSeconds(8))
            {
                bool canBriefRestore = Native.IdleTime() >= TimeSpan.FromSeconds(3);
                if ((frame.WindowIconic || frame.ScrollPercent < 0) && !canBriefRestore) return;
                nextAutoTopUtc = now.AddSeconds(20);
                var h = frame.MainHwnd;
                List<SessionInfo> top = null;
                WeChatWorker(() => Navigator.TryAutoReturnTop(h, canBriefRestore, out top), delegate(bool ok, string error) {
                    if (ok)
                    {
                        if (top != null && top.Count > 0) RememberTop(top);
                        viewportDrifted = false;
                        autoTopFailCount = 0;
                    }
                    else
                    {
                        autoTopFailCount++;
                        nextAutoTopUtc = DateTime.UtcNow.AddSeconds(autoTopFailCount >= 2 ? 120 : 25);
                    }
                }, canBriefRestore);
            }
        }

        public static bool IsViewportDrifted(Frame frame, IEnumerable<string> pins, bool previous)
        {
            if (frame.ScrollPercent >= 0) return frame.ScrollPercent > 0.5;
            if (frame.ViewportDrifted) return true;
            if (frame.Chats == null || frame.Chats.Count == 0) return previous;
            if (frame.NavTotalUnread > frame.Chats.Sum(s => Math.Max(0, s.Unread))) return true;
            return pins.Any() && !frame.Chats.Any(r => pins.Contains(r.Title));
        }

        void RememberTop(IEnumerable<SessionInfo> top)
        {
            pinnedAtTop.Clear();
            foreach (var row in top.Take(3).Where(s => s.IsPinned && !String.IsNullOrEmpty(s.Title))) pinnedAtTop.Add(row.Title);
        }

        void WeChatWorker(Func<bool> work, Action<bool, string> done, bool suppressFrames)
        {
            movingWechat = true;
            skipViewportSamples = suppressFrames;
            var thread = new Thread(delegate() {
                bool ok = false;
                string error = null;
                try { ok = work(); }
                catch (Exception ex) { error = ex.GetType().Name; }
                Panel.Dispatcher.BeginInvoke(new Action(delegate {
                    movingWechat = skipViewportSamples = false;
                    if (exiting) return;
                    done(ok, error);
                    Tick();
                }));
            }) { IsBackground = true, Name = "WeChat navigation" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        bool skipViewportSamples;
        void ReturnWechatTop()
        {
            if (navigating || movingWechat || waking) return;
            var h = lastWeChatHwnd;
            List<SessionInfo> top = null;
            WeChatWorker(() => Navigator.WithSessionList(h, uia => (top = uia.ManualTop()) != null), delegate(bool ok, string error) {
                if (ok) { RememberTop(top); viewportDrifted = false; }
                else ChatTip("回顶未完成", "请打开微信主窗口后重试，或直接将会话列表滚到顶部。");
            }, true);
        }

        internal static HashSet<string> BuildSyncedChatSet(State state, KnownChats knownChats)
        {
            var synced = new HashSet<string>(StringComparer.Ordinal);
            if (state != null && state.Groups != null)
            {
                foreach (var group in state.Groups)
                {
                    if (group == null || group.Chats == null) continue;
                    foreach (var title in group.Chats)
                        if (!String.IsNullOrWhiteSpace(title)) synced.Add(title.Trim());
                }
            }
            if (knownChats != null)
            {
                foreach (var entry in knownChats.All)
                    if (entry != null && !String.IsNullOrWhiteSpace(entry.Title)) synced.Add(entry.Title.Trim());
            }
            return synced;
        }

        internal static List<string> FilterUnsyncedChats(IEnumerable<string> scannedTitles, ISet<string> syncedChats, out int skippedCount)
        {
            skippedCount = 0;
            var result = new List<string>();
            if (scannedTitles == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in scannedTitles)
            {
                if (String.IsNullOrWhiteSpace(raw)) continue;
                string normalized = raw.Trim();
                if (!seen.Add(normalized)) continue;
                if (syncedChats != null && syncedChats.Contains(normalized))
                {
                    skippedCount++;
                    continue;
                }
                result.Add(normalized);
            }
            return result;
        }

        internal int ApplyScannedChats(IEnumerable<string> scannedTitles, out List<string> newTitles, out int skippedCount)
        {
            var synced = BuildSyncedChatSet(State, knownChats);
            newTitles = FilterUnsyncedChats(scannedTitles, synced, out skippedCount);
            if (newTitles.Count > 0)
                knownChats.Observe(newTitles, clock(), Group);
            return newTitles.Count;
        }

        void ScanChats()
        {
            if (navigating || movingWechat || waking) return;
            if (!ThemedDialog.Confirm(Panel, "同步更多群聊", "同步将短暂切换到微信并滚动会话列表，已同步的对象会自动保护跳过。请在接下来约 5–10 秒内暂停鼠标和键盘操作。", "开始同步", false)) return;
            var h = lastWeChatHwnd;
            var titles = new HashSet<string>(StringComparer.Ordinal);
            List<SessionInfo> top = null;
            WeChatWorker(() => Navigator.WithSessionList(h, uia => {
                try
                {
                    int noNew = 0;
                    var parser = new SessionParser();
                    uia.ManualTop();
                    for (int page = 0; page < 13 && noNew < 2 && Native.GetForegroundWindow() == uia.MainHwnd; page++)
                    {
                        int count = titles.Count;
                        var current = uia.ReadSessions(parser, new WatchStatus());
                        if (current == null || current.Count == 0) break;
                        foreach (var row in current.Where(r => !String.IsNullOrWhiteSpace(r.Title))) titles.Add(row.Title);
                        noNew = count == titles.Count ? noNew + 1 : 0;
                        if (page == 12 || noNew >= 2 || !uia.NextPage()) break;
                        Thread.Sleep(280);
                    }
                }
                finally { if (Native.GetForegroundWindow() == uia.MainHwnd) top = uia.ManualTop(); }
                return top != null;
            }), delegate(bool ok, string error) {
                List<string> newTitles;
                int skippedCount;
                ApplyScannedChats(titles, out newTitles, out skippedCount);
                if (ok) { RememberTop(top); viewportDrifted = false; }
                else ChatTip("同步后未能确认回顶", "可通过「列表回顶」重试。");
                TreeChanged();
                if (newTitles.Count > 0)
                {
                    SetChatActionNote("同步完成：新增 " + newTitles.Count + " 个会话" + (skippedCount > 0 ? "，已跳过 " + skippedCount + " 个已同步对象" : ""));
                    PickScannedChats(newTitles, skippedCount);
                }
                else if (skippedCount > 0)
                {
                    SetChatActionNote("未发现新会话（已跳过 " + skippedCount + " 个已同步对象）");
                    ChatTip("同步群聊完成", "扫描到的 " + skippedCount + " 个会话均为已同步对象，已自动保护跳过。");
                }
                else
                {
                    SetChatActionNote("未扫描到微信会话，请打开微信主窗口后重试");
                }
            }, true);
        }

        void PickScannedChats(IEnumerable<string> titles, int skippedCount = 0)
        {
            var items = titles.OrderBy(t => t, StringComparer.CurrentCulture).ToList();
            var list = new ListBox { SelectionMode = SelectionMode.Extended, MaxHeight = 280, MinHeight = 120,
                ItemsSource = items };
            var group = new ComboBox { IsEditable = true, ItemsSource = State.Groups.Select(g => g.Name).ToList(), Margin = new Thickness(0, 8, 0, 0),
                Text = SelectedGroup() == null || IsSystemChatGroup(SelectedGroup().Name) ? (State.Groups.Count > 0 ? State.Groups[0].Name : "常用") : SelectedGroup().Name };
            var selectAll = new Button { Content = "全选", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            selectAll.Click += delegate { list.SelectAll(); };
            var box = new ContentControl();
            var layout = new StackPanel();
            string hint = skippedCount > 0
                ? "本次新同步 " + items.Count + " 个会话（已保护跳过 " + skippedCount + " 个已同步对象），选择要加入分组的会话（Ctrl / Shift 多选）"
                : "本次新同步 " + items.Count + " 个会话，选择要加入分组的会话（Ctrl / Shift 多选）";
            layout.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap });
            layout.Children.Add(list); layout.Children.Add(selectAll); layout.Children.Add(group);
            box.Content = layout;
            if (PromptWindow("将扫描到的会话加入分组", box).ShowDialog() == true)
            {
                string dest = group.Text.Trim();
                if (!String.IsNullOrWhiteSpace(dest) && !IsSystemChatGroup(dest))
                    AssignChats(list.SelectedItems.Cast<string>().Where(t => CustomGroup(t).Length == 0), dest);
            }
        }

        void AddManyChats()
        {
            var group = SelectedGroup();
            string defaultName = group == null || IsSystemChatGroup(group.Name) ? (State.Groups.Count > 0 ? State.Groups[0].Name : "常用") : group.Name;
            var groupBox = new ComboBox { IsEditable = true, ItemsSource = State.Groups.Select(g => g.Name).ToList(), Text = defaultName };
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, Margin = new Thickness(0, 8, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "每行一个群名" };
            var host = new ContentControl();
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "目标分组（可输入新分组名）" });
            stack.Children.Add(groupBox);
            stack.Children.Add(new TextBlock { Text = "会话名称（每行一个）", Margin = new Thickness(0, 8, 0, 0) });
            stack.Children.Add(input);
            host.Content = stack;
            if (PromptWindow("批量添加群名", host).ShowDialog() == true)
            {
                string dest = groupBox.Text.Trim();
                if (!String.IsNullOrWhiteSpace(dest) && !IsSystemChatGroup(dest))
                    AssignChats(input.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), dest);
            }
        }

        void AssignChats(IEnumerable<string> entries, string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (IsSystemChatGroup(name)) return;
            var titles = entries.Where(e => !String.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToList();
            if (titles.Count == 0) return;
            var destination = FindGroup(name);
            foreach (var title in titles)
            {
                string trimmed = title.Trim();
                foreach (var group in State.Groups)
                    group.Chats.RemoveAll(t => t == title || (t != null && t.Trim() == trimmed));
                if (!destination.Chats.Contains(title)) destination.Chats.Add(title);
            }
            State.CollapsedChatGroups.Remove(destination.Name);
            Persist();
            foreach (var title in titles) knownChats.Annotate(title, null, Group(title), clock(), false);
            knownChats.Flush(clock());
            selectedChats.Clear();
            UpdateChatMultiButton();
            SetChatActionNote(titles.Count == 1 ? "已将「" + titles[0] + "」加入「" + destination.Name + "」" : "已将 " + titles.Count + " 个会话加入「" + destination.Name + "」");
            TreeChanged();
        }

        void RemoveChatsFromGroups(IEnumerable<string> entries)
        {
            var titles = entries.Where(e => !String.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToList();
            if (titles.Count == 0) return;
            bool removed = false;
            foreach (var title in titles)
            {
                string trimmed = title.Trim();
                foreach (var group in State.Groups)
                    if (group.Chats.RemoveAll(t => t == title || (t != null && t.Trim() == trimmed)) > 0) removed = true;
            }
            if (!removed) return;
            Persist();
            foreach (var title in titles) knownChats.Annotate(title, null, Group(title), clock(), false);
            knownChats.Flush(clock());
            selectedChats.Clear();
            UpdateChatMultiButton();
            SetChatActionNote(titles.Count == 1 ? "已将「" + titles[0] + "」移出分组" : "已将 " + titles.Count + " 个会话移出分组");
            TreeChanged();
        }

        void ChatTip(string title, string body)
        {
            NavigationStatus = body;
            tray.ShowBalloonTip(4500, title, body, System.Windows.Forms.ToolTipIcon.Info);
        }

        bool MatchChat(string title, string filter)
        {
            if (Match(title, filter)) return true;
            var entry = knownChats.Find(title);
            return SearchInitials(title, filter) || entry != null &&
                (Match(entry.Alias ?? "", filter) || SearchInitials(entry.Alias ?? "", filter));
        }

        bool SearchInitials(string name, string filter)
        {
            if (filter.Any(c => c > 127)) return false;
            string value;
            if (!initials.TryGetValue(name, out value)) initials[name] = value = KnownChats.PinyinInitials(name);
            return Match(value, filter);
        }
    }
}
