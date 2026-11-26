using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasGridCalibrationDraft
{
    internal string Columns = "1", Rows = "1";
    internal bool AlignBoundary = true;
    internal string? CandidateColumns, CandidateRows;
    internal void ResetCandidates() { CandidateColumns = CandidateRows = null; }
}

internal sealed class CanvasGridCalibrationWindow : Window
{
    internal enum CandidateState { InvalidInput, Checking, Safe, Unsafe, TargetInvalid }
    internal CandidateState State { get; private set; }
    internal CanvasSettings? Result { get; private set; }
    internal bool AdjustRequested { get; private set; }
    internal CanvasGridCalibrationWindow(Window owner, CanvasSettings initial, RectD sample,
        CanvasGridCalibrationDraft draft, Action<CanvasSettings?> preview,
        Func<CanvasSettings, CancellationToken, Task<CanvasCalibrationCheck>>? validate = null, Func<CanvasSettings, string?>? commit = null,
        Action<CanvasCalibrationIssue?>? highlight = null)
    {
        Title = L.Get("Canvas.GridCalibration"); Owner = owner; Topmost = true; ShowInTaskbar = false;
        Width = 480; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = WindowLayout.ContentMargin() };
        TextBlock Text(string key) => new() { Text = L.Get(key), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 8) };
        NumericValueField Number(string key, string value, bool stepper = false)
        {
            var field = new NumericValueField(1, CanvasCalibration.MaximumGridCount, integerOnly: true) { Unit = L.Get("Numeric.Unit.Cell"), ToolTip = L.Get(key) };
            field.SetScrubAnchor(key.Contains("Columns") ? NumericPropertyKind.Columns : NumericPropertyKind.Rows, L.Get(key));
            AutomationProperties.SetName(field.Input, L.Get(key)); AutomationProperties.SetName(field.Slider, L.Get(key));
            field.Input.Text = value;
            if (stepper) field.EnableStepper();
            return field;
        }
        panel.Children.Add(PropertyPanelLayout.SectionTitle(L.Get("Canvas.GridSampleSection")));
        var columns = Number("Canvas.SampleColumns", draft.Columns);
        var rows = Number("Canvas.SampleRows", draft.Rows);
        var picker = new GridCountPicker(() => Count(columns, out int c) && Count(rows, out int r) ? (c, r) : null);
        var samplePair = PropertyPanelLayout.Pair(columns, rows, picker); samplePair.Margin = PropertyPanelLayout.RowMargin; panel.Children.Add(samplePair);
        var boundary = new CheckBox { Content = L.Get("Canvas.AlignGridBoundaryShort"), IsChecked = draft.AlignBoundary, Margin = new(0, 4, 0, 8), ToolTip = L.Get("Canvas.AlignGridBoundary") };
        panel.Children.Add(boundary);
        var candidateBody = new StackPanel();
        var candidateRegion = new PropertyChangeRegion(candidateBody); panel.Children.Add(candidateRegion);
        var candidates = new StackPanel(); candidateBody.Children.Add(candidates);
        var candidateHeading = new DockPanel();
        var candidateMode = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), FontSize = 11 };
        candidateMode.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        DockPanel.SetDock(candidateMode, Dock.Right); candidateHeading.Children.Add(candidateMode);
        candidateHeading.Children.Add(PropertyPanelLayout.SectionTitle(L.Get("Canvas.GridCandidateSection"))); candidates.Children.Add(candidateHeading);
        var estimate = Text("Canvas.GridCandidateHelp"); candidates.Children.Add(estimate);
        var totalColumns = Number("Canvas.TotalColumns", draft.CandidateColumns ?? "1", true);
        var totalRows = Number("Canvas.TotalRows", draft.CandidateRows ?? "1", true);
        var automatic = new Button { Content = CommandGlyphs.Create("M3,4 A6,6 0 1 1 2,10 M1,1 V5 H5"),
            Width = PropertyPanelLayout.ControlHeight, Height = PropertyPanelLayout.ControlHeight, MinWidth = 0, MinHeight = 0,
            Margin = new(4, 0, 4, 0), Padding = new(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.Get("Canvas.AutomaticGridCandidate") };
        automatic.SetResourceReference(StyleProperty, "CanvasPropertyIconButton");
        AutomationProperties.SetName(automatic, L.Get("Canvas.AutomaticGridCandidate"));
        var totalPair = PropertyPanelLayout.Pair(totalColumns, totalRows, automatic); totalPair.Margin = PropertyPanelLayout.RowMargin; candidates.Children.Add(totalPair);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) }; candidateBody.Children.Add(status);
        var issueList = new ListBox { MaxHeight = 140, DisplayMemberPath = nameof(CanvasCalibrationIssue.Name) };
        issueList.SetResourceReference(Control.BackgroundProperty, "CanvasInput");
        issueList.SetResourceReference(Control.ForegroundProperty, "CanvasText");
        issueList.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder");
        VirtualizingPanel.SetIsVirtualizing(issueList, true);
        VirtualizingPanel.SetVirtualizationMode(issueList, VirtualizationMode.Recycling);
        var issuePath = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
        issuePath.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        var markIssue = new Button { Content = L.Get("Canvas.MarkGridIssue"), MinHeight = PropertyPanelLayout.ControlHeight, IsEnabled = false };
        var issueBody = new StackPanel(); issueBody.Children.Add(issueList); issueBody.Children.Add(issuePath); issueBody.Children.Add(markIssue);
        var issues = new Expander { Content = issueBody, Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 8) };
        candidateBody.Children.Add(issues);
        issueList.SelectionChanged += (_, _) =>
        {
            highlight?.Invoke(null);
            issuePath.Text = (issueList.SelectedItem as CanvasCalibrationIssue)?.Path ?? "";
            markIssue.IsEnabled = highlight is not null && issueList.SelectedItem is CanvasCalibrationIssue { OriginalBounds: not null };
        };
        markIssue.ToolTip = L.Get("Canvas.MarkGridIssueHint");
        markIssue.Click += (_, _) => { if (issueList.SelectedItem is CanvasCalibrationIssue issue) highlight?.Invoke(issue); };
        issues.Collapsed += (_, _) => highlight?.Invoke(null);
        void ClearIssues()
        {
            highlight?.Invoke(null); issues.IsExpanded = false; issues.Visibility = Visibility.Collapsed;
            issueList.ItemsSource = null; issuePath.Text = "";
        }
        var effect = Text("Canvas.GridCalibrationEffectShort"); panel.Children.Add(effect);
        effect.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var adjust = new Button { Content = L.Get("Canvas.AdjustGridSample"), MinHeight = PropertyPanelLayout.ControlHeight, Margin = new(0, 0, 8, 0) };
        var apply = new Button { Content = L.Get("Canvas.ApplyPlane"), MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 80 };
        AppInputPolicy.AttachFormSubmit(this, apply);
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 80, IsCancel = true, Margin = new(0, 0, 8, 0) };
        actions.Children.Add(adjust); actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions);
        CanvasSettings? candidate = null; bool syncing = false, queued = false, closed = false; long candidateVersion = 0;
        CancellationTokenSource? pendingCheck = null;
        void CancelCheck() { pendingCheck?.Cancel(); pendingCheck?.Dispose(); pendingCheck = null; }
        async void CheckCandidate(CanvasSettings settings, long version, string summary)
        {
            CancelCheck(); pendingCheck = new(); var token = pendingCheck.Token;
            CanvasCalibrationCheck check;
            try { check = await validate!(settings, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { check = new(0, error.Message); }
            if (closed || version != candidateVersion) return;
            if (check.TargetInvalid) { State = CandidateState.TargetInvalid; SessionWindowCoordinator.Close(this); return; }
            State = check.IsSafe ? CandidateState.Safe : CandidateState.Unsafe;
            status.Text = check.IsSafe ? summary : check.Error ?? L.Format("Canvas.GridUnsafeCount", check.AffectedCount);
            status.SetResourceReference(TextBlock.ForegroundProperty, check.IsSafe ? "CanvasMuted" : "CanvasErrorText");
            if (check.Issues.Count > 0)
            {
                issues.Header = L.Format("Canvas.GridIssueDetails", check.AffectedCount);
                issueList.ItemsSource = check.Issues; issues.Visibility = Visibility.Visible;
            }
            apply.IsEnabled = check.IsSafe; preview(check.IsSafe ? settings : null);
        }
        bool Count(NumericValueField f, out int value) => int.TryParse(f.Input.Text, out value) && value is >= 1 and <= CanvasCalibration.MaximumGridCount;
        void Refresh()
        {
            queued = false; if (closed) return;
            draft.Columns = columns.Input.Text; draft.Rows = rows.Input.Text; draft.AlignBoundary = boundary.IsChecked == true;
            bool manual = draft.CandidateColumns is not null || draft.CandidateRows is not null;
            candidateMode.Text = L.Get(manual ? "Canvas.GridCandidateManual" : "Canvas.GridCandidateAutomatic");
            automatic.IsEnabled = manual;
            var visibility = draft.AlignBoundary ? Visibility.Visible : Visibility.Collapsed;
            if (candidates.Visibility != visibility) candidateRegion.Change(() => candidates.Visibility = visibility);
            long version = ++candidateVersion; CancelCheck();
            ClearIssues();
            candidate = null; State = CandidateState.InvalidInput; var error = CanvasGridFitError.InvalidCounts;
            estimate.Text = ""; status.ToolTip = null;
            if (Count(columns, out int c) && Count(rows, out int r))
            {
                double nx = c / sample.Width, ny = r / sample.Height;
                estimate.Text = L.Format("Canvas.GridEstimateShort", nx, ny);
                syncing = true;
                if (draft.CandidateColumns is null) totalColumns.Input.Text = Math.Max(1, Math.Round(nx, MidpointRounding.AwayFromZero)).ToString(CultureInfo.CurrentCulture);
                if (draft.CandidateRows is null) totalRows.Input.Text = Math.Max(1, Math.Round(ny, MidpointRounding.AwayFromZero)).ToString(CultureInfo.CurrentCulture);
                syncing = false;
                bool valid = Count(totalColumns, out int tc) & Count(totalRows, out int tr);
                if (draft.AlignBoundary && !valid) error = CanvasGridFitError.CountOutOfRange;
                else if (CanvasGridCalibration.TryFitRectangle(initial, sample, c, r, draft.AlignBoundary,
                    draft.AlignBoundary ? tc : null, draft.AlignBoundary ? tr : null, out var fit, out error))
                {
                    candidate = fit!.Settings;
                    var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(candidate.Projection);
                    status.Text = L.Format("Canvas.GridResultShort", candidate.Grid.PlaneCellSize) +
                        (draft.AlignBoundary ? L.Format("Canvas.GridDeviationShort", fit.SampleDeviation) : "");
                    status.ToolTip = L.Format("Canvas.GridCalibrationResult", bounds.Width / bounds.Height, candidate.Grid.PlaneCellSize) +
                        (draft.AlignBoundary ? "\n" + L.Format("Canvas.GridDeviation", fit.SampleDeviation) : "");
                }
            }
            if (candidate is null) status.Text = L.Get("Canvas.GridFit." + error);
            status.SetResourceReference(TextBlock.ForegroundProperty, candidate is null ? "CanvasErrorText" : "CanvasMuted");
            apply.IsEnabled = candidate is not null && validate is null;
            if (candidate is null) preview(null);
            else if (validate is null) { State = CandidateState.Safe; preview(candidate); }
            else
            {
                State = CandidateState.Checking; preview(null);
                var summary = status.Text; status.Text = L.Get("Canvas.GridChecking");
                CheckCandidate(candidate, version, summary);
            }
        }
        void Schedule()
        {
            if (syncing || closed) return;
            candidateVersion++; CancelCheck(); ClearIssues(); candidate = null; apply.IsEnabled = false; preview(null);
            if (queued) return; queued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Refresh));
        }
        foreach (var field in new[] { columns, rows })
            field.Input.TextChanged += (_, _) => { draft.ResetCandidates(); Schedule(); };
        picker.CountsSelected += (c, r) =>
        {
            // Publish both coordinates together; no intermediate X/new, Y/old candidate.
            syncing = true;
            try { columns.Input.Text = c.ToString(CultureInfo.CurrentCulture); rows.Input.Text = r.ToString(CultureInfo.CurrentCulture); }
            finally { syncing = false; }
            draft.ResetCandidates(); Schedule();
        };
        totalColumns.Input.TextChanged += (_, _) => { if (!syncing) { draft.CandidateColumns = totalColumns.Input.Text; Schedule(); } };
        totalRows.Input.TextChanged += (_, _) => { if (!syncing) { draft.CandidateRows = totalRows.Input.Text; Schedule(); } };
        boundary.Checked += (_, _) => Schedule(); boundary.Unchecked += (_, _) => Schedule();
        automatic.Click += (_, _) => { draft.ResetCandidates(); Schedule(); };
        apply.Click += (_, _) =>
        {
            if (validate is null) Refresh();
            if (candidate is null || State != CandidateState.Safe || queued) return;
            try
            {
                if (commit?.Invoke(candidate) is { } message)
                { status.Text = message; status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); return; }
                Result = candidate; SessionWindowCoordinator.Close(this);
            }
            catch (Exception error) { status.Text = error.Message; status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); }
        };
        adjust.Click += (_, _) => { Refresh(); AdjustRequested = true; SessionWindowCoordinator.Close(this); };
        cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        PreviewKeyDown += (_, e) => { if (!e.Handled && e.Key == System.Windows.Input.Key.Escape && picker.TryClosePicker()) e.Handled = true; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) picker.ClosePicker(); };
        Closed += (_, _) => { closed = true; picker.ClosePicker(); candidateVersion++; CancelCheck(); highlight?.Invoke(null); candidateRegion.Reset(); };
        Content = PropertyChangeRegion.ReserveHighlightSpace(new ScrollViewer { Content = panel, Margin = new(10, 0, 10, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this);
        AppWindowChrome.SetHelp(this, L.Get("Canvas.GridCalibration"), L.Get("Canvas.GridCountPickerHelp") + "\n\n" + L.Get("Canvas.GridCalibrationHelp"));
        Refresh();
    }
}
