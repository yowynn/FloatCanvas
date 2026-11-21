using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class CustomSymbolTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint window);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("dark");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-symbols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Run(folder, check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(folder, true); }
    }

    private static void Run(string folder, Action<string, bool> check)
    {
        SymbolToolSize(folder, check);
        var src = new EditorSession(new() { Viewport = new(0,0,600,400) });
        using var bitmap = new SKBitmap(64,64);
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
            c.DrawCircle(32,32,28,paint); paint.BlendMode = SKBlendMode.Clear;
            c.DrawCircle(32,32,14,paint);
        }
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png,100);
        var ring = new CanvasElement { Kind = ElementKind.Image, AssetId = src.Assets.Add(png.ToArray()), Width=64,Height=64,Transform=Affine2D.Translation(40,40) };
        var dot = new CanvasElement { Kind = ElementKind.Ellipse, Width=12,Height=12, Transform=Affine2D.Translation(122,48),
            Style=new(){Variant=ElementAppearance.ShapeUniform, PrimaryColor=0xFF229966} };
        var background = new CanvasElement { Kind=ElementKind.Rectangle,Width=500,Height=300,Style=new(){Variant=ElementAppearance.ShapeUniform,PrimaryColor=0xFF7181B2} };
        src.Add(background);src.Add(ring);src.Add(dot);src.SetSelection([ring.Id,dot.Id]);
        SymbolShape? TraceSelection(double opacity=SymbolTraceSource.DefaultOpacity) =>
            ToolOptionsPanel.PrepareSelectionAsync(src).GetAwaiter().GetResult().Trace(opacity)?.Shape;
        var shape = TraceSelection()!;
        check("a selected transparent PNG and a separate vector keep an outer contour, hole and island",
            shape.Contours.Length == 3 && shape.AspectRatio is > 1.5 and < 1.7);
        var layer = new CanvasElement {Kind=ElementKind.Symbol,Symbol=SymbolKind.Custom,CustomSymbol=shape,Width=90,Height=56,
            Style=new(){Variant=ElementAppearance.ShapeUniform,PrimaryColor=0xFFD04041}};
        var doc = new SceneDocument {Viewport=new(0,0,95,60),Elements=[layer]};
        using var renderer = new SceneRenderer(); var assets=new AssetLibrary();
        using (var fill=SKBitmap.Decode(renderer.ExportPng(doc,assets,true)))
            check("recoloring retains the transparent center and the gap between the two components",
                fill.GetPixel(28,28).Alpha==0 && fill.GetPixel(50,28).Alpha==255 && fill.GetPixel(84,10).Alpha==255 && fill.GetPixel(70,20).Alpha==0 &&
                !ElementGeometry.HitTest(layer,new(28,28),0) && ElementGeometry.HitTest(layer,new(84,10),0));
        layer.Style.Variant=ElementAppearance.ShapeIndependent; layer.Style.SecondaryColor=0; layer.Style.StrokeWidth=3;
        foreach(var pattern in Enum.GetValues<StrokePattern>())
        {
            layer.Style.StrokePattern=pattern;
            using var ink=SKBitmap.Decode(renderer.ExportPng(doc,assets,true));
            bool anyNear(double radius) => Enumerable.Range(0,56).Any(y=>Enumerable.Range(0,56).Any(x=>
                Math.Abs(Math.Sqrt((x-28)*(x-28)+(y-28)*(y-28))-radius)<2 && ink.GetPixel(x,y).Alpha>90));
            check("custom " + pattern + " strokes reach both ring edges and leave the hole transparent",
                anyNear(26.5) && anyNear(15.5) && ink.GetPixel(28,28).Alpha==0 && ink.GetPixel(70,20).Alpha==0);
        }
        src.SetSelection([dot.Id]);
        var ellipse=TraceSelection()!;
        check("an opaque vector ellipse retains curved segments",ellipse.Contours.Length==1 && ellipse.Contours[0].Any(v=>v.Control1.HasValue));
        var line = new CanvasElement { Kind=ElementKind.Line, Points=[new(10,160),new(95,175)], Style=new(){PrimaryColor=0xFF248050,StrokeWidth=9} };
        src.Add(line);
        var lineShape=TraceSelection()!;
        check("an opaque line is extracted with its geometric stroke width and caps",lineShape.AspectRatio>2 && lineShape.Contours[0].Any(v=>v.Control1.HasValue));
        var overlap=dot.Clone(true);overlap.Transform=Affine2D.Translation(127,48);src.Add(overlap);src.SetSelection([dot.Id,overlap.Id]);
        check("overlapping opaque layers have one contour without an internal seam",TraceSelection()!.Contours.Length==1);
        src.SetSelection([overlap.Id]);src.UpdateSelection(e=>e.Style.PrimaryColor=0xFE229966, "Change alpha");
        check("254 alpha survives the default cutoff but is excluded at 100 percent", TraceSelection() is not null && TraceSelection(100) is null);

        var library=new ToolPresetLibrary(Path.Combine(folder,"presets.json"));var editor=new EditorController(src);
        editor.ApplyParameters(library.Apply("official.symbol-filled"));
        var panel=new ToolOptionsPanel(editor,library,new ColorLibrary(Path.Combine(folder,"colors.json")));
        var window=new Window { Content=panel, Width=ToolOptionsPanel.PreferredWidth+2, Height=650, ShowInTaskbar=false,ShowActivated=false };
        window.Show();panel.PrepareForShow();
        try
        {
            var gallery=Field<UniformGrid>(panel,"symbolGallery");var slot=Field<Button>(panel,"customSymbolSlot");var extract=Field<Button>(panel,"extractSymbol");
            check("18 built-ins occupy three rows and the single custom slot is initially empty",gallery.Columns==6 && gallery.Children.Count==18 && !slot.IsEnabled);
            src.SetSelection([]);panel.RefreshDocumentState();check("extract observes a cleared selection",!extract.IsEnabled);
            src.SetSelection([ring.Id,dot.Id]);panel.RefreshDocumentState();check("selecting source layers enables extraction",extract.IsEnabled);
            void Extract(string action, bool overlay = false, Func<SymbolTraceWindow,bool>? beforeClose = null)
            {
                var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
                var limit=System.Diagnostics.Stopwatch.StartNew();bool answered=false, managed=false;
                bool ownerBlockedDuringModal = false, ownerReadyAtDestroy = false;
                Exception? failure=null;
                timer.Tick+=(_,_)=>{
                    var preview=Application.Current.Windows.OfType<SymbolTraceWindow>().FirstOrDefault(w=>w.Owner==window && w.IsVisible);
                    if(!answered && preview is { IsLoaded: true, IsUpdating: false })
                    {
                        managed=preview.Resources.Contains("FloatCanvas.SessionWindowCoordinator.Modal");
                        try
                        {
                            if (limit.Elapsed.TotalSeconds>8) throw new TimeoutException("Symbol preview did not reach its requested state");
                            if (beforeClose is not null)
                            {
                                timer.Stop();
                                try { if (!beforeClose(preview)) return; }
                                finally { timer.Start(); }
                            }
                            answered=true;
                            if (managed)
                            {
                                var ownerHandle = new WindowInteropHelper(window).Handle;
                                ownerBlockedDuringModal = !IsWindowEnabled(ownerHandle);
                                ((HwndSource)PresentationSource.FromVisual(preview)!).AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
                                {
                                    if (msg == 0x0002) ownerReadyAtDestroy = IsWindowEnabled(ownerHandle);
                                    return 0;
                                });
                            }
                            if (action=="confirm" && !Field<Button>(preview,"accept").IsEnabled) throw new InvalidOperationException("Confirm disabled");
                            if(action=="escape")
                                preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(preview)!,Environment.TickCount,Key.Escape)
                                    { RoutedEvent=Keyboard.KeyDownEvent });
                            else
                            {
                                var button=action=="close" ? (Button)preview.Template.FindName("Close",preview) :
                                    SettingsWindowFixture.Descendants(preview).OfType<Button>().Single(b=>action=="confirm" ? b.IsDefault : b.IsCancel);
                                typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(button,null);
                            }
                        }
                        catch(Exception ex){failure=ex.GetBaseException();answered=true;preview.Close();}
                    }
                    if(limit.Elapsed.TotalSeconds>8 && preview?.IsVisible==true)
                    { failure??=new TimeoutException("Symbol preview stayed open after "+action);preview.Close(); }
                    if(answered && !Field<bool>(panel,"extractingSymbol") || limit.Elapsed.TotalSeconds>8)frame.Continue=false;
                };
                timer.Start();extract.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.PushFrame(frame);timer.Stop();
                if(failure is not null)throw new InvalidOperationException("Symbol preview "+action+" (overlay="+managed+")",failure);
                if (overlay) check("the owner stays modal during input and can take focus before the child HWND is destroyed on "+action,
                    ownerBlockedDuringModal && ownerReadyAtDestroy);
                check($"symbol preview {action} completes on {(overlay ? "the editing overlay" : "WPF ShowDialog")}",
                    answered && managed==overlay && !ComponentDispatcher.IsThreadModal && !Field<bool>(panel,"extractingSymbol") &&
                    extract.IsEnabled && IsWindowEnabled(new WindowInteropHelper(window).Handle));
            }
            Extract("cancel");check("cancelling the preview preserves the empty slot",editor.CustomSymbol is null);
            if(!PresentationDesktopAvailability.IsCurrentInputDesktop())
            {
                var bounds=new PixelBounds(-24000,-24000,1200,850);
                Func<IReadOnlyList<MonitorInfo>> monitors=()=>[new("symbol-preview",bounds,bounds,96,96,true)];
                using var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),BindingFlags.Instance|BindingFlags.NonPublic,null,
                    [OverlayMonitorScope.PrimaryScreen,monitors,false],null)!;
                host.EnableSystemInput();
                using var coordinator=new SessionWindowCoordinator(host,window);
                var editing=typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!;
                // Exercise the real overlay modal loop without taking input from the user's desktop.
                editing.SetValue(host,true);
                try
                {
                    Extract("confirm",true,preview =>
                    {
                        var child = new Window { Owner = preview, Width = 150, Height = 80, ShowInTaskbar = false };
                        bool veto = true, blockedAfterVeto = false, restoredOneLevel = false;
                        child.Closing += (_, e) => e.Cancel = veto;
                        child.Loaded += (_, _) => child.Dispatcher.BeginInvoke(() =>
                        {
                            var parent = new WindowInteropHelper(preview).Handle;
                            var rootOwner = new WindowInteropHelper(window).Handle;
                            SessionWindowCoordinator.SetModalResult(child, true);
                            blockedAfterVeto = child.IsVisible && !IsWindowEnabled(parent) && !IsWindowEnabled(rootOwner) &&
                                SessionWindowCoordinator.HasModalChild(preview) && SessionWindowCoordinator.HasModalChild(window);
                            veto = false;
                            child.Closed += (_, _) => restoredOneLevel = IsWindowEnabled(parent) && !IsWindowEnabled(rootOwner);
                            SessionWindowCoordinator.Close(child);
                        });
                        bool? result = SessionWindowCoordinator.ShowModal(child);
                        check("a vetoed close keeps the nested modal intact and does not return an earlier confirm result", blockedAfterVeto && result is null);
                        check("dismissing a nested child enables only its immediate modal level", restoredOneLevel && !IsWindowEnabled(new WindowInteropHelper(window).Handle));
                        return true;
                    });
                    var accepted=editor.CustomSymbol;
                    check("overlay confirmation publishes the extracted shape",accepted?.Fingerprint==shape.Fingerprint);
                    src.SetSelection([dot.Id]);panel.RefreshDocumentState();
                    foreach(var action in new[]{"cancel","close","escape"})
                    {
                        Extract(action,true);
                        check("overlay "+action+" preserves the prior slot",ReferenceEquals(editor.CustomSymbol,accepted));
                    }
                    src.SetSelection([ring.Id,dot.Id]);panel.RefreshDocumentState();
                }
                finally { editing.SetValue(host,false); }
            }
            Extract("confirm");check("confirming the preview installs the extracted silhouette at its natural aspect",
                editor.Symbol==SymbolKind.Custom && editor.CustomSymbol!.Fingerprint==shape.Fingerprint &&
                Math.Abs(editor.DefaultWidth/editor.DefaultHeight-shape.AspectRatio)<.001);
            src.SetSelection([overlap.Id]);panel.RefreshDocumentState();
            int stage=0;
            Extract("cancel",beforeClose:preview=>
            {
                var threshold=Field<NumericValueField>(preview,"opacity");var detail=Field<NumericValueField>(preview,"detail");
                var ok=Field<Button>(preview,"accept");
                switch(stage++)
                {
                    case 0:
                        check("the first preview includes nearly opaque pixels",preview.Result is not null && threshold.Value==50 && detail.Value==50);
                        threshold.Input.Text="100";
                        check("a changed threshold immediately invalidates the previous result",!ok.IsEnabled && preview.Result is null);
                        return false;
                    case 1:
                        check("an empty threshold keeps the dialog open with an explanation and no confirmation",!ok.IsEnabled &&
                            Field<TextBlock>(preview,"status").Text==FloatCanvas.Localization.AppText.Get("Symbol.TraceEmpty"));
                        threshold.Input.Text="25"; threshold.Input.Text="99.6";
                        detail.Input.Text="0"; detail.Input.Text="80";
                        return false;
                    default:
                        check("only the newest threshold and detail can be confirmed",preview.Result is not null && ok.IsEnabled &&
                            threshold.Value==99.6 && detail.Value==80);
                        return true;
                }
            });
            check("cancelling an adjusted preview keeps the previous slot",editor.CustomSymbol!.Fingerprint==shape.Fingerprint);
            src.SetSelection([ring.Id,dot.Id]);panel.RefreshDocumentState();
            var smoother=ToolOptionsPanel.PrepareSelectionAsync(src).GetAwaiter().GetResult().Trace(50,0)!.Shape;
            bool changedDetail=false;
            Extract("confirm",beforeClose:preview=>
            {
                if (changedDetail) return true;
                changedDetail=true;Field<NumericValueField>(preview,"detail").Slider.Value=0;
                return false;
            });
            check("confirm stores the exact geometry shown after adjusting the detail rail",editor.CustomSymbol?.Fingerprint==smoother.Fingerprint &&
                smoother.Fingerprint!=shape.Fingerprint);
            shape=smoother;
            var styleChoice=Field<SegmentedChoice>(panel,"linkColors");
            var staticIcon=((StackPanel)styleChoice.Items[0].Content).Children[0];
            var saved=library.Save("Ring and dot",editor.Parameters);
            var statePath=Path.Combine(folder,"working.json");var store=new ToolStateStore(statePath);
            store.Tools[EditorTool.Symbol]=editor.Parameters;store.Save();
            check("working state and personal presets round-trip the shape",
                new ToolStateStore(statePath).Tools[EditorTool.Symbol].CustomSymbol?.Fingerprint==shape.Fingerprint &&
                new ToolPresetLibrary(Path.Combine(folder,"presets.json")).Apply(saved.Id).CustomSymbol?.Fingerprint==shape.Fingerprint);
            CanvasElement Click(double x)
            {
                var pos=new OverlayPoint(x,220);
                editor.Pointer(new(OverlayPointerKind.Down,pos,OverlayMouseButton.Left,OverlayModifiers.Control));
                editor.Pointer(new(OverlayPointerKind.Up,pos,OverlayMouseButton.Left,OverlayModifiers.Control));
                return src.SelectedElements().Single();
            }
            Field<Dictionary<string,NumericValueField>>(panel,"numericFields")["SymbolSize"].Input.Text="120";
            var own=Click(100);
            var ownBox=ElementGeometry.Bounds(own,false);
            check("a custom shape uses the same long-edge field and natural rectangular box",Math.Abs(ownBox.Width-120)<1e-6 &&
                Math.Abs(ownBox.Width/ownBox.Height-shape.SizeAtLongEdge(1).X/shape.SizeAtLongEdge(1).Y)<1e-6);
            gallery.Children.OfType<Button>().Single(b=>Equals(b.Tag,SymbolKind.Star)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.PrepareForShow();
            check("choosing a built-in keeps the slot and the style tab keeps its static icon",
                slot.IsEnabled && editor.Parameters.SymbolSize==120 && editor.CustomSymbol?.Fingerprint==shape.Fingerprint &&
                ReferenceEquals(staticIcon,((StackPanel)styleChoice.Items[0].Content).Children[0]));
            var builtin=Click(250);
            check("only a custom placement receives a stored outline",own.CustomSymbol?.Fingerprint==shape.Fingerprint && builtin.CustomSymbol is null);
            var parameters=editor.Parameters; parameters.Symbol=SymbolKind.Custom;parameters.CustomSymbol=ellipse;editor.ApplyParameters(parameters);
            var other=Click(390);
            VerifyLayers(folder,src,own,other,builtin,shape,ellipse,check);
        }
        finally { window.Close(); }
    }

    private static void SymbolToolSize(string folder, Action<string,bool> check)
    {
        var session = new EditorSession(new() { Viewport = new(0,0,260,220) });
        var editor = new EditorController(session);
        var library = new ToolPresetLibrary(Path.Combine(folder,"sizing-presets.json"));
        editor.ApplyParameters(library.Apply("official.symbol-filled"));
        var panel = new ToolOptionsPanel(editor,library,new ColorLibrary(Path.Combine(folder,"sizing-colors.json")));
        panel.PrepareForShow();
        var rows = Field<Dictionary<string,FrameworkElement>>(panel,"rows");
        var sizes = Field<Dictionary<string,NumericValueField>>(panel,"numericFields");
        var gallery = Field<UniformGrid>(panel,"symbolGallery");
        check("symbol options have one size above the gallery and no dimension pair or aspect switch",
            rows["SymbolSize"].Visibility==Visibility.Visible && Grid.GetRow(rows["SymbolSize"])+1==Grid.GetRow(rows["Symbols"]) &&
            rows["Size"].Visibility==Visibility.Collapsed && rows["Aspect"].Visibility==Visibility.Collapsed && sizes["SymbolSize"].Value==72);
        sizes["SymbolSize"].Input.Text="96";
        void Pointer(OverlayPointerKind kind, int x, int y, OverlayModifiers mods = OverlayModifiers.None) =>
            editor.Pointer(new(kind,new(x,y),OverlayMouseButton.Left,mods|OverlayModifiers.Control));
        using var renderer = new SceneRenderer();
        foreach (var button in gallery.Children.OfType<Button>())
        {
            var kind = (SymbolKind)button.Tag;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pointer(OverlayPointerKind.Down,128,110); Pointer(OverlayPointerKind.Up,128,110);
            var placed = session.SelectedElements().Single();
            var box = ElementGeometry.Bounds(placed,false);
            var expected = SymbolGeometry.Shape(kind).SizeAtLongEdge(96);
            check(kind+" places its natural 96 px ink frame at the click",
                box.Center.DistanceTo(new(128,110))<1e-7 && Math.Abs(box.Width-expected.X)<1e-7 && Math.Abs(box.Height-expected.Y)<1e-7 &&
                Math.Abs(Math.Max(box.Width,box.Height)-96)<1e-7 && placed.ConstrainAspect==true);
            var doc = new SceneDocument {Viewport=session.Document.Viewport,Elements=[placed.Clone()]};
            using var bmp = SKBitmap.Decode(renderer.ExportPng(doc,session.Assets,true));
            int left=bmp.Width,right=0,top=bmp.Height,bottom=0;
            for(int y=0;y<bmp.Height;y++)for(int x=0;x<bmp.Width;x++)
                if(bmp.GetPixel(x,y).Alpha>0){left=Math.Min(left,x);right=Math.Max(right,x+1);top=Math.Min(top,y);bottom=Math.Max(bottom,y+1);}
            check(kind+" rendered silhouette reaches all four selection edges",
                Math.Abs(box.Left-left)<2 && Math.Abs(box.Top-top)<2 && Math.Abs(box.Right-right)<2 && Math.Abs(box.Bottom-bottom)<2);
        }
        gallery.Children.OfType<Button>().Single(b=>Equals(b.Tag,SymbolKind.Triangle)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pointer(OverlayPointerKind.Down,180,160); Pointer(OverlayPointerKind.Move,60,120);
        RectD SelectedBox() => ElementGeometry.Bounds(session.SelectedElements().Single(),false);
        var normal = SelectedBox();
        check("reverse drag retains an equilateral triangle", Math.Abs(normal.Width-120)<1e-7 && Math.Abs(normal.Height-60*Math.Sqrt(3))<1e-7 &&
            normal.Right==180 && Math.Abs(normal.Bottom-160)<1e-7);
        Pointer(OverlayPointerKind.Move,60,120,OverlayModifiers.Shift);
        var stretched=SelectedBox();
        check("Shift changes the live triangle to the exact rectangular drag box",Math.Abs(stretched.Width-120)<1e-7 && Math.Abs(stretched.Height-40)<1e-7);
        Pointer(OverlayPointerKind.Move,60,120); var released=SelectedBox();
        check("releasing Shift restores the original ratio before pointer up", Math.Abs(released.Height-normal.Height)<1e-7);
        Pointer(OverlayPointerKind.Up,60,120,OverlayModifiers.Shift);
        var layer=session.SelectedElements().Single();
        check("stretched layer retains its independent editable size and lock", Math.Abs(SelectedBox().Height-40)<1e-7 && layer.ConstrainAspect==true);
        var inspector=new InspectorWindow(()=>session);inspector.PrepareForShow();
        try
        {
            var fields=Field<Dictionary<string,NumericValueField>>(inspector,"numericFields");
            check("inspector width and height measure the painted frame",Math.Abs(fields["W"].Value-120)<1e-5 && Math.Abs(fields["H"].Value-40)<1e-5);
            Field<ComboBox>(inspector,"symbolChoice").SelectedValue=SymbolKind.Minus;inspector.CompletePendingEdit();
            var swapped=SelectedBox();
            check("changing a placed layer's symbol preserves its edited ink rectangle",
                swapped.Center.DistanceTo(stretched.Center)<1e-7 && Math.Abs(swapped.Width-stretched.Width)<1e-7 && Math.Abs(swapped.Height-stretched.Height)<1e-7);
        }
        finally{inspector.Close();}
    }

    private static void VerifyLayers(string folder,EditorSession src,CanvasElement first,CanvasElement second,CanvasElement builtin,SymbolShape shape,SymbolShape ellipse,Action<string,bool> check)
    {
        var inspector=new InspectorWindow(()=>src);
        var choice=Field<ComboBox>(inspector,"symbolChoice");
        void Select(params Guid[] ids){src.SetSelection(ids);inspector.PrepareForShow();}
        void Set(SymbolKind kind){choice.SelectedValue=kind;inspector.CompletePendingEdit();}
        bool OffersCustom()=>choice.Items.OfType<ComboBoxItem>().Any(i=>Equals(i.Tag,SymbolKind.Custom));
        try
        {
            Select(builtin.Id);check("a built-in-born layer has no Custom choice",!OffersCustom());
            Select(first.Id,builtin.Id);check("an ineligible mixed selection has no Custom choice",!OffersCustom());
            Select(first.Id,second.Id);check("eligible differently shaped layers offer Custom",OffersCustom());
            Set(SymbolKind.Star);
            var path=Path.Combine(folder,"inactive.floatcanvas");var projects=new ProjectStore();
            projects.SaveAsync(path,src.Document,src.Assets).GetAwaiter().GetResult();
            var reopened=new EditorSession(projects.LoadAsync(path).GetAwaiter().GetResult().Document);
            var reopenedInspector=new InspectorWindow(()=>reopened);
            try
            {
                reopened.SetSelection([first.Id,second.Id]);reopenedInspector.PrepareForShow();
                var combo=Field<ComboBox>(reopenedInspector,"symbolChoice");
                check("the project reopens showing the built-in while each private outline stays stored",reopened.SelectedElements().All(e=>e.Symbol==SymbolKind.Star && e.CustomSymbol!=null));
                combo.SelectedValue=SymbolKind.Custom;reopenedInspector.CompletePendingEdit();
                var a=reopened.Document.Elements.Single(e=>e.Id==first.Id);var b=reopened.Document.Elements.Single(e=>e.Id==second.Id);
                check("restoring multiple layers uses each saved outline, independently of the tool",a.Symbol==SymbolKind.Custom && b.Symbol==SymbolKind.Custom &&
                    a.CustomSymbol!.Fingerprint==shape.Fingerprint && b.CustomSymbol!.Fingerprint==ellipse.Fingerprint);
                check("restoring the selection is one reversible edit",reopened.Undo() && reopened.SelectedElements().All(e=>e.Symbol==SymbolKind.Star));
                reopened.Redo(); reopened.SetSelection([first.Id]);
                var transfer=ObjectClipboardCommand.CreateSnapshot(reopened);var destination=new EditorSession();
                ObjectClipboardCommand.Insert(destination,transfer,reopened.Assets,new(0,0,500,300));
                check("copying a custom symbol to a different document carries its independent contour",
                    destination.SelectedElements().Single().CustomSymbol?.Fingerprint==shape.Fingerprint);
            }
            finally {reopenedInspector.Close();}
            var tree=new LayersWindow(()=>src);tree.PrepareForShow();
            try{check("layer rows carry their displayed symbol geometry and keep custom names",tree.VisibleNodes.Single(r=>r.Id==first.Id).Symbol==SymbolGeometry.Shape(SymbolKind.Star) &&
                InspectorWindow.ElementName(new(){Kind=ElementKind.Symbol,Symbol=SymbolKind.Ring,Name="My label"})=="My label");}
            finally{tree.Close();}
        }
        finally{inspector.Close();}
    }
}
