using System.Diagnostics;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;

/// <summary>Exercises the actual font popup layout without foreground activation or physical input.</summary>
internal static class FontLayoutRegressionTests
{
    private sealed record LayoutSample(string Phase, double Milliseconds, int FontCount, string? ItemsHost,
        int RealizedChildren, double ViewportHeight, double MinimumRowHeight, int ViewportContainerBudget,
        bool PopupOpen, bool PopupVisible, int FormattedFontNames, bool FixtureOwnsForeground);

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_FONT_LAYOUT_DIR") ?? Path.GetFullPath(Path.Combine(".artifacts", "font-layout-regression"));
        Directory.CreateDirectory(output);
        var findings = new List<(string Name, bool Pass)>();
        foreach (bool enhanced in new[] { false, true })
        {
            string backend = enhanced ? "enhanced" : "ordinary";
            var root = new StackPanel { Opacity = 0, IsHitTestVisible = false };
            using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.FontLayout." + backend)
            {
                PositionX = -10000, PositionY = -10000, Width = 440, Height = 150,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0
            }) { RootVisual = root };
            using var scope = enhanced ? new OverlayWindowScope(source.Handle, () => true) : null;
            AppTheme.Apply("light");
            var picker = (FontPickerControl)ToolOptionPreviews.FontPicker();
            picker.SelectFont("Arial");
            var sibling = new TextBlock { Text = "Other tool properties", FontSize = 14, Focusable = true };
            root.Children.Add(picker); root.Children.Add(sibling);
            ShowWindow(source.Handle, 4);
            var samples = new List<LayoutSample>();
            try
            {
                Timed("initial-owner-layout", () => root.UpdateLayout());
                picker.ApplyTemplate();
                object popup = picker.Template.FindName("PART_Popup", picker)!;
                UIElement popupChild = popup switch
                {
                    Popup ordinary => ordinary.Child,
                    OverlayPopup overlay => overlay.Child!,
                    _ => throw new InvalidOperationException("Unexpected production font popup: " + popup?.GetType().FullName)
                };
                popupChild.Opacity = 0;
                popupChild.IsHitTestVisible = false;
                int popupOpenings = 0;
                if (popup is Popup ordinaryPopup) ordinaryPopup.Opened += (_, _) => popupOpenings++;
                else if (popup is OverlayPopup overlayPopup) overlayPopup.Opened += (_, _) => popupOpenings++;
                findings.Add(($"{backend} uses its actual production popup host", enhanced ? popup is OverlayPopup : popup is Popup));

                Timed("first-full-list-open", () => picker.IsDropDownOpen = true);
                ObserveBounded("full font list");
                var listScroll = Descendants(popupChild).OfType<ScrollViewer>().First();
                Timed("scroll-full-list-to-end", listScroll.ScrollToEnd);
                findings.Add(($"{backend} full font list can scroll to its last font", listScroll.VerticalOffset > 0 &&
                    picker.ItemContainerGenerator.ContainerFromIndex(picker.Items.Count - 1) is FrameworkElement));
                ObserveBounded("scrolled full font list");
                listScroll.ScrollToHome(); Pump();
                FocusEditor("initial filtering");
                Timed("filter-list", () => picker.ApplySearch("Segoe"));
                findings.Add(($"{backend} filters the actual open font list", picker.Items.Count > 0 && picker.Items.Count < samples.Single(sample => sample.Phase == "first-full-list-open").FontCount));
                ObserveBounded("filtered font list");
                Timed("select-and-close", CommitCandidateInput);
                findings.Add(($"{backend} font selection closes its actual popup", !picker.IsDropDownOpen && !popupChild.IsVisible));

                // This mirrors the subsequent tool-panel refresh: reset the font view,
                // collapse the font row and update another control on the same dispatcher.
                Timed("prepare-clean-closed-selection", () => picker.SelectFont("Arial"));
                var search = (DispatcherTimer)typeof(FontPickerControl).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                findings.Add(($"{backend} repeated refresh starts from a clean closed selection", !picker.IsDropDownOpen && !search.IsEnabled && picker.Text == "Arial"));
                int closedResets = 0;
                void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
                {
                    if (args.Action == NotifyCollectionChangedAction.Reset) closedResets++;
                }
                ((INotifyCollectionChanged)picker.Items).CollectionChanged += CollectionChanged;
                try
                {
                    for (int index = 0; index < 6; index++)
                    {
                        int iteration = index;
                        Timed("closed-other-tool-layout-" + index, () =>
                        {
                            picker.SelectFont("Arial");
                            picker.Visibility = Visibility.Collapsed;
                            sibling.Text = "Other tool properties " + iteration;
                            root.InvalidateMeasure(); root.UpdateLayout();
                        });
                    }
                }
                finally { ((INotifyCollectionChanged)picker.Items).CollectionChanged -= CollectionChanged; }
                Console.WriteLine($"FONT_LAYOUT_CLOSED_RESETS backend={backend} count={closedResets}");
                findings.Add(($"{backend} clean same-font refresh never resets the closed font list", closedResets == 0));
                picker.Visibility = Visibility.Visible;
                Timed("reopen-full-list", () => picker.IsDropDownOpen = true);
                ObserveBounded("reopened full font list");
                picker.IsDropDownOpen = false; Pump();
                VisibilityLifecycle("collapsed-ancestor", Visibility.Collapsed);
                VisibilityLifecycle("hidden-ancestor", Visibility.Hidden);
                Keyboard.ClearFocus(); picker.SelectFont("Arial"); Pump();
                var selectionInput = FocusEditor("visible font selection commit");
                selectionInput.Text = "Segoe"; PumpFor(TimeSpan.FromMilliseconds(350));
                SetFocus(source.Handle); Keyboard.Focus(selectionInput); Pump();
                findings.Add(($"{backend} font selection starts with a visible popup and focused editor", picker.IsVisible &&
                    picker.IsDropDownOpen && popupChild.IsVisible && ReferenceEquals(Keyboard.FocusedElement, selectionInput)));
                int openingsBeforeCommit = popupOpenings;
                CommitCandidateInput();
                string committedText = picker.Text;
                Timed("visible-focused-selection-commit-delay", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                Console.WriteLine("FONT_LAYOUT_COMMIT " + JsonSerializer.Serialize(new { Backend=backend, popupOpenings, openingsBeforeCommit, search.IsEnabled, picker.IsDropDownOpen, PopupVisible=popupChild.IsVisible, picker.Text, committedText, Focus=Keyboard.FocusedElement?.GetType().Name, SameEditor=ReferenceEquals(Keyboard.FocusedElement, selectionInput) }));
                findings.Add(($"{backend} committed font selection never reopens the popup while editor stays focused", popupOpenings == openingsBeforeCommit &&
                    !search.IsEnabled && !picker.IsDropDownOpen && !popupChild.IsVisible && picker.Text == committedText &&
                    ReferenceEquals(Keyboard.FocusedElement, selectionInput)));
                Keyboard.ClearFocus(); picker.SelectFont("Arial"); Pump();
                var finalInput = FocusEditor("lost-focus pending search");
                finalInput.Text = "Segoe";
                Keyboard.Focus(sibling);
                Timed("pending-search-after-editor-focus-leaves", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                findings.Add(($"{backend} pending search cannot reopen the popup after editor focus leaves", !search.IsEnabled && !picker.IsDropDownOpen && !popupChild.IsVisible));
                Keyboard.ClearFocus();
                findings.Add(($"{backend} never activates a fixture window", samples.All(sample => !sample.FixtureOwnsForeground) && !FixtureOwnsForeground()));

                TextBox FocusEditor(string name)
                {
                    picker.ApplyTemplate();
                    var input = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
                    SetFocus(source.Handle);
                    Keyboard.Focus(sibling); Pump();
                    Keyboard.Focus(input); Pump();
                    findings.Add(($"{backend} {name} uses a loaded visible focused editor", picker.IsLoaded && picker.IsVisible &&
                        input.IsKeyboardFocusWithin && picker.IsKeyboardFocusWithin && ReferenceEquals(Keyboard.FocusedElement, input)));
                    return input;
                }

                void CommitCandidateInput()
                {
                    // A highlighted candidate is already SelectedItem during search;
                    // Enter is the user commit, including when it is the first match.
                    picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }

                void VisibilityLifecycle(string name, Visibility hidden)
                {
                    root.Visibility = Visibility.Visible; picker.Visibility = Visibility.Visible;
                    Keyboard.ClearFocus(); picker.SelectFont("Arial"); Pump();
                    var input = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
                    SetFocus(source.Handle); Keyboard.Focus(sibling); Pump();
                    findings.Add(($"{backend} {name} programmatic text starts with sibling focus", ReferenceEquals(Keyboard.FocusedElement, sibling) && !input.IsKeyboardFocusWithin));
                    input.Text = "Segoe";
                    findings.Add(($"{backend} {name} programmatic unfocused text cannot start a search", !input.IsKeyboardFocusWithin && !search.IsEnabled));
                    int openingsBeforeHide = popupOpenings;
                    root.Visibility = hidden; Pump();
                    findings.Add(($"{backend} {name} programmatic path remains loaded while hidden", picker.IsLoaded && !picker.IsVisible));
                    Timed(name + "-unfocused-text-delay", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                    findings.Add(($"{backend} {name} unfocused text never opens a hidden popup", popupOpenings == openingsBeforeHide && !search.IsEnabled && !picker.IsDropDownOpen && !popupChild.IsVisible));
                    picker.IsDropDownOpen = false;
                    root.Visibility = Visibility.Visible; Keyboard.ClearFocus(); picker.SelectFont("Arial"); Pump();

                    input = FocusEditor(name + " pending search");
                    input.Text = "Segoe";
                    findings.Add(($"{backend} {name} starts a real pending input search", search.IsEnabled));
                    openingsBeforeHide = popupOpenings;
                    root.Visibility = hidden; Pump();
                    findings.Add(($"{backend} {name} keeps the font picker loaded while hiding it", picker.IsLoaded && !picker.IsVisible));
                    Timed(name + "-pending-delay", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                    findings.Add(($"{backend} {name} cancels pending search without an orphan popup", popupOpenings == openingsBeforeHide && !search.IsEnabled && !picker.IsDropDownOpen && !popupChild.IsVisible));
                    picker.IsDropDownOpen = false; Keyboard.ClearFocus();

                    input.Text = "";
                    input.Text = "Arial";
                    findings.Add(($"{backend} {name} programmatic hidden text cannot start a search", !search.IsEnabled));
                    Timed(name + "-hidden-text-delay", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                    findings.Add(($"{backend} {name} hidden text cannot reopen the font popup", !search.IsEnabled && !picker.IsDropDownOpen && !popupChild.IsVisible));
                    picker.IsDropDownOpen = false;

                    root.Visibility = Visibility.Visible; picker.SelectFont("Arial"); Pump();
                    int allFonts = picker.Items.Count;
                    input = FocusEditor(name + " restored filtering");
                    input.Text = "Segoe";
                    Timed(name + "-visible-focused-filter-delay", () => PumpFor(TimeSpan.FromMilliseconds(350)));
                    findings.Add(($"{backend} {name} restoration retains delayed visible-editor filtering", picker.IsDropDownOpen &&
                        popupChild.IsVisible && picker.Items.Count > 0 && picker.Items.Count < allFonts && !search.IsEnabled));
                    picker.IsDropDownOpen = false; Pump();
                    SetFocus(source.Handle); Keyboard.Focus(sibling); Pump();
                }

                void Timed(string phase, Action action)
                {
                    var clock = Stopwatch.StartNew(); action(); Pump(); clock.Stop();
                    var state = State(phase, clock.Elapsed.TotalMilliseconds);
                    samples.Add(state);
                    Console.WriteLine("FONT_LAYOUT " + JsonSerializer.Serialize(new { Backend = backend, Sample = state }));
                }

                LayoutSample State(string phase, double milliseconds)
                {
                    var child = picker.Template?.FindName("PART_Popup", picker) switch
                    {
                        Popup ordinary => ordinary.Child,
                        OverlayPopup overlay => overlay.Child,
                        _ => null
                    };
                    var host = child is null ? null : Descendants(child).OfType<Panel>().FirstOrDefault(panel => panel.IsItemsHost && ReferenceEquals(ItemsControl.GetItemsOwner(panel), picker));
                    var scroll = child is null ? null : Descendants(child).OfType<ScrollViewer>().FirstOrDefault();
                    int children = host is null ? 0 : VisualTreeHelper.GetChildrenCount(host);
                    var rows = host is null ? [] : Enumerable.Range(0, children).Select(index => VisualTreeHelper.GetChild(host, index)).OfType<FrameworkElement>().ToArray();
                    double rowHeight = rows.Select(row => row.ActualHeight > 0 ? row.ActualHeight : row.DesiredSize.Height).Where(height => height > 0).DefaultIfEmpty(1).Min();
                    double viewport = scroll?.ActualHeight ?? 0;
                    // A viewport plus one cache page on each side is generous enough
                    // for WPF recycling while still catching whole-catalog realization.
                    int budget = (int)Math.Ceiling(viewport / Math.Max(1, rowHeight)) * 3 + 4;
                    return new(phase, milliseconds, picker.Items.Count, host?.GetType().Name, children, viewport, rowHeight, budget,
                        picker.IsDropDownOpen, child?.IsVisible == true,
                        rows.SelectMany(Descendants).OfType<TextBlock>().Select(text => text.FontFamily.Source).Distinct().Count(), FixtureOwnsForeground());
                }

                void ObserveBounded(string name)
                {
                    var sample = State(name, 0);
                    findings.Add(($"{backend} {name} has an attached measurable items host", sample.ItemsHost is not null && sample.ViewportHeight > 0 && sample.RealizedChildren > 0));
                    findings.Add(($"{backend} {name} realizes only a viewport-bounded set of font containers", sample.RealizedChildren <= sample.ViewportContainerBudget));
                    var host = Descendants(popupChild).OfType<Panel>().First(panel => panel.IsItemsHost && ReferenceEquals(ItemsControl.GetItemsOwner(panel), picker));
                    var containers = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(host))
                        .Select(index => VisualTreeHelper.GetChild(host, index)).OfType<ComboBoxItem>().ToArray();
                    findings.Add(($"{backend} {name} renders font names in their own font families", containers.Length > 0 && containers.All(container =>
                        container.Tag is string font && Descendants(container).OfType<TextBlock>().Any(text => text.Text.Length > 0 && text.FontFamily.Source == font))));
                    findings.Add(($"{backend} {name} exposes readable standard-font name tooltips", containers.Length > 0 && containers.All(container =>
                        container.Tag is string font && container.ToolTip is ToolTip { Content: TextBlock hint } &&
                        hint.FontFamily.Source == PropertyPanelLayout.LabelFontFamily.Source && hint.Text.Contains(font, StringComparison.Ordinal))));
                }
            }
            finally
            {
                Keyboard.ClearFocus();
                picker.IsDropDownOpen = false;
                scope?.ClosePopups();
                File.WriteAllText(Path.Combine(output, backend + ".json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                root.Children.Clear(); Pump();
            }
        }
        findings.Add(("font layout regression leaves all fixture windows out of the foreground", !FixtureOwnsForeground()));
        foreach (var finding in findings)
        {
            Console.WriteLine($"FONT_LAYOUT_FINDING {finding.Pass}: {finding.Name}");
        }
        Console.WriteLine("Font layout evidence: " + output);
        foreach (var finding in findings) check(finding.Name, finding.Pass);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static bool FixtureOwnsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
        return process == Environment.ProcessId;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
}
