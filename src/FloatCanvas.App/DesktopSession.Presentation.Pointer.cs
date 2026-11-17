using System.Runtime.InteropServices;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PresentationFrame? _pointerPresentationFrame, _lastKeyboardPresentationFrame;
    private PresentationPointerSettings _pointerPresentationSettings = new();
    private object? _lastKeyboardPresentationLifetime;

    private bool IsPresentationInputInternal()
    {
        if (_host.IsEditing) return true;
        nint foreground = PresentationGetForegroundWindow();
        return foreground != 0 && PresentationGetWindowThreadProcessId(foreground, out uint process) != 0 && process == Environment.ProcessId;
    }

    private void ApplyPresentationGlobalSettings(PresentationGlobalSettings value)
    {
        if (!value.IsValid()) throw new ArgumentException("Invalid presentation preferences.", nameof(value));
        var previous = _settings.PresentationGlobal;
        _settings.PresentationGlobal = value;
        try { _settings.Save(); }
        catch { _settings.PresentationGlobal = previous; throw; }
        SynchronizePointerToolPreferences();
        UpdatePresentationRuntime();
        UpdateHiddenCanvasState();
        _presentationRuntime?.RetryIfFaulted();
        UpdatePresentationPixels();
        _toolOptionsPanel?.RefreshPresentationGlobalUi();
    }

    private void TogglePresentationPointer()
    {
        var value = _settings.PresentationGlobal.Clone();
        value.Pointer.Enabled = !value.Pointer.Enabled;
        ApplyPresentationGlobalSettings(value);
    }

    private void UpdatePresentationPixels()
    {
        if (_disposed) return;
        var frame = _presentationRuntime?.Observing == true ? _presentationRuntime.Frame : null;
        var settings = _settings.PresentationGlobal.Pointer;
        var before = _pointerPresentationFrame;
        var priorSettings = _pointerPresentationSettings;
        _pointerPresentationFrame = settings.AnyEnabled && _host.IsVisible ? _presentationRuntime?.PointerFrame : null;
        _pointerPresentationSettings = settings;
        var damage = new List<PixelBounds>();
        foreach (var monitor in _host.Monitors)
        {
            var viewport = new RectD(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
            var regions = PresentationPointerRenderer.DirtyBounds(before, priorSettings, viewport)
                .Concat(PresentationPointerRenderer.DirtyBounds(_pointerPresentationFrame, settings, viewport)).ToArray();
            if (regions.Length == 0) continue;
            var union = regions.Aggregate(RectD.Union).Inflate(1).Intersect(viewport);
            int left = (int)Math.Floor(union.Left), top = (int)Math.Floor(union.Top);
            int right = (int)Math.Ceiling(union.Right), bottom = (int)Math.Ceiling(union.Bottom);
            damage.Add(new(left, top, right - left, bottom - top));
        }
        if (damage.Count > 0) QueueCanvasPresentation(damage);

        var keyboardFrame = _presentationRuntime?.ObservingKeyboard == true ? frame : null;
        object? lifetime = keyboardFrame is not null ? _presentationRuntime!.Lifetime : null;
        bool keyboardChanged = !ReferenceEquals(_lastKeyboardPresentationLifetime, lifetime) ||
            !SameKeyboardPresentation(_lastKeyboardPresentationFrame, keyboardFrame) || _presentationRuntime?.HasKeyboardAnimation == true;
        _lastKeyboardPresentationFrame = keyboardFrame; _lastKeyboardPresentationLifetime = lifetime;
        if (keyboardChanged)
        { _presentationPixelsDirty = true; RequestPixelRender(Array.Empty<PixelBounds>()); }
    }

    private static bool SameKeyboardPresentation(PresentationFrame? first, PresentationFrame? second)
    {
        if (ReferenceEquals(first, second)) return true;
        return first is not null && second is not null && SameKeyboardScope(first, second) &&
            SameKeyboardScope(first.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas), second.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas)) &&
            SameKeyboardScope(first.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly), second.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly));
    }
    private static bool SameKeyboardScope(PresentationFrame first, PresentationFrame second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.KeyboardSuppressed != second.KeyboardSuppressed || first.RecentChords.Count != second.RecentChords.Count) return false;
        if (!first.PressedKeys.SequenceEqual(second.PressedKeys)) return false;
        for (int i = 0; i < first.RecentChords.Count; i++)
        {
            var a = first.RecentChords[i]; var b = second.RecentChords[i];
            if (a.IsHeld != b.IsHeld || a.Timestamp != b.Timestamp || !a.Keys.SequenceEqual(b.Keys)) return false;
        }
        return true;
    }

    private void DrawPresentationPointer(SKCanvas canvas)
    {
        if (!_host.IsVisible || _pointerPresentationFrame is not { } frame || !_pointerPresentationSettings.AnyEnabled) return;
        double opacity = CurrentCanvasLayerOpacity;
        if (opacity <= 0) return;
        var clip = canvas.LocalClipBounds;
        int saved = canvas.SaveCount;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)) };
        if (opacity < 1) canvas.SaveLayer(paint);
        try
        {
            PresentationPointerRenderer.Draw(canvas, frame, _pointerPresentationSettings,
                new(clip.Left, clip.Top, clip.Width, clip.Height));
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private bool HasPresentationLayerGuides => _host.IsVisible && _presentationLeaves.Any(IsLogPresentation);
    private static bool IsLogPresentation(ResolvedLeaf leaf) => leaf.RenderElement.Presentation?.DisplayStyle is PresentationDisplayStyle.Log or PresentationDisplayStyle.Keycaps;

    private void DrawPresentationLayerGuides(SKCanvas canvas)
    {
        if (!HasPresentationLayerGuides) return;
        using var dash = SKPathEffect.CreateDash([5, 4], 0);
        using var outline = new SKPaint { Color = new SKColor(90, 167, 245, 170), Style = SKPaintStyle.Stroke,
            StrokeWidth = 1, IsAntialias = true, PathEffect = dash };
        using var fill = new SKPaint { Color = new SKColor(90, 167, 245, 12), IsAntialias = true };
        // Guides are editing adorners, never scene content or capture pixels.
        foreach (var leaf in _presentationLeaves.Where(IsLogPresentation).Take(PresentationLimits.MaximumPrimitives / 8))
        {
            var display = _presentationDisplayDocument ?? Active.Document;
            var mapping = CanvasMapping.CreateViewing(display, EditingCanvasView, leaf.RootCanvas);
            if (!PresentationGeometry.TryScreenRegion(leaf.RenderElement, mapping, display.Viewport, out var polygon)) continue;
            using var builder = new SKPathBuilder(); builder.MoveTo((float)polygon[0].X, (float)polygon[0].Y);
            foreach (var point in polygon.Skip(1)) builder.LineTo((float)point.X, (float)point.Y);
            builder.Close(); using var path = builder.Detach();
            canvas.DrawPath(path, fill); canvas.DrawPath(path, outline);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PresentationGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint PresentationGetWindowThreadProcessId(nint window, out uint processId);
}
