using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;
using TextAlignment = System.Windows.TextAlignment;

namespace FloatCanvas.App;

internal sealed class ShortcutKeyboardPreviewWindow : Window
{
    internal sealed record Entry(CommandChord Chord, string Name, string Context);
    private const double Unit = 42, Gap = 4;
    private readonly ILookup<CommandChord, Entry> entries;
    private readonly Dictionary<OverlayModifiers, CheckBox> modifierToggles = [];
    private readonly List<(PresentationKeyboardKey Key, Button Button, TextBlock Command)> keyViews = [];
    private readonly StackPanel details = new();
    private int? selectedKey;

    internal ShortcutKeyboardPreviewWindow(IEnumerable<Entry> bindings, Window owner)
    {
        entries = bindings.ToLookup(entry => entry.Chord);
        Title = L.Get("Shortcuts.KeyboardPreview");
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1050; Height = 480; MinWidth = 640; MinHeight = 340;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };

        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 10) };
        foreach (var (flag, label) in new[] { (OverlayModifiers.Control, "Ctrl"), (OverlayModifiers.Shift, "Shift"), (OverlayModifiers.Alt, "Alt") })
        {
            var toggle = new CheckBox { Content = label, Margin = new(0, 0, 15, 0) };
            modifierToggles.Add(flag, toggle);
            toggle.Checked += (_, _) => Refresh();
            toggle.Unchecked += (_, _) => Refresh();
            modifiers.Children.Add(toggle);
        }
        var hint = new TextBlock { Text = L.Get("Shortcuts.KeyboardPreviewHint"), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        modifiers.Children.Add(hint);
        DockPanel.SetDock(modifiers, Dock.Top); body.Children.Add(modifiers);

        var layout = PresentationKeyboardLayout.CreateDefault();
        var keyboard = new Canvas
        {
            Width = layout.Keys.Max(key => key.X + key.Width) * Unit,
            Height = layout.Keys.Max(key => key.Y + key.Height) * Unit
        };
        foreach (var key in layout.Keys)
        {
            var physicalLabel = new TextBlock
            {
                Text = key.Label, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                FontSize = key.Width <= 1 && key.Label.Length >= 4 ? 10 : 13
            };
            var command = new TextBlock { FontSize = 9, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            command.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
            var labels = new StackPanel { Children = { physicalLabel, command } };
            var button = new Button
            {
                Content = labels, Width = key.Width * Unit - Gap, Height = key.Height * Unit - Gap,
                MinWidth = 0, MinHeight = 0, Padding = new(3, 1, 3, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = key.VirtualKey
            };
            button.Click += (_, _) =>
            {
                var modifier = KeyModifier(key.VirtualKey);
                if (modifierToggles.TryGetValue(modifier, out var toggle))
                    toggle.IsChecked = toggle.IsChecked != true;
                else { selectedKey = key.VirtualKey; Refresh(); }
            };
            Canvas.SetLeft(button, key.X * Unit + Gap / 2);
            Canvas.SetTop(button, key.Y * Unit + Gap / 2);
            keyboard.Children.Add(button);
            keyViews.Add((key, button, command));
        }
        var frame = new RoundedSurfaceBorder
        {
            Child = new Viewbox { Child = keyboard, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
            BorderThickness = new(1), CornerRadius = new(8), Padding = new(8)
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        DockPanel.SetDock(frame, Dock.Top); body.Children.Add(frame);
        body.Children.Add(new ScrollViewer
        {
            Content = details, Margin = new(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true; Close();
        };
        Refresh();
    }

    private void Refresh()
    {
        OverlayModifiers modifiers = OverlayModifiers.None;
        foreach (var (flag, toggle) in modifierToggles)
            if (toggle.IsChecked == true) modifiers |= flag;
        foreach (var (key, button, command) in keyViews)
        {
            var matches = entries[new(key.VirtualKey, modifiers)];
            bool selected = key.VirtualKey == selectedKey;
            bool occupied = matches.Any();
            bool modifierActive = (modifiers & KeyModifier(key.VirtualKey)) != 0;
            button.SetResourceReference(Control.BackgroundProperty, selected || modifierActive ? "CanvasPressed" : occupied ? "CanvasHover" : "CanvasInput");
            button.SetResourceReference(Control.BorderBrushProperty, occupied || selected || modifierActive ? "CanvasAccent" : "CanvasBorder");
            command.Text = matches.FirstOrDefault()?.Name ?? "";
            command.Visibility = occupied ? Visibility.Visible : Visibility.Collapsed;
            button.ToolTip = occupied ? string.Join("\n", matches.Select(entry => entry.Name + " · " + entry.Context)) : null;
            AutomationProperties.SetName(button, occupied ? key.Label + " · " + button.ToolTip : key.Label);
        }

        details.Children.Clear();
        if (selectedKey is not { } selectedCode)
        {
            details.Children.Add(new TextBlock { Text = L.Get("Shortcuts.KeyboardPreviewSelect"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var chord = new CommandChord(selectedCode, modifiers);
        details.Children.Add(PropertyPanelLayout.SectionTitle(chord.ToString()));
        var selectedEntries = entries[chord];
        foreach (var entry in selectedEntries)
            details.Children.Add(new TextBlock { Text = entry.Name + " · " + entry.Context, Margin = new(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (!selectedEntries.Any())
            details.Children.Add(new TextBlock { Text = L.Get("Shortcuts.Unbound"), Margin = new(0, 4, 0, 0) });
    }

    private static OverlayModifiers KeyModifier(int virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => OverlayModifiers.Shift,
        0xA2 or 0xA3 => OverlayModifiers.Control,
        0xA4 or 0xA5 => OverlayModifiers.Alt,
        _ => OverlayModifiers.None
    };
}
