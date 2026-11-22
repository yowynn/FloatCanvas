using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>Reveals a WPF window only after it has rendered its current content.</summary>
/// <remarks>DWM retains the last surface a hidden window presented. Showing that HWND directly exposes
/// the retained frame until WPF renders state changed while the window was hidden. The window is shown
/// cloaked and uncloaked after it has completed frames, or after a bounded timeout.
/// Activation is not deferred: a cloaked HWND accepts focus, so input typed before its first current
/// frame reaches the window that requested it rather than the canvas behind it.</remarks>
internal static class WindowRevealGate
{
    internal const int RenderedFrames = 3;
    internal const int UncloakRetries = 5;
    internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    internal static Func<nint, bool, bool> Cloak { get; set; } = SetDwmCloak;
    /// <summary>Receives uncloak failures; the session writes them to its diagnostics log.</summary>
    internal static Action<string, object>? Diagnostic { get; set; }
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(RevealState), typeof(WindowRevealGate));

    internal static bool IsPending(Window window) => window.GetValue(StateProperty) is RevealState { IsPending: true };
    internal static bool IsConcealed(Window window) => window.GetValue(StateProperty) is RevealState { IsConcealed: true };
    /// <summary>The window is shown and DWM presents its pixels: not pending, concealed or stuck cloaked.</summary>
    internal static bool IsPresented(Window window) =>
        window.IsVisible && window.GetValue(StateProperty) is not RevealState { IsCloaked: true };

    /// <summary>Shows the window; <paramref name="revealed"/> runs once, in the same turn its pixels become visible.</summary>
    /// <param name="contentChanged">For a concealed window, its rendered content is not yet current.</param>
    internal static void Show(Window window, Action? revealed = null, bool contentChanged = false)
    {
        var state = StateOf(window);
        if (state.IsConcealed) state.Reveal(revealed, contentChanged);
        else if (window.IsVisible) { window.Show(); state.WhenPresented(revealed); }
        else state.ShowHidden(revealed);
    }

    /// <summary>Hides a visible window's pixels and input while WPF keeps rendering its current state.</summary>
    internal static bool Conceal(Window window) => window.IsVisible && StateOf(window).Conceal();

    /// <summary>Records that a concealed window's next reveal must wait for its new content to render.</summary>
    internal static void MarkChanged(Window window)
    {
        if (window.GetValue(StateProperty) is RevealState { IsConcealed: true } state) state.MarkChanged();
    }

    private static RevealState StateOf(Window window)
    {
        if (window.GetValue(StateProperty) is RevealState state) return state;
        state = new RevealState(window);
        window.SetValue(StateProperty, state);
        return state;
    }

    private sealed class RevealState
    {
        private readonly Window window;
        private nint handle;
        private bool cloaked;
        private int frames, retries;
        private DispatcherTimer? timer, retryTimer;
        private List<Action>? callbacks;
        private int changedFrames = RenderedFrames;
        internal bool IsPending => timer is not null || retryTimer is not null;
        internal bool IsConcealed { get; private set; }
        internal bool IsCloaked => cloaked;

        internal RevealState(Window window)
        {
            this.window = window;
            window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Cancel(); };
            window.Closed += (_, _) => Cancel();
        }

        internal void ShowHidden(Action? revealed)
        {
            handle = new WindowInteropHelper(window).EnsureHandle();
            if (!SetCloaked(true))
            {
                window.Show();
                if (!cloaked) revealed?.Invoke();
                else { AddCallback(revealed); Present(); }
                return;
            }
            Begin(revealed);
            try { window.Show(); }
            catch { Cancel(); throw; }
            if (!window.IsVisible) Cancel();
        }

        // A visible window left cloaked by a failed uncloak retries when it is shown again.
        internal void WhenPresented(Action? revealed)
        {
            if (IsPending) AddCallback(revealed);
            else if (cloaked) { AddCallback(revealed); Present(); }
            else revealed?.Invoke();
        }

        internal bool Conceal()
        {
            handle = new WindowInteropHelper(window).EnsureHandle();
            if (!cloaked && !SetCloaked(true)) return false;
            IsConcealed = true;
            StopWaiting(); StopRetry(); callbacks = null;
            return true;
        }

        internal void Reveal(Action? revealed, bool contentChanged)
        {
            IsConcealed = false;
            bool rendering = changedFrames < RenderedFrames;
            StopChangeTracking();
            if (contentChanged || rendering) { Begin(revealed); return; }
            AddCallback(revealed);
            Present();
        }

        internal void MarkChanged()
        {
            if (changedFrames >= RenderedFrames) CompositionTarget.Rendering += OnChangeRendered;
            changedFrames = 0;
        }

        private void OnChangeRendered(object? sender, EventArgs args)
        {
            if (++changedFrames >= RenderedFrames) CompositionTarget.Rendering -= OnChangeRendered;
        }

        private void StopChangeTracking()
        {
            if (changedFrames < RenderedFrames) CompositionTarget.Rendering -= OnChangeRendered;
            changedFrames = RenderedFrames;
        }

        private void Begin(Action? revealed)
        {
            AddCallback(revealed);
            if (timer is not null) return;
            frames = 0;
            timer = new DispatcherTimer(Timeout, DispatcherPriority.Render, (_, _) => Present(), window.Dispatcher);
            CompositionTarget.Rendering += OnRendering;
        }

        // Repeated requests in one reveal cycle neither restart its frame count nor run a callback twice.
        private void AddCallback(Action? revealed)
        {
            if (revealed is null) return;
            callbacks ??= [];
            if (!callbacks.Contains(revealed)) callbacks.Add(revealed);
        }

        // Rendering runs before the frame is committed. Frame N's handler proves
        // that the frames before it, including the newly shown content, were sent.
        private void OnRendering(object? sender, EventArgs args)
        {
            if (++frames >= RenderedFrames) Present();
        }

        private void Cancel()
        {
            IsConcealed = false;
            StopChangeTracking();
            StopWaiting(); StopRetry(); callbacks = null;
            if (cloaked) SetCloaked(false);
        }

        private void StopWaiting()
        {
            if (timer is null) return;
            timer.Stop(); timer = null;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void StopRetry()
        {
            retryTimer?.Stop(); retryTimer = null;
        }

        // Callbacks run only once DWM actually presents the window. A failed uncloak stays
        // pending and retries; after the retries it reports and waits for the next Show.
        private void Present()
        {
            StopWaiting();
            if (IsConcealed) return;
            if (cloaked && !SetCloaked(false)) { UncloakFailed(); return; }
            if (retryTimer is not null) { StopRetry(); Report("Window.UncloakRecovered"); }
            var completed = callbacks; callbacks = null;
            if (completed is not null) foreach (var callback in completed) callback();
        }

        private void UncloakFailed()
        {
            if (retryTimer is null)
            {
                retries = 0;
                Report("Window.UncloakFailed");
                retryTimer = new DispatcherTimer(RetryInterval, DispatcherPriority.Render, (_, _) => Present(), window.Dispatcher);
            }
            else if (++retries >= UncloakRetries) { StopRetry(); Report("Window.UncloakAbandoned"); }
        }

        private void Report(string name) =>
            Diagnostic?.Invoke(name, new { Window = window.GetType().Name, window.Title, Retries = retries, PendingCallbacks = callbacks?.Count ?? 0 });

        private bool SetCloaked(bool value)
        {
            if (handle == 0) return false;
            bool applied = Cloak(handle, value);
            if (applied) cloaked = value;
            return applied;
        }
    }

    private static bool SetDwmCloak(nint handle, bool cloak)
    {
        int value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(handle, 13, ref value, sizeof(int)) == 0; // DWMWA_CLOAK
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}
