using System.Diagnostics;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class LayerRoundTwoTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        CanvasElement Box(string name) => new() { Name = name, Kind = ElementKind.Rectangle, Width = 20, Height = 20 };
        var a = Box("A"); var b = Box("B"); b.Visible = false;
        var session = new EditorSession(new() { Elements = [a,b] });
        var result = session.SetNodesFlagDetailed([a.Id,b.Id], true, false);
        check("flag result distinguishes changed and already matched nodes", result == new LayerFlagResult(1,1,0) && session.UndoCount == 1);
        int history = session.UndoCount;
        check("already matched flags add no history", session.SetNodesFlagDetailed([a.Id,b.Id],true,false).Unchanged == 2 && session.UndoCount == history);
        long version = session.HistoryNavigationVersion;
        session.Undo(); session.Redo();
        check("successful history navigation advances runtime version", session.HistoryNavigationVersion == version + 2);
        var group = new CanvasGroup { Name = "Locked", Locked = true, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var child = Box("Child"); child.GroupId = group.Id; group.ChildOrder.Add(child.Id);
        session = new(new() { Groups = [group], Elements = [child], RootOrder = [group.Id] });
        session.SetEditScope(group.Id);
        check("locked ordinary group remains navigable", session.EditScopeId == group.Id && !session.CanInsertInEditScope);
        bool rejected = false;
        try { session.BeginTransaction("Forbidden edit"); } catch (InvalidOperationException) { rejected = true; }
        check("locked browsing cannot start an author transaction", rejected && !session.IsTransactionActive && session.UndoCount == 0);
        session.LeaveScope(); session.SetNodeLocked(group.Id, false); session.SetEditScope(group.Id);
        check("unlocking in the parent restores ordinary scope authoring", session.CanInsertInEditScope);
        var definition = new PrefabDefinition { Name = "Shared", Graph = new() { Elements = [Box("Source")] } };
        var owner = new EditorSession(new() { Elements = Enumerable.Range(0,10000).Select(i => Box("Owner " + i)).ToList(), Definitions = [definition] });
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        owner.Document.Instances.Add(instance); owner.Document.RootOrder = owner.Document.Elements.Select(e => e.Id).Append(instance.Id).ToList();
        var draft = owner.BeginPrefabEdit(definition.Id);
        var context = new PrefabEditContext(owner,draft,default,EditorTool.Select) { EnteredInstanceId = instance.Id };
        var scene = new GroupCanvasScene(); var view = scene.Capture(draft.Session,context,GroupCanvasMode.RootProject,default,owner.Assets);
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        bool reused = true;
        for (int i=0;i<100;i++) reused &= ReferenceEquals(view,scene.Capture(draft.Session,context,GroupCanvasMode.RootProject,default,owner.Assets));
        check("unchanged preview reuses captured view", reused);
        allocated = GC.GetAllocatedBytesForCurrentThread()-allocated;
        check("preview cache hits allocate under 64 KiB per query", allocated/100 < 65536);
        Console.WriteLine($"ROUND_TWO_CACHE nodes=10000 meanMs={timer.Elapsed.TotalMilliseconds/100:F4} bytesPerCall={allocated/100}");
        owner.SetNodeVisible(instance.Id,false);
        check("hidden editing instance has no project editing placement", scene.Capture(draft.Session,context,GroupCanvasMode.RootProject,default,owner.Assets).EditingPlacement is null);
        foreach (int count in new[] {500,1000,2000})
        {
            var times = new List<double>(); long maxBytes=0;
            for (int sample=0;sample<21;sample++)
            {
                var editor = new EditorSession(new() { Elements = Enumerable.Range(0,count).Select(i=>Box("Layer "+i)).ToList() });
                var ids = editor.Document.Elements.Select(e=>e.Id).ToArray();
                long bytes = GC.GetAllocatedBytesForCurrentThread(); timer.Restart(); editor.SetNodesFlag(ids,true,false); timer.Stop();
                if(sample>0) { times.Add(timer.Elapsed.TotalMilliseconds); maxBytes=Math.Max(maxBytes,GC.GetAllocatedBytesForCurrentThread()-bytes); }
            }
            times.Sort(); Console.WriteLine($"ROUND_TWO_FLAGS count={count} medianMs={times[10]:F2} p95Ms={times[18]:F2} maxMB={maxBytes/1048576d:F2}");
            check("batch allocations remain bounded at "+count,maxBytes < 64L*1024*1024);
        }
    }
}
