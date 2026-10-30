using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class ToolbarScreenBoundsTests
{
    internal static void Run(Action<string, bool> check)
    {
        Fits("a negative-origin screen constrains the complete physical rectangle", new(-2000, -1200, 300, 94), [new(-1920, -1080, 1920, 1080)], new(-1920, -1080, 300, 94));
        Fits("a specific-screen scope cannot place the toolbar on an unselected screen", new(100, 100, 600, 94), [new(1920, 0, 1920, 1080)], new(1920, 100, 600, 94));
        PixelBounds[] adjacent = [new(0, 0, 100, 100), new(100, 0, 100, 100)];
        Fits("a fully covered toolbar can remain across an adjacent screen seam", new(70, 20, 80, 20), adjacent, new(70, 20, 80, 20));
        Fits("a toolbar wider than either screen can fit their continuous union", new(0, 20, 180, 20), adjacent, new(0, 20, 180, 20));
        Fits("negative physical origins do not prevent a seam-spanning position", new(-30, -70, 80, 20), [new(-100, -100, 100, 100), new(0, -100, 100, 100)], new(-30, -70, 80, 20));
        Fits("a gap rejects a frame even when all four corners are on screens", new(80, 20, 50, 20), [new(0, 0, 100, 100), new(120, 0, 100, 100)], new(50, 20, 50, 20));
        PixelBounds[] notch = [new(0, 0, 100, 100), new(100, 50, 100, 100)];
        Fits("a staggered-screen notch moves to the nearest fully covered position", new(80, 20, 40, 20), notch, new(60, 20, 40, 20));
        Fits("the covered part of a staggered seam remains unchanged", new(80, 60, 40, 20), notch, new(80, 60, 40, 20));
        Fits("overlapping screens form one continuous covered region", new(25, 10, 120, 20), [new(0, 0, 100, 100), new(50, 0, 100, 100)], new(25, 10, 120, 20));
        NoFit("overlap area cannot be double-counted to hide a screen gap", new(0, 10, 180, 20), [new(0, 0, 100, 100), new(50, 0, 100, 100), new(170, 0, 50, 100)]);
        Fits("a collapsed 94-pixel icon retains its valid near-edge position", new(900, 600, 94, 94), [new(0, 0, 1000, 800)], new(900, 600, 94, 94));
        Fits("expanding at the same origin constrains the entire toolbar", new(900, 600, 680, 95), [new(0, 0, 1000, 800)], new(320, 600, 680, 95));
        Fits("a high-DPI physical size does not scale the desktop origin", new(-300, 20, 1020, 142), [new(-1920, 0, 1920, 1080)], new(-1020, 20, 1020, 142));
        Fits("a far-away position chooses the nearest allowed screen", new(10000, -10000, 40, 20), [new(0, 0, 100, 100), new(200, 0, 100, 100)], new(260, 0, 40, 20));
        PixelBounds[] separated = [new(0, 0, 100, 100), new(200, 0, 100, 100)];
        Fits("equal-distance placements use a deterministic origin", new(130, 10, 40, 20), separated, new(60, 10, 40, 20));
        Fits("screen enumeration order does not change equal-distance placement", new(130, 10, 40, 20), separated.Reverse().ToArray(), new(60, 10, 40, 20));
        Fits("removing a monitor moves the old position into the remaining scope", new(230, 30, 40, 20), [separated[0]], new(60, 30, 40, 20));
        NoFit("a window larger than the complete available region reports failure", new(0, 0, 201, 20), adjacent);
        NoFit("a window taller than every covered region reports failure", new(0, 0, 20, 101), adjacent);
        NoFit("no monitor area leaves the proposed rectangle untouched", new(50, 60, 40, 20), []);
        NoFit("a degenerate screen area is invalid", new(50, 60, 40, 20), [new(0, 0, 0, 100)]);
        NoFit("a zero-sized proposed window is invalid", new(50, 60, 0, 20), adjacent);
        NoFit("a negative-sized proposed window is invalid", new(50, 60, -1, 20), adjacent);
        NoFit("a proposed physical rectangle cannot wrap integer coordinates", new(int.MaxValue - 10, 0, 20, 20), adjacent);
        NoFit("screen extents cannot wrap integer coordinates", new(0, 0, 20, 20), [new(int.MaxValue - 10, 0, 20, 20)]);

        string? failure = null; int cases = 0;
        for (int screenX = -1; screenX <= 1; screenX++)
        for (int screenY = -1; screenY <= 1; screenY++)
        for (int width = 1; width <= 3; width++)
        for (int height = 1; height <= 3; height++)
        for (int x = -2; x <= 3; x++)
        for (int y = -2; y <= 3; y++)
        {
            Compare(new(x, y, width, height), [new(0, 0, 2, 2), new(screenX, screenY, 2, 2)]);
            cases++;
        }
        check("toolbar placement matches an independent pixel oracle across " + cases + " exhaustive small cases" + failure, failure is null);
        failure = null; cases = 0;
        var random = new Random(20261030);
        for (int count = 1; count <= 4; count++)
        for (int index = 0; index < 64; index++)
        {
            var screens = Enumerable.Range(0, count).Select(_ => new PixelBounds(random.Next(-6, 7), random.Next(-6, 7), random.Next(1, 7), random.Next(1, 7))).ToArray();
            Compare(new(random.Next(-10, 11), random.Next(-10, 11), random.Next(1, 8), random.Next(1, 8)), screens);
            cases++;
        }
        check("toolbar placement matches an independent pixel oracle across " + cases + " seeded layouts with one to four screens" + failure, failure is null);

        void Fits(string name, PixelBounds requested, PixelBounds[] screens, PixelBounds expected) =>
            check(name, ToolbarScreenBounds.TryConstrain(requested, screens, out var actual) && actual == expected);
        void NoFit(string name, PixelBounds requested, PixelBounds[] screens) =>
            check(name, !ToolbarScreenBounds.TryConstrain(requested, screens, out var actual) && actual == requested);
        void Compare(PixelBounds requested, PixelBounds[] screens)
        {
            if (failure is not null) return;
            var expected = Oracle(requested, screens);
            bool found = ToolbarScreenBounds.TryConstrain(requested, screens, out var actual);
            if (found == expected.HasValue && actual == (expected ?? requested)) return;
            failure = " (requested=" + requested + "; screens=" + string.Join(" | ", screens) + "; expected=" + (expected?.ToString() ?? "no fit") + "; actual=" + actual + "; found=" + found + ")";
        }
    }

    // Integer screen edges partition the plane into unit pixels. Checking every
    // pixel independently avoids sharing the production rectangle-subtraction algorithm.
    private static PixelBounds? Oracle(PixelBounds requested, PixelBounds[] screens)
    {
        PixelBounds? best = null; double bestDistance = double.PositiveInfinity;
        for (int x = screens.Min(screen => screen.Left); x <= screens.Max(screen => screen.Right) - requested.Width; x++)
        for (int y = screens.Min(screen => screen.Top); y <= screens.Max(screen => screen.Bottom) - requested.Height; y++)
        {
            bool covered = true;
            for (int px = x; px < x + requested.Width && covered; px++)
            for (int py = y; py < y + requested.Height; py++)
                if (!screens.Any(screen => px >= screen.Left && px < screen.Right && py >= screen.Top && py < screen.Bottom)) { covered = false; break; }
            if (!covered) continue;
            double dx = (double)x - requested.Left, dy = (double)y - requested.Top, distance = dx * dx + dy * dy;
            // Ascending x/y iteration is the independent tie-breaking rule.
            if (distance >= bestDistance) continue;
            bestDistance = distance; best = requested with { Left = x, Top = y };
        }
        return best;
    }
}
