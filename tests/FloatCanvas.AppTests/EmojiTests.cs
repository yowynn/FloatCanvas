using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

internal static class EmojiTests
{
    private static readonly string[] Samples = ["😀","😃","😄","😁","😆","😅","😂","😊","😍","🥰","😎","🤔","😮","😢","😭",
        "😡","🥳","😴","👍","👎","👏","🙌","🙏","💪","❤️","💛","💚","💙","⭐","🔥","👩🏽‍🎨","🫫","🇨🇳","🫱🏻‍🫲🏿"];
    public static void Run(Action<string, bool> check, string? profileDirectory = null)
    {
        EmojiFontSources.Configure(profileDirectory ?? ApplicationPaths.DefaultProfileDirectory);
        _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        using(var catalog=typeof(EmojiCatalog).Assembly.GetManifestResourceStream("FloatCanvas.EmojiCatalog"))
        {
            string hash=catalog is null?"":Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(catalog));
            check("emoji bounds profile is tied to the generated Unicode catalog",
                EmojiBoundsProfile.CatalogVersion=="18.0"&&hash==EmojiBoundsProfile.CatalogSha256);
        }
        check("emoji bounds profile contains one explicit frame source per selectable font",
            EmojiBoundsProfile.Frames.Count==4&&
            EmojiFontIds.IsKnown(EmojiBoundsProfile.Frames[0].FontId)&&
            EmojiBoundsProfile.Frames.Select(frame=>frame.FontId).Distinct().Count()==4);
        foreach(var definition in EmojiBoundsProfile.Frames.Where(frame => frame.FontId != EmojiFontIds.SystemSegoe))
        {
            string path=EmojiFontSources.PathFor(definition.FontId);
            if(!File.Exists(path))continue;
            check(definition.FontId+" font fingerprint matches the bounds profile",
                EmojiFontSources.Sha256(definition.FontId)==definition.ExpectedSha256);
        }
        var mismatches = EmojiBoundsProfile.FingerprintMismatches();
        check("the app-managed emoji assets do not report a stale bounds profile",
            mismatches.All(mismatch => mismatch.FontId == EmojiFontIds.SystemSegoe));
        string systemHash = EmojiFontSources.Sha256(EmojiFontIds.SystemSegoe);
        bool systemChanged = systemHash.Length != 0 && systemHash != EmojiBoundsProfile.For(EmojiFontIds.SystemSegoe).ExpectedSha256;
        check("Windows font revisions may differ from the measured reference and are reported diagnostically",
            mismatches.Any(mismatch => mismatch.FontId == EmojiFontIds.SystemSegoe && mismatch.ActualSha256 == systemHash) == systemChanged);
        string notoPath=EmojiFontSources.PathFor(EmojiFontIds.NotoColor);
        if(File.Exists(notoPath))
        {
            var fallback=FontFormat(notoPath);
            check("app-managed Noto fallback is a COLRv1 vector font rather than bitmap artwork",fallback.ColrVersion==1 &&
                !fallback.HasCBDT && !fallback.HasCBLC);
        }
        string blobmojiPath=EmojiFontSources.PathFor(EmojiFontIds.BlobmojiColor);
        if(File.Exists(EmojiFontSources.PathFor(EmojiFontIds.OpenMojiColor)))
            check("app-managed OpenMoji exposes its internal OpenMoji Color family",EmojiFontSources.IsAvailable(EmojiFontIds.OpenMojiColor));
        if(File.Exists(blobmojiPath))
        {
            var blobmoji=FontFormat(blobmojiPath);
            check("Blobmoji is a COLRv1 vector font",blobmoji.ColrVersion==1 && !blobmoji.HasCBDT && !blobmoji.HasCBLC);
            var systemPreview=EmojiArtwork.Image("😀",EmojiFontIds.SystemSegoe);
            var blobmojiPreview=EmojiArtwork.Image("😀",EmojiFontIds.BlobmojiColor);
            check("emoji preview caches are isolated by selected font style",!ReferenceEquals(systemPreview,blobmojiPreview) &&
                ReferenceEquals(blobmojiPreview,EmojiArtwork.Image("😀",EmojiFontIds.BlobmojiColor)));
            using var styleRenderer=new SceneRenderer();
            var systemScene=new SceneDocument { Viewport=new(0,0,160,160),Elements=[Element("😀",96,EmojiFontIds.SystemSegoe)] };
            var blobmojiScene=new SceneDocument { Viewport=new(0,0,160,160),Elements=[Element("😀",96,EmojiFontIds.BlobmojiColor)] };
            check("emoji layer rendering follows its persistent font style",!styleRenderer.ExportPng(systemScene,new(),true)
                .AsSpan().SequenceEqual(styleRenderer.ExportPng(blobmojiScene,new(),true)));
        }
        check("nine standard categories cover all 3963 emoji including tone variants", Enum.GetValues<EmojiGroup>().Sum(g => EmojiCatalog.Search("",g,-1).Count)==3963);
        check("picker accepts complete emoji and normalizes recognized presentation spellings", EmojiCatalog.TryFind("❤",out var heart) && heart.Glyph=="❤️" &&
            new[]{"👩🏽‍🎨","🫱🏻‍🫲🏿","1️⃣","🇨🇳"}.All(g=>EmojiCatalog.TryFind(g,out _)));
        check("ordinary characters, stray components, multiple emoji and whitespace cannot be stored as a single emoji",
            new[]{""," ","a","汉","1","🏽","🔥🔥","🔥 ","😀\n"}.All(g=>!EmojiCatalog.TryFind(g,out _) && !new ToolParameters { Tool=EditorTool.Emoji,Emoji=g }.IsValid()));
        check("search covers both languages and exact glyphs across categories", new[]{"火箭","rocket","🚀"}.All(q=>EmojiCatalog.Search(q,EmojiGroup.Food,0)[0].Glyph=="🚀"));
        string historyPath=Path.Combine(Path.GetTempPath(),"fc-emoji-history-"+Guid.NewGuid().ToString("N")+".json");
        var historyStore=new EmojiHistoryStore(historyPath);
        historyStore.Remember("😀");historyStore.Remember("🚀");historyStore.Remember("😀");
        historyStore.Flush();
        var reopenedHistory=new EmojiHistoryStore(historyPath);
        check("recent emoji history persists across store instances and moves repeats to the front",
            reopenedHistory.Entries.Select(entry=>entry.Glyph).SequenceEqual(new[]{"😀","🚀"})&&File.Exists(historyPath));
        string normalizedPath=Path.Combine(Path.GetTempPath(),"fc-emoji-history-normalized-"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(normalizedPath,"{\"Version\":1,\"Glyphs\":[\"😀\",\"not-an-emoji\",\"😀\",\"🚀\"]}");
        var normalizedHistory=new EmojiHistoryStore(normalizedPath);
        normalizedHistory.Flush();
        var normalizedWire=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(normalizedPath))!.AsObject();
        check("invalid and duplicate historical entries are discarded and the cache is rewritten",
            normalizedHistory.Entries.Select(entry=>entry.Glyph).SequenceEqual(new[]{"😀","🚀"})&&
            normalizedWire["Glyphs"]!.AsArray().Select(node=>node!.GetValue<string>()).SequenceEqual(new[]{"😀","🚀"}));
        string blocker=Path.Combine(Path.GetTempPath(),"fc-emoji-history-blocker-"+Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker,"blocks directory creation");
        var unavailableHistory=new EmojiHistoryStore(Path.Combine(blocker,"emoji-history.json"));
        unavailableHistory.Remember("😀");
        check("an unwritable history cache remains usable in memory instead of blocking selection",
            unavailableHistory.Entries.Select(entry=>entry.Glyph).SequenceEqual(new[]{"😀"}));
        var globalFrame=EmojiGlyphs.GlobalFrame();
        var logicalFrame=EmojiGlyphs.Measure("😀",1);
        check("all emoji styles share the surveyed global square content frame",
            Math.Abs(globalFrame.Width-globalFrame.Height)<1e-6 &&
            Math.Abs(logicalFrame.Width-EmojiGlyphs.GlobalFrameSideEm)<1e-6 &&
            Math.Abs(logicalFrame.Height-EmojiGlyphs.GlobalFrameSideEm)<1e-6);
        check("every emoji style aligns its native design center inside the global square",
            EmojiBoundsProfile.Frames.All(definition =>
            {
                var frame=EmojiGlyphs.GlobalFrame(definition.FontId);
                double emScale=frame.Width/EmojiGlyphs.GlobalFrameSideEm;
                return Math.Abs(frame.MidX/emScale-definition.CenterXEm)<1e-6&&
                    Math.Abs(frame.MidY/emScale-definition.CenterYEm)<1e-6;
            }));
        var oversizedInk=new SKRect(
            globalFrame.Left-globalFrame.Width*.35f,
            globalFrame.Top-globalFrame.Height*.2f,
            globalFrame.Right+globalFrame.Width*.45f,
            globalFrame.Bottom+globalFrame.Height*.7f);
        float fitted=EmojiGlyphs.FitScale(oversizedInk,globalFrame);
        float cx=globalFrame.MidX,cy=globalFrame.MidY;
        var fittedInk=new SKRect(
            cx+(oversizedInk.Left-cx)*fitted,
            cy+(oversizedInk.Top-cy)*fitted,
            cx+(oversizedInk.Right-cx)*fitted,
            cy+(oversizedInk.Bottom-cy)*fitted);
        check("an oversized future glyph is reduced around the frame center until every edge fits",
            fitted>0&&fitted<1&&fittedInk.Left>=globalFrame.Left-1e-4&&fittedInk.Top>=globalFrame.Top-1e-4&&
            fittedInk.Right<=globalFrame.Right+1e-4&&fittedInk.Bottom<=globalFrame.Bottom+1e-4);
        var contained=SKRect.Intersect(globalFrame,oversizedInk);
        check("glyphs already inside the global frame are not enlarged",
            Math.Abs(EmojiGlyphs.FitScale(contained,globalFrame)-1)<1e-6);
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        bool HasAvailableGlyph(string emoji) => EmojiFontSources.ResolveChain(EmojiFontIds.Default)
            .Any(fontId => EmojiGlyphs.CanRender(emoji, fontId));
        foreach (string emoji in Samples)
        {
            bool supported = HasAvailableGlyph(emoji);
            foreach (double size in new[] { 12d, 48d, 96d })
            {
                var metrics = EmojiGlyphs.Measure(emoji, size);
                check(emoji + " uses a square safe area with the available glyph or a missing-glyph placeholder at " + size,
                    metrics.LineCount == 1 && metrics.GlyphCount == (supported ? 1 : 0) && metrics.MissingGlyphCount == (supported ? 0 : 1) &&
                    metrics.Width > 0 && Math.Abs(metrics.Width-metrics.Height)<1e-9);
                var element = Element(emoji, size);
                element.Transform = Affine2D.Translation(10, 10);
                var scene = new SceneDocument { Viewport = new(0, 0, Math.Ceiling(size) + 20, Math.Ceiling(size) + 20), Elements = [element] };
                using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
                var ink = Ink(bitmap);
                Console.WriteLine($"EMOJI_INK {emoji} size={size} metrics={metrics.Width:F4}x{metrics.Height:F4} ink={ink}");
                check(emoji + " keeps its native font placement inside the square safe area at " + size,
                    ink.Left >= 10 && ink.Top >= 10 && ink.Right <= 10 + size + 1 && ink.Bottom <= 10 + size + 1);
                check(emoji + " remains centered inside the global frame at " + size,
                    Math.Abs((ink.Left+ink.Right)/2d-(10+size/2d))<=Math.Max(2,size*.25)&&
                    Math.Abs((ink.Top+ink.Bottom)/2d-(10+size/2d))<=Math.Max(2,size*.25));
                check(emoji + " occupies a practical portion of the robust global frame at " + size,
                    ink.Right-ink.Left>Math.Max(2,size*.25)&&ink.Bottom-ink.Top>Math.Max(2,size*.25));
                if (supported)
                    check(emoji + " is rendered in color rather than monochrome at " + size, bitmap.Pixels.Count(Colorful) >=
                        Math.Max(1, size * size * .004));
            }
            var preview = EmojiArtwork.Image(emoji);
            check(emoji + " preview is frozen and reused", preview.IsFrozen && ReferenceEquals(preview, EmojiArtwork.Image(emoji)));
            var converted = new Imaging.FormatConvertedBitmap(preview, Media.PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[preview.PixelWidth * preview.PixelHeight * 4];
            converted.CopyPixels(pixels, preview.PixelWidth * 4, 0);
            int coloredPreviewPixels = 0;
            int visiblePreviewPixels = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                visiblePreviewPixels++;
                int high=Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]));
                int low=Math.Min(pixels[i],Math.Min(pixels[i+1],pixels[i+2]));
                if (high-low>20) coloredPreviewPixels++;
            }
            check(emoji + " gallery preview shows the available artwork or a visible placeholder",
                preview.PixelWidth==96&&preview.PixelHeight==96&&(supported ? coloredPreviewPixels : visiblePreviewPixels)>50);
        }
        if(EmojiFontSources.IsAvailable(EmojiFontIds.BlobmojiColor))
        {
            const double size=96;
            var outlier=Element("🇶🇦",size,EmojiFontIds.BlobmojiColor);
            outlier.Transform=Affine2D.Translation(10,10);
            var outlierScene=new SceneDocument { Viewport=new(0,0,size+20,size+20), Elements=[outlier] };
            using var outlierBitmap=SKBitmap.Decode(renderer.ExportPng(outlierScene,assets,true));
            var outlierInk=Ink(outlierBitmap);
            check("Blobmoji's wide Qatar flag is scaled around the global frame center until it fits",
                outlierInk.Left>=10&&outlierInk.Top>=10&&outlierInk.Right<=10+size+1&&outlierInk.Bottom<=10+size+1);
        }
        var sequence = EmojiGlyphs.Measure("👩‍🎨", 18);
        bool joinedSupported = HasAvailableGlyph("👩‍🎨");
        check("ZWJ emoji is shaped as a whole or represented by one placeholder",
            sequence.GlyphCount == (joinedSupported ? 1 : 0) && sequence.MissingGlyphCount == (joinedSupported ? 0 : 1));
        Parallel.For(0, 90, i => EmojiGlyphs.Measure(Samples[i % Samples.Length], 48));
        check("shared emoji measurement cache supports concurrent rendering callers", EmojiGlyphs.Measure("😀", 48) == EmojiGlyphs.Measure("😀", 48));
        var refreshErrors=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var refreshRace=Task.Run(()=>Parallel.For(0,240,i=>
        {
            try { EmojiGlyphs.Measure(Samples[i%Samples.Length],48); }
            catch(Exception ex) { refreshErrors.Enqueue(ex); }
        }));
        Thread.Sleep(5);EmojiFontSources.Refresh();refreshRace.GetAwaiter().GetResult();
        check("font refresh waits for active shaping and leaves the emoji renderer usable",
            refreshErrors.IsEmpty&&EmojiGlyphs.Measure("😀",48).Width>0);
        var beforeRefreshPreview=EmojiArtwork.Image("😀",EmojiFontIds.NotoColor);
        EmojiFontSources.Refresh();
        var afterRefreshPreview=EmojiArtwork.Image("😀",EmojiFontIds.NotoColor);
        check("font refresh invalidates dependent UI thumbnails",!ReferenceEquals(beforeRefreshPreview,afterRefreshPreview));
        CreationAndEditing(check);
        EmojiPickerWindowTests.Run(check);
        PreviewGrid(renderer, assets, check);
    }

    private static CanvasElement Element(string emoji, double size, string? fontId = null)
    {
        return new() { Kind = ElementKind.Emoji, Text = emoji, Width = size, Height = size,
            Style = new() { FontSize = size, Stroke = 0xFF333333, EmojiFontId = EmojiFontIds.Normalize(fontId) } };
    }

    private static void CreationAndEditing(Action<string, bool> check)
    {
        var session = new EditorSession();
        var controller = new EditorController(session) { Tool = EditorTool.Emoji, Emoji = "🤔", ScreenOrigin = new(-300, 100) };
        controller.Style.FontSize = 18;
        controller.Pointer(new(OverlayPointerKind.Down, new(-250, 160), OverlayMouseButton.Left, OverlayModifiers.None));
        controller.Pointer(new(OverlayPointerKind.Up, new(-250, 160), OverlayMouseButton.Left, OverlayModifiers.None));
        var element = session.Document.Elements.Single(); var metrics = EmojiGlyphs.Measure("🤔", 18);
        check("emoji creation centers an independent layer at the document click", element.Kind == ElementKind.Emoji && element.Text == "🤔" && element.Style.FontSize == 18 &&
            element.Width == 18 && element.Height == 18 && element.Transform == Affine2D.Translation(41, 51));
        check("emoji creation remains one undoable object", session.UndoCount == 1 && session.Undo() && session.Document.Elements.Count == 0 && session.Redo());
        var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
        var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
        fields["W"].Text = "36";
        Invoke(inspector, "Apply"); element = session.Document.Elements.Single(); metrics = EmojiGlyphs.Measure("🤔", 36);
        Console.WriteLine($"EMOJI_PROPERTY size={element.Style.FontSize} box={element.Width}x{element.Height} expected={metrics.Width}x{metrics.Height} selection={session.Selection.Count} status={((Controls.TextBlock)Field(inspector, "status")).Text}");
        check("emoji property geometry edits update both dimensions while aspect is locked", element.Width == 36 && element.Height == 36);
        inspector.PrepareForShow();
        var rows=(Dictionary<string,System.Windows.FrameworkElement>)Field(inspector,"fieldContainers");
        check("emoji has its own inspector section name instead of the shared text and numbering title",
            ((Controls.TextBlock)((Controls.Border)Field(inspector,"textSection")).Child).Text==L.Get("Tool.Emoji"));
        check("emoji properties expose replacement and art style while hiding text font, layout and ink",rows["Emoji"].Visibility==System.Windows.Visibility.Visible &&
            rows["EmojiFont"].Visibility==System.Windows.Visibility.Visible &&
            new[]{"FontSize","FontFamily","Text","TextAlignment","Stroke","StyleVariant"}.All(k=>rows[k].Visibility==System.Windows.Visibility.Collapsed));
        var keepAspect=(AspectRatioButton)Field(inspector,"keepAspect");
        check("emoji properties expose a default-locked aspect switch",rows["ConstrainAspect"].Visibility==System.Windows.Visibility.Visible&&
            keepAspect.IsChecked==true);
        keepAspect.IsChecked=false;
        keepAspect.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        element=session.Document.Elements.Single();
        check("unlocking the emoji aspect switch permits independent dimensions",keepAspect.IsChecked==false&&element.ConstrainAspect==false);
        fields["H"].Text="18";Invoke(inspector,"Apply");element=session.Document.Elements.Single();
        check("an unlocked emoji layer accepts non-square width and height",Math.Abs(element.Width-36)<1e-6&&Math.Abs(element.Height-18)<1e-6);
        var field=(EmojiField)Field(inspector,"emojiField");
        check("loading an emoji inspector does not open its picker automatically",!field.IsOpen);
        var emojiFont=(EmojiFontPicker)Field(inspector,"emojiFontPicker");
        check("emoji inspector style selector exposes its localized accessible name",
            System.Windows.Automation.AutomationProperties.GetName(emojiFont)==L.Get("Emoji.Style"));
        emojiFont.SetValue(EmojiFontIds.BlobmojiColor);Invoke(inspector,"Apply");
        element=session.Document.Elements.Single();
        check("emoji property persists the selected art style",element.Style.EmojiFontId==EmojiFontIds.BlobmojiColor);
        var input=(Controls.TextBox)Field(field,"input");
        var transform=element.Transform; var id=element.Id;
        input.Text="汉";Invoke(field,"CommitInput");
        check("invalid input leaves the existing emoji untouched",element.Text=="🤔");
        input.Text="👩🏽‍🎨";Invoke(field,"CommitInput");
        element=session.Document.Elements.Single();
        check("replacing a composed emoji retains its ID, edited frame and placement",element.Kind==ElementKind.Emoji && element.Text=="👩🏽‍🎨" &&
            element.Width==36 && element.Height==18 && element.Transform==transform && element.Id==id);
        check("emoji replacement is undoable", session.Undo() && session.Document.Elements.Single().Text=="🤔");
        element=session.Document.Elements.Single();session.SetSelection([element.Id]);
        check("an emoji converts explicitly to text without changing placement",LayersWindow.ConvertEmojiToText(session,element.Id) &&
            element.Kind==ElementKind.Text && element.Text=="🤔" && element.Transform==transform && session.Undo() &&
            session.Document.Elements.Single().Kind==ElementKind.Emoji);
        check("emoji detached edit checks create no native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
    }

    private static (int Left, int Top, int Right, int Bottom) Ink(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Alpha > 0) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return (left, top, right + 1, bottom + 1);
    }
    private static bool Colorful(SKColor c) => c.Alpha > 128 && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 35;
    private static void PreviewGrid(SceneRenderer renderer, AssetLibrary assets, Action<string,bool> check)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_EMOJI_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var scene = new SceneDocument { Viewport = new(0, 0, 600, 500) };
        for (int i = 0; i < 30; i++)
        {
            var element = Element(Samples[i], 72);
            element.Transform = Affine2D.Translation((i % 6) * 100 + 50 - element.Width / 2, (i / 6) * 100 + 50 - element.Height / 2);
            scene.Elements.Add(element);
        }
        File.WriteAllBytes(Path.Combine(output, "all-thirty-color-emoji.png"), renderer.ExportPng(scene, assets, true));
        string largeFont=EmojiFontSources.IsAvailable(EmojiFontIds.BlobmojiColor)?EmojiFontIds.BlobmojiColor:
            EmojiFontSources.IsAvailable(EmojiFontIds.NotoColor)?EmojiFontIds.NotoColor:EmojiFontIds.Default;
        var large = Element("😀", 800, largeFont);
        large.Width = large.Height = 800;
        large.Transform = Affine2D.Translation(50, 50);
        var largeScene = new SceneDocument { Viewport = new(0, 0, 900, 900), Elements = [large] };
        byte[] largePng = renderer.ExportPng(largeScene, assets, true);
        File.WriteAllBytes(Path.Combine(output, "emoji-fallback-vector-800.png"), largePng);
        using(var largeBitmap = SKBitmap.Decode(largePng))
        {
            var ink = Ink(largeBitmap);
            check("800px fallback emoji renders inside its square safe area",
                ink.Left >= 49 && ink.Top >= 49 && ink.Right <= 851 && ink.Bottom <= 851 &&
                ink.Right - ink.Left > 500 && ink.Bottom - ink.Top > 500);
        }
    }
    private static (int? ColrVersion,bool HasCBDT,bool HasCBLC) FontFormat(string path)
    {
        using var stream=File.OpenRead(path);
        Span<byte> header=stackalloc byte[12];stream.ReadExactly(header);
        int count=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
        int? colev=null;bool cbdt=false,cblc=false;
        Span<byte> record=stackalloc byte[16],version=stackalloc byte[2];
        for(int i=0;i<count;i++)
        {
            stream.ReadExactly(record);
            string tag=System.Text.Encoding.ASCII.GetString(record[..4]);
            long offset=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record[8..12]);
            if(tag=="CBDT")cbdt=true;
            else if(tag=="CBLC")cblc=true;
            else if(tag=="COLR")
            {
                long resume=stream.Position;stream.Position=offset;stream.ReadExactly(version);
                colev=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(version);stream.Position=resume;
            }
        }
        return(colev,cbdt,cblc);
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
