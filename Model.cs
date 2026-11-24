using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NudgeWatch
{
    // Local wall-clock ticks preserve meeting time across daylight-saving boundaries.
    public sealed class Reminder
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Title;
        public long Start, Delivered, Released;
        public int RepeatDays, Level = 2, Delay = 3, Duration = 60;
        public bool Quiet, Done;
        public RecurrenceRule Rule;
        public long Snooze;
        public string Kind = "reminder", Status, GroupId = "", LinkedId = "";
        public long CompletedAt, DeletedAt, NextFollowUp;
        public bool ConfirmRequired = true;
        public string TaskState = "";
        internal bool IsTask { get { return Kind == "task"; } }
        internal bool Active { get { return !Done && Status != "completed" && Status != "deleted"; } }
        internal bool IsPaused { get { return Active && IsTask && TaskState == "paused"; } }
        internal bool IsDeferred(DateTime now) { return Active && IsTask && TaskState == "deferred" && NextFollowUp > now.Ticks; }
        internal Reminder Copy() { return (Reminder)MemberwiseClone(); }
        internal bool Recurring { get { return Rule == null ? RepeatDays != 0 : Rule.Kind != "once"; } }

        internal long TaskDue(DateTime now)
        {
            if (IsPaused) return 0;
            if (TaskState == "deferred" && NextFollowUp > 0) return NextFollowUp;
            if (Start > 0 && Delivered == 0 && (NextFollowUp == 0 || NextFollowUp > now.Ticks)) return Start;
            return NextFollowUp;
        }

        internal bool Awaiting(DateTime now)
        {
            if (!Active || !IsTask || IsPaused) return false;
            if (TaskState == "deferred" && NextFollowUp > now.Ticks) return false;
            return Delivered > 0 || (Start > 0 && Start <= now.Ticks);
        }

        internal static long Tomorrow(DateTime now) { return now.Date.AddDays(1).AddHours(9.5).Ticks; }

        public void Complete(DateTime now)
        {
            Snooze=0;
            long next = IsTask || !Recurring ? 0 : Next(new DateTime(Math.Max(now.Ticks,Start)).AddTicks(1));
            if(next==0) { Done=true; Status="completed"; CompletedAt=now.Ticks; NextFollowUp=0; TaskState=""; }
            else { Start=next; Delivered=0; Released=0; }
        }

        public long Latest(DateTime now)
        {
            if (!Active || IsTask || Start > now.Ticks) return 0;
            if (Rule!=null) return Rule.Find(Start,now,-1);
            if (RepeatDays == 0) return Start;
            long days = (now.Ticks - Start) / TimeSpan.TicksPerDay;
            return new DateTime(Start).AddDays(days - days % RepeatDays).Ticks;
        }

        public long Next(DateTime now)
        {
            if (Rule!=null) return Rule.Find(Start,now,1);
            if (Start >= now.Ticks) return Start;
            long latest = Latest(now);
            if (latest == 0 || RepeatDays == 0 || latest == now.Ticks) return latest;
            return new DateTime(latest).AddDays(RepeatDays).Ticks;
        }

        public long QuietEnd(DateTime now)
        {
            // The previous day's late meeting may still be quiet before today's delayed start.
            long occurrence = Latest(now.AddMinutes(-Delay));
            if (!Quiet || occurrence == 0 || occurrence == Released) return 0;
            long end = new DateTime(occurrence).AddMinutes(Delay + Duration).Ticks;
            return end > now.Ticks ? end : 0;
        }
    }

    public class GroupDef { public string Name; public List<string> Chats = new List<string>(); }
    public sealed class TaskGroupDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public TaskGroupDef() { Id = ""; Name = "默认"; }
    }
    public sealed class State
    {
        public List<Reminder> Reminders = new List<Reminder>();
        public List<TaskGroupDef> TaskGroups = new List<TaskGroupDef>();
        public List<GroupDef> Groups = new List<GroupDef>();
        public List<string> Keywords = new List<string>{"@我","紧急"};
        public string Theme="system";
        public bool MigrationOffered;
        public Dictionary<string,int> Levels = new Dictionary<string,int>();
        public int DefaultLevel = 2;
        public long TemporaryUntil;
        public List<string> MutedChats = new List<string>();
        public Dictionary<string, long> ChatQuietUntil = new Dictionary<string, long>();
        public List<string> ChatQuietPending = new List<string>();
        public bool AutoReturnToTop = true;
        public bool AutoStart;
        public int ArchiveRetentionDays = 30;
        public List<string> CollapsedChatGroups = new List<string>();
        public List<string> CollapsedTaskGroups = new List<string>();

        internal int CleanTrash(DateTime now)
        {
            return Reminders.RemoveAll(r => r.Status == "deleted" && r.DeletedAt > 0 && r.DeletedAt <= now.AddDays(-30).Ticks);
        }

        public long QuietEnd(DateTime now)
        {
            return Math.Max(TemporaryUntil > now.Ticks ? TemporaryUntil : 0,
                Reminders.Select(r => r.QuietEnd(now)).DefaultIfEmpty().Max());
        }

        public void Release(DateTime now)
        {
            TemporaryUntil = 0;
            foreach (var r in Reminders.Where(r => r.QuietEnd(now) > 0))
                r.Released = r.Latest(now.AddMinutes(-r.Delay));
        }

        public static long QuietUntilForMinutes(DateTime now, int minutes)
        {
            if (minutes <= 0) return now.Date.AddDays(1).Ticks;
            return now.AddMinutes(minutes).Ticks;
        }

        public long ChatQuietEnd(string title, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(title) || ChatQuietUntil == null) return 0;
            long until;
            if (ChatQuietUntil.TryGetValue(title, out until) && until > now.Ticks) return until;
            return 0;
        }

        public bool IsChatMuted(string title, DateTime now)
        {
            return QuietEnd(now) > 0 || ChatQuietEnd(title, now) > 0;
        }

        public void SetChatQuiet(string title, long untilTicks)
        {
            if (String.IsNullOrWhiteSpace(title)) return;
            if (ChatQuietUntil == null) ChatQuietUntil = new Dictionary<string, long>();
            if (untilTicks <= 0)
            {
                ReleaseChatQuiet(title);
                return;
            }
            ChatQuietUntil[title] = untilTicks;
        }

        public bool ReleaseChatQuiet(string title)
        {
            if (String.IsNullOrWhiteSpace(title)) return false;
            bool removed = false;
            if (ChatQuietUntil != null && ChatQuietUntil.Remove(title)) removed = true;
            if (ChatQuietPending != null && ChatQuietPending.RemoveAll(t => t == title) > 0) removed = true;
            return removed;
        }

        public bool ReleaseAllChatQuiets()
        {
            bool had = (ChatQuietUntil != null && ChatQuietUntil.Count > 0) ||
                       (ChatQuietPending != null && ChatQuietPending.Count > 0);
            if (ChatQuietUntil != null) ChatQuietUntil.Clear();
            if (ChatQuietPending != null) ChatQuietPending.Clear();
            return had;
        }

        public List<string> ActiveQuietChats(DateTime now)
        {
            if (ChatQuietUntil == null || ChatQuietUntil.Count == 0) return new List<string>();
            long nowTicks = now.Ticks;
            return ChatQuietUntil
                .Where(kv => !String.IsNullOrWhiteSpace(kv.Key) && kv.Value > nowTicks)
                .OrderBy(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .ToList();
        }

        public List<string> ExpireChatQuiets(DateTime now, out List<string> withMessages)
        {
            withMessages = new List<string>();
            if (ChatQuietUntil == null) ChatQuietUntil = new Dictionary<string, long>();
            if (ChatQuietPending == null) ChatQuietPending = new List<string>();
            long nowTicks = now.Ticks;
            var expired = ChatQuietUntil
                .Where(kv => String.IsNullOrWhiteSpace(kv.Key) || kv.Value <= nowTicks)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var title in expired)
            {
                ChatQuietUntil.Remove(title);
                if (!String.IsNullOrWhiteSpace(title) && ChatQuietPending.RemoveAll(t => t == title) > 0)
                    withMessages.Add(title);
            }
            ChatQuietPending.RemoveAll(t => String.IsNullOrWhiteSpace(t) || !ChatQuietUntil.ContainsKey(t));
            return expired.Where(t => !String.IsNullOrWhiteSpace(t)).ToList();
        }

        public int ChatLevel(WxWatch.ChatEvent e)
        {
            int level;
            if (Levels.TryGetValue(e.Title, out level)) return level;
            return e.Level == WxWatch.Level.Strong ? 3 : DefaultLevel;
        }
    }

    public sealed class Store
    {
        readonly string file;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public Store(string directory) { file = Path.Combine(directory, "state.json"); }
        public State Load() { return Load(DateTime.Now); }
        internal State Load(DateTime now)
        {
            if (!File.Exists(file)) return new State();
            var raw=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
            var old=raw.ContainsKey("Groups") ? raw["Groups"] as Dictionary<string,object> : null;
            if(old!=null) raw["Groups"]=old.GroupBy(x=>(string)x.Value).Select(g=>new GroupDef{Name=g.Key,Chats=g.Select(x=>x.Key).ToList()}).ToArray();
            var state=json.ConvertToType<State>(raw);
            if (state == null || state.Reminders == null || state.Groups == null || state.Levels == null || state.MutedChats == null)
                throw new InvalidDataException("无法读取日程文件 " + file);
            if (state.TaskGroups == null) state.TaskGroups = new List<TaskGroupDef>();
            if (state.CollapsedChatGroups == null) state.CollapsedChatGroups = new List<string>();
            if (state.CollapsedTaskGroups == null) state.CollapsedTaskGroups = new List<string>();
            if (state.ChatQuietUntil == null) state.ChatQuietUntil = new Dictionary<string, long>();
            else
            {
                foreach (var bad in state.ChatQuietUntil.Where(kv => String.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0).Select(kv => kv.Key).ToList())
                    state.ChatQuietUntil.Remove(bad);
            }
            if (state.ChatQuietPending == null) state.ChatQuietPending = new List<string>();
            state.Groups.RemoveAll(g => g == null || String.IsNullOrWhiteSpace(g.Name) || g.Name == "新消息" || g.Name == "未分组" || g.Name == "已收录 · 待归组");
            foreach (var r in state.Reminders)
            {
                if (String.IsNullOrEmpty(r.Status)) r.Status = r.Done ? "completed" : "active";
                if (String.IsNullOrEmpty(r.Kind)) r.Kind = "reminder";
                if (r.TaskState == null) r.TaskState = "";
                r.Done = r.Status != "active";
            }
            if (state.CleanTrash(now) != 0) Save(state);
            return state;
        }
        public void Save(State state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".new";
            File.WriteAllText(temp, json.Serialize(state));
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak");
            else File.Move(temp, file);
        }
    }

    public static class Capture
    {
        static readonly Regex Clock = new Regex(@"^(?<tomorrow>明天\s*)?(?<h>\d{1,2})[:：](?<m>\d{2})\s+");
        static readonly Regex Later = new Regex(@"^(?<n>\d{1,4})\s*(?:分钟后|m后)\s*");
        static readonly Regex Quiet = new Regex(@"(?:^|\s)#避免(?:(\d{1,3})(h|m|小时|分钟))?(?=\s|$)", RegexOptions.IgnoreCase);
        static readonly Regex Task = new Regex(@"(?:^|\s)#任务(?=\s|$)");
        public static Reminder Parse(string input, DateTime now)
        {
            bool task = Task.IsMatch(input);
            string text = Task.Replace(input, " ").Trim();
            DateTime at = now.AddMinutes(10);
            var m = Clock.Match(text);
            if (m.Success)
            {
                int h = int.Parse(m.Groups["h"].Value), minute = int.Parse(m.Groups["m"].Value);
                if (h > 23 || minute > 59) throw new FormatException("请输入有效的时分");
                at = now.Date.AddHours(h).AddMinutes(minute);
                if (m.Groups["tomorrow"].Success || at <= now) at = at.AddDays(1);
                text = text.Substring(m.Length);
            }
            else
            {
                m = Later.Match(text);
                if (m.Success)
                {
                    at = now.AddMinutes(int.Parse(m.Groups["n"].Value));
                    text = text.Substring(m.Length);
                }
            }
            var r = new Reminder { Kind = task ? "task" : "reminder", Status = "active",
                Start = task && !m.Success ? 0 : at.Ticks };
            m = Quiet.Match(text);
            if (m.Success)
            {
                r.Quiet = true;
                if (m.Groups[1].Success)
                {
                    bool hours = m.Groups[2].Value.Equals("h", StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value == "小时";
                    r.Duration = int.Parse(m.Groups[1].Value) * (hours ? 60 : 1);
                }
                text = Quiet.Replace(text, " ");
            }
            foreach (var token in new[] {"@强","@弱","@每周","@每天"})
            {
                string pattern = @"(?:^|\s)" + token + @"(?=\s|$)";
                if (!Regex.IsMatch(text, pattern)) continue;
                if (token == "@强") r.Level = 3;
                if (token == "@每周") r.RepeatDays = 7;
                if (token == "@每天") r.RepeatDays = 1;
                text = Regex.Replace(text, pattern, " ");
            }
            r.Title = text.Trim();
            if (task)
            {
                if (r.Quiet || r.RepeatDays != 0) throw new FormatException("周期和免打扰请通过提醒副本设置");
                r.ConfirmRequired = true;
            }
            Validate(r);
            return r;
        }
        public static void Validate(Reminder r)
        {
            if (String.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 80) throw new FormatException("标题需为 1–80 字");
            if (r.Duration < 1 || r.Duration > 1440 || r.Delay < 0 || r.Delay > 1440)
                throw new FormatException("时长应为 1–1440 分钟，缓冲应为 0–1440 分钟");
        }
    }
}

