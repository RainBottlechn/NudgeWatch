using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NudgeWatch
{
    public sealed class KnownChat
    {
        public string Title, Alias = "", LastGroup = "";
        public long FirstSeenAt, LastSeenAt, SeenCount;
    }

    internal sealed class KnownChats
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        readonly string path;
        readonly Dictionary<string, KnownChat> chats = new Dictionary<string, KnownChat>(StringComparer.Ordinal);
        DateTime nextSave;
        bool pending, unreadable;
        public string Error { get; private set; }
        public IEnumerable<KnownChat> All { get { return chats.Values; } }

        public KnownChats(string directory)
        {
            path = Path.Combine(directory, "known_chats.json");
            try
            {
                if (File.Exists(path))
                    foreach (var entry in json.Deserialize<List<KnownChat>>(File.ReadAllText(path)))
                        if (entry != null && !String.IsNullOrWhiteSpace(entry.Title)) chats[entry.Title] = entry;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                unreadable = true;
                Error = "会话库读取失败";
            }
        }

        public KnownChat Find(string title)
        {
            KnownChat entry;
            return chats.TryGetValue(title, out entry) ? entry : null;
        }

        public bool Observe(IEnumerable<string> titles, DateTime now, Func<string, string> group)
        {
            bool added = false;
            foreach (var title in titles.Where(t => !String.IsNullOrWhiteSpace(t)).Distinct())
            {
                var entry = Find(title);
                if (entry == null)
                {
                    entry = new KnownChat { Title = title, FirstSeenAt = now.Ticks };
                    chats.Add(title, entry);
                    added = true;
                }
                if (entry.FirstSeenAt == 0) entry.FirstSeenAt = now.Ticks;
                entry.LastSeenAt = Math.Max(entry.LastSeenAt, now.Ticks);
                if (entry.SeenCount < Int64.MaxValue) entry.SeenCount++;
                entry.LastGroup = group(title);
                pending = true;
            }
            if (added || now >= nextSave) Flush(now);
            return added;
        }

        public void Annotate(string title, string alias, string group, DateTime now, bool flush = true)
        {
            var entry = Find(title);
            if (entry == null)
            {
                entry = new KnownChat { Title = title };
                chats.Add(title, entry);
            }
            if (alias != null) entry.Alias = alias;
            entry.LastGroup = group;
            pending = true;
            if (flush) Flush(now);
        }

        public void Flush(DateTime now)
        {
            if (!pending || unreadable) return;
            nextSave = now.AddSeconds(60);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".new";
                File.WriteAllText(temp, json.Serialize(chats.Values.ToArray()));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                pending = false;
                Error = null;
            }
            catch (IOException) { Error = "会话库写入失败"; }
            catch (UnauthorizedAccessException) { Error = "会话库无法写入"; }
        }

        static readonly Encoding Gb2312 = Encoding.GetEncoding(936, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        static readonly int[] InitialStarts = { 45217, 45253, 45761, 46318, 46826, 47010, 47297, 47614, 48119, 49062, 49324,
            49896, 50371, 50614, 50622, 50906, 51387, 51446, 52218, 52698, 52980, 53689, 54481 };
        const string Initials = "ABCDEFGHJKLMNOPQRSTWXYZ";

        internal static string PinyinInitials(string text)
        {
            var result = new StringBuilder();
            foreach (char ch in text)
            {
                if (ch < 128) { if (Char.IsLetterOrDigit(ch)) result.Append(ch); continue; }
                byte[] bytes = Gb2312.GetBytes(new[] { ch });
                if (bytes.Length != 2 || bytes[1] < 161) continue;
                int code = bytes[0] * 256 + bytes[1];
                if (code < InitialStarts[0] || code > 55289) continue;
                int index = Array.BinarySearch(InitialStarts, code);
                result.Append(Initials[index < 0 ? ~index - 1 : index]);
            }
            return result.ToString();
        }
    }
}
