using System.Runtime.CompilerServices;

namespace FloatCanvas.Core;

/// <summary>Only use with immutable, frozen definitions. Weak keys release replaced contexts.</summary>
public sealed class SceneEvaluationCache
{
    private readonly ConditionalWeakTable<PrefabDefinition, Lazy<EvaluatedScene>> definitions = new();
    private long builds;
    public long DefinitionBuildCount => Interlocked.Read(ref builds);
    internal EvaluatedScene Definition(PrefabDefinition definition, List<PrefabDefinition> closure) =>
        definitions.GetValue(definition, key => new(() =>
        {
            Interlocked.Increment(ref builds);
            return SceneEvaluation.Build(new SceneDocument { Elements = key.Graph.Elements, Groups = key.Graph.Groups,
                Instances = key.Graph.Instances, RootOrder = key.Graph.RootOrder, Definitions = closure }, this);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
