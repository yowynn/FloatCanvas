using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>The actual text-window commit operation; presentation decides whether to close on success.</summary>
internal static class TextEditCommand
{
    internal static bool TryCommit(EditorSession session, Guid? elementId, ElementKind kind, string text,
        ElementStyle newStyle, PointD point, SceneRenderer renderer, out string? error)
    {
        error = null;
        if (kind is not (ElementKind.Text or ElementKind.Number) || elementId is null && kind != ElementKind.Text)
        { error = L.Get("Text.TargetTypeChanged"); return false; }
        CanvasElement? existing = null;
        if (elementId is { } id)
        {
            existing = session.Document.Elements.FirstOrDefault(e => e.Id == id);
            if (existing is null) { error = L.Get("Text.TargetMissing"); return false; }
            if (existing.Kind != kind) { error = L.Get("Text.TargetTypeChanged"); return false; }
            if (existing.Locked) { error = L.Get("Text.TargetLocked"); return false; }
        }
        if (session.IsTransactionActive) { error = L.Get("Text.ActionInProgress"); return false; }
        if (kind == ElementKind.Number)
        {
            if (!int.TryParse(text, out var number) || number is < 1 or > 1_000_000)
            { error = L.Get("Text.InvalidNumber"); return false; }
            session.Execute(L.Get("History.EditNumber"), _ => existing!.Number = number);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            var style = existing?.Style.Clone() ?? newStyle.Clone();
            var metrics = renderer.MeasureText(text, style, existing?.Width ?? 360);
            if (existing is not null)
                session.Execute(L.Get("History.EditText"), _ =>
                {
                    existing.Text = text; existing.Height = Math.Max(1, metrics.Height);
                    if (EmojiGlyphs.IsEmoji(text, style)) existing.Width = metrics.Width;
                });
            else session.Add(new CanvasElement { Kind = ElementKind.Text, Text = text, Style = style,
                Width = Math.Max(40, Math.Min(360, metrics.Width + 4)), Height = Math.Max(style.FontSize, metrics.Height),
                Transform = Affine2D.Translation(point.X, point.Y) });
        }
        return true;
    }
}
