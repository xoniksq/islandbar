using System.Runtime.InteropServices;
using System.Security.Principal;

namespace IslandBar;

static class Startup
{
    const string TaskName="IslandBar";
    public static string ReturnShortcut=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Включить островок.lnk");
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    static extern void SHChangeNotify(uint eventId,uint flags,[MarshalAs(UnmanagedType.LPWStr)] string path,nint unused);
    public static void EnsureReturnShortcut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReturnShortcut)!);
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try {
            string menuPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"IslandBar","Островок.lnk");
            if(File.Exists(menuPath)){dynamic old=shell.CreateShortcut(menuPath);old.Hotkey="";old.Save();Marshal.ReleaseComObject(old);SHChangeNotify(0x2000,0x1005,menuPath,0);}
            bool existed=File.Exists(ReturnShortcut);
            dynamic link=shell.CreateShortcut(ReturnShortcut);
            try {
                link.TargetPath=Environment.ProcessPath!;link.Arguments="--return";link.WorkingDirectory=Program.Folder;
                link.Description="Включить или раскрыть островок · Ctrl+Alt+B";
                link.Hotkey="CTRL+ALT+B";link.Save();
            }finally{Marshal.ReleaseComObject(link);}
            SHChangeNotify(existed?0x2000u:2u,0x1005,ReturnShortcut,0);
        }finally{Marshal.ReleaseComObject(shell);}
    }
    public static bool Enabled
    {
        get {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            try {service.Connect();return service.GetFolder(@"\").GetTask(TaskName).Enabled;}
            catch(COMException){return false;}
            finally{Marshal.ReleaseComObject(service);}
        }
    }
    public static void Configure(bool enabled)
    {
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try {
            service.Connect();dynamic folder=service.GetFolder(@"\");
            if(!enabled){try{folder.GetTask(TaskName).Enabled=false;}catch(COMException){}return;}
            dynamic task=service.NewTask(0);
            string user=WindowsIdentity.GetCurrent().User!.Value;
            task.RegistrationInfo.Description="IslandBar — независимый запуск островка при входе в Windows";
            task.Principal.UserId=user;task.Principal.LogonType=3;task.Principal.RunLevel=0;
            task.Settings.ExecutionTimeLimit="PT0S";task.Settings.MultipleInstances=2;
            task.Settings.DisallowStartIfOnBatteries=false;task.Settings.StopIfGoingOnBatteries=false;
            task.Settings.AllowDemandStart=true;task.Settings.StartWhenAvailable=true;
            dynamic trigger=task.Triggers.Create(9);trigger.UserId=user;trigger.Delay="PT3S";
            dynamic action=task.Actions.Create(0);action.Path=Environment.ProcessPath!;action.WorkingDirectory=Program.Folder;
            folder.RegisterTaskDefinition(TaskName,task,6,user,null,3,null);
        }finally{Marshal.ReleaseComObject(service);}
    }
}
