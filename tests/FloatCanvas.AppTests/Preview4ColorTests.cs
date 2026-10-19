using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class Preview4ColorTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-Preview4Colors", Guid.NewGuid().ToString("N"));
        var library = new ColorLibrary(Path.Combine(root, "colors.json"));
        var editor = new EditorController(new EditorSession());
        var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(root, "presets.json")), library);
        var field = Field<ColorField>(panel, "foreground");
        var picker = Field<ColorPickerPanel>(field, "picker");
        // Exercise the real draft controls in an opened session without creating a Popup HWND.
        typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(field, true);
        picker.Value = field.Value;
        uint previous = field.Value;
        picker.Value = 0xFFAABBCC;
        check("programmatic picker synchronization stays silent during an open session", field.Value == previous && editor.Style.Stroke == previous);
        picker.Value = previous;
        Field<TextBox>(picker, "hex").Text = "#336699";
        Field<Slider>(picker, "alpha").Value = 25;
        check("picker draft immediately reaches the primary field and active drawing style with exact alpha",
            field.Value == 0x40336699 && editor.Style.Stroke == 0x40336699);
        check("live drafts do not pollute shared color history", library.History.Count == 0);
        Field<TextBox>(picker, "hex").Text = "invalid";
        check("invalid intermediate text retains the last valid live color", field.Value == 0x40336699);
        Field<TextBox>(picker, "hex").Text = "#112233";
        field.ClosePicker(); field.ClosePicker();
        check("closing stores only the final valid color once", library.History.SequenceEqual(new uint[] { 0x40112233 }));
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Eraser })
        {
            var sample = ToolParameterIcon.Create(new ToolParameters { Tool = tool,
                Style = new() { Stroke = 0x80336699, Fill = 0x4000FF00, StrokeWidth = 4 } }, 96);
            sample.Measure(new(96,96)); sample.Arrange(new Rect(0,0,96,96)); sample.UpdateLayout();
            var bitmap = new RenderTargetBitmap(96,96,96,96,PixelFormats.Pbgra32); bitmap.Render(sample);
            byte[] pixels = new byte[96*96*4]; bitmap.CopyPixels(pixels,96*4,0);
            check(tool + " icon retains partial alpha and transparent surroundings", pixels.Where((_,i)=>i%4==3).Max() is > 20 and < 240 && pixels[3] == 0);
        }
        var swatch = ColorPickerPanel.CreateSwatch(0x80336699);
        check("color swatches blend with the actual underlying surface", swatch.Background is null);
        var transparent = (Image)ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Rectangle,Style=new() {Stroke=0x00123456,Fill=0x00654321}});
        var transparentPixels = (BitmapSource)transparent.Source;
        byte[] empty = new byte[transparentPixels.PixelWidth*transparentPixels.PixelHeight*4];transparentPixels.CopyPixels(empty,transparentPixels.PixelWidth*4,0);
        check("zero alpha with nonzero RGB remains fully transparent in previews", empty.Where((_,i)=>i%4==3).All(a=>a==0));
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
