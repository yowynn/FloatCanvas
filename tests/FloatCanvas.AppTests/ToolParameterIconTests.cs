using System.Text.Json;
using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using Controls=System.Windows.Controls;
using Media=System.Windows.Media;
using Imaging=System.Windows.Media.Imaging;
using Wpf=System.Windows;

internal static class ToolParameterIconTests
{
    internal static void Run(Action<string,bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        PointerIconGeometryTests.Run(check);
        PatternedToolIconTests.Run(check);
        foreach (var tool in new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.PointerPresentation })
        {
            var bounds = InkBounds(Rasterize(ToolParameterIcon.Create(new ToolParameters { Tool = tool }, 96), 96));
            check(tool + " default artwork is centered in its icon slot", Math.Abs(bounds.X + bounds.Width / 2d - 48) <= 1 && Math.Abs(bounds.Y + bounds.Height / 2d - 48) <= 1);
        }
        foreach (var mode in new[] { 0, 1, 2, 3 })
        {
            var pointerSettings = new PresentationPointerSettings { CursorEnabled = mode == 0 || mode == 3, TrailEnabled = mode == 1 || mode == 3, ClicksEnabled = mode == 2 || mode == 3 };
            var bounds = InkBounds(Rasterize(ToolParameterIcon.Create(new ToolParameters { Tool = EditorTool.PointerPresentation, PointerPresentation = pointerSettings }, 96), 96));
            check("pointer icon centers enabled effect combination " + mode, Math.Abs(bounds.X + bounds.Width / 2d - 48) <= 1.5 && Math.Abs(bounds.Y + bounds.Height / 2d - 48) <= 1.5);
        }

