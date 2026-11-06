using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace IslandBar;

sealed partial class IslandWindow
{
    static readonly (DockModules Module,string Key,string Label)[] ModuleSettings=[
        (DockModules.Folders,"folders","Быстрые папки"),
        (DockModules.Downloads,"downloads","Загрузки"),
        (DockModules.Performance,"pc","Нагрузка ПК"),
        (DockModules.Sets,"sets","Наборы приложений"),
        (DockModules.Network,"network","Скорость сети"),
        (DockModules.Music,"music","Плеер"),
        (DockModules.Layouts,"layouts","Раскладка окон")
    ];

    IslandPreferences _preferences=IslandPreferences.Load();

    void OpenSettings()
    {
        if(!_expanded)Expand();
        var column=CardColumn(448);
        column.Children.Add(CardTitle("Настройки островка"));
        var body=new StackPanel();
        body.Children.Add(SettingsHeading("РЕЖИМ ПАНЕЛИ",0));
        var modes=new UniformGrid {Columns=2,Margin=new Thickness(-3,0,-3,4)};
        var compact=ModeButton("Компактный","Раскрывается по нажатию");
        var expanded=ModeButton("Всегда раскрыт","Панель остаётся на экране");
        modes.Children.Add(compact);modes.Children.Add(expanded);body.Children.Add(modes);
        void UpdateMode(){
            compact.Background=_preferences.AlwaysExpanded?PanelControls.Tile:PanelControls.Chrome;
            expanded.Background=_preferences.AlwaysExpanded?PanelControls.Chrome:PanelControls.Tile;
            compact.BorderBrush=_preferences.AlwaysExpanded?PanelControls.Outline:PanelControls.Rim;
            expanded.BorderBrush=_preferences.AlwaysExpanded?PanelControls.Rim:PanelControls.Outline;
            System.Windows.Automation.AutomationProperties.SetItemStatus(compact,_preferences.AlwaysExpanded?"":"Выбрано");
            System.Windows.Automation.AutomationProperties.SetItemStatus(expanded,_preferences.AlwaysExpanded?"Выбрано":"");
        }
        compact.Click+=(_,_)=>{if(SavePreferences(_preferences with {AlwaysExpanded=false}))UpdateMode();};
        expanded.Click+=(_,_)=>{if(SavePreferences(_preferences with {AlwaysExpanded=true}))UpdateMode();};
        UpdateMode();

        body.Children.Add(SettingsHeading("ПОВЕДЕНИЕ"));
        body.Children.Add(SettingsToggle("Скрывать в полноэкранном режиме",_preferences.HideFullscreen,
            on=>SavePreferences(_preferences with {HideFullscreen=on})));
        body.Children.Add(SettingsToggle("Сворачивать активное окно повторным кликом",_preferences.ClickToMinimize,
            on=>SavePreferences(_preferences with {ClickToMinimize=on})));
        body.Children.Add(SettingsToggle("Привязки Win к островку",_preferences.ShellShortcuts,
            on=>SavePreferences(_preferences with {ShellShortcuts=on})));
        body.Children.Add(SettingsToggle("Запускать при входе в Windows",Startup.Enabled,on=>{
            try{Program.SetAutostart(on);return true;}
            catch(Exception ex){PanelError("Не удалось изменить автозапуск");Program.Log("settings startup: "+ex.Message);return false;}
        }));
        AddUpdateSettings(body);

        body.Children.Add(SettingsHeading("ВНЕШНИЙ ВИД"));
        body.Children.Add(SettingsToggle("Анимации",_preferences.Animations,
            on=>SavePreferences(_preferences with {Animations=on})));
        body.Children.Add(SettingsToggle("Превью окон при наведении",_preferences.WindowPreviews,
            on=>SavePreferences(_preferences with {WindowPreviews=on})));
        body.Children.Add(SettingsToggle("Всплывающие уведомления островка",_preferences.Toasts,
            on=>SavePreferences(_preferences with {Toasts=on})));

        body.Children.Add(SettingsHeading("КНОПКИ НА ПАНЕЛИ"));
        foreach(var item in ModuleSettings){
            body.Children.Add(SettingsToggle(item.Label,_preferences.Modules.HasFlag(item.Module),on=>
                SavePreferences(_preferences with {Modules=on?_preferences.Modules|item.Module:_preferences.Modules&~item.Module})));
        }
        column.Children.Add(PanelControls.Scroller(body,Math.Clamp(SystemParameters.PrimaryScreenHeight-230,200,452)));
        var footer=LeftLabel("Изменения сохраняются сразу",11,Secondary);
        footer.Margin=new Thickness(0,12,0,0);column.Children.Add(footer);
        OpenCard("settings",column);
    }

