// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Computes the transitive closure of dependency edges: for each dependency target,
/// its own dependencies are also followed and added (flagged as transitive).
/// Cycle-safe via a visited set keyed by SymbolKey.
/// </summary>
public sealed class TransitiveClosure
{
    private readonly DependencyExtractor _extractor;
    private readonly SymbolContextLookup _lookup;

    public TransitiveClosure(DependencyExtractor extractor, SymbolContextLookup lookup)
    {
        _extractor = extractor;
        _lookup = lookup;
    }

    /// <summary>
    /// Expand the direct edges of each node with their transitive closure.
    /// </summary>
    /// <param name="nodes">Nodes with their direct (already-filtered) edges.</param>
    /// <param name="includeSystem">Echo of the include-system option (for re-filtering transitive edges).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Nodes with direct + transitive edges merged.</returns>
    public async Task<IReadOnlyList<NodeEdges>> ComputeAsync(
        IReadOnlyList<(SymbolContext Node, NodeEdges DirectEdges)> nodes,
        bool includeSystem,
        CancellationToken ct = default)
    {
        var result = new List<NodeEdges>();

        foreach (var (node, directEdges) in nodes)
        {
            var mergedTypes = new Dictionary<string, Dependency>();
            var mergedMembers = new Dictionary<string, Dependency>();

            // Seed with direct edges.
            foreach (var d in directEdges.Types)
            {
                mergedTypes[d.Id] = d;
            }
            foreach (var d in directEdges.Members)
            {
                mergedMembers[d.Id] = d;
            }

            // Worklist of targets to expand.
            var visited = new HashSet<string>();
            var worklist = new Queue<Dependency>();
            foreach (var d in directEdges.Types) worklist.Enqueue(d);
            foreach (var d in directEdges.Members) worklist.Enqueue(d);

            while (worklist.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var target = worklist.Dequeue();
                if (!visited.Add(target.Id))
                {
                    continue;
                }

                // Only source symbols can be expanded (external ones have no source to walk).
                if (target.IsExternal)
                {
                    continue;
                }

                // Look up the SymbolContext for this target.
                if (!_lookup.TryGet(target.Id, out var targetContext))
                {
                    continue;
                }

                // Determine the scope: type nodes use rolled-up; member nodes use per-member.
                var scope = target.Kind == Models.SymbolKind.Type
                    ? ExtractionScope.RolledUpType
                    : ExtractionScope.PerMember;

                var extracted = await _extractor.ExtractAsync(targetContext, scope, ct).ConfigureAwait(false);

                // Re-filter the extracted edges (self-ref + system filter; no rollup here,
                // no excludeSelf since these are not the node's own siblings).
                var (tTypes, tMembers) = DependencyFilter.ApplyEdges(
                    extracted, targetContext.Symbol, includeSystem, excludeSelf: false, rollupToType: false);

                foreach (var d in tTypes)
                {
                    if (!mergedTypes.ContainsKey(d.Id))
                    {
                        mergedTypes[d.Id] = d with { Direct = false };
                        worklist.Enqueue(d with { Direct = false });
                    }
                }
                foreach (var d in tMembers)
                {
                    if (!mergedMembers.ContainsKey(d.Id))
                    {
                        mergedMembers[d.Id] = d with { Direct = false };
                        worklist.Enqueue(d with { Direct = false });
                    }
                }
            }

            result.Add(new NodeEdges(
                Types: mergedTypes.Values.ToList(),
                Members: mergedMembers.Values.ToList()));
        }

        return result;
    }
}

/// <summary>
/// Resolves a dependency id (SymbolKey string) back to a SymbolContext for closure expansion.
/// </summary>
public sealed class SymbolContextLookup
{
    private readonly Dictionary<string, SymbolContext> _byId = new();

    public void AddRange(IEnumerable<SymbolContext> contexts)
    {
        foreach (var c in contexts)
        {
            var key = SymbolId.Create(c.Symbol);
            _byId[key] = c;
        }
    }

    public bool TryGet(string id, out SymbolContext context) => _byId.TryGetValue(id, out context!);
}
