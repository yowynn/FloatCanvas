using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Resizes a presentation frame while keeping its font tied to the vertical scale.</summary>
internal static class PresentationFrameLayout
{
    // The renderer fits the enabled layout vertically, then applies horizontal stretch.
    // Preserve that coordinate system when its visible bounds change, including moved keys.
    internal static bool ReframeKeyboard(CanvasElement target, CanvasElement source, out PointD shift)
    {
        shift = default;
        if (source.Presentation?.DisplayStyle != PresentationDisplayStyle.Keyboard ||
            target.Presentation?.DisplayStyle != PresentationDisplayStyle.Keyboard) return false;
        var before = Bounds(source.Presentation); var after = Bounds(target.Presentation);
        if (before.IsEmpty || after.IsEmpty) return false;
        var old = Geometry(source, before);
        double font = target.Style.FontSize;
        double ratio = (after.Width / .36 + .7) / (after.Height / .36 + .7);
        double cap = Math.Min(1, ratio) / 8;
        double pad = Math.Min(font * .35, after.Height * old.Unit.Y * cap / (1 - 2 * cap));
        target.Height = after.Height * old.Unit.Y + 2 * pad;
        target.Width = ratio * target.Height * old.Unit.X / old.Unit.Y;
        shift = old.Origin - Geometry(target, after).Origin;
        return true;

        static RectD Bounds(PresentationSettings settings)
        {
            var keys = (settings.KeyboardLayout ?? PresentationKeyboardLayout.CreateDefault()).Keys.Where(key => key.Enabled).ToArray();
            if (keys.Length == 0) return default;
            double x = keys.Min(key => key.X), y = keys.Min(key => key.Y);
            return new(x, y, keys.Max(key => key.X + key.Width) - x, keys.Max(key => key.Y + key.Height) - y);
        }
        static (PointD Origin, PointD Unit) Geometry(CanvasElement element, RectD bounds)
        {
            double width = (bounds.Width / .36 + .7) / (bounds.Height / .36 + .7) * element.Height;
            double pad = Math.Min(element.Style.FontSize * .35, Math.Min(width, element.Height) / 8);
            double unit = (element.Height - 2 * pad) / bounds.Height, stretch = element.Width / width;
            return (new((width / 2 - bounds.Width * unit / 2 - bounds.X * unit) * stretch, pad - bounds.Y * unit), new(unit * stretch, unit));
        }
    }

    internal static void Resize(CanvasElement target, CanvasElement source, double x, double y)
    {
        var center = ElementGeometry.LocalToPlane(source).TransformPoint(new(source.Width / 2, source.Height / 2));
        target.Width = Math.Max(.01, source.Width * x);
        target.Height = Math.Max(.01, source.Height * y);
        target.Style.FontSize = Math.Clamp(source.Style.FontSize * y, 1, 4096);
        target.Transform = source.Transform;
        var moved = ElementGeometry.LocalToPlane(target).TransformPoint(new(target.Width / 2, target.Height / 2));
        target.Transform = Affine2D.Translation(center.X - moved.X, center.Y - moved.Y).Compose(target.Transform);
    }
}
