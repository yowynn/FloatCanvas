using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private CheckBox AddRenderSettings(Panel panel)
    {
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.Rendering"), Margin = new Thickness(0, 18, 0, 6) });
        var preferGpu = new CheckBox { Content = L.Get("Settings.PreferGpuAcceleration"),
            IsChecked = _settings.PreferGpuAcceleration, MinHeight = 28 };
        AutomationProperties.SetName(preferGpu, L.Get("Settings.PreferGpuAcceleration"));
        panel.Children.Add(preferGpu);
        var hint = new TextBlock { Text = L.Get("Settings.GpuAccelerationHint"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 6) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        panel.Children.Add(hint);
        var backend = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(backend);
        var reason = new TextBlock { Margin = new Thickness(0, 3, 0, 6), TextWrapping = TextWrapping.Wrap };
        reason.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        panel.Children.Add(reason);
        var device = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        device.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        panel.Children.Add(device);
        var timing = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        timing.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        panel.Children.Add(timing);
        var refresh = new DispatcherTimer(DispatcherPriority.Background, backend.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        bool observing = false;
        void RefreshBackend()
        {
            if (_disposed || backend.Dispatcher.HasShutdownStarted) { refresh.Stop(); return; }
            if (!backend.Dispatcher.CheckAccess()) { _ = backend.Dispatcher.InvokeAsync(RefreshBackend); return; }
            bool hasFrame = _host.GpuFrameCount + _host.CpuFrameCount > 0;
            backend.Text = hasFrame ? L.Format("Settings.RenderBackend", _host.LastCompletedRenderBackend == OverlayRenderBackend.Gpu ? "GPU" : "CPU") :
                L.Get("Settings.RenderBackendPending");
            string reasonKey = !hasFrame ? "Settings.RenderReasonPending" :
                _host.LastCompletedRenderBackend == OverlayRenderBackend.Gpu ? "Settings.RenderReasonGpu" :
                !_settings.PreferGpuAcceleration ? "Settings.RenderReasonDisabled" :
                !_host.CurrentGpuWorkload ? "Settings.RenderReasonLight" :
                _host.LastGpuFailure is not null ? "Settings.RenderReasonFallback" : "Settings.RenderReasonPending";
            reason.Text = L.Get(reasonKey);
            string? details = _host.GpuDetails;
            device.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
            if (details is not null)
            {
                string[] parts = details.Split('/');
                device.Text = L.Format("Settings.RenderDevice", parts.Length > 1 ? parts[1].Trim() : details);
                device.ToolTip = details;
            }
            timing.Visibility = hasFrame ? Visibility.Visible : Visibility.Collapsed;
            if (hasFrame) timing.Text = L.Format("Settings.RenderTiming", _host.LastRenderMilliseconds, _host.GpuFrameCount, _host.CpuFrameCount);
        }
        refresh.Tick += (_, _) => RefreshBackend();
        RefreshBackend();
        preferGpu.Loaded += (_, _) =>
        {
            if (observing) return;
            observing = true; _host.RenderBackendChanged += RefreshBackend;
            RefreshBackend(); refresh.Start();
        };
        preferGpu.Unloaded += (_, _) =>
        {
            if (!observing) return;
            observing = false; refresh.Stop(); _host.RenderBackendChanged -= RefreshBackend;
        };
        return preferGpu;
    }
}
