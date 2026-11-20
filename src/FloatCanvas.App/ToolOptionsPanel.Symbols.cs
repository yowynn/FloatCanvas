using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class ToolOptionsPanel
{
    private readonly Button customSymbolSlot = new(), extractSymbol = new();
    private SymbolShape? slotPreview;
    private bool extractingSymbol;
    internal Func<EditorSession, Task<SymbolTraceSource>> PrepareSelectedSymbol { get; set; } = session => PrepareSelectionAsync(session);

    private void InitializeSymbolChoices()
    {
        foreach (var kind in SymbolGeometry.BuiltIns)
        {
            var button = ActionButton(L.Get("Symbol." + kind), () => Change(p => p.Symbol = kind));
            button.Content = SymbolIcon.Create(SymbolGeometry.Shape(kind)); button.Tag = kind;
            symbolGallery.Children.Add(button);
        }
        var controls = new DockPanel();
        var actions = new DockPanel { Margin = new(2, 4, 2, 2) };
        DockPanel.SetDock(actions, Dock.Bottom); controls.Children.Add(actions);
        extractSymbol.Content = L.Get("Symbol.Extract"); extractSymbol.Padding = new(8, 0, 8, 0);
        extractSymbol.Height = customSymbolSlot.Height = PropertyPanelLayout.ControlHeight;
        extractSymbol.Margin = new(6, 0, 0, 0); DockPanel.SetDock(extractSymbol, Dock.Right);
        ToolTipService.SetShowOnDisabled(extractSymbol, true);
        actions.Children.Add(extractSymbol); actions.Children.Add(customSymbolSlot);
        customSymbolSlot.Content = L.Get("Symbol.Custom"); AutomationProperties.SetName(customSymbolSlot, L.Get("Symbol.Custom"));
        customSymbolSlot.Click += (_, _) => { if (controller.CustomSymbol is { } shape) UseCustomSymbol(shape); };
        extractSymbol.Click += async (_, _) => await ExtractSymbolFromSelectionAsync();
        controls.Children.Add(symbolGallery);
        AddRow(parameterGrid, "Symbols", L.Get("ToolOptions.Symbols"), controls);
    }

    private void RefreshSymbolChoices()
    {
        var shape = controller.CustomSymbol;
        if (slotPreview != shape)
        {
            slotPreview = shape;
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            if (shape is not null) content.Children.Add(SymbolIcon.Create(shape));
            content.Children.Add(new TextBlock { Text = L.Get("Symbol.Custom"), Margin = shape is null ? new(0) : new(6, 0, 0, 0) });
            customSymbolSlot.Content = content;
        }
        customSymbolSlot.IsEnabled = shape is not null && !extractingSymbol;
        customSymbolSlot.SetResourceReference(BorderBrushProperty, controller.Symbol == SymbolKind.Custom ? "CanvasAccent" : "CanvasBorder");
        bool selected = controller.Session.Selection.Count > 0;
        extractSymbol.IsEnabled = selected && !extractingSymbol && !controller.Session.IsTransactionActive;
        extractSymbol.ToolTip = L.Get(selected ? "Symbol.ExtractHint" : "Symbol.SelectFirst");
        HighlightGallery(symbolGallery, controller.Symbol);
    }

    private void UseCustomSymbol(SymbolShape shape) => Change(p =>
    {
        p.Symbol = SymbolKind.Custom; p.CustomSymbol = shape;
    });

    private async Task ExtractSymbolFromSelectionAsync()
    {
        var session = controller.Session;
        if (extractingSymbol || controller.Tool != EditorTool.Symbol || session.Selection.Count == 0 || session.IsTransactionActive) return;
        var ids = session.Selection.ToHashSet(); var revision = session.SceneRevision; var slot = controller.CustomSymbol;
        var toolContext = controller.PlacementContextVersion; var editScope = session.EditScopeVersion;
        bool Current() => controller.Tool == EditorTool.Symbol && ReferenceEquals(session, controller.Session) &&
            session.SceneRevision == revision && session.EditScopeVersion == editScope && controller.PlacementContextVersion == toolContext &&
            !session.IsTransactionActive && session.Selection.SetEquals(ids) && controller.CustomSymbol == slot;
        extractingSymbol = true; RefreshSymbolChoices();
        try
        {
            using var retention = PopupChildDialog.RetainFor(this);
            var source = await PrepareSelectedSymbol(session);
            if (!Current() || !IsVisible) return;
            var preview = new SymbolTraceWindow(Window.GetWindow(this), source);
            if (SessionWindowCoordinator.ShowModal(preview) == true && Current() && preview.Result is { } shape) UseCustomSymbol(shape);
        }
        catch (Exception ex) { if (Current()) Error(ex.Message); }
        finally { extractingSymbol = false; RefreshSymbolChoices(); }
    }

    internal static async Task<SymbolTraceSource> PrepareSelectionAsync(EditorSession session, TimeSpan animationTime = default,
        PresentationCaptureSnapshot? presentation = null, Func<EditorSession, SceneDocument, IEnumerable<AssetResource>, WindowDocumentPackageCapture>? captureWindows = null)
    {
        var snapshot = session.CreateSelectionSnapshot(normalizeOrigin: false);
        var graph = SceneGraph.Read(session.Document);
        foreach (var e in snapshot.Elements) e.Visible &= graph.IsVisible(e.Id);
        foreach (var instance in snapshot.Instances) instance.Visible &= graph.IsVisible(instance.Id);
        snapshot.Canvas = session.Document.Canvas.ResolveReference(session.Document.Viewport);
        snapshot.Viewport = session.Document.Viewport;
        var resources = snapshot.ReferencedAssets.Select(session.Assets.Get).ToArray();
        bool hasWindows = snapshot.Elements.Any(e => e.WindowSource is not null) || snapshot.Definitions.Any(d => d.Graph.Elements.Any(e => e.WindowSource is not null));
        using var windows = hasWindows ? captureWindows?.Invoke(session, snapshot, resources) : null;
        return await Task.Run(() =>
        {
            var prepared = windows?.Prepare();
            var assets = new AssetLibrary();
            foreach (var resource in prepared?.Resources ?? resources) assets.Add(resource);
            using var renderer = new SceneRenderer { AnimationTime = animationTime };
            presentation?.ApplyTo(renderer);
            return renderer.PrepareSymbolExtraction(prepared?.Document ?? snapshot, assets);
        }).ConfigureAwait(false);
    }
}
