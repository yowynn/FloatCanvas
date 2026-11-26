using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using Threading = System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

/// <summary>
/// Full production CreateToolbarV2 content, rendered detached and in isolated native hosts.
/// Thumbnail work uses an invisible offscreen, nonactivating surface. The fixture never
/// registers hotkeys, enters editing, shows application windows, or injects input.
/// </summary>
internal static class ToolbarAcceptanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, bool headerOnly = false)
    {
        if (Wpf.Application.Current is not null) throw new InvalidOperationException("Run --toolbar2-only in a dedicated process.");
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_TOOLBAR2_PREVIEW_DIR") ?? Path.Combine(Path.GetTempPath(),"FloatCanvas-ToolbarAcceptance",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string profile=Path.Combine(output,"profile-"+Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        new UserSettings {Theme="light",Language="zh-CN",ShowFloatingLogo=false}.Save();
        var app=new Wpf.Application {ShutdownMode=Wpf.ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new Threading.DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? session=null; OverlayHost? host=null; Wpf.Window? toolbar=null; Wpf.FrameworkElement? shell=null;
        nint[] handles=[];
        try
        {
            // The internal hidden-host seam is deliberately reached through reflection; AppTests
            // does not need to broaden the platform assembly's public/internal API.
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),BindingFlags.Instance|BindingFlags.NonPublic,null,
                [OverlayMonitorScope.PrimaryScreen,false],null)!;
            // Project visible-canvas UI without showing its native HWNDs. Otherwise
            // test document edits legitimately trigger the hidden-canvas auto-reveal policy.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host,true);
            session=new DesktopSession(app,host);
            Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            toolbar=Field<Wpf.Window>(session,"_toolbar"); shell=(Wpf.FrameworkElement)toolbar.Content;
            handles=host.Diagnostics.SelectMany(pair=>new[]{pair.DisplayHandle,pair.InputHandle}).Append(host.ControlHandle).ToArray();
            toolbar.Content=null;
            if(headerOnly)
            {
                AppTheme.UsePopupPalette(shell);
                using var native=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("FloatCanvas header verification")
                    {Width=1200,Height=180,PositionX=-22000,PositionY=-22000,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000088});
                native.RootVisual=shell;
                ShowWindow(native.Handle,4);
                try {CheckHeaderStates(check,shell,Field<ToolbarUpperRow>(session,"_toolbarUpperRow"),Field<BoundFileIndicator>(session,"_boundFileIndicator"),Field<RootCanvasToolbarHeader>(session,"_rootCanvasHeader"),output);}
                finally {native.RootVisual=null;}
                return;
            }
            var body=Field<Controls.Border>(session,"_toolbarExpandedContent");
            var grid=(Controls.Grid)body.Child;
            var editor=Field<EditorController>(session,"_editor");
            var library=Field<StampLibrary>(session,"_stampLibrary");
            var shelf=Field<StampShelfControl>(session,"_stampShelf");
            var display=Field<ToolbarStatusDisplay>(session,"_toolbarStatusDisplay");
            var status=Field<Controls.TextBlock>(session,"_status");
            var allButtons=grid.Children.OfType<Controls.Button>().ToArray();
            var buttons=allButtons.Where(button=>Controls.Grid.GetRow(button)==0).ToArray();
            var tools=Field<Dictionary<EditorTool,Controls.Button>>(session,"_toolbarToolButtons");

            var saved=Task.Run(async()=>
            {
                await library.LoadAsync();
                var source=new EditorSession(new SceneDocument {Elements=[new CanvasElement {Kind=ElementKind.Rectangle,Width=80,Height=40,Style=new ElementStyle {Fill=0xFF2383E2,StrokeWidth=0}}]});
                source.SetSelection(source.Document.Elements.Select(element=>element.Id));
                var first=await library.SaveSelectionAsync(source,"示例印章一");
                await library.PinAsync(2,first.Id);
                var second=await library.SaveSelectionAsync(source,"示例印章二");
                var loaded=await library.LoadStampAsync(second.Id);
                return(first,second,loaded);
            }).GetAwaiter().GetResult();
            check("saving a preset atomically fills the first empty slot and preserves existing assignments",library.Pins[0]==saved.first.Id && library.Pins[1]==saved.second.Id && library.Pins[2]==saved.first.Id);
            var target=new EditorSession(new SceneDocument {Elements=[new CanvasElement {Kind=ElementKind.Ellipse,Width=10,Height=10}]});
            target.SelectAll(); int before=target.UndoCount;
            StampLibrary.Place(saved.loaded,target,new PointD(200,200));
            check("placing a stamp preserves prior objects and commits one insertion",target.Document.Elements.Count==2 && target.UndoCount==before+1);
            check("placing a stamp leaves no selected transform frame",target.Selection.Count==0);
            shelf.Refresh(); Invoke(session,"RefreshToolbarState"); PrepareShelfPreview(shell,shelf); Measure(shell);
            check("visible offscreen shelf loads every populated slot image",((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>()
                .Take(3).All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.Image>().Single().Source is Imaging.BitmapSource {IsFrozen:true}));

            var optionsPanel=Field<ToolOptionsPanel>(session,"_toolOptionsPanel");
            var optionsWindow=Field<Wpf.Window>(session,"_toolOptionsWindow");
            var optionsHeading=Field<Controls.TextBlock>(optionsPanel,"heading");
            var previousParameters=editor.Parameters;
            string hiddenHeading=optionsHeading.Text;
            editor.Tool=EditorTool.Highlighter;Invoke(session,"RefreshToolbarState");
            check("switching tools leaves hidden options controls untouched",!optionsWindow.IsVisible&&optionsHeading.Text==hiddenHeading);
            optionsPanel.PrepareForShow();
            check("explicit options synchronization reads the latest tool state",optionsHeading.Text==L.Get("ToolDescription.Highlighter"));
            editor.ApplyParameters(previousParameters);Invoke(session,"RefreshToolbarState");optionsPanel.PrepareForShow();

            CheckEqualRowInsets(check,body,grid,display,"production toolbar",1);
            check("production toolbar preserves two 26 DIP rows separated by two DIP",grid.RowDefinitions.Select(row=>row.Height.Value).SequenceEqual(new[]{26d,2d,26d}));
            check("production toolbar shares 30 DIP columns around tool and action separators",grid.ColumnDefinitions.Count==32 && grid.ColumnDefinitions.Select((column,index)=>(column,index)).All(pair=>pair.column.Width.IsAbsolute && pair.column.Width.Value==(pair.index is 2 or 13 or 16 or 20 or 23?10:30)));
            check("production upper row retains every action around its dedicated separators",buttons.Length==25 && buttons.Select(Controls.Grid.GetColumn).Order().SequenceEqual(Enumerable.Range(0,32).Where(column=>column is not (2 or 13 or 16 or 20 or 23 or 28 or 29))) && !buttons.Any(button=>Equals(button.Tag,"tool:Blur")));
            check("compact 60 DIP tool body aligns with the shared 101 DIP mascot",Near(body.ActualHeight,60)&&Near(body.TranslatePoint(new Wpf.Point(),shell).Y,39)&&ToolbarLauncher.ArtworkSize==101);
            var fileBubble=Field<BoundFileIndicator>(session,"_boundFileIndicator");
            fileBubble.Refresh(@"C:\图标验收\示例画布.floatcanvas",true,true);Measure(shell);
            check("bound-file bubble matches the shared upper row height and clearance",fileBubble.ActualHeight==ToolbarUpperRow.ExtensionHeight&&Near(fileBubble.TranslatePoint(new Wpf.Point(0,fileBubble.ActualHeight),shell).Y+ToolbarUpperRow.SurfaceGap,body.TranslatePoint(new Wpf.Point(),shell).Y));
            CheckHeaderStates(check,shell,Field<ToolbarUpperRow>(session,"_toolbarUpperRow"),fileBubble,Field<RootCanvasToolbarHeader>(session,"_rootCanvasHeader"),output);
            Save(shell,Path.Combine(output,"production-toolbar-bound-file-light.png"),1.5);
            fileBubble.Refresh(null,false,true);
            check("production lower row puts stamp actions under Select and Eraser",allButtons.Any(button=>Equals(button.Tag,"manage-stamps")&&Controls.Grid.GetRow(button)==2&&Controls.Grid.GetColumn(button)==0) && allButtons.Any(button=>Equals(button.Tag,"save-stamp")&&Controls.Grid.GetRow(button)==2&&Controls.Grid.GetColumn(button)==1));
            check("production lower row keeps its stamp actions and fills the remaining width with LCD",Controls.Grid.GetRow(shelf)==2 && Controls.Grid.GetColumn(shelf)==3 && Controls.Grid.GetColumnSpan(shelf)==10 && Controls.Grid.GetRow(display)==2 && Controls.Grid.GetColumn(display)==14 && Controls.Grid.GetColumnSpan(display)==18 && allButtons.Where(button=>Controls.Grid.GetRow(button)==2).Select(Controls.Grid.GetColumn).Order().SequenceEqual(new[]{0,1}));
            var billboard=allButtons.Single(button=>Equals(button.Tag,"billboard"));
            check("placement entry sits immediately right of canvas in the upper row and is unavailable without perspective",Controls.Grid.GetColumn(billboard)==15&&Controls.Grid.GetRow(billboard)==0&&!billboard.IsEnabled&&!editor.BillboardMode&&Controls.ToolTipService.GetShowOnDisabled(billboard));
            check("placement idle chrome reserves the same transparent one-DIP border as ordinary buttons",billboard.BorderThickness==new Wpf.Thickness(1)&&billboard.BorderBrush==Media.Brushes.Transparent);
            var surfaceArtwork=Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString();
            editor.Session.Document.Canvas.Projection=CanvasCalibration.CreateInitial(new(0,0,1200,800),10,8);
            editor.Session.Document.Canvas.Projection.Enabled=true;Invoke(session,"RefreshToolbarState");Measure(shell);
            var canvasAction=buttons.Single(button=>Equals(button.Tag,"canvas"));
            var canvasSample=Descendants(canvasAction).OfType<CanvasParameterIcon>().Single();
            var projection=editor.Session.Document.Canvas.Projection;
            projection.Columns++;projection.Rows++;Invoke(session,"RefreshToolbarState");
            check("canvas preview retains its fixed four-by-four sample while calibration dimensions change",ReferenceEquals(canvasSample,Descendants(canvasAction).OfType<CanvasParameterIcon>().Single()));
            projection.Columns--;projection.Rows--;
            var canvasGrid=editor.Session.Document.Canvas.Grid;uint savedGridColor=canvasGrid.Color;
            canvasGrid.Color=0xFFB27428;Invoke(session,"RefreshToolbarState");Measure(shell);
            check("canvas preview immediately refreshes when its displayed grid parameters change",!ReferenceEquals(canvasSample,Descendants(canvasAction).OfType<CanvasParameterIcon>().Single()));
            canvasGrid.Color=savedGridColor;Invoke(session,"RefreshToolbarState");Measure(shell);
            CheckCanvasPresentation(check,session,editor,toolbar,shell,canvasAction,billboard,
                buttons.Single(button=>Equals(button.Tag,"import")),buttons.Single(button=>Equals(button.Tag,"capture")),output);
            check("enabled perspective keeps idle Canvas on ordinary tool chrome with a live canvas sample",billboard.IsEnabled&&tools[EditorTool.Canvas]==canvasAction&&canvasAction.Background==app.Resources["CanvasSurface"]&&canvasAction.BorderBrush==Media.Brushes.Transparent&&Descendants(canvasAction).OfType<CanvasParameterIcon>().Count()==1);
            foreach(var tool in new[]{EditorTool.Select,EditorTool.Eraser})
            {
                var preview=ToolParameterIcon.Create(new ToolPresetLibrary(Path.Combine(profile,"preview-recipes.json")).OfficialFor(tool).First().Parameters);
                check(tool+" toolbar artwork uses the same preview as its official preset",Descendants(tools[tool]).OfType<Shapes.Path>().Single().Data.ToString()==Descendants(preview).OfType<Shapes.Path>().Single().Data.ToString());
            }
            var extraButtons=Field<Dictionary<EditorTool,Controls.Button>>(session,"_extraToolButtons");
            Invoke(session,"RefreshMoreToolPreviews");
            var effectEntries=extraButtons.Where(pair=>pair.Key is EditorTool.Blur or EditorTool.Pixelate).ToArray();
            check("more menu contains both blur and pixelate exactly once",effectEntries.Length==2&&effectEntries.Select(item=>item.Key).Distinct().Count()==2);
            check("more menu uses live neutral tool previews for both blur and pixelate",effectEntries.All(item=>item.Value.Content is Wpf.DependencyObject icon&&Descendants(icon).OfType<Shapes.Path>().Single().Stroke==app.Resources["CanvasText"]));
            CheckPresentationMenuPreviews(check,session,extraButtons);
            CheckMorePreviewLifecycle(check,session,editor,extraButtons,shell);
            var parametersBefore=System.Text.Json.JsonSerializer.Serialize(editor.Parameters);
            billboard.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
            Measure(shell);
            check("billboard toggle changes its actual artwork and active style without changing tool parameters",editor.BillboardMode&&Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString()!=surfaceArtwork&&billboard.Background==app.Resources["CanvasHiddenSurface"]&&billboard.BorderBrush==app.Resources["CanvasHiddenBorder"]&&Descendants(billboard).OfType<Shapes.Path>().Single().Stroke==app.Resources["CanvasHiddenText"]&&System.Text.Json.JsonSerializer.Serialize(editor.Parameters)==parametersBefore);
            var placementMenu=Field<Controls.ContextMenu>(session,"_toolbarMenu");
            placementMenu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));
            var modeMenu=placementMenu.Items.OfType<Controls.MenuItem>().SelectMany(item=>item.Items.OfType<Controls.MenuItem>()).Single(item=>Equals(item.Tag,"billboard"));
            check("main menu reflects the global billboard toggle and its actual artwork",modeMenu.IsEnabled&&modeMenu.IsChecked&&Descendants((Wpf.DependencyObject)modeMenu.Icon).OfType<Shapes.Path>().Single().Data.ToString()==Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString());
            check("placement menu and toolbar share their tool name, current mode and complete usage hint",Equals(modeMenu.Header,L.Get("Billboard.Toggle"))&&Equals(modeMenu.ToolTip,billboard.ToolTip)&&Controls.ToolTipService.GetShowOnDisabled(modeMenu));
            var import=buttons.Single(button=>Equals(button.Tag,"import"));
            check("image import uses the shared full-width menu artwork",Descendants(import).OfType<Shapes.Path>().Single().Data.ToString()==Media.Geometry.Parse(CommandGlyphs.Image).ToString()&&Media.Geometry.Parse(CommandGlyphs.Image).Bounds.Width>=14);
            check("placement toolbar menu and adjacent icons share the standard stroke weight",
                Descendants(billboard).OfType<Shapes.Path>().Single().StrokeThickness==1.5&&
                Descendants((Wpf.DependencyObject)modeMenu.Icon).OfType<Shapes.Path>().Single().StrokeThickness==1.5&&
                Descendants(import).OfType<Shapes.Path>().Single().StrokeThickness==1.5);
            var plane=Media.PathGeometry.CreateFromGeometry(Media.Geometry.Parse(CommandGlyphs.Surface));
            var placementPen=new Media.Pen(Media.Brushes.Black,1.5) {StartLineCap=Media.PenLineCap.Round,EndLineCap=Media.PenLineCap.Round,LineJoin=Media.PenLineJoin.Round};
            check("placement artwork fits the icon canvas including strokes",new Wpf.Rect(0,0,16,16).Contains(plane.GetRenderBounds(placementPen))&&
                new Wpf.Rect(0,0,16,16).Contains(Media.Geometry.Parse(CommandGlyphs.Billboard).GetRenderBounds(placementPen)));
            editor.Session.Document.Canvas.Projection.Enabled=false;Invoke(session,"RefreshToolbarState");Measure(shell);
            check("disabling perspective restores the surface artwork and leaves Canvas unselected",!editor.BillboardMode&&!billboard.IsEnabled&&Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString()==surfaceArtwork&&billboard.Background==app.Resources["CanvasButton"]&&canvasAction.Background==app.Resources["CanvasSurface"]&&canvasAction.BorderBrush==Media.Brushes.Transparent);
            canvasAction.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));Measure(shell);
            check("Canvas joins the mutually exclusive selected tool frames without opening settings on first activation",editor.Tool==EditorTool.Canvas&&canvasAction.BorderBrush==app.Resources["CanvasAccent"]&&tools[EditorTool.Freehand].BorderBrush==Media.Brushes.Transparent&&Field<CanvasToolOptions?>(session,"_canvasToolOptions") is null);
            Invoke(session,"ActivateTool",EditorTool.Freehand);Measure(shell);
            check("returning to Freehand restores its saved style and releases the Canvas frame",editor.Tool==EditorTool.Freehand&&tools[EditorTool.Freehand].BorderBrush==app.Resources["CanvasAccent"]&&canvasAction.BorderBrush==Media.Brushes.Transparent);
            check("upper twelve tool order matches the requested Select Eraser then ten drawing entries",buttons.Where(button=>Controls.Grid.GetColumn(button)<13).OrderBy(Controls.Grid.GetColumn).Select(button=>button.Tag?.ToString()).SequenceEqual(new[]{"tool:Select","tool:Eraser","tool:Freehand","tool:Highlighter","tool:Line","tool:Rectangle","tool:Ellipse","tool:Text","tool:Number","tool:Symbol","tool:Emoji","more"}));
            check("line and circle toolbar buttons expose their renamed labels and unchanged shortcut hints",System.Windows.Automation.AutomationProperties.GetName(tools[EditorTool.Line])=="直线"&&
                System.Windows.Automation.AutomationProperties.GetName(tools[EditorTool.Ellipse])=="圆形"&&
                System.Windows.Automation.AutomationProperties.GetAcceleratorKey(tools[EditorTool.Line])=="L"&&
                System.Windows.Automation.AutomationProperties.GetAcceleratorKey(tools[EditorTool.Ellipse])=="O");
            var opacity=Field<Controls.Slider>(session,"_opacitySlider");
            check("opacity occupies exactly two tool columns and added actions remain directly available",Controls.Grid.GetColumn(opacity)==28&&Controls.Grid.GetColumnSpan(opacity)==2&&opacity.Width+opacity.Margin.Left+opacity.Margin.Right==60&&buttons.Any(button=>Equals(button.Tag,"clear"))&&buttons.Any(button=>Equals(button.Tag,"visibility")));
            check("trailing actions follow canvas and placement, images, inspector/layers, then independent new and clear controls",buttons.Where(button=>Controls.Grid.GetColumn(button)>=14).OrderBy(Controls.Grid.GetColumn).Select(button=>button.Tag?.ToString()).SequenceEqual(new[]{"canvas","billboard","import","capture","crop","properties","layers","undo","redo","new-canvas","clear","visibility","done"}));
            var more=buttons.Single(button=>Equals(button.Tag,"more"));
            string MoreGlyph() => Fingerprint(Raster((Wpf.FrameworkElement)Glyph(more),1));
            string idleMoreGlyph = MoreGlyph();
            editor.Tool = EditorTool.Blur; Invoke(session,"RefreshToolbarState"); Measure(shell);
            string blurMoreGlyph = MoreGlyph();
            check("More replaces its closed tray icon with the selected blur preview", blurMoreGlyph != idleMoreGlyph);
            editor.Tool = EditorTool.Pixelate; Invoke(session,"RefreshToolbarState"); Measure(shell);
            check("More distinguishes mosaic from blur", MoreGlyph() != idleMoreGlyph && MoreGlyph() != blurMoreGlyph);
            editor.Tool = EditorTool.Freehand; Invoke(session,"RefreshToolbarState"); Measure(shell);
            check("More restores its closed tray icon while another tool is selected", MoreGlyph() == idleMoreGlyph);
            check("idle More identifies its menu shortcut without unrelated options instructions",more.IsEnabled&&ToolShortcutCatalog.IsReserved(0x4D)&&!ToolShortcutCatalog.TryGetTool(0x4D,out _)&&more.ToolTip?.ToString()==L.Get("Toolbar.MoreExpand")+" · M");
            var tooltipParameters=editor.Parameters;
            editor.Tool=EditorTool.Freehand;Invoke(session,"RefreshToolbarState");
            string canvasTooltip=L.Get("Canvas.Title")+" · C";
            check("unselected canvas tooltip contains only its name and shortcut",Equals(tools[EditorTool.Canvas].ToolTip,canvasTooltip));
            editor.Tool=EditorTool.Canvas;Invoke(session,"RefreshToolbarState");
            check("selected canvas adds one panel instruction line",Equals(tools[EditorTool.Canvas].ToolTip,canvasTooltip+"\n"+L.Format("Toolbar.ToolOptionsWithKeyHint","F4","C")));
            check("switching away removes the previous tool's extra instruction",Equals(tools[EditorTool.Freehand].ToolTip,L.Get("Preset.Freehand")+" · B"));
            editor.Tool=EditorTool.Blur;Invoke(session,"RefreshToolbarState");
            string moreTooltip=L.Get("Toolbar.MoreExpand")+" · M";
            check("selected blur keeps the More expansion command tooltip",Equals(more.ToolTip,moreTooltip));
            editor.Tool=EditorTool.Pixelate;Invoke(session,"RefreshToolbarState");
            check("selected mosaic shares More's expansion command",Equals(more.ToolTip,moreTooltip));
            editor.Tool=EditorTool.CaptureRegion;Invoke(session,"RefreshToolbarState");
            check("selected capture has exactly its name and panel instruction",Equals(tools[EditorTool.CaptureRegion].ToolTip,L.Get("Acceptance2.Toolbar.Capture")+" · P\n"+L.Format("Toolbar.ToolOptionsWithKeyHint","F4","P")));
            editor.Tool=EditorTool.Select;Invoke(session,"RefreshToolbarState");
            check("selected Select explains selection instead of an unsupported options shortcut",Equals(tools[EditorTool.Select].ToolTip,L.Get("ToolbarV2.Select")+" · V\n"+L.Get("Toolbar.SelectionHint")));
            check("unselected capture drops the former always-visible description",Equals(tools[EditorTool.CaptureRegion].ToolTip,L.Get("Acceptance2.Toolbar.Capture")+" · P"));
            editor.ApplyParameters(tooltipParameters);Invoke(session,"RefreshToolbarState");
            check("stamp commands use exact F8 and Ctrl+Shift+K bindings",EditorCommandCatalog.Resolve(0x77,FloatCanvas.Windows.OverlayModifiers.None)?.Id=="Stamp.Library" && EditorCommandCatalog.Resolve(0x4B,FloatCanvas.Windows.OverlayModifiers.Control|FloatCanvas.Windows.OverlayModifiers.Shift)?.Id=="Stamp.Create" && EditorCommandCatalog.Resolve(0xBB,FloatCanvas.Windows.OverlayModifiers.None) is null);
            check("toolbar controls retain keyboard input without focus frames or focus-only tooltips",allButtons.All(button=>button.FocusVisualStyle is null&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(button)==false)&&opacity.FocusVisualStyle is null&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(opacity)==false&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(Field<ToolbarLauncher>(session,"_toolbarLauncher"))==false);
            var clearAction=buttons.Single(button=>Equals(button.Tag,"clear"));
            check("clear broom uses danger red beside the gray new-canvas action",clearAction.Background==app.Resources["CanvasDanger"]&&buttons.Single(button=>Equals(button.Tag,"new-canvas")).Background==app.Resources["CanvasButton"]&&Controls.Grid.GetColumn(clearAction)==Controls.Grid.GetColumn(buttons.Single(button=>Equals(button.Tag,"new-canvas")))+1);
            var slotButtons=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
            for(int index=0;index<10;index++)
            {
                var top=buttons.Single(button=>Controls.Grid.GetColumn(button)==index+3);
                double topCenter=top.TranslatePoint(new Wpf.Point(top.ActualWidth/2,0),grid).X;
                double slotCenter=slotButtons[index].TranslatePoint(new Wpf.Point(slotButtons[index].ActualWidth/2,0),grid).X;
                check("production column "+(index+1)+" has one shared upper/lower center",Near(topCenter,slotCenter));
            }
            Measure(shell);
            foreach(var button in buttons)
            {
                string name=button.Tag?.ToString() ?? "button";
                check(name+" is a 26 DIP square inside its 30 DIP column",Near(button.ActualWidth,26) && Near(button.ActualHeight,26) && button.Margin==new Wpf.Thickness(2,0,2,0));
                if(button.Content is not Wpf.FrameworkElement content)throw new InvalidOperationException(name+" has no visual glyph.");
                var glyph=content is Controls.Grid hintGrid?hintGrid.Children.OfType<Controls.ContentPresenter>().Single():content;
                var bound=Media.VisualTreeHelper.GetDescendantBounds(glyph);
                if(bound.IsEmpty)bound=new Wpf.Rect(content.RenderSize);
                var actual=glyph.TransformToAncestor(button).TransformBounds(bound);
                if(Environment.GetEnvironmentVariable("FLOATCANVAS_ICON_AUDIT") == "1" && name.StartsWith("tool:"))
                    Console.WriteLine($"HOST {name}|{button.ActualWidth}|{button.ActualHeight}|{actual.X}|{actual.Y}|{actual.Width}|{actual.Height}");

                check(name+" glyph fits without clipping",actual.Left>=-.05 && actual.Top>=-.05 && actual.Right<=button.ActualWidth+.05 && actual.Bottom<=button.ActualHeight+.05);
            }
            check("straight-line entry now presents the actual current style sample",Glyph(tools[EditorTool.Line]) is Controls.Image {Source:Imaging.BitmapSource,Width:16,Height:16});
            var done=buttons.Single(button=>Equals(button.Tag,"done"));
            var mark=Vertices(Descendants(done).OfType<Shapes.Path>().Single().Data);
            check("done entry is a check mark rather than the pass-through cursor",mark.SequenceEqual(new[]{new Wpf.Point(2,8),new Wpf.Point(6,12),new Wpf.Point(14,3)}));
            check("save-stamp and stamp-management are independent lower-row entries",allButtons.Any(button=>Equals(button.Tag,"save-stamp")) && allButtons.Any(button=>Equals(button.Tag,"manage-stamps")));
            var imageMenu=more.ContextMenu;
            check("More retains blur pixelate and both demonstration tools in order",ReferenceEquals(tools[EditorTool.Blur],more)&&imageMenu is null && extraButtons.Keys.SequenceEqual(new[]{EditorTool.Blur,EditorTool.Pixelate,EditorTool.Presentation,EditorTool.PointerPresentation}));
            var cropButton=buttons.Single(button=>Equals(button.Tag,"crop"));
            check("disabled unselected crop retains only its tool name",Controls.ToolTipService.GetShowOnDisabled(cropButton) && Equals(cropButton.ToolTip,L.Get("Tool.Crop")));
            var cropMenus=Field<List<Controls.MenuItem>>(session,"_cropMenuItems");
            check("image crop follows import and capture and is disabled consistently without a target",Controls.Grid.GetColumn(cropButton)==Controls.Grid.GetColumn(buttons.Single(button=>Equals(button.Tag,"capture")))+1 &&
                ReferenceEquals(tools[EditorTool.Crop],cropButton) && !cropButton.IsEnabled && cropMenus.Count>0 && cropMenus.All(item=>!item.IsEnabled));
            // Crop eligibility also queues an asynchronous preview. Supply a real
            // resource so that queued rendering never raises a missing-image dialog.
            using var cropResource=Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.PassThrough.png",UriKind.Relative))!.Stream;
            using var cropBytes=new MemoryStream();cropResource.CopyTo(cropBytes);
            var cropTarget=new CanvasElement {Kind=ElementKind.Image,Width=100,Height=80,AssetId=Field<AssetLibrary>(session,"_assets").Add(cropBytes.ToArray())};
            editor.Session.Document.Elements.Add(cropTarget);editor.Session.SetSelection([cropTarget.Id]);Invoke(session,"RefreshToolbarState");
            check("selecting one editable image enables the crop button and all menu entries",cropButton.IsEnabled && cropMenus.All(item=>item.IsEnabled));
            Invoke(session,"ActivateTool",EditorTool.Crop);
            check("the dedicated crop action activates its own tool family",editor.Tool==EditorTool.Crop);
            check("selected crop adds its available panel shortcut",Equals(cropButton.ToolTip,L.Get("Tool.Crop")+"\n"+L.Format("Toolbar.ToolOptionsHint","F4")));
            cropTarget.Locked=true;editor.Session.NotifyChanged();Invoke(session,"RefreshToolbarState");
            check("locking the selected image disables all crop entries and exits the crop tool",!cropButton.IsEnabled && cropMenus.All(item=>!item.IsEnabled) && editor.Tool==EditorTool.Select);
            Invoke(session,"ActivateTool",EditorTool.Crop);
            check("an inactive crop menu or keyboard activation cannot bypass eligibility",editor.Tool==EditorTool.Select);
            editor.Session.ReplaceDocument(new SceneDocument());editor.Tool=EditorTool.Freehand;Invoke(session,"RefreshToolbarState");
            var manage=allButtons.Single(button=>Equals(button.Tag,"manage-stamps"));var saveStamp=allButtons.Single(button=>Equals(button.Tag,"save-stamp"));
            check("idle stamp-library launcher shares New Stamp's normal gray button background",((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasButton"]).Color);
            var newStampVertices=Vertices(Descendants(saveStamp).OfType<Shapes.Path>().Single().Data);
            check("stamp buttons expose current command bindings while New Stamp presents an explicit plus",manage.ToolTip?.ToString()==L.Get("Stamp.LibraryTitle")+" · F8"&&saveStamp.ToolTip?.ToString()==L.Get("Feedback3.Toolbar.NewStamp")+" · Ctrl+Shift+K"&&newStampVertices.Contains(new Wpf.Point(12.5,2.5))&&newStampVertices.Contains(new Wpf.Point(12.5,7.5))&&newStampVertices.Contains(new Wpf.Point(10,5))&&newStampVertices.Contains(new Wpf.Point(15,5)));

            var parameters=editor.Parameters; parameters.Tool=EditorTool.Symbol; parameters.Symbol=SymbolKind.Heart;parameters.Style.Stroke=0xFFDC4F78;
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var firstSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);string heart=Fingerprint((Imaging.BitmapSource)firstSymbol.Source);
            parameters=editor.Parameters;parameters.Symbol=SymbolKind.Hexagon;parameters.Style.Stroke=0xFF168F69;
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var nextSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);
            check("production symbol button replaces its shape and current paint color",heart!=Fingerprint((Imaging.BitmapSource)nextSymbol.Source) && ContainsColor((Imaging.BitmapSource)nextSymbol.Source,Media.Color.FromRgb(0x16,0x8F,0x69)));
            var oldEmoji=((Controls.Image)Glyph(tools[EditorTool.Emoji])).Source;
            parameters=editor.Parameters;parameters.Tool=EditorTool.Emoji;parameters.Emoji="🔥";
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var nextEmoji=(Controls.Image)Glyph(tools[EditorTool.Emoji]);
            check("production emoji button replaces the actual image with the chosen emoji",!ReferenceEquals(oldEmoji,nextEmoji.Source) && ReferenceEquals(nextEmoji.Source,EmojiArtwork.Image("🔥")) && nextEmoji.Width==16 && nextEmoji.Height==16);
            string configuredSymbol=Fingerprint((Imaging.BitmapSource)nextSymbol.Source);
            Invoke(session,"ActivateTool",EditorTool.Freehand);Measure(shell);
            var rememberedSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);
            check("returning to Freehand preserves the most recent symbol shape and paint",Fingerprint((Imaging.BitmapSource)rememberedSymbol.Source)==configuredSymbol && ContainsColor((Imaging.BitmapSource)rememberedSymbol.Source,Media.Color.FromRgb(0x16,0x8F,0x69)));
            check("returning to Freehand preserves the most recent emoji image",ReferenceEquals(((Controls.Image)Glyph(tools[EditorTool.Emoji])).Source,EmojiArtwork.Image("🔥")));
            var hints=Field<List<Controls.TextBlock>>(session,"_toolbarShortcutHintLabels");
            check("single-letter tools share corner legends; longer stamp bindings remain in tooltips",hints.Count==17&&hints.Take(15).Select(hint=>hint.Text).Order().SequenceEqual(ToolShortcutCatalog.All.Select(binding=>binding.Hint).Order())&&hints.Skip(15).Select(hint=>hint.Text).SequenceEqual(new[]{"",""}));
            foreach(var button in buttons.Where(button=>button.Content is Controls.Grid content&&content.Children.OfType<Controls.TextBlock>().Any()))
            {
                var hint=((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single();
                var origin=hint.TranslatePoint(new Wpf.Point(),button);
                check(button.Tag+" legend is centered on the right edge and top-aligned",Near(origin.X+hint.ActualWidth/2,button.ActualWidth)&&Near(origin.Y,0));
            }
            foreach(var button in slotButtons.Concat(new[]{manage,saveStamp}))
            {
                var hint=((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single();
                var origin=hint.TranslatePoint(new Wpf.Point(),button);
                // Slot content is centered in the wider 30-DIP hit cell; its visible frame is 26 DIP.
                double left=slotButtons.Contains(button)?2:0;
                check("lower "+hint.Text+" legend is centered on its left frame edge and bottom-aligned",Near(origin.X+hint.ActualWidth/2,left)&&Near(origin.Y+hint.ActualHeight,26));
            }
            var settings=Field<UserSettings>(session,"_settings");settings.ShowToolShortcutHints=false;Invoke(session,"RefreshToolShortcutHints");
            shelf.Refresh();shelf.PrepareForShow();Measure(shell);
            check("hiding shortcut legends also hides lower digits after a shelf rebuild",hints.All(hint=>hint.Visibility==Wpf.Visibility.Collapsed)&&((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single().Visibility==Wpf.Visibility.Collapsed)&&tools[EditorTool.Number]!=tools[EditorTool.Text]);
            settings.ShowToolShortcutHints=true;Invoke(session,"RefreshToolShortcutHints");
            shelf.PrepareForShow();
            check("restoring shortcut legends also restores every current slot digit",((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single().Visibility==Wpf.Visibility.Visible));
            check("unselected tools have no idle outline",tools.Where(pair=>pair.Key!=EditorTool.Freehand).All(pair=>pair.Value.BorderBrush is Media.SolidColorBrush brush&&brush.Color.A==0));
            Invoke(session,"SaveStampSelection");
            check("save-stamp with no selection explains the requirement without creating an unseen dialog",status.Text==L.Get("Stamp.NoSelection") && Field<StampLibraryWindow?>(session,"_stampWindow") is null);

            string longStatus=string.Concat(Enumerable.Repeat("这是用于验收省略与悬停滚动的长状态 · long toolbar status / ",8));
            Invoke(session,"UpdateStatus",longStatus);display.Refresh();Measure(shell);
            check("LCD keeps long content while trimming its extended layout",status.Text==longStatus && status.TextTrimming==Wpf.TextTrimming.CharacterEllipsis && double.IsNaN(status.Width) && Near(display.ActualWidth,476) && display.ClipToBounds);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice,Environment.TickCount) {RoutedEvent=Input.Mouse.MouseEnterEvent});
            var scroll=(Media.TranslateTransform)status.RenderTransform;
            check("LCD's actual hover handler begins a clipped scrolling animation",scroll.HasAnimatedProperties && status.TextTrimming==Wpf.TextTrimming.None && double.IsFinite(status.Width) && status.Width>display.ActualWidth);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice,Environment.TickCount) {RoutedEvent=Input.Mouse.MouseLeaveEvent});
            check("LCD's actual leave handler cancels animation and restores ellipsis",!scroll.HasAnimatedProperties && scroll.X==0 && status.TextTrimming==Wpf.TextTrimming.CharacterEllipsis && double.IsNaN(status.Width));

            Set(session,"_selectedStampId",(Guid?)saved.second.Id);Set(session,"_selectedStampSlot",1);Set(session,"_selectedStampRevision",saved.second.Revision);Set(session,"_selectedStamp",saved.loaded);
            editor.Tool=EditorTool.Stamp;Invoke(session,"RefreshToolbarState");
            check("a bound selected stamp retains stamp mode",editor.Tool==EditorTool.Stamp);
            check("slot selection highlights its slot and leaves the library launcher unselected",Field<int?>(shelf,"activeSlot")==1 && Wpf.Automation.AutomationProperties.GetItemStatus(manage)=="");
            Set(session,"_selectedStampSlot",null);Invoke(session,"RefreshToolbarState");PumpToIdle(app.Dispatcher);
            check("library selection highlights its launcher and clears every slot highlight",Field<int?>(shelf,"activeSlot") is null && Wpf.Automation.AutomationProperties.GetItemStatus(manage)==L.Get("ToolbarV2.Selected") && manage.ToolTip is StampPreviewTooltip);
            check("selected library launcher replaces its glyph with a static image",Glyph(manage) is Controls.Image {Source:Imaging.BitmapSource {IsFrozen:true}});
            foreach(string theme in new[]{"light","dark"})
            {
                toolbar.Content=shell;AppTheme.Apply(theme);PumpToIdle(app.Dispatcher);toolbar.Content=null;PrepareShelfPreview(shell,shelf);Measure(shell);
                check(theme+" active library launcher uses the same transparent preview and blue frame as a stamp slot",((Media.SolidColorBrush)manage.Background).Color.A==0 && ((Media.SolidColorBrush)manage.BorderBrush).Color==((Media.SolidColorBrush)app.Resources["CanvasAccent"]).Color && Glyph(manage) is Controls.Image {Width:20,Height:20});
                check(theme+" active library launcher retains the common action-button hover and press template",ReferenceEquals(manage.Template,saveStamp.Template));
                Save(shell,Path.Combine(output,"stamp-library-active-"+theme+".png"),1.5);
            }
            Set(session,"_selectedStampSlot",1);Invoke(session,"RefreshToolbarState");
            check("leaving the library stamp restores its normal trigger glyph and gray background",ReferenceEquals(Glyph(manage),Field<object>(session,"_stampLibraryDefaultGlyph")) && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.BorderBrush).Color.A==0 && manage.ToolTip?.ToString()==L.Get("Stamp.LibraryTitle")+" · F8");
            Task.Run(()=>library.PinAsync(1,null)).GetAwaiter().GetResult();PumpToIdle(app.Dispatcher);
            check("unbinding the active last slot restores Select through the library notification",editor.Tool==EditorTool.Select && Field<Guid?>(session,"_selectedStampId") is null);

            parameters=editor.Parameters;parameters.Tool=EditorTool.Symbol;parameters.Symbol=SymbolKind.Heart;parameters.Style.Stroke=0xFF2383E2;parameters.Emoji="😀";
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Invoke(session,"UpdateStatus","示例 · 心形 / 自定义印章");
            shelf.Refresh();shelf.PrepareForShow();PumpToIdle(app.Dispatcher);
            foreach(string theme in new[]{"light","dark"})
            {
                // Application resource changes traverse registered Window trees. Keep
                // the production owner connection while changing theme, then detach
                // only for rendering without ever showing its native window.
                toolbar.Content=shell;
                AppTheme.Apply(theme);PumpToIdle(app.Dispatcher);
                toolbar.Content=null;PrepareShelfPreview(shell,shelf);Measure(shell);
                var expected=((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color;
                check(theme+" idle library launcher follows the normal button background after theme changes",((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasButton"]).Color);
                check(theme+" production body resolves the actual current theme surface",((Media.SolidColorBrush)body.Background).Color==expected);
                check(theme+" LCD follows its dedicated surface and readable text theme",((Media.SolidColorBrush)display.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasLcdSurface"]).Color&&((Media.SolidColorBrush)status.Foreground).Color==((Media.SolidColorBrush)app.Resources["CanvasLcdText"]).Color);
                RenderVisibilityStates(session,host,shell,theme,output);
                RenderPlacementStates(check,session,editor,billboard,import,placementMenu,modeMenu,shell,theme,output);
                var bitmap=Save(shell,Path.Combine(output,"production-toolbar-"+theme+".png"),1.5);
                var upper=Field<ToolbarUpperRow>(session,"_toolbarUpperRow");
                var create=Field<NewCanvasButton>(session,"_newCanvasButton");
                upper.SetExpanded(true); fileBubble.Refresh(@"C:\图标验收\一个用来检查截断及上方整排同时显示的非常长的工程名称.floatcanvas",true,true);
                typeof(NewCanvasButton).GetProperty(nameof(NewCanvasButton.IsConfirming),Private|BindingFlags.Public)!.SetValue(create,true);
                typeof(NewCanvasButton).GetMethod("Refresh",Private)!.Invoke(create,null);Measure(shell);
                check(theme+" filename and confirmation retain their offsets while the extension reserves headroom",Near(shell.ActualHeight,body.ActualHeight+ToolbarLauncher.BodyTop+ToolbarUpperRow.Headroom+3)&&fileBubble.ActualWidth<=Field<RootCanvasToolbarHeader>(session,"_rootCanvasHeader").MaxWidth&&upper.ActualHeight==ToolbarUpperRow.ExtensionHeight&&!create.ConfirmationPopup.IsOpen);
                foreach(double scale in new[]{1d,1.5,2d}) Save(shell,Path.Combine(output,$"upper-row-{theme}-{scale*100:0}.png"),scale);
                CheckUpperRowOverlap(check,shell,upper,fileBubble,theme,output);
                create.CancelConfirmation(); fileBubble.Refresh(null,false,true); upper.SetExpanded(false);Measure(shell);
                var legend=hints.Single(value=>value.Text=="R");
                check(theme+" small shortcut lettering has strong contrast against its masking surface",Contrast(((Media.SolidColorBrush)legend.Foreground).Color,expected)>=7);
                foreach(double scale in new[]{1d,1.5,2d})
                {
                    var hint=hints.Single(value=>value.Text=="R");
                    int expectedInk=ReferenceInk(hint,scale);
                    int actualInk=LegendInk(shell,hint,scale);
                    Console.WriteLine($"LEGEND {theme} {scale:P0}: actual={actualInk}, reference={expectedInk}");
                    check($"{theme} {scale:P0} rectangle legend retains its complete rendered letter",expectedInk>5&&actualInk>=expectedInk*.8);
                    check($"{theme} {scale:P0} upper shortcut mask never erases its neighboring selected frame",NeighborUnchanged(shell,hint,tools[EditorTool.Ellipse],Controls.Control.BorderBrushProperty,true,scale));
                    var currentSlots=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
                    var slotGrid=(Controls.Grid)currentSlots[1].Content;
                    var priorFrame=((Controls.Grid)currentSlots[0].Content).Children.OfType<Controls.Border>().Single();
                    check($"{theme} {scale:P0} lower shortcut mask never erases its previous slot frame",NeighborUnchanged(shell,slotGrid.Children.OfType<Controls.TextBlock>().Single(),priorFrame,Controls.Border.BorderBrushProperty,false,scale));
                }
                var sample=body.TranslatePoint(new Wpf.Point(body.ActualWidth-3,body.ActualHeight/2),shell);
                var actual=ColorAt(bitmap,(int)(sample.X*1.5),(int)(sample.Y*1.5));
                check(theme+" full production preview paints that surface in actual pixels",Math.Abs(actual.R-expected.R)<=2 && Math.Abs(actual.G-expected.G)<=2 && Math.Abs(actual.B-expected.B)<=2 && actual.A==255);
            }
            foreach(double scale in new[]{1d,1.25,1.5,2d})CheckNativeDpiLegends(check,shell,hints,tools,shelf,output,scale);
            // Visibility-state previews vary the logical IsVisible flag. The native
            // display/input visibility is the isolation guarantee for this fixture.
            check("full-toolbar fixture never enters editing or shows canvas/input/tool windows",!host.IsEditing && host.Diagnostics.All(pair=>!pair.InputVisible && !pair.DisplayVisible) && app.Windows.Cast<Wpf.Window>().All(window=>!window.IsVisible));
        }
        finally
        {
            if(toolbar is not null && shell is not null)toolbar.Content=shell;
            session?.Dispose();if(session is null)host?.Dispose();
            SynchronizationContext.SetSynchronizationContext(context);
            app.Shutdown();
        }
        check("full-toolbar fixture destroys every native monitor/control HWND",handles.All(handle=>!IsWindow(handle)));
        Console.WriteLine("Full production toolbar evidence: "+output);
    }

    private static bool Near(double a,double b)=>Math.Abs(a-b)<.05;
    private static void CheckPresentationMenuPreviews(Action<string,bool> check,DesktopSession session,Dictionary<EditorTool,Controls.Button> buttons)
    {
        var memory=Field<Dictionary<EditorTool,ToolParameters>>(session,"_toolbarToolMemory");
        foreach(var tool in new[]{EditorTool.Presentation,EditorTool.PointerPresentation})
        {
            var entries=buttons.Where(pair=>pair.Key==tool).Select(pair=>pair.Value).ToArray();
            check("more menu contains one "+tool+" preview",entries.Length==1);
            bool hadPrevious=memory.TryGetValue(tool,out var previous);
            try
            {
                var parameters=previous?.Clone()??new ToolParameters {Tool=tool};
                parameters.Style.Opacity=1;parameters.Style.Background=0;
                parameters.PointerPresentation=new() {CursorEnabled=true,ClicksEnabled=false,TrailEnabled=false,CursorColor=0xFF168F69};
                parameters.Style.Foreground=0xFF168F69;memory[tool]=parameters;
                Invoke(session,"RefreshMoreToolPreviews");
                var icon=(Wpf.FrameworkElement)entries[0].Content;Measure(icon);var green=Raster(icon,4);
                parameters=parameters.Clone();parameters.Style.Foreground=0xFF8B4CCF;parameters.PointerPresentation!.CursorColor=0xFF8B4CCF;memory[tool]=parameters;
                Invoke(session,"RefreshMoreToolPreviews");
                icon=(Wpf.FrameworkElement)entries[0].Content;Measure(icon);var purple=Raster(icon,4);
                check(tool+" menu preview follows its remembered color and refreshes when that color changes",
                    ContainsColor(green,Media.Color.FromRgb(0x16,0x8F,0x69))&&ContainsColor(purple,Media.Color.FromRgb(0x8B,0x4C,0xCF))&&Fingerprint(green)!=Fingerprint(purple));
            }
            finally
            {
                if(hadPrevious)memory[tool]=previous!;else memory.Remove(tool);
                Invoke(session,"RefreshMoreToolPreviews");
            }
        }
    }
    private static void CheckMorePreviewLifecycle(Action<string,bool> check,DesktopSession session,EditorController editor,
        Dictionary<EditorTool,Controls.Button> buttons,Wpf.FrameworkElement shell)
    {
        var previous=editor.Parameters;
        var row=Field<ToolbarUpperRow>(session,"_toolbarUpperRow");
        var main=Field<Dictionary<EditorTool,Controls.Button>>(session,"_toolbarToolButtons");
        try
        {
            row.SetExpanded(true); Measure(shell);
            check("expanded tools are toolbar-owned buttons with no separate popup or assigned shortcut",buttons.Values.All(button=>
                Descendants(shell).Contains(button)&&button.ContextMenu is null&&string.IsNullOrEmpty(Wpf.Automation.AutomationProperties.GetAcceleratorKey(button))));
            check("expanded row fits the enlarged mascot without changing window height",Near(shell.ActualHeight,ToolbarLauncher.ArtworkSize+1+ToolbarUpperRow.Headroom)&&row.TranslatePoint(new(0,0),shell).Y>=3&&Field<ToolbarLauncher>(session,"_toolbarLauncher").Height==101);
            var strip = row.Children.OfType<Controls.Border>().Single(border => border.Child is Controls.Grid);
            var first = buttons.Values.First();
            double inset = first.TranslatePoint(new Wpf.Point(), strip).Y;
            var body = Field<Controls.Border>(session,"_toolbarExpandedContent");
            check("extra row retains its top inset and adds bottom rounding clearance",
                Near(inset-strip.BorderThickness.Top,2) && Near(strip.ActualHeight-inset-first.ActualHeight-strip.BorderThickness.Bottom,3) &&
                Near(strip.TranslatePoint(new Wpf.Point(0,strip.ActualHeight),shell).Y+ToolbarUpperRow.SurfaceGap,body.TranslatePoint(new Wpf.Point(),shell).Y));
            check("first extra tool aligns with More while later tools keep their own grouped spacing",Near(first.TranslatePoint(new Wpf.Point(),shell).X,main[EditorTool.Blur].TranslatePoint(new Wpf.Point(),shell).X)&&Near(buttons[EditorTool.Pixelate].TranslatePoint(new Wpf.Point(),shell).X-first.TranslatePoint(new Wpf.Point(),shell).X,30));
            var last=buttons.Values.Last();
            check("extension has four tools without a close button and equal clearance on all edges",((Controls.Grid)strip.Child).Children.OfType<Controls.Button>().Count()==4&&
                Near(first.TranslatePoint(new(),strip).X-strip.BorderThickness.Left,2)&&Near(strip.ActualWidth-last.TranslatePoint(new Wpf.Point(last.ActualWidth,0),strip).X-strip.BorderThickness.Right,2));
            var done=Descendants(body).OfType<Controls.Button>().Single(button=>Equals(button.Tag,"done"));
            check("main toolbar right inset equals the two-DIP vertical inset and outer corners are concentric",Near(body.ActualWidth-done.TranslatePoint(new Wpf.Point(done.ActualWidth,0),body).X-body.BorderThickness.Right,2)&&body.CornerRadius==new Wpf.CornerRadius(6));
            var next=previous.Clone();next.Tool=EditorTool.Blur;next.EffectStrength=5;
            editor.ApplyParameters(next);Invoke(session,"RefreshToolbarState");
            var icon=(Wpf.FrameworkElement)buttons[EditorTool.Blur].Content;Measure(icon);string before=Fingerprint(Raster(icon,1));
            next=editor.Parameters;next.EffectStrength=31;editor.ApplyParameters(next);Invoke(session,"RefreshToolbarState");
            icon=(Wpf.FrameworkElement)buttons[EditorTool.Blur].Content;Measure(icon);
            check("expanded tool keeps its live parameter preview without dismissing the row",row.IsExpanded&&Fingerprint(Raster(icon,1))!=before);
            editor.Tool=EditorTool.Freehand;Invoke(session,"RefreshToolbarState");
            check("selecting a main tool retains the expanded row and its orange More state",row.IsExpanded&&main[EditorTool.Blur].BorderBrush==Wpf.Application.Current.Resources["CanvasHiddenBorder"]);
            editor.Tool=EditorTool.Pixelate;Invoke(session,"RefreshToolbarState");row.SetExpanded(false);
            var shown=(Wpf.FrameworkElement)Field<Dictionary<EditorTool,Controls.ContentPresenter>>(session,"_toolbarGlyphHosts")[EditorTool.Blur].Content; Measure(shown); var expected=(Wpf.FrameworkElement)ToolParameterIcon.Create(editor.Parameters); Measure(expected);
            check("explicit collapse keeps the active extra tool and returns its preview to More",editor.Tool==EditorTool.Pixelate&&!row.IsExpanded&&Fingerprint(Raster(shown,2))==Fingerprint(Raster(expected,2)));
            row.SetExpanded(true);Measure(shell);Save(shell,Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_TOOLBAR2_PREVIEW_DIR")??Path.GetTempPath(),"expanded-toolbar.png"),1.5);
        }
        finally {row.SetExpanded(false);editor.ApplyParameters(previous);Invoke(session,"RefreshToolbarState");}
    }
    private static void PrepareShelfPreview(Wpf.FrameworkElement shell,StampShelfControl shelf)
    {
        var parent=(Controls.Panel)shelf.Parent;
        int index=parent.Children.IndexOf(shelf);
        parent.Children.Remove(shelf);
        var root=new Controls.Border {Opacity=0,Child=shelf};
        using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("FloatCanvas toolbar shelf preview")
        {
            Width=1600,Height=180,PositionX=-20000,PositionY=-20000,
            WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x080000A0
        }) {RootVisual=root};
        try
        {
            ShowWindow(source.Handle,4);PumpToIdle(shell.Dispatcher);
            if(!shelf.IsVisible||!shelf.IsLoaded)throw new InvalidOperationException("Shelf preview did not become visible and loaded.");
            shelf.PrepareForShow();
            bool Ready()=>((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>()
                .Where(button=>button.ContextMenu is not null)
                .All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.Image>().Single().Source is Imaging.BitmapSource);
            var deadline=DateTime.UtcNow.AddSeconds(3);
            while(!Ready()&&DateTime.UtcNow<deadline) {PumpToIdle(shell.Dispatcher);Thread.Sleep(5);}
            if(!Ready())throw new InvalidOperationException("Visible shelf thumbnails did not load.");
        }
        finally {root.Child=null;source.RootVisual=null;parent.Children.Insert(index,shelf);PumpToIdle(shell.Dispatcher);}
    }
    private static void CheckCanvasPresentation(Action<string,bool> check,DesktopSession session,EditorController editor,
        Wpf.Window toolbar,Wpf.FrameworkElement shell,Controls.Button canvas,Controls.Button placement,
        Controls.Button import,Controls.Button capture,string output)
    {
        var app=Wpf.Application.Current;
        var originalTool=editor.Tool;bool originalProjection=editor.Session.Document.Canvas.Projection.Enabled;
        var findings=new List<(string Name,bool Passed)>();
        try
        {
            foreach(string theme in new[]{"light","dark"})
            {
                toolbar.Content=shell;AppTheme.Apply(theme);PumpToIdle(app.Dispatcher);toolbar.Content=null;
                var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal};
                row.SetResourceReference(Controls.Panel.BackgroundProperty,"CanvasSurface");
                foreach(bool perspective in new[]{false,true})foreach(bool selected in new[]{false,true})
                {
                    editor.Session.Document.Canvas.Projection.Enabled=perspective;editor.Tool=selected?EditorTool.Canvas:EditorTool.Freehand;
                    Invoke(session,"RefreshToolbarState");Measure(shell);
                    var preview=Descendants(canvas).OfType<CanvasParameterIcon>().Single();
                    string state=$"{theme} {(perspective?"perspective":"flat")} {(selected?"selected":"idle")}";
                    bool Same(Media.Brush actual,string resource)=>actual is Media.SolidColorBrush brush&&brush.Color==((Media.SolidColorBrush)app.Resources[resource]).Color;
                    findings.Add((state+" uses ordinary tool fill and a live canvas preview",
                        Same(canvas.Background,selected?"CanvasHover":"CanvasSurface")&&preview.Width==16&&preview.Height==16));
                    findings.Add((state+" highlights only the selected tool frame",selected?Same(canvas.BorderBrush,"CanvasAccent"):
                        canvas.BorderBrush is Media.SolidColorBrush idle&&idle.Color.A==0));
                    var hint=Descendants(canvas).OfType<Controls.TextBlock>().Single(value=>value.Text=="C");
                    var origin=hint.TranslatePoint(new Wpf.Point(),canvas);
                    findings.Add((state+" anchors C on the upper right frame",Near(origin.X+hint.ActualWidth/2,canvas.ActualWidth)&&Near(origin.Y,0)));
                    var card=new Controls.StackPanel {Margin=new Wpf.Thickness(10),Width=240};
                    var label=new Controls.TextBlock {Text=state,Margin=new Wpf.Thickness(0,0,0,8)};label.SetResourceReference(Controls.TextBlock.ForegroundProperty,"CanvasText");card.Children.Add(label);
                    var icons=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal};
                    foreach(var button in new[]{canvas,placement,import,capture})
                    {
                        // Include the complete overhanging shortcut, while hiding
                        // neighboring controls only for this detached visual capture.
                        var neighbors=Descendants(shell).OfType<Controls.Button>().Where(other=>!ReferenceEquals(other,button)).Select(other=>(Button:other,other.Visibility)).ToArray();
                        foreach(var neighbor in neighbors)neighbor.Button.Visibility=Wpf.Visibility.Hidden;
                        Measure(shell);var point=button.TranslatePoint(new Wpf.Point(),shell);var pixels=Raster(shell,4);
                        foreach(var neighbor in neighbors)neighbor.Button.Visibility=neighbor.Visibility;
                        Measure(shell);
                        var crop=new Imaging.CroppedBitmap(pixels,new Wpf.Int32Rect((int)Math.Round((point.X-2)*4),(int)Math.Round((point.Y-2)*4),136,120));
                        icons.Children.Add(new Controls.Image {Source=crop,Width=57,Height=50,Margin=new Wpf.Thickness(0,0,2,0)});
                    }
                    card.Children.Add(icons);row.Children.Add(card);
                }
                Measure(row);Save(row,Path.Combine(output,"canvas-states-"+theme+".png"),1.5);
            }
            foreach(var button in new[]{canvas,placement,import,capture})
            {
                if(ReferenceEquals(button,canvas))
                {
                    var sample=Descendants(button).OfType<CanvasParameterIcon>().SingleOrDefault();
                    findings.Add(("canvas uses the bounded shared canvas preview",sample is {ActualWidth:16,ActualHeight:16}));
                    continue;
                }
                // Capture has a selection outline plus a separate captured-content cue.
                var path=Descendants(button).OfType<Shapes.Path>().FirstOrDefault();
                if(path is null){findings.Add((button.Tag+" retains the shared command glyph canvas",false));continue;}
                var transform=path.TransformToAncestor(button);
                var delta=transform.Transform(new Wpf.Point(1,0))-transform.Transform(new Wpf.Point());
                var pen=new Media.Pen(path.Stroke,path.StrokeThickness) {StartLineCap=path.StrokeStartLineCap,EndLineCap=path.StrokeEndLineCap,LineJoin=path.StrokeLineJoin};
                var bounds=transform.TransformBounds(path.RenderedGeometry.GetRenderBounds(pen));
                Console.WriteLine($"CANVAS GLYPH {button.Tag}: stroke={delta.Length*path.StrokeThickness:F2} bounds={bounds}");
                double expectedStroke=ReferenceEquals(button,capture)?1.5*16/18:1.5;
                findings.Add((button.Tag+" uses its shared artwork's scaled stroke",Near(delta.Length*path.StrokeThickness,expectedStroke)));
                findings.Add((button.Tag+" has a bounded matching visual size",bounds.Width is >=13 and <=16&&bounds.Height is >=12 and <=16));
            }
        }
        finally
        {
            editor.Tool=originalTool;editor.Session.Document.Canvas.Projection.Enabled=originalProjection;
            toolbar.Content=shell;AppTheme.Apply("light");PumpToIdle(app.Dispatcher);toolbar.Content=null;Invoke(session,"RefreshToolbarState");Measure(shell);
        }
        foreach(var finding in findings)Console.WriteLine($"CANVAS PRESENTATION {(finding.Passed?"PASS":"FAIL")}: {finding.Name}");
        foreach(var finding in findings)check(finding.Name,finding.Passed);
    }

    private static void RenderPlacementStates(Action<string,bool> check,DesktopSession session,EditorController editor,
        Controls.Button placement,Controls.Button import,Controls.ContextMenu menu,Controls.MenuItem modeMenu,
        Wpf.FrameworkElement shell,string theme,string output)
    {
        string language=L.Preference;bool perspective=editor.Session.Document.Canvas.Projection.Enabled,mode=editor.BillboardMode;
        try
        {
            foreach(string locale in new[]{"zh-CN","en"})
            {
                L.Initialize(locale);
                var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal};
                row.SetResourceReference(Controls.Panel.BackgroundProperty,"CanvasSurface");
                foreach(bool billboard in new[]{false,true})
                {
                    editor.Session.Document.Canvas.Projection.Enabled=true;editor.BillboardMode=billboard;
                    menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));Measure(shell);
                    string tooltip=(string)placement.ToolTip;
                    string[] lines=tooltip.Split('\n');
                    check($"{locale} {theme} {billboard} placement hint contains only its title and currently selected mode",
                        lines.Length==2&&lines[0]==L.Get("Billboard.Toggle")&&lines[1]==L.Format("Billboard.CurrentMode",L.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode"))&&Equals(modeMenu.ToolTip,tooltip)&&
                        System.Windows.Automation.AutomationProperties.GetItemStatus(placement)==L.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode"));
                    var card=new Controls.StackPanel {Width=340,Margin=new Wpf.Thickness(14)};
                    var icons=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Margin=new Wpf.Thickness(0,0,0,12)};
                    icons.Children.Add(new Controls.Image {Source=ControlPixels(placement),Width=52,Height=52,Stretch=Media.Stretch.Uniform});
                    icons.Children.Add(new Controls.Image {Source=ControlPixels(import),Width=52,Height=52,Margin=new Wpf.Thickness(14,0,0,0),Stretch=Media.Stretch.Uniform});
                    card.Children.Add(icons);
                    var text=new Controls.TextBlock {Text=tooltip,TextWrapping=Wpf.TextWrapping.Wrap,FontSize=14,LineHeight=22};
                    text.SetResourceReference(Controls.TextBlock.ForegroundProperty,"CanvasText");card.Children.Add(text);row.Children.Add(card);
                }
                Measure(row);Save(row,Path.Combine(output,$"placement-tooltip-{locale}-{theme}.png"),1.5);
            }
        }
        finally
        {
            L.Initialize(language);editor.Session.Document.Canvas.Projection.Enabled=perspective;editor.BillboardMode=mode;
            menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));Measure(shell);
        }
        Imaging.BitmapSource ControlPixels(Controls.Button button)
        {
            var point=button.TranslatePoint(new Wpf.Point(),shell);
            var pixels=Raster(shell,4);
            return new Imaging.CroppedBitmap(pixels,new Wpf.Int32Rect((int)Math.Round(point.X*4),(int)Math.Round(point.Y*4),
                (int)Math.Round(button.ActualWidth*4),(int)Math.Round(button.ActualHeight*4)));
        }
    }
    private static void RenderVisibilityStates(DesktopSession session,OverlayHost host,
        Wpf.FrameworkElement shell,string theme,string output)
    {
        var visibleProperty=typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!;
        var original=host.IsVisible;
        try
        {
            foreach(bool visible in new[]{true,false})
            {
                // Project each host state through production refresh without showing
                // a native canvas or pretending to test the actual visibility command.
                visibleProperty.SetValue(host,visible);Invoke(session,"RefreshToolbarState");Measure(shell);
                string state=visible?"visible":"hidden";
                Save(shell,Path.Combine(output,"production-toolbar-"+theme+"-canvas-"+state+".png"),1.5);
            }
        }
        finally {visibleProperty.SetValue(host,original);Invoke(session,"RefreshToolbarState");Measure(shell);}
    }
    private static void CheckEqualRowInsets(Action<string,bool> check,Controls.Border body,Controls.Grid grid,
        ToolbarStatusDisplay display,string name,double scale)
    {
        var upper=grid.Children.OfType<Controls.Button>().First(button=>Controls.Grid.GetRow(button)==0);
        var lower=grid.Children.OfType<Controls.Button>().First(button=>Controls.Grid.GetRow(button)==2);
        double top=upper.TranslatePoint(new Wpf.Point(),body).Y;
        double upperBottom=upper.TranslatePoint(new Wpf.Point(0,upper.ActualHeight),body).Y;
        double lowerTop=lower.TranslatePoint(new Wpf.Point(),body).Y;
        double bottom=body.ActualHeight-lower.TranslatePoint(new Wpf.Point(0,lower.ActualHeight),body).Y;
        double middle=lowerTop-upperBottom;
        double topInset=top-Math.Round(body.BorderThickness.Top*scale)/scale;
        double bottomInset=bottom-Math.Round(body.BorderThickness.Bottom*scale)/scale;
        Console.WriteLine($"ROW SPACING {name}: inner top={topInset*scale:F2}px middle={middle*scale:F2}px inner bottom={bottomInset*scale:F2}px");
        double tolerance=scale==1?.05:1.01/scale;
        check(name+" gives both rows equal two-DIP upper middle and lower inner spacing",Math.Abs(topInset-2)<=tolerance&&Math.Abs(middle-2)<=tolerance&&Math.Abs(bottomInset-2)<=tolerance);
        double lcdTop=display.TranslatePoint(new Wpf.Point(),body).Y;
        double lcdBottom=display.TranslatePoint(new Wpf.Point(0,display.ActualHeight),body).Y;
        double lowerBottom=body.ActualHeight-bottom;
        var upperChrome=(Controls.Border)upper.Template.FindName("Chrome",upper);
        var lowerChrome=(Controls.Border)lower.Template.FindName("Chrome",lower);
        check(name+" aligns LCD and lower frames with matching small button corners",Math.Abs(lcdTop-lowerTop)<=.05&&Math.Abs(lcdBottom-lowerBottom)<=.05&&display.CornerRadius==new Wpf.CornerRadius(3)&&upperChrome.CornerRadius==display.CornerRadius&&lowerChrome.CornerRadius==display.CornerRadius);
    }
    private static double Contrast(Media.Color first,Media.Color second)
    {
        static double Channel(byte value){double x=value/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        static double Light(Media.Color c)=>.2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);
        double a=Light(first),b=Light(second);return(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private static bool NeighborUnchanged(Wpf.FrameworkElement shell,Controls.TextBlock hint,Wpf.FrameworkElement neighbor,Wpf.DependencyProperty stroke,bool leftEdge,double scale)
    {
        if(!hint.IsDescendantOf(shell)||!neighbor.IsDescendantOf(shell))throw new InvalidOperationException("A legend pixel probe must use the current production visual tree.");
        var old=neighbor.GetValue(stroke);neighbor.SetResourceReference(stroke,"CanvasAccent");
        var point=neighbor.TranslatePoint(new Wpf.Point(),shell);Measure(shell);var before=Raster(shell,scale);
        hint.Visibility=Wpf.Visibility.Hidden;Measure(shell);var after=Raster(shell,scale);
        hint.Visibility=Wpf.Visibility.Visible;neighbor.SetValue(stroke,old);Measure(shell);
        int left=(int)Math.Floor((point.X+(leftEdge?0:neighbor.ActualWidth-2))*scale),top=(int)Math.Ceiling((point.Y+(leftEdge?6:14))*scale);
        return DifferentPixels(before,after,new Wpf.Int32Rect(left,top,(int)Math.Ceiling(2*scale),(int)Math.Floor(8*scale)))==0;
    }
    private static void CheckNativeDpiLegends(Action<string,bool> check,Wpf.FrameworkElement shell,
        IReadOnlyList<Controls.TextBlock> hints,Dictionary<EditorTool,Controls.Button> tools,StampShelfControl shelf,string output,double scale)
    {
        // RTB resolution alone does not change WPF layout DPI. A hidden native
        // source and the production Window's rounding policy reproduce the
        // fractional inner size of a 26-DIP button with its one-DIP border.
        var root=new Controls.Border {Child=shell,UseLayoutRounding=true,SnapsToDevicePixels=true};
        nint priorContext=SetThreadDpiAwarenessContext(-4),handle=0;
        try
        {
            using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("FloatCanvas toolbar DPI regression")
                {Width=1600,Height=180,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000000});
            handle=source.Handle;source.RootVisual=root;
            // Set the actual layout DPI explicitly so CI covers mixed scales on
            // machines whose physical monitor happens to use another scale.
            Media.VisualTreeHelper.SetRootDpi(root,new Wpf.DpiScale(scale,scale));Measure(root);
            check($"native toolbar DPI probe lays out at {scale:P0} without showing its HWND",IsWindow(handle)&&!IsWindowVisible(handle)&&Near(Media.VisualTreeHelper.GetDpi(shell).DpiScaleX,scale));
            var body=shell is Controls.Grid surface?surface.Children.OfType<Controls.Border>().Single():throw new InvalidOperationException("Toolbar surface has no body.");
            var grid=(Controls.Grid)body.Child;
            var display=grid.Children.OfType<ToolbarStatusDisplay>().Single();
            // Retain the existing 150% row-spacing gate; additional DPI cases
            // exercise shortcut placement and clipping rather than redefine spacing.
            if(scale==1.5)CheckEqualRowInsets(check,body,grid,display,$"native {scale:P0} toolbar",scale);
            var measured=new List<(string Text,int Expected,int Actual)>();
            foreach(var hint in hints)
            {
                var button=Descendants(shell).OfType<Controls.Button>().Single(button=>hint.IsDescendantOf(button));
                var origin=hint.TranslatePoint(new Wpf.Point(),button);bool lower=Controls.Grid.GetRow(button)==2;
                double horizontal=origin.X+hint.ActualWidth/2-(lower?0:button.ActualWidth);
                double vertical=lower?origin.Y+hint.ActualHeight-button.ActualHeight:origin.Y;
                check($"{scale:P0} native {hint.Text} stays on its shared frame corner",Math.Abs(horizontal*scale)<=1.01&&Math.Abs(vertical*scale)<=1.01);
                int expected=ReferenceInk(hint,scale,layoutDpi:true),actual=LegendInk(shell,hint,scale);
                Console.WriteLine($"NATIVE LEGEND {hint.Text}: actual={actual}, reference={expected}");
                measured.Add((hint.Text,expected,actual));
            }
            foreach(var result in measured)check($"{scale:P0} native toolbar retains the complete "+result.Text+" shortcut legend",result.Expected>0&&result.Actual>=result.Expected*.8);
            check($"{scale:P0} native upper mask preserves its neighboring selected frame",NeighborUnchanged(shell,hints.Single(hint=>hint.Text=="R"),tools[EditorTool.Ellipse],Controls.Control.BorderBrushProperty,true,scale));
            var slots=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
            var current=(Controls.Grid)slots[1].Content;
            var previous=((Controls.Grid)slots[0].Content).Children.OfType<Controls.Border>().Single();
            check($"{scale:P0} native lower mask preserves its previous slot frame",NeighborUnchanged(shell,current.Children.OfType<Controls.TextBlock>().Single(),previous,Controls.Border.BorderBrushProperty,false,scale));
            Save(shell,Path.Combine(output,$"production-toolbar-native-{scale*100:0}.png"),scale);
            var upper=Descendants(shell).OfType<ToolbarUpperRow>().Single();
            upper.SetExpanded(true);Measure(root);
            var extra=Descendants(upper).OfType<Controls.Button>().Single(button=>Equals(button.Tag,"extra-tool:Pixelate"));
            var oldBrush=extra.BorderBrush;extra.SetResourceReference(Controls.Control.BorderBrushProperty,"CanvasAccent");
            Measure(root);
            Save(shell,Path.Combine(output,$"selected-extra-native-{scale*100:0}.png"),scale);
            for(Media.Visual? visual=extra;visual is not null&&visual!=shell;visual=Media.VisualTreeHelper.GetParent(visual) as Media.Visual)
            {
                var clip=Media.VisualTreeHelper.GetClip(visual);
                var bounds=extra.TransformToAncestor(visual).TransformBounds(new Wpf.Rect(extra.RenderSize));
                Console.WriteLine($"EXTRA CLIP {scale}: {visual.GetType().Name} size={(visual as Wpf.FrameworkElement)?.RenderSize} clip={clip?.Bounds} button={bounds}");
                check($"{scale:P0} selected extra frame fits {visual.GetType().Name} layout clip",clip is null||clip.Bounds.Contains(bounds));
            }
            extra.BorderBrush=oldBrush;upper.SetExpanded(false);Measure(root);
            source.RootVisual=null;
        }
        finally
        {
            root.Child=null;
            if(priorContext!=0)SetThreadDpiAwarenessContext(priorContext);
        }
        check("native toolbar DPI probe destroys its hidden HWND",handle!=0&&!IsWindow(handle));
    }
    private static int ReferenceInk(Controls.TextBlock hint,double scale,bool layoutDpi=false)
    {
        var reference=new Controls.TextBlock {Text=hint.Text,Width=hint.Width,Height=hint.Height,
            FontFamily=hint.FontFamily,FontSize=hint.FontSize,FontWeight=hint.FontWeight,
            LineHeight=hint.LineHeight,LineStackingStrategy=hint.LineStackingStrategy,
            TextAlignment=hint.TextAlignment,Foreground=Media.Brushes.Black,Background=Media.Brushes.White};
        Media.TextOptions.SetTextFormattingMode(reference,Media.TextOptions.GetTextFormattingMode(hint));
        if(layoutDpi)
        {
            Media.VisualTreeHelper.SetRootDpi(reference,new Wpf.DpiScale(scale,scale));reference.UseLayoutRounding=true;
            reference.Foreground=hint.Foreground;reference.Background=(Media.Brush)Wpf.Application.Current.FindResource("CanvasSurface");
        }
        Measure(reference);var before=Raster(reference,scale);reference.Text="";Measure(reference);
        return DifferentPixels(before,Raster(reference,scale),new Wpf.Int32Rect(0,0,before.PixelWidth,before.PixelHeight));
    }
    private static int LegendInk(Wpf.FrameworkElement shell,Controls.TextBlock hint,double scale)
    {
        var point=hint.TranslatePoint(new Wpf.Point(),shell);var before=Raster(shell,scale);
        string text=hint.Text;hint.Text="";Measure(shell);var after=Raster(shell,scale);hint.Text=text;Measure(shell);
        int left=Math.Max(0,(int)Math.Floor(point.X*scale)),top=Math.Max(0,(int)Math.Floor(point.Y*scale));
        int right=Math.Min(before.PixelWidth,(int)Math.Ceiling((point.X+hint.ActualWidth)*scale));
        int bottom=Math.Min(before.PixelHeight,(int)Math.Ceiling((point.Y+hint.ActualHeight)*scale));
        return DifferentPixels(before,after,new Wpf.Int32Rect(left,top,right-left,bottom-top));
    }
    private static int DifferentPixels(Imaging.BitmapSource first,Imaging.BitmapSource second,Wpf.Int32Rect area)
    {
        int stride=area.Width*4;var a=new byte[stride*area.Height];var b=new byte[a.Length];
        first.CopyPixels(area,a,stride,0);second.CopyPixels(area,b,stride,0);int count=0;
        for(int i=0;i<a.Length;i+=4)if(Math.Abs(a[i]-b[i])+Math.Abs(a[i+1]-b[i+1])+Math.Abs(a[i+2]-b[i+2])>24)count++;
        return count;
    }
    private static Imaging.RenderTargetBitmap Raster(Wpf.FrameworkElement element,double scale)
    {var bitmap=new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,Media.PixelFormats.Pbgra32);bitmap.Render(element);return bitmap;}
    private static Wpf.UIElement Glyph(Controls.Button button)=>((Controls.Grid)button.Content).Children.OfType<Controls.ContentPresenter>().Single().Content as Wpf.UIElement ?? throw new InvalidOperationException("Missing tool glyph.");
    private static string Fingerprint(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));}
    private static bool ContainsColor(Imaging.BitmapSource bitmap,Media.Color color)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);for(int i=0;i<bytes.Length;i+=4)if(bytes[i+3]>250&&Math.Abs(bytes[i+2]-color.R)<4&&Math.Abs(bytes[i+1]-color.G)<4&&Math.Abs(bytes[i]-color.B)<4)return true;return false;}
    private static T Field<T>(object source,string name)=>(T)(CanvasOptionsTestWindow.Unwrap(source)).GetType().GetField(name,Private)!.GetValue(CanvasOptionsTestWindow.Unwrap(source))!;
    private static void Set(object source,string name,object? value)=>(CanvasOptionsTestWindow.Unwrap(source)).GetType().GetField(name,Private)!.SetValue(source,value);
    private static void Invoke(object source,string name,params object[] arguments)
    {
        try{source.GetType().GetMethod(name,Private)!.Invoke(source,arguments);}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();}
    }
    private static void CheckHeaderStates(Action<string,bool> check,Wpf.FrameworkElement shell,ToolbarUpperRow upper,BoundFileIndicator file,RootCanvasToolbarHeader header,string output)
    {
        string language=L.Preference;
        var strip=Field<Controls.Border>(upper,"strip");
        var select=Descendants(shell).OfType<Controls.Button>().Single(button=>Equals(button.Tag,"tool:Select"));
        var selectIcon=Descendants(select).OfType<Controls.Viewbox>().Single();
        double CenterX(Wpf.FrameworkElement element)=>element.TranslatePoint(new(element.ActualWidth/2,element.ActualHeight/2),shell).X;
        int navigated=0;
        var empty=new EditingScopeDescription("主画布","工程预览","",[],"主画布");
        void VerifyMenu(Controls.Button button,string prefix,bool invokeAncestor)
        {
            if(Wpf.PresentationSource.FromVisual(button) is not System.Windows.Interop.HwndSource owner)return;
            button.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
            var menu=(OverlayContextMenu)button.ContextMenu!;
            check(prefix+" opens the shared overlay menu rather than the WPF popup",menu.IsOpen&&!((Controls.ContextMenu)menu).IsOpen&&menu.Placement==Controls.Primitives.PlacementMode.Bottom);
            menu.UpdateLayout();
            var popup=(System.Windows.Interop.HwndSource)Wpf.PresentationSource.FromVisual(menu)!;
            check(prefix+" menu HWND stays above the topmost toolbar",IsWindowVisible(popup.Handle)&&WindowRank(popup.Handle)<WindowRank(owner.Handle));
            Save(menu,Path.Combine(output,prefix.Replace(' ','-')+"-menu.png"),1.5);
            if(invokeAncestor)menu.Items.OfType<Controls.MenuItem>().First(item=>!Equals(item.Tag,"scope-mode")).RaiseEvent(new Wpf.RoutedEventArgs(Controls.MenuItem.ClickEvent));
            else menu.IsOpen=false;
            check(prefix+" closes and releases its popup",!menu.IsOpen&&popup.IsDisposed);
        }
        try
        {
            foreach(string locale in new[]{"zh-CN","en"}) foreach(string theme in new[]{"light","dark"})
            {
                L.Initialize(locale); AppTheme.Apply(theme); upper.SetExpanded(true);
                string root=locale=="en"?"Main":"主画布", group=locale=="en"?"Sketch":"草图组";
                string mode=locale=="en"?"Project preview":"工程预览";
                var scope=new EditingScopeDescription(root+" › "+group,mode,"",[(root,()=>navigated++)],group);
                double filenameBaseline=double.NaN;
                for(int state=1;state<=4;state++)
                {
                    bool linked=state>=3,entered=state is 2 or 4;
                    file.Refresh(linked?@"C:\Projects\Sketch.floatcanvas":null,true,true);
                    header.SetScope(entered?scope:empty,entered); Measure(shell);
                    string prefix=$"header {locale} {theme} state {state}";
                    check(prefix+" has the expected empty/file/path visibility",(header.Visibility==Wpf.Visibility.Visible)==(state!=1));
                    if(state==1){Save(shell,Path.Combine(output,$"header-state1-{locale}-{theme}.png"),1.5);continue;}
                    var frame=Field<Controls.Border>(header,"frame");
                    var edge=(Media.Effects.DropShadowEffect)frame.Effect;
                    check(prefix+" file and expanded toolbar share the current theme edge effect",ReferenceEquals(edge,Field<System.Windows.Shapes.Path>(upper,"outline").Effect)&&
                        (Wpf.SystemParameters.HighContrast?edge.Opacity==0:theme=="dark"?edge.Color.R>150&&edge.Color.G>150&&edge.Color.B>150:edge.Color==Media.Colors.Black));
                    Save(shell,Path.Combine(output,$"bubble-{locale}-{theme}-state{state}.png"),1);
                    var outline=Media.VisualTreeHelper.GetDrawing(frame).Children.OfType<Media.GeometryDrawing>().Single();
                    var geometry=outline.Geometry;
                    var pen=outline.Pen;
                    var selectedBounds=select.TransformToVisual(header).TransformBounds(new Wpf.Rect(select.RenderSize));
                    var painted=Media.Geometry.Combine(geometry,geometry.GetWidenedPathGeometry(pen),Media.GeometryCombineMode.Union,null);
                    var intersection=Media.Geometry.Combine(painted,new Media.RectangleGeometry(selectedBounds),Media.GeometryCombineMode.Intersect,null);
                    check(prefix+" has one closed bubble contour with no internal join stroke",geometry.GetFlattenedPathGeometry().Figures.Count==1&&
                        geometry.GetFlattenedPathGeometry().Figures[0].IsClosed&&geometry.FillContains(new Wpf.Point(.5,31))&&!geometry.StrokeContains(pen,new(.5,31))&&
                        geometry.FillContains(new Wpf.Point(3,32.5))&&!geometry.StrokeContains(pen,new(3,32.5)));
                    check(prefix+" tail avoids the Select frame and shares the surface shadow",geometry.Bounds.Left<0&&geometry.Bounds.Bottom>header.ActualHeight&&intersection.GetArea()<.001&&
                        !frame.IsHitTestVisible&&frame.Effect is Media.Effects.DropShadowEffect);
                    check(prefix+" has a fixed left edge and stays within the extension limit",header.ActualHeight==strip.ActualHeight&&header.ActualWidth<=header.MaxWidth&&frame.CornerRadius==new Wpf.CornerRadius(ToolbarUpperRow.OuterCornerRadius)&&
                        Near(header.TranslatePoint(new(),shell).X,select.TranslatePoint(new(),shell).X-(ToolbarUpperRow.OuterCornerRadius-ToolbarUpperRow.CornerRadiusValue))&&header.TranslatePoint(new(header.ActualWidth,0),shell).X<=strip.TranslatePoint(new(),shell).X+.1&&Near(header.TranslatePoint(new(),shell).Y,strip.TranslatePoint(new(),shell).Y));
                    check(prefix+" collapses the file only when showing navigation",!linked||(entered?file.ActualWidth==ToolbarUpperRow.ButtonSlot:Field<Controls.TextBlock>(file,"label").Visibility==Wpf.Visibility.Visible));
                    if(linked)
                    {
                        var icon=Descendants(file).OfType<Controls.Viewbox>().Single();
                        var entry=Field<Controls.Button>(file,"entry");
                        var chrome=(Controls.Border)entry.Template.FindName("Chrome",entry);
                        var selectedChrome=(Controls.Border)select.Template.FindName("Chrome",select);
                        check(prefix+" file action shares the main toolbar button height and radius",Near(chrome.ActualHeight,selectedChrome.ActualHeight)&&chrome.CornerRadius==selectedChrome.CornerRadius&&(!entered||Near(chrome.ActualWidth,selectedChrome.ActualWidth)));
                        var dot=Field<System.Windows.Shapes.Ellipse>(file,"modifiedDot");var capsule=Field<Controls.Border>(file,"unsaved");
                        var projectIcon=Field<Controls.Grid>(file,"projectIcon");
                        var iconBounds=icon.TransformToAncestor(projectIcon).TransformBounds(new Wpf.Rect(icon.RenderSize));
                        var dotBounds=dot.TransformToAncestor(projectIcon).TransformBounds(new Wpf.Rect(dot.RenderSize));
                        check(prefix+" compact modifications use a corner dot instead of a miniature capsule",entered?
                            dot.Visibility==Wpf.Visibility.Visible&&capsule.Visibility==Wpf.Visibility.Collapsed&&dotBounds.Left>=iconBounds.Left+iconBounds.Width/2&&dotBounds.Top<iconBounds.Top+iconBounds.Height/2:
                            dot.Visibility==Wpf.Visibility.Collapsed&&capsule.Visibility==Wpf.Visibility.Visible);
                        var fileText=Field<Controls.TextBlock>(file,"label");
                        if(state==3)filenameBaseline=fileText.TranslatePoint(new(0,fileText.BaselineOffset),header).Y;
                        var corner=chrome.TranslatePoint(new(chrome.CornerRadius.TopLeft,chrome.CornerRadius.TopLeft),header);
                        check(prefix+" aligns the first icon with Select and keeps the outer corner concentric",Near(CenterX(icon),CenterX(selectIcon))&&Near(corner.X,frame.CornerRadius.TopLeft)&&Near(corner.Y,frame.CornerRadius.TopLeft));
                        file.Refresh(@"C:\Projects\Sketch.floatcanvas",false,true);Measure(shell);
                        check(prefix+" saving clears both modification indicators",dot.Visibility==Wpf.Visibility.Collapsed&&capsule.Visibility==Wpf.Visibility.Collapsed);
                        check(prefix+" saved state and hidden extra tools retain the same icon axis",Near(CenterX(icon),CenterX(selectIcon)));
                        upper.SetExpanded(false);Measure(shell);
                        check(prefix+" collapsed main body follows the current header edge effect",ReferenceEquals(Field<Controls.Border>(upper,"body").Effect,frame.Effect));
                        check(prefix+" collapsed extra tools retain the same icon axis",Near(CenterX(icon),CenterX(selectIcon)));
                        upper.SetExpanded(true);file.Refresh(@"C:\Projects\Sketch.floatcanvas",true,true);Measure(shell);
                    }
                    if(entered)
                    {
                        var parent=Field<Controls.Button>(header,"scopeParent");var current=Field<Controls.Button>(header,"scopeButton");
                        var bounds=current.TransformToAncestor(header).TransformBounds(new Wpf.Rect(current.RenderSize));
                        check(prefix+" keeps current location inside the shared frame with toolbar-sized hover frames",bounds.Right<=header.ActualWidth+.1&&parent.ActualHeight==select.ActualHeight&&current.ActualHeight==select.ActualHeight&&current.ContextMenu!.Items.OfType<Controls.MenuItem>().Count(i=>!Equals(i.Tag,"scope-mode"))==2);
                        var parentText=(Controls.TextBlock)parent.Content; var currentText=Field<Controls.TextBlock>(header,"current");
                        double baseline=parentText.TranslatePoint(new(0,parentText.BaselineOffset),header).Y;
                        check(prefix+" ancestor and current text share a baseline with the expanded filename",Near(baseline,currentText.TranslatePoint(new(0,currentText.BaselineOffset),header).Y)&&(!linked||Near(baseline,filenameBaseline)));
                        parent.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
                        var dropdown=Field<Wpf.FrameworkElement>(header,"dropdown");
                        check(prefix+" centers the dropdown icon independently of the text baseline",Near(dropdown.TranslatePoint(new(dropdown.ActualWidth/2,dropdown.ActualHeight/2),current).Y,current.ActualHeight/2));
                        VerifyMenu(current,prefix,true);
                        if(linked&&Wpf.PresentationSource.FromVisual(file) is not null)
                        {
                            var entry=Field<Controls.Button>(file,"entry");entry.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
                            var menu=(OverlayContextMenu)file.ContextMenu!;
                            check(prefix+" compact file opens its existing actions with full filename and save status",menu.IsOpen&&menu.Items.Count==4&&
                                System.Windows.Automation.AutomationProperties.GetName(entry)=="Sketch.floatcanvas"&&System.Windows.Automation.AutomationProperties.GetItemStatus(entry)==L.Get("Project.Unsaved"));
                            menu.IsOpen=false;
                        }
                    }
                    if(state==4)CheckBubbleLayer(check,shell,upper,header,frame,geometry,prefix,output);
                    foreach(double scale in new[]{1d,1.5,2})Save(shell,Path.Combine(output,$"header-state{state}-{locale}-{theme}-{scale}.png"),scale);
                }
                file.Refresh(@"C:\Projects\Sketch.floatcanvas",false,true);
                header.SetScope(new("Main › One › Two › Three › Long current name",mode,"",[("Main",()=>{}),("One",()=>{}),("Two",()=>{}),("Three",()=>{})],"Long current name"),true);Measure(shell);
                var overflow=Field<Controls.Button>(header,"overflow");
                check(locale+theme+" deep path folds older ancestors and keeps direct parent",overflow.Visibility==Wpf.Visibility.Visible&&overflow.ContextMenu!.Items.OfType<Controls.MenuItem>().Count(i=>!Equals(i.Tag,"scope-mode"))==5&&Field<Controls.Button>(header,"scopeParent").Visibility==Wpf.Visibility.Visible);
                VerifyMenu(overflow,$"header-deep-{locale}-{theme}",false);
                Save(shell,Path.Combine(output,$"header-deep-{locale}-{theme}.png"),1.5);
                header.SetScope(empty,false);file.Refresh(@"C:\Projects\S.floatcanvas",false,true);Measure(shell);double shortWidth=header.ActualWidth,shortLeft=header.TranslatePoint(new(),shell).X;
                file.Refresh(@"C:\Projects\This is a deliberately long linked project filename.floatcanvas",true,true);Measure(shell);
                check(locale+theme+" filename strip grows to the right while its left edge stays fixed",shortWidth<header.ActualWidth&&header.ActualWidth<=header.MaxWidth&&Near(shortLeft,header.TranslatePoint(new(),shell).X));
            }
            check("direct parent buttons and dropdown items both retain navigation",navigated==16);
        }
        finally {L.Initialize(language);AppTheme.Apply("light");header.SetScope(empty,false);file.Refresh(@"C:\图标验收\示例画布.floatcanvas",true,true);upper.SetExpanded(false);Measure(shell);}
    }
    private static void CheckBubbleLayer(Action<string,bool> check,Wpf.FrameworkElement shell,ToolbarUpperRow upper,RootCanvasToolbarHeader header,Controls.Border frame,Media.Geometry geometry,string prefix,string output)
    {
        var diagnostic=Media.Color.FromRgb(203,23,137);
        var effect=frame.Effect;
        var sample=Enumerable.Range(39,8).SelectMany(y=>Enumerable.Range(-9,11).Select(x=>new Wpf.Point(x+.5,y+.5)))
            .First(point=>geometry.FillContains(point)&&!geometry.StrokeContains(new Media.Pen(Media.Brushes.Black,2.1),point));
        var sampleOnShell=header.TranslatePoint(sample,shell);
        try
        {
            frame.Background=new Media.SolidColorBrush(diagnostic);
            PumpToIdle(frame.Dispatcher);
            foreach(double scale in new[]{1d,1.5,2})
            {
                var bitmap=Save(shell,Path.Combine(output,prefix.Replace(' ','-')+$"-layer-{scale}.png"),scale);
                var actual=ColorAt(bitmap,(int)(sampleOnShell.X*scale),(int)(sampleOnShell.Y*scale));
                check(prefix+$" tail paints above the connected main toolbar at {scale*100:0}% ({sampleOnShell}: {actual})",actual.R==diagnostic.R&&actual.G==diagnostic.G&&actual.B==diagnostic.B);
            }
            var shadowed=Save(shell,Path.Combine(output,prefix.Replace(' ','-')+"-shadow.png"),2);
            frame.Effect=null;
            PumpToIdle(frame.Dispatcher);
            var plain=Save(shell,Path.Combine(output,prefix.Replace(' ','-')+"-without-shadow.png"),2);
            int shadowPixels=0;
            for(int y=38;y<55;y++)for(int x=-15;x<2;x++)
            {
                var point=new Wpf.Point(x,y);if(geometry.FillContains(point))continue;
                var onShell=header.TranslatePoint(point,shell);
                var a=ColorAt(shadowed,(int)(onShell.X*2),(int)(onShell.Y*2));var b=ColorAt(plain,(int)(onShell.X*2),(int)(onShell.Y*2));
                if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)>6)shadowPixels++;
            }
            check(prefix+" shadow extends around the tail below the rectangular body",shadowPixels>3);
            frame.Effect=effect;upper.Visibility=Wpf.Visibility.Collapsed;Measure(shell);
            var hidden=Save(shell,Path.Combine(output,prefix.Replace(' ','-')+"-hidden.png"),1);
            var hiddenPixel=ColorAt(hidden,(int)sampleOnShell.X,(int)sampleOnShell.Y);
            check(prefix+" hiding the toolbar header also hides the tail",hiddenPixel!=diagnostic&&!frame.IsVisible);
        }
        finally {upper.Visibility=Wpf.Visibility.Visible;frame.SetResourceReference(Controls.Border.BackgroundProperty,"CanvasSurface");frame.SetResourceReference(Wpf.UIElement.EffectProperty,"ToolbarEdgeEffect");Measure(shell);PumpToIdle(frame.Dispatcher);}
    }
    private static void Measure(Wpf.FrameworkElement element)
    {element.Measure(new Wpf.Size(double.PositiveInfinity,double.PositiveInfinity));element.Arrange(new Wpf.Rect(new Wpf.Point(),element.DesiredSize));element.UpdateLayout();}
    private static void PumpToIdle(Threading.Dispatcher dispatcher)
    {
        if(dispatcher.HasShutdownStarted)return;
        var frame=new Threading.DispatcherFrame();dispatcher.BeginInvoke(Threading.DispatcherPriority.ContextIdle,()=>frame.Continue=false);Threading.Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        for(int index=0;index<Media.VisualTreeHelper.GetChildrenCount(root);index++)
        {var child=Media.VisualTreeHelper.GetChild(root,index);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static Wpf.Point[] Vertices(Media.Geometry geometry)=>geometry.GetFlattenedPathGeometry().Figures.SelectMany(figure=>new[]{figure.StartPoint}.Concat(figure.Segments.SelectMany(segment=>segment switch
    {Media.LineSegment line=>new[]{line.Point},Media.PolyLineSegment poly=>poly.Points.AsEnumerable(),_=>throw new InvalidOperationException("Expected a flattened line path.")}))).ToArray();
    private static void CheckUpperRowOverlap(Action<string,bool> check,Wpf.FrameworkElement shell,ToolbarUpperRow upper,BoundFileIndicator file,string theme,string output)
    {
        var strip=Field<Controls.Border>(upper,"strip");var confirmation=Field<Controls.Border>(upper,"confirmation");
        check(theme+" header surfaces share the extension height and top edge",file.ActualHeight==strip.ActualHeight&&confirmation.ActualHeight==strip.ActualHeight&&
            Near(file.TranslatePoint(new(),shell).Y,strip.TranslatePoint(new(),shell).Y)&&Near(confirmation.TranslatePoint(new(),shell).Y,strip.TranslatePoint(new(),shell).Y));
        var slots=(Controls.Grid)strip.Child;int count=slots.ColumnDefinitions.Count;double width=strip.Width;
        var additions=new List<Controls.Button>();
        try
        {
            for(int i=0;i<8;i++)
            {
                slots.ColumnDefinitions.Add(new(){Width=new Wpf.GridLength(30)});
                var button=new Controls.Button {Content=(i+5).ToString(),Width=26,Height=26,MinWidth=0,MinHeight=0,Margin=new Wpf.Thickness(2,0,2,0),Padding=new Wpf.Thickness(0),Background=Media.Brushes.Red};
                Controls.Grid.SetColumn(button,count+i);slots.Children.Add(button);additions.Add(button);
            }
            strip.Width=width+240;Measure(shell);
            var origin=confirmation.TranslatePoint(new(),shell);
            check(theme+" extended tool row reaches under the confirmation",strip.TranslatePoint(new Wpf.Point(strip.ActualWidth,0),shell).X>origin.X+12);
            var hit=Media.VisualTreeHelper.HitTest(upper,confirmation.TranslatePoint(new Wpf.Point(10,confirmation.ActualHeight-6),upper))?.VisualHit;
            check(theme+" confirmation owns the topmost hit-testable surface above hidden tools",Equals(confirmation.ReadLocalValue(Wpf.UIElement.IsHitTestVisibleProperty),true)&&(ReferenceEquals(hit,confirmation)||hit is not null&&Descendants(confirmation).Contains(hit)));
            var pixels=Save(shell,Path.Combine(output,$"overflow-confirmation-{theme}.png"),1.5);
            var actual=ColorAt(pixels,(int)((origin.X+10)*1.5),(int)((origin.Y+confirmation.ActualHeight-6)*1.5));
            var expected=((Media.SolidColorBrush)confirmation.Background).Color;
            check(theme+" confirmation covers future tools with its opaque surface",Controls.Panel.GetZIndex(confirmation)>Controls.Panel.GetZIndex(strip)&&
                Math.Abs(actual.R-expected.R)<=2&&Math.Abs(actual.G-expected.G)<=2&&Math.Abs(actual.B-expected.B)<=2);
        }
        finally {foreach(var button in additions)slots.Children.Remove(button);while(slots.ColumnDefinitions.Count>count)slots.ColumnDefinitions.RemoveAt(count);strip.Width=width;Measure(shell);}
    }
    private static Imaging.RenderTargetBitmap Save(Wpf.FrameworkElement element,string path,double scale)
    {
        var pixels=new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,Media.PixelFormats.Pbgra32);pixels.Render(element);
        var png=new Imaging.PngBitmapEncoder();png.Frames.Add(Imaging.BitmapFrame.Create(pixels));using var output=File.Create(path);png.Save(output);
        return pixels;
    }
    private static Media.Color ColorAt(Imaging.BitmapSource bitmap,int x,int y)
    {byte[] pixel=new byte[4];bitmap.CopyPixels(new Wpf.Int32Rect(x,y,1,1),pixel,4,0);return Media.Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);}
    private static int WindowRank(nint handle){nint current=GetTopWindow(0);for(int i=0;i<8192&&current!=0;i++,current=GetWindow(current,2))if(current==handle)return i;return int.MaxValue;}
    [DllImport("user32.dll")]private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint relation);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
}
