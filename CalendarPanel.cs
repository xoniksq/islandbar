using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IslandBar;

sealed partial class IslandWindow
{
    static readonly CultureInfo RussianCalendar=CultureInfo.GetCultureInfo("ru-RU");
    static readonly Brush TodayFill=PanelControls.Chrome;
    readonly Queue<IslandEvent> _islandEvents=new();
    sealed record IslandEvent(DateTime Time,string Text,Action Open);
    DateTime _calendarMonth,_calendarDay;
    UniformGrid? _calendarDays;
    TextBlock? _calendarCaption,_calendarEventHeading;
    StackPanel? _calendarEvents;

    void RememberIslandEvent(string text,Action open)
    {
        _islandEvents.Enqueue(new(DateTime.Now,text,open));
        while(_islandEvents.Count>40)_islandEvents.Dequeue();
        if(_featureKind=="calendar")RenderCalendarEvents();
    }

    void OpenCalendar()
    {
        if(_featureKind=="calendar"&&_featurePopup.IsOpen){CloseFeature();return;}
        _calendarDay=DateTime.Today;
        _calendarMonth=new(_calendarDay.Year,_calendarDay.Month,1);
        var column=CardColumn();
        column.Children.Add(CardTitle("Календарь"));
        var navigation=new DockPanel {Margin=new Thickness(0,0,0,4)};
        var next=PanelControls.Button("›","Следующий месяц",30,true);next.Width=34;DockPanel.SetDock(next,Dock.Right);
        var previous=PanelControls.Button("‹","Предыдущий месяц",30,true);previous.Width=34;DockPanel.SetDock(previous,Dock.Right);
        next.Click+=(_,_)=>MoveMonth(1);previous.Click+=(_,_)=>MoveMonth(-1);
        navigation.Children.Add(next);navigation.Children.Add(previous);
        var today=PanelControls.Button("Сегодня","Вернуться к сегодняшней дате",30,true);
        DockPanel.SetDock(today,Dock.Right);navigation.Children.Add(today);
        today.Click+=(_,_)=>{
            _calendarDay=DateTime.Today;
            _calendarMonth=new(_calendarDay.Year,_calendarDay.Month,1);
            RenderCalendar();
            ChromeMotion.Reveal(_calendarDays!);
        };
        _calendarCaption=LeftLabel("",14);_calendarCaption.FontWeight=FontWeights.SemiBold;
        navigation.Children.Add(_calendarCaption);column.Children.Add(navigation);
        var weekdays=new UniformGrid {Columns=7};
        foreach(string day in new[]{"Пн","Вт","Ср","Чт","Пт","Сб","Вс"}){
            var label=Label(day,11);label.Height=25;label.Foreground=Secondary;
            weekdays.Children.Add(label);
        }
        column.Children.Add(weekdays);
        _calendarDays=new UniformGrid {Columns=7,Rows=6};column.Children.Add(_calendarDays);
        column.Children.Add(new Border {Height=1,Background=PanelControls.Outline,Margin=new Thickness(0,10,0,10)});
        _calendarEventHeading=LeftLabel("",12);_calendarEventHeading.Margin=new Thickness(0,0,0,4);
        column.Children.Add(_calendarEventHeading);
        _calendarEvents=new StackPanel();column.Children.Add(PanelControls.Scroller(_calendarEvents,96));
        RenderCalendar();
        OpenCard("calendar",column);
    }

    void MoveMonth(int delta)
    {
        if((delta<0&&_calendarMonth==DateTime.MinValue)||(delta>0&&_calendarMonth.Year==9999&&_calendarMonth.Month==12))return;
        _calendarMonth=_calendarMonth.AddMonths(delta);
        _calendarDay=_calendarMonth;
        RenderCalendar();
        ChromeMotion.Reveal(_calendarDays!);
    }

    void RenderCalendar()
    {
        _calendarCaption!.Text=RussianCalendar.TextInfo.ToTitleCase(_calendarMonth.ToString("MMMM yyyy",RussianCalendar));
        _calendarDays!.Children.Clear();
        int firstColumn=((int)_calendarMonth.DayOfWeek+6)%7;
        int days=DateTime.DaysInMonth(_calendarMonth.Year,_calendarMonth.Month);
        for(int i=0;i<42;i++){
            int day=i-firstColumn+1;
            if(day<1||day>days){_calendarDays.Children.Add(new Border {Height=30});continue;}
            var date=_calendarMonth.AddDays(day-1);
            var label=Label(day.ToString(),12);
            if(date==DateTime.Today)label.FontWeight=FontWeights.Bold;
            var button=PanelControls.Button(label,date.ToString("D",RussianCalendar),30,true);
            button.Padding=new Thickness(0);button.Margin=new Thickness(1);
            if(date==_calendarDay)button.Background=TodayFill;
            else if(date==DateTime.Today)button.Background=PanelControls.Tile;
            button.Click+=(_,_)=>{_calendarDay=date;RenderCalendar();};
            _calendarDays.Children.Add(button);
        }
        RenderCalendarEvents();
    }

    void RenderCalendarEvents()
    {
        _calendarEventHeading!.Text="События островка · "+_calendarDay.ToString("d MMMM",RussianCalendar);
        _calendarEvents!.Children.Clear();
        var items=_islandEvents.Reverse().Where(item=>item.Time.Date==_calendarDay).ToArray();
        if(items.Length==0){
            _calendarEvents.Children.Add(LeftLabel("За этот день событий пока нет",11,Secondary));
            return;
        }
        foreach(var item in items){
            string text=item.Time.ToString("HH:mm")+"   "+item.Text;
            var button=PanelControls.Button(LeftLabel(text,11),text,30);
            button.Click+=(_,_)=>item.Open();
            _calendarEvents.Children.Add(button);
        }
    }
}
