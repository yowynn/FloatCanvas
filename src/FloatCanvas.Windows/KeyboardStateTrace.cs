using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>Bounded, thread-local diagnostics; callers persist only on entry failure.</summary>
public static class KeyboardStateTrace
{
    public sealed record Sample(long Tick, uint Thread, string Source, long Lease, long Sequence,
        int[] BeforeDown, int[] AfterDown);
    [ThreadStatic] private static Queue<Sample>? samples;
    public static void RecordWrite(string source, long lease, long sequence, byte[] before, byte[] after)
    {
        var queue=samples??=new(); long now=Environment.TickCount64;
        while(queue.Count>0&&(queue.Count>=64||now-queue.Peek().Tick>10000))queue.Dequeue();
        queue.Enqueue(new(now,GetCurrentThreadId(),source,lease,sequence,Down(before),Down(after)));
    }
    public static void RecordBoundary(string source)
    {
        var state=new byte[256];
        if(GetKeyboardState(state))RecordWrite(source,0,0,state,state);
    }
    public static IReadOnlyList<Sample> Recent(IReadOnlyList<uint> blockers) => samples?
        .Where(s=>Environment.TickCount64-s.Tick<=10000)
        .Select(s=>s with { BeforeDown=s.BeforeDown.Where(k=>blockers.Contains((uint)k)).ToArray(),
            AfterDown=s.AfterDown.Where(k=>blockers.Contains((uint)k)).ToArray() }).ToArray()??[];
    private static int[] Down(byte[] state) => Enumerable.Range(1,Math.Min(256,state.Length)-1).Where(key=>(state[key]&0x80)!=0).ToArray();
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
