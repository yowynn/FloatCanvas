using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class CommandHotkeyTests
{
    internal static void Run(Action<bool,string> check)
    {
        var state=new KeyboardSuppressionState(256,2000);
        state.Begin(new byte[256],default,default,100);
        state.CommandHotkeys=new() { [0x4650]=new(2,0x48) };
        bool Key(uint key,bool up=false)=>state.Process(key,0,up?0x80u:0,120,up,up?0x101u:0x100u,42,true,true,120);
        List<KeyboardInputPacket> Drain()
        {
            var packets=new List<KeyboardInputPacket>();
            while(state.TryDequeue(out var packet)) { packets.Add(packet!); state.Complete(packet!.Sequence); }
            return packets;
        }
        Key(0xA2); Drain();
        check(Key(0x48),"Optional global command suppresses its physical key");
        var command=Drain().Single();
        check(command.Kind==KeyboardInputPacketKind.Command && command.VirtualKey==0x4650 && command.ScanCode==0x48 && command.Modifiers==OverlayModifiers.Control,
            "Optional command carries stable ID and its actual chord for stale-message validation");
        Key(0x48); check(Drain().Count==0,"Optional commands do not repeat");
        Key(0x48,true); var release=Drain().Single();
        check(release.IsKeyUp && release.IsLocalBalance && state.IsActive,"Optional command release balances state without leaving editing");
        state.CommandsSuspended=true; Key(0x48);
        check(Drain().Single().Kind==KeyboardInputPacketKind.Key,"Recorder suspension delivers the chord as input rather than a command");
        Key(0x48,true);Drain();state.CommandsSuspended=false;
        Key(0xA0);Drain();Key(0x48);
        check(Drain().Single().Kind==KeyboardInputPacketKind.Key,"An additional modifier does not invoke an optional global command");
        Key(0x48,true);Key(0xA0,true);Drain();
        state.CommandHotkeys=[];Key(0x48);
        check(Drain().Single().Kind==KeyboardInputPacketKind.Key,"Cleared optional bindings no longer invoke commands");
    }
}
