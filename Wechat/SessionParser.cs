using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WxWatch
{
    /// <summary>一条会话列表项（mmui::ChatSessionCell）的解析结果。</summary>
    public class SessionInfo
    {
        public string Raw = "";
        public string Title = "";
        public int Unread = -1;          // -1 = 未找到未读标记
        public string Preview = "";
        public string Time = "";
        public bool Muted;
        public bool IsPinned;
        public bool IsDraft;
        public System.Windows.Rect Rect = System.Windows.Rect.Empty;
        public int Index;

        public override string ToString()
        {
            return string.Format("[{0}] 标题={1} | 未读={2} | 时间={3} | 置顶={4} | 免打扰={5} | 摘要={6}",
                Index, Title, Unread, Time, IsPinned, Muted, Preview);
        }
    }

    /// <summary>
    /// 启发式解析器。注意：Name 的真实格式需要用 dump 命令确认后再固化，
    /// 所有正则都可以通过构造参数覆盖。
    /// </summary>
    public class SessionParser
    {
        public const string DefaultTimeRegex =
            @"^(\d{1,2}:\d{2}|昨天( \d{1,2}:\d{2})?|前天|星期[一二三四五六日天]|周[一二三四五六日天]|\d{1,2}/\d{1,2}|\d{2,4}/\d{1,2}/\d{1,2}|\d{1,2}月\d{1,2}日|\d{4}年\d{1,2}月\d{1,2}日)$";

        static readonly Regex BadgeRx = new Regex(@"^(\d{1,3})\+?$");
        static readonly Regex UnreadWordsRx = new Regex(@"^(\d+)\s*条(新消息|未读消息|未读)$");
        static readonly Regex PrefixRx = new Regex(@"^\[(\d+)条\]\s*");

        readonly Regex timeRx;
        readonly Regex customUnreadRx;

        public SessionParser() : this(null, null) { }

        public SessionParser(string timeRegex, string unreadRegex)
        {
            timeRx = new Regex(string.IsNullOrEmpty(timeRegex) ? DefaultTimeRegex : timeRegex);
            customUnreadRx = string.IsNullOrEmpty(unreadRegex) ? null : new Regex(unreadRegex);
        }

        public SessionInfo Parse(string raw)
        {
            var s = new SessionInfo();
            s.Raw = raw ?? "";
            var lines = new List<string>();
            foreach (var p in s.Raw.Replace("\r", "").Split('\n'))
            {
                var t = p.Trim();
                if (t.Length > 0) lines.Add(t);
            }
            if (lines.Count == 0) return s;
            s.Title = lines[0];

            if (customUnreadRx != null)
            {
                var m = customUnreadRx.Match(s.Raw);
                if (m.Success && m.Groups.Count > 1) s.Unread = int.Parse(m.Groups[1].Value);
            }

            var rest = new List<string>();
            for (int i = 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (s.Time.Length == 0 && timeRx.IsMatch(line)) { s.Time = line; continue; }

                var m = BadgeRx.Match(line);
                if (m.Success)
                {
                    // WeChat 4.1.8 places the latest message immediately before its time.
                    // A numeric message occupies that slot even when the row also has a badge.
                    if(i+1<lines.Count && timeRx.IsMatch(lines[i+1])) { rest.Add(line); continue; }
                    if (s.Unread < 0) s.Unread = int.Parse(m.Groups[1].Value);
                    continue;
                }

                m = UnreadWordsRx.Match(line);
                if (m.Success) { if (s.Unread < 0) s.Unread = int.Parse(m.Groups[1].Value); continue; }

                if (line == "已置顶" || line == "置顶") { s.IsPinned = true; continue; }

                if (line.Length <= 8 && line.Contains("免打扰")) { s.Muted = true; continue; }

                m = PrefixRx.Match(line);
                if (m.Success)
                {
                    if (s.Unread < 0) s.Unread = int.Parse(m.Groups[1].Value);
                    line = line.Substring(m.Length);
                }
                if (line.StartsWith("[草稿]")) s.IsDraft = true;
                rest.Add(line);
            }
            s.Preview = string.Join(" ", rest);
            return s;
        }
    }
}
