using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Production color controls in a nonactivating offscreen native host.</summary>
internal static class HiddenSecondaryPanelsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-secondary-hidden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var findings = new List<(string Name, bool Pass)>();
        var root = new StackPanel { Opacity = 0, IsHitTestVisible = false };
        using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.HiddenColors")
        { PositionX = -10000, PositionY = -10000, Width = 1100, Height = 700,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0 }) { RootVisual = root };
        try
        {
            var library = new ColorLibrary(Path.Combine(folder, "colors.json"));
            var field = new ColorField(ColorRole.Foreground, library);
            var picker = new ColorPickerPanel(ColorRole.Foreground, library);
            root.Children.Add(field); root.Children.Add(picker);
            ShowWindow(source.Handle, 4); Pump();
            var label = Field<TextBlock>(field, "label");
            var hex = Field<TextBox>(picker, "hex");
            var history = Field<Panel>(picker, "history");
            string oldLabel = label.Text, oldHex = hex.Text;
            int oldSwatches = history.Children.Count;
            root.Visibility = Visibility.Collapsed; Pump();
            findings.Add(("hidden color fixture remains loaded without being visible", field.IsLoaded && picker.IsLoaded && !field.IsVisible && !picker.IsVisible));
            field.Value = 0xFF123456;
            picker.Value = 0xFF654321;
            library.Remember(0xFF2468AC); picker.RefreshCollections();
            findings.Add(("hidden color field retains the latest business value without painting", field.Value == 0xFF123456 && label.Text == oldLabel));
            findings.Add(("hidden color picker retains the latest value without rewriting its hex editor", picker.Value == 0xFF654321 && hex.Text == oldHex));
            findings.Add(("hidden color swatch collection is not rebuilt", history.Children.Count == oldSwatches));
            root.Visibility = Visibility.Visible; Pump();
            findings.Add(("color field displays its latest value when shown", label.Text.Contains("123456", StringComparison.OrdinalIgnoreCase)));
            findings.Add(("color picker synchronizes its latest value when shown", hex.Text.Contains("654321", StringComparison.OrdinalIgnoreCase)));
            findings.Add(("color swatches synchronize once the panel is shown", history.Children.Count == library.History.Count));
            Layers();
            Stamps();
            RenderSettings();
            Welcome();
            History();
            Updates();
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            findings.Add(("secondary panel fixture never owns foreground", foreground != Environment.ProcessId));

            void Layers()
            {
                var session = new EditorSession();
                session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30, Name = "Before hidden" });
                var window = new LayersWindow(() => session);
                try
                {
                    ShowOffscreen(window);
                    var list = Field<ListBox>(window, "layers");
                    TextBlock Label() => ((Grid)((ListBoxItem)list.Items[0]).Content).Children.OfType<TextBlock>().Single();
                    string before = Label().Text;
                    window.Hide(); session.Document.Elements[0].Name = "After hidden"; window.Refresh();
                    findings.Add(("hidden layer window does not rewrite its rows", Label().Text == before));
                    window.Show(); Pump();
                    findings.Add(("layer window synchronizes the latest model when shown", Label().Text == "After hidden"));
                }
                finally { DisposeWindow(window); }
            }

            void Stamps()
            {
                var stamps = new StampLibrary(Path.Combine(folder, "stamp-profile"));
                var entry = Task.Run(async () =>
                {
                    await stamps.LoadAsync();
                    var scene = new EditorSession(); scene.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30, Style = new() { Fill = 0xFF2383E2, StrokeWidth = 0 } });
                    var saved = await stamps.SaveSelectionAsync(scene, "Before stamp");
                    await stamps.PinAsync(0, saved.Id);
                    return saved;
                }).GetAwaiter().GetResult();
                var window = new StampLibraryWindow(stamps, () => new EditorSession(), _ => { });
                try
                {
                    ShowOffscreen(window);
                    var list = Field<ListBox>(window, "grid");
                    Image? TilePreview() => list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { Content: StackPanel tile }
                        ? ((Grid)tile.Children[0]).Children.OfType<Image>().Single() : null;
                    Image SlotPreview() => ((Grid)Field<Dictionary<int, Button>>(window, "slotButtons")[0].Content).Children.OfType<Image>().Single();
                    bool WaitForPreviews()
                    {
                        var until = DateTime.UtcNow.AddSeconds(3);
                        do { Pump(); Thread.Sleep(5); }
                        while ((TilePreview()?.Source is null || SlotPreview().Source is null) && DateTime.UtcNow < until);
                        return TilePreview()?.Source is not null && SlotPreview().Source is not null;
                    }
                    findings.Add(("stamp thumbnail fixture primes the real tile and pinned-slot cache", WaitForPreviews()));
                    window.Refresh(); window.UpdateLayout(); Pump();
                    findings.Add(("refreshing a visible stamp library restores cached tile and slot thumbnails", WaitForPreviews()));
                    var oldItems = list.ItemsSource;
                    var previews = Field<CancellationTokenSource>(window, "thumbnails");
                    window.Hide(); Pump();
                    findings.Add(("hiding the stamp library cancels its thumbnail work", previews.IsCancellationRequested));
                    Task.Run(() => stamps.RenameAsync(entry.Id, "After stamp")).GetAwaiter().GetResult(); Pump();
                    findings.Add(("hidden stamp catalog changes preserve existing controls", ReferenceEquals(list.ItemsSource, oldItems)));
                    var input = Field<TextBox>(window, "search");
                    var timer = Field<DispatcherTimer>(window, "searchDelay");
                    input.Text = "After";
                    findings.Add(("hidden stamp search does not start its refresh timer", !timer.IsEnabled));
                    PumpFor(220);
                    findings.Add(("hidden stamp search does not rebuild its catalog", ReferenceEquals(list.ItemsSource, oldItems)));
                    window.Show(); Pump();
                    findings.Add(("stamp library synchronizes the latest catalog and search when shown", list.Items.Cast<StampEntry>().Single().Name == "After stamp" && !ReferenceEquals(list.ItemsSource, oldItems)));
                    findings.Add(("reopening the stamp library restores cached tile and pinned-slot thumbnails", WaitForPreviews()));

                    var shelf = new StampShelfControl(stamps, _ => { });
                    root.Children.Add(shelf); Pump();
                    var slots = Field<StackPanel>(shelf, "slots");
                    var children = slots.Children.Cast<UIElement>().ToArray();
                    var shelfLifetime = Field<CancellationTokenSource>(shelf, "thumbnails").Token;
                    shelf.Visibility = Visibility.Collapsed; Pump();
                    findings.Add(("hidden stamp shelf cancels previews and unsubscribes from the library", shelfLifetime.IsCancellationRequested && !Field<bool>(shelf, "subscribed")));
                    shelf.SetActive(entry.Id); shelf.SetShortcutHintsVisible(false);
                    Task.Run(() => stamps.RenameAsync(entry.Id, "Latest shelf")).GetAwaiter().GetResult(); Pump(); shelf.Refresh();
                    findings.Add(("hidden stamp shelf keeps controls unchanged while retaining new state", children.SequenceEqual(slots.Children.Cast<UIElement>())));
                    shelf.Visibility = Visibility.Visible; Pump();
                    var first = (Button)slots.Children[0];
                    var face = (Grid)first.Content;
                    findings.Add(("stamp shelf synchronizes the latest library name when shown", System.Windows.Automation.AutomationProperties.GetName(first).Contains("Latest shelf")));
                    findings.Add(("stamp shelf restores current active state and shortcut visibility", face.Children.OfType<Border>().Single().Opacity == 1 &&
                        slots.Children.OfType<Button>().Select(button => (Grid)button.Content).SelectMany(content => content.Children.OfType<TextBlock>()).All(hint => hint.Visibility == Visibility.Collapsed)));
                    root.Children.Remove(shelf); Pump();
                }
                finally { window.Close(); Pump(); }
            }

            void RenderSettings()
            {
                var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [OverlayMonitorScope.PrimaryScreen, false], null)!;
                DesktopSession? session = null;
                var section = new StackPanel();
                try
                {
                    session = new DesktopSession(Application.Current, host);
                    Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
                    typeof(DesktopSession).GetMethod("AddRenderSettings", Private)!.Invoke(session, [section]);
                    root.Children.Add(section); Pump();
                    int Count() => (Field<Action?>(session, "PreviewBackendChanged")?.GetInvocationList().Length) ?? 0;
                    findings.Add(("visible rendering settings subscribe to backend changes", Count() == 1));
                    section.Visibility = Visibility.Collapsed; Pump();
                    findings.Add(("hidden rendering settings unsubscribe from backend changes", Count() == 0));
                    section.Visibility = Visibility.Visible; Pump();
                    findings.Add(("rendering settings resume one subscription when shown", Count() == 1));
                }
                finally { root.Children.Remove(section); session?.Dispose(); host.Dispose(); Pump(); }
            }

            void Welcome()
            {
                var welcome = new WelcomeView(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, () => { });
                root.Children.Add(welcome); Pump();
                string oldToggle = welcome.ToggleShortcut.Text;
                welcome.Visibility = Visibility.Collapsed;
                welcome.Refresh(new(6, 0x47), new(6, 0x48));
                findings.Add(("hidden welcome shortcuts retain their UI values", welcome.ToggleShortcut.Text == oldToggle));
                welcome.Visibility = Visibility.Visible; Pump();
                findings.Add(("welcome shortcuts synchronize current bindings when shown", welcome.ToggleShortcut.Text.Contains("G") && welcome.HideShortcut.Text.Contains("H")));
                root.Children.Remove(welcome);
            }

            void History()
            {
                var history = new WorkspaceRecoveryHistory(new FloatCanvas.Storage.RecoveryStore(Path.Combine(folder, "history-profile")));
                var window = new WorkspaceHistoryWindow(history, [new(Path.Combine(folder, "missing-history"), DateTime.UtcNow, false)], regular: true);
                var gate = Field<SemaphoreSlim>(window, "previewGate");
                gate.Wait();
                try
                {
                    ShowOffscreen(window);
                    var list = Field<ListBox>(window, "list");
                    var placeholder = Descendants(list).OfType<TextBlock>().First(value => value.Text == FloatCanvas.Localization.AppText.Get("Recovery.PreviewLoading"));
                    string previous = placeholder.Text;
                    window.Hide(); gate.Release(); PumpFor(200);
                    findings.Add(("hidden history preview completion does not rewrite its placeholder", placeholder.Text == previous));
                    window.Show(); PumpFor(200);
                    findings.Add(("history preview resumes when its row becomes visible", placeholder.Text == FloatCanvas.Localization.AppText.Get("Recovery.NoPreview")));
                }
                finally { if (gate.CurrentCount == 0) gate.Release(); window.Close(); Pump(); }
            }

            void Updates()
            {
                var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var http = new HttpClient(new DeferredHandler(completion));
                using var client = new GitHubUpdateClient(http);
                var window = new AppUpdateWindow("0.2.0", client, _ => Task.CompletedTask, false, autoCheck: false);
                try
                {
                    ShowOffscreen(window);
                    Task request = window.CheckAsync();
                    string status = window.StatusText.Text, version = window.LatestVersionText.Text;
                    bool checkEnabled = window.CheckButton.IsEnabled;
                    window.Hide();
                    completion.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (!request.IsCompleted && DateTime.UtcNow < until) PumpFor(5);
                    request.GetAwaiter().GetResult();
                    findings.Add(("hidden update completion stores its result without writing controls", window.StatusText.Text == status && window.LatestVersionText.Text == version && window.CheckButton.IsEnabled == checkEnabled && !window.IsBusy));
                    window.Show(); Pump();
                    findings.Add(("update window displays its completed result when shown", window.LatestVersionText.Text == "v0.2.0" && window.CheckButton.IsEnabled && window.StatusText.Text == FloatCanvas.Localization.AppText.Get("Updates.UpToDate")));
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally { root.Children.Clear(); SynchronizationContext.SetSynchronizationContext(priorContext); Directory.Delete(folder, true); }
        foreach (var finding in findings) Console.WriteLine($"SECONDARY_HIDDEN_FINDING {finding.Pass}: {finding.Name}");
        foreach (var finding in findings) check(finding.Name, finding.Pass);
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private sealed class DeferredHandler(TaskCompletionSource<HttpResponseMessage> completion) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => completion.Task.WaitAsync(token);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void PumpFor(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Pump(); Thread.Sleep(5); } while (DateTime.UtcNow < until);
    }
    private static void ShowOffscreen(Window window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false; window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -10000;
        window.SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(handle, -20, GetWindowLongPtrW(handle, -20) | 0x08000000);
        };
        window.Show(); Pump();
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        if (foreground == Environment.ProcessId) throw new InvalidOperationException("Fixture unexpectedly owns foreground.");
    }
    private static void DisposeWindow(Window window)
    {
        window.Hide();
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.Dispose();
        Pump();
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint handle, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint handle, int index, nint value);
}
