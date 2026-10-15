using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

/// <summary>Synchronous, copy-only OLE negotiation. Data belongs to the source, not this object.</summary>
internal sealed class ImageDropProtocol(
    Func<bool> available,
    Func<IDataObject, bool> supports,
    Func<IDataObject, OverlayPoint, bool> drop,
    Action<Exception> reportError)
{
    internal const uint Copy = 1;
    internal static int PrimaryMouseVirtualKey(bool swapped) => swapped ? 2 : 1;
    private IDataObject? current;
    internal bool IsDragOver => current is not null;

    internal uint Enter(IDataObject data, uint allowed)
    {
        Leave();
        current = data;
        return Negotiate(data, allowed);
    }

    internal uint Over(uint allowed) => current is null ? 0 : Negotiate(current, allowed);

    internal uint Drop(IDataObject data, OverlayPoint point, uint allowed)
    {
        // Drop is no longer hovering, including during the synchronous borrowed-data callback.
        Leave();
        try
        {
            return (allowed & Copy) != 0 && available() && supports(data) && drop(data, point) ? Copy : 0;
        }
        catch (Exception error) { Report(error); return 0; }
    }

    internal void Leave() => current = null;

    private uint Negotiate(IDataObject data, uint allowed)
    {
        try { return (allowed & Copy) != 0 && available() && supports(data) ? Copy : 0; }
        catch (Exception error) { Leave(); Report(error); return 0; }
    }

    private void Report(Exception error)
    {
        // An application error subscriber must not unwind across an OLE COM callback either.
        try { reportError(error); } catch { }
    }
}

/// <summary>Balances only its own OLE initialization and registered HWNDs. The injectable calls are tested without native OLE.</summary>
internal sealed class ImageDropRegistrations<T> : IDisposable where T : class
{
    private readonly Action uninitialize;
    private readonly Func<nint, T, int> register;
    private readonly Func<nint, int> revoke;
    private readonly Action<T> clear;
    private readonly Action<Exception> reportError;
    private readonly Dictionary<nint, T> targets = [];
    private bool disposed;
    internal IReadOnlyCollection<T> Targets => targets.Values;

    internal ImageDropRegistrations(Func<int> initialize, Action uninitialize, Func<nint, T, int> register,
        Func<nint, int> revoke, Action<T> clear, Action<Exception> reportError)
    {
        int result = initialize();
        if (result < 0) throw new COMException(L.Format("ImageDropHost.InitializeFailed", $"0x{unchecked((uint)result):X8}"), result);
        // Both S_OK and S_FALSE increment our apartment initialization reference.
        this.uninitialize = uninitialize;
        this.register = register;
        this.revoke = revoke;
        this.clear = clear;
        this.reportError = reportError;
    }

    internal bool Contains(nint window) => targets.ContainsKey(window);

    internal void Add(nint window, T target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (window == 0 || targets.ContainsKey(window)) throw new InvalidOperationException(L.Get("ImageDropHost.InvalidRegistration"));
        targets.Add(window, target);
        try
        {
            int result = register(window, target);
            if (result < 0) throw new COMException(L.Format("ImageDropHost.RegisterFailed", $"0x{unchecked((uint)result):X8}"), result);
        }
        catch { targets.Remove(window); throw; }
    }

    internal void Remove(nint window)
    {
        if (!targets.Remove(window, out var target)) return;
        try { clear(target); }
        catch (Exception error) { Report(error); }
        try
        {
            int result = revoke(window);
            if (result < 0) Report(new COMException(L.Format("ImageDropHost.RevokeFailed", $"0x{unchecked((uint)result):X8}"), result));
        }
        catch (Exception error) { Report(error); }
    }

    internal void ClearHover()
    {
        foreach (var target in targets.Values) clear(target);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { foreach (var window in targets.Keys.ToArray()) Remove(window); }
        finally { uninitialize(); }
    }

    private void Report(Exception error) { try { reportError(error); } catch { } }
}
