// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Orchestrates the full report build: enumerate → filter → extract → filter-edges →
/// (transitive?) → assemble <see cref="DependencyReport"/>.
/// </summary>
public sealed class ReportBuilder
{
    private readonly DependencyExtractor _extractor = new();

    public async Task<DependencyReport> BuildAsync(
        Solution solution,
        string sourcePath,
        string sourceKind,
        ReportOptions options,
        CancellationToken ct = default)
    {
        // 1. Enumerate source types and members.
        var types = await SymbolEnumerator.GetSourceTypesAsync(solution, ct).ConfigureAwait(false);
        var membersByType = new Dictionary<string, IReadOnlyList<SymbolContext>>();
        foreach (var t in types)
        {
            var members = await SymbolEnumerator.GetSourceMembersAsync(t, ct).ConfigureAwait(false);
            membersByType[SymbolId.Create(t.Symbol)] = members;
        }

        // 2. Build the candidate node set per granularity.
        var candidates = new List<FilteredNode>();
        if (options.Granularity is Granularity.Type or Granularity.Both)
        {
            foreach (var t in types)
            {
                candidates.Add(new FilteredNode(t, FullyQualifiedName(t.Symbol)));
            }
        }
        if (options.Granularity is Granularity.Member or Granularity.Both)
        {
            foreach (var kv in membersByType)
            {
                foreach (var m in kv.Value)
                {
                    candidates.Add(new FilteredNode(m, FullyQualifiedName(m.Symbol)));
                }
            }
        }

        // 3. Apply --include/--exclude node filters.
        var (surviving, removedByFilter) = NodeFilter.Select(candidates, options.Include, options.Exclude);

        // 4. Build the lookup for transitive closure (all source symbols by id).
        var lookup = new SymbolContextLookup();
        lookup.AddRange(types);
        foreach (var kv in membersByType)
        {
            lookup.AddRange(kv.Value);
        }

        // 5. Extract + filter edges for each surviving node.
        var rollupToType = options.Granularity == Granularity.Type;
        var nodesWithDirectEdges = new List<(SymbolContext Node, NodeEdges DirectEdges)>();
        var emptyCount = 0;

        var progress = new ProgressReporter();
        Console.Error.WriteLine($"Extracting dependencies for {surviving.Count} node(s)...");
        for (var i = 0; i < surviving.Count; i++)
        {
            var fn = surviving[i];
            var symbol = fn.Context.Symbol;

            progress.Report(i, surviving.Count, fn.FullyQualifiedName);

            var scope = symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.NamedType
                ? ExtractionScope.RolledUpType
                : ExtractionScope.PerMember;

            var extracted = await _extractor.ExtractAsync(fn.Context, scope, ct).ConfigureAwait(false);
            var (t, m) = DependencyFilter.ApplyEdges(
                extracted, symbol, options.IncludeSystem, options.ExcludeSelf, rollupToType);

            var edges = new NodeEdges(Types: t, Members: m);
            if (t.Count == 0 && m.Count == 0)
            {
                emptyCount++;
                if (!options.IncludeEmpty)
                {
                    continue;
                }
            }
            nodesWithDirectEdges.Add((fn.Context, edges));
        }

        progress.Complete($"Extracted {nodesWithDirectEdges.Count} node(s) with edges.");

        // 6. Transitive closure (if requested).
        IReadOnlyList<NodeEdges> finalEdges;
        int transitiveEdgeCount;
        if (options.Transitive)
        {
            var closure = new TransitiveClosure(_extractor, lookup);
            finalEdges = await closure.ComputeAsync(nodesWithDirectEdges, options.IncludeSystem, ct).ConfigureAwait(false);
            transitiveEdgeCount = finalEdges.Sum(e => e.Types.Count(d => !d.Direct) + e.Members.Count(d => !d.Direct));
        }
        else
        {
            finalEdges = nodesWithDirectEdges.Select(x => x.DirectEdges).ToList();
            transitiveEdgeCount = 0;
        }

        // 7. Assemble nodes.
        var nodes = new List<DependencyNode>();
        for (var i = 0; i < nodesWithDirectEdges.Count; i++)
        {
            var (ctx, _) = nodesWithDirectEdges[i];
            var symbol = ctx.Symbol;
            nodes.Add(new DependencyNode
            {
                Id = SymbolId.Create(symbol),
                Name = symbol.Name,
                Kind = ToModelKind(symbol),
                ContainingType = symbol.ContainingType?.Name,
                Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
                    ? ""
                    : symbol.ContainingNamespace?.ToDisplayString() ?? "",
                Location = ToSourceLocation(symbol),
                Edges = finalEdges[i],
            });
        }

        // 8. Summary.
        var directEdgeCount = finalEdges.Sum(e => e.Types.Count(d => d.Direct) + e.Members.Count(d => d.Direct));
        var totalEdges = finalEdges.Sum(e => e.Types.Count + e.Members.Count);

        var diagnostics = new List<string>();
        if (removedByFilter > 0)
        {
            diagnostics.Add($"Excluded {removedByFilter} node(s) via --include/--exclude filters.");
        }
        if (emptyCount > 0 && !options.IncludeEmpty)
        {
            diagnostics.Add($"Omitted {emptyCount} node(s) with no edges (use --include-empty to show).");
        }

        return new DependencyReport
        {
            SchemaVersion = "1.1",
            Tool = "RoslynDepGraph",
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Options = options,
            Source = new ReportSource { Path = sourcePath, Kind = sourceKind },
            Nodes = nodes,
            Summary = new ReportSummary
            {
                NodeCount = nodes.Count,
                EdgeCount = totalEdges,
                DirectEdges = directEdgeCount,
                TransitiveEdges = transitiveEdgeCount,
                NodesExcludedByFilter = removedByFilter,
                NodesOmittedEmpty = options.IncludeEmpty ? 0 : emptyCount,
            },
            Diagnostics = diagnostics,
        };
    }

    private static string FullyQualifiedName(ISymbol symbol)
    {
        if (symbol.ContainingType is not null)
        {
            return $"{FullyQualifiedName(symbol.ContainingType)}.{symbol.Name}";
        }
        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            return $"{ns.ToDisplayString()}.{symbol.Name}";
        }
        return symbol.Name;
    }

    private static Models.SymbolKind ToModelKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.NamedType => Models.SymbolKind.Type,
            Microsoft.CodeAnalysis.SymbolKind.Method => Models.SymbolKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => Models.SymbolKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => Models.SymbolKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Event => Models.SymbolKind.Event,
            _ => Models.SymbolKind.Type,
        };
    }

    private static SourceLocation ToSourceLocation(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource)
            ?? symbol.Locations.FirstOrDefault();
        if (loc is null)
        {
            return new SourceLocation("", 0, 0);
        }
        var lineSpan = loc.GetLineSpan();
        return new SourceLocation(
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }
}
