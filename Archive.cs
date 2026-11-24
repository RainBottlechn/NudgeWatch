using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NudgeWatch
{
    public sealed class ArchiveEntry
    {
        public long ts;
        public string time, chat, group, preview, hash;
        public int unread, level;
        public bool muted;
    }

    internal sealed class MessageArchive
    {
        const string Prefix = "messages-";
        const string Extension = ".jsonl";
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        readonly string folder;
        readonly object gate = new object(), files = new object();
        readonly Queue<ArchiveEntry> pending = new Queue<ArchiveEntry>();
        readonly Dictionary<string, long> recent = new Dictionary<string, long>(StringComparer.Ordinal);
        Task writer = Task.FromResult(false);
        bool writing, sweepPending, seeded;
        DateTime sweepDay, retryAfter;
        int retention;
        string error;

        public MessageArchive(string directory) { folder = Path.Combine(directory, "archive"); }
        public string Error { get { lock (gate) return error; } }

        public void Maintain(DateTime now, int days)
        {
            lock (gate)
            {
                if (sweepDay != now.Date || retention != days)
                {
                    sweepDay = now.Date;
                    retention = days;
                    sweepPending = true;
                }
                Start(false);
            }
        }

        public void Add(IEnumerable<ArchiveEntry> entries)
        {
            lock (gate)
            {
                foreach (var entry in entries) pending.Enqueue(entry);
                Start(false);
            }
        }

        public void Flush()
        {
            Task wait;
            lock (gate) { Start(true); wait = writer; }
            wait.GetAwaiter().GetResult();
        }

        void Start(bool force)
        {
            if (writing || pending.Count == 0 && !sweepPending || !force && DateTime.UtcNow < retryAfter) return;
            writing = true;
            writer = Task.Run((Action)WriteLoop);
        }

        void WriteLoop()
        {
            var json = new JavaScriptSerializer();
            for (;;)
            {
                ArchiveEntry[] batch;
                DateTime day;
                int days;
                bool sweep;
                lock (gate)
                {
                    sweep = sweepPending; day = sweepDay; days = retention;
                    batch = pending.Take(128).ToArray();
                    if (!sweep && batch.Length == 0) { writing = false; return; }
                }
                bool appended = false;
                try
                {
                    lock (files)
                    {
                        Write(batch, json);
                        appended = true;
                        if (sweep) Clean(day, days);
                    }
                    lock (gate)
                    {
                        foreach (var entry in batch) pending.Dequeue();
                        if (sweep && day == sweepDay && days == retention) sweepPending = false;
                        error = null; retryAfter = DateTime.MinValue;
                    }
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        if (appended) foreach (var entry in batch) pending.Dequeue();
                        error = "消息归档写入失败（" + ex.GetType().Name + "）";
                        retryAfter = DateTime.UtcNow.AddSeconds(30);
                        writing = false;
                    }
                    return;
                }
            }
        }

        void Write(ArchiveEntry[] entries, JavaScriptSerializer json)
        {
            if (entries.Length == 0) return;
            long cutoff = entries[0].ts - 5 * TimeSpan.TicksPerMinute;
            if (!seeded)
            {
                var at = new DateTime(entries[0].ts);
                foreach (var old in ReadFiles(at.Date.AddDays(-1), at.Date, json).Where(e => e.ts >= cutoff && !String.IsNullOrEmpty(e.hash)).OrderBy(e => e.ts))
                    recent[old.hash] = old.ts;
                seeded = true;
            }
            foreach (var stale in recent.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray()) recent.Remove(stale);
            Directory.CreateDirectory(folder);
            using (var sha = SHA256.Create())
                foreach (var part in entries.GroupBy(e => new DateTime(e.ts).Date))
                {
                    var lines = new StringBuilder();
                    var saved = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var entry in part)
                    {
                        entry.hash = Convert.ToBase64String(sha.ComputeHash(Utf8.GetBytes(entry.chat.Length + ":" + entry.chat + entry.preview)));
                        long previous;
                        if ((saved.TryGetValue(entry.hash, out previous) || recent.TryGetValue(entry.hash, out previous)) &&
                            Math.Abs(entry.ts - previous) < 5 * TimeSpan.TicksPerMinute) continue;
                        lines.Append(json.Serialize(entry)).Append('\n');
                        saved[entry.hash] = entry.ts;
                    }
                    if (saved.Count == 0) continue;
                    var bytes = Utf8.GetBytes(lines.ToString());
                    using (var stream = new FileStream(DayFile(part.Key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                    {
                        long length = stream.Seek(0, SeekOrigin.End);
                        try { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
                        catch (IOException)
                        {
                            stream.SetLength(length);
                            throw;
                        }
                    }
                    foreach (var savedEntry in saved) recent[savedEntry.Key] = savedEntry.Value;
                }
        }

        string DayFile(DateTime day) { return Path.Combine(folder, Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension); }

        IEnumerable<string> Partitions(DateTime start, DateTime end)
        {
            if (!Directory.Exists(folder)) yield break;
            foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*" + Extension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                DateTime day;
                if (String.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase) && name.StartsWith(Prefix, StringComparison.Ordinal) &&
                    DateTime.TryParseExact(name.Substring(Prefix.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
                    day >= start.Date && day <= end.Date) yield return file;
            }
        }

        void Clean(DateTime day, int days)
        {
            if (days != 7 && days != 30 && days != 90) return;
            foreach (var file in Partitions(DateTime.MinValue, day.AddDays(-days))) File.Delete(file);
        }

        List<ArchiveEntry> ReadFiles(DateTime start, DateTime end, JavaScriptSerializer json)
        {
            var entries = new List<ArchiveEntry>();
            foreach (var file in Partitions(start, end))
                foreach (string line in File.ReadLines(file, Utf8))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        var entry = json.Deserialize<ArchiveEntry>(line);
                        if (entry != null && entry.ts > 0 && entry.ts <= DateTime.MaxValue.Ticks && !String.IsNullOrEmpty(entry.chat) &&
                            new DateTime(entry.ts).Date >= start.Date && new DateTime(entry.ts).Date <= end.Date) entries.Add(entry);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (FormatException) { }
                    catch (OverflowException) { }
                }
            return entries;
        }

        public List<ArchiveEntry> Read(DateTime start, DateTime end)
        {
            lock (files) return ReadFiles(start, end, new JavaScriptSerializer()).OrderBy(e => e.ts).ToList();
        }

        public static string Export(IEnumerable<ArchiveEntry> entries, int format, bool grouped)
        {
            if (format == 1)
            {
                var csv = new StringBuilder("时间,分组,会话,消息摘要,未读数,等级,静默\r\n");
                foreach (var e in entries)
                    csv.Append(String.Join(",", Csv(When(e)), Csv(e.group), Csv(e.chat), Csv(e.preview),
                        e.unread.ToString(CultureInfo.InvariantCulture), e.level.ToString(CultureInfo.InvariantCulture), e.muted ? "是" : "否")).Append("\r\n");
                return csv.ToString();
            }
            var text = new StringBuilder();
            if (format == 0 && grouped)
            {
                foreach (var group in entries.GroupBy(e => e.group).OrderBy(g => g.Key))
                {
                    text.Append("## ").Append(Markdown(group.Key)).AppendLine();
                    foreach (var chat in group.GroupBy(e => e.chat))
                    {
                        text.Append("### ").Append(Markdown(chat.Key)).AppendLine();
                        foreach (var e in chat) text.Append("- ").Append(When(e)).Append(" ").AppendLine(Markdown(e.preview));
                    }
                    text.AppendLine();
                }
            }
            else foreach (var e in entries)
            {
                if (format == 0) text.Append("- ").Append(When(e)).Append(" **").Append(Markdown(e.chat)).Append("**  ").AppendLine(Markdown(e.preview));
                else text.Append(When(e)).Append("  [").Append(e.chat).Append("] ").AppendLine(e.preview);
            }
            return text.ToString();
        }

        static string When(ArchiveEntry entry) { return new DateTime(entry.ts).ToString("yyyy-MM-dd HH:mm:ss"); }
        static string Markdown(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]")
                .Replace("`", "\\`").Replace("<", "&lt;").Replace("\r", "").Replace("\n", " ");
        }
        static string Csv(string text)
        {
            text = text ?? "";
            string trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0 || text.StartsWith("\t") || text.StartsWith("\r"))
                text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
