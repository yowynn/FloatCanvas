using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

/// <summary>Visibility commands retain the editing lease without typing their consumed key.</summary>
internal static class HideCommandTests
{
    internal static void Run(Action<bool,string> check)
    {
        static KeyboardSuppressionState New(HotkeyBinding? hide=null)
        {var state=new KeyboardSuppressionState(256,2000);state.Begin(new byte[256],HotkeyBinding.DefaultToggle,hide??HotkeyBinding.DefaultHide,100);return state;}
        static bool Key(KeyboardSuppressionState state,uint key,bool up=false,long now=110,bool own=true,bool healthy=true) =>
            state.Process(key,key==0xC0?0x29u:key==0xA0?0x2Au:0x1Du,up?0x80u:0u,(uint)now,up,up?0x101u:0x100u,42,own,healthy,now);
        static List<KeyboardInputPacket> Drain(KeyboardSuppressionState state)
        {var result=new List<KeyboardInputPacket>();while(state.TryDequeue(out var packet)){result.Add(packet!);state.Complete(packet!.Sequence);}return result;}
        var state=New();long lease=state.LeaseId;
        check(Key(state,0xA2)&&Key(state,0xA0),"Hide modifiers are privately isolated before the command.");
        var modifiers=Drain(state);
        check(modifiers.Count==2&&modifiers.All(packet=>packet.Kind==KeyboardInputPacketKind.Key&&!packet.IsKeyUp),"Hide starts with ordinary private modifier downs.");
        check(Key(state,0xC0,now:120),"The hide command key is suppressed globally.");
        var command=Drain(state);
        check(state.IsActive&&state.LeaseId==lease&&state.LastEndReason is null,"Hide preserves the same active editing lease.");
        check(command.Count==1&&command[0].Kind==KeyboardInputPacketKind.Hide&&command[0].LeaseId==lease,
            "Hide emits exactly one private command without a LeaseEnded marker.");
        check(command[0].Modifiers==(OverlayModifiers.Control|OverlayModifiers.Shift),"Hide retains the actual held modifier state.");
        for(int index=0;index<40;index++)check(Key(state,0xC0,now:130+index*10),"Held hide-key repeats remain suppressed.");
        check(state.IsActive&&state.QueuedCount==0,"Hide repeats neither toggle visibility again nor enqueue typing packets.");
        check(Key(state,0xC0,up:true,now:600),"The consumed command's physical up remains suppressed.");
        var release=Drain(state);
        check(release.Count==1&&release[0].Kind==KeyboardInputPacketKind.Key&&release[0].IsKeyUp&&release[0].IsLocalBalance&&
            release[0].KeyboardState[0xC0]==0&&release[0].Modifiers==(OverlayModifiers.Control|OverlayModifiers.Shift),
            "Command up only balances private state without reconstructing a down.");
        check(Key(state,0xC0,now:610),"A second physical press can show the canvas within the same lease.");
        check(Drain(state).Single().Kind==KeyboardInputPacketKind.Hide&&state.IsActive&&state.LeaseId==lease,"A fresh hide/show press is a second visibility command, not a new lease.");
        Key(state,0xC0,up:true,now:620);Drain(state);
        check(Key(state,0xA0,up:true,now:630)&&Key(state,0xA2,up:true,now:640),"Command modifiers retain their physical private releases.");
        var balanced=Drain(state);
        check(balanced.Count==2&&balanced.All(packet=>packet.Kind==KeyboardInputPacketKind.Key&&packet.IsKeyUp)&&
            balanced[0].Modifiers==OverlayModifiers.Control&&balanced[1].Modifiers==OverlayModifiers.None,
            "Shift and Ctrl are balanced in actual release order without ending editing.");
        var local=state.CopyLocalState();
        check(local[0x10]==0&&local[0x11]==0&&local[0xA0]==0&&local[0xA2]==0&&local[0xC0]==0&&state.IsActive,
            "No consumed key or modifier remains down after hide/show releases.");
        check(Key(state,0x58,now:650),"Ordinary editing keys remain isolated after visibility changes.");
        check(Drain(state).Single().Kind==KeyboardInputPacketKind.Key,"Visibility commands do not swallow the next unrelated editing key.");

        state=New();Key(state,0xA2);Key(state,0xA0);Drain(state);Key(state,0xC0);Drain(state);
        check(Key(state,0xA0,up:true)&&Key(state,0xA2,up:true),"Modifiers can be released before the command key.");Drain(state);
        check(Key(state,0xC0)&&state.QueuedCount==0&&state.IsActive,"The held command remains a command stream after modifier release.");
        Key(state,0xC0,up:true);Drain(state);Key(state,0xC0);
        var normal=Drain(state);
        check(normal.Count==1&&normal[0].Kind==KeyboardInputPacketKind.Key&&!normal[0].IsKeyUp,
            "A released command key types normally on a later unmodified press.");

        foreach(bool loseForeground in new[]{true,false})
        {
            state=New();Key(state,0xA2);Key(state,0xA0);Drain(state);Key(state,0xC0);Drain(state);
            check(!Key(state,0x59,own:!loseForeground,healthy:loseForeground)&&!state.IsActive,
                "Foreground/watchdog loss still fails open while a visibility command is held.");
            var ended=Drain(state);
            check(ended.Last().Kind==KeyboardInputPacketKind.LeaseEnded&&ended.Any(packet=>packet.IsLocalBalance&&packet.VirtualKey==0xC0),
                "A real lease end balances the held visibility command before its end marker.");
            check(Key(state,0xC0)&&Key(state,0xC0,up:true),"Only the already consumed command tail stays suppressed after loss.");
            check(!Key(state,0xC0),"A fresh command-key press fails open after lease loss.");
        }
        state=New(new(0,0x48));
        check(Key(state,0x48)&&Drain(state).Single().Kind==KeyboardInputPacketKind.Hide&&state.IsActive,"A custom unmodified hide binding also keeps editing active.");
        Key(state,0x48,up:true);Drain(state);Key(state,0x48);
        check(Drain(state).Single().Kind==KeyboardInputPacketKind.Hide,"Custom visibility bindings can be used repeatedly without re-entering editing.");
    }
}
