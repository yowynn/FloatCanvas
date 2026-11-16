using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;

internal static class PresentationPrivacyGateTests
{
    private static readonly PresentationRuntimeDemand Demand=new(true,true,true,true,true,900,450,800);
    internal static void Run(Action<string,bool> check)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            InactiveAndKnown(dispatcher,check);TimeoutAndLate(dispatcher,check);Foreground(dispatcher,check);
            SourceAndLifetime(dispatcher,check);PauseAndDispose(dispatcher,check);
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static void InactiveAndKnown(Dispatcher dispatcher,Action<string,bool> check)
    {
        using var rig=new Rig(dispatcher);
        rig.Gate.Refresh();rig.Gate.Observe();
        check("privacy gate without observing keyboard never queries metadata or foreground",rig.QueryCount==0&&rig.ForegroundReads==0&&rig.Sources.Count==0);
        rig.Start(Demand with{Keyboard=false});rig.Gate.Refresh();rig.Gate.Observe();
        check("pointer-only runtime does not start a password query",rig.Runtime.Observing&&!rig.Runtime.ObservingKeyboard&&rig.QueryCount==0);
        rig.Runtime.Configure(Demand,true,rig.Identity,"paused");var plan=rig.Plan(true);
        rig.Gate.Refresh();
        check("privacy Refresh arms its timer without immediately querying a provider",rig.QueryCount==0);
        rig.Gate.Observe();Wait(()=>plan.Entered.IsSet);
        check("explicit Observe starts one injected MTA metadata query",rig.QueryCount==1&&rig.QueryApartments.All(apartment=>apartment==ApartmentState.MTA));
        rig.Source.Queue([new(PresentationObservedKind.KeyDown,0,1,0x11,29,0,"Ctrl"),new(PresentationObservedKind.KeyDown,0,1,0x43,46,0,"C")]);Pump(dispatcher);
        check("unknown metadata retains the explicitly selected physical-key policy",!rig.Runtime.Frame.KeyboardSuppressed&&rig.Runtime.Frame.PressedKeys.Count==2);
        plan.Release.Set();rig.AwaitCallbacks(1);
        check("known password metadata clears held keys and protects the active frame",rig.Runtime.Frame.KeyboardSuppressed&&rig.Runtime.Frame.PressedKeys.Count==0);
        var unknown=rig.Plan(null);rig.Now=250;rig.Gate.Observe();Wait(()=>unknown.Entered.IsSet);unknown.Release.Set();rig.AwaitCallbacks(2);
        check("unknown metadata can restore the selected policy without replaying protected keys",!rig.Runtime.Frame.KeyboardSuppressed&&rig.Runtime.Frame.PressedKeys.Count==0);
        check("one privacy gate reuses a single probe worker across queries",rig.ProbeCount==1&&rig.QueryThreads.Distinct().Count()==1);
    }
    private static void TimeoutAndLate(Dispatcher dispatcher,Action<string,bool> check)
    {
        using var rig=new Rig(dispatcher);rig.Start();var first=rig.Plan(true);rig.Gate.Observe();Wait(()=>first.Entered.IsSet);first.Release.Set();rig.AwaitCallbacks(1);
        check("timeout fixture begins with confirmed password protection",rig.Runtime.Frame.KeyboardSuppressed);
        rig.Now=249;rig.Gate.Observe();check("completed metadata queries are rate limited below250ms",rig.QueryCount==1);
        var stalled=rig.Plan(true);rig.Now=250;rig.Gate.Observe();Wait(()=>stalled.Entered.IsSet);
        rig.Now=449;rig.Gate.Observe();check("pending query keeps its existing policy before200ms deadline",rig.Runtime.Frame.KeyboardSuppressed&&rig.QueryCount==2);
        rig.Now=450;rig.Gate.Observe();
        check("200ms timeout enters unknown policy without waiting for a stalled provider",!rig.Runtime.Frame.KeyboardSuppressed&&rig.QueryCount==2);
        rig.Now=499;rig.Gate.Observe();check("deadline handling does not exceed the250ms request interval",rig.QueryCount==2);
        var fresh=rig.Plan(true);rig.Now=500;rig.Gate.Observe();
        check("a stalled provider cannot create parallel query workers",rig.QueryCount==2&&rig.QueryThreads.Distinct().Count()==1);
        stalled.Release.Set();Wait(()=>fresh.Entered.IsSet);rig.AwaitCallbacks(2);
        check("late timed-out password result cannot revive its old request",!rig.Runtime.Frame.KeyboardSuppressed);
        fresh.Release.Set();rig.AwaitCallbacks(3);
        check("the current post-timeout query can establish password protection",rig.Runtime.Frame.KeyboardSuppressed);
    }
    private static void Foreground(Dispatcher dispatcher,Action<string,bool> check)
    {
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var first=rig.Plan(false);rig.Gate.Observe();Wait(()=>first.Entered.IsSet);first.Release.Set();rig.AwaitCallbacks(1);
            rig.Source.Queue([new(PresentationObservedKind.KeyDown,0,1,0x11,29,0,"Ctrl"),new(PresentationObservedKind.KeyDown,0,1,0x43,46,0,"C")]);Pump(dispatcher);
            check("foreground fixture has visible held key state before switching",rig.Runtime.Frame.PressedKeys.Count==2);
            var next=rig.Plan(null);rig.Foreground=202;rig.Gate.Observe();Wait(()=>next.Entered.IsSet);
            check("foreground change preserves the global physical stream while new privacy metadata is pending",rig.Runtime.Frame.PressedKeys.Count==2&&!rig.Runtime.Frame.KeyboardSuppressed);
            next.Release.Set();rig.AwaitCallbacks(2);
            var password=rig.Plan(true);rig.Now=250;rig.Gate.Observe();Wait(()=>password.Entered.IsSet);password.Release.Set();rig.AwaitCallbacks(3);
            check("confirmed password metadata still clears global held state after a foreground change",rig.Runtime.Frame.PressedKeys.Count==0&&rig.Runtime.Frame.KeyboardSuppressed);
        }
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var old=rig.Plan(true);rig.Gate.Observe();Wait(()=>old.Entered.IsSet);
            rig.Foreground=202;rig.Gate.Observe();var latest=rig.Plan(false);rig.Foreground=101;rig.Gate.Observe();
            old.Release.Set();Wait(()=>latest.Entered.IsSet);rig.AwaitCallbacks(1);
            check("foreground A-B-A cannot accept the first A request after identity returns",!rig.Runtime.Frame.KeyboardSuppressed);
            latest.Release.Set();rig.AwaitCallbacks(2);check("latest foreground A request remains independently usable",!rig.Runtime.Frame.KeyboardSuppressed&&rig.QueryCount==2);
        }
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var result=rig.Plan(true);rig.Gate.Observe();Wait(()=>result.Entered.IsSet);result.Release.Set();Wait(()=>rig.CallbackCount==1);
            rig.Foreground=202;Pump(dispatcher);
            check("queued result rechecks foreground on the UI Dispatcher before acceptance",!rig.Runtime.Frame.KeyboardSuppressed);
        }
    }
    private static void SourceAndLifetime(Dispatcher dispatcher,Action<string,bool> check)
    {
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var old=rig.Plan(true);rig.Gate.Observe();Wait(()=>old.Entered.IsSet);long source=rig.Runtime.InputEpoch;
            rig.Identity=new();rig.Runtime.Configure(Demand,true,rig.Identity,"paused");var latest=rig.Plan(true);rig.Gate.Refresh();rig.Gate.Observe();
            old.Release.Set();Wait(()=>latest.Entered.IsSet);rig.AwaitCallbacks(1);
            check("metadata from a previous source epoch cannot protect a restarted observer",rig.Runtime.InputEpoch!=source&&!rig.Runtime.Frame.KeyboardSuppressed&&rig.Sources[0].StopCount>0);
            latest.Release.Set();rig.AwaitCallbacks(2);
            check("new source metadata can protect without recreating the Desktop-lifetime probe",rig.Runtime.Frame.KeyboardSuppressed&&rig.ProbeCount==1);
        }
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var old=rig.Plan(true);rig.Gate.Observe();Wait(()=>old.Entered.IsSet);long source=rig.Runtime.InputEpoch;
            rig.Runtime.ClearKeyboardForFocusChange();old.Release.Set();rig.AwaitCallbacks(1);
            check("same-source callback with an obsolete runtime lifetime is rejected",rig.Runtime.InputEpoch==source&&!rig.Runtime.Frame.KeyboardSuppressed);
        }
    }
    private static void PauseAndDispose(Dispatcher dispatcher,Action<string,bool> check)
    {
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var pending=rig.Plan(true);rig.Gate.Observe();Wait(()=>pending.Entered.IsSet);
            rig.Runtime.Pause(false);rig.Gate.Refresh();pending.Release.Set();rig.AwaitCallbacks(1);
            check("paused runtime rejects a pending metadata completion without reopening observation",!rig.Runtime.Observing&&!rig.Runtime.Frame.KeyboardSuppressed);
            int count=rig.QueryCount;rig.Now=1000;PumpFor(dispatcher,80);
            check("paused privacy gate stops its query timer",rig.QueryCount==count);
            var resumed=rig.Plan(true);rig.Runtime.Configure(Demand,true,rig.Identity,"paused");rig.Gate.Refresh();rig.Gate.Observe();Wait(()=>resumed.Entered.IsSet);resumed.Release.Set();rig.AwaitCallbacks(2);
            check("resume reuses the gate but requires a fresh source metadata result",rig.Runtime.Observing&&rig.Runtime.Frame.KeyboardSuppressed&&rig.ProbeCount==1);
        }
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var blocked=rig.Plan(true);rig.Gate.Observe();Wait(()=>blocked.Entered.IsSet);rig.Gate.Dispose();
            check("privacy disposal returns while its synthetic provider is still blocked",!blocked.Release.IsSet&&!rig.Probe!.Completed.IsCompleted);
            blocked.Release.Set();Wait(()=>rig.Probe!.Completed.IsCompleted);Pump(dispatcher);
            check("disposed blocked probe terminates without publishing its late result",rig.CallbackCount==0&&!rig.Runtime.Frame.KeyboardSuppressed);
        }
        using(var rig=new Rig(dispatcher))
        {
            rig.Start();var result=rig.Plan(true);rig.Gate.Observe();Wait(()=>result.Entered.IsSet);result.Release.Set();Wait(()=>rig.CallbackCount==1);
            rig.Gate.Dispose();Pump(dispatcher);
            check("disposed gate rejects a result already queued on its Dispatcher",!rig.Runtime.Frame.KeyboardSuppressed);
        }
    }
    private static void Pump(Dispatcher dispatcher)
    {var frame=new DispatcherFrame();dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static void Wait(Func<bool> condition)
    {var clock=Stopwatch.StartNew();while(!condition()){if(clock.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("Synthetic metadata query timed out.");Thread.Sleep(1);}}
    private static void PumpFor(Dispatcher dispatcher,int milliseconds)
    {var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){Pump(dispatcher);Thread.Sleep(1);}}
    private sealed class QueryPlan(bool? answer)
    {internal readonly ManualResetEventSlim Entered=new();internal readonly ManualResetEventSlim Release=new();internal bool? Answer=>answer;}
    private sealed class Rig:IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly ConcurrentQueue<QueryPlan> plans=new();private readonly List<QueryPlan> allPlans=[];
        private int foreground=101,foregroundReads,queries,callbacks;private long now;
        internal readonly List<PresentationRuntimeTests.FakeSource> Sources=[];
        internal readonly ConcurrentQueue<int> QueryThreads=new();internal readonly ConcurrentQueue<ApartmentState> QueryApartments=new();
        internal object Identity=new();internal PresentationRuntimeBroker Runtime{get;}internal PresentationKeyboardPrivacyGate Gate{get;}
        internal PresentationKeyboardPrivacyProbe? Probe;internal int ProbeCount;
        internal int Foreground{get=>Volatile.Read(ref foreground);set=>Volatile.Write(ref foreground,value);}
        internal long Now{get=>Volatile.Read(ref now);set=>Volatile.Write(ref now,value);}
        internal int ForegroundReads=>Volatile.Read(ref foregroundReads);internal int QueryCount=>Volatile.Read(ref queries);internal int CallbackCount=>Volatile.Read(ref callbacks);
        internal PresentationRuntimeTests.FakeSource Source=>Sources[^1];
        internal Rig(Dispatcher dispatcher)
        {
            this.dispatcher=dispatcher;
            Runtime=new(dispatcher,()=>{},(epoch,keyboard,pointer,token)=>
            {var source=new PresentationRuntimeTests.FakeSource(epoch,[]);Sources.Add(source);return Task.FromResult<IPresentationInputSource>(source);},()=>TimeSpan.FromMilliseconds(Now));
            Gate=new(dispatcher,Runtime,completed=>
            {
                ProbeCount++;return Probe=new((id,window,password)=>{completed(id,window,password);Interlocked.Increment(ref callbacks);},Query,ReadForeground);
            },ReadForeground,()=>Now);
        }
        private nint ReadForeground(){Interlocked.Increment(ref foregroundReads);return Foreground;}
        private bool? Query()
        {
            Interlocked.Increment(ref queries);QueryThreads.Enqueue(Environment.CurrentManagedThreadId);QueryApartments.Enqueue(Thread.CurrentThread.GetApartmentState());
            if(!plans.TryDequeue(out var plan))return null;plan.Entered.Set();if(!plan.Release.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException("Synthetic query release missing.");return plan.Answer;
        }
        internal QueryPlan Plan(bool? answer){var plan=new QueryPlan(answer);allPlans.Add(plan);plans.Enqueue(plan);return plan;}
        internal void Start(PresentationRuntimeDemand? demand=null){Runtime.Configure(demand??Demand,true,Identity,"paused");if(!Runtime.Armed)Runtime.Toggle();}
        internal void AwaitCallbacks(int count){Wait(()=>CallbackCount>=count);Pump(dispatcher);}
        public void Dispose()
        {
            Gate.Dispose();Runtime.Dispose();foreach(var plan in allPlans)plan.Release.Set();Wait(()=>Probe!.Completed.IsCompleted);Pump(dispatcher);
        }
    }
}
