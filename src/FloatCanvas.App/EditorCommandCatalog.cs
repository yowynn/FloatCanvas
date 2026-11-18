using FloatCanvas.Windows;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal readonly record struct CommandChord(int Key, OverlayModifiers Modifiers = OverlayModifiers.None)
{
    public override string ToString() => HotkeyDisplay.Format(new((uint)Modifiers, (uint)Key));
}

internal sealed record EditorCommand(string Id, string DefaultName, CommandChord[] Defaults,
    EditorTool? Tool = null, bool Repeat = false, bool Mutates = false, bool Configurable = true)
{
    internal string Name => Tool is {} tool
        ? ToolDisplayName.Get(tool) : FloatCanvas.Localization.AppText.Get("Shortcuts.Command."+Id);
}

/// <summary>Commands, exact default chords and user overrides share one lookup for routing and UI.</summary>
internal static class EditorCommandCatalog
{
    private const OverlayModifiers C = OverlayModifiers.Control, S = OverlayModifiers.Shift;
    internal static readonly IReadOnlyList<EditorCommand> All = Build();
    [ThreadStatic] private static Dictionary<string, CommandChord[]>? overrides;
    internal static Dictionary<string, CommandChord[]> Overrides
    { get => overrides ??= []; set => overrides = value; }
    internal static CommandChord[] Bindings(EditorCommand command) =>
        Overrides.TryGetValue(command.Id, out var bindings) && command.Configurable ? bindings : command.Defaults;
    internal static EditorCommand? Resolve(int key, OverlayModifiers modifiers) =>
        All.FirstOrDefault(command => Bindings(command).Contains(new CommandChord(key, modifiers)));
    internal static string Hint(string id) => All.FirstOrDefault(c => c.Id == id) is { } command
        ? string.Join(" / ", Bindings(command).Select(key => key.ToString())) : "";
    internal static string ToolId(EditorTool tool) => "Tool." + tool;
    internal static bool IsRetired(string id) => id is "Tool.Arrow" or "Tool.DoubleArrow" or "Tool.RoundedRectangle" or "Tool.Triangle" or "Tool.Stamp";

    private static bool IsReserved(CommandChord key) =>
        key.Key <= 0 || key.Key > 254 || key.Key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5 ||
        (key.Modifiers & ~(C | S | OverlayModifiers.Alt)) != 0 ||
        key.Key == 0x20 && key.Modifiers != OverlayModifiers.None ||
        key.Modifiers.HasFlag(OverlayModifiers.Alt) && key.Key is 0x09 or 0x1B or 0x73 ||
        key.Modifiers.HasFlag(C) && key.Key == 0x1B;

    // Existing user choices take priority over defaults introduced by an upgrade.
    // Fixed gestures and global hotkeys already own their input before editor commands.
    internal static Dictionary<string, CommandChord[]> ReconcileSavedBindings(
        Dictionary<string, CommandChord[]> saved, IEnumerable<KeyValuePair<CommandChord, string>> globals,
        out List<string> notices)
    {
        notices = [];
        var occupied = new Dictionary<CommandChord, string>();
        foreach (var (chord, owner) in globals) occupied.TryAdd(chord, owner);
        foreach (var command in All.Where(command => !command.Configurable))
            foreach (var chord in command.Defaults) occupied[chord] = command.Name;

        var result = new Dictionary<string, CommandChord[]>();
        foreach (var command in All.Where(command => command.Configurable && saved.ContainsKey(command.Id)))
        {
            var kept = new List<CommandChord>(2);
            var original = saved[command.Id];
            if (original is null || original.Length > 2) notices.Add(L.Format("Shortcuts.BindingUnavailable", command.Name));
            foreach (var chord in (original ?? []).Take(2))
            {
                if (IsReserved(chord)) notices.Add(L.Format("Shortcuts.BindingUnavailable", command.Name + " · " + chord));
                else if (occupied.TryGetValue(chord, out var owner)) notices.Add(L.Format("Shortcuts.BindingConflict", command.Name, chord, owner));
                else { occupied.Add(chord, command.Name); kept.Add(chord); }
            }
            result[command.Id] = kept.ToArray();
        }
        foreach (var command in All.Where(command => command.Configurable && !saved.ContainsKey(command.Id)))
        {
            var kept = new List<CommandChord>(command.Defaults.Length);
            foreach (var chord in command.Defaults)
            {
                if (occupied.TryGetValue(chord, out var owner)) notices.Add(L.Format("Shortcuts.BindingConflict", command.Name, chord, owner));
                else { occupied.Add(chord, command.Name); kept.Add(chord); }
            }
            if (kept.Count != command.Defaults.Length) result[command.Id] = kept.ToArray();
        }
        return result;
    }

