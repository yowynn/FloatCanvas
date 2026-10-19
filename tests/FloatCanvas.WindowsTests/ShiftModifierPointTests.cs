using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class ShiftModifierPointTests
{
    internal static void Run(Action<bool,string> check)
    {
        var state=new KeyboardSuppressionState(256,2000);
        state.Begin(new byte[256],HotkeyBinding.DefaultToggle,HotkeyBinding.DefaultHide,100);
        check(state.Process(0xA0,0x2A,0,110,false,0x100,42,true,true,110,new(-1500,310),true),"Shift down is captured with its physical pointer position");
        check(state.TryDequeue(out var down)&&down!.PointerPosition==new OverlayPoint(-1500,310)&&down.Modifiers==OverlayModifiers.Shift&&down.PointerLeftDown==true,"queued Shift down retains the original physical coordinates, button state and modifiers");
        state.Complete(down!.Sequence);
        check(state.Process(0xA0,0x2A,0x80,120,true,0x101,42,true,true,120,new(-1400,370),false),"Shift release is captured while the lease remains active");
        check(state.TryDequeue(out var up)&&up!.PointerPosition==new OverlayPoint(-1400,370)&&up.Modifiers==OverlayModifiers.None&&up.IsKeyUp&&up.PointerLeftDown==false,"queued Shift release retains its independent endpoint and mouse-button snapshot");
        check(down.PointerPosition==new OverlayPoint(-1500,310),"processing a later key never mutates the earlier pointer snapshot");
        state.Complete(up!.Sequence);
    }
}
