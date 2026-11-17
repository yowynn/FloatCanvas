using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class KeyboardStateTraceTests
{
    internal static void Run(Action<string,bool> check,string output)
    {
        var before=new byte[256];var after=new byte[256];after[67]=after[65]=0x80;
        KeyboardStateTrace.RecordWrite("fixture",12,34,before,after);
        after[67]=0;
        var sample=KeyboardStateTrace.Recent([67]).Last();
        check("entry trace snapshots state without retaining mutable buffers",sample.AfterDown.SequenceEqual([67])&&sample.Lease==12&&sample.Sequence==34);
        check("entry failure trace omits unrelated key history",!sample.AfterDown.Contains(65));
        for(int i=0;i<80;++i)KeyboardStateTrace.RecordWrite("fixture",12,i,before,after);
        var ring=KeyboardStateTrace.Recent([67]);
        check("entry trace has a fixed memory bound and retains newest writes",ring.Count==64&&ring[^1].Sequence==79);
        int otherCount=-1;var thread=new Thread(()=>otherCount=KeyboardStateTrace.Recent([67]).Count);thread.Start();thread.Join();
        check("entry trace does not mix keyboard queues across threads",otherCount==0);
        var diagnostics=new ApplicationDiagnostics(Path.Combine(output,"entry-trace-fixture-"+Guid.NewGuid().ToString("N")));
        check("recording key state alone creates no log",!File.Exists(diagnostics.LogPath));
        check("entry failure diagnostics can persist bounded state evidence",diagnostics.Write("Input.EntryBlocked",new { Blockers=new[]{67}, StateWrites=ring }));
        using var json=JsonDocument.Parse(File.ReadLines(diagnostics.LogPath).Last());
        check("entry failure evidence survives normal log serialization",json.RootElement.GetProperty("Metadata").GetProperty("StateWrites").GetArrayLength()==64&&!json.RootElement.GetProperty("EntryTruncated").GetBoolean());
    }
}
