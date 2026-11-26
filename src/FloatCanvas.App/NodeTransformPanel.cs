using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One transform contract for permanent groups and temporary node selections.</summary>
internal sealed class NodeTransformPanel : StackPanel
{
    private readonly Func<EditorSession> getSession;
    private readonly Func<bool> canMutate;
    private readonly Dictionary<string, NumericValueField> fields = [];
    private readonly SegmentedChoice mode = new();
    private readonly SegmentedChoice placement = new();
    private readonly FrameworkElement placementRow;
    private readonly FrameworkElement heightRow;
    private readonly AspectRatioButton aspect = new() { IsChecked = true };
    private readonly Button enter = new() { Height = PropertyPanelLayout.ControlHeight, Margin = PropertyPanelLayout.RowMargin };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private EditorSession? editingSession;
    private SelectionTransformSnapshot? original;
    private Guid[] selection = [];
    private long transaction;
    private RectD bounds;
    private bool refreshing;
    internal bool IsEditing => editingSession is not null;
    internal event Action<Guid>? EditPrefabRequested;
    internal bool HasCurrentContext => editingSession is { IsTransactionActive: true } session && ReferenceEquals(session, getSession()) &&
        session.TransactionId == transaction && session.Selection.SetEquals(selection);
    internal bool IsApplicable => getSession().Selection.Count > 1 || getSession().Selection.Any(id => getSession().Document.Groups.Any(group => group.Id == id) || getSession().Document.Instances.Any(instance => instance.Id == id));

