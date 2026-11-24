using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NudgeWatch
{
    public sealed partial class Shell
    {
        sealed class UndoStep
        {
            public Reminder[] Before;
            public string AddedId;
        }

        readonly List<UndoStep> undo = new List<UndoStep>();
        readonly DispatcherTimer undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly HashSet<string> selectedTasks = new HashSet<string>(StringComparer.Ordinal);
        bool taskMultiMode, buildingTaskTree, refreshingAgenda;
        string lastClickedTask;
        DateTime lastTrashDay;

        IEnumerable<TaskGroupDef> TaskGroups()
        {
            return new[] { new TaskGroupDef() }.Concat(State.TaskGroups);
        }

        void SetupWorkItems()
        {
            Forms.Groups = TaskGroups;
            Forms.SwitchKind = SwitchKind;
            Forms.CompleteItem = CompleteWork;
            Forms.Exists = id => State.Reminders.Any(r => r.Id == id);
            Alerts.ScheduleAction = ScheduleAction;
            Alerts.Convert = id => SwitchKind(State.Reminders.Find(r => r.Id == id));
            Alerts.CustomFollowUp = delegate {
                string value = Prompt("下次追催（yyyy-MM-dd HH:mm）", new DateTime(Reminder.Tomorrow(clock())).ToString("yyyy-MM-dd HH:mm"));
                if (value == null) return 0;
                try { return Forms.ParseFollowUp(value); }
                catch (FormatException ex) { ThemedDialog.Info(Panel, "下次追催", ex.Message); return 0; }
            };

            Panel.Part<ComboBox>("Scope").SelectionChanged += delegate { RefreshAgenda(); };
            Panel.Part<ComboBox>("TaskScope").SelectionChanged += delegate { selectedTasks.Clear(); RefreshAgenda(); };
            SetupScopeChips("Scope", "ScopeChip");
            SetupScopeChips("TaskScope", "TaskScopeChip");
            Panel.Part<ListBox>("Agenda").SelectionChanged += delegate { if (!refreshingAgenda) UpdateWorkBars(); };
            Panel.Part<ListBox>("Agenda").MouseDoubleClick += delegate { var r = Selected(false); if (r != null && r.Active) EditWork(r); };
            Panel.Part<TreeView>("Tasks").SelectedItemChanged += delegate { if (!buildingTaskTree) UpdateWorkBars(); };
            Panel.Click("Add", delegate { Forms.Editor(Panel, null).ShowDialog(); });
            Panel.Click("AddTask", delegate {
                var branch = TaskBranch();
                Forms.Editor(Panel, new Reminder { Kind = "task", GroupId = branch == null ? "" : branch.Id }).ShowDialog();
            });
            Panel.Click("Edit", delegate { EditWork(Selected(false)); });
            Panel.Click("EditTask", delegate { EditWork(Selected(true)); });
            Panel.Click("Done", delegate {
                var items = SelectedReminders();
                if (items.Count == 1) { if (items[0].Active) CompleteWork(items[0]); else RestoreWork(items[0]); }
                else if (items.Count > 1) CompleteOrRestoreMany(items);
            });
            Panel.Click("Delete", delegate {
                var items = SelectedReminders();
                if (items.Count == 1) { if (items[0].Active) DeleteWork(items[0]); else PurgeWork(items[0]); }
                else if (items.Count > 1) DeleteOrPurgeMany(items);
            });
            Panel.Click("DoneTask", delegate {
                var items = TargetTasks(Selected(true));
                if (items.Count == 1) { if (items[0].Active) CompleteWork(items[0]); else RestoreWork(items[0]); }
                else if (items.Count > 1) CompleteOrRestoreMany(items);
            });
            Panel.Click("DeleteTask", delegate {
                var items = TargetTasks(Selected(true));
                if (items.Count == 1) { if (items[0].Active) DeleteWork(items[0]); else PurgeWork(items[0]); }
                else if (items.Count > 1) DeleteOrPurgeMany(items);
            });
            Panel.Click("MoveTaskGroup", delegate {
                var targets = TargetTasks(Selected(true));
                if (targets.Count > 0) OpenTaskMoveMenu(Panel.Part<Button>("MoveTaskGroup"), targets);
            });
            Panel.Click("TaskStateBtn", delegate {
                var targets = TargetTasks(Selected(true));
                if (targets.Count > 0) OpenTaskStateMenu(Panel.Part<Button>("TaskStateBtn"), targets);
            });
            Panel.Click("ToggleTaskExpand", ToggleAllTaskGroups);
            Panel.Click("TaskMulti", delegate {
                taskMultiMode = !taskMultiMode;
                if (!taskMultiMode) selectedTasks.Clear();
                RefreshAgenda();
            });
            Panel.Click("TaskBatchMove", delegate {
                var targets = TargetTasks(null);
                if (targets.Count > 0) OpenTaskMoveMenu(Panel.Part<Button>("TaskBatchMove"), targets);
            });
            Panel.Click("TaskBatchState", delegate {
                var targets = TargetTasks(null);
                if (targets.Count > 0) OpenTaskStateMenu(Panel.Part<Button>("TaskBatchState"), targets);
            });
            Panel.Click("TaskBatchDone", delegate {
                var targets = TargetTasks(null);
                if (targets.Count > 0) CompleteOrRestoreMany(targets);
            });
            Panel.Click("TaskBatchDelete", delegate {
                var targets = TargetTasks(null);
                if (targets.Count > 0) DeleteOrPurgeMany(targets);
            });
            Panel.Click("TaskBatchClear", delegate {
                taskMultiMode = false;
                selectedTasks.Clear();
                RefreshAgenda();
            });
            var agenda = Panel.Part<ListBox>("Agenda");
            agenda.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(delegate(object s, RoutedEventArgs e) {
                var btn = e.OriginalSource as Button;
                if (btn == null || btn.Name != "RowDoneBtn") return;
                var rem = btn.Tag as Reminder;
                if (rem != null && rem.Active) { CompleteWork(rem); e.Handled = true; }
            }));
            agenda.ContextMenu = new ContextMenu();
            agenda.ContextMenuOpening += delegate {
                agenda.ContextMenu.Items.Clear();
                FillWorkMenu(agenda.ContextMenu, Selected(false));
            };
            agenda.PreviewMouseRightButtonDown += SelectOnRightClick;
            Panel.Part<TreeView>("Tasks").PreviewMouseRightButtonDown += SelectOnRightClick;

            var menu = new ContextMenu();
            MenuItem(menu, "新建分组", delegate { EditTaskGroup(null); });
            MenuItem(menu, "重命名分组", delegate { var g = TaskBranch(); if (g != null && g.Id != "") EditTaskGroup(g); });
            MenuItem(menu, "删除分组", delegate {
                var g = TaskBranch();
                if (g == null || g.Id == "") return;
                State.TaskGroups.Remove(g);
                foreach (var r in State.Reminders.Where(r => r.GroupId == g.Id)) r.GroupId = "";
                Changed();
            });
            Panel.Click("TaskGroups", delegate { menu.PlacementTarget = Panel.Part<Button>("TaskGroups"); menu.IsOpen = true; });

            Panel.Click("Undo", UndoWork);
            undoTimer.Tick += delegate { HideUndo(); };
            Panel.PreviewKeyDown += (s, e) => {
                if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control || undo.Count == 0) return;
                UndoWork(); e.Handled = true;
            };
        }

        static bool IsInsideButton(DependencyObject source, DependencyObject stopAt)
        {
            var cur = source;
            while (cur != null && cur != stopAt)
            {
                if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (cur is Visual || cur is System.Windows.Media.Media3D.Visual3D) cur = VisualTreeHelper.GetParent(cur);
                else cur = LogicalTreeHelper.GetParent(cur);
            }
            return false;
        }

        static void SelectOnRightClick(object sender, MouseButtonEventArgs e)
        {
            var control = (ItemsControl)sender;
            for (var visual = e.OriginalSource as Visual; visual != null && visual != control; visual = VisualTreeHelper.GetParent(visual) as Visual)
            {
                var box = visual as ListBoxItem;
                if (box != null) { box.IsSelected = true; return; }
                var node = visual as TreeViewItem;
                if (node != null) { node.IsSelected = true; return; }
            }
        }

        Reminder Selected(bool task)
        {
            if (!task)
            {
                var row = Panel.Part<ListBox>("Agenda").SelectedItem as AgendaRow;
                return row == null ? null : row.Item;
            }
            var node = Panel.Part<TreeView>("Tasks").SelectedItem as TreeViewItem;
            return node == null ? null : node.Tag as Reminder;
        }

        TaskGroupDef TaskBranch()
        {
            var node = Panel.Part<TreeView>("Tasks").SelectedItem as TreeViewItem;
            if (node != null && node.Tag is Reminder) node = ItemsControl.ItemsControlFromItemContainer(node) as TreeViewItem;
            return node == null ? null : node.Tag as TaskGroupDef;
        }

        void EditTaskGroup(TaskGroupDef group)
        {
            string name = Prompt("任务分组名称", group == null ? "" : group.Name);
            if (String.IsNullOrWhiteSpace(name) || TaskGroups().Any(g => g.Name == name)) return;
            if (group == null) State.TaskGroups.Add(new TaskGroupDef { Id = Guid.NewGuid().ToString("N"), Name = name });
            else group.Name = name;
            Changed();
        }

        void EditWork(Reminder r)
        {
            if (r != null && r.Active) Forms.Editor(Panel, r).ShowDialog();
        }

        List<Reminder> SelectedReminders()
        {
            return Panel.Part<ListBox>("Agenda").SelectedItems.OfType<AgendaRow>().Select(row => row.Item).ToList();
        }

        List<Reminder> TargetTasks(Reminder fallback)
        {
            if (selectedTasks.Count > 0 && (fallback == null || selectedTasks.Contains(fallback.Id)))
                return State.Reminders.Where(r => r.IsTask && selectedTasks.Contains(r.Id)).ToList();
            return fallback == null ? new List<Reminder>() : new List<Reminder> { fallback };
        }

        void UpdateTaskBatchBar()
        {
            var bar = Panel.Part<Border>("TaskBatchBar");
            int taskScope = Panel.Part<ComboBox>("TaskScope").SelectedIndex;
            bool show = taskMultiMode || selectedTasks.Count > 1;
            bar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<TextBlock>("TaskBatchCount").Text = "已选 " + selectedTasks.Count + " 项";
            Panel.Part<Button>("TaskMulti").Content = taskMultiMode ? "完成多选" : "多选";
            Panel.Part<Button>("TaskBatchMove").Visibility = taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("TaskBatchState").Visibility = taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("TaskBatchDone").Content = taskScope < 2 ? "批量完成" : "批量恢复";
            Panel.Part<Button>("TaskBatchDelete").Content = taskScope < 2 ? "删除" : "彻底删除";
        }

        void UpdateTaskExpandButton()
        {
            var tree = Panel.Part<TreeView>("Tasks");
            bool anyExpanded = tree.Items.OfType<TreeViewItem>().Any(b => b.IsExpanded);
            Panel.Part<Button>("ToggleTaskExpand").Content = anyExpanded ? "全折" : "全展";
        }

        void ToggleAllTaskGroups()
        {
            var tree = Panel.Part<TreeView>("Tasks");
            var branches = tree.Items.OfType<TreeViewItem>().ToList();
            bool collapse = branches.Any(b => b.IsExpanded);
            State.CollapsedTaskGroups.Clear();
            if (collapse)
            {
                foreach (var b in branches)
                {
                    var g = b.Tag as TaskGroupDef;
                    if (g != null && !State.CollapsedTaskGroups.Contains(g.Id)) State.CollapsedTaskGroups.Add(g.Id);
                }
            }
            Persist();
            RefreshAgenda();
        }

        void MoveTasksToGroup(List<Reminder> targets, string groupId, string groupName)
        {
            var list = targets.Where(r => r.IsTask && r.GroupId != groupId).ToList();
            if (list.Count == 0) return;
            Remember(list.Count == 1 ? "已移至「" + groupName + "」" : "已将 " + list.Count + " 项任务移至「" + groupName + "」", null, list.ToArray());
            foreach (var r in list) r.GroupId = groupId;
            State.CollapsedTaskGroups.Remove(groupId);
            selectedTasks.Clear();
            Changed();
        }

        ContextMenu BuildTaskMoveMenu(List<Reminder> targets)
        {
            var menu = new ContextMenu();
            foreach (var g in TaskGroups())
            {
                string gid = g.Id, gname = g.Name;
                var item = new MenuItem { Header = gname, IsChecked = targets.Count > 0 && targets.All(t => t.GroupId == gid) };
                item.Click += delegate { MoveTasksToGroup(targets, gid, gname); };
                menu.Items.Add(item);
            }
            var create = new MenuItem { Header = "新建分组…" };
            create.Click += delegate {
                string name = Prompt("任务分组名称", "");
                if (String.IsNullOrWhiteSpace(name) || TaskGroups().Any(g => g.Name == name)) return;
                var added = new TaskGroupDef { Id = Guid.NewGuid().ToString("N"), Name = name };
                State.TaskGroups.Add(added);
                MoveTasksToGroup(targets, added.Id, added.Name);
            };
            menu.Items.Add(create);
            return menu;
        }

        void OpenTaskMoveMenu(Button anchor, List<Reminder> targets)
        {
            var menu = BuildTaskMoveMenu(targets);
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
        }

        void SetTasksInProgress(List<Reminder> targets)
        {
            var list = targets.Where(r => r.IsTask && r.Active).ToList();
            if (list.Count == 0) return;
            long nowTicks = clock().Ticks;
            Remember(list.Count == 1 ? "已设为进行中「" + list[0].Title + "」" : "已将 " + list.Count + " 项设为进行中", null, list.ToArray());
            foreach (var r in list)
            {
                r.TaskState = "in_progress";
                r.Delivered = 0;
                r.NextFollowUp = 0;
                if (r.Start > 0 && r.Start <= nowTicks) r.Start = 0;
                Alerts.ClearChats(n => n.ReminderId == r.Id);
            }
            selectedTasks.Clear();
            Changed();
        }

        void DeferTasks(List<Reminder> targets, long followUpTicks, string desc)
        {
            var list = targets.Where(r => r.IsTask && r.Active).ToList();
            if (list.Count == 0 || followUpTicks <= 0) return;
            Remember(list.Count == 1 ? "已延后「" + list[0].Title + "」至" + desc : "已将 " + list.Count + " 项延后至" + desc, null, list.ToArray());
            foreach (var r in list)
            {
                r.TaskState = "deferred";
                r.NextFollowUp = followUpTicks;
                Alerts.ClearChats(n => n.ReminderId == r.Id);
            }
            selectedTasks.Clear();
            Changed();
        }

        void SetTasksPaused(List<Reminder> targets)
        {
            var list = targets.Where(r => r.IsTask && r.Active).ToList();
            if (list.Count == 0) return;
            Remember(list.Count == 1 ? "已暂缓「" + list[0].Title + "」" : "已暂缓 " + list.Count + " 项任务", null, list.ToArray());
            foreach (var r in list)
            {
                r.TaskState = "paused";
                Alerts.ClearChats(n => n.ReminderId == r.Id);
            }
            selectedTasks.Clear();
            Changed();
        }

        void PopulateTaskStateItems(ItemCollection items, List<Reminder> targets)
        {
            var first = targets.Count == 1 ? targets[0] : null;
            DateTime now = clock();
            var done = new MenuItem { Header = targets.Count > 1 ? "✓ 确认已完成（" + targets.Count + " 项）" : "✓ 确认已完成" };
            done.Click += delegate { if (targets.Count == 1) CompleteWork(targets[0]); else CompleteOrRestoreMany(targets); };
            items.Add(done);

            var inProg = new MenuItem { Header = "▶ 进行中（停止追催）", IsChecked = first != null && first.TaskState == "in_progress" && !first.Awaiting(now) };
            inProg.Click += delegate { SetTasksInProgress(targets); };
            items.Add(inProg);

            var defer = new MenuItem { Header = "⏱ 延后追催", IsChecked = first != null && first.IsDeferred(now) };
            var d30 = new MenuItem { Header = "30 分钟后" };
            d30.Click += delegate { DeferTasks(targets, clock().AddMinutes(30).Ticks, " 30 分钟后"); };
            defer.Items.Add(d30);
            var d60 = new MenuItem { Header = "1 小时后" };
            d60.Click += delegate { DeferTasks(targets, clock().AddMinutes(60).Ticks, " 1 小时后"); };
            defer.Items.Add(d60);
            var dTom = new MenuItem { Header = "明天 09:30" };
            dTom.Click += delegate { DeferTasks(targets, Reminder.Tomorrow(clock()), "明天 09:30"); };
            defer.Items.Add(dTom);
            var dCustom = new MenuItem { Header = "自定义时间…" };
            dCustom.Click += delegate {
                string value = Prompt("下次追催（yyyy-MM-dd HH:mm）", new DateTime(Reminder.Tomorrow(clock())).ToString("yyyy-MM-dd HH:mm"));
                if (value == null) return;
                try
                {
                    long ticks = Forms.ParseFollowUp(value);
                    DeferTasks(targets, ticks, new DateTime(ticks).ToString("MM-dd HH:mm"));
                }
                catch (FormatException ex) { ThemedDialog.Info(Panel, "下次追催", ex.Message); }
            };
            defer.Items.Add(dCustom);
            items.Add(defer);

            var pause = new MenuItem { Header = "⏸ 暂缓 / 等待中", IsChecked = first != null && first.IsPaused };
            pause.Click += delegate { SetTasksPaused(targets); };
            items.Add(pause);
        }

        void OpenTaskStateMenu(Button anchor, List<Reminder> targets)
        {
            var menu = new ContextMenu();
            PopulateTaskStateItems(menu.Items, targets);
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
        }

        void CompleteOrRestoreMany(List<Reminder> items)
        {
            if (items.Count == 0) return;
            var active = items.Where(r => r.Active).ToList();
            if (active.Count > 0)
            {
                Remember("已完成 " + active.Count + " 项", null, active.ToArray());
                foreach (var item in active)
                {
                    item.Complete(clock());
                    Alerts.ClearChats(n => n.ReminderId == item.Id);
                }
            }
            else
            {
                Remember("已恢复 " + items.Count + " 项", null, items.ToArray());
                foreach (var item in items)
                {
                    item.Status = "active"; item.Done = false; item.TaskState = "";
                    item.CompletedAt = item.DeletedAt = 0;
                    if (item.Awaiting(clock()) && item.NextFollowUp == 0 && item.Delivered > 0) item.NextFollowUp = Reminder.Tomorrow(clock());
                }
            }
            selectedTasks.Clear();
            Changed();
        }

        void DeleteOrPurgeMany(List<Reminder> items)
        {
            if (items.Count == 0) return;
            var active = items.Where(r => r.Active).ToList();
            if (active.Count > 0)
            {
                Remember("已删除 " + active.Count + " 项", null, active.ToArray());
                long nowTicks = clock().Ticks;
                foreach (var r in active)
                {
                    r.Status = "deleted"; r.Done = true; r.DeletedAt = nowTicks;
                    Alerts.ClearChats(n => n.ReminderId == r.Id);
                }
            }
            else
            {
                if (!ThemedDialog.Confirm(Panel, "彻底删除", "彻底删除选中的 " + items.Count + " 项？此操作无法撤销。", "彻底删除", true)) return;
                var ids = new HashSet<string>(items.Select(x => x.Id), StringComparer.Ordinal);
                State.Reminders.RemoveAll(r => ids.Contains(r.Id));
                undo.RemoveAll(step => step.Before.Any(item => ids.Contains(item.Id)) || (step.AddedId != null && ids.Contains(step.AddedId)));
                HideUndo();
            }
            selectedTasks.Clear();
            Changed();
        }

        void FillWorkMenu(ContextMenu menu, Reminder r)
        {
            if (r == null) return;
            if (!r.Active)
            {
                MenuItem(menu, "恢复为进行中", delegate { RestoreWork(r); });
                MenuItem(menu, "复制为新项", delegate { DuplicateWork(r); });
                MenuItem(menu, "彻底删除", delegate { PurgeWork(r); });
                return;
            }
            var targets = r.IsTask ? TargetTasks(r) : SelectedReminders().Any(x => x.Id == r.Id) ? SelectedReminders() : new List<Reminder> { r };
            if (!r.IsTask)
            {
                string completeText = targets.Count > 1 ? "✓ 完成（" + targets.Count + " 项）"
                    : r.Recurring ? "✓ 完成本次（推进下一次）" : "✓ 完成";
                MenuItem(menu, completeText, delegate {
                    if (targets.Count == 1) CompleteWork(r); else CompleteOrRestoreMany(targets);
                });
            }
            MenuItem(menu, "编辑", delegate { EditWork(r); });
            if (r.IsTask)
            {
                var moveSub = new MenuItem { Header = targets.Count > 1 ? "移动已选 " + targets.Count + " 项至分组" : "移动到分组" };
                foreach (var g in TaskGroups())
                {
                    string gid = g.Id, gname = g.Name;
                    var subItem = new MenuItem { Header = gname, IsChecked = targets.All(t => t.GroupId == gid) };
                    subItem.Click += delegate { MoveTasksToGroup(targets, gid, gname); };
                    moveSub.Items.Add(subItem);
                }
                var createGrp = new MenuItem { Header = "新建分组…" };
                createGrp.Click += delegate {
                    string name = Prompt("任务分组名称", "");
                    if (String.IsNullOrWhiteSpace(name) || TaskGroups().Any(g => g.Name == name)) return;
                    var added = new TaskGroupDef { Id = Guid.NewGuid().ToString("N"), Name = name };
                    State.TaskGroups.Add(added);
                    MoveTasksToGroup(targets, added.Id, added.Name);
                };
                moveSub.Items.Add(createGrp);
                menu.Items.Add(moveSub);
                MenuItem(menu, targets.Count > 1 ? "确认已完成（" + targets.Count + " 项）" : "确认已完成", delegate {
                    if (targets.Count == 1) CompleteWork(r); else CompleteOrRestoreMany(targets);
                });
                var stateSub = new MenuItem { Header = targets.Count > 1 ? "切换状态（" + targets.Count + " 项）" : "切换状态" };
                PopulateTaskStateItems(stateSub.Items, targets);
                menu.Items.Add(stateSub);
                MenuItem(menu, "派生提醒副本", delegate { SwitchKind(r); });
                MenuItem(menu, "转为提醒", delegate { ConvertToReminder(r); });
            }
            else if (!r.Recurring) MenuItem(menu, "转为任务…", delegate { SwitchKind(r); });
            MenuItem(menu, targets.Count > 1 ? "删除（" + targets.Count + " 项）" : "删除", delegate {
                if (targets.Count == 1) DeleteWork(r); else DeleteOrPurgeMany(targets);
            });
        }

        void Remember(string label, string addedId, params Reminder[] items)
        {
            if (undo.Count == 20) undo.RemoveAt(0);
            undo.Add(new UndoStep { Before = items.Select(r => r.Copy()).ToArray(), AddedId = addedId });
            Panel.Part<TextBlock>("UndoText").Text = label;
            Panel.Part<DockPanel>("UndoBar").Visibility = Visibility.Visible;
            undoTimer.Stop(); undoTimer.Start();
        }

        void HideUndo()
        {
            undoTimer.Stop();
            Panel.Part<DockPanel>("UndoBar").Visibility = Visibility.Collapsed;
        }

        public void UndoWork()
        {
            if (undo.Count == 0) return;
            var step = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            foreach (var item in step.Before)
            {
                int i = State.Reminders.FindIndex(r => r.Id == item.Id);
                if (i >= 0) State.Reminders[i] = item;
                Alerts.ClearChats(n => n.ReminderId == item.Id);
            }
            if (step.AddedId != null)
            {
                State.Reminders.RemoveAll(r => r.Id == step.AddedId);
                Alerts.ClearChats(n => n.ReminderId == step.AddedId);
            }
            HideUndo();
            Changed();
        }

        void CompleteWork(Reminder r)
        {
            var copies = r.IsTask ? State.Reminders.Where(x => !x.IsTask && x.LinkedId == r.Id && x.Active && x.Delivered == 0).ToArray() : new Reminder[0];
            if (copies.Length > 0 && !ThemedDialog.Confirm(Panel, "确认完成", "同时结束 " + copies.Length + " 条尚未触发的关联提醒？", "一并结束", false)) copies = new Reminder[0];
            Remember("已完成「" + r.Title + "」", null, new[] { r }.Concat(copies).ToArray());
            foreach (var item in new[] { r }.Concat(copies))
            {
                item.Complete(clock());
                Alerts.ClearChats(n => n.ReminderId == item.Id);
            }
            Changed();
        }

        void DeleteWork(Reminder r)
        {
            Remember("已删除「" + r.Title + "」", null, r);
            r.Status = "deleted";
            r.Done = true;
            r.DeletedAt = clock().Ticks;
            Alerts.ClearChats(n => n.ReminderId == r.Id);
            Changed();
        }

        void RestoreWork(Reminder r)
        {
            r.Status = "active"; r.Done = false; r.TaskState = "";
            r.CompletedAt = r.DeletedAt = 0;
            if (r.Awaiting(clock()) && r.NextFollowUp == 0 && r.Delivered > 0) r.NextFollowUp = Reminder.Tomorrow(clock());
            Changed();
        }

        void DuplicateWork(Reminder original)
        {
            var r = original.Copy();
            r.Id = Guid.NewGuid().ToString("N");
            r.Status = "active"; r.Done = false; r.LinkedId = "";
            r.NextFollowUp = r.CompletedAt = r.DeletedAt = r.Delivered = r.Released = r.Snooze = 0;
            if (r.Start > 0) r.Start = clock().AddMinutes(10).Ticks;
            Forms.Editor(Panel, r).ShowDialog();
        }

        void PurgeWork(Reminder r)
        {
            if (!ThemedDialog.Confirm(Panel, "彻底删除", "彻底删除「" + r.Title + "」？此操作无法撤销。", "彻底删除", true)) return;
            State.Reminders.Remove(r);
            undo.RemoveAll(step => step.Before.Any(item => item.Id == r.Id) || step.AddedId == r.Id);
            HideUndo();
            Changed();
        }

        void SwitchKind(Reminder r)
        {
            if (r == null || !r.Active) return;
            if (r.IsTask)
            {
                var copy = new Reminder { Title = r.Title, Start = clock().AddMinutes(10).Ticks, Level = r.Level, LinkedId = r.Id, Status = "active" };
                Remember("已派生「" + r.Title + "」的提醒", copy.Id, r);
                SaveReminder(copy);
                return;
            }
            if (r.Recurring) return;
            var group = ChooseTaskGroup();
            if (group == null) return;
            Remember("已转为任务「" + r.Title + "」", null, r);
            r.Kind = "task"; r.GroupId = group.Id; r.ConfirmRequired = true;
            r.NextFollowUp = r.Snooze > clock().Ticks ? r.Snooze : r.Delivered > 0 ? Reminder.Tomorrow(clock()) : 0;
            r.Snooze = 0;
            Alerts.ClearChats(n => n.ReminderId == r.Id);
            SelectWorkTab(true);
            Changed();
        }

        void ConvertToReminder(Reminder r)
        {
            Remember("已转为提醒「" + r.Title + "」", null, r);
            r.Kind = "reminder"; r.GroupId = ""; r.LinkedId = "";
            r.Start = r.NextFollowUp > clock().Ticks ? r.NextFollowUp : r.Start > clock().Ticks ? r.Start : clock().AddMinutes(10).Ticks;
            r.Delivered = r.Released = r.NextFollowUp = r.Snooze = 0;
            r.Quiet = false;
            Alerts.ClearChats(n => n.ReminderId == r.Id);
            SelectWorkTab(false);
            Changed();
        }

        TaskGroupDef ChooseTaskGroup()
        {
            var combo = new ComboBox { ItemsSource = TaskGroups().ToList(), DisplayMemberPath = "Name", SelectedIndex = 0 };
            var dialog = PromptWindow("选择任务分组", combo);
            return dialog.ShowDialog() == true ? combo.SelectedItem as TaskGroupDef : null;
        }

        void SelectWorkTab(bool task)
        {
            Panel.Part<TabControl>("Tabs").SelectedItem = Panel.Part<TabItem>(task ? "TasksTab" : "RemindersTab");
            Panel.Part<ComboBox>(task ? "TaskScope" : "Scope").SelectedIndex = 0;
        }

        void ScheduleAction(string id, long value)
        {
            var r = State.Reminders.Find(x => x.Id == id);
            if (r == null || !r.Active) return;
            if (value == 0) { CompleteWork(r); return; }
            if (r.IsTask)
            {
                if (value == -2) { SetTasksInProgress(new List<Reminder> { r }); return; }
                if (value == -3) { SetTasksPaused(new List<Reminder> { r }); return; }
                long ticks = value == -1 ? Reminder.Tomorrow(clock()) : value <= 60 ? clock().AddMinutes(value).Ticks : value;
                Remember("已延后追催「" + r.Title + "」", null, r);
                r.NextFollowUp = ticks;
                r.TaskState = "deferred";
            }
            else r.Snooze = clock().AddMinutes(value).Ticks;
            Changed();
        }

        static bool InScope(Reminder r, int scope, DateTime now)
        {
            if (scope == 2) return r.Status == "completed";
            if (scope == 3) return r.Status == "deleted";
            if (!r.Active) return false;
            if (scope != 1) return true;
            if (!r.IsTask) return new DateTime(r.Next(now.Date)).Date == now.Date;
            return r.Awaiting(now) || new DateTime(r.Start).Date == now.Date || new DateTime(r.NextFollowUp).Date == now.Date;
        }

        static readonly string[] WeekNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        static readonly string[] RuleNames = { "一次", "每天", "每周", "工作日", "每月", "每年", "指定日" };

        static string DayHint(DateTime at, DateTime now)
        {
            int d = (at.Date - now.Date).Days;
            if (d == 0) return "今天";
            if (d == 1) return "明天";
            if (d == -1) return "昨天";
            if (d > 1 && d < 7) return WeekNames[(int)at.DayOfWeek];
            return at.ToString(at.Year == now.Year ? "MM-dd" : "yyyy-MM-dd");
        }

        bool HasLinkedReminder(Reminder r)
        {
            return State.Reminders.Any(x => !x.IsTask && x.LinkedId == r.Id && x.Status != "deleted");
        }

        string WorkLabel(Reminder r, long date)
        {
            string label = (date == 0 ? "" : new DateTime(date).ToString("MM-dd HH:mm") + "  ") + r.Title;
            if (r.IsTask)
            {
                if (r.NextFollowUp > 0) label += " · 追催 " + new DateTime(r.NextFollowUp).ToString("MM-dd HH:mm");
                if (HasLinkedReminder(r)) label += " · 含提醒";
            }
            else
            {
                if (r.RepeatDays == 7) label += " · 每周";
                if (r.RepeatDays == 1) label += " · 每天";
                if (r.Quiet) label += " · 免打扰";
                if (r.RepeatDays == 0 && r.Delivered > 0 && r.Active) label += " · 已提醒";
                if (!String.IsNullOrEmpty(r.LinkedId)) label += " · 来自任务";
            }
            return label;
        }

        /// <summary>把一条提醒拆成结构化行：时间列、日期提示、标题、状态胶囊与标签。</summary>
        AgendaRow BuildRow(Reminder r, long date, DateTime now)
        {
            var tags = new List<string>();
            string status = null;
            if (!r.Active) tags.Add(r.Status == "deleted" ? "已删除" : "✓ 已完成");
            int kind = r.Rule == null ? -1 : Array.IndexOf(RecurrenceRule.Kinds, r.Rule.Kind);
            if (kind > 0) tags.Add("↻ " + RuleNames[kind]);
            else if (r.RepeatDays == 7) tags.Add("↻ 每周");
            else if (r.RepeatDays == 1) tags.Add("↻ 每天");
            if (r.Quiet) tags.Add("☾ 会议免打扰");
            if (!String.IsNullOrEmpty(r.LinkedId)) tags.Add("⇠ 来自任务");
            if (r.RepeatDays == 0 && r.Delivered > 0 && r.Active) status = "● 已提醒，待处理";
            var row = new AgendaRow { Item = r, Label = WorkLabel(r, date), Title = r.Title, Tags = tags, Status = status,
                Level = r.Level, Emphasis = status != null, Inactive = !r.Active };
            if (date == 0) { row.TimeText = "—"; row.DayText = "未定时"; }
            else { var at = new DateTime(date); row.TimeText = at.ToString("HH:mm"); row.DayText = DayHint(at, now); }
            return row;
        }

        FrameworkElement TaskLeafView(Reminder t, bool pending, long date, DateTime now)
        {
            var parts = new List<string>();
            if (date > 0) parts.Add(DayHint(new DateTime(date), now) + " " + new DateTime(date).ToString("HH:mm"));
            else if (t.Active) parts.Add("未定时");
            if (t.IsPaused) parts.Add("已暂停追催");
            else if (t.NextFollowUp > 0) parts.Add("追催 " + DayHint(new DateTime(t.NextFollowUp), now) + " " + new DateTime(t.NextFollowUp).ToString("HH:mm"));
            if (HasLinkedReminder(t)) parts.Add("含提醒");
            var right = new List<UIElement>();
            if (t.Active)
            {
                if (taskMultiMode)
                {
                    if (pending) right.Add(Rows.Pill("待确认", "L3SoftBrush", "L3Brush"));
                    else if (t.IsPaused) right.Add(Rows.Pill("暂缓", "WarningSoftBrush", "WarningBrush"));
                    else if (t.IsDeferred(now)) right.Add(Rows.Pill("已延后", null, null));
                }
                else
                {
                    string label = pending ? "待确认 ▾" : t.IsPaused ? "暂缓 ▾" : t.IsDeferred(now) ? "已延后 ▾" : "进行中 ▾";
                    string bg = pending ? "L3SoftBrush" : t.IsPaused ? "WarningSoftBrush" : "SubCardBrush";
                    string fg = pending ? "L3Brush" : t.IsPaused ? "WarningBrush" : "MutedBrush";
                    var currentTask = t;
                    right.Add(Rows.PillButton(label, bg, fg, "点击切换任务状态", btn => OpenTaskStateMenu(btn, TargetTasks(currentTask))));
                }
            }
            else if (t.Status == "completed") right.Add(Rows.Pill("✓ 已完成", null, null));
            return Rows.Leaf(t.Title, String.Join(" · ", parts), pending, right);
        }

        void SetupScopeChips(string combo, string chipPrefix)
        {
            var box = Panel.Part<ComboBox>(combo);
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                var chip = Panel.Part<RadioButton>(chipPrefix + i);
                chip.Checked += delegate { if (box.SelectedIndex != index) box.SelectedIndex = index; };
            }
            box.SelectionChanged += delegate {
                var chip = Panel.Part<RadioButton>(chipPrefix + Math.Max(0, box.SelectedIndex));
                if (chip.IsChecked != true) chip.IsChecked = true;
            };
        }

        bool agendaBarActive, taskBarActive;

        /// <summary>上下文操作条：未选中时只给新建入口，选中后出现完成 / 编辑 / 删除。</summary>
        void UpdateWorkBars()
        {
            int scope = Panel.Part<ComboBox>("Scope").SelectedIndex;
            int n = Panel.Part<ListBox>("Agenda").SelectedItems.Count;
            bool sel = n > 0;
            Panel.Part<Button>("Done").Visibility = Panel.Part<Button>("Delete").Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("Edit").Visibility = sel && n == 1 && scope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("Add").Visibility = !sel && scope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<TextBlock>("AgendaHint").Text = sel ? "已选 " + n + " 项" : scope < 2 ? "点击行末 ✓ 完成，或选中条目操作" : "选择条目后可恢复或彻底删除";
            if (sel && !agendaBarActive) Motion.Reveal(Panel.Part<Border>("ReminderBar"), 4, 140);
            agendaBarActive = sel;

            int taskScope = Panel.Part<ComboBox>("TaskScope").SelectedIndex;
            int m = TargetTasks(Selected(true)).Count;
            bool tsel = m > 0;
            Panel.Part<Button>("DoneTask").Visibility = Panel.Part<Button>("DeleteTask").Visibility = tsel ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("EditTask").Visibility = tsel && m == 1 && taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("MoveTaskGroup").Visibility = tsel && taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("TaskStateBtn").Visibility = tsel && taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<Button>("AddTask").Visibility = !tsel && taskScope < 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Part<TextBlock>("TaskHint").Text = tsel ? "已选 " + m + " 项" : taskScope < 2 ? "" : "选择任务后可恢复或彻底删除";
            if (tsel && !taskBarActive) Motion.Reveal(Panel.Part<Border>("TaskBar"), 4, 140);
            taskBarActive = tsel;
        }

        void RefreshAgenda()
        {
            var now = clock();
            int scope = Panel.Part<ComboBox>("Scope").SelectedIndex;
            int taskScope = Panel.Part<ComboBox>("TaskScope").SelectedIndex;
            var list = Panel.Part<ListBox>("Agenda");
            var selectedIds = new HashSet<string>(SelectedReminders().Select(x => x.Id), StringComparer.Ordinal);
            var items = State.Reminders.Where(r => !r.IsTask && InScope(r, scope, now))
                .OrderBy(r => scope == 2 ? -r.CompletedAt : scope == 3 ? -r.DeletedAt : r.Next(scope == 1 ? now.Date : now))
                .Select(r => BuildRow(r, r.Active ? r.Next(scope == 1 ? now.Date : now) : (scope == 2 ? r.CompletedAt : r.DeletedAt), now)).ToList();
            refreshingAgenda = true;
            try
            {
                list.ItemsSource = items;
                foreach (var row in items.Where(x => selectedIds.Contains(x.Item.Id)))
                    list.SelectedItems.Add(row);
            }
            finally { refreshingAgenda = false; }
            var empty = Panel.Part<TextBlock>("AgendaEmpty");
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = scope == 0 ? "这里还没有提醒\n点击左下角「＋ 新建提醒」或顶部「新建 ▾」创建" : scope == 1 ? "今天没有提醒" : scope == 2 ? "还没有已完成的提醒" : "回收站是空的";
            Panel.Part<Button>("Done").Content = scope < 2 ? "✓ 完成" : "恢复";
            Panel.Part<Button>("Delete").Content = scope < 2 ? "删除" : "彻底删除";
            Panel.Part<Button>("DoneTask").Content = taskScope < 2 ? "✓ 完成" : "恢复";
            Panel.Part<Button>("DeleteTask").Content = taskScope < 2 ? "删除" : "彻底删除";

            var tree = Panel.Part<TreeView>("Tasks");
            var task = Selected(true);
            var selectedGroup = TaskBranch();
            var groups = TaskGroups().ToList();
            var tasks = State.Reminders.Where(r => r.IsTask).ToList();
            selectedTasks.RemoveWhere(id => !tasks.Any(t => t.Id == id && InScope(t, taskScope, now)));
            int pendingCount = tasks.Count(r => r.Awaiting(now));
            Panel.Part<TextBlock>("PendingTasks").Text = "逾期 / 待确认 " + pendingCount + " 项";
            Panel.Part<Border>("PendingPill").Visibility = pendingCount > 0 ? Visibility.Visible : Visibility.Hidden;
            Panel.Part<TabItem>("RemindersTab").Header = Rows.TabHeader("提醒", State.Reminders.Count(r => !r.IsTask && r.Active), "MutedBrush", false);
            Panel.Part<TabItem>("TasksTab").Header = pendingCount > 0 ? Rows.TabHeader("任务", pendingCount, "L3Brush", true)
                : Rows.TabHeader("任务", tasks.Count(r => r.Active), "MutedBrush", false);
            buildingTaskTree = true;
            try
            {
                tree.Items.Clear();
                foreach (var group in groups)
                {
                    var currentGroup = group;
                    var members = tasks.Where(r => r.GroupId == currentGroup.Id || currentGroup.Id == "" && !groups.Any(g => g.Id == r.GroupId)).ToList();
                    var visibleMembers = members.Where(x => InScope(x, taskScope, now)).OrderByDescending(x => x.Awaiting(now)).ThenBy(x => x.IsPaused ? 1 : 0).ThenBy(x => x.Start).ToList();
                    string branchText = currentGroup.Name + "  (" + members.Count(r => r.Active) + " 未完成)";
                    int groupPending = members.Count(r => r.Awaiting(now));
                    object branchHeader = Rows.Branch(currentGroup.Name, members.Count(r => r.Active) + " 未完成", groupPending, "L3Brush", "OnL3Brush");
                    if (taskMultiMode)
                    {
                        var groupCheck = new CheckBox
                        {
                            Content = Rows.Branch(currentGroup.Name, members.Count(r => r.Active) + " 未完成", groupPending, "L3Brush", "OnL3Brush"),
                            FontWeight = FontWeights.SemiBold,
                            IsChecked = visibleMembers.Count > 0 && visibleMembers.All(x => selectedTasks.Contains(x.Id))
                        };
                        var groupIds = visibleMembers.Select(x => x.Id).ToList();
                        groupCheck.Click += (s, e) => {
                            e.Handled = true;
                            if (groupCheck.IsChecked == true) foreach (var id in groupIds) selectedTasks.Add(id);
                            else foreach (var id in groupIds) selectedTasks.Remove(id);
                            RefreshAgenda();
                        };
                        branchHeader = groupCheck;
                    }
                    var branch = new TreeViewItem
                    {
                        Header = branchHeader,
                        Tag = currentGroup,
                        FontWeight = FontWeights.SemiBold,
                        IsExpanded = !State.CollapsedTaskGroups.Contains(currentGroup.Id)
                    };
                    Rows.Name(branch, branchText);
                    branch.Expanded += (s, e) => {
                        if (buildingTaskTree || e.OriginalSource != branch) return;
                        if (State.CollapsedTaskGroups.Remove(currentGroup.Id)) Persist();
                        UpdateTaskExpandButton();
                    };
                    branch.Collapsed += (s, e) => {
                        if (buildingTaskTree || e.OriginalSource != branch) return;
                        if (!State.CollapsedTaskGroups.Contains(currentGroup.Id)) { State.CollapsedTaskGroups.Add(currentGroup.Id); Persist(); }
                        UpdateTaskExpandButton();
                    };
                    tree.Items.Add(branch);
                    if (task == null && selectedGroup != null && selectedGroup.Id == currentGroup.Id) branch.IsSelected = true;
                    var branchOrder = visibleMembers.Select(x => x.Id).ToList();
                    foreach (var r in visibleMembers)
                    {
                        var currentTask = r;
                        bool pending = currentTask.Awaiting(now);
                        long leafDate = taskScope == 2 ? currentTask.CompletedAt : taskScope == 3 ? currentTask.DeletedAt : currentTask.Start;
                        string leafText = (pending ? "待确认 · " : "") + WorkLabel(currentTask, leafDate);
                        object leafHeader = TaskLeafView(currentTask, pending, leafDate, now);
                        if (taskMultiMode)
                        {
                            var cb = new CheckBox { Content = TaskLeafView(currentTask, pending, leafDate, now), IsChecked = selectedTasks.Contains(currentTask.Id), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                            cb.Click += (s, e) => {
                                e.Handled = true;
                                if (cb.IsChecked == true) selectedTasks.Add(currentTask.Id); else selectedTasks.Remove(currentTask.Id);
                                lastClickedTask = currentTask.Id;
                                RefreshAgenda();
                            };
                            leafHeader = cb;
                        }
                        var leaf = new TreeViewItem { Tag = currentTask, Header = leafHeader };
                        Rows.Name(leaf, leafText);
                        if (selectedTasks.Contains(currentTask.Id))
                        {
                            leaf.SetResourceReference(Control.BackgroundProperty, "SelectionBrush");
                            leaf.SetResourceReference(Control.ForegroundProperty, "SelectionInkBrush");
                        }
                        leaf.PreviewMouseLeftButtonDown += (s, e) => {
                            if (taskMultiMode || IsInsideButton(e.OriginalSource as DependencyObject, leaf)) return;
                            if (Keyboard.Modifiers == ModifierKeys.Control)
                            {
                                if (!selectedTasks.Remove(currentTask.Id)) selectedTasks.Add(currentTask.Id);
                                lastClickedTask = currentTask.Id;
                                RefreshAgenda();
                                e.Handled = true;
                            }
                            else if (Keyboard.Modifiers == ModifierKeys.Shift && lastClickedTask != null)
                            {
                                int a = branchOrder.IndexOf(lastClickedTask), b = branchOrder.IndexOf(currentTask.Id);
                                if (a >= 0 && b >= 0)
                                {
                                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                                    for (int idx = lo; idx <= hi; idx++) selectedTasks.Add(branchOrder[idx]);
                                    RefreshAgenda();
                                    e.Handled = true;
                                }
                            }
                            else
                            {
                                lastClickedTask = currentTask.Id;
                                if (selectedTasks.Count > 0) { selectedTasks.Clear(); RefreshAgenda(); }
                            }
                        };
                        leaf.ContextMenu = new ContextMenu();
                        FillWorkMenu(leaf.ContextMenu, currentTask);
                        branch.Items.Add(leaf);
                        if (task != null && currentTask.Id == task.Id) leaf.IsSelected = true;
                    }
                }
            }
            finally { buildingTaskTree = false; }
            UpdateTaskBatchBar();
            UpdateTaskExpandButton();
            UpdateWorkBars();
        }
    }
}
