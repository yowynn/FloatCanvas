using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private enum CanvasSamplingStage { Lines, Rectangle, AdjustRectangle, Dialog }
    private CanvasSamplingStage _canvasSamplingStage;
    private readonly List<CanvasReferenceLine> _canvasReferenceLines = [];
    private CanvasSettings? _canvasSamplingInitial;
    private PointD? _canvasSampleStart, _canvasSampleEnd;
    private (PointD? Start, PointD? End) _canvasSampleBeforeAdjust;
    private bool _canvasSampleDragging;
    private int _canvasLineHandle = -1;
    private CanvasGridCalibrationDraft? _canvasGridDraft;
    private CanvasCalibrationSafety? _canvasGridSafety;
    private CanvasCalibrationIssue? _canvasGridIssueHighlight;
    private bool _canvasGridCommitting;
    private string _canvasSampleHint = "";
    private OverlayPoint _canvasSamplePointer;
    private SKBitmap? _canvasSampleLoupe;
    private long _canvasSampleLoupeTick;

    private void BeginCanvasFourLines() => BeginCanvasSampling(CanvasSamplingStage.Lines);
    private void BeginCanvasGridCalibration() => BeginCanvasSampling(CanvasSamplingStage.Rectangle);
    private void BeginCanvasSampling(CanvasSamplingStage stage)
    {
        if (DocumentOperationBusy || !_host.IsEditing || !_host.IsVisible || !CanEditDisplayedCanvas || Active.IsTransactionActive ||
            stage != CanvasSamplingStage.Lines && !CurrentCanvasSettings.Projection.Enabled) return;
        CancelCanvasGridCalibration();
        _editor.Cancel(); _canvasGridScale.Cancel(); _canvasCalibration.End();
        if (_editor.Tool != EditorTool.Canvas) SwitchToolState(EditorTool.Canvas);
        _canvasGridCalibrationSession = Active; _canvasGridCalibrationRevision = Active.SceneRevision;
        _canvasGridCalibrationTarget = CaptureCanvasSettingsTarget(); _canvasSamplingInitial = _canvasGridCalibrationTarget.Settings.Clone();
        _canvasGridSafety = stage == CanvasSamplingStage.Lines ? null : new(_canvasGridCalibrationTarget.CreateDisplayDocument(), EditingCanvasView);
        _canvasGridCalibrationEditingVersion = _host.EditingSessionVersion;
        _canvasSamplingStage = stage; _canvasGridDraft = new();
        _canvasReferenceLines.Clear(); _canvasSampleStart = _canvasSampleEnd = null;
        _canvasSampleDragging = false; _canvasLineHandle = -1; _canvasGridCalibrationDialogPending = false;
        if (_canvasPlacementSession is null)
        {
            _canvasPlacementSession = Active; _canvasPlacementEditingVersion = _host.EditingSessionVersion;
            _canvasPlacementTarget = _canvasGridCalibrationTarget;
            _canvasPlacementWindows = _app.Windows.Cast<Window>().Where(w => w.IsVisible && !ReferenceEquals(w, _toolbar)).ToArray();
            _windowCoordinator?.HideTemporarily(_canvasPlacementWindows);
        }
        SetCanvasSampleHint(); _canvasDraftRevision++; RequestRender();
    }
    private bool CanvasSamplingCurrent => _canvasGridCalibrationSession is { } session &&
        ReferenceEquals(session, Active) && _editor.Tool == EditorTool.Canvas && !DocumentOperationBusy && _canvasGridCalibrationTarget is { IsCurrent: true, IsWritable: true } &&
        session.SceneRevision == _canvasGridCalibrationRevision && !session.IsTransactionActive &&
        _host.IsEditing && _host.IsVisible && _host.EditingSessionVersion == _canvasGridCalibrationEditingVersion;

    private void ClearCanvasGridCalibration()
    {
        if (_canvasGridCalibrationSession is null && _canvasGridCalibrationPreview is null && _canvasSamplingInitial is null) return;
        _canvasGridCalibrationSession = null; _canvasGridCalibrationTarget = null; _canvasSamplingInitial = null;
        _canvasReferenceLines.Clear(); _canvasSampleStart = _canvasSampleEnd = null; _canvasGridCalibrationPreview = null;
        _canvasSampleDragging = false; _canvasGridDraft = null; _canvasGridSafety = null; _canvasGridIssueHighlight = null; _canvasLineHandle = -1;
        _canvasSampleLoupe?.Dispose(); _canvasSampleLoupe = null;
        _canvasGridCalibrationDialogPending = false; _canvasDraftRevision++;
        var dialog = _canvasGridCalibrationWindow; _canvasGridCalibrationWindow = null;
        if (dialog is { IsVisible: true }) SessionWindowCoordinator.Close(dialog);
    }
    private void CancelCanvasGridCalibration()
    {
        if (_canvasGridCommitting) return;
        if (_canvasGridCalibrationSession is null) return;
        ClearCanvasGridCalibration(); RestoreCanvasPlacementWindows(); SynchronizeCanvasTool(); RequestRender();
    }
    private void SetCanvasSampleHint(string? message = null)
    {
        _canvasSampleHint = message ?? (_canvasSamplingStage == CanvasSamplingStage.Lines
            ? _canvasReferenceLines.Count < 4 ? L.Format("Canvas.LineStep", L.Get("Canvas.Line" + _canvasReferenceLines.Count)) : L.Get("Canvas.LineReview")
            : _canvasSamplingStage == CanvasSamplingStage.AdjustRectangle ? L.Get("Canvas.SampleAdjustHint") : L.Get("Canvas.SampleDragHint"));
        UpdateStatus(_canvasSampleHint); RequestRender();
    }
    private double CanvasSampleDip => (_host.Monitors.FirstOrDefault(m => m.Bounds.Contains(_canvasSamplePointer)) ?? _host.PrimaryMonitor).DpiX / 96d;
    private bool TryCanvasSample(out RectD sample, out PointD[] corners)
    {
        sample = default; corners = [];
        return _canvasSamplingInitial is not null && _canvasSampleStart is { } a && _canvasSampleEnd is { } b &&
            CanvasGridCalibration.TrySample(_canvasSamplingInitial.Projection, a, b, out sample, out corners);
    }
    private bool ValidCanvasSample()
    {
        if (!TryCanvasSample(out _, out var corners)) return false;
        double screen = EditingCanvasView.Zoom / CanvasSampleDip;
        return corners[0].DistanceTo(corners[1]) * screen >= 8 && corners[0].DistanceTo(corners[3]) * screen >= 8;
    }
    private bool HandleCanvasGridCalibrationPointer(OverlayPointerEvent input)
    {
        if (_canvasGridCalibrationSession is null) return false;
        if (!CanvasSamplingCurrent || input.Kind == OverlayPointerKind.Cancel) { CancelCanvasGridCalibration(); return true; }
        if (_canvasSamplingStage == CanvasSamplingStage.Dialog) return true;
        _canvasSamplePointer = input.Position;
        if (input.Kind == OverlayPointerKind.Down && input.Button == OverlayMouseButton.Right) { EscapeCanvasSample(); return true; }
        var view = new PointD(input.Position.X - _editor.CanvasOrigin.X, input.Position.Y - _editor.CanvasOrigin.Y);
        var point = EditingCanvasView.ToCanvas(view);
        if (input.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && input.Button == OverlayMouseButton.Left)
        {
            for (int i = 0; i < 3; i++) if (CanvasSampleActionBounds(i).Contains(view))
            {
                if (i == 0) QueueConfirmCanvasSample();
                else if (i == 1) { _canvasReferenceLines.Clear(); _canvasSampleStart = _canvasSampleEnd = null; SetCanvasSampleHint(); }
                else EscapeCanvasSample();
                return true;
            }
            _canvasSampleDragging = true;
            if (_canvasSamplingStage == CanvasSamplingStage.Lines)
            {
                _canvasLineHandle = -1;
                for (int i = 0; _canvasReferenceLines.Count == 4 && i < _canvasReferenceLines.Count; i++)
                    for (int j = 0; j < 2; j++)
                        if (EditingCanvasView.ToView(j == 0 ? _canvasReferenceLines[i].Start : _canvasReferenceLines[i].End).DistanceTo(view) < 12 * CanvasSampleDip)
                            _canvasLineHandle = i * 2 + j;
                if (_canvasLineHandle < 0) { if (_canvasReferenceLines.Count == 4) _canvasSampleDragging = false; else _canvasSampleStart = _canvasSampleEnd = point; }
            }
            else
            {
                var opposite = point;
                if (_canvasSamplingStage == CanvasSamplingStage.AdjustRectangle && TryCanvasSample(out _, out var corners))
                    for (int i = 0; i < 4; i++) if (EditingCanvasView.ToView(corners[i]).DistanceTo(view) < 12 * CanvasSampleDip) { opposite = corners[(i + 2) % 4]; break; }
                _canvasSampleStart = opposite; _canvasSampleEnd = point;
            }
        }
        else if (_canvasSampleDragging && input.Kind is OverlayPointerKind.Move or OverlayPointerKind.Up)
        {
            if (_canvasSamplingStage == CanvasSamplingStage.Lines && _canvasLineHandle >= 0)
            {
                int i = _canvasLineHandle / 2; var line = _canvasReferenceLines[i];
                _canvasReferenceLines[i] = _canvasLineHandle % 2 == 0 ? line with { Start = point } : line with { End = point };
            }
            else _canvasSampleEnd = point;
            if (input.Kind == OverlayPointerKind.Up && input.Button == OverlayMouseButton.Left)
            {
                _canvasSampleDragging = false; _canvasSampleLoupe?.Dispose(); _canvasSampleLoupe = null;
                if (_canvasSamplingStage == CanvasSamplingStage.Lines)
                {
                    if (_canvasLineHandle < 0 && _canvasSampleStart is { } a && _canvasSampleEnd is { } b)
                    {
                        if (a.DistanceTo(b) * EditingCanvasView.Zoom / CanvasSampleDip >= 8) _canvasReferenceLines.Add(new(a, b));
                        else SetCanvasSampleHint(L.Get("Canvas.LineFit.ShortLine"));
                    }
                    _canvasSampleStart = _canvasSampleEnd = null; _canvasLineHandle = -1;
                    if (_canvasReferenceLines.Count == 4 && !CanvasLineCalibration.TryGetQuad(_canvasReferenceLines, CanvasSampleDip / EditingCanvasView.Zoom, out _, out var error))
                        SetCanvasSampleHint(L.Get("Canvas.LineFit." + error));
                    else SetCanvasSampleHint();
                }
                else if (!ValidCanvasSample()) SetCanvasSampleHint(L.Get("Canvas.SampleTooSmall"));
                else if (_canvasSamplingStage == CanvasSamplingStage.Rectangle) QueueConfirmCanvasSample();
            }
        }
        if (_canvasSampleDragging && Environment.TickCount64 - _canvasSampleLoupeTick > 33)
        {
            _canvasSampleLoupeTick = Environment.TickCount64;
            _canvasSampleLoupe?.Dispose(); _canvasSampleLoupe = DesktopPixelSampler.SamplePatch(input.Position, 40);
        }
        RequestRender(); return true;
    }
    private bool HandleCanvasSampleKey(int key)
    {
        if (_canvasGridCalibrationSession is null || _canvasSamplingStage == CanvasSamplingStage.Dialog ||
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;
        if (!CanvasSamplingCurrent) { CancelCanvasGridCalibration(); return true; }
        if (key == 0x1B) EscapeCanvasSample();
        else if (key == 0x0D) QueueConfirmCanvasSample();
        else if (key == 0x08 && _canvasSamplingStage == CanvasSamplingStage.Lines)
        {
            if (_canvasReferenceLines.Count > 0) _canvasReferenceLines.RemoveAt(_canvasReferenceLines.Count - 1);
            _canvasSampleStart = _canvasSampleEnd = null; _canvasSampleDragging = false; SetCanvasSampleHint();
        }
        // Sampling owns canvas input; editing shortcuts must not mutate the sampled document.
        return true;
    }
    private void EscapeCanvasSample()
    {
        if (_canvasSamplingStage == CanvasSamplingStage.AdjustRectangle)
        {
            (_canvasSampleStart, _canvasSampleEnd) = _canvasSampleBeforeAdjust;
            _canvasSampleDragging = false; QueueConfirmCanvasSample(resetCandidates: false);
        }
        else CancelCanvasGridCalibration();
    }
    private void QueueConfirmCanvasSample(bool resetCandidates = true)
    {
        if (_canvasGridCalibrationDialogPending || _canvasSampleDragging) return;
        var target = _canvasGridCalibrationTarget;
        if (target is null) return;
        _canvasGridCalibrationDialogPending = true;
        _app.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(target, _canvasGridCalibrationTarget)) return;
            _canvasGridCalibrationDialogPending = false;
            if (!CanvasSamplingCurrent) { CancelCanvasGridCalibration(); return; }
            if (_canvasSamplingStage == CanvasSamplingStage.Lines) ConfirmCanvasLines(target);
            else if (!ValidCanvasSample()) SetCanvasSampleHint(L.Get("Canvas.SampleTooSmall"));
            else { if (resetCandidates) _canvasGridDraft!.ResetCandidates(); FinishCanvasGridCalibration(target); }
        }));
    }
    private void ConfirmCanvasLines(CanvasSettingsTarget target)
    {
        if (!CanvasLineCalibration.TryGetQuad(_canvasReferenceLines, CanvasSampleDip / EditingCanvasView.Zoom, out var quad, out var error))
        { SetCanvasSampleHint(L.Get("Canvas.LineFit." + error)); return; }
        if (!CanvasCalibration.TryCalibrate(target.Settings.Projection, quad, target.Viewport, out _, out var geometryError))
        { SetCanvasSampleHint(L.Get(geometryError ?? "Canvas.InvalidQuad")); return; }
        // Reuse the four-point transaction and its unsafe-content resolution, not a second write path.
        ClearCanvasGridCalibration();
        _calibrationSession = Active; _calibrationTarget = target;
        _canvasCalibration.BeginPlacement(Active, target);
        foreach (var point in quad) _canvasCalibration.Down(point);
        _canvasCalibration.Up();
        if (!_canvasCalibration.IsAwaitingResolution) RestoreCanvasPlacementWindows();
        ScheduleRecovery(); RequestRender();
    }
    private void FinishCanvasGridCalibration(CanvasSettingsTarget target)
    {
        if (!ReferenceEquals(target, _canvasGridCalibrationTarget) || !CanvasSamplingCurrent || !TryCanvasSample(out var sample, out _)) return;
        _canvasSamplingStage = CanvasSamplingStage.Dialog;
        bool adjusting = false;
        try
        {
            var dialog = new CanvasGridCalibrationWindow(_toolbar, _canvasSamplingInitial!, sample, _canvasGridDraft!, settings =>
            {
                if (!ReferenceEquals(target, _canvasGridCalibrationTarget) || !CanvasSamplingCurrent) return;
                _canvasGridCalibrationPreview = settings?.Clone();
                if (_canvasGridCalibrationPreview is { } preview)
                { preview.Grid.Visible = true; if (preview.Grid.Width <= 0) preview.Grid.Width = 1; if ((preview.Grid.Color >> 24) == 0) preview.Grid.Color = 0xAA23B5CC; }
                _canvasDraftRevision++; RequestRender();
            }, async (settings, cancellationToken) =>
            {
                if (_canvasGridSafety is not { } safety) return new(0, TargetInvalid: true);
                var check = await safety.Check(settings, cancellationToken);
                return !ReferenceEquals(target, _canvasGridCalibrationTarget) || !target.IsCurrent ||
                    Active.SceneRevision != _canvasGridCalibrationRevision ? new(0, TargetInvalid: true) : check;
            }, settings =>
            {
                if (DocumentOperationBusy || Active.IsTransactionActive) return L.Get("Inspector.BusyLayersRetained");
                if (!ReferenceEquals(target, _canvasGridCalibrationTarget) || !CanvasSamplingCurrent) return L.Get("Layers.StaleMove");
                // The validated content revision is still current. Only this candidate's settings change.
                _canvasGridCommitting = true;
                try
                {
                    if (!target.Replace(settings, L.Get("Canvas.GridCalibration"))) return L.Get("Layers.StaleMove");
                    ScheduleRecovery(); return null;
                }
                finally { _canvasGridCommitting = false; }
            }, issue =>
            {
                if (issue is not null && (!ReferenceEquals(target, _canvasGridCalibrationTarget) || !CanvasSamplingCurrent)) return;
                _canvasGridIssueHighlight = issue; RequestRender();
            });
            _canvasGridCalibrationWindow = dialog; ConfigureAppWindow(dialog); SessionWindowCoordinator.ShowModal(dialog);
            if (!ReferenceEquals(target, _canvasGridCalibrationTarget) || !CanvasSamplingCurrent) return;
            if (dialog.AdjustRequested)
            {
                adjusting = true; _canvasSamplingStage = CanvasSamplingStage.AdjustRectangle;
                _canvasSampleBeforeAdjust = (_canvasSampleStart, _canvasSampleEnd); _canvasGridCalibrationPreview = null;
                _canvasGridCalibrationWindow = null; _canvasDraftRevision++; SetCanvasSampleHint(); return;
            }
        }
        finally
        {
            if (!adjusting && (_canvasGridCalibrationTarget is null || ReferenceEquals(target, _canvasGridCalibrationTarget)))
            { ClearCanvasGridCalibration(); RestoreCanvasPlacementWindows(); SynchronizeCanvasTool(); RefreshCanvasSettingsTarget(); RequestRender(); }
        }
    }
    private RectD CanvasSampleActionBounds(int index)
    {
        var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(_canvasSamplePointer)) ?? _host.PrimaryMonitor; double dip = monitor.DpiX / 96d;
        return new(monitor.WorkArea.Left - _editor.CanvasOrigin.X + (20 + index * 100) * dip,
            monitor.WorkArea.Bottom - _editor.CanvasOrigin.Y - 52 * dip, 92 * dip, 32 * dip);
    }
    private void DrawCanvasGridCalibration(SKCanvas canvas)
    {
        if (_canvasSamplingStage == CanvasSamplingStage.Dialog)
        {
            if (_canvasGridIssueHighlight is { OriginalBounds: { } bounds } issue && CanvasSamplingCurrent)
            {
                using var outline = new SKPaint { Color = new SKColor(245, 155, 35), StrokeWidth = (float)(2 * CanvasSampleDip), IsAntialias = true, Style = SKPaintStyle.Stroke };
                var box = bounds.Inflate(4 * CanvasSampleDip);
                canvas.DrawRect(new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom), outline);
                using var issueTypeface = SKTypeface.FromFamilyName("Microsoft YaHei");
                using var issueFont = new SKFont(issueTypeface, (float)(13 * CanvasSampleDip)); outline.Style = SKPaintStyle.Fill;
                canvas.DrawText(issue.Name, (float)box.Left, (float)(box.Top - 5 * CanvasSampleDip), SKTextAlign.Left, issueFont, outline);
            }
            return;
        }
        double dip = CanvasSampleDip;
        SKColor ThemeColor(string key, SKColor fallback)
        {
            if (_toolbar.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
            { var c = brush.Color; return new(c.R, c.G, c.B, c.A); }
            return fallback;
        }
        var horizontalColor = ThemeColor("CanvasAccent", new(30, 190, 185));
        var verticalColor = ThemeColor("CanvasFileModified", new(235, 158, 45));
        using var paint = new SKPaint { Color = horizontalColor, StrokeWidth = (float)(2 * dip), IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
        using var font = new SKFont(typeface, (float)(13 * dip));
        void Dot(PointD p, string? label = null)
        {
            p = EditingCanvasView.ToView(p); canvas.DrawCircle((float)p.X, (float)p.Y, (float)(6 * dip), paint);
            if (label is not null) { paint.Style = SKPaintStyle.Fill; canvas.DrawText(label, (float)(p.X + 10 * dip), (float)(p.Y - 10 * dip), SKTextAlign.Left, font, paint); paint.Style = SKPaintStyle.Stroke; }
        }
        if (_canvasSamplingStage == CanvasSamplingStage.Lines)
        {
            for (int i = 0; i < _canvasReferenceLines.Count; i++)
            {
                var l = _canvasReferenceLines[i]; var a = EditingCanvasView.ToView(l.Start); var b = EditingCanvasView.ToView(l.End);
                paint.Color = i < 2 ? horizontalColor : verticalColor;
                using (var dash = SKPathEffect.CreateDash([6, 6], 0))
                {
                    paint.PathEffect = dash; var d = b - a; double length = Math.Max(1, a.DistanceTo(b)); d *= 10000 / length;
                    canvas.DrawLine((float)(a.X - d.X), (float)(a.Y - d.Y), (float)(b.X + d.X), (float)(b.Y + d.Y), paint); paint.PathEffect = null;
                }
                canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, paint); Dot(l.Start, L.Get("Canvas.Line" + i)); Dot(l.End);
            }
            if (_canvasSampleStart is { } start && _canvasSampleEnd is { } end)
                DrawScreenPolygon(canvas, new[] { EditingCanvasView.ToView(start), EditingCanvasView.ToView(end) }, paint);
            if (CanvasLineCalibration.TryGetQuad(_canvasReferenceLines, dip / EditingCanvasView.Zoom, out var quad, out _))
            { paint.Color = SKColors.White; DrawScreenPolygon(canvas, quad.Select(EditingCanvasView.ToView).ToArray(), paint); }
        }
        else if (TryCanvasSample(out _, out var corners))
        { DrawScreenPolygon(canvas, corners.Select(EditingCanvasView.ToView).ToArray(), paint); foreach (var p in corners) Dot(p); }
        var first = CanvasSampleActionBounds(0); paint.Style = SKPaintStyle.Fill; paint.Color = new(20, 31, 43, 240);
        float hintWidth = Math.Min((float)(_host.PrimaryMonitor.WorkArea.Width - 40 * dip), Math.Max((float)(300 * dip), font.MeasureText(_canvasSampleHint) + (float)(24 * dip)));
        canvas.DrawRoundRect((float)first.X, (float)(first.Y - 40 * dip), hintWidth, (float)(32 * dip), 6, 6, paint);
        paint.Color = SKColors.White; canvas.DrawText(_canvasSampleHint, (float)(first.X + 8 * dip), (float)(first.Y - 18 * dip), SKTextAlign.Left, font, paint);
        for (int i = 0; i < 3; i++)
        {
            var r = CanvasSampleActionBounds(i); paint.Color = i == 0 ? new(20, 110, 125) : new(35, 45, 60);
            canvas.DrawRoundRect((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, 6, 6, paint);
            paint.Color = SKColors.White; canvas.DrawText(L.Get(i == 0 ? "Canvas.SampleConfirm" : i == 1 ? "Canvas.SampleRedraw" : "Dialog.Cancel"),
                (float)(r.X + 8 * dip), (float)(r.Y + 22 * dip), SKTextAlign.Left, font, paint);
        }
        if (_canvasSampleDragging && _canvasSampleLoupe is { } bitmap)
        {
            var m = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(_canvasSamplePointer)) ?? _host.PrimaryMonitor;
            float size = (float)(144 * dip), x = (float)Math.Clamp(_canvasSamplePointer.X + 36 * dip, m.WorkArea.Left, m.WorkArea.Right - size) - (float)_editor.CanvasOrigin.X;
            float y = (float)Math.Clamp(_canvasSamplePointer.Y + 36 * dip, m.WorkArea.Top, m.WorkArea.Bottom - size) - (float)_editor.CanvasOrigin.Y;
            canvas.DrawBitmap(bitmap, new SKRect(x, y, x + size, y + size), new SKSamplingOptions(SKFilterMode.Nearest));
            paint.Style = SKPaintStyle.Stroke; paint.Color = SKColors.White; paint.StrokeWidth = (float)dip;
            canvas.DrawRect(x, y, size, size, paint); canvas.DrawLine(x + size / 2 - 10, y + size / 2, x + size / 2 + 10, y + size / 2, paint);
            canvas.DrawLine(x + size / 2, y + size / 2 - 10, x + size / 2, y + size / 2 + 10, paint);
        }
    }
}
