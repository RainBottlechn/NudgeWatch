using System;
using System.Globalization;
using System.Linq;
namespace NudgeWatch
{
    public class RecurrenceRule
    {
        public string Kind = "once";
        public string[] Values = new string[0];
        public static readonly string[] Kinds = {"once","daily","weekly","workdays","monthly","yearly","dates"};
        public static RecurrenceRule Parse(string kind,string text,DateTime start)
        {
            var rule = new RecurrenceRule { Kind = kind };
            if (!new[]{"weekly","monthly","yearly","dates"}.Contains(kind)) return rule;
            var values = text.Split(new[]{',','，',' ','；',';'},StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0) values = new[]{Key(kind,start)};
            rule.Values = values.Select(v => {
                int n; DateTime date;
                if (kind=="weekly" || kind=="monthly")
                {
                    if (!int.TryParse(v,out n) || n<1 || n>(kind=="weekly"?7:31)) throw new FormatException("请输入范围内的日期数字");
                    return n.ToString();
                }
                if (!DateTime.TryParseExact(kind=="yearly"?"2000-"+v:v,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date))
                    throw new FormatException("日期格式应为 MM-dd 或 yyyy-MM-dd");
                return Key(kind,date);
            }).Distinct().ToArray();
            return rule;
        }
        static string Key(string kind,DateTime d)
        {
            switch(kind) {
                case "weekly": return (((int)d.DayOfWeek+6)%7+1).ToString();
                case "monthly": return d.Day.ToString();
                case "yearly": return d.ToString("MM-dd",CultureInfo.InvariantCulture);
                default: return d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            }
        }
        public long Find(long start, DateTime when, int direction)
        {
            var anchor = new DateTime(start);
            if (direction < 0 && when < anchor) return 0;
            if (Kind=="once") return start;
            var day = when < anchor ? anchor : when.Date.Add(anchor.TimeOfDay);
            if ((direction>0 && day<when) || (direction<0 && day>when)) day=day.AddDays(direction);
            if (Kind=="dates") {
                var dates=Values.Select(v=>DateTime.ParseExact(v,"yyyy-MM-dd",CultureInfo.InvariantCulture).Add(anchor.TimeOfDay))
                    .Where(d=>d>=anchor && (direction>0?d>=day:d<=day)).Select(d=>d.Ticks);
                return direction>0?dates.DefaultIfEmpty().Min():dates.DefaultIfEmpty().Max();
            }
            for(int i=0;i<366*8 && day>=anchor;i++,day=day.AddDays(direction))
                if (Kind=="daily" || (Kind=="workdays" ? day.DayOfWeek!=DayOfWeek.Saturday && day.DayOfWeek!=DayOfWeek.Sunday : Values.Contains(Key(Kind,day)))) return day.Ticks;
            return 0;
        }
    }
}
