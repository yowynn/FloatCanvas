using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.WindowsTests;

internal static class ImageDropProtocolTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(ImageDropProtocol.PrimaryMouseVirtualKey(false) == 1, "Default primary-button reads the physical left-button virtual key");
        check(ImageDropProtocol.PrimaryMouseVirtualKey(true) == 2, "Swapped Windows primary-button reads the physical right-button virtual key");
        bool available = true;
        int probes = 0, commits = 0;
        var errors = new List<Exception>();
        var supported = new Data { Supported = true };
        var unsupported = new Data();
        IDataObject? received = null;
        OverlayPoint point = default;
        ImageDropProtocol? protocol = null;
        protocol = new(() => available, data => { probes++; return ((Data)data).Supported; },
            (data, at) => { check(!protocol!.IsDragOver, "Drop clears hover before the synchronous application snapshot callback"); received = data; point = at; commits++; return true; }, errors.Add);
        check(!protocol.IsDragOver && protocol.Over(7) == 0, "DragOver before Enter cannot advertise a copy");
        check(protocol.Enter(supported, 7) == 1 && protocol.IsDragOver, "Supported Copy/Move/Link source is negotiated as Copy only");
        check(protocol.Over(2 | 4) == 0 && commits == 0, "Move-only or Link-only hover never causes a commit");
        check(protocol.Over(1 | 0x80000000) == 1, "Extra source flags never escape the Copy-only target");
        protocol.Leave();
        check(!protocol.IsDragOver && protocol.Over(1) == 0, "DragLeave releases borrowed hover state and rejects stale Over");
        check(protocol.Enter(unsupported, 1) == 0 && protocol.IsDragOver, "Unsupported external data is still recognized as an OLE hover until Leave/Drop");
        check(protocol.Drop(unsupported, new(-20, -30), 1) == 0 && commits == 0 && !protocol.IsDragOver,
            "Unsupported drop clears hover without modifying the application");
        protocol.Enter(supported, 1);
        available = false;
        int probesBefore = probes;
        check(protocol.Over(1) == 0 && probes == probesBefore, "Hidden/cancelled/busy availability gates feedback before format probes");
        check(protocol.Drop(supported, new(1, 2), 1) == 0 && commits == 0 && !protocol.IsDragOver,
            "Availability is rechecked at Drop rather than trusting previous Enter");
        available = true;
        protocol.Enter(supported, 1);
        check(protocol.Drop(supported, new(-2048, 171), 1) == 1 && commits == 1 && ReferenceEquals(received, supported) && point == new OverlayPoint(-2048, 171),
            "The same borrowed data and signed screen coordinates reach one synchronous callback without DPI rescaling");
        protocol.Enter(supported, 1);
        check(protocol.Drop(supported, new(100, 200), 2) == 0 && commits == 1 && !protocol.IsDragOver,
            "Drop cannot upgrade a source that no longer permits Copy");
        var rejecting = new ImageDropProtocol(() => true, _ => true, (_, _) => false, errors.Add);
        rejecting.Enter(supported, 1);
        check(rejecting.Drop(supported, default, 1) == 0 && !rejecting.IsDragOver, "Application rejection returns None and clears hover");
        var throwingProbe = new ImageDropProtocol(() => true, _ => throw new COMException("source probe failed"), (_, _) => true, errors.Add);
        var native = new NativeImageDropTarget(throwingProbe);
        uint effect = 1;
        check(native.DragEnter(supported, 0, new(-10, 40), ref effect) == 0 && effect == 0 && !native.IsDragOver && errors.Count == 1,
            "A COM/data-source probe exception becomes S_OK/None and never crosses the native callback");
        var throwingDrop = new ImageDropProtocol(() => true, _ => true, (_, _) => throw new InvalidOperationException("snapshot failed"), _ => throw new Exception("error subscriber failed"));
        native = new(throwingDrop); effect = 1;
        native.DragEnter(supported, 0, default, ref effect); effect = 1;
        check(native.Drop(supported, 0, new(-2000, -1500), ref effect) == 0 && effect == 0 && !native.IsDragOver,
            "Snapshot and error-subscriber exceptions cannot unwind through the native Drop boundary");
        native = new(protocol); effect = 1;
        check(native.DragEnter(supported, 0, default, ref effect) == 0 && effect == 1 && native.IsDragOver,
            "Native DragEnter preserves a successful protocol effect and hover state");
        effect = 3;
        check(native.DragOver(0, default, ref effect) == 0 && effect == 1, "Native DragOver only returns the copy effect");
        check(native.DragLeave() == 0 && !native.IsDragOver, "Native DragLeave is idempotent and clears hover");
        check(native.DragLeave() == 0 && !native.IsDragOver, "Repeated DragLeave does not retain stale COM data");
        effect = 1; native.DragEnter(supported, 0, default, ref effect); native.Clear();
        check(!native.IsDragOver, "Host cancellation can clear hover without changing mouse capture or invoking Drop");

        Registrations(check);
    }

    private static void Registrations(Action<bool, string> check)
    {
        int initializations = 0, uninitializations = 0, nativeRegisters = 0, nativeRevokes = 0;
        var order = new List<string>();
        var errors = new List<Exception>();
        ImageDropRegistrations<string> Create(int initializeResult = 0, Func<nint, string, int>? register = null, Func<nint, int>? revoke = null) => new(
            () => { initializations++; return initializeResult; },
            () => { uninitializations++; order.Add("uninitialize"); },
            register ?? ((window, _) => { nativeRegisters++; order.Add("register" + window); return 0; }),
            revoke ?? (window => { nativeRevokes++; order.Add("revoke" + window); return 0; }),
            target => order.Add("clear" + target), errors.Add);
        var registrations = Create(1); // S_FALSE must be balanced exactly like S_OK.
        check(initializations == 1 && uninitializations == 0 && nativeRegisters == 0,
            "OLE initialization is separate from HWND registration; an existing STA OLE reference is owned once");
        registrations.Add(11, "A"); registrations.Add(22, "B");
        check(registrations.Contains(11) && registrations.Contains(22) && registrations.Targets.Count == 2 && nativeRegisters == 2,
            "Each real input HWND owns one independent strongly-held registration");
        bool duplicate = false;
        try { registrations.Add(11, "other"); } catch (InvalidOperationException) { duplicate = true; }
        check(duplicate && nativeRegisters == 2, "Duplicate HWND rejection happens before a second native registration");
        bool nullWindow = false;
        try { registrations.Add(0, "invalid"); } catch (InvalidOperationException) { nullWindow = true; }
        check(nullWindow && nativeRegisters == 2, "A null HWND cannot be sent to RegisterDragDrop");
        registrations.ClearHover();
        check(nativeRevokes == 0 && registrations.Targets.Count == 2 && order.Contains("clearA") && order.Contains("clearB"),
            "Ending temporary reception clears data without prematurely revoking still-live input HWNDs");
        registrations.Remove(11);
        check(!registrations.Contains(11) && registrations.Contains(22) && nativeRevokes == 1 && uninitializations == 0,
            "Monitor rebuild revokes the removed HWND but keeps the host's STA reference");
        registrations.Remove(11);
        check(nativeRevokes == 1, "Unknown or already-removed HWND does not cause an unmatched revoke");
        registrations.Add(33, "C");
        registrations.Dispose(); registrations.Dispose();
        check(nativeRevokes == 3 && uninitializations == 1 && registrations.Targets.Count == 0,
            "Dispose revokes every remaining HWND and balances S_FALSE exactly once, even on repeated Dispose");
        check(order.IndexOf("clearB") < order.IndexOf("revoke22") && order.IndexOf("clearC") < order.IndexOf("revoke33") && order.Last() == "uninitialize",
            "Borrowed state clears before native revoke, and all revokes precede OleUninitialize");
        bool disposed = false;
        try { registrations.Add(44, "late"); } catch (ObjectDisposedException) { disposed = true; }
        check(disposed && nativeRegisters == 3, "A disposed OLE owner cannot register a late HWND");
        int beforeUninit = uninitializations;
        bool wrongApartment = false;
        try { using var failed = Create(unchecked((int)0x80010106)); }
        catch (COMException error) { wrongApartment = error.HResult == unchecked((int)0x80010106); }
        check(wrongApartment && uninitializations == beforeUninit,
            "RPC_E_CHANGED_MODE is reported and never balanced by an unmatched OleUninitialize");
        int rollbackRevokes = 0, rollbackUninit = 0;
        using (var partial = new ImageDropRegistrations<string>(() => 0, () => rollbackUninit++,
            (window, _) => window == 2 ? unchecked((int)0x80040101) : 0,
            _ => { rollbackRevokes++; return 0; }, _ => { }, errors.Add))
        {
            partial.Add(1, "first");
            bool failed = false;
            try { partial.Add(2, "rejected"); } catch (COMException) { failed = true; }
            check(failed && partial.Contains(1) && !partial.Contains(2) && partial.Targets.Count == 1,
                "A failed native registration keeps only earlier successful native ownership for rollback");
        }
        check(rollbackRevokes == 1 && rollbackUninit == 1, "Partial enabling failure can revoke earlier HWNDs and balance its OLE reference");
        int revokedDespiteError = 0, uninitDespiteError = 0;
        int beforeErrors = errors.Count;
        using (var partial = new ImageDropRegistrations<string>(() => 0, () => uninitDespiteError++, (_, _) => 0,
            _ => { revokedDespiteError++; return unchecked((int)0x80040100); }, _ => { }, errors.Add))
        { partial.Add(1, "one"); partial.Add(2, "two"); }
        check(revokedDespiteError == 2 && uninitDespiteError == 1 && errors.Count == beforeErrors + 2,
            "Revocation errors are reported while remaining HWNDs and the owned OLE reference still receive cleanup attempts");
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                string? message = null;
                try { using var failed = Create(unchecked((int)0x80010106)); }
                catch (COMException error) { message = error.Message; }
                check(message == L.Format("ImageDropHost.InitializeFailed", "0x80010106"),
                    language + " native initialization failures use the selected interface language and retain HRESULT");
                check(!string.IsNullOrWhiteSpace(L.Get("ImageDropHost.Unavailable")) && !string.IsNullOrWhiteSpace(L.Get("ImageDropHost.RequiresSta")) &&
                    !string.IsNullOrWhiteSpace(L.Get("ImageDropHost.ShowFailed")), language + " transient reception errors all have interface translations");
            }
        }
        finally { L.Initialize(preference); }
    }

    private sealed class Data : IDataObject
    {
        internal bool Supported;
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => throw new NotSupportedException();
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int QueryGetData(ref FORMATETC format) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) => throw new NotSupportedException();
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) => throw new NotSupportedException();
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? enumerator) => throw new NotSupportedException();
    }
}
