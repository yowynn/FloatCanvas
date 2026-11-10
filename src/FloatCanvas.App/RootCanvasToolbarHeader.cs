using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One bounded strip for the bound file and the current editing path.</summary>
internal sealed class RootCanvasToolbarHeader : Grid
{
    private readonly BoundFileIndicator file;
    private readonly Button returnToMain = InlineButton();
    private readonly TextBlock caption = Label();
    private readonly Func<bool> canReturn;
    private RootCanvasKind root;
    private bool scoped;
    private readonly Border frame;
    private readonly Border fileSeparator;
    private readonly Grid content = new();
    private readonly StackPanel scopeArea = new() { Orientation = Orientation.Horizontal };
    private readonly Button scopeParent = InlineButton();
    private readonly Button scopeButton = InlineButton();
    private readonly Button overflow = InlineButton();
    private readonly List<(Button Button, TextBlock Text, FrameworkElement Separator)> ancestors = [];
    private readonly TextBlock current = Label();
    private readonly Border modeBadge = new() { Height = 18, CornerRadius = new(3), BorderThickness = new(1), Padding = new(3,0,3,0), Margin = new(6,0,0,0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock modeLabel = new() { FontSize = 10, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 114, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl modeIcon = new() { Margin = new(0,0,3,0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly FrameworkElement dropdown = CommandGlyphs.Create("M4,6 L8,10 L12,6");
    private Action? navigateParent;
    private string? scopeViewKey;
    private object? scopeContextKey;

    internal RootCanvasToolbarHeader(BoundFileIndicator file, Action returnToMain, Func<bool> canReturn)
    {
        this.file = file; this.canReturn = canReturn;
        Height = ToolbarUpperRow.ExtensionHeight; MaxWidth = 330;
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
        Margin = new(ToolbarLauncher.ArtworkSize, 0, 3, 0);
        System.Windows.Documents.TextElement.SetFontFamily(this,new("Segoe UI, Microsoft YaHei UI")); TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
        frame = new BubbleFrame { CornerRadius = new(ToolbarUpperRow.OuterCornerRadius), BorderThickness = new(1), IsHitTestVisible = false };
        frame.SetResourceReference(EffectProperty,"ToolbarEdgeEffect");
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        frame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder"); Children.Add(frame);
        // Inline chrome starts three DIP inside the frame, sharing its corner
        // center just like the extra tools' frame and their inner buttons.
        content.Margin = new(ToolbarUpperRow.OuterCornerRadius - ToolbarUpperRow.CornerRadiusValue - 2,0,4,0);
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new());
        content.Children.Add(file);
        fileSeparator = new Border { Width = 1, Margin = new(3,7,4,7) };
        fileSeparator.SetResourceReference(Border.BackgroundProperty, "CanvasBorder");
        SetColumn(fileSeparator, 1); content.Children.Add(fileSeparator);
        var navigation = new Grid();
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); navigation.ColumnDefinitions.Add(new());
        navigation.Children.Add(this.returnToMain); SetColumn(scopeArea,1); navigation.Children.Add(scopeArea);
        SetColumn(caption,1); navigation.Children.Add(caption); caption.Margin = new(2,3,5,4);
        SetColumn(navigation, 2); content.Children.Add(navigation); Children.Add(content);
        file.DisplayChanged += RefreshVisibility;
        this.returnToMain.Content = CommandGlyphs.Create("M6,3 L2,7 L6,11 M2,7 H10 M9,4 H15 V14 H9 V10");
        this.returnToMain.Width = ToolbarUpperRow.ButtonSize; this.returnToMain.HorizontalContentAlignment = HorizontalAlignment.Center;
        ToolTipService.SetShowOnDisabled(this.returnToMain, true);
        this.returnToMain.Click += (_, _) => { if (root != RootCanvasKind.Main && this.canReturn()) returnToMain(); };
        scopeParent.Click += (_, _) => navigateParent?.Invoke();
        var currentContent = new Grid();
        currentContent.ColumnDefinitions.Add(new()); currentContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); currentContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        currentContent.Children.Add(current);
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal }; badgeContent.Children.Add(modeIcon); badgeContent.Children.Add(modeLabel);
        modeBadge.Child = badgeContent; modeBadge.SetResourceReference(Border.BackgroundProperty,"CanvasInput"); modeBadge.SetResourceReference(Border.BorderBrushProperty,"CanvasBorder");
        modeLabel.SetResourceReference(TextBlock.ForegroundProperty,"CanvasMuted");
        SetColumn(modeBadge,1); currentContent.Children.Add(modeBadge);
        dropdown.Width = dropdown.Height = 12; dropdown.Margin = new(3,0,0,0); dropdown.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(dropdown, 2); currentContent.Children.Add(dropdown); scopeButton.Content = currentContent;
        scopeButton.Padding = new(3,0,3,0);
        scopeButton.Click += (_, _) => OpenMenu(scopeButton);
        overflow.Content = new TextBlock { Text = "…", FontSize = 14 };
        overflow.Width = ToolbarUpperRow.ButtonSize; overflow.Click += (_, _) => OpenMenu(overflow);
        Refresh(RootCanvasKind.Main);
    }

    private static TextBlock Label() => new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private static Button InlineButton()
    {
        var button = new Button { Height = ToolbarUpperRow.ButtonSize, Margin = new(2,3,2,4), VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, "CanvasToolbarInlineButton"); return button;
    }
    private static FrameworkElement Separator()
    {
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L3,3 L0,6"), Width = 4, Height = 8, Stretch = Stretch.Uniform,
            StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new(1,3,1,4), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasMuted"); return arrow;
    }
    private static void OpenMenu(Button target)
    {
        if (target.ContextMenu is not { } menu || menu.Items.Count == 0) return;
        menu.PlacementTarget = target; OverlayContextMenu.SetOpen(menu, true);
    }

    internal void Refresh(RootCanvasKind root)
    {
        _ = SceneCanvasRoots.PaintRank(root); this.root = root;
        this.returnToMain.IsEnabled = root != RootCanvasKind.Main && canReturn();
        string name = L.Get("RootCanvas." + root);
        caption.Text = name;
        string action = L.Get("RootCanvas.ReturnAction");
        this.returnToMain.ToolTip = action + "\n" + name + "\n" + L.Get(this.returnToMain.IsEnabled ? "RootCanvas." + root + "Hint" : "RootCanvas.ReturnUnavailable");
        AutomationProperties.SetName(this.returnToMain, action + " (" + name + ")");
        AutomationProperties.SetItemStatus(this.returnToMain, name);
        AutomationProperties.SetHelpText(this.returnToMain, (string)this.returnToMain.ToolTip);
        RefreshVisibility();
    }

    internal void SetScope(EditingScopeDescription description, bool active)
    {
        var path = ScopeNavigationPath.From(description);
        string key = ScopeNavigation.ViewKey(path,description.Mode,description.State,description.ToggleMode is not null && (description.CanToggleMode?.Invoke() ?? true));
        if (scoped == active && scopeViewKey == key && Equals(scopeContextKey,description.ContextKey)) return;
        foreach (var button in new[] { scopeButton, overflow }) if (button.ContextMenu is { } old) OverlayContextMenu.SetOpen(old,false);
        scopeViewKey = key; scopeContextKey = description.ContextKey;
        scoped = active; ancestors.Clear(); scopeArea.Children.Clear();
        if (active)
        {
            current.Text = description.CurrentName;
            modeLabel.Text = description.State; modeLabel.Visibility = description.State.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            modeIcon.Margin = description.State.Length > 0 ? new(0,0,3,0) : new(0);
            modeIcon.Content = CanvasModeVisuals.Icon(description.Mode == L.Get("GroupCanvas.Temporary"), 12, "CanvasMuted");
            dropdown.Visibility = Visibility.Visible;
            scopeButton.ToolTip = description.Caption;
            AutomationProperties.SetName(scopeButton, description.Caption);
            var menu = ScopeNavigation.Menu(path, description.Mode, description.ToggleMode, description.CanToggleMode);
            var foldedMenu = ScopeNavigation.Menu(path, description.Mode, description.ToggleMode, description.CanToggleMode);
            scopeButton.ContextMenu = menu; overflow.ContextMenu = foldedMenu; overflow.ToolTip = description.Path;
            scopeArea.Children.Add(overflow);
            for (int index = 0; index < description.Ancestors.Count; index++)
            {
                var ancestor = description.Ancestors[index]; var text = Label(); text.FontWeight = FontWeights.Normal; text.Text = ancestor.Name;
                bool parent = index == description.Ancestors.Count - 1;
                var button = parent ? scopeParent : InlineButton(); button.Content = text; button.ToolTip = ancestor.Name;
                if (parent) navigateParent = ancestor.Navigate; else button.Click += (_, _) => ancestor.Navigate();
                var separator = Separator(); ancestors.Add((button, text, separator)); scopeArea.Children.Add(button); scopeArea.Children.Add(separator);
            }
            scopeArea.Children.Add(scopeButton);
        }
        RefreshVisibility(); InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        double capacity = Math.Max(0, Math.Min(MaxWidth, double.IsFinite(available.Width) ? available.Width : MaxWidth) - content.Margin.Left - content.Margin.Right);
        bool navigation = scoped || root != RootCanvasKind.Main;
        file.MaxWidth = navigation ? ToolbarUpperRow.ButtonSlot : capacity;
        file.Measure(new Size(file.MaxWidth, Height));
        if (scoped)
        {
            double budget = Math.Max(0, capacity - file.DesiredSize.Width - (fileSeparator.Visibility == Visibility.Visible ? 8 : 0) - (returnToMain.Visibility == Visibility.Visible ? ToolbarUpperRow.ButtonSlot : 0));
            current.MaxWidth = double.PositiveInfinity; current.Measure(new Size(double.PositiveInfinity, Height));
            modeBadge.Measure(new Size(double.PositiveInfinity, Height));
            double currentChrome = 12 + modeBadge.DesiredSize.Width + (dropdown.Visibility == Visibility.Visible ? 15 : 0);
            double natural = current.DesiredSize.Width + currentChrome;
            foreach (var ancestor in ancestors)
            {
                ancestor.Button.Visibility = ancestor.Separator.Visibility = Visibility.Visible;
                ancestor.Text.MaxWidth = double.PositiveInfinity; ancestor.Text.Measure(new Size(double.PositiveInfinity, Height));
                natural += ancestor.Text.DesiredSize.Width + 24;
            }
            bool fold = natural > budget; overflow.Visibility = fold && ancestors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (fold)
            {
                foreach (var ancestor in ancestors.SkipLast(1)) ancestor.Button.Visibility = ancestor.Separator.Visibility = Visibility.Collapsed;
                double remaining = Math.Max(0, budget - (overflow.Visibility == Visibility.Visible ? ToolbarUpperRow.ButtonSlot : 0));
                double parentWidth = ancestors.Count > 0 ? Math.Min(90, Math.Max(0, remaining - Math.Min(current.DesiredSize.Width + currentChrome, remaining * .62) - 24)) : 0;
                if (ancestors.Count > 0) ancestors[^1].Text.MaxWidth = parentWidth;
                current.MaxWidth = Math.Max(0, remaining - (ancestors.Count > 0 ? parentWidth + 24 : 0) - currentChrome);
            }
        }
        return base.MeasureOverride(available);
    }
    private void RefreshVisibility()
    {
        bool navigation = scoped || root != RootCanvasKind.Main;
        file.SetCompact(navigation);
        scopeArea.Visibility = scoped ? Visibility.Visible : Visibility.Collapsed;
        returnToMain.Visibility = root != RootCanvasKind.Main ? Visibility.Visible : Visibility.Collapsed;
        caption.Visibility = !scoped && root != RootCanvasKind.Main ? Visibility.Visible : Visibility.Collapsed;
        fileSeparator.Visibility = navigation && file.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        Visibility = navigation || file.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The body and tail share one contour, stroke and effect bounds.</summary>
    private sealed class BubbleFrame : Border
    {
        private Geometry? outline;
        private Size outlineSize;
        private double outlineStroke, outlineRadius;

        protected override void OnRender(DrawingContext drawing)
        {
            double stroke = BorderThickness.Left, radius = CornerRadius.TopLeft;
            if (RenderSize.Width <= stroke || RenderSize.Height <= stroke) return;
            if (outline is null || outlineSize != RenderSize || outlineStroke != stroke || outlineRadius != radius)
            {
                double inset = stroke / 2, right = RenderSize.Width - inset, bottom = RenderSize.Height - inset;
                double r = Math.Min(Math.Max(0, radius - inset), Math.Min(right - inset, bottom - inset) / 2);
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new(inset + r, inset), isFilled: true, isClosed: true);
                    path.LineTo(new(right - r, inset), true, false);
                    path.ArcTo(new(right, inset + r), new(r,r), 0, false, SweepDirection.Clockwise, true, false);
                    path.LineTo(new(right, bottom - r), true, false);
                    path.ArcTo(new(right - r, bottom), new(r,r), 0, false, SweepDirection.Clockwise, true, false);
                    path.LineTo(new(10, bottom), true, false);
                    // The lower flank stays left of the first tool's frame once
                    // it reaches the main toolbar, while covering its background.
                    path.BezierTo(new(-2, bottom), new(-2,48), new(-12,51), true, false);
                    path.BezierTo(new(-10,42), new(inset,32), new(inset,20), true, false);
                    path.LineTo(new(inset, inset + r), true, false);
                    path.ArcTo(new(inset + r, inset), new(r,r), 0, false, SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze(); outline = geometry;
                outlineSize = RenderSize; outlineStroke = stroke; outlineRadius = radius;
            }
            drawing.DrawGeometry(Background, BorderBrush is null || stroke == 0 ? null : new Pen(BorderBrush, stroke), outline);
        }
    }
}
