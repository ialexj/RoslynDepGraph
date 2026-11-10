// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Analyzes report roots and their reachable source dependencies using a shared,
/// dynamically expanding work queue and a cache of raw extraction results.
/// </summary>
public sealed class DependencyAnalyzer
{
    private readonly DependencyExtractor _extractor;
    private readonly SymbolContextLookup _lookup;
    private readonly Func<ISymbol, bool>? _isExcluded;

    public DependencyAnalyzer(
        DependencyExtractor extractor,
        SymbolContextLookup lookup,
        Func<ISymbol, bool>? isExcluded = null)
    {
        _extractor = extractor;
        _lookup = lookup;
        _isExcluded = isExcluded;
    }

    public async Task<(IReadOnlyList<NodeEdges> DirectEdges, IReadOnlyList<NodeEdges> FinalEdges)> ComputeAsync(
        IReadOnlyList<SymbolContext> roots,
        bool includeSystem,
        bool excludeSelf,
        Granularity granularity,
        bool transitive,
        CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<AnalysisWork>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
        var extractionCache = new ConcurrentDictionary<ExtractionKey, Lazy<Task<ExtractedDependencies>>>();
        var scheduledClosureNodes = new ConcurrentDictionary<string, byte>();
        var closureEdges = new ConcurrentDictionary<string, NodeEdges>();
        var directEdges = new NodeEdges[roots.Count];
        var pending = roots.Count;
        var queued = 0;
        var completed = 0;
        var progress = new ProgressReporter();

        Console.Error.WriteLine($"Analyzing dependencies for {roots.Count} report node(s)...");
        for (var i = 0; i < roots.Count; i++)
        {
            channel.Writer.TryWrite(new AnalysisWork(roots[i], IsRoot: true, RootIndex: i));
        }
        if (pending == 0)
        {
            channel.Writer.TryComplete();
        }

        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => ProcessQueueAsync(workerCancellation.Token))
            .ToArray();

        async Task ProcessQueueAsync(CancellationToken token)
        {
            try
            {
                await foreach (var work in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var symbol = work.Context.Symbol;
                    var scope = GetScope(symbol);
                    var key = new ExtractionKey(SymbolId.Create(symbol), scope);
                    var cached = extractionCache.GetOrAdd(
                        key,
                        _ => new Lazy<Task<ExtractedDependencies>>(
                            () => _extractor.ExtractAsync(work.Context, scope, token, _isExcluded),
                            LazyThreadSafetyMode.ExecutionAndPublication));
                    var extracted = await cached.Value.ConfigureAwait(false);
                    var added = 0;

                    if (work.IsRoot)
                    {
                        var (types, members) = DependencyFilter.ApplyEdges(
                            extracted, symbol, includeSystem, excludeSelf, granularity == Granularity.Type,
                            _isExcluded);
                        var edges = new NodeEdges(types, members);
                        directEdges[work.RootIndex] = edges;

                        if (transitive)
                        {
                            foreach (var dependency in types.Concat(members))
                            {
                                added += EnqueueClosureNode(dependency);
                            }
                        }
                    }
                    else
                    {
                        var (types, members) = DependencyFilter.ApplyEdges(
                            extracted, symbol, includeSystem, excludeSelf: false, rollupToType: false,
                            isExcluded: _isExcluded);
                        var edges = new NodeEdges(types, members);
                        closureEdges[SymbolId.Create(symbol)] = edges;

                        foreach (var dependency in types.Concat(members))
                        {
                            added += EnqueueClosureNode(dependency);
                        }
                    }

                    var done = Interlocked.Increment(ref completed);
                    progress.ReportQueueProgress(
                        done,
                        Volatile.Read(ref queued),
                        added,
                        SymbolId.Create(symbol));

                    if (Interlocked.Decrement(ref pending) == 0)
                    {
                        channel.Writer.TryComplete();
                    }
                }
            }
            catch
            {
                workerCancellation.Cancel();
                channel.Writer.TryComplete();
                throw;
            }
        }

        int EnqueueClosureNode(Dependency dependency)
        {
            // In member mode, type references are targets, not rolled-up analysis roots.
            if (granularity == Granularity.Member && dependency.Kind == Models.SymbolKind.Type)
            {
                return 0;
            }

            if (dependency.IsExternal ||
                !_lookup.TryGet(dependency.Id, out var context) ||
                _isExcluded?.Invoke(context.Symbol) == true ||
                !scheduledClosureNodes.TryAdd(dependency.Id, 0))
            {
                return 0;
            }

            Interlocked.Increment(ref pending);
            Interlocked.Increment(ref queued);
            if (!channel.Writer.TryWrite(new AnalysisWork(context, IsRoot: false, RootIndex: -1)))
            {
                throw new InvalidOperationException("The dependency analysis queue closed before all work was scheduled.");
            }

            return 1;
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        progress.Complete($"Analyzed {completed} node task(s); queued {queued} additional node(s).");

        if (!transitive)
        {
            return (directEdges, directEdges);
        }

        var finalEdges = new List<NodeEdges>(roots.Count);
        for (var i = 0; i < roots.Count; i++)
        {
            finalEdges.Add(ExpandRoot(directEdges[i], closureEdges));
        }

        return (directEdges, finalEdges);
    }

    private static NodeEdges ExpandRoot(
        NodeEdges rootEdges,
        IReadOnlyDictionary<string, NodeEdges> closureEdges)
    {
        var mergedTypes = rootEdges.Types.ToDictionary(d => d.Id);
        var mergedMembers = rootEdges.Members.ToDictionary(d => d.Id);
        var visited = new HashSet<string>();
        var worklist = new Queue<Dependency>(rootEdges.Types.Concat(rootEdges.Members));

        while (worklist.Count > 0)
        {
            var target = worklist.Dequeue();
            if (!visited.Add(target.Id) || target.IsExternal ||
                !closureEdges.TryGetValue(target.Id, out var dependencies))
            {
                continue;
            }

            AddTransitive(dependencies.Types, mergedTypes, worklist);
            AddTransitive(dependencies.Members, mergedMembers, worklist);
        }

        return new NodeEdges(mergedTypes.Values.ToList(), mergedMembers.Values.ToList());
    }

    private static void AddTransitive(
        IReadOnlyList<Dependency> dependencies,
        IDictionary<string, Dependency> merged,
        Queue<Dependency> worklist)
    {
        foreach (var dependency in dependencies)
        {
            if (merged.ContainsKey(dependency.Id))
            {
                continue;
            }

            var transitive = dependency with { Direct = false };
            merged[dependency.Id] = transitive;
            worklist.Enqueue(transitive);
        }
    }

    private static ExtractionScope GetScope(ISymbol symbol)
    {
        return symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.NamedType
            ? ExtractionScope.RolledUpType
            : ExtractionScope.PerMember;
    }

    private readonly record struct ExtractionKey(string Id, ExtractionScope Scope);

    private sealed record AnalysisWork(SymbolContext Context, bool IsRoot, int RootIndex);
}

/// <summary>
/// Resolves dependency ids back to source contexts for queue expansion.
/// </summary>
public sealed class SymbolContextLookup
{
    private readonly Dictionary<string, SymbolContext> _byId = new();

    public void AddRange(IEnumerable<SymbolContext> contexts)
    {
        foreach (var context in contexts)
        {
            _byId[SymbolId.Create(context.Symbol)] = context;
        }
    }

    public bool TryGet(string id, out SymbolContext context) => _byId.TryGetValue(id, out context!);
}
