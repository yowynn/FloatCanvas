using FloatCanvas.App;

internal static class DepthOcclusionUiRuntimeTests
{
    public static void Run(Action<string, bool> check) => CanvasOptionsRefinementTests.Run(check,
        Environment.GetEnvironmentVariable("FLOATCANVAS_DEPTH_UI_OUTPUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FloatCanvas-depth-ui"));
}
