using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class NumberSequenceTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-number-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Formats(check);
            ToolAndStorage(folder, check);
            CounterControl(folder, check);
            LayerEdits(check);
            Pixels(check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Formats(Action<string, bool> check)
    {
        foreach (var (format, pairs) in new (NumberFormat, (int, string)[])[]
        {
            (NumberFormat.Decimal, [(0,"0"),(-9,"-9"),(int.MinValue,"-2147483648")]),
            (NumberFormat.LeadingZero, [(0,"00"),(4,"04"),(-8,"-08"),(100,"100")]),
            (NumberFormat.LowerLatin, [(0,"0"),(1,"a"),(26,"z"),(27,"aa"),(52,"az"),(53,"ba"),(703,"aaa")]),
            (NumberFormat.UpperLatin, [(-2,"-2"),(26,"Z"),(27,"AA"),(702,"ZZ")]),
            (NumberFormat.LowerRoman, [(4,"iv"),(9,"ix"),(49,"xlix")]),
            (NumberFormat.UpperRoman, [(-7,"-7"),(0,"0"),(944,"CMXLIV"),(3999,"MMMCMXCIX"),(4000,"4000")]),
            (NumberFormat.Chinese, [(0,"零"),(-12,"负十二"),(21,"二十一"),(101,"一百零一"),(1010,"一千零一十"),
                (10010,"一万零一十"),(100001000,"一亿零一千"),(int.MinValue,"负二十一亿四千七百四十八万三千六百四十八")]),
            (NumberFormat.ChineseFinancial, [(0,"零"),(2,"贰"),(-21,"负贰拾壹"),(101,"壹佰零壹"),(100000001,"壹亿零壹")])
        })
            foreach (var (number, expected) in pairs)
                check($"{format} {number} displays {expected}", NumberSequence.Format(number, format) == expected);

        var list = new NumberSequenceSettings { Format = NumberFormat.Custom, Items = ["甲", "乙", "丙", "丁"] };
        var repeat = list with { Cycle = true };
        check("finite custom sequence uses the actual integer on both sides", new[] { -2, 0, 1, 4, 5 }.Select(list.Label).SequenceEqual(["-2", "0", "甲", "丁", "5"]));
        check("cyclic list wraps signed numbers in both directions", new[] { -1, 0, 1, 4, 5 }.Select(repeat.Label).SequenceEqual(["丙", "丁", "甲", "丁", "甲"]));
        check("custom anchor is independent of number-tool starting value", (list with { FirstNumber = -2 }).Label(0) == "丙");
        check("minimum signed number can cycle from maximum signed anchor", (repeat with { FirstNumber = int.MaxValue }).Label(int.MinValue) == "乙");
        check("empty custom sequence consistently uses decimal", (repeat with { Items = [] }).Label(-3) == "-3");
    }

    private static void ToolAndStorage(string folder, Action<string, bool> check)
    {
        var session = new EditorSession(new SceneDocument { Viewport = new(0, 0, 1200, 720), NextNumber = 1 });
        var editor = new EditorController(session);
        editor.ApplyParameters(ToolPresetLibrary.OfficialDefault(EditorTool.Number));
        string libraryFile = Path.Combine(folder, "presets.json"), stateFile = Path.Combine(folder, "tool-state.json");
        var library = new ToolPresetLibrary(libraryFile);
        var levels = library.OfficialFor(EditorTool.Number).Select(preset => library.Apply(preset.Id)).ToArray();
        check("the only official number recipes are three classic levels", levels.Length == 3 &&
            levels.All(p => p.Style.Variant == ElementAppearance.NumberFilled && p.Style.SecondaryColor == 0xFFFFFFFF &&
                p.NumberStart == 1 && p.NumberStep == 1));
        check("levels have red decimal, blue lowercase and green Roman sequences",
            levels.Select(p => p.Style.PrimaryColor).SequenceEqual([BuiltInColors.Red, BuiltInColors.Blue, BuiltInColors.Green]) &&
            levels.Select(p => (p.Numbering ?? NumberSequenceSettings.Default).Label(2)).SequenceEqual(["2", "b", "ii"]));
        OfficialLevelSizes(library, check);
        var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(folder, "colors.json")));
        panel.PrepareForShow();
        var format = Field<NumberFormatPicker>(panel, "numberFormatPicker");
        var edit = Field<Button>(panel, "editNumberSequence");
        var controls = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
        check("standard numbering hides its edit button", format.Items.Count == 9 && edit.Visibility == Visibility.Collapsed);
        format.SelectedValue = NumberFormat.Custom; panel.PrepareForShow();
        check("custom format reveals only an edit button on the same row", edit.Visibility == Visibility.Visible &&
            ReferenceEquals(edit.Parent, format.Parent) && !rows.ContainsKey("SequenceItems") && !controls.ContainsKey("SequenceFirst"));
        var initial = editor.Numbering;
        int publications = 0; panel.ParametersChanged += () => publications++;
        UseDialog(panel, dialog =>
        {
            Field<TextBox>(dialog, "items").Text = "discarded";
            Field<NumericValueField>(dialog, "first").Input.Text = "-12";
            Field<SegmentedChoice>(dialog, "overflow").SelectedValue = true;
            check("dialog drafts have no effect on the active table", ReferenceEquals(initial, editor.Numbering));
        }, accept: false);
        check("cancelling the dialog discards the table and both options", ReferenceEquals(initial, editor.Numbering) && publications == 0);
        UseDialog(panel, dialog =>
        {
            var items = Field<TextBox>(dialog, "items"); var split = Field<Button>(dialog, "split");
            var first = Field<NumericValueField>(dialog, "first");
            first.Input.Text = "1.5";
            check("a fractional anchor cannot apply the draft", !Field<Button>(dialog, "accept").IsEnabled);
            first.Input.Text = "0";
            items.Text = "甲乙丙丁";
            check("one line stays a phrase until explicitly split", items.LineCount == 1 && split.IsEnabled);
            split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("splitting separates characters", items.Text.ReplaceLineEndings("\n") == "甲\n乙\n丙\n丁");
            items.Text = "👩‍💻甲"; split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("splitting preserves combined emoji as one entry", items.Text.ReplaceLineEndings("\n") == "👩‍💻\n甲");
            items.Text = "甲\r\n办理中\r\n甲";
            Field<SegmentedChoice>(dialog, "overflow").SelectedValue = true;
        });
        check("OK publishes the whole draft once", publications == 1 && editor.Numbering.FirstNumber == 0 &&
            editor.Numbering.Cycle && editor.Numbering.Items.SequenceEqual(["甲", "办理中", "甲"]));
        UseDialog(panel, _ => { });
        check("OK without edits leaves the current recipe alone", publications == 1);
        controls["NextNumber"].Input.Text = "-1";
        controls["NumberStep"].Input.Text = "1";
        var preset = library.Save("流程", editor.Parameters);
        var state = new ToolStateStore(stateFile); state.Tools[EditorTool.Number] = editor.Parameters; state.Save();
        var restored = new ToolStateStore(stateFile).Tools[EditorTool.Number].Numbering!;
        check("tool cache and personal preset round-trip entries, signed anchor and cycling",
            restored.FirstNumber == 0 && restored.Cycle && restored.Items.SequenceEqual(["甲", "办理中", "甲"]) &&
            new ToolPresetLibrary(libraryFile).Apply(preset.Id).Numbering!.Label(1) == "办理中");
        for (int i = 0; i < 3; i++)
        {
            editor.Pointer(new(OverlayPointerKind.Down, new(90 + 240 * i, 120), OverlayMouseButton.Left, OverlayModifiers.None));
            check("placing previews the resolved string before release " + i, session.Document.Elements.Last().Text == restored.Label(i - 1));
            editor.Pointer(new(OverlayPointerKind.Up, new(90 + 240 * i, 120), OverlayMouseButton.Left, OverlayModifiers.None));
        }
        check("click placement advances only the logical sequence", session.Document.NextNumber == 2 &&
            session.Document.Elements.Select(e => (e.Number, e.Text)).SequenceEqual([(-1, "甲"), (0, "甲"), (1, "办理中")]));
        UseDialog(panel, dialog => Field<TextBox>(dialog, "items").Text = "新标签");
        var secondPreset = library.Save("第二套", editor.Parameters);
        var currentLibrary = new ToolPresetLibrary(libraryFile);
        editor.ApplyParameters(currentLibrary.Apply(preset.Id)); panel.PrepareForShow();
        check("loading a personal preset refreshes the selected custom choice", format.SelectedItem.ToString()!.Contains("办理中"));
        check("loading a personal preset replaces the one active table", editor.Numbering.Items.SequenceEqual(["甲", "办理中", "甲"]));
        editor.ApplyParameters(currentLibrary.Apply(secondPreset.Id)); panel.PrepareForShow();
        check("a shorter table replaces the old preview", format.SelectedItem.ToString()!.Contains("新标签") && !format.SelectedItem.ToString()!.Contains("办理中"));
        check("a second preset restores an independent list", editor.Numbering.Items.SequenceEqual(["新标签"]));
        UseDialog(panel, dialog => Field<TextBox>(dialog, "items").Text = "草稿已应用");
        check("accepting an edit changes only the working table", editor.Numbering.Items.SequenceEqual(["草稿已应用"]) &&
            currentLibrary.Apply(secondPreset.Id).Numbering!.Items.SequenceEqual(["新标签"]));
        currentLibrary.Update(secondPreset.Id, editor.Parameters);
        var reread = new ToolPresetLibrary(libraryFile);
        check("explicitly updating one saved preset preserves the other table", reread.Apply(secondPreset.Id).Numbering!.Items.SequenceEqual(["草稿已应用"]) &&
            reread.Apply(preset.Id).Numbering!.Items.SequenceEqual(["甲", "办理中", "甲"]));
        check("changing a tool table does not relabel existing layers", session.Document.Elements.Last().Text == "办理中");

        string file = Path.Combine(folder, "scene.floatcanvas");
        var store = new ProjectStore();
        store.SaveAsync(file, session.Document, session.Assets).GetAwaiter().GetResult();
        var loaded = store.LoadAsync(file).GetAwaiter().GetResult().Document;
        using var zip = ZipFile.OpenRead(file);
        using var entry = zip.GetEntry("scene.json")!.Open();
        using var json = JsonDocument.Parse(entry);
        var wire = json.RootElement.GetProperty("Elements")[2];
        check("project stores the integer, custom format and its one resolved label", wire.GetProperty("Number").GetInt32() == 1 &&
            wire.GetProperty("Text").GetString() == "办理中" && wire.GetProperty("NumberFormat").GetInt32() == (int)NumberFormat.Custom &&
            !wire.TryGetProperty("Numbering", out _) && !wire.TryGetProperty("Items", out _) && loaded.Elements.Last().Text == "办理中" && loaded.Version == SceneDocument.CurrentVersion);
        var last = loaded.Elements.Last();
        var pasting = new EditorSession(loaded); pasting.SetSelection([last.Id]); pasting.DuplicateSelection();
        check("duplicated layers keep resolved text and the next number", pasting.SelectedElements().Single().Text == "办理中" && pasting.Document.NextNumber == 2);
        var copied = ObjectClipboardCommand.CreateSnapshot(pasting);
        var target = new EditorSession();
        ObjectClipboardCommand.Insert(target, copied, pasting.Assets, new(0, 0, 800, 600));
        var pasted = target.SelectedElements().Single();
        check("cross-document paste keeps the resolved custom label without its source tool",
            pasted.NumberFormat == NumberFormat.Custom && pasted.Number == 1 && pasted.Text == "办理中");
        check("released decimal markers need no format field", !JsonSerializer.SerializeToElement(new CanvasElement { Kind = ElementKind.Number }).TryGetProperty("NumberFormat", out _));
    }

    private static void OfficialLevelSizes(ToolPresetLibrary library, Action<string, bool> check)
    {
        var session = new EditorSession();
        var editor = new EditorController(session) { Tool = EditorTool.Number };
        var gallery = new ToolPresetGallery(editor, library);
        gallery.PrepareForShow();
        var buttons = gallery.OfficialTiles.Children.OfType<Button>().ToArray();
        var tiles = buttons.Select(button => (Border)button.Content).ToArray();
        check("official level thumbnails show relative size in equal tiles",
            tiles.All(tile => tile.Width == 24 && tile.Height == 24) &&
            tiles.Select(tile => ((FrameworkElement)tile.Child).Width).SequenceEqual([24d, 64d / 3, 56d / 3]));

        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var point = new OverlayPoint(100 + 150 * i, 100);
            editor.Pointer(new(OverlayPointerKind.Down, point, OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Up, point, OverlayMouseButton.Left, OverlayModifiers.None));
        }
        check("clicking the official levels places successively smaller round markers",
            session.Document.Elements.Select(e => (e.Width, e.Height)).SequenceEqual([(72d, 72d), (64d, 64d), (56d, 56d)]));
    }

    private static void UseDialog(ToolOptionsPanel panel, Action<NumberSequenceEditorWindow> edit, bool accept = true)
    {
        Exception? failure = null;
        panel.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<NumberSequenceEditorWindow>().Single(window => window.IsVisible);
            try
            {
                edit(dialog);
                if (accept) Field<Button>(dialog, "accept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else SessionWindowCoordinator.SetModalResult(dialog, false);
            }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        Field<Button>(panel, "editNumberSequence").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure is not null) throw new InvalidOperationException("Number sequence dialog", failure);
    }

    private static void CounterControl(string folder, Action<string, bool> check)
    {
        var marker = new CanvasElement { Kind = ElementKind.Number, Number = 12, Text = "手写", NumberFormat = NumberFormat.Custom };
        var session = new EditorSession(new() { NextNumber = 45, Elements = [marker] });
        var editor = new EditorController(session);
        var recipe = ToolPresetLibrary.OfficialDefault(EditorTool.Number); recipe.NumberStart = -2; recipe.NumberStep = -3;
        recipe.Numbering = new() { Format = NumberFormat.Custom, FirstNumber = 3, Items = ["甲", "乙", "丙", "丁"] };
        editor.ApplyParameters(recipe);
        var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(folder, "counter-presets.json")),
            new ColorLibrary(Path.Combine(folder, "counter-colors.json")));
        panel.PrepareForShow();
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
        var reset = Field<Button>(panel, "resetNextNumber");
        var field = Field<Dictionary<string, NumericValueField>>(panel, "numericFields")["NextNumber"];
        check("number format sits directly between size and start", Grid.GetRow(rows["NumberFormat"]) == Grid.GetRow(rows["FontSize"]) + 1 &&
            Grid.GetRow(rows["NumberStart"]) == Grid.GetRow(rows["NumberFormat"]) + 1);
        check("next number has one square trailing action", ReferenceEquals(reset.Parent, field.Parent) && Grid.GetColumn(reset) == 1 &&
            reset.Width == field.Height && reset.Height == field.Height);
        var picker = Field<NumberFormatPicker>(panel, "numberFormatPicker");
        check("custom option displays the first three actual strings irrespective of the numeric anchor",
            picker.SelectedItem.ToString()!.Contains("甲") && picker.SelectedItem.ToString()!.Contains("丙") && !picker.SelectedItem.ToString()!.Contains("丁"));
        void Click() => reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click(); Click();
        check("reset uses the signed start exactly once without editing layers, step or mapping",
            session.Document.NextNumber == -2 && field.Value == -2 && session.UndoCount == 1 && editor.NumberStep == -3 &&
            session.Document.Elements.Single().Text == "手写" && editor.Numbering.FirstNumber == 3);
        check("reset undo and redo restore the two counter values", session.Undo() && session.Document.NextNumber == 45 && session.Redo() && session.Document.NextNumber == -2);
        session.ExecuteSettings("test counter", d => d.NextNumber = 19); session.BeginTransaction("unrelated gesture");
        Click();
        check("reset never writes through an active gesture", session.IsTransactionActive && session.Document.NextNumber == 19);
        session.Cancel();
        recipe.NumberStart = 0; recipe.NumberStep = 0; editor.ApplyParameters(recipe); panel.PrepareForShow(); Click();
        check("reset also accepts zero start and stationary sequences", session.Document.NextNumber == 0 && editor.NumberStep == 0);
        editor.ReplaceSession(new(new SceneDocument { NextNumber = 53 }, readOnly: true), default);
        panel.PrepareForShow(); Click();
        check("read-only canvas disables the number reset", !reset.IsEnabled && editor.Session.Document.NextNumber == 53);
    }

    private static void LayerEdits(Action<string, bool> check)
    {
        var a = new CanvasElement { Kind = ElementKind.Number, Width = 72, Height = 72, Number = 1, NumberFormat = NumberFormat.Custom, Text = "甲" };
        var b = a.Clone(true); b.Number = 2; b.Text = "保留";
        var roman = a.Clone(true); roman.Number = 4; roman.NumberFormat = NumberFormat.UpperRoman; roman.Text = "";
        var session = new EditorSession(new() { Elements = [a, b, roman], NextNumber = -8 });
        var editor = new EditorController(session);
        editor.ApplyParameters(new() { Tool = EditorTool.Number, Numbering = new() { Format = NumberFormat.ChineseFinancial } });
        editor.ApplyParameters(new() { Tool = EditorTool.Select });
        var inspector = new InspectorWindow(() => session, numberSequence: () => editor.Numbering);
        session.SetSelection([a.Id]);
        using var surface = new InspectorTestSurface(inspector);
        var number = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields")["Number"];
        var label = Field<Dictionary<string, TextBox>>(inspector, "fields")["NumberLabel"];
        var format = Field<NumberFormatPicker>(inspector, "numberFormatPicker");
        var rows = Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers");
        CanvasElement Layer(Guid id) => session.Document.Elements.Single(e => e.Id == id);
        void Select(params Guid[] ids) { inspector.CompletePendingEdit(); session.SetSelection(ids); inspector.PrepareForShow(); }
        void Finish() { inspector.CompletePendingEdit(); inspector.PrepareForShow(); }
        number.Input.Text = "01"; Finish();
        check("re-entering one preserves 甲 and creates no history", Layer(a.Id).Text == "甲" && session.UndoCount == 0);
        number.BeginGesture(); number.Slider.Value = 2;
        check("while dragging 1 to 2 the custom layer previews 贰 using the inactive number tool", Layer(a.Id).Number == 2 && Layer(a.Id).Text == "贰" &&
            Layer(a.Id).NumberFormat == NumberFormat.Custom && session.Document.NextNumber == -8);
        number.Slider.Value = 1; number.EndGesture(true);
        check("returning within the same drag restores 甲 exactly and creates no undo", Layer(a.Id).Text == "甲" && session.UndoCount == 0);
        number.BeginGesture(); number.Slider.Value = 3; number.EndGesture(false);
        check("cancelled number drag restores both fields", Layer(a.Id).Text == "甲" && Layer(a.Id).Number == 1 && session.UndoCount == 0);
        number.Input.Text = "2"; Finish();
        check("a real number change commits number and remapped text as one action", Layer(a.Id).Text == "贰" && session.UndoCount == 1 &&
            session.Undo() && Layer(a.Id).Number == 1 && Layer(a.Id).Text == "甲" && session.Redo() && Layer(a.Id).Text == "贰");
        Select(a.Id);
        label.Text = "手写"; Finish();
        check("manual label edit preserves the logical number and stays after re-entry", Layer(a.Id).Text == "手写" && Layer(a.Id).Number == 2);
        number.Input.Text = "02"; Finish();
        check("same number after a manual edit never looks up a replacement", Layer(a.Id).Text == "手写");
        editor.ApplyParameters(new() { Tool = EditorTool.Number, Numbering = new() { Format = NumberFormat.Custom, FirstNumber = -1, Cycle = true, Items = ["申", "酉", "戌"] } });
        editor.ApplyParameters(new() { Tool = EditorTool.Select });
        inspector.Refresh();
        check("pinned inspector previews the current tool table even before another layer edit", format.SelectedItem.ToString()!.Contains("戌"));
        number.Input.Text = "0"; Finish();
        check("a changed integer uses current custom table and its anchor", Layer(a.Id).Text == "酉" && Layer(a.Id).Number == 0);

        Select(roman.Id);
        check("built-in layer has no editable literal and initially renders IV", rows["NumberLabel"].Visibility == Visibility.Collapsed && NumberSequence.Label(Layer(roman.Id)) == "IV");
        format.SelectedValue = NumberFormat.Custom; Finish();
        check("switch to custom seeds the visible Roman string without consulting the active table", Layer(roman.Id).Text == "IV" && rows["NumberLabel"].Visibility == Visibility.Visible);
        format.SelectedValue = NumberFormat.UpperLatin; Finish();
        number.Input.Text = "5"; Finish();
        check("a built-in layer follows its own format even when the tool is custom", NumberSequence.Label(Layer(roman.Id)) == "E" && Layer(roman.Id).Text == "");

        Select(a.Id); label.Text = "手写零"; Finish();
        Select(b.Id, a.Id); number.Input.Text = "0"; Finish();
        check("entering the first representative number still updates other mixed layers", Layer(a.Id).Text == "手写零" &&
            Layer(b.Id).Number == 0 && Layer(b.Id).Text == "酉");
        check("mixed numbering undo restores both independent labels", session.Undo() && Layer(a.Id).Text == "手写零" && Layer(b.Id).Text == "保留");
        Select(b.Id, a.Id); number.Input.Text = "2"; Finish();
        check("mixed number edit preserves existing matching labels per layer", Layer(b.Id).Text == "保留" && Layer(a.Id).Text == "申" && Layer(a.Id).Number == 2);
        Select(a.Id, roman.Id);
        check("mixed built-in and custom selection offers format but hides custom text", format.SelectedIndex == -1 && rows["NumberLabel"].Visibility == Visibility.Collapsed);
        format.SelectedValue = NumberFormat.Custom; Finish();
        check("bulk conversion seeds each display independently", Layer(a.Id).Text == "申" && Layer(roman.Id).Text == "E");
        check("all layer edits leave the canvas counter untouched", session.Document.NextNumber == -8);
    }

    private static void Pixels(Action<string, bool> check)
    {
        var e = new CanvasElement
        {
            Kind = ElementKind.Number,
            Number = 21,
            Width = 94,
            Height = 94,
            NumberTextSizeMode = NumberTextSizeMode.FitBadge,
            Style = new() { PrimaryColor = 0xff303030, SecondaryColor = 0xffffffff, FontFamily = "Microsoft YaHei UI", FontSize = 64 }
        };
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        byte[] Render() => renderer.ExportPng(new() { Viewport = new(0, 0, 96, 96), Elements = [e.Clone()] }, assets, true);
        e.NumberFormat = NumberFormat.ChineseFinancial; var formatted = Render(); e.NumberFormat = NumberFormat.Custom; e.Text = "贰拾壹";
        var literal = Render(); e.Text = "";
        using var one = SKBitmap.Decode(literal);
        check("export of custom and formatted Chinese uses the same actual ink and fits one badge", formatted.SequenceEqual(literal) &&
            !literal.SequenceEqual(Render()) && one.Pixels.Any(p => p == SKColors.White));
        e.Style.PrimaryColor = 0; e.Style.SecondaryColor = 0xFFFFFFFF;
        foreach (var label in new[] { "流程二", "A\u0301", "👩‍💻" })
        {
            e.Text = label;
            using var pixels = SKBitmap.Decode(Render());
            check("custom Unicode labels retain visible ink: " + label, pixels.Pixels.Any(p => p.Alpha > 100));
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
