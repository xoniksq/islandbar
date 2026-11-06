using System.Windows.Automation;
namespace IslandBar;

sealed record TrayIcon(string Name,AutomationElement Element,int[] RuntimeId);
sealed record TrayContextMenu(nint Window);

sealed class TrayFlyout
{
    public async Task<TrayIcon[]> ReadAsync()
    {
        nint window=Native.TrayOverflowWindow();
        var cached=await Task.Run(()=>Read(window));
        if(cached.Length>0)return cached;
        return await WithOverflowAsync(Read,preserveForeground:true)??[];
    }

    public async Task<bool> InvokeAsync(TrayIcon icon)
    {
        if(await Task.Run(()=>Invoke(icon.Element)))return true;
        return await WithOverflowAsync(window=>Read(window)
            .Any(current=>current.Name==icon.Name&&Invoke(current.Element))?icon:null) is not null;
    }

    public Task<TrayContextMenu?> ShowContextMenuAsync(TrayIcon icon,Native.Point anchor)
    {
        return WithOverflowAsync(window=>{
            var icons=Read(window);
            var target=icons.FirstOrDefault(current=>current.RuntimeId.SequenceEqual(icon.RuntimeId));
            if(target is null){
                var named=icons.Where(current=>current.Name==icon.Name).Take(2).ToArray();
                if(named.Length==1)target=named[0];
            }
            if(target is null)return null;
            Native.GetWindowThreadProcessId(window,out uint shellPid);
            var before=VisibleMenuCandidates(shellPid);
            var bounds=target.Element.Current.BoundingRectangle;
            const uint moveOnly=0x0015; // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            bool moved=false;
            Native.Rect original=default;
            if(!bounds.IsEmpty&&Native.GetWindowRect(window,out original)){
                int x=original.Left+anchor.X-(int)Math.Round(bounds.X+bounds.Width/2);
                int y=original.Top+anchor.Y-(int)Math.Round(bounds.Y+bounds.Height/2);
                moved=Native.SetWindowPos(window,0,x,y,0,0,moveOnly);
            }
            try{
                Native.SetForegroundWindow(window);
                target.Element.SetFocus();
                var focused=AutomationElement.FocusedElement;
                if(focused is null||!target.RuntimeId.SequenceEqual(focused.GetRuntimeId()))return null;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out uint foregroundPid);
                if(shellPid==0||shellPid!=foregroundPid||!ShellKeys.SendContextMenuKey())return null;

                // Some tray apps draw their own popup rather than using a Win32 menu.
                for(int i=0;i<18;i++){
                    Thread.Sleep(20);
                    nint menu=VisibleMenuCandidates(shellPid).FirstOrDefault(candidate=>!before.Contains(candidate));
                    if(menu!=0)return new TrayContextMenu(menu);
                }
                return null;
            }
            finally{if(moved)Native.SetWindowPos(window,0,original.Left,original.Top,0,0,moveOnly);}
        });
    }

    static HashSet<nint> VisibleMenuCandidates(uint shellPid)
    {
        var menus=new HashSet<nint>();
        Native.EnumWindows((window,_)=>{
            if(!Native.IsWindowVisible(window))return true;
            Native.GetWindowThreadProcessId(window,out uint pid);
            if(pid!=Environment.ProcessId&&(pid!=shellPid||Native.ClassName(window)=="#32768"))menus.Add(window);
            return true;
        },0);
        return menus;
    }

    static TrayIcon[] Read(nint window)
    {
        if(window==0)return [];
        try{
            var root=AutomationElement.FromHandle(window);
            var buttons=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
            var icons=new List<TrayIcon>();
            foreach(AutomationElement button in buttons){
                string name=button.Current.Name?.Trim()??"";
                if(name.Length>0&&button.TryGetCurrentPattern(InvokePattern.Pattern,out _))
                    icons.Add(new(name,button,button.GetRuntimeId()));
            }
            return icons.ToArray();
        }
        catch(ElementNotAvailableException){return [];}
    }

    static bool Invoke(AutomationElement element)
    {
        try{
            if(!element.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern))return false;
            ((InvokePattern)pattern).Invoke();return true;
        }
        catch(ElementNotAvailableException){return false;}
        catch(InvalidOperationException){return false;}
    }

    async Task<T?> WithOverflowAsync<T>(Func<nint,T?> action,bool preserveForeground=false) where T:class
    {
        Native.TaskbarPresentation? bar=null;
        Native.OverflowPresentation? overflow=null;
        nint foreground=preserveForeground?Native.GetForegroundWindow():0;
        nint window=Native.TrayOverflowWindow();
        try{
            if(window!=0)overflow=Native.MaskOverflow(window);
            bar=Native.RevealTaskbarTransparently();
            if(bar is not null)await Task.Delay(50);
            if(!await Task.Run(InvokeChevron))return null;
            for(int i=0;i<25;i++){
                window=Native.TrayOverflowWindow();
                if(window!=0&&Native.IsWindowVisible(window)){
                    overflow??=Native.MaskOverflow(window);
                    return await Task.Run(()=>action(window));
                }
                await Task.Delay(20);
            }
            return null;
        }
        finally{
            nint current=preserveForeground?Native.GetForegroundWindow():0;
            bool restoreFocus=foreground!=0&&foreground!=current&&
                (current==window||current==Native.FindWindow("Shell_TrayWnd",null));
            if(overflow is not null){Native.RestoreOverflowPresentation(overflow);Program.SaveOverflowPresentation(null);}
            else if(window!=0)Native.ShowWindow(window,0);
            if(bar is not null){Native.RestoreTaskbarPresentation(bar);Program.SaveTaskbarPresentation(null);}
            if(restoreFocus&&Native.IsWindow(foreground)&&!Native.IsIconic(foreground))Native.SetForegroundWindow(foreground);
        }
    }
    static bool InvokeChevron()
    {
        try {
            nint taskbar=Native.FindWindow("Shell_TrayWnd",null);if(taskbar==0)return false;
            var root=AutomationElement.FromHandle(taskbar);
            var condition=new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty,"SystemTrayIcon"),new PropertyCondition(AutomationElement.AutomationIdProperty,"ChevronButton")));
            var candidates=root.FindAll(TreeScope.Descendants,condition);
            foreach(AutomationElement candidate in candidates) {
                if(candidate.Current.ClassName is not ("SystemTray.NormalButton" or "SystemTray.ChevronIconView"))continue;
                if(candidate.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var expand))((ExpandCollapsePattern)expand).Expand();
                else if(candidate.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))((InvokePattern)invoke).Invoke();
                else continue;
                return true;
            }
            Program.Log("tray: chevron not found; candidates="+candidates.Count);
        }catch(Exception ex){Program.Log("tray: "+ex.Message);}
        return false;
    }
}
