using System.Runtime.InteropServices;
using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private readonly Dictionary<HighlighterKey, HighlighterStroke> _highlighterStrokes = [];
    private Guid? _highlighterWorkingId;
    private HighlighterInkProfile? _lastInkProfile;
    internal long HighlighterRasterBuildCount { get; private set; }
    internal long HighlighterRasterBytes => _highlighterStrokes.Values.Sum(stroke => stroke.ImageBytes);

    private bool TryDrawHighlighterDeposits(SKCanvas canvas, CanvasElement element, double opacity)
    {
        var style = element.Style;
        var ink = HighlighterInk(new SKColor(style.Foreground));
        if (ink.Alpha == 0 || opacity <= 0 || style.StrokeWidth <= 0 || element.Points.Count == 0) return true;
        var matrix = canvas.TotalMatrix;
        var transform = new Projective2D(matrix.ScaleX, matrix.SkewX, matrix.TransX,
            matrix.SkewY, matrix.ScaleY, matrix.TransY, matrix.Persp0, matrix.Persp1, matrix.Persp2);
        if (!transform.TryInverse(out var inverse)) return false;
        var clip = canvas.DeviceClipBounds;
        if (clip.IsEmpty) return true;

        var key = new HighlighterKey(element.Id, transform, clip, style.StrokeWidth, style.StrokePattern,
            style.HighlighterTip, style.HighlighterTipAngle, style.HighlighterInkContrast, ink, opacity);
        PointCollection points = element.Points;
        bool keep = CacheByteLimit > 0;
        _highlighterStrokes.TryGetValue(key, out var stroke);
        if (stroke is null || !stroke.Points.ContentEquals(points))
        {
            bool append = stroke is { CanExtend: true } &&
                style.StrokePattern == StrokePattern.Solid && points.StartsWith(stroke.Points);
            if (!append)
            {
                if (_highlighterStrokes.Remove(key, out var previous)) previous.Dispose();
                if (_highlighterWorkingId is { } workingId)
                    foreach (var pair in _highlighterStrokes)
                        if (pair.Key.Id == workingId && (workingId != key.Id || !pair.Key.SameInk(key)))
                            pair.Value.Finish();
                if (_lastInkProfile is null || _lastInkProfile.Alpha != ink.Alpha || _lastInkProfile.Contrast != key.Contrast)
                    _lastInkProfile = new(ink.Alpha, key.Contrast);
                stroke = new HighlighterStroke(key, inverse, _lastInkProfile);
                _highlighterStrokes.Add(key, stroke);
                _highlighterWorkingId = key.Id;
            }
            if (style.StrokePattern == StrokePattern.Solid)
                stroke!.Extend(points);
            else
                foreach (var run in ElementGeometry.HighlighterRuns(points, style)) stroke!.AddRun(run);
            stroke!.Points = points.Snapshot();
            HighlighterRasterBuildCount++;
        }
        stroke.Used = _clock;
        stroke.Draw(canvas);
        if (!keep)
        {
            stroke.Dispose();
            _highlighterStrokes.Remove(key);
        }
        return true;
    }

    private void TrimHighlighterRasters()
    {
        long budget = Math.Clamp(CacheByteLimit, 0, 48L * 1024 * 1024);
        long bytes = HighlighterRasterBytes;
        if (bytes <= budget) return;
        foreach (var pair in _highlighterStrokes.OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (bytes <= budget) break;
            if (budget > 0 && pair.Key.Id == _highlighterWorkingId && pair.Value.Used == _clock) continue;
            bytes -= pair.Value.ImageBytes;
            pair.Value.Dispose();
            _highlighterStrokes.Remove(pair.Key);
        }
    }

    private readonly record struct HighlighterKey(Guid Id, Projective2D Transform, SKRectI Clip,
        double Width, StrokePattern Pattern, HighlighterTip Tip, double Angle, double Contrast, SKColor Ink, double Opacity)
    {
        internal bool SameInk(HighlighterKey other) => Width == other.Width && Pattern == other.Pattern &&
            Tip == other.Tip && Angle == other.Angle && Contrast == other.Contrast && Ink == other.Ink;
    }

    private sealed class HighlighterInkProfile
    {
        private const int LevelsPerDepth = 512;
        private readonly float[] _alphas = new float[8 * LevelsPerDepth + 1];
        private readonly float[] _dotAlphas = new float[256];
        private readonly double _baseDepth;
        internal byte Alpha { get; }
        internal double Contrast { get; }
        internal double CenterDose { get; }

        internal static double FootprintAlpha(byte alpha, double contrast) => alpha * (.93 - .47 * contrast);

        internal HighlighterInkProfile(byte alpha, double contrast)
        {
            Alpha = alpha;
            Contrast = contrast;
            _baseDepth = -Math.Log(1 - FootprintAlpha(alpha, contrast) / 255);
            double straightDepth = -Math.Log(1 - alpha / 255d);
            double baseFourth = Math.Pow(_baseDepth, 4);
            CenterDose = Math.Pow(Math.Pow(straightDepth, 4) - baseFourth, .25);
            for (int i = 0; i < _alphas.Length; i++)
            {
                double dose = i / (double)LevelsPerDepth;
                // Smoothly combine the footprint and the traveled distance in optical depth.
                double depth = Math.Sqrt(Math.Sqrt(baseFourth + dose * dose * dose * dose));
                _alphas[i] = (float)(255 * (1 - Math.Exp(-depth)));
            }
            for (int dots = 1; dots < _dotAlphas.Length; dots++)
                _dotAlphas[dots] = (float)(255 * (1 - Math.Exp(-dots * _baseDepth)));
        }

        // Zero is untouched. A swept sample stores one plus dose, so a stationary tip still has a footprint.
        internal float SweptAlpha(float sample) => sample == 0 ? 0 :
            _alphas[(int)Math.Min((sample - 1) * LevelsPerDepth + .5f, _alphas.Length - 1)];
        internal float DotAlpha(float count) => count < _dotAlphas.Length ? _dotAlphas[(int)count] :
            (float)(255 * (1 - Math.Exp(-count * _baseDepth)));
    }

    private sealed class HighlighterStroke : IDisposable
    {
        private const int TileSize = 64;
        private static readonly SKImageInfo TileInfo = new(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        private readonly Dictionary<(int X, int Y), CoverageTile> _tiles = [];
        private readonly HighlighterKey _key;
        private readonly Projective2D _inverse;
        private readonly bool _translationOnly;
        private readonly int _samplesPerAxis;
        private readonly double _radius, _sin, _cos;
        private readonly PointD _extent;
        private readonly HighlighterInkProfile _profile;
        private readonly uint[] _palette = new uint[256];

        internal HighlighterStroke(HighlighterKey key, Projective2D inverse, HighlighterInkProfile profile)
        {
            _key = key;
            _profile = profile;
            _inverse = inverse;
            _translationOnly = key.Transform with { M13 = 0, M23 = 0 } == Projective2D.Identity;
            // Bound contact-buffer memory on very large outputs.
            _samplesPerAxis = (long)key.Clip.Width * key.Clip.Height <= 10_000_000 ? 2 : 1;
            _radius = key.Width / 2;
            (_sin, _cos) = Math.SinCos(key.Angle * Math.PI / 180);
            _extent = key.Tip == HighlighterTip.Flat
                ? new(_radius * (Math.Abs(_sin) + Math.Abs(_cos) / 4), _radius * (Math.Abs(_cos) + Math.Abs(_sin) / 4))
                : new(_radius, _radius);
            for (uint a = 0; a < _palette.Length; a++)
                _palette[(int)a] = a << 24 | (key.Ink.Red * a + 127) / 255 << 16 |
                    (key.Ink.Green * a + 127) / 255 << 8 | (key.Ink.Blue * a + 127) / 255;
        }

        internal PointCollection Points { get; set; } = [];
        internal bool CanExtend { get; private set; } = true;
        internal long Used { get; set; }
        internal long ImageBytes => (long)_tiles.Count * TileInfo.BytesSize;

        internal void Extend(PointCollection points)
        {
            if (Points.Count == 0) AddSegment(points[0], points[0]);
            for (int i = Math.Max(1, Points.Count); i < points.Count; i++)
                if (points[i] != points[i - 1]) AddSegment(points[i - 1], points[i]);
        }

        internal void AddRun(HighlighterRun run)
        {
            var points = run.Points;
            if (points.Count == 1)
            {
                AddSegment(points[0], points[0], dashDotDab: _key.Pattern == StrokePattern.DashDot);
                return;
            }
            for (int i = 1; i < points.Count; i++)
                AddSegment(points[i - 1], points[i]);
        }

        private void AddSegment(PointD start, PointD end, bool dashDotDab = false)
        {
            var delta = end - start;
            double length = delta.DistanceTo(default);
            var bounds = new RectD(Math.Min(start.X, end.X) - _extent.X, Math.Min(start.Y, end.Y) - _extent.Y,
                Math.Abs(delta.X) + 2 * _extent.X, Math.Abs(delta.Y) + 2 * _extent.Y);
            var clip = _key.Clip;
            int left = clip.Left, top = clip.Top, right = clip.Right, bottom = clip.Bottom;
            if (_key.Transform.TryTransformBounds(bounds, out var screen))
            {
                left = (int)Math.Clamp(Math.Floor(screen.Left), left, right);
                top = (int)Math.Clamp(Math.Floor(screen.Top), top, bottom);
                right = (int)Math.Clamp(Math.Ceiling(screen.Right), left, right);
                bottom = (int)Math.Clamp(Math.Ceiling(screen.Bottom), top, bottom);
            }
            if (left >= right || top >= bottom) return;
            var segment = new InkSegment(start, delta, length, _radius, _sin, _cos, _profile,
                _key.Tip == HighlighterTip.Flat);
            bool dotted = _key.Pattern == StrokePattern.Dotted;
            int samples = _samplesPerAxis * _samplesPerAxis;
            for (int tileY = top & -TileSize; tileY < bottom; tileY += TileSize)
            {
                int rowLeft = left, rowRight = right;
                // A sparse pointer event (or Shift segment) can span a whole monitor diagonally.
                // Visit only the tile columns that the moving nib can reach in this row.
                if (_translationOnly && Math.Abs(delta.Y) > TileSize && Math.Abs(delta.X) > TileSize)
                {
                    double centerY = start.Y - _inverse.M23;
                    double first = Math.Clamp((tileY - _extent.Y - centerY) / delta.Y, 0, 1);
                    double last = Math.Clamp((tileY + TileSize + _extent.Y - centerY) / delta.Y, 0, 1);
                    double x1 = start.X - _inverse.M13 + delta.X * first;
                    double x2 = start.X - _inverse.M13 + delta.X * last;
                    rowLeft = (int)Math.Clamp(Math.Floor(Math.Min(x1, x2) - _extent.X), left, right);
                    rowRight = (int)Math.Clamp(Math.Ceiling(Math.Max(x1, x2) + _extent.X), left, right);
                }
                for (int tileX = rowLeft & -TileSize; tileX < rowRight; tileX += TileSize)
                {
                    var tileKey = (tileX, tileY);
                    _tiles.TryGetValue(tileKey, out var tile);
                    int x0 = Math.Max(rowLeft, tileX), x1 = Math.Min(rowRight, tileX + TileSize);
                    int y0 = Math.Max(top, tileY), y1 = Math.Min(bottom, tileY + TileSize);
                    bool touched = false;
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int index = ((y - tileY) * TileSize + (x - tileX)) * samples;
                        for (int sy = 0; sy < _samplesPerAxis; sy++)
                        for (int sx = 0; sx < _samplesPerAxis; sx++, index++)
                        {
                            var sample = new PointD(x + (sx + .5) / _samplesPerAxis, y + (sy + .5) / _samplesPerAxis);
                            PointD local;
                            if (_translationOnly) local = sample + new PointD(_inverse.M13, _inverse.M23);
                            else if (!_inverse.TryTransformPoint(sample, out local) || _key.Transform.Denominator(local) <= 0) continue;
                            if (!segment.TryAccumulate(local, out float dose)) continue;
                            if (tile is null) _tiles.Add(tileKey, tile = new CoverageTile(samples));
                            if (dashDotDab)
                                (tile.Dabs ??= new float[TileSize * TileSize * samples])[index]++;
                            else
                            {
                                float prior = tile.Amounts![index];
                                tile.Amounts[index] = dotted ? prior + 1 : Math.Max(1, prior) + dose;
                            }
                            touched = true;
                        }
                    }
                    if (touched)
                    {
                        var region = new SKRectI(x0 - tileX, y0 - tileY, x1 - tileX, y1 - tileY);
                        tile!.Dirty = tile.Dirty.IsEmpty ? region : SKRectI.Union(tile.Dirty, region);
                    }
                }
            }
        }

        internal void Draw(SKCanvas canvas)
        {
            int saved = canvas.Save();
            try
            {
                canvas.ResetMatrix();
                int samples = _samplesPerAxis * _samplesPerAxis;
                bool dotted = _key.Pattern == StrokePattern.Dotted;
                double scale = _key.Opacity / samples;
                Span<uint> pixels = stackalloc uint[TileSize * TileSize];
                foreach (var (position, tile) in _tiles)
                {
                    if (!tile.Dirty.IsEmpty)
                    {
                        var region = tile.Dirty;
                        if (region.Width < TileSize || region.Height < TileSize)
                        {
                            using var previous = tile.Image?.PeekPixels();
                            if (previous is not null) previous.GetPixelSpan().CopyTo(MemoryMarshal.AsBytes(pixels));
                            else if (tile.Image is null) pixels.Clear();
                            else region = new(0, 0, TileSize, TileSize);
                        }
                        var amounts = tile.Amounts!;
                        for (int y = region.Top; y < region.Bottom; y++)
                        for (int x = region.Left; x < region.Right; x++)
                        {
                            int i = y * TileSize + x;
                            double alpha = 0;
                            for (int sample = 0; sample < samples; sample++)
                            {
                                int index = i * samples + sample;
                                float amount = amounts[index];
                                float coverage = dotted ? _profile.DotAlpha(amount) : _profile.SweptAlpha(amount);
                                if (tile.Dabs is { } dabs)
                                    coverage += _profile.DotAlpha(dabs[index]) * (1 - coverage / 255);
                                alpha += coverage;
                            }
                            pixels[i] = _palette[(int)(alpha * scale + .5)];
                        }
                        var image = SKImage.FromPixelCopy(TileInfo, MemoryMarshal.AsBytes(pixels));
                        if (image is not null)
                        {
                            tile.Image?.Dispose();
                            tile.Image = image;
                            tile.Dirty = default;
                        }
                    }
                    if (tile.Image is { } raster) canvas.DrawImage(raster, position.X, position.Y, Nearest);
                }
            }
            finally { canvas.RestoreToCount(saved); }
        }

        internal void Finish()
        {
            if (!CanExtend) return;
            CanExtend = false;
            foreach (var tile in _tiles.Values)
            {
                // A failed image allocation can be retried on a later draw.
                if (!tile.Dirty.IsEmpty) continue;
                tile.Amounts = null;
                tile.Dabs = null;
            }
        }

        public void Dispose()
        {
            foreach (var tile in _tiles.Values) tile.Image?.Dispose();
            _tiles.Clear();
        }

        private sealed class CoverageTile(int samples)
        {
            internal float[]? Amounts = new float[TileSize * TileSize * samples];
            internal float[]? Dabs;
            internal SKImage? Image;
            internal SKRectI Dirty;
        }
    }

    private readonly struct InkSegment
    {
        private readonly PointD _start, _unit;
        private readonly double _length, _radiusSquared, _sin, _cos, _inverseRadius;
        private readonly double _longVelocity, _shortVelocity, _inverseLongVelocity, _inverseShortVelocity, _rate;
        private readonly bool _flat;

        internal InkSegment(PointD start, PointD delta, double length, double radius, double sin, double cos,
            HighlighterInkProfile profile, bool flat)
        {
            _start = start;
            _length = length;
            _radiusSquared = radius * radius;
            _unit = length > 0 ? delta * (1 / length) : default;
            _sin = sin;
            _cos = cos;
            _flat = flat;
            _inverseRadius = 1 / radius;
            _rate = profile.CenterDose / (4 * radius / 3);
            if (!flat) return;
            _longVelocity = (-sin * _unit.X + cos * _unit.Y) * _inverseRadius;
            _shortVelocity = (cos * _unit.X + sin * _unit.Y) * (4 * _inverseRadius);
            _inverseLongVelocity = _longVelocity == 0 ? 0 : 1 / _longVelocity;
            _inverseShortVelocity = _shortVelocity == 0 ? 0 : 1 / _shortVelocity;
            if (length > 0)
            {
                double halfChord = 1 / Math.Max(Math.Abs(_longVelocity), Math.Abs(_shortVelocity));
                double u2 = Math.Pow(halfChord * _longVelocity, 2);
                double v2 = Math.Pow(halfChord * _shortVelocity, 2);
                double centerMean = 1 - (u2 + v2) / 3 + u2 * v2 / 5;
                double directionGain = Math.Pow(4 * halfChord / radius, .44 * profile.Contrast);
                _rate = profile.CenterDose * directionGain / (2 * halfChord * centerMean);
            }
        }

        internal bool TryAccumulate(PointD sample, out float dose)
        {
            dose = 0;
            var p = sample - _start;
            double entry = 0, exit = _length, mean;
            if (_flat)
            {
                double u = (-_sin * p.X + _cos * p.Y) * _inverseRadius;
                double v = (_cos * p.X + _sin * p.Y) * (4 * _inverseRadius);
                if (!Clip(u, _inverseLongVelocity, ref entry, ref exit) ||
                    !Clip(v, _inverseShortVelocity, ref entry, ref exit)) return false;
                // Exact mean of (1-u²)(1-v²) over the clipped interval, centered to avoid cancellation.
                double middle = (entry + exit) / 2, halfLength = (exit - entry) / 2;
                double midU = u - _longVelocity * middle, midV = v - _shortVelocity * middle;
                double du = _longVelocity * halfLength, dv = _shortVelocity * halfLength;
                double longWeight = 1 - midU * midU, shortWeight = 1 - midV * midV;
                mean = longWeight * shortWeight +
                    (4 * midU * midV * du * dv - longWeight * dv * dv - shortWeight * du * du) / 3 +
                    du * du * dv * dv / 5;
            }
            else
            {
                if (_length == 0) return p.X * p.X + p.Y * p.Y <= _radiusSquared;
                double along = p.X * _unit.X + p.Y * _unit.Y;
                double perpendicular = p.X * _unit.Y - p.Y * _unit.X;
                double chordSquared = _radiusSquared - perpendicular * perpendicular;
                if (chordSquared < 0) return false;
                double halfChord = Math.Sqrt(chordSquared);
                entry = Math.Max(entry, along - halfChord);
                exit = Math.Min(exit, along + halfChord);
                if (entry > exit) return false;
                // Exact mean of 1-r²/R² along this portion of the circular footprint.
                double offset = along - (exit + entry) / 2, halfLength = (exit - entry) / 2;
                mean = 1 - (offset * offset + perpendicular * perpendicular + halfLength * halfLength / 3) / _radiusSquared;
            }
            dose = (float)(_rate * (exit - entry) * Math.Max(0, mean));
            return true;
        }

        private static bool Clip(double offset, double inverse, ref double entry, ref double exit)
        {
            if (inverse == 0) return Math.Abs(offset) <= 1;
            double a = (offset - 1) * inverse, b = (offset + 1) * inverse;
            entry = Math.Max(entry, Math.Min(a, b));
            exit = Math.Min(exit, Math.Max(a, b));
            return entry <= exit;
        }
    }
}