    internal static string? Validate(Dictionary<string, CommandChord[]> bindings)
    {
        var used = new Dictionary<CommandChord, string>();
        foreach (var entry in bindings)
            if (!All.Any(c => c.Id == entry.Key && c.Configurable)) return FloatCanvas.Localization.AppText.Get("Shortcuts.UnknownCommand") + entry.Key;
        foreach (var command in All)
        {
            var keys = bindings.GetValueOrDefault(command.Id, command.Defaults);
            if (keys is null || keys.Length > 2) return command.Name + ": " + FloatCanvas.Localization.AppText.Get("Shortcuts.TwoBindings");
            foreach (var key in keys)
            {
                if (key.Key == 0x20 && key.Modifiers != OverlayModifiers.None) return L.Get("Shortcuts.SpaceReserved");
                if (IsReserved(key))
                    return FloatCanvas.Localization.AppText.Get("Shortcuts.Reserved") + key;
                if (used.TryGetValue(key, out var other)) return key + ": " + other + " / " + command.Name;
                used[key] = command.Name;
            }
        }
        return null;
    }

    private static IReadOnlyList<EditorCommand> Build()
    {
        var result = new List<EditorCommand>();
        void Add(string id, string name, int key = 0, OverlayModifiers mods = 0, bool repeat = false, bool mutates = false, bool configurable = true) =>
            result.Add(new(id, name, key == 0 ? [] : [new(key, mods)], Repeat: repeat, Mutates: mutates, Configurable: configurable));
        var letters = new Dictionary<EditorTool, int>
        {
            [EditorTool.Select]=0x56,[EditorTool.Eraser]=0x45,[EditorTool.Freehand]=0x42,[EditorTool.Highlighter]=0x48,
            [EditorTool.Line]=0x4C,[EditorTool.Rectangle]=0x52,[EditorTool.Ellipse]=0x4F,[EditorTool.Text]=0x54,
            [EditorTool.Number]=0x4E,[EditorTool.Symbol]=0x53,[EditorTool.Emoji]=0x4A,[EditorTool.Canvas]=0x43,[EditorTool.CaptureRegion]=0x50
        };
        foreach (var tool in Enum.GetValues<EditorTool>())
            if (tool != EditorTool.Image && !IsRetired(ToolId(tool))) result.Add(new(ToolId(tool), tool.ToString(), letters.TryGetValue(tool, out int key) ? [new(key)] : [], tool));
        Add("Image.Import", "置入内容", 0x49); Add("Tools.More", "更多工具", 0x4D);
        Add("Stamp.Library", "印章库", 0xC0); Add("Stamp.Create", "从选择新建印章", 0xBB);
        for (int i=0;i<10;i++) Add("Stamp.Slot"+i, "印章槽 "+(i+1), i==9?0x30:0x31+i);
        Add("Edit.Undo", "撤销", 0x5A,C,mutates:true);
        result.Add(new("Edit.Redo","重做",[new(0x5A,C|S),new(0x59,C)],Mutates:true));
        Add("Edit.SelectAll","全选",0x41,C); Add("Edit.Deselect","取消选择",0x41,C|S);
        Add("Edit.Copy","复制",0x43,C); Add("Edit.Cut","剪切",0x58,C,mutates:true);
        Add("Edit.Paste","粘贴",0x56,C,mutates:true); Add("Edit.Duplicate","复制选中对象",0x44,C,mutates:true);
        Add("Edit.Group","成组",0x47,C,mutates:true); Add("Edit.Ungroup","解组",0x47,C|S,mutates:true);
        Add("Edit.Delete","删除",0x2E,mutates:true); Add("Edit.Clear","清空画布",0x08,C|S,mutates:true);
        Add("File.Save","保存",0x53,C); Add("File.SaveAs","另存为",0x53,C|S);
        Add("File.Open","打开工程",0x4F,C); Add("File.New","新建画布",0x4E,C);
        Add("Layer.Down","下移一层",0xDB,C,mutates:true); Add("Layer.Up","上移一层",0xDD,C,mutates:true);
        Add("Layer.Bottom","置底",0xDB,C|S,mutates:true); Add("Layer.Top","置顶",0xDD,C|S,mutates:true);
        Add("View.Fit","适配全部内容",0x30,C); Add("View.Actual","100%",0x31,C);
        Add("View.Selection","适配选择",0x32,C); Add("View.Reset","重置视图",0x30,C|S);
        Add("Canvas.OpacityUp","增加画布透明度值",0x21,C,true,true); Add("Canvas.OpacityDown","减少画布透明度值",0x22,C,true,true);
        Add("Panel.Tools","工具参数",0x73); Add("Panel.Layers","图层",0x4C,C);
        Add("Panel.Settings","设置",0xBC,C); Add("Commands.Search","搜索命令",0x50,C|S);
        Add("Selection.Properties","图层属性",0x0D,configurable:false); Add("Selection.Rename","重命名",0x71);
        Add("UI.ToggleToolbar","展开/收起工具条",0x09,configurable:false);
        Add("Interaction.Cancel","取消",0x1B,configurable:false); Add("Interaction.Pan","临时平移",0x20,configurable:false);
        Add("Capture.Back","删除截图顶点",0x08,repeat:true,configurable:false);
        foreach (var key in new[]{0x25,0x26,0x27,0x28})
        {
            Add("Move."+key,"移动 "+KeyInterop.KeyFromVirtualKey(key),key,mutates:true);
            Add("Move.Fast"+key,"快速移动 "+KeyInterop.KeyFromVirtualKey(key),key,S,mutates:true);
        }
        return result.AsReadOnly();
    }
}
