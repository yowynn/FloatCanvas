using System.IO;
using System.Text.Json;
using System.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record ConfigurationFailure(string Path, Exception Error);

/// <summary>Validate configuration before constructing native session resources; repair only after an explicit choice.</summary>
internal static class ConfigurationStartup
{
    internal static readonly string[] FileNames = ["settings.json","tool-state.json","tool-presets.json","canvas-presets.json","colors.json"];

    internal static ConfigurationFailure? Validate()
    {
        string root=ApplicationPaths.ProfileDirectory;
        (string Name, Action Read)[] readers =
        [
            ("settings.json",()=>{var settings=UserSettings.Load();L.Initialize(settings.Language);}),
            ("tool-state.json",()=>{_ = new ToolStateStore();}),
            ("tool-presets.json",()=>{_ = new ToolPresetLibrary();}),
            ("canvas-presets.json",()=>{_ = new CanvasPresetLibrary();}),
            ("colors.json",()=>{_ = new ColorLibrary();})
        ];
        foreach(var (name,read) in readers)
        {
            try { read(); }
            catch(Exception error) when(error is JsonException or InvalidDataException)
            { return new(Path.Combine(root,name),error); }
        }
        return null;
    }

    internal static string Message(ConfigurationFailure failure) => L.Format("Configuration.RepairMessage",System.IO.Path.GetFileName(failure.Path),ApplicationPaths.ProfileDirectory);

    internal static bool Prepare(ApplicationDiagnostics diagnostics,bool interactive=true)
    {
        while(Validate() is { } failure)
        {
            diagnostics.Write("Configuration.Invalid",new {failure.Path},failure.Error);
            if(!interactive)return false;
            if(AppDialog.Show(null,Message(failure),L.Get("App.StartupFailed"),MessageBoxButton.YesNo,MessageBoxImage.Warning,
                "Configuration.BackupRepair","Configuration.Exit")!=MessageBoxResult.Yes)return false;
            string backup=Repair(failure.Path);
            diagnostics.Write("Configuration.Repaired",new {failure.Path,Backup=backup});
        }
        return true;
    }

    internal static string Repair(string path)
    {
        string backup=Backup(path);
        string target=System.IO.Path.GetFullPath(path);
        // All backups complete before replacing the one unreadable file. Canvas and recovery assets are untouched.
        string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temporary,"{}");File.Move(temporary,target,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
        return backup;
    }

    internal static bool ConfirmWrite(string path,IReadOnlyList<string> fields,ApplicationDiagnostics diagnostics)
    {
        if(Application.Current?.Dispatcher.HasShutdownStarted==true)return false;
        if(AppDialog.Show(null,L.Format("Configuration.WriteRepairMessage",System.IO.Path.GetFileName(path),string.Join(", ",fields)),
            L.Get("Configuration.Title"),MessageBoxButton.YesNo,MessageBoxImage.Warning,"Configuration.BackupRepair","Dialog.Cancel")!=MessageBoxResult.Yes)return false;
        string backup=Backup(path);diagnostics.Write("Configuration.WriteRepair",new {Path=path,Backup=backup});return true;
    }

    internal static string Backup(string path)
    {
        string root=System.IO.Path.GetFullPath(ApplicationPaths.ProfileDirectory);
        string target=System.IO.Path.GetFullPath(path);
        if(!FileNames.Any(name=>StringComparer.OrdinalIgnoreCase.Equals(target,System.IO.Path.Combine(root,name))))
            throw new ArgumentException("Only application configuration files can be repaired.",nameof(path));
        string backup=System.IO.Path.Combine(root,"configuration-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        foreach(string name in FileNames)
        {
            string source=System.IO.Path.Combine(root,name);
            if(File.Exists(source))File.Copy(source,System.IO.Path.Combine(backup,name));
        }
        return backup;
    }
}