        if (Environment.GetEnvironmentVariable("FLOATCANVAS_ICON_AUDIT") == "1")
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            try { var bounds = InkBounds(Rasterize(ToolParameterIcon.Create(new ToolParameters { Tool = tool },96),96));
                Console.WriteLine($"AUDIT {tool} {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}"); }
            catch (Exception e) { Console.WriteLine($"AUDIT {tool} ERROR {e.GetType().Name}"); }
        }

        var textSample = new ToolParameters { Tool = EditorTool.Text, Style = new() { PrimaryColor = BuiltInColors.Blue } };
        var textIcons = AppearanceRegistry.VariantsFor(ElementKind.Text).Select(variant =>
        {
            textSample.Style.Variant = variant.Id;
            return Hash(Rasterize(ToolParameterIcon.Create(textSample, 24), 24));
        }).ToArray();
        check("text toolbar and preset previews distinguish plain, outline and label at display size", textIcons.Distinct().Count() == 3);

        var keys = new ToolParameters { Tool = EditorTool.Presentation, Presentation = new() { DisplayStyle = PresentationDisplayStyle.Log, RecentChordLimit = 1 }, Style = new() { Foreground = 0xffff4400, Background = 0xff113388 } };
        string Icon() => Hash(Rasterize(ToolParameterIcon.Create(keys,72),72));
        var one = Icon(); keys.Presentation.RecentChordLimit = 2; var two = Icon(); keys.Presentation.RecentChordLimit = 3; var three = Icon(); keys.Presentation.RecentChordLimit = 4; var four = Icon(); keys.Presentation.RecentChordLimit = 5; var more = Icon(); keys.Presentation.RecentChordLimit = 6;
        check("keyboard icon distinguishes one through four rows and uses a stable ellipsis above four", new[] { one,two,three,four,more }.Distinct().Count() == 5 && Icon() == more);
        keys.Presentation.RecentChordLimit = 2; keys.Presentation.KeyPanelAlignment = PresentationPanelAlignment.TopLeft; var top = Icon(); keys.Presentation.KeyPanelAlignment = PresentationPanelAlignment.BottomLeft; var bottom = Icon(); keys.Presentation.KeyPanelAlignment = PresentationPanelAlignment.BottomRight;
        check("keyboard icon changes with vertical and horizontal alignment", top != bottom && bottom != Icon());
        keys.Presentation.DisplayStyle = PresentationDisplayStyle.Keycaps; var caps = Icon(); keys.Presentation.DisplayStyle = PresentationDisplayStyle.Log; var log = Icon(); keys.Presentation.DisplayStyle = PresentationDisplayStyle.Keyboard; var live = Icon();
        check("keyboard icon distinguishes all three presentation styles", new[] { caps,log,live }.Distinct().Count() == 3);
        keys.Style.Background = 0; var transparent = Icon(); keys.Style.Foreground = 0; var empty = Icon();
        check("keyboard icon respects foreground and background alpha", transparent != live && transparent != empty);

        var pen = new ToolParameters { Tool = EditorTool.Freehand, Style = new() { Foreground = 0xFFCC3344, StrokeWidth = 2 } };
        var penBefore = JsonSerializer.Serialize(pen);
        var thin = Hash(Rasterize(ToolParameterIcon.Create(pen),16));
        check("curved pen preview shares one immutable image between toolbar and preset sizes",ReferenceEquals(((Controls.Image)ToolParameterIcon.Create(pen,16)).Source,((Controls.Image)ToolParameterIcon.Create(pen,24)).Source)&&JsonSerializer.Serialize(pen)==penBefore);
        pen.Style.StrokeWidth=6;
        check("curved pen preview still displays stroke width",thin!=Hash(Rasterize(ToolParameterIcon.Create(pen),16)));
        var thick=Hash(Rasterize(ToolParameterIcon.Create(pen),16));pen.Style.Foreground=0xFF2266DD;
        check("curved pen preview still displays the chosen color",thick!=Hash(Rasterize(ToolParameterIcon.Create(pen),16)));
        var demonstration = new ToolPresetLibrary(Path.Combine(Path.GetTempPath(), "FloatCanvas-icon-unused-" + Guid.NewGuid().ToString("N"))).Official;
        CheckMatchingArrowheads(demonstration, check);
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_ICON_AUDIT") == "1")
        foreach (var preset in demonstration)
        {
            var bounds = InkBounds(Rasterize(ToolParameterIcon.Create(preset.Parameters,96),96));
            Console.WriteLine($"PRESET {preset.Parameters.Tool}|{preset.Id}|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}");
        }
        var keyboardPreviews = demonstration.Where(preset => preset.Parameters.Tool == EditorTool.Presentation)
            .Select(preset => Rasterize(ToolParameterIcon.Create(preset.Parameters), 16)).ToArray();
        check("log keycaps and full-keyboard recipes keep distinct visible artwork at sixteen DIP", keyboardPreviews.Length == 3 && keyboardPreviews.All(bitmap => InkCount(bitmap) > 12) && keyboardPreviews.Select(Hash).Distinct().Count() == 3);
        var mouseRecipes = demonstration.Where(preset => preset.Parameters.Tool == EditorTool.PointerPresentation).ToArray();
        var mousePreviews = mouseRecipes.Select(preset => Rasterize(ToolParameterIcon.Create(preset.Parameters), 16)).ToArray();
        check("halo clicks and trail mouse recipes expose distinct configured artwork at sixteen DIP", mousePreviews.Length == 3 && mousePreviews.All(bitmap => InkCount(bitmap) > 12) && mousePreviews.Select(Hash).Distinct().Count() == 3);
        var customMouse = mouseRecipes[0].Parameters.Clone(); string mouseBefore = JsonSerializer.Serialize(customMouse);
        string oldMouseIcon = Hash(Rasterize(ToolParameterIcon.Create(customMouse), 16)); customMouse.PointerPresentation!.CursorColor = 0xFF00FF00;
        check("mouse artwork reflects effect color and alpha while preserving the saved recipe", Hash(Rasterize(ToolParameterIcon.Create(customMouse), 16)) != oldMouseIcon && JsonSerializer.Serialize(mouseRecipes[0].Parameters) == mouseBefore);
        check("new and older profiles display shortcut legends by default",new UserSettings().ShowToolShortcutHints&&JsonSerializer.Deserialize<UserSettings>("{}")!.ShowToolShortcutHints);
        var settings=new UserSettings {ShowToolShortcutHints=false};
        check("shortcut legend visibility is a persistent preference",!JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!.ShowToolShortcutHints);
        check("all reserved single-letter legends are unique",ToolShortcutCatalog.All.Count==15&&ToolShortcutCatalog.All.Select(item=>item.VirtualKey).Distinct().Count()==15&&ToolShortcutCatalog.All.All(item=>item.Hint.Length==1));
        foreach(var binding in ToolShortcutCatalog.All)
            check("catalog routes "+binding.Hint+" consistently",binding.Tool is { } tool?ToolShortcutCatalog.TryGetTool(binding.VirtualKey,out var actual)&&actual==tool:ToolShortcutCatalog.IsReserved(binding.VirtualKey)&&!ToolShortcutCatalog.TryGetTool(binding.VirtualKey,out _));
        var parameters=new ToolParameters {Tool=EditorTool.Rectangle,Style=new ElementStyle {Stroke=0x801234D0,Fill=0x4024BC68,Opacity=.5,StrokeWidth=4}};
        string before=JsonSerializer.Serialize(parameters);
        var shape=Bitmap(parameters);
        check("style icon retains actual partial alpha and object opacity",MaxAlpha(shape) is > 30 and < 110);
        check("style icon never changes the user's translucent drawing parameters",JsonSerializer.Serialize(parameters)==before);
        check("cached style artwork is frozen and reusable at distinct UI sizes",shape.IsFrozen&&ReferenceEquals(shape,((Controls.Image)ToolParameterIcon.Create(parameters,32)).Source));
        var line=new ToolParameters {Tool=EditorTool.Line};
        string plain=Hash(Bitmap(line));line.Style.EndArrowHead=ArrowHeadStyle.FilledTriangle;
        check("line endpoint presets change the actual rendered arrow geometry",plain!=Hash(Bitmap(line)));
        line.Style.StrokeWidth=12;
        var arrowPreview=Bitmap(line);
        check("arrow preset preview retains a tapered upper-right tip inside its padded image",HasTaperedTip(arrowPreview)&&HasTransparentEdges(arrowPreview));
        line.Style.EndArrowHead=ArrowHeadStyle.None;line.Style.StrokePattern=StrokePattern.Dashed;string dashed=Hash(Bitmap(line));
        line.Style.StrokePattern=StrokePattern.Dotted;
        check("solid dashed and dotted presets have distinct rendered strokes",plain!=dashed&&dashed!=Hash(Bitmap(line)));
        var rectangle=new ToolParameters {Tool=EditorTool.Rectangle};string squareCorners=Hash(Bitmap(rectangle));rectangle.CornerRadius=24;
        check("rounded corner presets change their image geometry",squareCorners!=Hash(Bitmap(rectangle)));
        foreach(var tool in new[]{EditorTool.Rectangle,EditorTool.RoundedRectangle,EditorTool.Ellipse})
        foreach(double ratio in new[]{.5,1,2})
        {
            var sample=new ToolParameters {Tool=tool,ConstrainAspect=true,DefaultWidth=100*ratio,DefaultHeight=100,Style=new() {StrokeWidth=0,Fill=0xFF123456}};
            var bounds=InkBounds(Bitmap(sample));
            check(tool+" locked preset preview preserves configured ratio "+ratio,Math.Abs((double)bounds.Width/bounds.Height-ratio)<.06);
        }
        var highlighter=new ToolParameters {Tool=EditorTool.Highlighter,Style=new ElementStyle {Stroke=0x8053C7D3,Opacity=.5,StrokeWidth=24}};
        check("highlighter preview describes its selected alpha and opacity",MaxAlpha(Bitmap(highlighter)) is > 30 and < 110);
        highlighter.Style = new() { Foreground = ColorLibrary.ForegroundYellow, HighlighterTip = HighlighterTip.Flat, StrokeWidth = 32 };
        foreach (double angle in new[] { -90d, -35d, 0d, 35d, 90d })
        foreach (int size in new[] { 16, 24 })
        {
            highlighter.Style.HighlighterTipAngle = angle;
            highlighter.Style.StrokePattern = StrokePattern.Solid;
            var solid = Rasterize(ToolParameterIcon.Create(highlighter, size), size);
            highlighter.Style.StrokePattern = StrokePattern.Dashed;
            var dashedTip = Rasterize(ToolParameterIcon.Create(highlighter, size), size);
            check($"flat highlighter separates dashed and solid at {angle}° and {size} DIP",
                InkCount(solid) - InkCount(dashedTip) >= 6 && InkCount(dashedTip) >= 12);
        }
        var symbols=new ToolParameters {Tool=EditorTool.Symbol,Symbol=SymbolKind.Heart};string heart=Hash(Bitmap(symbols));symbols.Symbol=SymbolKind.Hexagon;
        check("symbol preset previews retain distinct actual outlines",heart!=Hash(Bitmap(symbols)));
        var toolbarTools=new[]{EditorTool.Freehand,EditorTool.Highlighter,EditorTool.Line,EditorTool.Arrow,EditorTool.DoubleArrow,EditorTool.Rectangle,EditorTool.Ellipse,EditorTool.Text,EditorTool.Number,EditorTool.Symbol};
        foreach(var tool in toolbarTools)
        {
            var image=ToolParameterIcon.Create(new ToolParameters {Tool=tool});
            var pixels=Rasterize(image,16);
            var bounds=InkBounds(pixels);
            Console.WriteLine($"ICON INK {tool}: {bounds.Width}x{bounds.Height} at 16 DIP");
            check(tool+" artwork fills a readable portion of the actual 16-DIP toolbar icon",Math.Max(bounds.Width,bounds.Height)>=13&&Math.Max(bounds.Width,bounds.Height)<=16);
        }
        var readableLine=Rasterize(ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Line}),16);
        var readableDashed=Rasterize(ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Line,Style=new ElementStyle {StrokePattern=StrokePattern.Dashed}}),16);
        check("solid and dashed line samples retain visible ink and distinct pixels at toolbar size",InkCount(readableLine)>=16&&InkCount(readableDashed)>=10&&Hash(readableLine)!=Hash(readableDashed));
        foreach(var tool in new[]{EditorTool.Eraser,EditorTool.Blur,EditorTool.Pixelate})
            check(tool+" presets retain a distinct bounded live control-tool illustration",ToolParameterIcon.Create(new ToolParameters {Tool=tool},32) is Controls.Viewbox {Width:32,Height:32});
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Line, EditorTool.Arrow, EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Symbol })
        {
            var invisible = new ToolParameters { Tool = tool, Style = new() { Foreground = 0xFFFF0000, Background = 0, StrokeWidth = 0 } };
            check(tool + " zero-width transparent sample does not invent visible ink", MaxAlpha(Bitmap(invisible)) == 0);
        }
        var emojiOpacities = new[] { 1d, .5, .25, 0 }.Select(opacity => Rasterize(ToolParameterIcon.Create(new ToolParameters { Tool = EditorTool.Emoji, Style = new() { Opacity = opacity } }, 32), 32)).ToArray();
        check("emoji preview follows full half quarter and zero opacity", MaxAlpha(emojiOpacities[0]) > 200 && MaxAlpha(emojiOpacities[1]) is > 0 and <= 129 && MaxAlpha(emojiOpacities[2]) is > 0 and <= 65 && MaxAlpha(emojiOpacities[3]) == 0);
        var imageOpacities=new[]{1d,.5,.25}.Select(opacity=>Rasterize(ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Image,Style=new() {Opacity=opacity}},32),32)).ToArray();
        check("image preset icons preview distinct full half and quarter opacity",imageOpacities.Select(Hash).Distinct().Count()==3&&
            MaxAlpha(imageOpacities[0])>200&&MaxAlpha(imageOpacities[1]) is >0 and <=129&&MaxAlpha(imageOpacities[2]) is >0 and <=65);
        CheckStrokePatternIcons(check);
        var examples=new List<(string Label,ToolParameters Parameters)>();
        foreach(string label in new[]{"Line","Arrow","Double","Dashed","Dotted"})
        {
            var sample=new ToolParameters {Tool=EditorTool.Line};sample.Style.StrokeWidth=12;
            sample.Style.StartArrowHead=label=="Double"?ArrowHeadStyle.FilledTriangle:ArrowHeadStyle.None;
            sample.Style.EndArrowHead=label is "Arrow" or "Double"?ArrowHeadStyle.FilledTriangle:ArrowHeadStyle.None;
            sample.Style.StrokePattern=label=="Dashed"?StrokePattern.Dashed:label=="Dotted"?StrokePattern.Dotted:StrokePattern.Solid;
            examples.Add((label,sample));
        }
        check("large drawing width cannot erase the five line-preset identities",examples.Select(example=>Hash(Bitmap(example.Parameters))).Distinct().Count()==5);
        string? previewDirectory=Environment.GetEnvironmentVariable("FLOATCANVAS_TOOL_ICON_PREVIEW_DIR");
        if(!string.IsNullOrWhiteSpace(previewDirectory))
        {
            Directory.CreateDirectory(previewDirectory);
            var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Background=Media.Brushes.White};
            foreach(var example in examples)
            {
                var column=new Controls.StackPanel {Width=80,Margin=new Wpf.Thickness(0,5,0,0)};
                column.Children.Add(ToolParameterIcon.Create(example.Parameters,32));column.Children.Add(new Controls.TextBlock {Text=example.Label,Foreground=Media.Brushes.Black,FontSize=11,TextAlignment=Wpf.TextAlignment.Center,Margin=new Wpf.Thickness(0,5,0,0)});row.Children.Add(column);
            }
            row.Measure(new Wpf.Size(400,60));row.Arrange(new Wpf.Rect(0,0,400,60));row.UpdateLayout();
            var pixels=new Imaging.RenderTargetBitmap(800,120,192,192,Media.PixelFormats.Pbgra32);pixels.Render(row);
            var encoder=new Imaging.PngBitmapEncoder();encoder.Frames.Add(Imaging.BitmapFrame.Create(pixels));using var stream=File.Create(Path.Combine(previewDirectory,"line-style-width-12.png"));encoder.Save(stream);
            var toolbar=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Background=Media.Brushes.White};
            foreach(var tool in toolbarTools)
            {
                var column=new Controls.StackPanel {Width=64,Margin=new Wpf.Thickness(0,5,0,0)};
                column.Children.Add(ToolParameterIcon.Create(new ToolParameters {Tool=tool}));
                column.Children.Add(new Controls.TextBlock {Text=tool.ToString(),Foreground=Media.Brushes.Black,FontSize=8,TextAlignment=Wpf.TextAlignment.Center,Margin=new Wpf.Thickness(0,5,0,0)});toolbar.Children.Add(column);
            }
            toolbar.Measure(new Wpf.Size(640,40));toolbar.Arrange(new Wpf.Rect(0,0,640,40));toolbar.UpdateLayout();
            var toolbarPixels=new Imaging.RenderTargetBitmap(640,40,96,96,Media.PixelFormats.Pbgra32);toolbarPixels.Render(toolbar);
            var toolbarEncoder=new Imaging.PngBitmapEncoder();toolbarEncoder.Frames.Add(Imaging.BitmapFrame.Create(toolbarPixels));using var toolbarStream=File.Create(Path.Combine(previewDirectory,"toolbar-icons-at-16-dip.png"));toolbarEncoder.Save(toolbarStream);
            WriteEffectCapturePreview(previewDirectory);
        }
    }
    private static void WriteEffectCapturePreview(string directory)
    {
        var examples=new List<(string Label,ToolParameters Parameters)>
        {
            ("模糊",new() {Tool=EditorTool.Blur}),
            ("马赛克",new() {Tool=EditorTool.Pixelate}),
            ("矩形截图",new() {Tool=EditorTool.CaptureRegion}),
            ("圆形截图",new() {Tool=EditorTool.CaptureRegion,CaptureShape=CaptureShape.Ellipse}),
            ("套索截图",new() {Tool=EditorTool.CaptureRegion,CaptureShape=CaptureShape.FreehandLasso}),
            ("多边形截图",new() {Tool=EditorTool.CaptureRegion,CaptureShape=CaptureShape.PolygonLasso}),
            ("矩形绘图",new() {Tool=EditorTool.Rectangle,Style=new() {Stroke=0xFF6688CC}}),
            ("圆形绘图",new() {Tool=EditorTool.Ellipse,Style=new() {Stroke=0xFF6688CC}})
        };
        var sheet=new Controls.StackPanel();
        foreach(string theme in new[]{"light","dark"})
        {
            AppTheme.Apply(theme);
            var section=new Controls.StackPanel {Background=(Media.Brush)Wpf.Application.Current.Resources["CanvasSurface"]};
            var title=new Controls.TextBlock {Text=(theme=="light"?"浅色":"深色")+" · 实际 16 DIP / 32 DIP / 48 DIP",FontSize=12,Margin=new(12,10,0,6),Foreground=(Media.Brush)Wpf.Application.Current.Resources["CanvasText"]};
            section.Children.Add(title);
            var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal};
            foreach(var example in examples)
            {
                var column=new Controls.StackPanel {Width=96};
                column.Children.Add(new Controls.TextBlock {Text=example.Label,FontSize=11,TextAlignment=Wpf.TextAlignment.Center,Foreground=(Media.Brush)Wpf.Application.Current.Resources["CanvasText"],Margin=new(0,0,0,8)});
                foreach(int size in new[]{16,32,48})
                {
                    // Render each theme now so changing the shared palette cannot recolor earlier rows.
                    var pixels=Rasterize(ToolParameterIcon.Create(example.Parameters,size),size);
                    byte[] bytes=new byte[size*size*4];pixels.CopyPixels(bytes,size*4,0);
                    var snapshot=Imaging.BitmapSource.Create(size,size,96,96,Media.PixelFormats.Pbgra32,null,bytes,size*4);snapshot.Freeze();
                    if(size==48)
                    {
                        var sampleEncoder=new Imaging.PngBitmapEncoder();sampleEncoder.Frames.Add(Imaging.BitmapFrame.Create(snapshot));
                        using var sampleStream=File.Create(Path.Combine(directory,theme+"-"+example.Label+".png"));sampleEncoder.Save(sampleStream);
                    }
                    column.Children.Add(new Controls.Image {Source=snapshot,Width=size,Height=size,Margin=new(0,0,0,8)});
                }
                row.Children.Add(column);
            }
            section.Children.Add(row);sheet.Children.Add(section);
        }
        var image=RasterizePreview(sheet,768,360);
        var encoder=new Imaging.PngBitmapEncoder();encoder.Frames.Add(Imaging.BitmapFrame.Create(image));
        using var stream=File.Create(Path.Combine(directory,"effect-capture-icons.png"));encoder.Save(stream);
        AppTheme.Apply("light");
    }
    private static Imaging.BitmapSource RasterizePreview(Wpf.UIElement element,int width,int height)
    {
        element.Measure(new Wpf.Size(width,height));element.Arrange(new Wpf.Rect(0,0,width,height));element.UpdateLayout();
        var pixels=new Imaging.RenderTargetBitmap(width,height,96,96,Media.PixelFormats.Pbgra32);pixels.Render(element);return pixels;
    }
    private static void CheckStrokePatternIcons(Action<string, bool> check)
    {
        const int size = 16;
        string? directory = Environment.GetEnvironmentVariable("FLOATCANVAS_TOOL_ICON_PREVIEW_DIR");
        var patterns = Enum.GetValues<StrokePattern>();
        foreach (var (pattern, expected) in new (StrokePattern, int[])[]
        {
            (StrokePattern.Solid, [11]), (StrokePattern.Dashed, [3, 3, 3]),
            (StrokePattern.Dotted, [1, 1, 1, 1, 1, 1]), (StrokePattern.DashDot, [3, 1, 3, 1])
        })
        {
            var icon = (Controls.Viewbox)SegmentedChoicePreviews.Icon(pattern);
            var path = (System.Windows.Shapes.Path)((Controls.Canvas)icon.Child).Children[0];
            var geometry = (Media.GeometryGroup)path.Data;
            var marks = geometry.Children.Select(mark => mark.Bounds).ToArray();
            var choice = Rasterize(icon, 18);
            byte[] pixels = new byte[18 * 18 * 4];
            choice.CopyPixels(pixels, 18 * 4, 0);
            var bands = Enumerable.Range(0, 18)
                .Select(x => Enumerable.Range(0, 18).Any(y => pixels[(y * 18 + x) * 4 + 3] >= 80)).ToArray();
            check($"{pattern} picker spans eleven units with the expected mark lengths and one-unit gaps",
                geometry.Bounds.Width == 16.5 &&
                marks.Select(mark => mark.Width / 1.5).SequenceEqual(expected.Select(mark => (double)mark)) &&
                marks.Zip(marks.Skip(1), (a, b) => b.Left - a.Right).All(gap => gap == 1.5) &&
                bands.Where((ink, x) => ink && (x == 0 || !bands[x - 1])).Count() == expected.Length);
            Save("selector-" + pattern, choice);
        }

        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow,
            EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle, EditorTool.Symbol })
        foreach (var tip in tool == EditorTool.Highlighter ? Enum.GetValues<HighlighterTip>() : [HighlighterTip.Round])
        {
            string label = tool == EditorTool.Highlighter ? $"{tool}-{tip}" : tool.ToString();
            var sample = new ToolParameters { Tool = tool, Style = new() { Foreground = 0xFFFF0000,
                StrokeWidth = tool == EditorTool.Highlighter ? 32 : 6, HighlighterTip = tip } };
            var icons = patterns.Select(pattern =>
            {
                sample.Style.StrokePattern = pattern;
                var icon = Rasterize(ToolParameterIcon.Create(sample, size), size);
                Save(label + "-" + pattern, icon);
                return icon;
            }).ToArray();
            check($"{label} toolbar renders four distinct line patterns at {size} DIP",
                icons.All(icon => InkCount(icon) >= 8) && icons.Select(Hash).Distinct().Count() == patterns.Length);
        }

        var gridIcons = patterns.Select(pattern =>
        {
            var icon = Rasterize(new CanvasParameterIcon(new() { GridVisible = true, GridWidth = 2,
                GridColor = 0xFFFF0000, GridPattern = pattern }, size), size);
            Save("Canvas-" + pattern, icon);
            return icon;
        });
        check("the canvas toolbar icon distinguishes all four grid patterns", gridIcons.Select(Hash).Distinct().Count() == patterns.Length);

        void Save(string name, Imaging.BitmapSource icon)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var encoder = new Imaging.PngBitmapEncoder();
            encoder.Frames.Add(Imaging.BitmapFrame.Create(icon));
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(stream);
        }
    }

    private static void CheckMatchingArrowheads(IReadOnlyList<ToolPreset> presets, Action<string, bool> check)
    {
        var single = presets.Single(p => p.Id == "official.arrow").Parameters;
        var both = presets.Single(p => p.Id == "official.double-arrow").Parameters;
        check("official arrows use the same stroke width and automatic head size",
            single.Style.StrokeWidth == both.Style.StrokeWidth && single.Style.ArrowHeadSize == 0 && both.Style.ArrowHeadSize == 0);
        // The gallery renders at 24 DIP. Isolate its actual artwork from the checkerboard.
        static Imaging.BitmapSource GalleryInk(ToolParameters p)
        {
            var tile = (Controls.Border)ToolPresetGallery.CreatePreview(p);
            return Rasterize(tile.Child, (int)tile.Width);
        }
        static double EndInk(Imaging.BitmapSource icon, bool start = false)
        {
            int half = icon.PixelWidth / 2;
            var quadrant = new Wpf.Int32Rect(start ? 0 : half, start ? half : 0, half, half);
            var pixels = new byte[half * half * 4];
            icon.CopyPixels(quadrant, pixels, half * 4, 0);
            int alpha = 0;
            for (int i = 3; i < pixels.Length; i += 4) alpha += pixels[i];
            return alpha / 255d;
        }
        void Compare(ToolParameters left, ToolParameters right, string name, bool start = false)
        {
            double singleInk = EndInk(GalleryInk(left), start), doubleInk = EndInk(GalleryInk(right), start);
            check($"{name}: same endpoint coverage at 24 DIP ({singleInk:F2} vs {doubleInk:F2} px)",
                singleInk > 3 && Math.Abs(singleInk - doubleInk) < 1);
        }
        Compare(single, both, "official single and double arrows");
        double smallSingle = EndInk(Rasterize(ToolParameterIcon.Create(single, 16), 16));
        double smallDouble = EndInk(Rasterize(ToolParameterIcon.Create(both, 16), 16));
        check($"official arrows keep the common head at toolbar size ({smallSingle:F2} vs {smallDouble:F2} px)",
            smallSingle > 3 && Math.Abs(smallSingle - smallDouble) < 1);
        var curved = single.Clone(); curved.Style.LineArcBulge = ElementGeometry.DefaultLineArcBulge;
        var curvedPair = curved.Clone(); curvedPair.Style.StartArrowHead = ArrowHeadStyle.Diamond;
        Compare(curved, curvedPair, "curved line with a different opposite head");
        check("curved preview includes the entire arrow artwork", HasTransparentEdges(Bitmap(curved)) && HasTransparentEdges(Bitmap(curvedPair)));
        var scaled = both.Clone(); scaled.Style.ArrowHeadSize = 8;
        double narrow = EndInk(GalleryInk(scaled));
        scaled.Style.ArrowHeadSize = 16;
        check("explicit head size is visible on a double-arrow preset", EndInk(GalleryInk(scaled)) > narrow + 3);
        foreach (var head in new[] { ArrowHeadStyle.FilledTriangle, ArrowHeadStyle.Open, ArrowHeadStyle.Diamond })
        {
            var first = single.Clone(); first.Style.ArrowHeadSize = 13;
            first.Style.StartArrowHead = head; first.Style.EndArrowHead = ArrowHeadStyle.None;
            var pair = first.Clone(); pair.Style.EndArrowHead = head;
            Compare(first, pair, head + " start with an explicit size", start: true);
        }
    }

    private static Imaging.BitmapSource Bitmap(ToolParameters parameters)=>(Imaging.BitmapSource)((Controls.Image)ToolParameterIcon.Create(parameters)).Source;
    private static Imaging.BitmapSource Rasterize(Wpf.UIElement image,int size)
    {
        if(image is Wpf.FrameworkElement element)AppTheme.UsePopupPalette(element);
        image.Measure(new Wpf.Size(size,size));image.Arrange(new Wpf.Rect(0,0,size,size));image.UpdateLayout();
        var pixels=new Imaging.RenderTargetBitmap(size,size,96,96,Media.PixelFormats.Pbgra32);pixels.Render(image);return pixels;
    }
    private static Wpf.Int32Rect InkBounds(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        int left=bitmap.PixelWidth,top=bitmap.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[(y*bitmap.PixelWidth+x)*4+3]>=32)
        {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        return right<left?Wpf.Int32Rect.Empty:new(left,top,right-left+1,bottom-top+1);
    }
    private static int InkCount(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);int count=0;for(int i=3;i<bytes.Length;i+=4)if(bytes[i]>=96)count++;return count;}
    private static bool HasTaperedTip(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        var points=new List<(int X,int Y)>();
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[(y*bitmap.PixelWidth+x)*4+3]>=96)points.Add((x,y));
        int tip=points.Max(point=>point.X-point.Y);
        int tipInk=points.Count(point=>point.X-point.Y>=tip-2),shoulderInk=points.Count(point=>point.X-point.Y>=tip-10&&point.X-point.Y<tip-7);
        return tipInk>0&&shoulderInk>tipInk;
    }
    private static bool HasTransparentEdges(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[x*4+3]!=0||bytes[((bitmap.PixelHeight-1)*bitmap.PixelWidth+x)*4+3]!=0)return false;
        for(int y=0;y<bitmap.PixelHeight;y++)if(bytes[(y*bitmap.PixelWidth)*4+3]!=0||bytes[(y*bitmap.PixelWidth+bitmap.PixelWidth-1)*4+3]!=0)return false;
        return true;
    }
    private static string Hash(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));}
    private static int MaxAlpha(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes.Where((_,i)=>i%4==3).Max();}
    private static int Count(Imaging.BitmapSource bitmap,Media.Color color)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);int count=0;for(int i=0;i<bytes.Length;i+=4)if(bytes[i+3]>250&&Math.Abs(bytes[i+2]-color.R)<4&&Math.Abs(bytes[i+1]-color.G)<4&&Math.Abs(bytes[i]-color.B)<4)count++;return count;}
}
