using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IslandBar;

enum ShellAction { None, Start, Calendar, Sound, Tray }

readonly record struct ShellKeyRoute(bool Suppress,ShellAction Action=ShellAction.None,bool MaskWin=false,bool ReleaseWin=false);

// Keep Windows modifier presses in the normal input stream so other shortcuts
// still reach the shell, including shortcuts handled outside this process.
sealed class ShellKeyRouter
{
    const uint LeftWin=0x5B,RightWin=0x5C;
    readonly HashSet<uint> _consumed=[];
    uint _heldWin;
    uint _tap;

    public ShellKeyRoute Route(uint key,bool up,bool ctrl,bool shift,bool alt,bool win)
    {
        if(key is LeftWin or RightWin){
            uint bit=key==LeftWin?1u:2u;
            if(up){
                _heldWin&=~bit;
                if(_tap!=key)return default;
                _tap=0;
                return new(true,ShellAction.Start,MaskWin:true,ReleaseWin:true);
            }
            if(!win)_heldWin=0;
            if((_heldWin&bit)==0){
                _tap=_heldWin==0&&!ctrl&&!shift&&!alt&&!win?key:0;
                _heldWin|=bit;
            }
            return default;
        }

        _tap=0;
        if(_consumed.Contains(key)){
            if(up)_consumed.Remove(key);
            return new(true);
        }
        if(up||shift||alt)return default;
        if(key==0x1B&&ctrl&&!win){
            _consumed.Add(key);
            return new(true,ShellAction.Start);
        }
        if(!win||ctrl)return default;
        var action=key switch {0x4E=>ShellAction.Calendar,0x41=>ShellAction.Sound,0x42=>ShellAction.Tray,_=>ShellAction.None};
        if(action==ShellAction.None)return default;
        _consumed.Add(key);
        return new(true,action,MaskWin:true);
    }

    public void CancelSuppression(uint key)=>_consumed.Remove(key);
}

sealed class ShellKeys:IDisposable
{
    const int KeyboardHook=13, KeyboardInput=1;
    const uint KeyUp=2, Extended=1;
    const uint HookKeyUp=0x80;
    const ushort DummyKey=0xFF;
    const uint OwnInput=0x49534252;

    [StructLayout(LayoutKind.Sequential)]
    struct KeyEvent {public uint Key,Scan,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]
    struct Keyboard {public ushort Key,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]
    struct Mouse {public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Explicit)]
    struct InputPayload
    {
        [FieldOffset(0)]public Keyboard Keyboard;
        [FieldOffset(0)]public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Input {public uint Type;public InputPayload Payload;}
    [DllImport("user32.dll",SetLastError=true)]
    static extern uint SendInput(uint count,[In] Input[] inputs,int size);

    readonly Dispatcher _ui;
    readonly Action<ShellAction> _action;
    readonly Action _statusChanged;
    readonly ShellKeyRouter _router=new();
    readonly Native.MouseProc _callback;
    readonly Thread _thread;
    readonly object _lifetime=new();
    Dispatcher? _loop;
    nint _hook;
    volatile bool _installed;
    volatile bool _disposed;
    public bool Installed=>_installed;

    public ShellKeys(Dispatcher ui,Action<ShellAction> action,Action statusChanged)
    {
        _ui=ui;_action=action;_statusChanged=statusChanged;_callback=Handle;
        _thread=new Thread(Run) {IsBackground=true,Name="IslandBar keyboard"};
        _thread.Start();
    }

    void Run()
    {
        var dispatcher=Dispatcher.CurrentDispatcher;
        lock(_lifetime){
            if(_disposed)return;
            _loop=dispatcher;
        }
        _hook=Native.SetWindowsHookEx(KeyboardHook,_callback,Native.GetModuleHandle(null),0);
        _installed=_hook!=0;
        _ui.BeginInvoke(_statusChanged);
        if(!_installed){Program.Log("keyboard: could not install hook");return;}
        try{Dispatcher.Run();}
        finally{Native.UnhookWindowsHookEx(_hook);_installed=false;}
    }

    nint Handle(int code,nint message,nint data)
    {
        if(code==0&&!_disposed){
            var key=Marshal.PtrToStructure<KeyEvent>(data);
            if(key.Extra!=OwnInput){
                var route=_router.Route(key.Key,(key.Flags&HookKeyUp)!=0,
                    Down(0x11),Down(0x10),Down(0x12),Down(0x5B)||Down(0x5C));
                if(route.Suppress){
                    if(route.MaskWin&&!MaskWindowsKey(route.ReleaseWin?(ushort)key.Key:(ushort)0)){
                        _router.CancelSuppression(key.Key);
                        return Native.CallNextHookEx(_hook,code,message,data);
                    }
                    if(route.Action!=ShellAction.None)
                        _ui.BeginInvoke(()=>_action(route.Action),DispatcherPriority.Input);
                    return 1;
                }
            }
        }
        return Native.CallNextHookEx(_hook,code,message,data);
    }

    static bool Down(int key)=>Native.GetAsyncKeyState(key)<0;

    static bool MaskWindowsKey(ushort release)
    {
        // The dummy key marks Win as used in a chord. For a lone Win tap,
        // replay the original release after the dummy key to keep Win unstuck.
        Input[] inputs=release==0
            ?[Key(DummyKey,0),Key(DummyKey,KeyUp)]
            :[Key(DummyKey,0),Key(DummyKey,KeyUp),Key(release,KeyUp|Extended)];
        return SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())==inputs.Length;
    }

    static Input Key(ushort key,uint flags)=>new() {
        Type=KeyboardInput,Payload=new InputPayload {Keyboard=new Keyboard {Key=key,Flags=flags,Extra=OwnInput}}
    };

    public static bool SendContextMenuKey()
        =>SendInput(2,[Key(0x5D,Extended),Key(0x5D,KeyUp|Extended)],Marshal.SizeOf<Input>())==2;

    public void Dispose()
    {
        lock(_lifetime){
            if(_disposed)return;
            _disposed=true;
            _loop?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }
}
