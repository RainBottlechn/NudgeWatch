using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NudgeWatch
{
    public sealed class Forms
    {
        public readonly Window Quick = Views.Load("Capture");
        readonly TextBox input;
        readonly Action<Reminder> save;
        internal Func<IEnumerable<TaskGroupDef>> Groups;
        internal Action<Reminder> SwitchKind;
        internal Action<Reminder> CompleteItem;
        internal Func<string, bool> Exists;
        public Forms(Action<Reminder> save)
        {
            this.save = save;
            input = Quick.Part<TextBox>("Input");
            Quick.Click("Save", Submit);
            Quick.Click("Later", delegate { input.Text = "10分钟后 " + input.Text; input.Focus(); input.CaretIndex = input.Text.Length; });
            Quick.Click("Hour", delegate { input.Text = input.Text.TrimEnd() + " #避免1h "; input.Focus(); input.CaretIndex = input.Text.Length; });
            Quick.PreviewKeyDown += (s,e) => {
                if (e.Key == Key.Escape) { Quick.Hide(); e.Handled = true; }
                if (e.Key == Key.Enter && !imeComposing) { Submit(); e.Handled = true; }
            };
            input.TextChanged += delegate { Preview(); };
            TextCompositionManager.AddPreviewTextInputStartHandler(input, (s,e) => imeComposing = true);
            TextCompositionManager.AddPreviewTextInputHandler(input, (s,e) => imeComposing = false);
            Quick.Closing += (s,e) => { e.Cancel = true; Quick.Hide(); };
        }
        bool imeComposing;
        public void OpenQuick()
        {
            imeComposing = false;
            Quick.Show(); Quick.Activate(); input.Focus(); Keyboard.Focus(input);
            Motion.Reveal(Quick.Part<FrameworkElement>("Card"), -8, 160);
        }
        const string DefaultHint = "例：15:00 评审 / #任务 核对流水";
        /// <summary>实时解析预览：回车前就能确认时间、标题、免打扰与等级是否被正确识别。</summary>
        void Preview()
        {
            var hint = Quick.Part<TextBlock>("Hint");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            string text = input.Text.Trim();
            if (text.Length == 0) { hint.Text = DefaultHint; return; }
            try
            {
                var r = Capture.Parse(input.Text, DateTime.Now);
                hint.Text = Describe(r, DateTime.Now);
                hint.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            }
            catch (FormatException ex) { hint.Text = "继续输入… " + ex.Message; }
        }
        internal static string Describe(Reminder r, DateTime now)
        {
            var parts = new List<string> { r.IsTask ? "任务" : "提醒" };
            if (r.Start > 0)
            {
                var at = new DateTime(r.Start);
                int d = (at.Date - now.Date).Days;
                parts.Add((d == 0 ? "今天" : d == 1 ? "明天" : at.ToString("MM-dd")) + " " + at.ToString("HH:mm"));
            }
            else parts.Add("未定时");
            parts.Add(r.Title);
            if (r.RepeatDays == 7) parts.Add("每周");
            if (r.RepeatDays == 1) parts.Add("每天");
            if (r.Quiet) parts.Add("免打扰" + (r.Duration % 60 == 0 ? r.Duration / 60 + "h" : r.Duration + "m"));
            parts.Add(ThemeManager.LevelName(r.Level));
            return "→ " + String.Join(" · ", parts);
        }
        void Submit()
        {
            try { save(Capture.Parse(input.Text, DateTime.Now)); }
            catch (FormatException ex) { var h = Quick.Part<TextBlock>("Hint"); h.Text = ex.Message; h.SetResourceReference(TextBlock.ForegroundProperty, "L3Brush"); return; }
            input.Clear();
            Quick.Part<TextBlock>("Hint").Text = DefaultHint;
            Quick.Hide();
        }
        static bool Time(string value, out TimeSpan t)
        {
            return TimeSpan.TryParseExact(value.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out t);
        }
        public Window Editor(Window owner, Reminder original)
        {
            var w = Views.Load("Editor");
            w.Owner = owner;
            var r = original ?? new Reminder { Start = DateTime.Now.AddMinutes(10).Ticks };
            var at = r.Start == 0 ? DateTime.Now.AddMinutes(10) : new DateTime(r.Start);
            var day = w.Part<DatePicker>("Day");
            var clock = w.Part<TextBox>("StartTime");
            var end = w.Part<TextBox>("EndTime");
            var delay = w.Part<TextBox>("Delay");
            var subject = w.Part<TextBox>("Subject");
            var avoid = w.Part<CheckBox>("Avoid");
            var repeat = w.Part<ComboBox>("Repeat");
            var level = w.Part<ComboBox>("Level");
            var taskGroup = w.Part<ComboBox>("TaskGroup");
            var timedTask = w.Part<CheckBox>("TimedTask");
            var follow = w.Part<TextBox>("FollowUp");
            if (r.IsTask)
            {
                w.Title = "任务";
                w.Part<StackPanel>("TaskFields").Visibility = Visibility.Visible;
                w.Part<Border>("QuietCard").Visibility = Visibility.Collapsed;
                repeat.Visibility = Visibility.Collapsed;
                w.Part<TextBlock>("LevelLabel").Text = "提醒等级";
                timedTask.IsChecked = r.Start > 0;
                var groups = Groups().ToList();
                taskGroup.ItemsSource = groups;
                taskGroup.SelectedItem = groups.FirstOrDefault(g => g.Id == r.GroupId) ?? groups[0];
                if (r.NextFollowUp > 0) follow.Text = new DateTime(r.NextFollowUp).ToString("yyyy-MM-dd HH:mm");
                w.Part<DockPanel>("ScheduleFields").SetBinding(UIElement.IsEnabledProperty,
                    new System.Windows.Data.Binding("IsChecked") { Source = timedTask });
                w.Part<Button>("Save").Content = "保存任务";
            }
            if (original != null && original.Active && Exists != null && Exists(original.Id))
            {
                var completeBtn = w.Part<Button>("CompleteItem");
                if (completeBtn != null)
                {
                    completeBtn.Visibility = Visibility.Visible;
                    completeBtn.Content = r.IsTask ? "✓ 确认已完成" : "✓ 完成提醒";
                    completeBtn.Click += delegate { w.Close(); if (CompleteItem != null) CompleteItem(original); };
                    var footerHint = w.Part<TextBlock>("FooterHint");
                    if (footerHint != null) footerHint.Visibility = Visibility.Collapsed;
                }
            }
            if (original != null && original.Active && (r.IsTask || !r.Recurring) && Exists != null && Exists(original.Id))
            {
                var button = w.Part<Button>("SwitchKind");
                button.Visibility = Visibility.Visible;
                button.Content = r.IsTask ? "从已保存任务派生提醒" : "将已保存提醒转为任务";
                button.Click += delegate { w.Close(); SwitchKind(original); };
            }
            day.SelectedDate = at.Date;
            clock.Text = at.ToString("HH:mm");
            subject.Text = r.Title ?? "";
            delay.Text = r.Delay.ToString();
            avoid.IsChecked = r.Quiet;
            repeat.ItemsSource = new[] { "一次", "每天", "每周多选", "工作日", "每月多日", "每年月日", "指定多日" };
            repeat.SelectedIndex = r.RepeatDays == 7 ? 2 : r.RepeatDays == 1 ? 1 : 0;
            var days=w.Part<TextBox>("Days");
            if(r.Rule!=null) { repeat.SelectedIndex=Array.IndexOf(RecurrenceRule.Kinds,r.Rule.Kind); days.Text=string.Join(",",r.Rule.Values); }
            string[] hints={"","","星期 1–7，逗号分隔","","每月 1–31，逗号分隔","MM-dd，逗号分隔","yyyy-MM-dd，逗号分隔"};
            string[] examples={"","","如 1,3,5（周一、三、五）","","如 1,15","如 03-08,10-01","如 2026-12-01,2026-12-15"};
            Action hint=delegate { var h=w.Part<TextBlock>("DaysHint"); h.Text=hints[repeat.SelectedIndex]; days.Tag=examples[repeat.SelectedIndex];
                days.Visibility=h.Visibility=h.Text==""?Visibility.Collapsed:Visibility.Visible; };
            hint(); repeat.SelectionChanged+=delegate { days.Clear(); hint(); };
            level.ItemsSource = new[] { "L1 仅记录", "L2 浮片", "L3 强提醒", "L4 桌面提醒" };
            // 分段控件与隐藏的 Level 下拉框双向同步（保留原有保存逻辑）
            for (int i = 1; i <= 4; i++)
            {
                int lv = i;
                w.Part<RadioButton>("LevelSeg" + i).Checked += delegate { if (level.SelectedIndex != lv - 1) level.SelectedIndex = lv - 1; };
            }
            level.SelectionChanged += delegate { if (level.SelectedIndex >= 0) w.Part<RadioButton>("LevelSeg" + (level.SelectedIndex + 1)).IsChecked = true; };
            level.SelectedIndex = r.Level - 1;
            w.Part<RadioButton>("LevelSeg" + Math.Max(1, Math.Min(4, r.Level))).IsChecked = true;
            w.Click("Cancel", w.Close);
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } };
            int duration = r.Duration;
            Action updateEnd = delegate {
                TimeSpan start; int buffer;
                if (Time(clock.Text, out start) && int.TryParse(delay.Text, out buffer) && buffer >= 0 && buffer <= 1440)
                    end.Text = DateTime.Today.Add(start).AddMinutes(buffer + duration).ToString("HH:mm");
            };
            Action readDuration = delegate {
                TimeSpan start, finish; int buffer;
                if (!Time(clock.Text, out start) || !Time(end.Text, out finish) || !int.TryParse(delay.Text, out buffer)) return;
                duration = ((int)(finish - start).TotalMinutes - buffer) % 1440;
                if (duration <= 0) duration += 1440;
            };
            WheelBehavior.Attach(day,clock,end,delay);
            updateEnd();
            clock.TextChanged += delegate { updateEnd(); };
            delay.TextChanged += delegate { updateEnd(); };
            end.LostFocus += delegate { readDuration(); };
            w.Click("Half", delegate { duration = 30; updateEnd(); });
            w.Click("One", delegate { duration = 60; updateEnd(); });
            w.Click("Two", delegate { duration = 120; updateEnd(); });
            w.Click("Save", delegate {
                try
                {
                    TimeSpan start, finish;
                    int buffer;
                    bool timed = !r.IsTask || timedTask.IsChecked == true;
                    bool quiet = !r.IsTask && avoid.IsChecked == true;
                    start = TimeSpan.Zero;
                    if (timed && (!day.SelectedDate.HasValue || !Time(clock.Text, out start))) throw new FormatException("请输入日期和有效时分");
                    if (quiet && (!Time(end.Text, out finish) || !int.TryParse(delay.Text, out buffer)))
                        throw new FormatException("请输入缓冲分钟数和结束时分");
                    if (quiet) readDuration();
                    DateTime scheduled = timed ? day.SelectedDate.Value.Date.Add(start) : DateTime.MinValue;
                    var edited = r.Copy();
                    edited.Title = subject.Text.Trim();
                    edited.Start = scheduled.Ticks;
                    edited.Level = level.SelectedIndex + 1;
                    edited.Quiet = quiet;
                    edited.Delay = quiet ? int.Parse(delay.Text) : 3;
                    edited.Duration = quiet ? duration : 60;
                    if (r.Start / TimeSpan.TicksPerMinute == edited.Start / TimeSpan.TicksPerMinute) edited.Start = r.Start;
                    else
                    {
                        edited.Delivered = edited.Released = edited.Snooze = 0;
                        if (edited.IsTask && edited.Start > DateTime.Now.Ticks && edited.TaskState != "paused") edited.TaskState = "";
                    }
                    if (r.IsTask)
                    {
                        edited.GroupId = ((TaskGroupDef)taskGroup.SelectedItem).Id;
                        var followText = follow.Text.Trim();
                        var oldFollow = r.NextFollowUp == 0 ? "" : new DateTime(r.NextFollowUp).ToString("yyyy-MM-dd HH:mm");
                        if (followText != oldFollow)
                        {
                            if (followText.Length == 0)
                            {
                                edited.NextFollowUp = 0;
                                edited.Delivered = 0;
                                if (!edited.IsPaused) edited.TaskState = "in_progress";
                                if (edited.Start > 0 && edited.Start <= DateTime.Now.Ticks) edited.Start = 0;
                            }
                            else
                            {
                                edited.NextFollowUp = ParseFollowUp(followText);
                                if (!edited.IsPaused) edited.TaskState = "deferred";
                            }
                        }
                    }
                    else
                    {
                        edited.Rule = RecurrenceRule.Parse(RecurrenceRule.Kinds[repeat.SelectedIndex], days.Text, scheduled);
                        edited.RepeatDays = repeat.SelectedIndex == 2 ? 7 : repeat.SelectedIndex == 1 ? 1 : 0;
                        if (edited.Recurring && edited.Rule.Find(edited.Start, scheduled, 1) == 0) throw new FormatException("请选择有效的未来日期");
                    }
                    Capture.Validate(edited);
                    save(edited); w.Close();
                }
                catch (FormatException ex) { w.Part<TextBlock>("Error").Text = ex.Message; }
            });
            w.Loaded += delegate { subject.Focus(); };
            return w;
        }
        internal static long ParseFollowUp(string value)
        {
            DateTime at;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at) || at <= DateTime.Now)
                throw new FormatException("请输入未来时间，格式 yyyy-MM-dd HH:mm");
            return at.Ticks;
        }
    }
}

