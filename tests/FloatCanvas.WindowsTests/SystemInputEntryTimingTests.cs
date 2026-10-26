using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class SystemInputEntryTimingTests
{
    internal static void Run(Action<bool,string> check)
    {
        var timing=new SystemInputEntryTiming();timing.Begin();
        check(!timing.TimedOut(true,100)&&!timing.TimedOut(true,2099),"Native key release waiting does not consume the separate ACK budget");
        check(!timing.TimedOut(false,2100),"Key release starts a fresh two-second ACK budget");
        check(!timing.TimedOut(false,4000)&&!timing.TimedOut(false,4099),"Repeated ACK polls cannot shorten or renew the fixed budget");
        check(timing.TimedOut(false,4100)&&timing.TimedOut(false,4200),"Missing ACK expires at a fixed deadline");
        timing.Begin();
        check(!timing.TimedOut(false,9000)&&!timing.TimedOut(false,10999),"A retry or native-dialog return receives a new ACK budget");
        check(timing.TimedOut(false,11000),"The new generation still has a bounded ACK deadline");
        timing.Begin();
        check(!timing.TimedOut(true,50000)&&!timing.TimedOut(false,60000)&&!timing.TimedOut(false,61999)&&timing.TimedOut(false,62000),
            "Delayed message pumping grants a full ACK budget after native key waiting ends");
    }
}
