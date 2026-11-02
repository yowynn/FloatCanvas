using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PresentationStorageTests
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"presentation-storage",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var store=new ProjectStore("0.4.0-m6-test");var assets=new AssetLibrary();
        var leaf=new CanvasElement{Kind=ElementKind.Presentation,Width=360,Height=200,Presentation=new(){Enabled=false,Modules=PresentationModules.None,
            TrailMilliseconds=1700,CursorRadiusPixels=27,KeyDisplay=PresentationKeyDisplayMode.AllPhysicalKeys},Style=new(){Foreground=0x00112233,Background=0x7F445566}};
        var group=new CanvasGroup{OrderMode=GroupOrderMode.AtomicBlock,ChildOrder=[leaf.Id],Transform=Affine2D.Translation(37,41),ElevationScale=-2};leaf.GroupId=group.Id;
        var definition=new PrefabDefinition{Graph=new(){Elements=[leaf],Groups=[group],RootOrder=[group.Id]}};
        var instance=new PrefabInstance{DefinitionId=definition.Id,Transform=Affine2D.Rotation(17)};
        var document=new SceneDocument{Definitions=[definition],Instances=[instance],RootOrder=[instance.Id]};
        string path=Path.Combine(root,"presentation.floatcanvas"),before=JsonSerializer.Serialize(document);
        await store.SaveAsync(path,document,assets);var loaded=(await store.LoadAsync(path)).Document;
        check(loaded.Version==SceneDocument.CurrentVersion&&JsonSerializer.Serialize(loaded)==before,"current format stores disabled format13 presentation settings and hidden module values in shared definitions");
        string old14=Path.Combine(root,"true-v14-log.floatcanvas");File.Copy(path,old14);
        await Edit(old14,"manifest.json",n=>n["Version"]=14);
        await Edit(old14,"scene.json",n=>{n["Version"]=14;Settings(n).Remove("DisplayStyle");Settings(n).Remove("KeyboardLayout");});
        byte[] old14Bytes=await File.ReadAllBytesAsync(old14);var migrated14=(await store.LoadAsync(old14)).Document;
        check(migrated14.Version==SceneDocument.CurrentVersion&&JsonSerializer.Serialize(migrated14)==before&&
            (await File.ReadAllBytesAsync(old14)).AsSpan().SequenceEqual(old14Bytes),
            "v14 keyboard logs migrate to the current format with Log and implicit full layout without changing placement colors identity or source bytes");
        foreach(var style in Enum.GetValues<PresentationDisplayStyle>())
        {
            var styled=document.Clone();var settings=styled.Definitions[0].Graph.Elements[0].Presentation!;settings.DisplayStyle=style;
            settings.KeyboardLayout=PresentationKeyboardLayout.CreateDefault();
            foreach(var key in settings.KeyboardLayout.Keys)key.Enabled=key.VirtualKey is 0x57 or 0x41 or 0x53 or 0x44;
            settings.KeyboardLayout.Keys.Single(key=>key.VirtualKey==0x57).X=25;settings.KeyboardLayout.Keys.Single(key=>key.VirtualKey==0x57).Height=2;
            string stylePath=Path.Combine(root,"style-"+style+".floatcanvas");await store.SaveAsync(stylePath,styled,assets);
            check(JsonSerializer.Serialize((await store.LoadAsync(stylePath)).Document)==JsonSerializer.Serialize(styled),
                "v15 roundtrips authored style and full custom key identities geometry and enabled state: "+style);
            if(style==PresentationDisplayStyle.Keyboard)
            {
                foreach(var invalidField in new[]{"ScanCode","IsExtended","Enabled","X","Width"})
                {
                    string invalidPath=stylePath+invalidField;File.Copy(stylePath,invalidPath);
                    await Edit(invalidPath,"scene.json",n=>Settings(n)["KeyboardLayout"]!["Keys"]![0]!.AsObject().Remove(invalidField));
                    bool bad=false;try{await store.LoadAsync(invalidPath);}catch(InvalidDataException){bad=true;}
                    check(bad,"v15 rejects partial custom physical key definitions: "+invalidField);
                }
                string legacyEnvelope=stylePath+"-old-envelope";File.Copy(stylePath,legacyEnvelope);
                await Edit(legacyEnvelope,"manifest.json",n=>n["Version"]=14);await Edit(legacyEnvelope,"scene.json",n=>n["Version"]=14);
                bool oldEnvelopeRejected=false;try{await store.LoadAsync(legacyEnvelope);}catch(InvalidDataException){oldEnvelopeRejected=true;}
                check(oldEnvelopeRejected,"new keyboard display modes cannot hide inside an older version envelope");
                var keyboardPackage=new PrefabPackage{Definitions=styled.Definitions.Select(value=>value.Clone()).ToList(),EntryDefinitionId=definition.Id};
                var keyboardPrefabStore=new PrefabStore("0.4.0-styles-test");string keyboardPrefab=Path.Combine(root,"keyboard.floatprefab");
                await keyboardPrefabStore.SaveAsync(keyboardPrefab,keyboardPackage,assets);
                check(JsonSerializer.Serialize((await keyboardPrefabStore.LoadAsync(keyboardPrefab)).Package)==JsonSerializer.Serialize(keyboardPackage),
                    "v15 prefab retains the owned custom keyboard layout without runtime held data");
            }
        }
        var copy=document.Clone();copy.Definitions[0].Graph.Elements[0].Presentation!.CursorRadiusPixels=99;
        check(leaf.Presentation!.CursorRadiusPixels==27,"definition graph clone owns its presentation configuration");
        var prefabStore=new PrefabStore("0.4.0-m6-test");var package=new PrefabPackage{Definitions=[definition.Clone()],EntryDefinitionId=definition.Id};
        string prefab=Path.Combine(root,"presentation.floatprefab");await prefabStore.SaveAsync(prefab,package,assets);
        check(JsonSerializer.Serialize((await prefabStore.LoadAsync(prefab)).Package)==JsonSerializer.Serialize(package),"format13 prefab roundtrip preserves presentation source configuration without runtime state");
        var recovery=new RecoveryStore(Path.Combine(root,"recovery"));var capture=document.Clone();capture.Kind=DocumentKind.FrozenCapture;
        var workspace=new WorkspaceState{LiveDocument=document,CaptureDocument=capture,PendingPrefabEdit=new(){OwnerResourceId=document.ResourceId,
            DefinitionId=definition.Id,ExpectedRevision=definition.Revision,DraftDocument=document.Clone()}};
        await recovery.SaveAsync(workspace,assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State)==JsonSerializer.Serialize(workspace),"presentation configuration survives live/capture and recoverable definition draft packages");
        foreach(var mutation in new (string Name,Action<JsonObject> Edit)[]{
            ("missing-settings",n=>Leaf(n).Remove("Presentation")),("null-settings",n=>Leaf(n)["Presentation"]=null),
            ("missing-modules",n=>Settings(n).Remove("Modules")),("unknown-module-bit",n=>Settings(n)["Modules"]=16),
            ("unknown-key-mode",n=>Settings(n)["KeyDisplay"]=2),("unknown-alignment",n=>Settings(n)["KeyPanelAlignment"]=6),
            ("unknown-display-style",n=>Settings(n)["DisplayStyle"]=3),
            ("invalid-hidden-width",n=>Settings(n)["TrailWidthPixels"]=0),("wrong-kind",n=>Leaf(n)["Kind"]=(int)ElementKind.Rectangle),
            ("runtime-injection",n=>Settings(n)["Frame"]=new JsonObject{["PressedKeys"]=new JsonArray("secret")}),
            ("point-payload",n=>Leaf(n)["Points"]=new JsonArray(new JsonObject{["X"]=1,["Y"]=1})),
            ("zero-region",n=>Leaf(n)["Width"]=0)})
        {
            string invalid=Path.Combine(root,mutation.Name+".floatcanvas");File.Copy(path,invalid);await Edit(invalid,"scene.json",mutation.Edit);
            bool rejected=false;try{await store.LoadAsync(invalid);}catch(InvalidDataException){rejected=true;}
            check(rejected,"format13 rejects invalid nested presentation source atomically: "+mutation.Name);
        }
        string missingFields=Path.Combine(root,"missing-required-settings.floatcanvas");
        foreach(string field in JsonSerializer.SerializeToNode(new PresentationSettings())!.AsObject().Select(pair=>pair.Key))
        {
            string invalid=missingFields+field;File.Copy(path,invalid);await Edit(invalid,"scene.json",n=>Settings(n).Remove(field));
            bool rejected=false;try{await store.LoadAsync(invalid);}catch(InvalidDataException){rejected=true;}
            check(rejected,"each authored presentation setting is explicit in canonical format13: "+field);
        }
        string hiddenOld=Path.Combine(root,"new-kind-old12.floatcanvas");File.Copy(path,hiddenOld);
        await Edit(hiddenOld,"manifest.json",n=>n["Version"]=12);await Edit(hiddenOld,"scene.json",n=>n["Version"]=12);
        bool oldRejected=false;try{await store.LoadAsync(hiddenOld);}catch(InvalidDataException){oldRejected=true;}
        check(oldRejected,"format12 cannot hide a presentation kind/config inside a legacy envelope");
        // A true v12 palette/graph stays byte-for-byte the same in content after only upgrading Version.
        var oldDocument=document.Clone();var oldLeaf=oldDocument.Definitions[0].Graph.Elements[0];oldLeaf.Kind=ElementKind.Number;oldLeaf.Presentation=null;
        oldLeaf.Style.Variant=ElementAppearance.NumberOutlineIndependent;oldLeaf.NumberTextSizeMode=NumberTextSizeMode.CapFontSize;oldLeaf.Style.FontSize=11;
        oldDocument.Canvas.Grid.PlaneCellSize=17;oldDocument.Canvas.Grid.PlaneOrigin=new(2,3);
        string oldPath=Path.Combine(root,"true-v12.floatcanvas");await store.SaveAsync(oldPath,oldDocument,assets);
        await Edit(oldPath,"manifest.json",n=>n["Version"]=12);await Edit(oldPath,"scene.json",n=>n["Version"]=12);
        byte[] oldBytes=await File.ReadAllBytesAsync(oldPath);var migrated=(await store.LoadAsync(oldPath)).Document;
        check(migrated.Version==SceneDocument.CurrentVersion&&migrated.ResourceIdentityPersistent&&migrated.ResourceId==oldDocument.ResourceId&&
            JsonSerializer.Serialize(migrated)==JsonSerializer.Serialize(oldDocument)&&(await File.ReadAllBytesAsync(oldPath)).AsSpan().SequenceEqual(oldBytes),
            "true v12-to13 migration preserves canonical colors/third style/typography/local group matrix/elevation/order/grid/identity and source bytes");
        var oldPackage=new PrefabPackage{Definitions=migrated.Definitions.Select(item=>item.Clone()).ToList(),EntryDefinitionId=definition.Id};
        string oldPrefab=Path.Combine(root,"true-v12.floatprefab");await prefabStore.SaveAsync(oldPrefab,oldPackage,assets);
        await Edit(oldPrefab,"manifest.json",n=>n["Version"]=12);await Edit(oldPrefab,"prefab.json",n=>n["Version"]=12);
        check(JsonSerializer.Serialize((await prefabStore.LoadAsync(oldPrefab)).Package)==JsonSerializer.Serialize(oldPackage),"true v12 prefab explicitly upgrades without resetting shared graph contents");
        oldDocument.Version=12;oldDocument.Definitions[0].Graph.Elements[0].Presentation=new();
        bool saveRejected=false;try{await store.SaveAsync(path,oldDocument,assets);}catch(InvalidDataException){saveRejected=true;}
        check(saveRejected&&JsonSerializer.Serialize(document)==before,"typed legacy scene cannot publish a hidden new configuration before save upgrade");
        Console.WriteLine("Presentation storage artifacts: "+root);
    }
    private static JsonObject Leaf(JsonObject json)=>json["Definitions"]![0]!["Graph"]!["Elements"]![0]!.AsObject();
    private static JsonObject Settings(JsonObject json)=>Leaf(json)["Presentation"]!.AsObject();
    private static async Task Edit(string path,string name,Action<JsonObject> edit)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Update);var entry=zip.GetEntry(name)!;JsonObject json;
        using(var reader=new StreamReader(entry.Open()))json=JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();edit(json);LegacyFixtureSchema.RemoveFutureRootFields(json);entry.Delete();
        using var writer=new StreamWriter(zip.CreateEntry(name).Open());await writer.WriteAsync(json.ToJsonString());
    }
}
