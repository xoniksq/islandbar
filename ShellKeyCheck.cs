namespace IslandBar;

static class ShellKeyCheck
{
    public static void Run()
    {
        const uint left=0x5B,right=0x5C,esc=0x1B;

        static void Expect(bool condition,string name)
        {
            if(!condition)throw new InvalidOperationException("Keyboard routing: "+name);
        }

        foreach(uint winKey in new[]{left,right}){
            var router=new ShellKeyRouter();
            Expect(!router.Route(winKey,false,false,false,false,false).Suppress,"Win down passes through");
            Expect(!router.Route(winKey,false,false,false,false,true).Suppress,"Win repeat passes through");
            var release=router.Route(winKey,true,false,false,false,true);
            Expect(release is {Suppress:true,Action:ShellAction.Start,MaskWin:true,ReleaseWin:true},"Win alone opens Start and releases the modifier");
            Expect(!router.Route(winKey,true,false,false,false,false).Suppress,"Duplicate release is ignored");
        }

        foreach(uint letter in new uint[]{0x45,0x4C,0x53}){
            bool shift=letter==0x53;
            var router=new ShellKeyRouter();
            router.Route(left,false,false,false,false,false);
            if(shift)router.Route(0xA0,false,false,false,false,true);
            Expect(!router.Route(letter,false,false,shift,false,true).Suppress,"Unmapped Windows chord is forwarded");
            Expect(!router.Route(left,true,false,shift,false,true).Suppress,"Win release after a chord is forwarded");
        }

        foreach(var (key,action) in new[]{(0x4Eu,ShellAction.Calendar),(0x41u,ShellAction.Sound),(0x42u,ShellAction.Tray)}){
            var router=new ShellKeyRouter();
            router.Route(left,false,false,false,false,false);
            var chord=router.Route(key,false,false,false,false,true);
            Expect(chord.Suppress&&chord.MaskWin&&chord.Action==action,"Panel chord masks Start and opens the correct panel");
            Expect(router.Route(key,false,false,false,false,true) is {Suppress:true,Action:ShellAction.None},"Panel chord repeat does not toggle");
            Expect(router.Route(key,true,false,false,false,true).Suppress,"Panel chord release is consumed");
            Expect(!router.Route(left,true,false,false,false,true).Suppress,"Physical Win release is forwarded for panel chords");
        }

        var control=new ShellKeyRouter();
        Expect(control.Route(esc,false,true,false,false,false) is {Suppress:true,Action:ShellAction.Start},"Ctrl+Esc opens Start");
        Expect(control.Route(esc,true,false,false,false,false).Suppress,"Ctrl+Esc release is consumed after Ctrl is released");
        Expect(!control.Route(esc,false,true,true,false,false).Suppress,"Ctrl+Shift+Esc stays available");
        var both=new ShellKeyRouter();
        both.Route(left,false,false,false,false,false);
        both.Route(right,false,false,false,false,true);
        Expect(!both.Route(left,true,false,false,false,true).Suppress&&!both.Route(right,true,false,false,false,true).Suppress,
            "Both Windows keys do not trigger the Start menu binding");
    }
}
