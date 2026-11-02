using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class WindowSourceUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference; var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Picker(check); Crop(check); Commands(check); Inspector(check); Menu(check); if (output is not null) Visuals(output, check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); L.Initialize(language); AppTheme.Apply("light"); }
    }
    private static void Until(Func<bool> done)
    {
        var time = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (time.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fake window-source UI work did not settle.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    private static void Finish(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static WindowSourceCandidate Candidate(string title = "Synthetic viewer", string process = "synthetic.exe", string? reason = null) => new(new object(), title, process, "SyntheticWindow", reason);
    private static void Picker(Action<string, bool> check)
    {
        int calls = 0; var first = Candidate(); var second = Candidate("Synthetic document", "document.exe");
        var picker = new WindowSourcePickerWindow(_ => { calls++; return Task.FromResult<IReadOnlyList<WindowSourceCandidate>>([first, second]); });
        check("window picker construction never invokes a metadata provider or creates a native host", calls == 0 && picker.SelectedCandidate is null && new WindowInteropHelper(picker).Handle == 0);
        Finish(picker.RefreshCandidatesAsync()); var list = Field<ListBox>(picker, "candidates");
        check("explicit picker refresh displays only injected metadata without automatically choosing a target", calls == 1 && list.Items.Count == 2 && picker.SelectedCandidate is null);
        Field<TextBox>(picker, "search").Text = "document.exe";
        check("window metadata search filters the cached catalog without enumerating again", list.Items.Count == 1 && ReferenceEquals(list.Items[0], second) && calls == 1);
        Field<TextBox>(picker, "search").Text = "no such synthetic source";
        check("empty metadata search has its own readable state", list.Items.Count == 0 && Field<TextBlock>(picker, "status").Text == L.Get("WindowSource.NoMatches"));
        Field<TextBox>(picker, "search").Text = ""; list.SelectedIndex = 0;
        check("picker retains the exact opaque candidate chosen by the user", ReferenceEquals(picker.SelectedCandidate, first) && Field<Button>(picker, "select").IsEnabled);
        Field<Button>(picker, "select").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("picker acceptance returns metadata without owning a capture", picker.Accepted && ReferenceEquals(picker.SelectedCandidate!.Target, first.Target) && calls == 1);

        var unavailable = new WindowSourcePickerWindow(_ => Task.FromResult<IReadOnlyList<WindowSourceCandidate>>([Candidate(reason: "WindowSource.Status.Unavailable")]));
        Finish(unavailable.RefreshCandidatesAsync()); Field<ListBox>(unavailable, "candidates").SelectedIndex = 0;
        check("unavailable metadata cannot be accepted as a capture target", !Field<Button>(unavailable, "select").IsEnabled && Field<TextBlock>(unavailable, "status").Text == L.Get("WindowSource.Status.Unavailable")); SessionWindowCoordinator.Close(unavailable);

        var mixed = new WindowSourcePickerWindow(_ => Task.FromResult<IReadOnlyList<WindowSourceCandidate>>([Candidate(reason: "WindowSource.Status.Unavailable"), second]));
        Finish(mixed.RefreshCandidatesAsync()); var mixedList = Field<ListBox>(mixed, "candidates"); mixedList.SelectedIndex = 0; mixedList.SelectedIndex = 1;
        check("choosing an available source clears a previous candidate's failure instead of displaying a stale warning", Field<Button>(mixed, "select").IsEnabled &&
            Field<TextBlock>(mixed, "status").Text == "" && Field<TextBlock>(mixed, "status").Visibility == Visibility.Collapsed);
        SessionWindowCoordinator.Close(mixed);

        var old = new TaskCompletionSource<IReadOnlyList<WindowSourceCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource<IReadOnlyList<WindowSourceCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously); calls = 0; CancellationToken oldToken = default;
        var racing = new WindowSourcePickerWindow(token => { if (++calls == 1) { oldToken = token; return old.Task; } return latest.Task; });
        var oldRequest = racing.RefreshCandidatesAsync(); Until(() => calls == 1); var newRequest = racing.RefreshCandidatesAsync(); Until(() => calls == 2);
        latest.SetResult([second]); Finish(newRequest); old.SetResult([first]); Finish(oldRequest);
        check("new picker refresh cancels its predecessor and rejects an older completed catalog", oldToken.IsCancellationRequested && Field<ListBox>(racing, "candidates").Items.Count == 1 && ReferenceEquals(Field<ListBox>(racing, "candidates").Items[0], second)); SessionWindowCoordinator.Close(racing);
        var late = new TaskCompletionSource<IReadOnlyList<WindowSourceCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously); bool entered = false;
        var closed = new WindowSourcePickerWindow(_ => { entered = true; return late.Task; }); var closedRequest = closed.RefreshCandidatesAsync(); Until(() => entered); SessionWindowCoordinator.Close(closed); late.SetResult([first]); Finish(closedRequest);
        check("closing a picker rejects late metadata without a new native host", !closed.Accepted && closed.SelectedCandidate is null && Field<ListBox>(closed, "candidates").Items.Count == 0 && new WindowInteropHelper(closed).Handle == 0);
        var broken = new WindowSourcePickerWindow(_ => Task.FromException<IReadOnlyList<WindowSourceCandidate>>(new IOException("Fake catalog failure"))); Finish(broken.RefreshCandidatesAsync());
        check("metadata failures stay readable after the request completes", Field<TextBlock>(broken, "status").Text == L.Get("WindowSource.QueryFailed") && !Field<Button>(broken, "select").IsEnabled); SessionWindowCoordinator.Close(broken);
    }
    private static byte[] Png()
    {
        using var surface = SKSurface.Create(new SKImageInfo(320, 180)); surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.CornflowerBlue }; surface.Canvas.DrawRect(0, 0, 160, 180, paint);
        paint.Color = SKColors.Orange; surface.Canvas.DrawCircle(235, 90, 50, paint);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static void Crop(Action<string, bool> check)
    {
        var image = WindowSourceCropGeometry.Fit(400, 400, 400, 200);
        check("full source preview fits without confusing letterbox with image pixels", image == new RectD(0, 100, 400, 200));
        check("crop coordinates normalize against the complete fitted source", WindowSourceCropGeometry.TryDrag(image, new(100, 150), new(300, 250), out var crop) && crop == new RectD(.25, .25, .5, .5));
        check("a crop cannot start in preview letterbox", !WindowSourceCropGeometry.TryDrag(image, new(50, 50), new(300, 250), out _));
        check("crop drag clamps its end to source pixels", WindowSourceCropGeometry.TryDrag(image, new(100, 150), new(800, 800), out crop) && crop == new RectD(.25, .25, .75, .75));
        check("reverse crop drag has the same normalized meaning", WindowSourceCropGeometry.TryDrag(image, new(300, 250), new(100, 150), out crop) && crop == new RectD(.25, .25, .5, .5));
        check("empty and nonfinite preview crops are rejected", !WindowSourceCropGeometry.TryDrag(image, new(100, 150), new(100, 150), out _) && !WindowSourceCropGeometry.TryDrag(image, new(double.NaN, 0), new(100, 150), out _) && WindowSourceCropGeometry.Fit(400, 400, double.PositiveInfinity, 200).IsEmpty);
        byte[] bytes = Png(); var window = new WindowSourceCropWindow(bytes); bytes.AsSpan().Clear();
        check("crop panel owns its decoded frozen full-source preview", Field<BitmapImage>(window, "bitmap") is { PixelWidth: 320, PixelHeight: 180, IsFrozen: true } && window.NormalizedCrop is null && new WindowInteropHelper(window).Handle == 0);
        window.SetCrop(new(.1, .2, .7, .6)); Field<Button>(window, "apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("crop panel returns only the normalized ImageCrop after explicit Apply", window.Accepted && window.NormalizedCrop == new RectD(.1, .2, .7, .6));
        var cancelled = new WindowSourceCropWindow(Png(), new(.1, .2, .7, .6)); cancelled.SetCrop(null); SessionWindowCoordinator.Close(cancelled);
        check("resetting or cancelling a source crop does not imply Apply", cancelled.NormalizedCrop is null && !cancelled.Accepted);
        bool invalid = false; try { _ = new WindowSourceCropWindow(Png(), new(-.1, 0, .5, .5)); } catch (ArgumentOutOfRangeException) { invalid = true; }
        check("out-of-range source crop is rejected before a dialog is constructed", invalid);
    }
    private static EditorSession ImageSession(bool live = true)
    {
        var assets = new AssetLibrary(); string asset = assets.Add(Png(), "image/png");
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 200, Height = 100,
            WindowSource = live ? new() { BindingIntent = Guid.NewGuid(), TitleHint = "Synthetic viewer", ProcessNameHint = "synthetic.exe", WindowClassHint = "SyntheticWindow" } : null };
        var session = new EditorSession(new() { Viewport = new(0, 0, 600, 400), Elements = [image] }, assets); session.SetSelection([image.Id]); return session;
    }
    private static void Commands(Action<string, bool> check)
    {
        var session = ImageSession(); var controller = new EditorController(session) { Tool = EditorTool.Crop }; int requests = 0; Guid requested = default;
        controller.WindowSourceCropRequested += id => { requests++; requested = id; }; string before = JsonSerializer.Serialize(session.Document);
        controller.Pointer(new(OverlayPointerKind.Down, new(20, 20), OverlayMouseButton.Left, OverlayModifiers.Control));
        controller.Pointer(new(OverlayPointerKind.Move, new(120, 80), OverlayMouseButton.Left, OverlayModifiers.Control)); controller.Pointer(new(OverlayPointerKind.Up, new(120, 80), OverlayMouseButton.Left, OverlayModifiers.Control));
        check("live Image Crop redirects a real source ID without creating a canvas crop transaction", requests == 1 && requested == session.Document.Elements[0].Id && session.UndoCount == 0 && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before);
        check("tool entry can explicitly redirect live source crop through the same event", controller.TryRedirectWindowSourceCrop() && requests == 2);
        session.BeginTransaction("foreign"); controller.TryRedirectWindowSourceCrop();
        check("source crop redirect does not act inside a foreign transaction", requests == 2 && session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before); session.Cancel();
        var ordinary = ImageSession(false); controller = new(ordinary) { Tool = EditorTool.Crop };
        check("ordinary image Crop does not redirect into a window-source editor", !controller.TryRedirectWindowSourceCrop());
        controller.Pointer(new(OverlayPointerKind.Down, new(20, 20), OverlayMouseButton.Left, OverlayModifiers.Control)); controller.Pointer(new(OverlayPointerKind.Move, new(120, 80), OverlayMouseButton.Left, OverlayModifiers.Control)); controller.Pointer(new(OverlayPointerKind.Up, new(120, 80), OverlayMouseButton.Left, OverlayModifiers.Control));
        check("ordinary image canvas crop retains its authored geometry and single undo behavior", ordinary.UndoCount == 1 && ordinary.Document.Elements[0].Width == 100 && ordinary.Document.Elements[0].Height == 60 && ordinary.Document.Elements[0].WindowSource is null && ordinary.Document.Elements[0].ImageCrop == new RectD(.1, .2, .5, .6));
    }
    private static void Inspector(Action<string, bool> check)
    {
        var controls = new WindowSourceControls(); var events = new List<WindowSourceAction>(); controls.Requested += events.Add;
        check("window source status controls are inert and hidden without resolved sources", controls.Visibility == Visibility.Collapsed && !Buttons(controls).Any(button => button.IsEnabled));
        Guid target = Guid.NewGuid(); controls.Refresh(new(SourceCount: 1, DisplayName: "Synthetic", CanPause: true, CanRebind: true, CanCrop: true, CanFreeze: true, EditableImageId: target));
        foreach (var button in Buttons(controls)) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("source status controls emit only enabled commands without owning a session", events.SequenceEqual(new[] { WindowSourceAction.Pause, WindowSourceAction.Rebind, WindowSourceAction.Crop, WindowSourceAction.Freeze }));
        var session = ImageSession(); var group = session.CreateEmptyGroup(); session.SetSelection([session.Document.Elements[0].Id]);
        string before = JsonSerializer.Serialize(session.Document); int history = session.UndoCount, freezes = 0, pauses = 0; var inspector = new InspectorWindow(() => session);
        inspector.SetWindowSourcesUi(_ => new(SourceCount: 1, CanPause: true, CanFreeze: true, CanRebind: true, CanCrop: true, EditableImageId: session.Document.Elements[0].Id)); inspector.PrepareForShow();
        inspector.WindowSourcesFreezeRequested += _ => freezes++; inspector.WindowSourcesPauseRequested += (_, _) => pauses++;
        var panel = Field<WindowSourceControls>(inspector, "windowSources"); FindButton(panel, "WindowSource.Freeze").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); FindButton(panel, "WindowSource.Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("Inspector window-source commands do not write descriptors assets or history", freezes == 1 && pauses == 1 && JsonSerializer.Serialize(session.Document) == before && session.UndoCount == history);
        session.EnterScope(group); session.LeaveScope(); session.SetSelection([session.Document.Elements[0].Id]); FindButton(panel, "WindowSource.Freeze").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("a stale source form cannot freeze after a scope round trip", freezes == 1);
        inspector.RefreshWindowSourcesUi(); session.BeginTransaction("foreign"); FindButton(panel, "WindowSource.Freeze").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("a window-source authoring command cannot enter a foreign transaction", freezes == 1 && session.IsTransactionActive); session.Cancel(); SessionWindowCoordinator.Close(inspector);
        var shared = ImageSession(); shared.SetSelection([shared.Document.Elements[0].Id]); var prefab = shared.CreatePrefabFromSelection("Synthetic shared source"); shared.SetSelection([prefab.InstanceId]);
        string sharedBefore = JsonSerializer.Serialize(shared.Document); int sharedHistory = shared.UndoCount, requested = 0; Guid[] received = [];
        var instanceInspector = new InspectorWindow(() => shared); instanceInspector.SetWindowSourcesUi(_ => new(SourceCount: 1, CanPause: true, CanRebind: true, CanCrop: true, OccurrenceCount: 2, ReasonKey: "WindowSource.EditDefinitionHint"));
        instanceInspector.WindowSourcesPauseRequested += (ids, _) => { requested++; received = ids.ToArray(); }; instanceInspector.PrepareForShow(); var instancePanel = Field<WindowSourceControls>(instanceInspector, "windowSources");
        check("instance source summary remains available outside the virtual leaf property panel", instancePanel.Visibility == Visibility.Visible && Field<StackPanel>(instanceInspector, "propertyPanel").Visibility == Visibility.Collapsed && !FindButton(instancePanel, "WindowSource.Rebind").IsEnabled && !FindButton(instancePanel, "WindowSource.Crop").IsEnabled);
        FindButton(instancePanel, "WindowSource.Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("instance runtime pause forwards its host ID without editing the shared definition", requested == 1 && received.SequenceEqual(new[] { prefab.InstanceId }) && shared.UndoCount == sharedHistory && JsonSerializer.Serialize(shared.Document) == sharedBefore); SessionWindowCoordinator.Close(instanceInspector);
    }
    private static void Menu(Action<string, bool> check)
    {
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession)); Set(desktop, "_editor", new EditorController(ImageSession()));
        var menu = (MenuItem)typeof(DesktopSession).GetMethod("CreateWindowSourcesMenu", Hidden)!.Invoke(desktop, null)!;
        check("window-source menu has six explicit commands and no extra tool enum", menu.Items.OfType<MenuItem>().Count() == 6 && menu.Items.OfType<Separator>().Count() == 1 && menu.Items.OfType<MenuItem>().All(item => item.Tag is not EditorTool));
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        check("window-source menu commands stay disabled until the root provides capabilities", menu.Items.OfType<MenuItem>().All(item => !item.IsEnabled));
    }
    private static void Visuals(string output, Action<string, bool> check)
    {
        Directory.CreateDirectory(output);
        foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(locale); AppTheme.Apply(theme);
            var picker = new WindowSourcePickerWindow(_ => Task.FromResult<IReadOnlyList<WindowSourceCandidate>>([Candidate(), Candidate("A second synthetic window for source selection", "document.exe")])); Finish(picker.RefreshCandidatesAsync());
            Save((FrameworkElement)picker.Content, picker.MinWidth - 2, picker.MinHeight - 38, Path.Combine(output, $"window-source-picker-min-{locale}-{theme}.png"));
            var sourceList = Field<ListBox>(picker, "candidates");
            var sourceItem = (ListBoxItem)sourceList.ItemContainerGenerator.ContainerFromIndex(0);
            check(locale + " " + theme + " narrow picker keeps standard search height and application list item template", Field<TextBox>(picker, "search").ActualHeight == PropertyPanelLayout.ControlHeight &&
                ReferenceEquals(sourceList.ItemContainerStyle.BasedOn, picker.FindResource(typeof(ListBoxItem))) &&
                ReferenceEquals(sourceItem.Template, ((Style)picker.FindResource(typeof(ListBoxItem))).Setters.OfType<Setter>().Single(setter => setter.Property == Control.TemplateProperty).Value));
            check(locale + " " + theme + " narrow picker retains a usable result viewport", sourceList.ActualHeight >= 72);
            Save((FrameworkElement)picker.Content, 560, 390, Path.Combine(output, $"window-source-picker-{locale}-{theme}.png"));
            var crop = new WindowSourceCropWindow(Png(), new(.15, .2, .7, .6));
            var cropBody = (FrameworkElement)crop.Content;
            Save(cropBody, crop.MinWidth - 2, crop.MinHeight - 38, Path.Combine(output, $"window-source-crop-min-{locale}-{theme}.png"));
            check(locale + " " + theme + " narrow crop keeps all footer actions inside its content bounds", Buttons(cropBody).All(button =>
                button.MinHeight == PropertyPanelLayout.ControlHeight && button.TransformToAncestor(cropBody).TransformBounds(new Rect(button.RenderSize)).Right <= crop.MinWidth - 2 + .1));
            Save(cropBody, 560, 390, Path.Combine(output, $"window-source-crop-{locale}-{theme}.png"));
            var session = ImageSession(); var inspector = new InspectorWindow(() => session); inspector.SetWindowSourcesUi(_ => new(SourceCount: 1, DisplayName: "Synthetic viewer", StatusKey: "WindowSource.Status.SourceClosed", CanRebind: true, CanCrop: true, CanFreeze: true, EditableImageId: session.Document.Elements[0].Id, OccurrenceCount: 3)); inspector.PrepareForShow();
            Save((FrameworkElement)inspector.Content, 300, 650, Path.Combine(output, $"window-source-inspector-{locale}-{theme}.png")); var panel = Field<WindowSourceControls>(inspector, "windowSources");
            check(locale + " " + theme + " window source actions fit a 300-wide Inspector", panel.ActualWidth is > 0 and <= 300 && Buttons(panel).All(button => button.ActualWidth is > 0 and <= 300));
            check(locale + " " + theme + " source action captions wrap", Buttons(panel).All(button => button.Content is TextBlock { TextWrapping: TextWrapping.Wrap }));
            check(locale + " " + theme + " fake picker crop and Inspector visuals create no native window", new WindowInteropHelper(picker).Handle == 0 && new WindowInteropHelper(crop).Handle == 0 && new WindowInteropHelper(inspector).Handle == 0);
            SessionWindowCoordinator.Close(picker); SessionWindowCoordinator.Close(crop); SessionWindowCoordinator.Close(inspector);
        }
    }
    private static void Save(FrameworkElement element, double width, double height, string path)
    {
        element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasSurface"), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<Button> Buttons(DependencyObject element)
    { if (element is Button button) yield return button; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) foreach (var child in Buttons(VisualTreeHelper.GetChild(element, index))) yield return child; }
    private static Button FindButton(DependencyObject panel, string key) => Buttons(panel).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == L.Get(key));
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, Hidden)!.SetValue(value, field);
}