    static TextBlock SettingsHeading(string title,int top=14)
    {
        var label=PanelControls.Eyebrow(title);label.Margin=new Thickness(3,top,0,6);
        return label;
    }

    static Button ModeButton(string title,string subtitle)
    {
        var labels=new StackPanel();
        var name=LeftLabel(title,13);name.FontWeight=FontWeights.SemiBold;
        labels.Children.Add(name);
        var detail=LeftLabel(subtitle,11,Secondary);detail.Margin=new Thickness(0,4,0,0);
        labels.Children.Add(detail);
        var button=PanelControls.Button(labels,title,59);
        button.Margin=new Thickness(3,0,3,0);PanelControls.FinishTile(button);
        return button;
    }

    static readonly ControlTemplate SettingsSwitch=(ControlTemplate)XamlReader.Parse("""
    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
      <Border x:Name="Row" Background="Transparent" CornerRadius="8" Padding="8,0">
        <DockPanel>
          <Border x:Name="Track" DockPanel.Dock="Right" Width="33" Height="19" Margin="8,0,0,0"
                  Background="#191A1E" BorderBrush="#5F616A" BorderThickness="1" CornerRadius="10">
            <Border x:Name="Knob" Background="#9C9EA7" Width="13" Height="13" CornerRadius="7"
                    HorizontalAlignment="Left" VerticalAlignment="Center" Margin="2,0"/>
          </Border>
          <ContentPresenter VerticalAlignment="Center"/>
        </DockPanel>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="#16FFFFFF"/></Trigger>
        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="Background" Value="#20FFFFFF"/></Trigger>
        <Trigger Property="IsChecked" Value="True">
          <Setter TargetName="Track" Property="Background" Value="#C4C7D0"/>
          <Setter TargetName="Track" Property="BorderBrush" Value="#DBDDE6"/>
          <Setter TargetName="Knob" Property="Background" Value="#18191D"/>
          <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
    """);

    static CheckBox SettingsToggle(string title,bool value,Func<bool,bool> change)
    {
        var check=new CheckBox {Content=LeftLabel(title,12),Height=34,IsChecked=value,Template=SettingsSwitch,
            Cursor=System.Windows.Input.Cursors.Hand,FocusVisualStyle=null};
        System.Windows.Automation.AutomationProperties.SetName(check,title);
        check.Click+=(_,_)=>{
            bool enabled=check.IsChecked==true;
            if(!change(enabled))check.IsChecked=!enabled;
        };
        return check;
    }

    bool SavePreferences(IslandPreferences next)
    {
        if(next==_preferences)return true;
        try{next.Save();}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            PanelError("Не удалось сохранить настройки");Program.Log("settings save: "+ex.Message);return false;
        }
        _preferences=next;
        ApplyPreferences();
        return true;
    }

    void ApplyPreferences()
    {
        ChromeMotion.PreferencesEnabled=_preferences.Animations;
        foreach(var item in ModuleSettings)
            _featureTriggers[item.Key].Visibility=_preferences.Modules.HasFlag(item.Module)?Visibility.Visible:Visibility.Collapsed;

        if(!_preview){
            if(_preferences.ShellShortcuts)_shellKeys??=new ShellKeys(Dispatcher,RunShellAction,WriteStatus);
            else{_shellKeys?.Dispose();_shellKeys=null;}
        }
        if(!_preferences.WindowPreviews){_previewTimer.Stop();ClosePreview();}
        else if(_expanded)_previewTimer.Start();
        if(!_preferences.Toasts)_toastUntil=DateTime.MinValue;
        if(!UpdateFullscreenVisibility()&&_preferences.AlwaysExpanded&&!_expanded)Expand();
        else{MeasureDock();BeginMotion();}
        SyncOutsideTracking();
        WriteStatus();
    }
}
