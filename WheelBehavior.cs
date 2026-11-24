using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
namespace NudgeWatch {
    static class WheelBehavior {
        static void Bind(UIElement control,Action<int,double> change) {
            var watch=new Stopwatch();
            control.PreviewMouseWheel+=(s,e)=>{e.Handled=true;
                if(watch.IsRunning && watch.ElapsedMilliseconds<90)return;
                watch.Restart(); change(Math.Sign(e.Delta),e.GetPosition(control).X);};
        }
        public static void Attach(DatePicker day,TextBox clock,TextBox end,TextBox delay) {
            Bind(day,(n,x)=>day.SelectedDate=(day.SelectedDate??DateTime.Today).AddDays(n));
            foreach(var box in new[]{clock,end}) Bind(box,(n,x)=>{
                TimeSpan t;
                if(TimeSpan.TryParse(box.Text,out t)) box.Text=DateTime.Today.Add(t).AddMinutes(n*(x<box.ActualWidth/2?60:1)).ToString("HH:mm");});
            Bind(delay,(n,x)=>{int value;if(int.TryParse(delay.Text,out value))delay.Text=Math.Max(0,Math.Min(1440,value+n)).ToString();});
        }
    }
}