    internal NodeTransformPanel(Func<EditorSession> getSession, Func<bool> canMutate)
    {
        this.getSession = getSession; this.canMutate = canMutate;
        placement.Add(L.Get("Billboard.GroupSurface"), false, SegmentedChoicePreviews.Icon("surface"));
        placement.Add(L.Get("Billboard.Name"), true, SegmentedChoicePreviews.Icon("billboard"));
        placement.ToolTip = L.Get("Billboard.GroupModeHint");
        placementRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Billboard.Placement")), placement);
        placement.SelectionChanged += (_, _) =>
        {
            if (refreshing || placement.SelectedValue is not bool enabled || getSession().Selection.Count != 1) return;
            CompletePendingEdit();
            try { getSession().SetContainerBillboard(getSession().Selection.Single(), enabled); Refresh(); }
            catch (InvalidOperationException error) { Refresh(); status.Text = error.Message; }
        };
        Visibility = Visibility.Collapsed;
        mode.Add(L.Get("GroupTransform.Overall"), false, SegmentedChoicePreviews.Icon("combined"));
        mode.Add(L.Get("GroupTransform.Individual"), true, SegmentedChoicePreviews.Icon("canvas"));
        Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("GroupTransform.Pivot")), mode));
        mode.SelectionChanged += (_, _) => { if (!refreshing && mode.SelectedValue is bool value) { CompletePendingEdit(); getSession().TransformIndividually = value; Refresh(); } };
        foreach (var (key, label) in new[] { ("X", "X"), ("Y", "Y"), ("W", L.Get("Inspector.Width")), ("H", L.Get("Inspector.Height")), ("Angle", L.Get("GroupTransform.RotationDelta")), ("CardHeight", L.Get("Billboard.Height")), ("Opacity", L.Get("Inspector.Opacity")) })
        {
            var field = key == "Opacity" ? new NumericValueField(0, 100, sliderMinimum: 0, sliderMaximum: 100, step: 1) : key == "Angle" ? new NumericValueField(-360000, 360000, sliderMinimum: -180, sliderMaximum: 180) :
                key is "W" or "H" ? new NumericValueField(.01, 100_000_000, sliderMinimum: 1, sliderMaximum: 2000) :
                new NumericValueField(-100_000_000, 100_000_000, sliderMinimum: -1000, sliderMaximum: 1000);
            fields.Add(key, field); field.Unit = key == "Opacity" ? "%" : key == "Angle" ? "°" : "px";
            field.SetScrubAnchor(NumericPropertyIcon.ForProperty(key), label);
            AutomationProperties.SetName(field.Input, label); AutomationProperties.SetName(field.Slider, label);
            if (key is "Angle" or "Opacity")
            {
                var caption = new PropertyLabel(label); field.SetLabel(caption);
                Children.Add(PropertyPanelLayout.Row(caption, field));
            }
            field.CanBeginGesture = BeginEdit;
            field.ValueChanged += _ => { if (!refreshing && field.IsInteracting) Publish(key); };
            field.GestureCompleted += commit => { if (commit) CompletePendingEdit(); else CancelEditing(); };
            field.SliderValueCommitted += () => { if (!refreshing) { Publish(key); CompletePendingEdit(); } };
            field.Input.LostKeyboardFocus += (_, _) => { if (!refreshing && !field.IsInteracting) { Publish(key); CompletePendingEdit(); } };
            field.Input.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && IsEditing) { CancelEditing(); Refresh(); e.Handled = true; }
            };
        }
        Children.Insert(1, PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.Position")), PropertyPanelLayout.Pair(fields["X"], fields["Y"])));
        Children.Insert(2, PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.Size")), PropertyPanelLayout.Pair(fields["W"], fields["H"], aspect)));
        heightRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Billboard.Height")), fields["CardHeight"]);
        Children.Add(placementRow); Children.Add(heightRow);
        enter.Content = L.Get("GroupTransform.Enter");
        enter.Click += (_, _) =>
        {
            CompletePendingEdit(); var session = getSession();
            if (!canMutate() || session.Selection.Count != 1 || !session.CanBrowseNode(session.Selection.First())) return;
            if (session.Selection.Count == 1 && session.Document.Instances.Any(instance => instance.Id == session.Selection.First()))
            { EditPrefabRequested?.Invoke(session.Selection.First()); return; }
            if (session.Selection.Count == 1 && session.Document.Groups.Any(group => group.Id == session.Selection.First())) session.EnterScope(session.Selection.First());
            Refresh();
        };
        Children.Add(enter); Children.Add(status);
        Unloaded += (_, _) => CompletePendingEdit();
    }
    internal void Refresh()
    {
        if (IsEditing) return;
        var session = getSession(); Visibility = IsApplicable ? Visibility.Visible : Visibility.Collapsed;
        if (!IsApplicable) return;
        refreshing = true;
        try
        {
            var graph = new SceneGraph(session.Document);
            bool editable = canMutate() && session.Selection.All(id => !graph.IsLocked(id)) && !session.IsTransactionActive;
            Guid? container = session.Selection.Count == 1 && (graph.FindGroup(session.Selection.First()) is not null || graph.FindInstance(session.Selection.First()) is not null) ? session.Selection.First() : null;
            var card = container is { } node ? graph.ContainerBillboard(node) : null;
            placementRow.Visibility = container is not null ? Visibility.Visible : Visibility.Collapsed;
            heightRow.Visibility = card is not null ? Visibility.Visible : Visibility.Collapsed;
            placement.SelectedValue = card is not null; placement.IsEnabled = editable;
            placement.ToolTip = L.Get("Billboard.GroupModeHint") +
                (container is { } legacy && graph.FindGroup(legacy)?.OrderMode == GroupOrderMode.LegacyPassThrough ? "\n" + L.Get("Layers.CollectHint") : "");
            fields["CardHeight"].SetModelValue(card?.Height ?? 0);
            mode.SelectedValue = session.TransformIndividually;
            var current = Bounds(session);
            fields["X"].SetModelValue(current.X); fields["Y"].SetModelValue(current.Y);
            fields["W"].SetModelValue(Math.Max(.01, current.Width)); fields["H"].SetModelValue(Math.Max(.01, current.Height)); fields["Angle"].SetModelValue(0);
            var opacity = graph.NormalizeSelection(session.Selection).Select(graph.NodeOpacity).Distinct().ToArray();
            fields["Opacity"].SetModelValue(opacity.FirstOrDefault(1) * 100);
            if (opacity.Length > 1) fields["Opacity"].ShowPlaceholder("");
            foreach (var field in fields.Values) field.IsEnabled = editable;
            fields["W"].IsEnabled = fields["H"].IsEnabled = editable && current.Width > 0 && current.Height > 0;
            enter.Visibility = session.Selection.Count == 1 && (graph.FindGroup(session.Selection.First()) is not null || graph.FindInstance(session.Selection.First()) is not null) ? Visibility.Visible : Visibility.Collapsed;
            enter.IsEnabled = canMutate() && container is { } browse && session.CanBrowseNode(browse);
            status.Text = L.Get("GroupTransform.LayoutHelp");
        }
        finally { refreshing = false; }
    }
    private static RectD Bounds(EditorSession session)
    {
        if (session.SelectionBounds() is { } bounds) return bounds;
        var graph = new SceneGraph(session.Document);
        return session.Selection.Where(id => graph.FindGroup(id) is not null || graph.FindInstance(id) is not null)
            .Select(id => graph.NodeWorldPlacement(id).PlaneTransform.TransformPoint(default))
            .Select(point => new RectD(point.X, point.Y, 0, 0)).DefaultIfEmpty(default).Aggregate(RectD.Union);
    }
    private bool BeginEdit()
    {
        if (!canMutate()) { CancelEditing(); return false; }
        if (IsEditing) return ReferenceEquals(editingSession, getSession()) && editingSession!.Selection.SetEquals(selection);
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive || !IsApplicable) return false;
        var graph = new SceneGraph(session.Document);
        if (session.Selection.Any(graph.IsLocked)) return false;
        bounds = Bounds(session); selection = session.Selection.ToArray();
        original = session.CaptureSelectionTransform();
        session.BeginTransaction(L.Get("Editor.History.Transform")); transaction = session.TransactionId; editingSession = session;
        return true;
    }
    private void Publish(string key)
    {
        if (refreshing || !fields[key].TryGetValue(out double value) || !BeginEdit()) return;
        var session = editingSession!;
        if (!ReferenceEquals(session, getSession()) || !session.Selection.SetEquals(selection) || !session.IsTransactionActive || session.TransactionId != transaction) { CancelEditing(); return; }
        if (key == "CardHeight")
        {
            if (selection is [var id] && !session.TryUpdateContainerBillboardHeight(id, value)) status.Text = L.Get("Perspective.InputOutsidePlane");
            return;
        }
        if (key == "Opacity") { session.TrySetSelectionOpacity(value / 100); return; }
        if (session.TransformIndividually && key is "X" or "Y")
        {
            if (!session.TrySetIndividualSelectionPosition(original!, key == "X" ? value : null, key == "Y" ? value : null))
                status.Text = L.Get("Perspective.InputOutsidePlane");
            return;
        }
        Affine2D delta; double elevation = 1;
        if (key == "X") delta = Affine2D.Translation(value - bounds.X, 0);
        else if (key == "Y") delta = Affine2D.Translation(0, value - bounds.Y);
        else if (key == "Angle") delta = Affine2D.Around(bounds.Center, Affine2D.Rotation(value));
        else
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            double sx = key == "W" ? value / bounds.Width : 1, sy = key == "H" ? value / bounds.Height : 1;
            if (aspect.IsChecked == true) sx = sy = key == "W" ? sx : sy;
            delta = Affine2D.Around(bounds.Center, Affine2D.Scale(sx, sy)); elevation = sy;
        }
        if (!session.TryApplySelectionTransform(original!, delta, elevation, key == "X" || key == "Y" ? false : null))
            status.Text = L.Get("Perspective.InputOutsidePlane");
    }
    internal void CompletePendingEdit()
    {
        var session = editingSession; editingSession = null; original = null;
        if (session is not null && session.IsTransactionActive && session.TransactionId == transaction)
        {
            if (canMutate()) session.Commit(); else session.Cancel();
        }
        Refresh();
    }
    internal void CancelEditing()
    {
        var session = editingSession; editingSession = null; original = null;
        if (session is not null && session.IsTransactionActive && session.TransactionId == transaction) session.Cancel();
    }
}
