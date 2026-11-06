using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace IslandBar;
sealed class AppSetEditor:Window
{
    readonly TextBox _name=new(){Foreground=Brushes.White,Background=Brushes.Transparent,BorderThickness=new Thickness(0),
        FontSize=16,CaretBrush=PanelControls.Silver,SelectionBrush=PanelControls.Silver,SelectionOpacity=.35,Padding=new Thickness(0,8,0,8)};
    readonly StackPanel _list=new();
    readonly List<(CheckBox Check,StoredApp App)> _choices=[];
    readonly TextBlock _error=new(){Foreground=Brushes.Orange,FontSize=11,TextWrapping=TextWrapping.Wrap};
    public AppSetEditor(AppSet? existing,IEnumerable<StoredApp> available,Action<AppSet> save,bool inspect)
    {
        Title="IslandBar — набор приложений";Width=470;Height=510;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=inspect;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Segoe UI");
        var root=new DockPanel();var frame=PanelControls.Frame(root,new Thickness(22));Content=frame;
        var header=new StackPanel();
        header.Children.Add(PanelControls.Eyebrow("НАБОРЫ ПРИЛОЖЕНИЙ"));
        header.Children.Add(new TextBlock{Text=existing is null?"Новый набор":"Редактировать набор",Foreground=Brushes.White,FontSize=23,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,2,0,11)});
        _name.Text=existing?.Name??"Новый набор";
        System.Windows.Automation.AutomationProperties.SetName(_name,"Название набора");
        header.Children.Add(PanelControls.Well(_name,new Thickness(10,0,10,0)));
        header.Children.Add(new TextBlock{Text="Выбери программы для запуска",Foreground=PanelControls.Secondary,FontSize=12,Margin=new Thickness(0,12,0,8)});
        DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new StackPanel();footer.Children.Add(_error);var add=Button("Добавить программу…");add.Click+=(_,_)=>{var dialog=new OpenFileDialog{Title="Добавить программу",Filter="Программы и ярлыки|*.exe;*.lnk|Все файлы|*.*"};if(dialog.ShowDialog(this)==true){try{var app=StoredApp.FromFile(dialog.FileName);var existingChoice=_choices.FirstOrDefault(c=>c.App.Key.Equals(app.Key,StringComparison.OrdinalIgnoreCase));if(existingChoice.Check is not null)existingChoice.Check.IsChecked=true;else AddChoice(app,true);}catch{_error.Text="Не удалось прочитать программу или ярлык";}}};footer.Children.Add(add);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
        var cancel=Button("Отмена");cancel.Background=Brushes.Transparent;cancel.Margin=new Thickness(0,3,8,0);cancel.Click+=(_,_)=>Close();
        var accept=Button("Сохранить");
        accept.Click+=(_,_)=>{
            string name=_name.Text.Trim();
            var apps=_choices.Where(c=>c.Check.IsChecked==true).Select(c=>c.App).DistinctBy(a=>a.Key).ToList();
            if(name.Length==0||apps.Count==0){_error.Text="Укажи название и выбери хотя бы одну программу";return;}
            save(new(name,apps));Close();
        };
        buttons.Children.Add(cancel);buttons.Children.Add(accept);footer.Children.Add(buttons);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var appList=PanelControls.Scroller(_list,224);appList.VerticalAlignment=VerticalAlignment.Top;root.Children.Add(appList);
        var selected=existing?.Apps.Select(a=>a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)??[];
        foreach(var app in (existing?.Apps??[]).Concat(available).DistinctBy(a=>a.Key,StringComparer.OrdinalIgnoreCase))AddChoice(app,selected.Contains(app.Key));
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        Loaded+=(_,_)=>{Activate();_name.Focus();_name.SelectAll();ChromeMotion.Enter(frame,anchored:false);ChromeMotion.Reveal(header);ChromeMotion.Reveal(_list,2);ChromeMotion.Reveal(footer,3);};
    }
    void AddChoice(StoredApp app,bool selected){var text=new TextBlock{Text=app.Name,Foreground=Brushes.White,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};var check=new CheckBox{Content=text,IsChecked=selected,Template=PanelControls.CheckBox,Margin=new Thickness(0,6,0,6)};System.Windows.Automation.AutomationProperties.SetName(check,app.Name);_choices.Add((check,app));_list.Children.Add(check);}
    static Button Button(string text)
    {
        var button=PanelControls.Button(text,text,36,true);
        PanelControls.FinishTile(button);
        button.Margin=new Thickness(0,3,0,0);
        return button;
    }
}
