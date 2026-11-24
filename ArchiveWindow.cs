using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NudgeWatch
{
    internal sealed class ArchiveWindow
    {
        readonly Window window = Views.Load("ArchiveWindow");
        internal Window View { get { return window; } }
        readonly MessageArchive archive;
        readonly DateTime today;
        readonly Dictionary<string, string> groups;
        readonly HashSet<string> selected;
        readonly TextBox keyword, chatFilter, preview;
        readonly ComboBox range, importance, format, layout;
        readonly DatePicker from, through;
        readonly TextBlock matched;
        readonly TreeView tree;
        List<ArchiveEntry> entries = new List<ArchiveEntry>(), result = new List<ArchiveEntry>();
        Dictionary<string, string> available;
        bool settingDates, closed, updatingChecks, loading, showAll;
        int request;

        public ArchiveWindow(Window owner, MessageArchive archive, DateTime now, Dictionary<string, string> groups, IEnumerable<string> preselect)
        {
            window.Owner = owner;
            this.archive = archive; this.groups = groups; today = now.Date;
            selected = new HashSet<string>(preselect ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            showAll = preselect == null;
            keyword = window.Part<TextBox>("Keyword"); chatFilter = window.Part<TextBox>("ChatFilter"); preview = window.Part<TextBox>("Preview");
            range = window.Part<ComboBox>("Range"); importance = window.Part<ComboBox>("Importance");
            format = window.Part<ComboBox>("Format"); layout = window.Part<ComboBox>("MarkdownLayout");
            from = window.Part<DatePicker>("From"); through = window.Part<DatePicker>("Through");
            matched = window.Part<TextBlock>("Matched"); tree = window.Part<TreeView>("ArchiveChats");
            Options(range, new[] { "今天", "近 3 天", "近 7 天", "近 30 天", "自定义日期" }, 2);
            Options(importance, new[] { "全部消息", "仅强提醒", "仅 @我", "强提醒或 @我" }, 0);
            Options(format, new[] { "Markdown", "CSV", "TXT" }, 0);
            Options(layout, new[] { "按群聊分组", "全局时间线" }, 0);

            window.Loaded += delegate { SetRange(); };
            window.Closed += delegate { closed = true; request++; };
            range.SelectionChanged += delegate { SetRange(); };
            from.SelectedDateChanged += delegate { if (!settingDates) Reload(); };
            through.SelectedDateChanged += delegate { if (!settingDates) Reload(); };
            importance.SelectionChanged += delegate { UpdatePreview(); };
            format.SelectionChanged += delegate { layout.IsEnabled = format.SelectedIndex == 0; UpdatePreview(); };
            layout.SelectionChanged += delegate { UpdatePreview(); };
            keyword.TextChanged += delegate { UpdatePreview(); };
            chatFilter.TextChanged += delegate { if (available != null) UpdateChats(); };
            window.Click("SelectAll", delegate { SetSelection(true, false); });
            window.Click("Invert", delegate { SetSelection(false, true); });
            window.Click("SelectNone", delegate { SetSelection(false, false); });
            window.Click("Copy", Copy);
            window.Click("Export", Save);
            SetExportEnabled(false);
        }

        public void Show() { window.ShowDialog(); }

        static void Options(ComboBox combo, string[] options, int current)
        {
            combo.ItemsSource = options;
            combo.SelectedIndex = current;
        }

        void SetRange()
        {
            bool custom = range.SelectedIndex == 4;
            if (range.SelectedIndex < 0) return;
            from.IsEnabled = through.IsEnabled = custom;
            if (!custom)
            {
                settingDates = true;
                from.SelectedDate = today.AddDays(1 - new[] { 1, 3, 7, 30 }[range.SelectedIndex]);
                through.SelectedDate = today;
                settingDates = false;
            }
            Reload();
        }

        async void Reload()
        {
            int revision = ++request;
            entries.Clear();
            result.Clear();
            SetExportEnabled(false);
            preview.Clear();
            var start = from.SelectedDate;
            var end = through.SelectedDate;
            if (!start.HasValue || !end.HasValue || start > end)
            {
                loading = true;
                matched.Text = "请选择有效的日期范围";
                return;
            }
            loading = true;
            matched.Text = "正在读取归档…";
            try
            {
                var loaded = await Task.Run(() => { archive.Flush(); return archive.Read(start.Value, end.Value); });
                if (closed || request != revision) return;
                entries = loaded;
                available = new Dictionary<string, string>(groups, StringComparer.Ordinal);
                foreach (var entry in entries.AsEnumerable().Reverse())
                    if (!available.ContainsKey(entry.chat)) available.Add(entry.chat, String.IsNullOrEmpty(entry.group) || entry.group == "未分组" ? "新消息" : entry.group);
                if (showAll) selected.UnionWith(available.Keys);
                loading = false;
                UpdateChats();
                UpdatePreview();
            }
            catch (Exception ex)
            {
                if (!closed && request == revision) matched.Text = "无法读取归档（" + ex.GetType().Name + "）";
            }
        }

        void SetSelection(bool all, bool invert)
        {
            if (available == null) return;
            var visible = all || invert ? available.Keys.Where(t => Has(t, chatFilter.Text.Trim())).ToArray() : selected.ToArray();
            showAll = all && visible.Length == available.Count;
            foreach (var title in visible)
            {
                if (all || invert && !selected.Contains(title)) selected.Add(title);
                else selected.Remove(title);
            }
            UpdateChats();
            UpdatePreview();
        }

        void UpdateChats()
        {
            var filter = chatFilter.Text.Trim();
            tree.Items.Clear();
            foreach (var group in available.GroupBy(p => p.Value).OrderBy(g => g.Key))
            {
                var members = group.Select(p => p.Key).OrderBy(t => t).ToArray();
                var visible = members.Where(t => Has(t, filter)).ToArray();
                if (visible.Length == 0) continue;
                var groupBox = new CheckBox { Content = group.Key, IsChecked = CheckedGroup(members), FontWeight = FontWeights.SemiBold };
                var branch = new TreeViewItem { Header = groupBox, IsExpanded = true };
                tree.Items.Add(branch);
                var buttons = new List<CheckBox>();
                foreach (var name in visible)
                {
                    var choice = new CheckBox { Content = name, IsChecked = selected.Contains(name) };
                    buttons.Add(choice);
                    branch.Items.Add(new TreeViewItem { Header = choice });
                    choice.Click += delegate {
                        if (updatingChecks) return;
                        showAll = false;
                        if (choice.IsChecked == true) selected.Add(name); else selected.Remove(name);
                        groupBox.IsChecked = CheckedGroup(members);
                        UpdatePreview();
                    };
                }
                groupBox.Click += delegate {
                    showAll = false;
                    bool check = groupBox.IsChecked == true;
                    updatingChecks = true;
                    foreach (var member in members)
                    {
                        if (check) selected.Add(member); else selected.Remove(member);
                    }
                    foreach (var button in buttons) button.IsChecked = check;
                    updatingChecks = false;
                    UpdatePreview();
                };
            }
        }

        bool? CheckedGroup(string[] members)
        {
            int count = members.Count(selected.Contains);
            return count == members.Length ? (bool?)true : count == 0 ? (bool?)false : null;
        }

        static bool Has(string value, string term) { return (value ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0; }
        internal static bool Matches(ArchiveEntry entry, string keyword, int importance)
        {
            if (!Has(entry.preview, keyword) && !Has(entry.chat, keyword) && !Has(entry.group, keyword)) return false;
            bool strong = entry.level >= 3;
            bool mention = Has(entry.preview, "@我") || Has(entry.preview, "@ 我") || Has(entry.preview, "＠我");
            return importance == 0 || (importance == 1 && strong) || (importance == 2 && mention) || (importance == 3 && (strong || mention));
        }

        void UpdatePreview()
        {
            if (loading) return;
            string text = keyword.Text.Trim();
            result = entries.Where(e => selected.Contains(e.chat) && Matches(e, text, importance.SelectedIndex)).ToList();
            matched.Text = result.Count + " 条" + (result.Count > 100 ? " · 预览最新 100 条" : "");
            preview.Text = MessageArchive.Export(result.Skip(Math.Max(0, result.Count - 100)), format.SelectedIndex, layout.SelectedIndex == 0);
            SetExportEnabled(result.Count > 0);
        }

        void SetExportEnabled(bool value)
        {
            window.Part<Button>("Copy").IsEnabled = window.Part<Button>("Export").IsEnabled = value;
        }

        void Copy()
        {
            try
            {
                Clipboard.SetText(MessageArchive.Export(result, format.SelectedIndex, layout.SelectedIndex == 0));
                matched.Text = "已复制 " + result.Count + " 条";
            }
            catch (ExternalException) { matched.Text = "剪贴板正忙，请重试"; }
        }

        void Save()
        {
            int f = format.SelectedIndex;
            string extension = new[] { "md", "csv", "txt" }[f];
            var dialog = new SaveFileDialog { DefaultExt = "." + extension, AddExtension = true, OverwritePrompt = true,
                Filter = new[] { "Markdown 文档|*.md", "CSV 文件|*.csv", "纯文本|*.txt" }[f], FileName = "微信消息-" + today.ToString("yyyy-MM-dd") };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                WriteExport(dialog.FileName, result, f, layout.SelectedIndex == 0);
                matched.Text = "已导出 " + result.Count + " 条";
            }
            catch (IOException) { matched.Text = "无法导出文件"; }
            catch (UnauthorizedAccessException) { matched.Text = "无法写入所选位置"; }
        }

        internal static void WriteExport(string path, IEnumerable<ArchiveEntry> entries, int format, bool grouped)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                File.WriteAllText(temp, MessageArchive.Export(entries, format, grouped), new UTF8Encoding(format == 1));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
