using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IslandBar;
sealed partial class IslandWindow
{
    readonly NetworkMonitor _networkMonitor=new();
    NetworkRate? _networkRate;
    bool _networkReading,_mixerUpdating,_launchingSet;
    readonly TextBlock _netDown=Label("↓ —",9),_netUp=Label("↑ —",9);
    TextBlock? _networkDown,_networkUp,_networkAdapters;
    StackPanel? _mixerList;
    readonly Dictionary<string,(Border Root,Slider Volume,TextBlock Number,TextBlock Mute)> _mixerRows=[];
    AppSetEditor? _setEditor;
    static string Rate(double value)=>Bytes((long)Math.Max(0,value))+"/с";
    void InitializeConvenience()
    {
        InitializePreviews();
        var sets=MakeButton(Glyph("\uE8F1",17),"Наборы приложений",32);sets.Click+=(_,_)=>OpenSets();_featureTriggers["sets"]=sets;
        var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center};labels.Children.Add(_netDown);labels.Children.Add(_netUp);var network=MakeButton(labels,"Скорость сети: загрузка и отдача",78);network.Click+=(_,_)=>OpenNetwork();_featureTriggers["network"]=network;
        int index=_dock.Children.IndexOf(_scroll);_dock.Children.Insert(index++,sets);_dock.Children.Insert(index,network);
    }
    void OpenNetwork()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Скорость сети"));
        var rates=new System.Windows.Controls.Primitives.UniformGrid {Columns=2};
        Border Metric(string label,Thickness margin,out TextBlock value)
        {
            var content=new StackPanel();
            content.Children.Add(LeftLabel(label,11,Secondary));
            value=LeftLabel("—",21);value.Margin=new Thickness(0,4,0,0);content.Children.Add(value);
            return new Border {Child=content,Margin=margin,Padding=new Thickness(12),Background=PanelControls.Tile,CornerRadius=new CornerRadius(10)};
        }
        rates.Children.Add(Metric("↓ Скачивание",new Thickness(0,0,4,0),out _networkDown));
        rates.Children.Add(Metric("↑ Отдача",new Thickness(4,0,0,0),out _networkUp));
        column.Children.Add(rates);
        _networkAdapters=LeftLabel("",11,Secondary);_networkAdapters.TextWrapping=TextWrapping.Wrap;_networkAdapters.Margin=new Thickness(0,12,0,0);column.Children.Add(_networkAdapters);OpenCard("network",column);UpdateNetworkPanel();
    }
    void UpdateNetworkPanel(){if(_featureKind!="network"||_networkRate is not {} rate)return;_networkDown!.Text=Rate(rate.Down);_networkUp!.Text=Rate(rate.Up);_networkAdapters!.Text=rate.Adapters.Length==0?"Нет активного подключения":"Текущие передачи · "+string.Join(", ",rate.Adapters);}
    async Task RefreshNetworkAsync(){if(_networkReading||_closing)return;_networkReading=true;try{var rate=await Task.Run(_networkMonitor.Read);if(_closing)return;_networkRate=rate;_netDown.Text="↓ "+Rate(rate.Down);_netUp.Text="↑ "+Rate(rate.Up);UpdateNetworkPanel();}catch(Exception ex){Program.Log("network: "+ex.Message);}finally{_networkReading=false;}}
    void OpenMixer()
    {
        var column=CardColumn();
        var header=new DockPanel {Margin=new Thickness(0,0,0,10)};
        var back=PanelControls.Button(LeftLabel("‹ Звук",11,Secondary),"Вернуться к звуку",26,true);
        back.Click+=(_,_)=>OpenSound();DockPanel.SetDock(back,Dock.Right);header.Children.Add(back);
        var title=CardTitle("Микшер приложений");title.Margin=new Thickness(0);title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(title);
        column.Children.Add(header);
        _mixerList=new StackPanel();_mixerRows.Clear();column.Children.Add(PanelControls.Scroller(_mixerList,320));
        var hint=LeftLabel("Здесь видны приложения со звуковой сессией",11,Secondary);
        hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,6,0,0);column.Children.Add(hint);
        OpenCard("mixer",column);UpdateMixerPanel();
    }
    void UpdateMixerPanel()
    {
        if(_featureKind!="mixer"||_mixerList is null)return;
        try{
            var apps=AppMixer.Read();var keys=apps.Select(a=>a.Key).ToHashSet();foreach(string key in _mixerRows.Keys.Where(k=>!keys.Contains(k)).ToArray()){_mixerList.Children.Remove(_mixerRows[key].Root);_mixerRows.Remove(key);}
            for(int i=0;i<apps.Length;i++){
                var app=apps[i];if(!_mixerRows.TryGetValue(app.Key,out var row)){
                    var content=new StackPanel();content.Children.Add(LeftLabel(app.Name,12));
                    var line=new DockPanel{Margin=new Thickness(0,4,0,0)};
                    var muteLabel=Glyph("\uE767",16);
                    var mute=PanelControls.Button(muteLabel,"Звук: "+app.Name,30,true);mute.Width=34;mute.Padding=new Thickness(0);mute.Margin=new Thickness(0,0,8,0);
                    mute.Click+=(_,_)=>{var current=AppMixer.Read().FirstOrDefault(a=>a.Key==app.Key);if(current is not null)AppMixer.SetMute(app.Key,!current.Muted);UpdateMixerPanel();};line.Children.Add(mute);
                    var number=Label("",11);number.Width=44;DockPanel.SetDock(number,Dock.Right);line.Children.Add(number);
                    var slider=new Slider{Minimum=0,Maximum=100,Template=PanelControls.Slider,IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center};
                    System.Windows.Automation.AutomationProperties.SetName(slider,"Громкость "+app.Name);
                    slider.ValueChanged+=(_,_)=>{if(!_mixerUpdating){int volume=(int)Math.Round(slider.Value);AppMixer.SetVolume(app.Key,volume);number.Text=$"{volume}%";}};
                    line.Children.Add(slider);content.Children.Add(line);
                    var root=new Border{Child=content,Padding=new Thickness(11),CornerRadius=new CornerRadius(10),Background=PanelControls.Tile,Margin=new Thickness(0,0,0,8)};row=(root,slider,number,muteLabel);_mixerRows[app.Key]=row;
                    ChromeMotion.Reveal(root,i);
                }
                int index=_mixerList.Children.IndexOf(row.Root);if(index!=i){if(index>=0)_mixerList.Children.Remove(row.Root);_mixerList.Children.Insert(i,row.Root);}
                _mixerUpdating=true;try{if(!row.Volume.IsMouseCaptureWithin){row.Volume.Value=app.Volume;row.Number.Text=$"{app.Volume}%";}}finally{_mixerUpdating=false;}
                row.Mute.Text=app.Muted?"\uE74F":"\uE767";
            }
        }catch(Exception ex){PanelError("Не удалось прочитать звуковые сессии");Program.Log("mixer: "+ex.Message);}
    }
    List<AppSet> AvailableSets()
    {
        var sets=AppSets.Load();if(sets.Count>0||File.Exists(Path.Combine(Program.Folder,"app-sets.json")))return sets;
        var apps=_tiles.Values.Select(t=>t.App).ToArray();
        if(apps.Length==0)return sets;
        void Add(string name,Func<DockApp,bool> select){var selected=apps.Where(select).Select(StoredApp.From).OfType<StoredApp>().ToList();if(selected.Count>0)sets.Add(new(name,selected));}
        Add("Игры",a=>a.Key is "app:steam" or "app:discord");Add("Работа",a=>a.Key=="app:explorer"||a.Name.Contains("Yandex",StringComparison.OrdinalIgnoreCase));Add("Музыка",a=>a.Name.Contains("Музыка",StringComparison.OrdinalIgnoreCase)||a.Name.Equals("Telegram",StringComparison.OrdinalIgnoreCase));AppSets.Save(sets);return sets;
    }
    void OpenSets()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Наборы приложений"));var list=new StackPanel();
        var sets=AvailableSets();
        if(sets.Count==0)list.Children.Add(LeftLabel("Создай набор для запуска нескольких программ",11,Secondary));
        foreach(var set in sets){
            var row=new DockPanel {Margin=new Thickness(0,0,0,4)};
            var edit=PanelControls.Button(Glyph("\uE70F",15),"Изменить набор "+set.Name,50,true);edit.Width=40;edit.Margin=new Thickness(4,0,0,0);edit.Background=PanelControls.Tile;edit.ToolTip=PanelControls.Hint("Изменить набор");
            edit.Click+=(_,_)=>EditSet(set);DockPanel.SetDock(edit,Dock.Right);row.Children.Add(edit);
            var labels=new StackPanel();labels.Children.Add(LeftLabel(set.Name,13));var detail=LeftLabel(string.Join(" · ",set.Apps.Select(a=>a.Name)),11,Secondary);detail.Margin=new Thickness(0,3,0,0);labels.Children.Add(detail);
            var run=PanelControls.Button(labels,"Запустить набор "+set.Name,50);PanelControls.FinishTile(run);
            run.Click+=async(_,_)=>await RunSetAsync(set);row.Children.Add(run);
            var menu=CreateMenu();TrackMenu(menu);var remove=new MenuItem{Header="Удалить набор"};remove.Click+=(_,_)=>{AppSets.Save(AppSets.Load().Where(s=>s.Name!=set.Name));CloseFeature();OpenSets();};menu.Items.Add(remove);run.ContextMenu=menu;list.Children.Add(row);
        }
        column.Children.Add(PanelControls.Scroller(list,300));
        var add=PanelControls.Button(LeftLabel("+  Создать набор",12),"Создать набор",36,true);add.Margin=new Thickness(0,8,0,0);PanelControls.FinishTile(add);add.Click+=(_,_)=>EditSet(null);column.Children.Add(add);OpenCard("sets",column);
    }
    void EditSet(AppSet? existing)
    {
        var available=_tiles.Values.Select(t=>StoredApp.From(t.App)).OfType<StoredApp>().ToArray();Collapse();if(_setEditor is not null){_setEditor.Activate();return;}
        _setEditor=new AppSetEditor(existing,available,set=>{var sets=AppSets.Load();int index=sets.FindIndex(s=>s.Name==existing?.Name||s.Name==set.Name);sets.RemoveAll(s=>s.Name==existing?.Name||s.Name==set.Name);sets.Insert(index<0?sets.Count:Math.Min(index,sets.Count),set);AppSets.Save(sets);Expand();OpenSets();},_preview||_inspect);_setEditor.Closed+=(_,_)=>_setEditor=null;_setEditor.Show();
    }
    async Task RunSetAsync(AppSet set)
    {
        if(_launchingSet)return;_launchingSet=true;var failed=new List<string>();Collapse();
        try{var catalog=await _catalog;var running=await Task.Run(catalog.GetApps);foreach(var app in set.Apps.DistinctBy(a=>a.Key,StringComparer.OrdinalIgnoreCase)){if(AppSets.IsRunning(app,running))continue;try{Catalog.Launch(app.ToApp());}catch{failed.Add(app.Name);}await Task.Delay(120);}if(failed.Count>0){Expand();OpenSets();PanelError("Не удалось открыть: "+string.Join(", ",failed));}}
        finally{_launchingSet=false;}
    }
}
