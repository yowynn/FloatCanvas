using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class SymbolTraceWindow : Window
{
    private readonly SymbolTraceSource source;
    private readonly Canvas drawing = new() { UseLayoutRounding = false };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Padding = new(8, 4, 8, 4),
        Visibility = Visibility.Collapsed
    };
    private readonly NumericValueField opacity, detail;
    private readonly Button accept = new() { Content = L.Get("Dialog.OK"), IsEnabled = false };
    private readonly SemaphoreSlim traceWorker = new(1, 1);
    private CancellationTokenSource? pending;
    private readonly double previewScale;
    internal SymbolShape? Result { get; private set; }
    internal bool IsUpdating => pending is not null;

    internal SymbolTraceWindow(Window? owner, SymbolTraceSource source)
    {
        this.source = source;
        Owner = owner; Title = L.Get("Symbol.ExtractPreview"); Icon = AppIcon.WindowImage;
        Width = PropertyPanelLayout.WindowWidth; Height = 430; MinWidth = 340; MinHeight = 345;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Topmost = true;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true };
        cancel.MinWidth = accept.MinWidth = 78; cancel.Height = accept.Height = PropertyPanelLayout.ControlHeight;
        accept.Margin = new(8, 0, 0, 0);
        accept.Click += (_, _) => { if (Result is not null && !IsUpdating) SessionWindowCoordinator.SetModalResult(this, true); };
        AppInputPolicy.AttachFormSubmit(this, accept);
        actions.Children.Add(cancel); actions.Children.Add(accept); DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);

        var controls = new StackPanel { Margin = new(0, 8, 0, 0) };
        opacity = AddControl("Symbol.OpacityThreshold", "Symbol.OpacityThresholdHint", .1, SymbolTraceSource.DefaultOpacity, NumericPropertyKind.Opacity);
        detail = AddControl("Symbol.OutlineDetail", "Symbol.OutlineDetailHint", 0, SymbolTraceSource.DefaultDetail, NumericPropertyKind.Strength);
        DockPanel.SetDock(controls, Dock.Bottom); body.Children.Add(controls);

        previewScale = 256 / Math.Max(source.FrameWidth, source.FrameHeight);
        drawing.Width = source.FrameWidth * previewScale; drawing.Height = source.FrameHeight * previewScale;
        var view = new Viewbox { Child = drawing, Stretch = Stretch.Uniform, Margin = new(12) };
        var preview = new Grid { Background = ToolPresetGallery.PreviewBackground, ClipToBounds = true };
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText"); status.SetResourceReference(TextBlock.BackgroundProperty, "CanvasSurface");
        preview.Children.Add(view); preview.Children.Add(status); body.Children.Add(preview);
        Content = body;
        if (!SessionWindowCoordinator.IsEditingWindow(owner)) OverlayWindowScope.UseRegularWindow(this);
        AppWindowChrome.ApplyDialog(this);
        Loaded += (_, _) => Schedule(first: true);
        Closed += (_, _) => { pending?.Cancel(); pending = null; };

        NumericValueField AddControl(string key, string hint, double minimum, double initial, NumericPropertyKind kind)
        {
            var field = new NumericValueField(minimum, 100, minimum, 100, 1) { Unit = "%" };
            var caption = new PropertyLabel(L.Get(key)); field.SetLabel(caption); field.SetScrubAnchor(kind, L.Get(key));
            field.SetModelValue(initial); field.IsEnabled = source.IsRaster;
            string help = L.Get(source.IsRaster ? hint : "Symbol.ExactVector");
            field.ToolTip = help; ToolTipService.SetShowOnDisabled(field, true);
            AutomationProperties.SetHelpText(field, help);
            field.ValueChanged += _ => Schedule(); field.InvalidInput += () => Schedule();
            controls.Children.Add(PropertyPanelLayout.Row(caption, field));
            return field;
        }
    }

    private void Schedule(bool first = false)
    {
        pending?.Cancel(); pending = null; Result = null; accept.IsEnabled = false;
        if (!opacity.TryGetValue(out double threshold) || !detail.TryGetValue(out double fidelity))
        { ShowMessage(L.Get("ToolOptions.InvalidNumber")); return; }
        drawing.Opacity = .55;
        if (drawing.Children.Count == 0) ShowMessage(L.Get("Symbol.Tracing"));
        var request = new CancellationTokenSource(); pending = request;
        _ = UpdateAsync(request, threshold, fidelity, first);
    }

    private async Task UpdateAsync(CancellationTokenSource request, double threshold, double fidelity, bool first)
    {
        var token = request.Token;
        try
        {
            if (!first) await Task.Delay(30, token);
            await traceWorker.WaitAsync(token);
            SymbolTraceResult? traced;
            try { traced = await Task.Run(() => source.Trace(threshold, fidelity, token), token); }
            finally { traceWorker.Release(); }
            if (!ReferenceEquals(pending, request)) return;
            drawing.Children.Clear();
            if (traced is null) { ShowMessage(L.Get("Symbol.TraceEmpty")); return; }
            var box = traced.Bounds;
            var path = SymbolIcon.Create(traced.Shape);
            path.Stretch = Stretch.Fill; path.Fill = Brushes.Black; path.UseLayoutRounding = false;
            path.Width = box.Width * previewScale; path.Height = box.Height * previewScale;
            Canvas.SetLeft(path, box.X * previewScale); Canvas.SetTop(path, box.Y * previewScale);
            drawing.Children.Add(path); drawing.Opacity = 1; status.Visibility = Visibility.Collapsed;
            Result = traced.Shape; accept.IsEnabled = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (ReferenceEquals(pending, request)) ShowMessage(ex.Message); }
        finally { if (ReferenceEquals(pending, request)) pending = null; request.Dispose(); }
    }

    private void ShowMessage(string message)
    {
        drawing.Children.Clear(); status.Text = message; status.Visibility = Visibility.Visible;
    }
}
