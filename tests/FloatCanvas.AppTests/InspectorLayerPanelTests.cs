using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Peers = System.Windows.Automation.Peers;
using Providers = System.Windows.Automation.Provider;

/// <summary>Detached WPF layout and standard automation behavior; no Show, HWND or injected input.</summary>
internal static class InspectorLayerPanelTests
{
    internal static void Run(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" }) Verify(language, check);
        }
        finally { L.Initialize(preference); }
    }

    private static void Verify(string language, Action<string, bool> check)
    {
        L.Initialize(language);
        var session = new EditorSession();
        session.Add(new() { Kind = ElementKind.Text, Text = "Original", Width = 120, Height = 40 });
        Guid selected = session.Document.Elements.Single().Id;
        var inspector = new InspectorWindow(() => session);
        var expander = Descendants(inspector).OfType<Controls.Expander>().Single();
        var panel = (Controls.StackPanel)expander.Content;
        var layers = (Controls.ListBox)Field(inspector, "layers");
        var properties = (Controls.StackPanel)Field(inspector, "propertyPanel");
        var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
        var actions = Descendants(panel).OfType<Controls.Button>().ToArray();
        const string draft = "Unsubmitted\n保留草稿";
        fields["Text"].Text = draft;
        string documentBefore = JsonSerializer.Serialize(session.Document);
        int undoBefore = session.UndoCount, redoBefore = session.RedoCount;
        var peer = new Peers.ExpanderAutomationPeer(expander);
        var pattern = (Providers.IExpandCollapseProvider)peer.GetPattern(Peers.PatternInterface.ExpandCollapse);
        void Check(string name, bool condition) => check(language + " layer panel " + name, condition);
        Check("starts expanded with a localized accessible header", expander.IsExpanded && peer.GetName() == L.Get("Inspector.Layers") &&
            expander.Header is Controls.TextBlock header && header.Text == L.Get("Inspector.Layers"));
        Check("contains the list, hint and all six existing layer actions", Descendants(panel).Contains(layers) && actions.Length == 6 &&
            Descendants(panel).OfType<Controls.TextBlock>().Any(text => text.Text == L.Get("Inspector.LayersHint")));
        // A never-shown Window can suspend layout for its subtree. Measure this same actual
        // Expander under a detached layout root, then return it to its unchanged inspector slot.
        var body = (Controls.Panel)Wpf.LogicalTreeHelper.GetParent(expander);
        int index = body.Children.IndexOf(expander);
        body.Children.Remove(expander);
        var layoutRoot = new Controls.Border { Child = expander };
        try
        {
            bool templateApplied = expander.ApplyTemplate();
            layoutRoot.Measure(new Wpf.Size(280, double.PositiveInfinity));
            double expandedHeight = layoutRoot.DesiredSize.Height;
            var expandSite = expander.Template?.FindName("ExpandSite", expander) as Wpf.FrameworkElement;
            Console.WriteLine($"LAYER_EXPANDER_LAYOUT {language}: applied={templateApplied}, templatePresent={expander.Template is not null}, expandedHeight={expandedHeight}, site={expandSite?.Visibility}, visibility={expander.Visibility}, measureValid={expander.IsMeasureValid}");
            Check("has a nonzero expanded layout baseline with its real default template", expander.Template is not null && expandedHeight > 0);
            pattern.Collapse();
            inspector.Refresh();
            Console.WriteLine($"LAYER_EXPANDER_LAYOUT {language}: beforeIdle state={pattern.ExpandCollapseState}, site={expandSite?.Visibility}");
            bool reachedIdle = ProcessPendingLayout();
            layoutRoot.UpdateLayout();
            expander.InvalidateMeasure(); layoutRoot.InvalidateMeasure(); layoutRoot.Measure(new Wpf.Size(280, double.PositiveInfinity));
            Console.WriteLine($"LAYER_EXPANDER_LAYOUT {language}: afterIdle={reachedIdle}, collapsedHeight={layoutRoot.DesiredSize.Height}, state={pattern.ExpandCollapseState}, site={expandSite?.Visibility}");
            Check("finishes pending template updates within the bounded dispatcher turn", reachedIdle);
            Check("supports the standard accessible collapse pattern", !expander.IsExpanded && pattern.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Collapsed);
            Check("collapses the actual default template content site", expandSite?.Visibility == Wpf.Visibility.Collapsed);
            Check("releases layout space when collapsed", layoutRoot.DesiredSize.Height < expandedHeight);
        }
        finally
        {
            layoutRoot.Child = null;
            body.Children.Insert(index, expander);
        }
        Check("keeps properties and their unsubmitted draft available", properties.Visibility == Wpf.Visibility.Visible && fields["Text"].Text == draft);
        Check("leaves document selection and history unchanged", JsonSerializer.Serialize(session.Document) == documentBefore &&
            session.Selection.SetEquals([selected]) && session.UndoCount == undoBefore && session.RedoCount == redoBefore);
        pattern.Expand(); inspector.Refresh();
        Check("reopens the same controls and draft", expander.IsExpanded && ReferenceEquals(panel, expander.Content) &&
            ReferenceEquals(layers, Descendants(panel).OfType<Controls.ListBox>().Single()) &&
            actions.SequenceEqual(Descendants(panel).OfType<Controls.Button>()) && fields["Text"].Text == draft);
        Check("reopening does not add a history step or change selection", session.UndoCount == undoBefore && session.RedoCount == redoBefore && session.Selection.SetEquals([selected]));
        Check("never creates a native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && !inspector.IsVisible);
    }

    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static bool ProcessPendingLayout()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        bool reachedIdle = false;
        var timeout = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            { Interval = TimeSpan.FromSeconds(1) };
        timeout.Tick += (_, _) => frame.Continue = false;
        var idle = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { reachedIdle = true; frame.Continue = false; }));
        timeout.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); idle.Abort(); }
        return reachedIdle;
    }
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject parent)
    {
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(parent).OfType<Wpf.DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
