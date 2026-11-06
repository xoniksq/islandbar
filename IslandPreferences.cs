using System.Text.Json;

namespace IslandBar;

[Flags]
enum DockModules
{
    Folders=1, Downloads=2, Performance=4, Sets=8, Network=16, Music=32, Layouts=64,
    All=Folders|Downloads|Performance|Sets|Network|Music|Layouts
}

sealed record IslandPreferences
{
    public bool AlwaysExpanded {get;init;}
    public bool Animations {get;init;}=true;
    public bool WindowPreviews {get;init;}=true;
    public bool Toasts {get;init;}=true;
    public bool HideFullscreen {get;init;}=true;
    public bool ShellShortcuts {get;init;}=true;
    public bool ClickToMinimize {get;init;}
    public DockModules Modules {get;init;}=DockModules.All;

    static string Pathname=>Path.Combine(Program.Folder,"island-settings.json");

    public static IslandPreferences Load()
    {
        try{
            if(File.Exists(Pathname))
                return JsonSerializer.Deserialize<IslandPreferences>(File.ReadAllText(Pathname))??new();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){
            Program.Log("settings read: "+ex.Message);
        }
        return new();
    }

    public void Save()
    {
        string temp=Pathname+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions {WriteIndented=true}));
        File.Move(temp,Pathname,true);
    }
}
