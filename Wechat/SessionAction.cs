using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace WxWatch
{
    // UIA rectangles use physical screen pixels, including on mixed-DPI monitors.
    public static class SessionAction
    {
        public static bool ConfirmRead(WeChatUia uia,string title)
        {
            var rows=uia.ReadSessions(new SessionParser(),new WatchStatus());
            if(rows==null || rows.Count(r=>r.Title==title)!=1)return false;
            var row=rows.First(r=>r.Title==title);
            if(row.Unread<=0)return true;
            if(row.Rect.IsEmpty || !Navigator.ForceForeground(uia.MainHwnd))return false;
            var point=new Point(row.Rect.Left+row.Rect.Width/2,row.Rect.Top+row.Rect.Height/2);
            if(!HitSession(point,title))return false;
            Native.ClickPhysical((int)point.X,(int)point.Y,true);
            var pids=WeChatUia.Pids();
            var condition=new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"标为已读"),
                new PropertyCondition(AutomationElement.NameProperty,"标记为已读"));
            for(int i=0;i<12;i++) {
                foreach(var window in Native.TopWindowsOf(pids,false)){
                    AutomationElement command;
                    try{command=AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,condition);}
                    catch(ElementNotAvailableException){continue;}
                    if(command==null || command.Current.IsOffscreen)continue;
                    var rect=command.Current.BoundingRectangle;
                    if(rect.IsEmpty)continue;
                    var center=new Point(rect.Left+rect.Width/2,rect.Top+rect.Height/2);
                    var hit=AutomationElement.FromPoint(center);
                    if(hit==null || !pids.Contains((uint)hit.Current.ProcessId) || hit.Current.Name!=command.Current.Name)continue;
                    Native.ClickPhysical((int)center.X,(int)center.Y,false);
                    for(int j=0;j<15;j++){
                        Thread.Sleep(120);
                        var sample=uia.ReadSessions(new SessionParser(),new WatchStatus());
                        if(sample!=null && sample.Exists(r=>r.Title==title && r.Unread<=0))return true;
                    }
                    return false;
                }
                Thread.Sleep(100);
            }
            return false;
        }
        public static bool HitSession(Point point, string title)
        {
            var parser = new SessionParser();
            var node = AutomationElement.FromPoint(point);
            for (int i = 0; node != null && i < 12; i++, node = TreeWalker.RawViewWalker.GetParent(node))
                if (node.Current.ClassName == WeChatUia.CellClass)
                    return WeChatUia.Pids().Contains((uint)node.Current.ProcessId) && parser.Parse(node.Current.Name).Title == title;
            return false;
        }

    }
}
