using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class KeyboardLayoutEditorFeatureTests
{
    internal static void Run(Action<string, bool> check)
    {
        RequirePrivateDesktop();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-keyboard-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ApplicationPaths.Configure(["--profile", Path.Combine(folder, "profile")]); L.Initialize("en");

        Model(check);
        FormEnter(check);
        CaptureOtherDevices(check);
        Editor(app, check);
    }

    private static void CaptureOtherDevices(Action<string, bool> check)
    {
        ushort gamepad = 1 << ((int)PresentationGamepadButton.A - 1);
        var dialog = new KeyboardLayoutEditorWindow(null, readGamepad: slot => slot == 0 ? gamepad : (ushort)0);
        dialog.Show(); Pump();
        var capture = ButtonByKey(dialog, "Presentation.Layout.CaptureKey");
        int originalCount = dialog.Draft.Keys.Count;
        capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var captureGamepad = typeof(KeyboardLayoutEditorWindow).GetMethod("CaptureGamepad", BindingFlags.Instance | BindingFlags.NonPublic)!;
        captureGamepad.Invoke(dialog, null);
        check("capture ignores controller buttons already held at entry", dialog.Draft.Keys.Count == originalCount);
        gamepad = 0; captureGamepad.Invoke(dialog, null);
        gamepad = 1 << ((int)PresentationGamepadButton.A - 1); captureGamepad.Invoke(dialog, null);
        var controllerKey = dialog.Draft.Keys.Single(key => key.DeviceType == PresentationKeyDevice.Gamepad);
        check("a new controller press creates a valid device-specific binding", controllerKey.IsValid() &&
            dialog.SelectedKeyId == controllerKey.Id && !controllerKey.Matches(new(0x41, "A")));
        capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var content = (UIElement)dialog.Content;
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        content.RaiseEvent(press);
        var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
            { RoutedEvent = Mouse.PreviewMouseUpEvent };
        content.RaiseEvent(release);
        var mouseKey = dialog.Draft.Keys.Single(key => key.DeviceType == PresentationKeyDevice.Mouse);
        check("mouse binding captures and consumes the press and release in the editor",
            press.Handled && release.Handled && mouseKey.VirtualKey == 4 && mouseKey.Label == "MB4" && dialog.SelectedKeyId == mouseKey.Id);
        foreach (int delta in new[] { 120, -120 })
        {
            capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            content.RaiseEvent(wheel);
            int code = delta > 0 ? 6 : 7;
            var key = dialog.Draft.Keys.Single(item => item.DeviceType == PresentationKeyDevice.Mouse && item.VirtualKey == code);
            check("capture intercepts wheel input and selects its own direction", wheel.Handled && dialog.SelectedKeyId == key.Id);
        }
        dialog.Hide();
        dialog.Accept();
        check("applying publishes a valid keyboard, mouse and controller layout",
            dialog.ResultLayout is { } result && result.IsValid() && result.Keys.Count == originalCount + 4);
        dialog.Close();
    }

    private static void FormEnter(Action<string, bool> check)
    {
        var numeric = new NumericValueField(1, 16, sliderMaximum: 4);
        var label = new TextBox();
        var paragraph = new TextBox { AcceptsReturn = true };
        var promptInput = new TextBox();
        var search = new TextBox();
        var apply = new Button { IsDefault = true };
        var content = new StackPanel();
        foreach (var child in new UIElement[] { numeric, label, paragraph, promptInput, search, apply }) content.Children.Add(child);
        var window = new Window { Content = content, ShowInTaskbar = false };
        AppInputPolicy.AttachFormSubmit(window, apply);
        AppInputPolicy.AllowPromptSubmit(promptInput);
        int searches = 0;
        AppInputPolicy.AttachSearchEnter(search, () => searches++);
        int applied = 0;
        apply.Click += (_, _) => applied++;
        window.Show(); Pump();
        Keyboard.Focus(numeric.Input);
        check("the numeric input owns keyboard focus before Enter", numeric.Input.IsKeyboardFocused);
        numeric.Input.Text = "4";
        var enter = Return(numeric.Input);
        check("Enter completes the typed numeric field without submitting the form",
            enter.Handled && numeric.Value == 4 && applied == 0 && !numeric.Input.IsKeyboardFocused);
        check("a second plain Enter on the form leaves it open", !Return(window).Handled && applied == 0 && !apply.IsDefault);
        numeric.Input.Text = "44";
        Keyboard.Focus(numeric.Input);
        bool validated = false;
        numeric.ConfirmText = () => { validated = true; return true; };
        check("invalid numeric Enter is consumed before confirmation or form submission",
            Return(numeric.Input).Handled && !validated && applied == 0 && numeric.Input.IsKeyboardFocused &&
            AutomationProperties.GetItemStatus(numeric.Input) == L.Get("ToolOptions.InvalidNumber"));
        Keyboard.Focus(label);
        check("plain Enter confirms single-line text and leaves its editor",
            Return(label).Handled && !label.IsKeyboardFocused && applied == 0);
        Keyboard.Focus(paragraph);
        Return(paragraph);
        check("multiline Enter stays in the text editor and leaves the form open", paragraph.IsKeyboardFocused && applied == 0);
        check("the prompt field leaves Enter available to its default button", !Return(promptInput).Handled);
        Keyboard.Focus(search);
        check("search consumes Enter locally and keeps focus", Return(search).Handled && searches == 1 && search.IsKeyboardFocused);
        numeric.Input.Text = "8";
        check("correcting the value clears its Enter validation state", string.IsNullOrEmpty(AutomationProperties.GetItemStatus(numeric.Input)));
        check("Ctrl+Enter explicitly routes to the form action", Return(numeric.Input, ModifierKeys.Control).Handled);
        Pump();
        check("the explicit submit activates the action once", applied == 1);
        Keyboard.Focus(apply);
        Return(apply);
        Pump();
        check("plain Enter activates the focused submit button", applied == 2);
        window.Close();

        KeyEventArgs Return(UIElement target, ModifierKeys modifiers = ModifierKeys.None)
        {
            using var scope = new ThreadModifierScope(modifiers);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent };
            target.RaiseEvent(key);
            return key;
        }
    }

    private static void Model(Action<string, bool> check)
    {
        var layout = PresentationKeyboardLayout.CreateDefault();
        var a = layout.Keys.Single(key => key.VirtualKey == 0x41);
        a.SecondaryLabel = "！";
        var copy = layout.Clone();
        check("secondary key labels survive a detached layout clone",
            copy.Keys.Single(key => key.VirtualKey == 0x41).SecondaryLabel == "！" && copy.IsValid());
        string json = JsonSerializer.Serialize(layout);
        var restored = JsonSerializer.Deserialize<PresentationKeyboardLayout>(json)!;
        check("secondary key labels persist in the authored layout JSON",
            restored.Keys.Single(key => key.VirtualKey == 0x41).SecondaryLabel == "！");
        var placeholder = PresentationKeyboardLayout.CreatePlaceholder("Shift");
        placeholder.X = 60; placeholder.Y = 60;
        check("a neutral placeholder has a stable custom identity and does not impersonate a physical key",
            placeholder.IsPlaceholder && placeholder.Id.StartsWith("custom:", StringComparison.Ordinal) &&
            placeholder.VirtualKey == 0 && placeholder.ScanCode == 0 && placeholder.IsValid());
        check("a physical key and a neutral placeholder remain distinct layout identities",
            placeholder.Id != a.Id && new PresentationKeyboardLayout { Keys = [a.Clone(), placeholder] }.IsValid());
    }

    private static void Editor(Application app, Action<string, bool> check)
    {
        var source = PresentationKeyboardLayout.CreateDefault();
        var dialog = new KeyboardLayoutEditorWindow(source) { ShowActivated = false };
        dialog.Show(); Pump();
        string aId = source.Keys.Single(key => key.VirtualKey == 0x41).Id;
        dialog.SelectKey(aId);
        var sizeField = Field<NumericValueField>(dialog, "relativeFontSize");
        string sizeName = L.Get("Presentation.Layout.RelativeSize");
        check("relative font size names the input, slider, and drag anchor and associates the painted label",
            AutomationProperties.GetName(sizeField.Input) == sizeName && AutomationProperties.GetName(sizeField.Slider) == sizeName &&
            AutomationProperties.GetName(sizeField.DragAnchor) == sizeName && AutomationProperties.GetLabeledBy(sizeField.Input) is not null &&
            ReferenceEquals(AutomationProperties.GetLabeledBy(sizeField.Input), AutomationProperties.GetLabeledBy(sizeField.Slider)));
        var align = ButtonByKey(dialog, "Presentation.Layout.AlignLeft");
        var spacingButton = ButtonByKey(dialog, "Presentation.Layout.SpaceHorizontal");
        var paste = ButtonByKey(dialog, "Presentation.Layout.PasteKeys");
        check("single selection disables group operations and an empty editor clipboard disables paste",
            !align.IsEnabled && !spacingButton.IsEnabled && !paste.IsEnabled);
        dialog.CopySelectedKeys();
        check("copying selected keys immediately enables paste", paste.IsEnabled);
        var views = Field<Dictionary<string, Thumb>>(dialog, "keyViews");
        string bId = source.Keys.Single(key => key.VirtualKey == 0x42).Id;
        dialog.SelectKey(bId);
        dialog.SelectKey(aId);
        int aZ = Panel.GetZIndex(views[aId]);
        int maximumZ = views.Values.Max(Panel.GetZIndex);
        check("the most recently activated key is painted above every other preview key",
            aZ > Panel.GetZIndex(views[bId]) && aZ == maximumZ);
        dialog.SelectKey(aId);
        var draggedView = views[bId];
        draggedView.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        draggedView.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
        check("starting a drag immediately raises that key above overlapping neighbors",
            Panel.GetZIndex(draggedView) > Panel.GetZIndex(views[aId]));
        dialog.SelectKeys([aId, bId]);
        check("selecting multiple keys enables alignment and spacing", align.IsEnabled && spacingButton.IsEnabled);
        AssertSelectedOnTop();
        typeof(KeyboardLayoutEditorWindow).GetMethod("BuildPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
        AssertSelectedOnTop();
        void AssertSelectedOnTop()
        {
            int lowestSelected = dialog.SelectedKeyIds.Min(id => Panel.GetZIndex(views[id]));
            check("every selected key remains above unselected keys",
                views.Where(pair => !dialog.SelectedKeyIds.Contains(pair.Key)).All(pair => Panel.GetZIndex(pair.Value) < lowestSelected));
        }
        dialog.SelectKey(aId);
        var operations = Descendants(dialog).OfType<Border>()
            .Single(border => AutomationProperties.GetAutomationId(border) == "LayoutOperations");
        var footer = Descendants(dialog).OfType<DockPanel>()
            .Single(panel => AutomationProperties.GetAutomationId(panel) == "LayoutFooter");
        string[] operationKeys =
        [
            "Presentation.Layout.CaptureKey", "Presentation.Layout.AddPlaceholder",
            "Presentation.Layout.CopyKeys", "Presentation.Layout.PasteKeys",
            "Presentation.Layout.DeleteKeys", "Presentation.Layout.AlignLeft",
            "Presentation.Layout.AlignTop", "Presentation.Layout.SpaceHorizontal",
            "Presentation.Layout.ResetKey"
        ];
        var operationButtons = Descendants(operations).OfType<Button>()
            .Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal);
        var footerButtons = Descendants(footer).OfType<Button>()
            .Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal);
        var selectedPanel = Field<StackPanel>(dialog, "properties");
        var selectedButtonKeys = Descendants(selectedPanel).OfType<Button>()
            .Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal);
        check("layout operations are docked above the preview and selected-key panel",
            DockPanel.GetDock(operations) == Dock.Top && operationKeys.All(operationButtons.Contains));
        var operationRow = (WrapPanel)operations.Child;
        var operationGroups = operationRow.Children.OfType<StackPanel>().ToArray();
        double originalWidth = dialog.Width;
        dialog.Width = 800;
        Pump();
        check("layout actions wrap between complete operation groups in a narrow window",
            operationGroups.Length == 9 &&
            operationGroups.All(group => group.Orientation == Orientation.Horizontal) &&
            operationGroups.Select(group => group.TranslatePoint(new Point(), operationRow).Y).Distinct().Count() > 1 &&
            operationGroups.All(group => group.TranslatePoint(new Point(group.ActualWidth, 0), operationRow).X <= operationRow.ActualWidth + .5));
        dialog.Width = originalWidth;
        Pump();
        check("selected-key panel excludes key management and selection commands",
            operationKeys.All(key => !selectedButtonKeys.Contains(key)) &&
            !footerButtons.Where(key => key.Length > 0).Any(selectedButtonKeys.Contains));
        check("bulk visibility and complete reset share the lower action group",
            new[] {
                "Presentation.Layout.Reset", "Presentation.Layout.EnableAll", "Presentation.Layout.DisableAll"
            }.All(footerButtons.Contains) && operationKeys.All(key => !footerButtons.Contains(key)));
        check("layout snapping controls belong to the shared operations area",
            Descendants(operations).OfType<CheckBox>().Contains(Field<CheckBox>(dialog, "snapGrid")) &&
            Descendants(operations).OfType<CheckBox>().Contains(Field<CheckBox>(dialog, "snapGap")) &&
            !Descendants(selectedPanel).OfType<CheckBox>().Any(checkBox =>
                ReferenceEquals(checkBox, Field<CheckBox>(dialog, "snapGrid")) ||
                ReferenceEquals(checkBox, Field<CheckBox>(dialog, "snapGap"))));
        check("classic editor actions use compact icon buttons",
            ButtonByKey(dialog, "Edit.Undo").Content is not string &&
            ButtonByKey(dialog, "Edit.Redo").Content is not string &&
            ButtonByKey(dialog, "Presentation.Layout.CopyKeys").Content is not string &&
            ButtonByKey(dialog, "Presentation.Layout.Reset").Content is not string);
        var coordinateFields = Field<Dictionary<string, NumericValueField>>(dialog, "coordinates");
        check("selected-key numeric fields retain the shared editor with paired XY and size rows",
            coordinateFields.Values.All(field =>
                field.Slider.Visibility == Visibility.Visible && field.Height == PropertyPanelLayout.ControlHeight));
        var xPair = (Grid)coordinateFields["X"].Parent;
        var sizePair = (Grid)coordinateFields["Width"].Parent;
        check("XY and WH are grouped beneath shared Position and Size property labels",
            ReferenceEquals(coordinateFields["Y"].Parent, xPair) &&
            ReferenceEquals(coordinateFields["Height"].Parent, sizePair) &&
            xPair.Parent is Grid positionRow && sizePair.Parent is Grid sizeRow &&
            positionRow.Children.OfType<PropertyLabel>().Single().Text == L.Get("Property.Position") &&
            sizeRow.Children.OfType<PropertyLabel>().Single().Text == L.Get("Property.Size") &&
            Grid.GetColumn(coordinateFields["X"]) == 0 && Grid.GetColumn(coordinateFields["Y"]) == 2 &&
            Grid.GetColumn(coordinateFields["Width"]) == 0 && Grid.GetColumn(coordinateFields["Height"]) == 2 &&
            Descendants(selectedPanel).OfType<NumericValueField>().Count(field => field.IsVisible) == 5);
        string leftShiftId = source.Keys.Single(key => key.VirtualKey == 0xA0).Id;
        var matchingSection = Field<StackPanel>(dialog, "matchingSection");
        var matchRelatedKeys = Field<CheckBox>(dialog, "matchRelatedKeys");
        dialog.SelectKey(leftShiftId);
        check("related physical keys expose an optional matching policy",
            matchingSection.Visibility == Visibility.Visible && matchRelatedKeys.IsEnabled && matchRelatedKeys.IsChecked == false);
        matchRelatedKeys.IsChecked = true;
        matchRelatedKeys.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("related-key matching is authored on the selected key",
            dialog.Draft.Keys.Single(key => key.Id == leftShiftId).MatchRelatedKeys);
        dialog.SelectKey(aId);
        check("ordinary keys do not expose related-key matching",
            matchingSection.Visibility == Visibility.Collapsed);
        dialog.SelectKeys([leftShiftId, bId]);
        check("multi-selection hides the single-key matching policy",
            matchingSection.Visibility == Visibility.Collapsed);
        string capsLockId = source.Keys.Single(key => key.VirtualKey == 0x14).Id;
        var lockIndicatorSection = Field<StackPanel>(dialog, "lockIndicatorSection");
        var showLockIndicator = Field<CheckBox>(dialog, "showLockIndicator");
        dialog.SelectKey(capsLockId);
        check("lock keys expose a default-off persistent lock indicator",
            lockIndicatorSection.Visibility == Visibility.Visible && showLockIndicator.IsChecked == false);
        showLockIndicator.IsChecked = true;
        showLockIndicator.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("the lock indicator can be enabled on an individual lock key",
            dialog.Draft.Keys.Single(key => key.Id == capsLockId).ShowLockIndicator);
        string numLockId = source.Keys.Single(key => key.VirtualKey == 0x90).Id;
        dialog.SelectKeys([capsLockId, numLockId]);
        check("two lock keys expose their mixed indicator state",
            lockIndicatorSection.IsVisible && showLockIndicator.IsChecked is null);
        showLockIndicator.IsChecked = true;
        showLockIndicator.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("one indicator edit applies to both selected locks",
            dialog.Draft.Keys.Where(key => dialog.SelectedKeyIds.Contains(key.Id)).All(key => key.ShowLockIndicator));
        dialog.SelectKeys([aId, bId]);
        var selectedPair = dialog.Draft.Keys.Where(key => dialog.SelectedKeyIds.Contains(key.Id)).ToArray();
        var groupFields = Field<Dictionary<string, NumericValueField>>(dialog, "coordinates");
        check("multi-selection displays the combined position and size",
            Nearly(groupFields["X"].Value, selectedPair.Min(key => key.X)) &&
            Nearly(groupFields["Width"].Value, selectedPair.Max(key => key.X + key.Width) - selectedPair.Min(key => key.X)));
        double selectionLeft = groupFields["X"].Value, selectionWidth = groupFields["Width"].Value;
        groupFields["X"].Input.Text = "-1";
        groupFields["Width"].Input.Text = (selectionWidth * 2).ToString(System.Globalization.CultureInfo.CurrentCulture);
        check("editing the group frame translates and resizes selected keys together",
            selectedPair.All(before =>
            {
                var after = dialog.Draft.Keys.Single(key => key.Id == before.Id);
                return Nearly(after.X, -1 + (before.X - selectionLeft) * 2) && Nearly(after.Width, before.Width * 2);
            }));
        dialog.Undo();
        dialog.Undo();
        check("undo restores the original multi-selection frame",
            Nearly(groupFields["X"].Value, selectionLeft) && Nearly(groupFields["Width"].Value, selectionWidth));
        Field<TextBox>(dialog, "label").Text = "Shared";
        Field<TextBox>(dialog, "secondaryLabel").Text = "Alt";
        check("both text fields edit every selected key",
            dialog.Draft.Keys.Where(key => dialog.SelectedKeyIds.Contains(key.Id)).All(key => key.Label == "Shared" && key.SecondaryLabel == "Alt"));
        dialog.SelectKey(aId);
        check("ordinary keys do not expose the lock indicator property",
            lockIndicatorSection.Visibility == Visibility.Collapsed);
        dialog.SelectKey(leftShiftId);
        dialog.CopySelectedKeys();
        dialog.PasteKeys();
        var duplicatedPhysical = dialog.Draft.Keys.Single(key => key.Id == dialog.SelectedKeyId);
        check("duplicating a physical key into a placeholder clears matching-only properties",
            duplicatedPhysical.IsPlaceholder && !duplicatedPhysical.MatchRelatedKeys &&
            !duplicatedPhysical.ShowLockIndicator && dialog.Draft.IsValid());
        dialog.DeleteSelectedKeys();
        dialog.SelectKey(capsLockId);
        dialog.CopySelectedKeys();
        dialog.PasteKeys();
        var duplicatedLock = dialog.Draft.Keys.Single(key => key.Id == dialog.SelectedKeyId);
        check("duplicating a configured lock key into a placeholder clears its indicator flag",
            duplicatedLock.IsPlaceholder && !duplicatedLock.MatchRelatedKeys &&
            !duplicatedLock.ShowLockIndicator && dialog.Draft.IsValid());
        dialog.DeleteSelectedKeys();
        dialog.SelectKeys([aId, bId]);
        dialog.CopySelectedKeys();
        dialog.PasteKeys();
        var pastedPair = dialog.Draft.Keys.Where(key => dialog.SelectedKeyIds.Contains(key.Id)).ToArray();
        check("pasting multiple keys assigns each copy a distinct non-overlapping position",
            pastedPair.Length == 2 &&
            !(pastedPair[0].X < pastedPair[1].X + pastedPair[1].Width &&
              pastedPair[0].X + pastedPair[0].Width > pastedPair[1].X &&
              pastedPair[0].Y < pastedPair[1].Y + pastedPair[1].Height &&
              pastedPair[0].Y + pastedPair[0].Height > pastedPair[1].Y));
        dialog.DeleteSelectedKeys();
        dialog.SelectKey(aId);
        double zoomBefore = ((ScaleTransform)Field<Canvas>(dialog, "preview").LayoutTransform).ScaleX;
        ButtonByKey(dialog, "Presentation.Layout.ZoomIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("keyboard preview exposes working zoom controls",
            ((ScaleTransform)Field<Canvas>(dialog, "preview").LayoutTransform).ScaleX > zoomBefore);
        double originalX = dialog.Draft.Keys.Single(key => key.Id == aId).X;
        SendKey(dialog, views[aId], Key.Right, ModifierKeys.None);
        check("arrow keys nudge the selection by one grid step", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX + .25));
        check("valid edits expose an unsaved-change status", Field<TextBlock>(dialog, "status").Text == L.Get("Presentation.Layout.Unsaved"));
        SendKey(dialog, ButtonByKey(dialog, "Presentation.Layout.ZoomIn"), Key.Z, ModifierKeys.Control);
        check("window-level Ctrl+Z works outside a key Thumb", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX));
        SendKey(dialog, ButtonByKey(dialog, "Presentation.Layout.ZoomIn"), Key.Y, ModifierKeys.Control);
        check("window-level Ctrl+Y works outside a key Thumb", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX + .25));
        SendKey(dialog, views[aId], Key.Right, ModifierKeys.Shift);
        check("Shift-arrow uses a one-unit coarse nudge", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX + 1.25));
        dialog.Undo();
        check("layout editor Ctrl+Z equivalent restores the previous nudge", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX + .25));
        dialog.Redo();
        check("layout editor redo reapplies the nudge", Nearly(dialog.Draft.Keys.Single(key => key.Id == aId).X, originalX + 1.25));

        int beforeAdd = dialog.Draft.Keys.Count;
        dialog.AddPlaceholder();
        string placeholderId = dialog.SelectedKeyId!;
        check("add placeholder creates one neutral selected key",
            dialog.Draft.Keys.Count == beforeAdd + 1 && dialog.Draft.Keys.Single(key => key.Id == placeholderId).IsPlaceholder);
        Field<TextBox>(dialog, "label").Text = "Macro";
        Field<TextBox>(dialog, "secondaryLabel").Text = "M1";
        check("primary and secondary labels edit the neutral key",
            dialog.Draft.Keys.Single(key => key.Id == placeholderId) is { Label: "Macro", SecondaryLabel: "M1" });
        dialog.CopySelectedKeys(); dialog.PasteKeys();
        string pastedId = dialog.SelectedKeyId!;
        check("pasting a copied physical/neutral key creates a distinct identity",
            pastedId != placeholderId && dialog.Draft.Keys.Single(key => key.Id == pastedId).SecondaryLabel == "M1");
        int beforeDelete = dialog.Draft.Keys.Count; dialog.DeleteSelectedKeys();
        check("delete removes only the selected key", dialog.Draft.Keys.Count == beforeDelete - 1 && dialog.Draft.Keys.All(key => key.Id != pastedId));

        int beforeCapture = dialog.Draft.Keys.Count;
        ButtonByKey(dialog, "Presentation.Layout.CaptureKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SendKey(dialog, dialog, Key.F24, ModifierKeys.None);
        var captured = dialog.Draft.Keys.SingleOrDefault(key => key.VirtualKey == 0x87);
        check("physical-key capture adds the captured VK and scan code",
            dialog.Draft.Keys.Count == beforeCapture + 1 && captured is { IsPlaceholder: false, ScanCode: >= 0 });
        int beforeMedia = dialog.Draft.Keys.Count;
        ButtonByKey(dialog, "Presentation.Layout.CaptureKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SendKey(dialog, dialog, Key.MediaNextTrack, ModifierKeys.None);
        var mediaCaptured = dialog.Draft.Keys.SingleOrDefault(key => key.VirtualKey == 0xB0);
        check("captured media keys retain their extended physical identity",
            dialog.Draft.Keys.Count == beforeMedia + 1 && mediaCaptured is { IsExtended: true });

        var alignSource = DialogKeys(dialog);
        dialog.SelectKeys(alignSource.Take(3).Select(key => key.Id));
        var draft = Field<PresentationKeyboardLayout>(dialog, "draft");
        var selected = draft.Keys.Where(key => dialog.SelectedKeyIds.Contains(key.Id)).ToArray();
        selected[0].Y = 1; selected[1].Y = 3; selected[2].Y = 5;
        ButtonByKey(dialog, "Presentation.Layout.AlignTop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("top alignment moves every selected key to one edge",
            DialogKeys(dialog).Where(key => dialog.SelectedKeyIds.Contains(key.Id)).Select(key => key.Y).Distinct().Count() == 1);

        var spacing = draft.Keys.Take(3).ToArray();
        spacing[0].X = 1; spacing[1].X = 4; spacing[2].X = 9;
        dialog.SelectKeys(spacing.Select(key => key.Id));
        ButtonByKey(dialog, "Presentation.Layout.SpaceHorizontal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var spaced = DialogKeys(dialog).Where(key => dialog.SelectedKeyIds.Contains(key.Id)).OrderBy(key => key.X).ToArray();
        double firstGap = spaced[1].X - (spaced[0].X + spaced[0].Width);
        double secondGap = spaced[2].X - (spaced[1].X + spaced[1].Width);
        check("horizontal spacing distributes equal gaps", Nearly(firstGap, secondGap));

        dialog.SelectKey(aId);
        var target = draft.Keys.Single(key => key.Id == aId);
        target.X = -2; target.Y = -1; target.Width = 2; target.Height = 1.5; target.SecondaryLabel = "changed";
        var toggles = Field<Dictionary<PresentationKeyStyleOverrides, CheckBox>>(dialog, "styleOverrides");
        var toggle = toggles[PresentationKeyStyleOverrides.Size];
        var styleFields = Field<StackPanel>(dialog, "customStyleFields");
        check("four independent style controls follow the geometry fields",
            styleFields.Children.Count == 4 && selectedPanel.Children.IndexOf(styleFields) > selectedPanel.Children.IndexOf((UIElement)sizePair.Parent));
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check("size-only override keeps every unrelated switch off",
            toggles.Where(pair => pair.Key != PresentationKeyStyleOverrides.Size).All(pair => pair.Value.IsChecked == false));
        Field<NumericValueField>(dialog, "relativeFontSize").Input.Text = "140";
        foreach (var pair in toggles.Where(pair => pair.Key != PresentationKeyStyleOverrides.Size))
            pair.Value.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Field<ColorField>(dialog, "keyForeground").SetUserValue(0xFF01AB02);
        Field<ColorField>(dialog, "keyBackground").SetUserValue(0xFF650243);
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var font = Field<FontPickerControl>(dialog, "keyFont");
        var fontChoice = font.Items.OfType<FontPickerControl.FontChoice>().Single(item => item.Name == "Consolas");
        typeof(FontPickerControl).GetMethod("Commit", instancePrivate)!.Invoke(font, [fontChoice, true, false]);
        Pump();
        var styled = dialog.Draft.Keys.Single(key => key.Id == aId);
        var styledView = views[aId];
        check("preview uses the selected key's current font, size and both colors",
            styled.ActiveStyle is { FontFamily: "Consolas", Foreground: 0xFF01AB02, Background: 0xFF650243 } activeStyle &&
            Nearly(activeStyle.ScaleFont(100), 140) &&
            styledView.Template.FindName("KeyCaption", styledView) is TextBlock caption && caption.FontSize > 10 &&
            caption.FontFamily.Source == "Consolas" &&
            styledView.Template.FindName("KeySecondary", styledView) is TextBlock secondaryCaption &&
            secondaryCaption.FontFamily.Source == "Consolas" &&
            styledView.Foreground is SolidColorBrush ink && ink.Color == Color.FromRgb(1, 171, 2) &&
            styledView.Background is SolidColorBrush fill && fill.Color == Color.FromRgb(101, 2, 67));
        check("the font picker displays the selected font as a live sample",
            font.Template.FindName("PART_EditableTextBox", font) is TextBox sample && sample.FontFamily.Source == "Consolas");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var sizeOff = dialog.Draft.Keys.Single(key => key.Id == aId);
        check("disabling only relative size preserves the other overrides and stored size",
            sizeOff.CustomStyle is { } savedStyle && Nearly(savedStyle.ScaleFont(100), 140) &&
            sizeOff.ActiveStyle is { RelativeSize: 0, FontFamily: "Consolas", Foreground: 0xFF01AB02, Background: 0xFF650243 });
        ButtonByKey(dialog, "Presentation.Layout.ResetKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reset = DialogKeys(dialog).Single(key => key.Id == aId);
        check("reset selected key restores its complete standard state",
            JsonSerializer.Serialize(reset) == JsonSerializer.Serialize(source.Keys.Single(key => key.Id == aId)));

        draft = Field<PresentationKeyboardLayout>(dialog, "draft");
        target = draft.Keys.Single(key => key.Id == aId);
        target.X = 2.75;
        dialog.SelectKey(aId);
        var thumb = Field<Dictionary<string, Thumb>>(dialog, "keyViews")[aId];
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(7, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(7, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        double snappedX = DialogKeys(dialog).Single(key => key.Id == aId).X;
        check($"grid snapping moves from 2.75 to 3 (actual {snappedX})", Nearly(snappedX, 3));

        string escId = source.Keys.Single(key => key.VirtualKey == 0x1B).Id;
        var esc = draft.Keys.Single(key => key.Id == escId);
        esc.X = esc.Y = 0;
        dialog.SelectKey(escId);
        Field<CheckBox>(dialog, "snapGrid").IsChecked = false;
        Field<CheckBox>(dialog, "snapGap").IsChecked = false;
        var escView = Field<Dictionary<string, Thumb>>(dialog, "keyViews")[escId];
        escView.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        escView.RaiseEvent(new DragDeltaEventArgs(-12, -9) { RoutedEvent = Thumb.DragDeltaEvent });
        escView.RaiseEvent(new DragCompletedEventArgs(-12, -9, false) { RoutedEvent = Thumb.DragCompletedEvent });
        var negative = DialogKeys(dialog).Single(key => key.Id == escId);
        check("unsnapped dragging crosses both axes at the original Esc position",
            Nearly(negative.X, -.4) && Nearly(negative.Y, -.3) && dialog.Draft.IsValid());
        check("the original keyboard outline stays behind the editable keys",
            Field<System.Windows.Shapes.Rectangle>(dialog, "keyboardOutline") is { IsHitTestVisible: false } outline &&
            Panel.GetZIndex(outline) < Panel.GetZIndex(escView));
        ButtonByKey(dialog, "Presentation.Layout.CaptureKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        nint dialogHandle = new WindowInteropHelper(dialog).Handle;
        SendMessage(dialogHandle, 0x100, 0x1B, 1 << 16);
        SendMessage(dialogHandle, 0x100, 0x1B, (1 << 30) | (1 << 16));
        SendMessage(dialogHandle, 0x101, 0x1B, unchecked((nint)0xC0010000));
        check("native Esc and its repeat are consumed by capture instead of closing the editor",
            dialog.IsVisible && dialog.SelectedKeyId == escId && !Field<bool>(dialog, "capturingKey"));
        int keyCount = dialog.Draft.Keys.Count;
        ButtonByKey(dialog, "Presentation.Layout.CaptureKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SendMessage(dialogHandle, 0x100, 'Q', 0x1E << 16);
        SendMessage(dialogHandle, 0x101, 'Q', unchecked((nint)0xC01E0000));
        check("capturing an AZERTY virtual key selects the existing physical position",
            dialog.SelectedKeyId == aId && dialog.Draft.Keys.Count == keyCount);

        var gapSource = new PresentationKeyboardLayout
        {
            Keys =
            [
                PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x41),
                PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x42)
            ]
        };
        gapSource.Keys[0].X = 2.75; gapSource.Keys[0].Y = 1;
        gapSource.Keys[1].X = 3.75; gapSource.Keys[1].Y = 1;
        var gapDialog = new KeyboardLayoutEditorWindow(gapSource) { ShowActivated = false }; gapDialog.Show(); Pump();
        gapDialog.SelectKey(gapSource.Keys[0].Id); Field<CheckBox>(gapDialog, "snapGap").IsChecked = true;
        thumb = Field<Dictionary<string, Thumb>>(gapDialog, "keyViews")[gapSource.Keys[0].Id];
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(25.5, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(25.5, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        check("key-gap snapping aligns a dragged edge to a nearby neighbour edge",
            Nearly(DialogKeys(gapDialog).Single(key => key.VirtualKey == 0x41).X, 3.75));
        check("applying an overlap retains the moved key above its neighbor",
            gapDialog.Draft.Keys[^1].Id == aId);
        foreach (bool hidden in new[] { false, true })
        {
            var gapDraft = Field<PresentationKeyboardLayout>(gapDialog, "draft");
            gapDraft.Keys.Single(key => key.Id == aId).X = 2.75;
            var other = gapDraft.Keys.Single(key => key.Id == bId);
            other.Y = hidden ? 1 : 5;
            other.Enabled = !hidden;
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(25.5, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(25.5, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            check(hidden ? "spacing snap skips a hidden neighbor" : "spacing snap skips a distant row",
                Nearly(DialogKeys(gapDialog).Single(key => key.VirtualKey == 0x41).X, 3.5));
        }
        var edgeDraft = Field<PresentationKeyboardLayout>(gapDialog, "draft");
        edgeDraft.Keys.Single(key => key.Id == aId).X = 60;
        edgeDraft.Keys.Single(key => key.Id == bId).X = 63;
        gapDialog.SelectKeys(edgeDraft.Keys.Select(key => key.Id));
        SendKey(gapDialog, thumb, Key.Right, ModifierKeys.None);
        check("nudging at the boundary preserves the full group's spacing",
            gapDialog.Draft.Keys.Single(key => key.Id == aId).X == 60 &&
            gapDialog.Draft.Keys.Single(key => key.Id == bId).X == 63);
        SessionWindowCoordinator.Close(gapDialog);

        var placeholderView = Field<Dictionary<string, Thumb>>(dialog, "keyViews")[placeholderId];
        dialog.SelectKey(placeholderId);
        check("editor preview renders a second line for secondary labels",
            placeholderView.Template.FindName("KeySecondary", placeholderView) is TextBlock secondary && secondary.Text == "M1");

        var typingDialog = new KeyboardLayoutEditorWindow(PresentationKeyboardLayout.CreateDefault()) { ShowActivated = false };
        typingDialog.Show(); Pump();
        string typingId = typingDialog.Draft.Keys.Single(key => key.VirtualKey == 0x41).Id;
        typingDialog.SelectKey(typingId);
        var undoStack = (System.Collections.ICollection)Field<object>(typingDialog, "undoStack");
        int undoBeforeTyping = undoStack.Count;
        var typingLabel = Field<TextBox>(typingDialog, "label");
        typingLabel.Text = "M"; typingLabel.Text = "Mo"; typingLabel.Text = "Move";
        check("one label editing session creates one undo record", undoStack.Count == undoBeforeTyping + 1);
        typingDialog.Undo();
        check("undoing that session restores the label before the whole edit",
            typingDialog.Draft.Keys.Single(key => key.Id == typingId).Label == "A");
        SessionWindowCoordinator.Close(typingDialog);

        var zoomDialog = new KeyboardLayoutEditorWindow(PresentationKeyboardLayout.CreateDefault())
            { Width = 800, Height = 480, ShowActivated = false };
        zoomDialog.Show(); Pump();
        var zoomPreview = Field<Canvas>(zoomDialog, "preview");
        var zoomViewport = Field<ScrollViewer>(zoomDialog, "previewViewport");
        var zoomTransform = (ScaleTransform)zoomPreview.LayoutTransform;
        check("keyboard preview opens at a stable one-hundred-percent zoom",
            Nearly(zoomTransform.ScaleX, 1) && Field<TextBlock>(zoomDialog, "zoomStatus").Text == "100%");
        zoomDialog.Width = 1040; zoomDialog.UpdateLayout(); Pump();
        check("resizing the editor never recomputes the current zoom percentage",
            Nearly(zoomTransform.ScaleX, 1) && Field<TextBlock>(zoomDialog, "zoomStatus").Text == "100%");
        ButtonByKey(zoomDialog, "Presentation.Layout.ZoomIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        zoomDialog.UpdateLayout(); Pump();
        double manualScale = zoomTransform.ScaleX;
        zoomDialog.Width = 800; zoomDialog.UpdateLayout(); Pump();
        check("manual zoom remains stable across a second window resize", Nearly(zoomTransform.ScaleX, manualScale));
        string zoomText = Field<TextBlock>(zoomDialog, "zoomStatus").Text;
        string expectedZoomText = Math.Round(manualScale * 100) + "%";
        check("manual zoom text remains synchronized after a resize",
            zoomText == expectedZoomText);
        check("preview scroll range reflects the scaled content exactly once",
            Math.Abs(zoomViewport.ScrollableWidth -
                Math.Max(0, zoomPreview.Width * manualScale - zoomViewport.ViewportWidth)) < 1);
        ButtonByKey(zoomDialog, "Presentation.Layout.Fit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        zoomDialog.UpdateLayout(); Pump();
        double fittedScale = zoomTransform.ScaleX;
        check("Fit is a one-shot action that removes overflow without entering an auto-fit loop",
            fittedScale < manualScale && zoomViewport.ScrollableWidth < .5 && zoomViewport.ScrollableHeight < .5);
        zoomDialog.Width = 1040; zoomDialog.UpdateLayout(); Pump();
        check("changing the window size after Fit preserves the fitted percentage",
            Nearly(zoomTransform.ScaleX, fittedScale) &&
            Field<TextBlock>(zoomDialog, "zoomStatus").Text == Math.Round(fittedScale * 100) + "%");
        ButtonByKey(zoomDialog, "Presentation.Layout.Reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        zoomDialog.UpdateLayout(); Pump();
        check("restoring the standard layout keeps zoom and scrollbar math consistent",
            Nearly(zoomTransform.ScaleX, fittedScale) &&
            Math.Abs(zoomViewport.ScrollableWidth -
                Math.Max(0, zoomPreview.Width * fittedScale - zoomViewport.ViewportWidth)) < 1);
        SessionWindowCoordinator.Close(zoomDialog);

        var recoveryDialog = new KeyboardLayoutEditorWindow(PresentationKeyboardLayout.CreateDefault()) { ShowActivated = false };
        recoveryDialog.Show(); Pump();
        string recoveryId = recoveryDialog.Draft.Keys.Single(key => key.VirtualKey == 0x41).Id;
        recoveryDialog.SelectKey(recoveryId);

        var xField = Field<Dictionary<string, NumericValueField>>(recoveryDialog, "coordinates")["X"];
        double initialX = recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X;
        var recoveryUndo = (System.Collections.ICollection)Field<object>(recoveryDialog, "undoStack");
        int undoBeforeGesture = recoveryUndo.Count;
        xField.BeginGesture();
        xField.Slider.Value = initialX + .25; xField.Slider.Value = initialX + .5; xField.Slider.Value = initialX + .75;
        xField.EndGesture(true);
        check("one continuous numeric gesture creates one undo snapshot",
            recoveryUndo.Count == undoBeforeGesture + 1 &&
            Nearly(recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X, initialX + .75));
        recoveryDialog.Undo();
        check("undoing that numeric gesture restores its complete starting value",
            Nearly(recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X, initialX));
        int undoBeforeTyped = recoveryUndo.Count;
        xField.Input.Text = (initialX + .5).ToString(System.Globalization.CultureInfo.CurrentCulture);
        Pump();
        check("a directly typed coordinate creates one undo snapshot",
            recoveryUndo.Count == undoBeforeTyped + 1 &&
            Nearly(recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X, initialX + .5));
        recoveryDialog.Undo();
        check("undoing a typed coordinate restores its prior value",
            Nearly(recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X, initialX));

        var recoveryViews = Field<Dictionary<string, Thumb>>(recoveryDialog, "keyViews");
        recoveryDialog.SelectKey(recoveryId);
        string status = AutomationProperties.GetItemStatus(recoveryViews[recoveryId]);
        check("preview keys expose enabled and selected state to accessibility clients",
            status.Contains("Enabled", StringComparison.Ordinal) && status.Contains("Selected", StringComparison.Ordinal));

        SendKey(recoveryDialog, recoveryViews[recoveryId], Key.Right, ModifierKeys.None);
        var recoveryLabel = Field<TextBox>(recoveryDialog, "label");
        recoveryLabel.IsUndoEnabled = false;
        SendKey(recoveryDialog, recoveryLabel, Key.Z, ModifierKeys.Control);
        check("Ctrl+Z routes to layout history when a focused text field has no local undo",
            Nearly(recoveryDialog.Draft.Keys.Single(key => key.Id == recoveryDialog.SelectedKeyId).X, initialX));

        ButtonByKey(recoveryDialog, "Presentation.Layout.CaptureKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        recoveryLabel.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, recoveryLabel)
            { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        check("moving focus into a text field cancels an armed physical-key capture",
            !Field<bool>(recoveryDialog, "capturingKey"));
        SessionWindowCoordinator.Close(recoveryDialog);

        var crowded = new PresentationKeyboardLayout
        {
            Keys = Enumerable.Range(1, 128).Select(i => new PresentationKeyboardKey
            {
                VirtualKey = i, ScanCode = i, Label = new string('W', 64), X = (i - 1) % 16, Y = (i - 1) / 16
            }).ToList()
        };
        var crowdedDialog = new KeyboardLayoutEditorWindow(crowded);
        crowdedDialog.Accept();
        check("a keyboard that exceeds the render budget cannot be applied",
            !crowdedDialog.Accepted && !Field<Button>(crowdedDialog, "apply").IsEnabled &&
            Field<TextBlock>(crowdedDialog, "status").Text == L.Get("Presentation.Layout.BudgetExceeded"));
        crowdedDialog.Close();

        SessionWindowCoordinator.Close(dialog);
    }

    private static PresentationKeyboardKey[] DialogKeys(KeyboardLayoutEditorWindow dialog) => dialog.Draft.Keys.ToArray();
    private static bool Nearly(double left, double right) => Math.Abs(left - right) < 1e-8;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Button ButtonByKey(DependencyObject root, string key) =>
        Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == key);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index ++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void SendKey(Window dialog, UIElement target, Key key, ModifierKeys modifiers)
    {
        using var scope = new ThreadModifierScope(modifiers);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) ||
            !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --keyboard-layout-editor-features-only.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
}
