using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

internal static class PresentationRuntimeTests
{
    private static readonly PresentationRuntimeDemand All = new(true, true, true, true, true, 900, 450, 5000);
    private static PresentationObservedInput Key(int vk, bool down=true, long time=0, long device=1, int scan=0, uint flags=0) =>
        new(down ? PresentationObservedKind.KeyDown : PresentationObservedKind.KeyUp, time, device, vk, scan, flags, "VK " + vk);
    private static PresentationObservedBatch Batch(params PresentationObservedInput[] values) => new(1, values, null, false);
    private static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);

    internal static void Run(Action<string,bool> check)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            Timeline(check);
            DeviceButtons(check);
            DeviceButtonRoutes(dispatcher, check);
            BrokerLifecycle(dispatcher,check);
            HeldKeyObserverFailure(dispatcher,check);
            RegistrationRoles(dispatcher,check);
            LateStarts(dispatcher,check);
            NotificationsAndSourceIsolation(dispatcher,check);
            BeforeInputPause(dispatcher,check);
            NoAmbientContext(dispatcher,check);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void HeldKeyObserverFailure(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory();
        bool keyboardAvailable = true;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start,
            keyboardAvailable: () => keyboardAvailable);
        broker.Configure(new(true, false, false, false, true, 900, 450, 5000), true, new(), "Paused");
        broker.SetArmed(true);
        PumpUntil(dispatcher, () => broker.Observing);
        factory.Requests[0].Source.Queue([Key(0xA2, scan: 29)]);
        PumpUntil(dispatcher, () => broker.Frame.PressedKeys.Count != 0);
        keyboardAvailable = false;
        PumpUntil(dispatcher, () => !broker.Observing);
        check("a stopped key observer clears a lone held modifier without another input event or gamepad timer",
            !broker.Observing && broker.Frame.PressedKeys.Count == 0);
    }

    private static void DeviceButtons(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        var demand = All with { MouseButtons = true, GamepadButtons = true };
        var mouse = new PresentationObservedInput(PresentationObservedKind.ButtonDown, 10, Device: 11, Button: 3,
            DeviceKind: PresentationDeviceKind.Mouse);
        var gamepad = new PresentationObservedInput(PresentationObservedKind.ButtonDown, 10,
            Device: 0, Button: (int)PresentationGamepadButton.A - 1, DeviceKind: PresentationDeviceKind.Gamepad);
        timeline.Apply(Batch(Key(0x41), mouse, mouse with { Device = 12 }, gamepad), demand, Ms(10));
        var frame = timeline.Capture(1, 1, Ms(10));
        check("keyboard and controller A stay distinct, and two mice share one held binding",
            frame.PressedKeys.Select(cap => cap.DeviceType).ToHashSet().SetEquals(Enum.GetValues<PresentationKeyDevice>()) &&
            frame.PressedKeys.Count == 3 && frame.RecentChords.Single().Keys.Single().DeviceType == PresentationKeyDevice.Keyboard);
        timeline.Apply(Batch(mouse with { Kind = PresentationObservedKind.ButtonUp },
            gamepad with { Kind = PresentationObservedKind.ButtonUp }), demand, Ms(20));
        check("controller release clears its key while another mouse still owns the shared button",
            timeline.Capture(1, 2, Ms(20)).PressedKeys.Select(cap => cap.DeviceType).ToHashSet()
                .SetEquals([PresentationKeyDevice.Keyboard, PresentationKeyDevice.Mouse]));
        timeline.Apply(Batch(mouse with { Kind = PresentationObservedKind.DeviceRemoved, Device = 12 }), demand, Ms(20));
        check("removing the remaining mouse clears its held key without releasing keyboard A",
            timeline.Capture(1, 3, Ms(20)).PressedKeys.Single().DeviceType == PresentationKeyDevice.Keyboard);
        timeline.Apply(Batch(mouse, gamepad), demand, Ms(30));
        timeline.SetKeyboardSuppressed(true);
        check("privacy suppression clears all live keycaps", timeline.Capture(1, 4, Ms(30)).PressedKeys.Count == 0);
    }

    private static void DeviceButtonRoutes(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory();
        ushort gamepad = 0;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start,
            readGamepad: slot => slot == 0 ? gamepad : (ushort)0);
        var demand = new PresentationRuntimeDemand(true, false, false, false, true, 900, 450, 800)
            { MouseButtons = true, GamepadButtons = true, ScopedKeyboard = true };
        broker.Configure(demand, true, new object(), "paused");
        broker.Toggle();
        check("a mouse keycap subscribes to raw buttons with mouse effects disabled",
            factory.Requests.Single() is { Keyboard: true, Pointer: true } && broker.PointerFrame is null);
        broker.ConfigureEditingInput(true);
        long now = Environment.TickCount64;
        var mouse = new PresentationObservedInput(PresentationObservedKind.ButtonDown, now, Device: 17, Button: 4,
            HasPosition: true, X: 50, Y: 50, DeviceKind: PresentationDeviceKind.Mouse);
        factory.Requests[0].Source.Queue([mouse]); Pump(dispatcher);
        check("a mouse side button reaches both live and appropriate scoped keycaps in editing mode",
            broker.Frame.PressedKeys is [{ DeviceType: PresentationKeyDevice.Mouse, VirtualKey: 5 }] &&
            broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).PressedKeys.Count == 1 &&
            broker.Frame.Clicks.Count == 0);
        gamepad = 1 << ((int)PresentationGamepadButton.A - 1);
        PumpUntil(dispatcher, () => broker.Frame.PressedKeys.Any(cap => cap.DeviceType == PresentationKeyDevice.Gamepad));
        gamepad = 0;
        PumpUntil(dispatcher, () => broker.Frame.PressedKeys.All(cap => cap.DeviceType != PresentationKeyDevice.Gamepad));
        factory.Requests[0].Source.Queue([mouse with { Kind = PresentationObservedKind.ButtonUp }]); Pump(dispatcher);
        check("native mouse and polled controller releases clear their live keycaps", broker.Frame.PressedKeys.Count == 0);
        factory.Requests[0].Source.Queue([mouse with { Kind = PresentationObservedKind.Wheel, Milliseconds = Environment.TickCount64, Button = 5 }]);
        Pump(dispatcher);
        check("wheel up lights its own key in ordinary input without showing click effects",
            broker.Frame.PressedKeys is [{ DeviceType: PresentationKeyDevice.Mouse, VirtualKey: 6 }] &&
            broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).PressedKeys.Count == 1 &&
            broker.Frame.Clicks.Count == 0);
        PumpUntil(dispatcher, () => broker.Frame.PressedKeys.Count == 0);
        factory.Requests[0].Source.Queue([mouse]); Pump(dispatcher);
        check("a side button can be held again after release", broker.Frame.PressedKeys.Count == 1);
        factory.Requests[0].Source.Queue(overflow: true); Pump(dispatcher);
        check("losing a raw mouse transition cannot leave an editing keycap stuck", broker.Frame.PressedKeys.Count == 0);

        factory.Requests[0].Source.Queue([mouse]); Pump(dispatcher);
        check("a live mouse keycap is held before leaving edit mode", broker.Frame.PressedKeys.Count == 1);
        factory.Requests[0].Source.Queue([mouse with { Kind = PresentationObservedKind.ButtonUp }]);
        broker.ConfigureEditingInput(false); Pump(dispatcher);
        check("an earlier physical mouse release delivered after edit exit clears all of its keycaps", broker.Frame.PressedKeys.Count == 0 &&
            broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).PressedKeys.Count == 0);
    }

    private static void Timeline(Action<string,bool> check)
    {
        var timeline=new PresentationTimeline();
        timeline.Apply(Batch(Key(0xA2,scan:29),Key(0xA3,scan:29,flags:2)),All,Ms(100));
        var frame=timeline.Capture(1,1,Ms(100));
        check("runtime preserves independently held left/right modifiers",frame.PressedKeys.Select(key=>key.VirtualKey).SequenceEqual(new[]{0xA2,0xA3}));
        timeline.Apply(Batch(Key(0xA2,false,100,scan:29)),All,Ms(100));
        check("one modifier break leaves the other physical modifier held",timeline.Capture(1,2,Ms(100)).PressedKeys.Single().VirtualKey==0xA3);
        timeline.Clear();
        timeline.Apply(Batch(Key(0x43,device:1,scan:46),Key(0x43,device:2,scan:46)),All,Ms(100));
        check("two keyboards share one visible keycap while retaining device ownership",timeline.Capture(1,3,Ms(100)).PressedKeys.Count==1);
        timeline.Apply(Batch(Key(0x43,false,100,1,46)),All,Ms(100));
        check("first device break cannot release another device's held key",timeline.Capture(1,4,Ms(100)).PressedKeys.Single().VirtualKey==0x43);
        timeline.Apply(Batch(new PresentationObservedInput(PresentationObservedKind.DeviceRemoved,100,Device:2)),All,Ms(100));
        check("device removal clears only its held keys and releases its chord",timeline.Capture(1,5,Ms(100)).PressedKeys.Count==0&&timeline.Capture(1,6,Ms(100)).RecentChords.All(chord=>!chord.IsHeld));
        timeline.Clear();
        timeline.Apply(Batch(Key(0x11),Key(0x43)),All,Ms(100));long revision=timeline.Revision;
        for(int batch=0;batch<20;batch++)timeline.Apply(new(1,Enumerable.Repeat(Key(0x43),500),null,false),All,Ms(100));
        frame=timeline.Capture(1,7,Ms(100));
        check("10000 repeated make packets do not grow held state, chords or revision",frame.PressedKeys.Count==2&&frame.RecentChords.Count==1&&timeline.Revision==revision);
        timeline.Apply(Batch(Key(0x43,false,110)),All,Ms(110));
        check("key break starts the released chord hold interval",!timeline.Capture(1,8,Ms(110)).RecentChords.Single().IsHeld);
        timeline.Trim(Ms(1010),All);
        check("released chords expire at the configured hold boundary",timeline.Capture(1,9,Ms(1010)).RecentChords.Count==0);
        timeline.Clear();var shortcuts=All with{AllPhysicalKeys=false};
        timeline.Apply(Batch(Key(0x41),Key(0xA0),Key(0x42)),shortcuts,Ms(100));
        check("default shortcut mode ignores unmodified and Shift-only letter input",timeline.Capture(1,10,Ms(100)).PressedKeys.All(key=>key.IsModifier)&&timeline.Capture(1,11,Ms(100)).RecentChords is [{ IsHeld: true }]);
        timeline.Apply(Batch(Key(0xA2),Key(0x43),Key(13)),shortcuts,Ms(100));
        check("shortcut mode retains Ctrl combinations and named controls",timeline.Capture(1,12,Ms(100)).RecentChords.Count==2);
        timeline.Clear();
        int[] namedControls = [0x60, 0x69, 0x6A, 0x6F, 0xA6, 0xAC, 0xAD, 0xAF, 0xB0, 0xB3, 0xB7];
        timeline.Apply(Batch(namedControls.Select(vk => Key(vk)).ToArray()), shortcuts, Ms(100));
        check("shortcut mode retains numpad media and browser command keys",
            namedControls.All(vk => timeline.Capture(1, 121, Ms(100)).PressedKeys.Any(key => key.VirtualKey == vk)));
        timeline.Clear();
        timeline.Apply(Batch(Key(0x11, device: 0)), All, Ms(100));
        timeline.Apply(Batch(new PresentationObservedInput(PresentationObservedKind.DeviceRemoved, 100, Device: 7,
            DeviceKind: PresentationDeviceKind.Mouse)), All, Ms(100));
        check("removing a mouse never clears an anonymous held keyboard key",
            timeline.Capture(1, 13, Ms(100)).PressedKeys.Single().VirtualKey == 0x11);
        timeline.Apply(Batch(new PresentationObservedInput(PresentationObservedKind.DeviceRemoved, 100, Device: 9,
            DeviceKind: PresentationDeviceKind.Unknown)), All, Ms(100));
        check("an unidentified removed device never clears an anonymous held keyboard key",
            timeline.Capture(1, 14, Ms(100)).PressedKeys.Single().VirtualKey == 0x11);
        timeline.Apply(Batch(new PresentationObservedInput(PresentationObservedKind.DeviceRemoved, 100, Device: 8,
            DeviceKind: PresentationDeviceKind.Keyboard)), All, Ms(100));
        check("removing a keyboard retires anonymous held hook state",
            timeline.Capture(1, 15, Ms(100)).PressedKeys.Count == 0);
        timeline.Clear();timeline.Apply(new(1,Enumerable.Range(1,PresentationLimits.MaximumHeldKeys).Select(vk=>Key(vk)),null,false),All,Ms(100));
        check("held-key capacity accepts the complete bounded physical state",timeline.Capture(1,13,Ms(100)).PressedKeys.Count==PresentationLimits.MaximumHeldKeys);
        timeline.Apply(Batch(Key(PresentationLimits.MaximumHeldKeys+1)),All,Ms(100));
        check("held-key saturation clears stale ownership at the configured capacity",timeline.Capture(1,14,Ms(100)).PressedKeys.Count==1&&timeline.Capture(1,14,Ms(100)).RecentChords.Count<=PresentationLimits.MaximumChords);
        for(int i=0;i<600;i++)timeline.Apply(new(1,[],new(i*2,-i,100+i),false),All,Ms(100+i));
        frame=timeline.Capture(1,15,Ms(700));
        check("600 cursor samples retain only512 points with a new segment boundary",frame.Trail.Count==512&&frame.Trail[0].StartsSegment&&frame.CursorScreen==new PointD(1198,-599));
        check("runtime payload retains unshifted negative physical screen coordinates",frame.Trail[^1].ScreenPoint==new PointD(1198,-599)&&frame.PayloadBytes<=PresentationLimits.MaximumFramePayloadBytes);
        timeline.Apply(new(1,[],new(1300,-700,3000),false),All,Ms(3000));
        check("a long sampling gap starts a disconnected trail segment",timeline.Capture(1,16,Ms(3000)).Trail[^1].StartsSegment);
        timeline.Clear();timeline.Apply(Batch(Key(65,time:long.MaxValue),Key(66,time:-1)),All,Ms(20));
        frame=timeline.Capture(1,17,Ms(1));
        check("future native timestamps clamp to clock and negative packets are ignored",frame.Time==Ms(20)&&frame.RecentChords.Single().Timestamp==Ms(20)&&frame.PressedKeys.Single().VirtualKey==65);
        timeline.Apply(Batch(Key(67,time:2)),All,Ms(1));
        check("backward clocks cannot make a runtime frame time move backward",timeline.Capture(1,18,Ms(1)).Time==Ms(20));
        timeline.Clear();timeline.Apply(Batch(Key(0),Key(-1),Key(256)),All,Ms(20));
        check("unsupported virtual-key metadata never becomes a displayed keycap",timeline.Capture(1,18,Ms(20)).PressedKeys.Count==0);
        timeline.SetKeyboardSuppressed(true);
        timeline.Apply(new(1,[Key(0x11),Key(0x43)],new(5,6,20),false),All,Ms(20));
        frame=timeline.Capture(1,19,Ms(20));
        check("known-password suppression drops keys while pointer presentation continues",frame.KeyboardSuppressed&&frame.PressedKeys.Count==0&&frame.RecentChords.Count==0&&frame.CursorScreen==new PointD(5,6));
        timeline.Apply(new(1,[Key(0x11),Key(0x43)],new(7,8,20),true),All,Ms(20));
        frame=timeline.Capture(2,20,Ms(20));
        check("queue overflow cannot clear a known-password suppression gate",frame.KeyboardSuppressed&&frame.PressedKeys.Count==0&&frame.RecentChords.Count==0&&frame.CursorScreen==new PointD(7,8));
        timeline.SetKeyboardSuppressed(false);
        check("leaving protected input does not replay keys held during suppression",timeline.Capture(3,21,Ms(20)).PressedKeys.Count==0);
    }

    private static void BrokerLifecycle(Dispatcher dispatcher,Action<string,bool> check)
    {
        var factory=new FakeFactory();var clock=new FakeClock();int changes=0;var context=new object();
        using var broker=new PresentationRuntimeBroker(dispatcher,()=>changes++,factory.Start,()=>clock.Time);
        check("broker begins disarmed with no input source",!broker.Armed&&!broker.Observing&&factory.Requests.Count==0);
        broker.Configure(PresentationRuntimeDemand.Empty,true,context,"paused");broker.Toggle();
        check("arming without visible demand never starts a source",broker.Armed&&!broker.Observing&&factory.Requests.Count==0&&broker.StatusKey=="Presentation.Status.NoLayers");
        broker.Configure(All,false,context,"Presentation.Status.PausedEditing");
        check("editing gate is closed before any source start",factory.Requests.Count==0&&broker.Armed&&!broker.Observing);
        broker.Configure(All,true,context,"paused");
        check("one allowed demand starts exactly one application input source",broker.Observing&&factory.Requests.Count==1);
        var source=factory.Requests[0].Source;
        source.Queue(cursor:new(30,40,10));clock.Time=Ms(10);Pump(dispatcher);
        source.Queue(cursor:new(40,50,20));clock.Time=Ms(20);Pump(dispatcher);
        check("broker publishes bounded trail frames without source edits",broker.Frame.Trail.Count==2&&broker.Frame.CursorScreen==new PointD(40,50));
        var lifetime=broker.Lifetime;broker.Configure(All,false,context,"Presentation.Status.PausedEditing");
        check("view-to-edit pause closes source and clears active frame before retaining readonly trail",!broker.Observing&&source.StopCount>0&&source.DisposeCount>0&&broker.Frame.PressedKeys.Count==0&&broker.Frame.Trail.Count==0&&broker.PausedTrail?.Trail.Count==2&&!ReferenceEquals(lifetime,broker.Lifetime));
        var paused=broker.PausedTrail;source.Queue(cursor:new(999,999,21));Pump(dispatcher);
        check("stopped source cannot change the retained pause snapshot",ReferenceEquals(paused,broker.PausedTrail)&&paused!.Trail[^1].ScreenPoint==new PointD(40,50));
        broker.Configure(All,true,context,"paused");
        check("resume starts a fresh source and discards the paused trail",broker.Observing&&factory.Requests.Count==2&&broker.PausedTrail is null);
        check("old stop and dispose precede the next registration",factory.Events.IndexOf("stop:"+source.Epoch)<factory.Events.LastIndexOf("start:"+factory.Requests[1].Epoch)&&factory.Events.IndexOf("dispose:"+source.Epoch)<factory.Events.LastIndexOf("start:"+factory.Requests[1].Epoch));
        broker.Configure(All,true,context,"paused");
        check("repeated stable demand does not create duplicate input sources",factory.Requests.Count==2);
        broker.Configure(All with{Keyboard=false,AllPhysicalKeys=false},true,context,"paused");
        check("registration-role changes stop the old source before starting one pointer-only source",factory.Requests.Count==3&&!factory.Requests[2].Keyboard&&factory.Requests[2].Pointer&&factory.Requests[1].Source.StopCount>0);
        factory.Requests[2].Source.Queue([Key(0x41,time:20)],new(80,90,20));Pump(dispatcher);
        check("pointer-only demand never accepts key packets from its source",broker.Frame.PressedKeys.Count==0&&broker.Frame.CursorScreen==new PointD(80,90));
        broker.Configure(PresentationRuntimeDemand.Empty,true,context,"paused");
        check("last subscriber removal closes the sole source and clears pause state",!broker.Observing&&broker.PausedTrail is null&&factory.Requests[2].Source.StopCount>0);
    }

    private static void RegistrationRoles(Dispatcher dispatcher,Action<string,bool> check)
    {
        var factory=new FakeFactory();var clock=new FakeClock();var context=new object();
        var keyboardOnly=new PresentationRuntimeDemand(true,false,false,false,false,900,450,800);
        var both=keyboardOnly with { Pointer=true, Clicks=true, Trail=true };
        using var broker=new PresentationRuntimeBroker(dispatcher,()=>{},factory.Start,()=>clock.Time);
        broker.Configure(keyboardOnly,true,context,"paused");broker.Toggle();
        check("a pure keyboard demand starts only the keyboard role",
            factory.Requests.Single() is { Keyboard:true, Pointer:false });
        var animation=(DispatcherTimer)typeof(PresentationRuntimeBroker)
            .GetField("animation",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .GetValue(broker)!;
        factory.Requests[0].Source.Queue([Key(0x70,time:0)]);Pump(dispatcher);
        check("a steadily held key does not keep a 30 FPS visual timer awake",!animation.IsEnabled);
        factory.Requests[0].Source.Queue([Key(0x70,false,10)]);clock.Time=Ms(10);Pump(dispatcher);
        check("releasing a held key starts its bounded release animation",animation.IsEnabled);
        clock.Time=Ms(1000);PumpUntil(dispatcher,()=>!animation.IsEnabled);
        check("the visual timer stops after release animation expiry",!animation.IsEnabled);
        broker.Configure(both,true,context,"paused");
        check("enabling pointer demand restarts registration with both roles",
            factory.Requests.Count==2&&factory.Requests[1] is { Keyboard:true, Pointer:true }&&
            factory.Requests[0].Source.StopCount>0);
        broker.Configure(both with { Keyboard=false },true,context,"paused");
        check("disabling keyboard demand leaves one pointer-only role",
            factory.Requests.Count==3&&factory.Requests[2] is { Keyboard:false, Pointer:true }&&
            factory.Requests[1].Source.StopCount>0);
    }

    private static void LateStarts(Dispatcher dispatcher,Action<string,bool> check)
    {
        var factory=new FakeFactory(false);var clock=new FakeClock();int changes=0;var a=new object();var b=new object();
        var broker=new PresentationRuntimeBroker(dispatcher,()=>changes++,factory.Start,()=>clock.Time);
        try
        {
            broker.Configure(All,true,a,"paused");broker.Toggle();var firstLifetime=broker.Lifetime;
            broker.Configure(All,true,b,"paused");broker.Configure(All,true,a,"paused");
            check("A-B-A identities produce distinct pending lifetimes and cancel old starts",factory.Requests.Count==3&&!ReferenceEquals(firstLifetime,broker.Lifetime)&&factory.Requests[0].Token.IsCancellationRequested&&factory.Requests[1].Token.IsCancellationRequested);
            factory.Complete(0);factory.Complete(1);PumpUntil(dispatcher,()=>factory.Requests[0].Source.DisposeCount>0&&factory.Requests[1].Source.DisposeCount>0);
            check("late successful old starts stop instead of reviving either ABA owner",!broker.Observing&&factory.Requests.Take(2).All(request=>request.Source.StopCount>0));
            factory.Complete(2);PumpUntil(dispatcher,()=>broker.Observing);
            check("only the newest pending identity can publish an active source",broker.Observing&&factory.Requests[2].Source.StopCount==0);
        }
        finally{broker.Dispose();}
        factory=new(false);changes=0;broker=new(dispatcher,()=>changes++,factory.Start,()=>clock.Time);
        broker.Configure(All,true,new object(),"paused");broker.Toggle();broker.Pause(false);int afterPause=changes;
        factory.Complete(0);PumpUntil(dispatcher,()=>factory.Requests[0].Source.DisposeCount>0);
        check("a successful start completing after pause cannot publish or reopen observation",!broker.Observing&&changes==afterPause&&factory.Requests[0].Source.StopCount>0);broker.Dispose();
        factory=new(false);changes=0;broker=new(dispatcher,()=>changes++,factory.Start,()=>clock.Time);
        broker.Configure(All,true,new object(),"paused");broker.Toggle();broker.Dispose();int afterDispose=changes;
        factory.Complete(0);PumpUntil(dispatcher,()=>factory.Requests[0].Source.DisposeCount>0);
        check("dispose rejects late starts without publishing callbacks",!broker.Armed&&!broker.Observing&&changes==afterDispose);
    }

    private static void NotificationsAndSourceIsolation(Dispatcher dispatcher,Action<string,bool> check)
    {
        var session=new EditorSession(new(){Elements=[new(){Kind=ElementKind.Presentation,Width=300,Height=180,Presentation=new()}]});
        string fingerprint=JsonSerializer.Serialize(session.Document);long revision=session.SceneRevision,content=session.ContentVersion,bytes=session.HistoryBytes;
        int sourceChanges=0;session.Changed+=(_,_)=>sourceChanges++;
        var factory=new FakeFactory();var clock=new FakeClock{Time=Ms(1000)};int callbacks=0,wrongThread=0;
        int thread=Environment.CurrentManagedThreadId;
        var broker=new PresentationRuntimeBroker(dispatcher,()=>{callbacks++;if(Environment.CurrentManagedThreadId!=thread)wrongThread++;},factory.Start,()=>clock.Time);
        try
        {
            broker.Configure(All,true,new object(),"paused");broker.Toggle();var source=factory.Requests.Single().Source;
            int drain=source.DrainCount,beforeCallbacks=callbacks;long sequence=broker.Frame.Sequence;
            for(int i=0;i<8000;i++)source.Queue(cursor:new(i,-40,1000));
            Pump(dispatcher);
            check("8000 source notifications coalesce to one Dispatcher drain and latest movement",source.DrainCount==drain+1&&broker.Frame.CursorScreen==new PointD(7999,-40));
            check("one coalesced batch publishes one immutable frame on the owning Dispatcher",callbacks==beforeCallbacks+1&&broker.Frame.Sequence==sequence+1&&wrongThread==0);
            source.Queue([Key(0x11,time:1000),Key(0x43,time:1000)],new(20,30,1000));Pump(dispatcher);
            var visible=broker.Frame;broker.SuppressKeyboard(true);source.Queue([Key(0x41,time:1000)],new(30,40,1000));Pump(dispatcher);
            check("broker password gate clears keyboard while retaining mouse modules",broker.Frame.KeyboardSuppressed&&broker.Frame.PressedKeys.Count==0&&broker.Frame.CursorScreen==new PointD(30,40));
            long protectedEpoch=broker.Frame.Epoch;source.Queue([Key(0x11,time:1000),Key(0x43,time:1000)],new(35,45,1000),overflow:true);Pump(dispatcher);
            check("broker overflow starts a new epoch without clearing current password protection",broker.Frame.Epoch>protectedEpoch&&broker.Frame.KeyboardSuppressed&&broker.Frame.PressedKeys.Count==0&&broker.Frame.CursorScreen==new PointD(35,45));
            check("previously published frames remain immutable after later input and suppression",visible.PressedKeys.Count==2&&visible.CursorScreen==new PointD(20,30));
            clock.Time=Ms(7000);PumpUntil(dispatcher,()=>broker.Frame.Trail.Count==0);long expiredSequence=broker.Frame.Sequence;PumpFor(dispatcher,80);
            check("fading timers stop publishing once all transient trails expire",broker.Frame.Sequence==expiredSequence&&broker.Frame.Trail.Count==0);
            check("runtime ticks leave document fingerprint revision history assets and source events unchanged",JsonSerializer.Serialize(session.Document)==fingerprint&&session.SceneRevision==revision&&session.ContentVersion==content&&session.HistoryBytes==bytes&&session.UndoCount==0&&session.RedoCount==0&&session.Assets.Count==0&&sourceChanges==0);
            source.Queue(cursor:new(40,50,1000));int disposeCallbacks=callbacks;broker.Dispose();Pump(dispatcher);
            check("dispose drops an already queued Available callback without a final publish",callbacks==disposeCallbacks&&!broker.Observing&&source.StopCount>0&&source.DisposeCount>0);
        }
        finally{broker.Dispose();}
    }

    private static void BeforeInputPause(Dispatcher dispatcher,Action<string,bool> check)
    {
        var factory=new FakeFactory();var clock=new FakeClock();using var broker=new PresentationRuntimeBroker(dispatcher,()=>{},factory.Start,()=>clock.Time);
        broker.Configure(All,true,new object(),"paused");broker.Toggle();var source=factory.Requests.Single().Source;
        broker.BeforeInput+=_=>broker.Pause(false);
        source.Queue([Key(0x11),Key(0x43)],new(40,50,0));Pump(dispatcher);
        check("BeforeInput pause closes the source before the drained batch can publish keys or cursor",!broker.Observing&&source.StopCount>0&&broker.Frame.PressedKeys.Count==0&&broker.Frame.CursorScreen is null);
    }

    private static void NoAmbientContext(Dispatcher dispatcher,Action<string,bool> check)
    {
        var factory=new FakeFactory(false);var clock=new FakeClock();int owner=Environment.CurrentManagedThreadId,wrongThread=0;
        using var broker=new PresentationRuntimeBroker(dispatcher,()=>{if(Environment.CurrentManagedThreadId!=owner)wrongThread++;},factory.Start,()=>clock.Time);
        var previous=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            broker.Configure(All,true,new object(),"paused");broker.Toggle();
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
        Task.Run(()=>factory.Complete(0)).GetAwaiter().GetResult();
        PumpUntil(dispatcher,()=>broker.Observing||factory.Requests[0].Source.DisposeCount>0);
        check("explicit Dispatcher accepts a background factory completion without ambient SynchronizationContext",broker.Observing&&wrongThread==0&&factory.Requests[0].Source.DisposeCount==0);
    }

    private static void Pump(Dispatcher dispatcher)
    {
        var frame=new DispatcherFrame();dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Dispatcher dispatcher,Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("Synthetic presentation continuation did not complete.");Pump(dispatcher);Thread.Sleep(1);}Pump(dispatcher);
    }
    private static void PumpFor(Dispatcher dispatcher,int milliseconds)
    {var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Pump(dispatcher);Thread.Sleep(1);}}
    private sealed class FakeClock{internal TimeSpan Time;}
    private sealed class FakeFactory(bool immediate=true)
    {
        internal sealed record Request(long Epoch,bool Keyboard,bool Pointer,CancellationToken Token,FakeSource Source,TaskCompletionSource<IPresentationInputSource> Completion);
        internal readonly List<Request> Requests=[];internal readonly List<string> Events=[];
        internal Task<IPresentationInputSource> Start(long epoch,bool keyboard,bool pointer,CancellationToken token)
        {
            Events.Add("start:"+epoch);var source=new FakeSource(epoch,Events);var completion=new TaskCompletionSource<IPresentationInputSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new(epoch,keyboard,pointer,token,source,completion));if(immediate)completion.SetResult(source);return completion.Task;
        }
        internal void Complete(int index)=>Requests[index].Completion.SetResult(Requests[index].Source);
    }
    internal sealed class FakeSource(long epoch,List<string> events):IPresentationInputSource
    {
        private readonly List<PresentationObservedInput> pending=[];private PresentationObservedCursor? cursor;private bool overflowed;
        private readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Epoch=>epoch;public bool IsRunning{get;private set;}=true;public Task Completion=>completion.Task;
        public event Action? Available;internal int StopCount,DisposeCount,DrainCount;
        internal void Queue(IEnumerable<PresentationObservedInput>? changes=null,PresentationObservedCursor? cursor=null,bool overflow=false)
        {
            if(!IsRunning)return;if(changes is not null)pending.AddRange(changes);if(cursor is not null)this.cursor=cursor;overflowed|=overflow;Available?.Invoke();
        }
        public PresentationObservedBatch Drain(){DrainCount++;var result=new PresentationObservedBatch(Epoch,pending,cursor,overflowed);pending.Clear();cursor=null;overflowed=false;return result;}
        public bool Stop(){StopCount++;IsRunning=false;events.Add("stop:"+Epoch);completion.TrySetResult();return true;}
        public void Dispose(){DisposeCount++;IsRunning=false;events.Add("dispose:"+Epoch);completion.TrySetResult();}
    }
}
