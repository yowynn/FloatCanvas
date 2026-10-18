using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class WgcSessionStateTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(!WgcDesktopSession.AcceptFrame(2, 3, 200, 100), "a new pixel timestamp cannot authorize the previous window exclusion configuration");
        check(WgcDesktopSession.AcceptFrame(3, 3, 100, 100), "the exact required exclusion iteration and clean boundary are accepted");
        check(!WgcDesktopSession.AcceptFrame(4, 3, 99, 100), "a later exclusion iteration cannot authorize pre-boundary pixels");
        check(!WgcDesktopSession.AcceptFrame(3, 3, 0, 0), "a zero WGC pixel timestamp is never published");
        check(WgcDesktopSession.AcceptFrame(4, 3, 201, 100), "a later completed exclusion iteration can provide current pixels");
        check(!WgcDesktopSession.AcceptFrame(3,3,200,100,200), "a repeated capture timestamp cannot republish an unchanged WGC frame");
        check(!WgcDesktopSession.AcceptFrame(3,3,199,100,200), "draining a queued older frame cannot replace the latest accepted texture");
        check(!WgcDesktopSession.AcceptFrame(2,3,300,100,200), "a fresher queued frame still cannot bypass exclusion configuration");
        check(WgcDesktopSession.AcceptFrame(3,3,201,100,200), "the newest correctly configured pending frame replaces the cache");
        check(LiveCaptureCadence.ResolveRate([30])==30 && LiveCaptureCadence.ResolveRate([59])==59,
            "readback follows lower display rates instead of assuming every monitor is 60 Hz");
        check(LiveCaptureCadence.ResolveRate([144,240])==60 && LiveCaptureCadence.ResolveRate([0,1])==60,
            "fast and unspecified display rates retain the deliberate 60 FPS readback ceiling");
        check(LiveCaptureCadence.ResolveRate([30,60])==60 && LiveCaptureCadence.Interval(60)<=Stopwatch.Frequency/60+1,
            "mixed displays do not inherit the old 15 FPS sampling delay");
        check(WgcDesktopSession.ToQpc(10_000_000) == Stopwatch.Frequency, "WGC relative 100ns time converts to the process QPC clock");
        foreach (int hr in new[] { unchecked((int)0x80004002), unchecked((int)0x80040154), unchecked((int)0x80040111), unchecked((int)0x8000000F) })
            check(WgcDesktopSession.IsMissingCapability(new COMException("synthetic capability", hr)), "known absent WinRT classes or interfaces permit the explicit compatibility backend");
        foreach (int hr in new[] { unchecked((int)0x80070005), unchecked((int)0x887A0005), unchecked((int)0x80070057), unchecked((int)0x80004005) })
            check(!WgcDesktopSession.IsMissingCapability(new COMException("synthetic failure", hr)), "device policy and unexpected errors do not silently become capability absence");

        check(WgcWindowIds.IterableIid == new Guid("745698bf-22ad-5c0d-b0e0-07d35a1c9719") &&
            WgcWindowIds.IteratorIid == new Guid("ba0a30a1-c082-5671-ac07-7aaa4f269670"), "WindowId generic COM identities match the published ABI accepted by the real session");
        nint list = WgcWindowIds.Create((nint)101, (nint)202), iterator = 0, queried = 0;
        try
        {
            var iid = WgcWindowIds.IterableIid;
            check(Marshal.QueryInterface(list, in iid, out queried) == 0 && queried == list, "the owned window collection supports its exact iterable interface");
            Marshal.Release(queried); queried = 0;
            check(Method<First>(list, 6)(list, out iterator) == 0, "the native window iterable returns an independently owned iterator");
            WgcWindowIds.Release(list); list = 0;
            check(Method<Current>(iterator, 6)(iterator, out ulong first) == 0 && first == 101, "an iterator retains immutable window IDs after its original collection is released");
            check(Method<HasCurrent>(iterator, 7)(iterator, out byte present) == 0 && present == 1, "the first retained iterator value is available");
            check(Method<MoveNext>(iterator, 8)(iterator, out present) == 0 && present == 1 &&
                Method<Current>(iterator, 6)(iterator, out ulong second) == 0 && second == 202, "moving an iterator preserves UInt64 WindowId ABI values");
            check(Method<MoveNext>(iterator, 8)(iterator, out present) == 0 && present == 0 &&
                Method<Current>(iterator, 6)(iterator, out _) == unchecked((int)0x8000000B), "the iterator ends without reading beyond its owned values");
        }
        finally { if (queried != 0) Marshal.Release(queried); WgcWindowIds.Release(iterator); WgcWindowIds.Release(list); }
        list = WgcWindowIds.Create(); iterator = 0;
        try
        {
            check(Method<First>(list, 6)(list, out iterator) == 0 && Method<HasCurrent>(iterator, 7)(iterator, out byte present) == 0 && present == 0,
                "clearing the exclusion list uses a valid empty iterable, not a null ABI object");
        }
        finally { WgcWindowIds.Release(iterator); WgcWindowIds.Release(list); }
        using var source = new LiveDesktopCapture(useSessionWindowExclusion: true);
        check(source.UsesSessionWindowExclusion && source.UpdateTargets([], [], 0) == 0 && !source.TryGetLatest(out _), "an idle private source reports its explicit mode without starting screen capture");
        source.Dispose();
        check(source.Completion.Wait(TimeSpan.FromSeconds(2)), "private idle worker completes its MTA runtime and resource cleanup");

        nint handler=0, retained=0;int notifications=0;
        try
        {
            handler=WgcFrameArrival.Create(()=>Interlocked.Increment(ref notifications));
            var iid=WgcFrameArrival.HandlerIid;
            check(Marshal.QueryInterface(handler,in iid,out retained)==0 && retained==handler,
                "the FrameArrived delegate exposes the parameterized WinRT handler ABI");
            check(Method<Arrival>(retained,3)(retained,0,0)==0 && notifications==1,
                "native FrameArrived signals its worker without an application dispatcher");
            WgcFrameArrival.Detach(handler);WgcFrameArrival.Release(handler);handler=0;
            check(Method<Arrival>(retained,3)(retained,0,0)==0 && notifications==1,
                "a queued COM notification retained past detach cannot call the removed worker");
            var unrelated=Guid.NewGuid();int hr=Marshal.QueryInterface(retained,in unrelated,out nint unsupported);
            check(hr==unchecked((int)0x80004002) && unsupported==0,
                "the native event delegate does not pretend to implement unrelated interfaces");
        }
        finally {WgcFrameArrival.Release(handler);WgcFrameArrival.Release(retained);}
        using(var idle=new LiveDesktopCapture(useSessionWindowExclusion:true))
        {
            var signal=typeof(LiveDesktopCapture).GetMethod("SignalFrameAvailable",BindingFlags.Instance|BindingFlags.NonPublic)!.CreateDelegate<Action>(idle);
            handler=WgcFrameArrival.Create(signal);
            try
            {
                Method<Arrival>(handler,3)(handler,0,0);
                check(idle.FrameArrivalCount==1 && idle.SamplingPassCount==0,
                    "a frame notification with no targets cannot spin up sampling or desktop capture");
                idle.Dispose();check(idle.Completion.Wait(TimeSpan.FromSeconds(2)),"an idle notified worker shuts down normally");
                Method<Arrival>(handler,3)(handler,0,0);
                check(idle.FrameArrivalCount==1,"late native notification cannot signal an already disposed wake event");
            }
            finally {WgcFrameArrival.Detach(handler);WgcFrameArrival.Release(handler);handler=0;}
        }
    }
    private static T Method<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int First(nint instance, out nint iterator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Current(nint instance, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HasCurrent(nint instance, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MoveNext(nint instance, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Arrival(nint instance,nint sender,nint args);
}
