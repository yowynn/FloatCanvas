using System.ComponentModel;
using System.Runtime.InteropServices;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private readonly Dictionary<nint,uint> _liveCaptureOriginalAffinities=[];
    /// <summary>
    /// Excludes only this host's display/input windows while continuous desktop effects are active.
    /// Must run on the creating UI thread, and be reapplied after monitor windows are rebuilt.
    /// A failed change throws; callers must stop capture instead of sampling overlay feedback.
    /// </summary>
    public void SetLiveCaptureExclusion(bool exclude)
    {
        CheckThread();
        if (!Capabilities.SupportsCaptureExclusion)
        {
            if (exclude) throw new PlatformNotSupportedException(L.Get("Capture.LiveExclusionUnsupported"));
            return;
        }
        var handles = _windows.SelectMany(pair => new[] { pair.Display.Handle, pair.Input.Handle }).Where(handle => handle != 0).Distinct().ToArray();
        var previous = new List<(nint Handle, uint Affinity)>();
        try
        {
            foreach (var handle in handles)
            {
                if (!NativeMethods.GetWindowDisplayAffinity(handle, out uint affinity))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionStateReadFailed"));
                previous.Add((handle, affinity));
                uint desired=exclude?0x11u:_liveCaptureOriginalAffinities.GetValueOrDefault(handle,affinity);
                if (affinity!=desired && !NativeMethods.SetWindowDisplayAffinity(handle, desired))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionFailed"));
            }
        }
        catch (Exception failure)
        {
            List<Exception> failures = [failure];
            foreach (var item in previous)
                if (!NativeMethods.SetWindowDisplayAffinity(item.Handle, item.Affinity))
                    failures.Add(new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Capture.ExclusionStateRestoreFailed")));
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
        foreach(var handle in _liveCaptureOriginalAffinities.Keys.Where(handle=>!handles.Contains(handle)).ToArray())
            _liveCaptureOriginalAffinities.Remove(handle);
        foreach(var item in previous)
            if(exclude)_liveCaptureOriginalAffinities.TryAdd(item.Handle,item.Affinity);
            else _liveCaptureOriginalAffinities.Remove(item.Handle);
    }
}
