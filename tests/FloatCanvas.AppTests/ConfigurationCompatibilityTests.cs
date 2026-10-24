using System.IO;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ConfigurationCompatibilityTests
{
    internal static void Run(Action<string,bool> check)
    {
        string language=FloatCanvas.Localization.AppText.Preference;
        try {RunCore(check);}
        finally {FloatCanvas.Localization.AppText.Initialize(language);}
    }
    private static void RunCore(Action<string,bool> check)
    {
        string folder=Path.Combine(ApplicationPaths.ProfileDirectory,"compatibility");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"tool-state.json");
        var state=new ToolStateStore(path);state.Tools[EditorTool.Freehand]=new();state.Save();
        var json=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["FutureRoot"]=new JsonObject { ["important"]="preserve me" };
        json["Buttons"]!["Canvas"]="FutureCanvas";
        File.WriteAllText(path,json.ToJsonString());
        check("future canvas button names do not prevent startup", new ToolStateStore(path).Tools.ContainsKey(EditorTool.Freehand));
        json["Buttons"]!["FutureTool"]="FutureTool";
        json["Tools"]!["Freehand"]!["FutureParameter"]=17;
        json["Tools"]!["Freehand"]!["Style"]!["FutureStyle"]="new";
        json["Tools"]!["Freehand"]!["Style"]!["FontSize"]="unreadable";
        json["Tools"]!["Freehand"]!["Style"]!["Opacity"]=-1;
        json["Tools"]!["Freehand"]!["DefaultWidth"]=123;
        json["Tools"]!["Freehand"]!.AsObject().Remove("Emoji");
        File.WriteAllText(path,json.ToJsonString());string original=File.ReadAllText(path);
        var loaded=new ToolStateStore(path);
        check("future button enum and fields load without touching the file", File.ReadAllText(path)==original && loaded.Tools[EditorTool.Freehand].DefaultWidth==123 && !loaded.Buttons.ContainsKey(EditorTool.Canvas));
        check("missing unreadable and invalid fields default individually",loaded.Tools[EditorTool.Freehand].Style.FontSize==24 && loaded.Tools[EditorTool.Freehand].Style.Opacity==1 && loaded.Tools[EditorTool.Freehand].Emoji=="😀");
        loaded.Tools[EditorTool.Freehand].DefaultWidth=456;loaded.Save();var saved=JsonNode.Parse(File.ReadAllText(path))!;
        check("saving another setting retains future root parameter style and enum values",saved["FutureRoot"]!["important"]!.GetValue<string>()=="preserve me" && saved["Buttons"]!["Canvas"]!.GetValue<string>()=="FutureCanvas" && saved["Buttons"]!["FutureTool"]!.GetValue<string>()=="FutureTool" && saved["Tools"]!["Freehand"]!["FutureParameter"]!.GetValue<int>()==17 && saved["Tools"]!["Freehand"]!["Style"]!["FutureStyle"]!.GetValue<string>()=="new");
        check("unchanged unparseable values remain available to other versions",saved["Tools"]!["Freehand"]!["Style"]!["FontSize"]!.GetValue<string>()=="unreadable" && saved["Tools"]!["Freehand"]!["DefaultWidth"]!.GetValue<int>()==456);
        OtherLibraries(folder,check); LegacyNumberStates(folder,check); Collections(folder,check); WriteRepair(folder,check); Repair(folder,check);
    }
    private static void LegacyNumberStates(string folder,Action<string,bool> check)
    {
        foreach(uint fill in new uint[]{0,0xFF2383E2})
        {
            string path=Path.Combine(folder,"legacy-state-"+fill+".json");var state=new ToolStateStore(path);var old=new ToolParameters {Tool=EditorTool.Number,Style=new(){Stroke=ColorLibrary.ForegroundRed,Fill=fill}};
            state.Tools[EditorTool.Number]=old;state.OfficialBaselines["official.number"]=old.Clone();state.PreviewSources[EditorTool.Number]=old.Clone();state.Save();var raw=JsonNode.Parse(File.ReadAllText(path))!;
            raw["Tools"]!["Number"]!.AsObject().Remove("NumberColorRolesVersion");raw["OfficialBaselines"]!["official.number"]!.AsObject().Remove("NumberColorRolesVersion");raw["PreviewSources"]!["Number"]!.AsObject().Remove("NumberColorRolesVersion");File.WriteAllText(path,raw.ToJsonString());string original=File.ReadAllText(path);state=new(path);
            foreach(var (label,parameters) in new[]{("working tool",state.Tools[EditorTool.Number]),("official baseline",state.OfficialBaselines["official.number"]),("preview source",state.PreviewSources[EditorTool.Number])})
            {
                var editor=new EditorController(new EditorSession());editor.ApplyParameters(parameters);
                check("legacy number "+label+" fill "+fill+" retains its badge and readable white labels",editor.Style.Stroke==(fill==0?ColorLibrary.ForegroundRed:fill) && editor.Style.Fill==0xFFFFFFFF && File.ReadAllText(path)==original);
            }
            var library=new ToolPresetLibrary(Path.Combine(folder,"official-legacy-"+fill+".json"));var refreshed=library.Apply("official.number");var current=new EditorController(new EditorSession());current.ApplyParameters(refreshed);
            check("explicit official colors replace legacy number color semantics together",current.Style.Stroke==ColorLibrary.ForegroundRed && current.Style.Fill==0xFFFFFFFF);
        }
    }
    private static void Collections(string folder,Action<string,bool> check)
    {
        string path=Path.Combine(folder,"collection-presets.json");var library=new ToolPresetLibrary(path);var a=library.Save("A",new());var b=library.Save("B",new());var raw=JsonNode.Parse(File.ReadAllText(path))!;
        raw["Presets"]![0]!["Future"]="A";raw["Presets"]![1]!["Future"]="B";File.WriteAllText(path,raw.ToJsonString());library=new(path);library.Move(b.Id,a.Id);library.Rename(a.Id,"A renamed");raw=JsonNode.Parse(File.ReadAllText(path))!;
        check("reordering presets keeps future fields paired by stable ID",raw["Presets"]![0]!["Id"]!.GetValue<string>()==b.Id && raw["Presets"]![0]!["Future"]!.GetValue<string>()=="B" && raw["Presets"]![1]!["Future"]!.GetValue<string>()=="A");library.Delete(b.Id);raw=JsonNode.Parse(File.ReadAllText(path))!;
        check("deleting a preset retains its peer's future fields",raw["Presets"]!.AsArray().Count==1 && raw["Presets"]![0]!["Future"]!.GetValue<string>()=="A");
        string colorsPath=Path.Combine(folder,"collection-colors.json");var colors=new ColorLibrary(colorsPath);var ca=colors.Save("A",1);var cb=colors.Save("B",2);raw=JsonNode.Parse(File.ReadAllText(colorsPath))!;raw["Swatches"]![0]!["Future"]="A";raw["Swatches"]![1]!["Future"]="B";File.WriteAllText(colorsPath,raw.ToJsonString());colors=new(colorsPath);colors.Move(cb.Id,ca.Id);colors.Update(ca.Id,3);raw=JsonNode.Parse(File.ReadAllText(colorsPath))!;
        check("reordered swatches retain future metadata with their own IDs",raw["Swatches"]![0]!["Future"]!.GetValue<string>()=="B" && raw["Swatches"]![1]!["Future"]!.GetValue<string>()=="A" && raw["Swatches"]![1]!["Value"]!.GetValue<uint>()==3);colors.Delete(cb.Id);raw=JsonNode.Parse(File.ReadAllText(colorsPath))!;
        check("deleting a swatch retains its peer's future metadata",raw["Swatches"]!.AsArray().Count==1 && raw["Swatches"]![0]!["Future"]!.GetValue<string>()=="A");
    }
    private static void WriteRepair(string folder,Action<string,bool> check)
    {
        string previous=ApplicationPaths.ProfileDirectory;var previousPrompt=CompatibleConfiguration.ConfirmRepair;string profile=Path.Combine(folder,"write-repair");Directory.CreateDirectory(profile);ApplicationPaths.Configure(["--profile",profile]);
        try
        {
            foreach(string name in ConfigurationStartup.FileNames)File.WriteAllText(Path.Combine(profile,name),"{}");string path=Path.Combine(profile,"tool-state.json");File.WriteAllText(path,"{\"Tools\":\"future schema\",\"FutureRoot\":true}");string original=File.ReadAllText(path);var state=new ToolStateStore(path);state.Tools[EditorTool.Freehand]=new();int prompts=0;
            CompatibleConfiguration.ConfirmRepair=(file,fields)=>{prompts++;return false;};bool cancelled=false;try{state.Save();}catch(IOException){cancelled=true;}
            check("replacing a known unreadable structure requires a choice and cancellation retains every byte",cancelled && prompts==1 && File.ReadAllText(path)==original);
            string? backup=null;bool beforeWrite=false;CompatibleConfiguration.ConfirmRepair=(file,fields)=>{backup=ConfigurationStartup.Backup(file);beforeWrite=File.ReadAllText(path)==original && fields.SequenceEqual(new[]{"Tools"});return true;};state.Save();var raw=JsonNode.Parse(File.ReadAllText(path))!;
            check("approved compatibility write backs up raw data before applying only the requested change",beforeWrite && File.ReadAllText(Path.Combine(backup!,"tool-state.json"))==original && raw["Tools"] is JsonObject && raw["FutureRoot"]!.GetValue<bool>());
        }
        finally {CompatibleConfiguration.ConfirmRepair=previousPrompt;ApplicationPaths.Configure(["--profile",previous]);}
    }
    private static void OtherLibraries(string folder,Action<string,bool> check)
    {
        string presetPath=Path.Combine(folder,"presets.json");var library=new ToolPresetLibrary(presetPath);var preset=library.Save("Existing",new());
        var raw=JsonNode.Parse(File.ReadAllText(presetPath))!;raw["Version"]=23;raw["FutureRoot"]="future";raw["Presets"]![0]!["Parameters"]!["Style"]!["FutureColor"]=123;raw["Presets"]![0]!["Parameters"]!["Style"]!["StrokeWidth"]="new-width";
        File.WriteAllText(presetPath,raw.ToJsonString());library=new(presetPath);library.Rename(preset.Id,"Renamed");raw=JsonNode.Parse(File.ReadAllText(presetPath))!;
        check("preset rename retains future version nested fields and unreadable values",raw["Version"]!.GetValue<int>()==23 && raw["Presets"]![0]!["Parameters"]!["Style"]!["FutureColor"]!.GetValue<int>()==123 && raw["Presets"]![0]!["Parameters"]!["Style"]!["StrokeWidth"]!.GetValue<string>()=="new-width");
        string numbersPath=Path.Combine(folder,"legacy-number-presets.json");var numberLibrary=new ToolPresetLibrary(numbersPath);var numberPreset=numberLibrary.Save("Legacy number",new ToolParameters {Tool=EditorTool.Number,Style=new(){Stroke=0xFF00FF00,Fill=0xFF2383E2}});raw=JsonNode.Parse(File.ReadAllText(numbersPath))!;raw["Presets"]![0]!["Parameters"]!.AsObject().Remove("NumberColorRolesVersion");File.WriteAllText(numbersPath,raw.ToJsonString());string oldNumber=File.ReadAllText(numbersPath);
        numberLibrary=new(numbersPath);var controller=new EditorController(new EditorSession());controller.ApplyParameters(numberLibrary.Apply(numberPreset.Id));
        check("legacy number presets convert their former badge color to foreground and retain white labels without rewriting",controller.Style.Stroke==0xFF2383E2 && controller.Style.Fill==0xFFFFFFFF && controller.Parameters.NumberColorRolesVersion==1 && File.ReadAllText(numbersPath)==oldNumber);
        var currentNumber=controller.Parameters;currentNumber.Style.Fill=0xFF000000;var modern=numberLibrary.Save("Modern labels",currentNumber);controller.ApplyParameters(new ToolPresetLibrary(numbersPath).Apply(modern.Id));
        check("modern number presets retain custom text colors across save reload and apply",controller.Style.Fill==0xFF000000);
        string colorsPath=Path.Combine(folder,"colors.json");File.WriteAllText(colorsPath,"{\"Future\":{\"x\":1},\"History\":[],\"Pinned\":[]}");var colors=new ColorLibrary(colorsPath);colors.Save("Blue",0xFF2383E2);
        check("missing color-library fields default while future data survives a save",JsonNode.Parse(File.ReadAllText(colorsPath))!["Future"]!["x"]!.GetValue<int>()==1 && colors.Swatches.Count==1);
        string canvasPath=Path.Combine(folder,"canvas-presets.json");var canvases=new CanvasPresetLibrary(canvasPath);var canvas=canvases.Save("Canvas",new());raw=JsonNode.Parse(File.ReadAllText(canvasPath))!;
        raw["Presets"]![0]!["Parameters"]!["FutureGrid"]=true;raw["Presets"]![0]!["Parameters"]!["GridWidth"]="unknown";File.WriteAllText(canvasPath,raw.ToJsonString());canvases=new(canvasPath);canvases.Rename(canvas.Id,"Renamed canvas");raw=JsonNode.Parse(File.ReadAllText(canvasPath))!;
        check("canvas preset defaults only the bad field and preserves it and unknown fields on rename",canvases.User.Single().Parameters.GridWidth==1 && raw["Presets"]![0]!["Parameters"]!["FutureGrid"]!.GetValue<bool>() && raw["Presets"]![0]!["Parameters"]!["GridWidth"]!.GetValue<string>()=="unknown");
        string previous=ApplicationPaths.ProfileDirectory;string settingsProfile=Path.Combine(folder,"settings");Directory.CreateDirectory(settingsProfile);ApplicationPaths.Configure(["--profile",settingsProfile]);
        try
        {
            string settingsPath=Path.Combine(settingsProfile,"settings.json");File.WriteAllText(settingsPath,"{\"Theme\":\"dark\",\"Language\":\"en\",\"FuturePreference\":42,\"NumberStep\":\"future\"}");var settings=UserSettings.Load();settings.ShowFloatingLogo=false;settings.Save();raw=JsonNode.Parse(File.ReadAllText(settingsPath))!;
            check("settings preserve valid preferences future fields and default only the unreadable step",settings.Theme=="dark" && settings.NumberStep==1 && raw["FuturePreference"]!.GetValue<int>()==42 && raw["NumberStep"]!.GetValue<string>()=="future" && !raw["ShowFloatingLogo"]!.GetValue<bool>());
            check("saving unrelated settings does not materialize missing fields as null",!raw.AsObject().ContainsKey("Toggle") && !raw.AsObject().ContainsKey("NumberStart"));
        }
        finally {ApplicationPaths.Configure(["--profile",previous]);}
    }
    private static void Repair(string folder,Action<string,bool> check)
    {
        string previous=ApplicationPaths.ProfileDirectory;string profile=Path.Combine(folder,"repair");Directory.CreateDirectory(profile);ApplicationPaths.Configure(["--profile",profile]);
        try
        {
            foreach(string name in ConfigurationStartup.FileNames)File.WriteAllText(Path.Combine(profile,name),"{}");
            string broken=Path.Combine(profile,"tool-state.json");File.WriteAllText(broken,"{invalid");string canvas=Path.Combine(profile,"unrelated.floatcanvas");File.WriteAllText(canvas,"keep canvas");
            var failure=ConfigurationStartup.Validate();check("startup preflight identifies the unreadable file without native resources or mutation",failure?.Path==broken && File.ReadAllText(broken)=="{invalid" && !Directory.Exists(Path.Combine(profile,"configuration-backups")));
            bool backupFailed=false;using(var held=new FileStream(Path.Combine(profile,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.None))
            {try{ConfigurationStartup.Repair(broken);}catch(IOException){backupFailed=true;}}
            check("failed backup leaves the unreadable configuration untouched",backupFailed && File.ReadAllText(broken)=="{invalid");
            string backup=ConfigurationStartup.Repair(broken);
            check("explicit repair backs up every configuration file before resetting only the bad file",ConfigurationStartup.FileNames.All(name=>File.Exists(Path.Combine(backup,name))) && File.ReadAllText(Path.Combine(backup,"tool-state.json"))=="{invalid" && File.ReadAllText(broken)=="{}" && File.ReadAllText(canvas)=="keep canvas" && ConfigurationStartup.Validate() is null);
            bool rejected=false;try{ConfigurationStartup.Repair(canvas);}catch(ArgumentException){rejected=true;}
            check("configuration repair refuses project and recovery targets",rejected && File.ReadAllText(canvas)=="keep canvas");
            _=System.Windows.Application.Current??new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
            var dialog=AppDialog.Create(null,ConfigurationStartup.Message(failure!),"Startup",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Warning,"Configuration.BackupRepair","Configuration.Exit").Window;
            var content=(System.Windows.Controls.DockPanel)dialog.Content;var buttons=((System.Windows.Controls.StackPanel)content.Children[0]).Children.OfType<System.Windows.Controls.Button>().ToArray();
            check("repair dialog offers backup and exit without a raw stack trace",buttons.Length==2 && Equals(buttons[0].Content,FloatCanvas.Localization.AppText.Get("Configuration.BackupRepair")) && Equals(buttons[1].Content,FloatCanvas.Localization.AppText.Get("Configuration.Exit")) && !((System.Windows.Controls.TextBlock)((System.Windows.Controls.ScrollViewer)content.Children[1]).Content).Text.Contains("System.Text.Json"));dialog.Close();
        }
        finally {ApplicationPaths.Configure(["--profile",previous]);}
    }
}
