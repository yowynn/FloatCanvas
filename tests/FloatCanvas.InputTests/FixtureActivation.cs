using System.Diagnostics;
using System.IO;
using FloatCanvas.PlatformTests;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static async Task WaitForOwnFixtureActivation()
    {
        if (!KeyboardWaitForActivation) return;
        Console.WriteLine($"WAITING_OWN_INPUT_FIXTURE: pid={Environment.ProcessId}; hwnd={Lower!.Handle}; title=浮绘 · 自建输入验证窗口; timeout=30s");
        long activationStart = Stopwatch.GetTimestamp();
        while (Native.GetForegroundWindow() != Lower.Handle)
        {
            if (Stopwatch.GetElapsedTime(activationStart).TotalSeconds > 30)
                throw new InvalidOperationException("Own input fixture did not gain foreground within 30 seconds; no input injected.");
            await Task.Delay(60);
        }
        // The external runner acknowledges only after its supported activation/refresh completes.
        Directory.CreateDirectory(Profile!);
        string activationReady = Path.Combine(Profile!, "activation.ready");
        Console.WriteLine("WAITING_ACTIVATION_ACTION_COMPLETE: " + activationReady);
        while (!File.Exists(activationReady))
        {
            if (Stopwatch.GetElapsedTime(activationStart).TotalSeconds > 45)
                throw new InvalidOperationException("Activation completion was not acknowledged; no input injected.");
            await Task.Delay(60);
        }
        if (Native.GetForegroundWindow() != Lower.Handle)
            throw new InvalidOperationException("Own fixture lost foreground after activation acknowledgement; no input injected.");
    }
}
