using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WxWatch
{
    public class GroupConfig
    {
        public string name = "";
        public List<string> chats = new List<string>();
    }

    public class RuleConfig
    {
        public List<string> strong = new List<string>();
        public List<string> normal = new List<string>();
        public List<string> ignore = new List<string>();
        public List<string> strongKeywords = new List<string> { "[有人@我]", "@我", "紧急" };
        public string defaultLevel = "Normal";
    }

    public class AppConfig
    {
        public int pollIntervalMs = 1200;
        public int searchDelayMs = 350;
        public string hotkey = "Alt+W";
        public bool autoMoveOffscreen = false;
        public string timeRegex = "";
        public string unreadRegex = "";
        public RuleConfig rules = new RuleConfig();
        public List<GroupConfig> groups = new List<GroupConfig>();

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wxwatch.config.json"); }
        }

        public static AppConfig CreateSample()
        {
            var c = new AppConfig();
            c.rules.strong.Add("老板");
            c.rules.strong.Add("*核心*");
            c.rules.ignore.Add("*广告*");
            c.groups.Add(new GroupConfig
            {
                name = "核心项目群",
                chats = new List<string> { "文件传输助手" }
            });
            c.groups.Add(new GroupConfig
            {
                name = "重要联系人",
                chats = new List<string> { "文件传输助手" }
            });
            return c;
        }

        public static AppConfig LoadOrCreate(string path)
        {
            if (string.IsNullOrEmpty(path)) path = DefaultPath;
            var ser = new JavaScriptSerializer();
            if (!File.Exists(path))
            {
                var sample = CreateSample();
                Save(path, sample);
                return sample;
            }
            string json = File.ReadAllText(path, Encoding.UTF8);
            var cfg = ser.Deserialize<AppConfig>(json) ?? CreateSample();
            if (cfg.rules == null) cfg.rules = new RuleConfig();
            if (cfg.rules.strong == null) cfg.rules.strong = new List<string>();
            if (cfg.rules.normal == null) cfg.rules.normal = new List<string>();
            if (cfg.rules.ignore == null) cfg.rules.ignore = new List<string>();
            if (cfg.rules.strongKeywords == null) cfg.rules.strongKeywords = new List<string>();
            if (cfg.groups == null) cfg.groups = new List<GroupConfig>();
            if (cfg.pollIntervalMs < 400) cfg.pollIntervalMs = 400;
            if (cfg.searchDelayMs < 150) cfg.searchDelayMs = 150;
            return cfg;
        }

        public static void Save(string path, AppConfig cfg)
        {
            var ser = new JavaScriptSerializer();
            string raw = ser.Serialize(cfg);
            File.WriteAllText(path, FormatJson(raw), Encoding.UTF8);
        }

        public Rules ToRules()
        {
            var r = new Rules();
            r.Strong.AddRange(rules.strong);
            r.Normal.AddRange(rules.normal);
            r.Ignore.AddRange(rules.ignore);
            r.StrongKeywords.Clear();
            r.StrongKeywords.AddRange(rules.strongKeywords);
            Level lv;
            if (Enum.TryParse(rules.defaultLevel, true, out lv)) r.Default = lv;
            return r;
        }

        static string FormatJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (!inStr)
                {
                    if (ch == '{' || ch == '[')
                    {
                        sb.Append(ch).AppendLine().Append(' ', ++indent * 2);
                        continue;
                    }
                    if (ch == '}' || ch == ']')
                    {
                        sb.AppendLine().Append(' ', --indent * 2).Append(ch);
                        continue;
                    }
                    if (ch == ',')
                    {
                        sb.Append(ch).AppendLine().Append(' ', indent * 2);
                        continue;
                    }
                    if (ch == ':') { sb.Append(": "); continue; }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
