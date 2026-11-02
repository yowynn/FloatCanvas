using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Shares the existing toolbar header between its filename and an explicit return from a screen root.</summary>
internal sealed class RootCanvasToolbarHeader : Grid
{
    private readonly BoundFileIndicator file;
    private readonly Button returnToMain = new();
    private readonly TextBlock caption = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Func<bool> canReturn;
    private RootCanvasKind root;

    internal RootCanvasToolbarHeader(BoundFileIndicator file, Action returnToMain, Func<bool> canReturn)
    {
        this.file = file; this.canReturn = canReturn;
        Height = 28; VerticalAlignment = VerticalAlignment.Top;
        Margin = new(ToolbarLauncher.ArtworkSize, 0, 3, 0);
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        file.AlignTextLeft(15); file.ClipToBounds = true; Children.Add(file);
        file.DisplayChanged += RefreshVisibility;
        this.returnToMain.Height = this.returnToMain.MinHeight = 28; this.returnToMain.MinWidth = 0;
        this.returnToMain.Padding = new(8, 3, 8, 3); this.returnToMain.Margin = new(4, 0, 0, 0);
        this.returnToMain.SetResourceReference(StyleProperty, "CanvasCaptionButton");
        this.returnToMain.BorderThickness = new(1);
        this.returnToMain.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var back = ActionIconButton.Glyph(PanelActionIcon.Previous); back.Margin = new(0, 0, 5, 0);
        content.Children.Add(back); content.Children.Add(caption); this.returnToMain.Content = content;
        ToolTipService.SetShowOnDisabled(this.returnToMain, true);
        this.returnToMain.Click += (_, _) => { if (root != RootCanvasKind.Main && this.canReturn()) returnToMain(); };
        Grid.SetColumn(this.returnToMain, 1); Children.Add(this.returnToMain);
        Refresh(RootCanvasKind.Main);
    }

    internal void AlignFileTextLeft(double x) => file.AlignTextLeft(x);

    internal void Refresh(RootCanvasKind root)
    {
        _ = SceneCanvasRoots.PaintRank(root); this.root = root;
        bool screenRoot = root != RootCanvasKind.Main;
        returnToMain.Visibility = screenRoot ? Visibility.Visible : Visibility.Collapsed;
        returnToMain.IsEnabled = screenRoot && canReturn();
        string name = L.Get("RootCanvas." + root);
        caption.Text = L.Format("RootCanvas.ReturnToMain", name);
        returnToMain.ToolTip = caption.Text + "\n" + L.Get(returnToMain.IsEnabled ? "RootCanvas." + root + "Hint" : "RootCanvas.ReturnUnavailable");
        AutomationProperties.SetName(returnToMain, caption.Text);
        AutomationProperties.SetItemStatus(returnToMain, name);
        AutomationProperties.SetHelpText(returnToMain, (string)returnToMain.ToolTip);
        RefreshVisibility();
    }

    private void RefreshVisibility() => Visibility = root != RootCanvasKind.Main || file.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
}
