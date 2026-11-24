using System;
using System.Collections.Generic;

namespace WxWatch
{
    public enum Level { Ignore = 0, Normal = 1, Strong = 2 }

    public class ChatEvent
    {
        public string Title, Preview, Reason;
        public int Unread;
        public Level Level;
        public bool WeChatActive;
        public DateTime At;
    }

    public class Detection
    {
        public readonly List<ChatEvent> Events = new List<ChatEvent>();
        public readonly List<string> Read = new List<string>();
    }

    /// <summary>原型与 M0 双模式规则：支持默认全强/全弱，再通过强提醒/非强提醒/忽略单独覆盖。</summary>
    public class Rules
    {
        public readonly List<string> Strong = new List<string>();
        public readonly List<string> Normal = new List<string>();
        public readonly List<string> Ignore = new List<string>();
        public readonly List<string> StrongKeywords = new List<string> { "[有人@我]", "@我" };
        public Level Default = Level.Normal;

        public static bool Match(string pattern, string text)
        {
            if (string.IsNullOrEmpty(pattern) || text == null) return false;
            if (pattern.StartsWith("re:"))
                return System.Text.RegularExpressions.Regex.IsMatch(text, pattern.Substring(3));
            if (pattern.Contains("*"))
            {
                string rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(text, rx);
            }
            return string.Equals(pattern.Trim(), text.Trim(), StringComparison.Ordinal);
        }

        public Level Resolve(string title, string preview, out string why)
        {
            foreach (var p in Ignore) if (Match(p, title)) { why = "忽略:" + p; return Level.Ignore; }
            foreach (var p in Strong) if (Match(p, title)) { why = "强提醒标注:" + p; return Level.Strong; }
            foreach (var p in Normal) if (Match(p, title)) { why = "非强提醒标注:" + p; return Level.Normal; }
            if (Default != Level.Ignore && preview != null)
                foreach (var k in StrongKeywords)
                    if (preview.Contains(k)) { why = "关键词:" + k; return Level.Strong; }
            why = Default == Level.Strong ? "默认(全强)" : (Default == Level.Normal ? "默认(全弱)" : "默认(忽略)");
            return Default;
        }
    }

    /// <summary>
    /// 新消息检测：以“未读数上升”为主信号，摘要变化为兜底信号（仅在未读格式不可用或免打扰时启用）。
    /// 纯逻辑类，不依赖 UIA，便于单元测试。
    /// </summary>
    public class ChangeDetector
    {
        class Prev { public int Unread; public string Preview; }

        readonly Dictionary<string, Prev> state = new Dictionary<string, Prev>();
        bool baselined;
        public bool BadgesSeen { get; private set; }

        public void ResetBaseline()
        {
            state.Clear();
            baselined = false;
        }

        public Detection Process(List<SessionInfo> list, bool weChatActive, Rules rules)
        {
            var d = new Detection();
            if (list == null || list.Count == 0) return d;
            foreach (var s in list) if (s.Unread > 0) BadgesSeen = true;

            if (!baselined)
            {
                foreach (var s in list)
                    if (s.Title.Length > 0 && !state.ContainsKey(s.Title))
                        state[s.Title] = new Prev { Unread = Math.Max(0, s.Unread), Preview = s.Preview };
                baselined = true;
                return d;
            }

            var seen = new HashSet<string>();
            foreach (var s in list)
            {
                if (s.Title.Length == 0 || !seen.Add(s.Title)) continue; // 重名会话只取第一条
                Prev p;
                bool had = state.TryGetValue(s.Title, out p);
                int cur = Math.Max(0, s.Unread);
                int old = had ? p.Unread : 0;
                string reason = null;

                if (cur > old)
                {
                    // 新进入可见区的会话：只有微信不在前台时才认为是新消息（前台时可能是用户在滚动列表）
                    if (had) reason = "未读 " + old + "→" + cur;
                    else if (!weChatActive) reason = "新出现的未读会话(" + cur + ")";
                }
                else if (cur == 0 && had && !weChatActive && (!BadgesSeen || s.Muted)
                         && s.Preview.Length > 0 && s.Preview != p.Preview && !s.Preview.StartsWith("[草稿]"))
                {
                    reason = "摘要变化";
                }

                if (had && old > 0 && cur == 0) d.Read.Add(s.Title);
                state[s.Title] = new Prev { Unread = cur, Preview = s.Preview };

                if (reason != null)
                {
                    string why;
                    var lv = rules.Resolve(s.Title, s.Preview, out why);
                    if (lv == Level.Ignore) continue;
                    d.Events.Add(new ChatEvent
                    {
                        Title = s.Title, Preview = s.Preview, Unread = cur, Level = lv,
                        Reason = reason + "; " + why, WeChatActive = weChatActive, At = DateTime.Now
                    });
                }
            }
            return d;
        }
    }
}
