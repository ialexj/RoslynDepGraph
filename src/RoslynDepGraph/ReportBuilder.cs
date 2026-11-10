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
                candidates.Add(new FilteredNode(t, NodeFilter.FullyQualifiedName(t.Symbol)));
            }
        }
        if (options.Granularity is Granularity.Member or Granularity.Both)
        {
            foreach (var kv in membersByType)
            {
                foreach (var m in kv.Value)
                {
                    candidates.Add(new FilteredNode(m, NodeFilter.FullyQualifiedName(m.Symbol)));
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

        // 5. Analyze report nodes and, if requested, their reachable dependencies.
        var analyzer = new DependencyAnalyzer(
            _extractor, lookup,
            options.Exclude.Count == 0 ? null : NodeFilter.CreateExclusionPredicate(options.Exclude));
        var (allDirectEdges, allFinalEdges) = await analyzer.ComputeAsync(
            surviving.Select(n => n.Context).ToList(),
            options.IncludeSystem,
            options.ExcludeSelf,
            options.Granularity,
            options.Transitive,
            ct).ConfigureAwait(false);

        var nodesWithDirectEdges = new List<(SymbolContext Node, NodeEdges DirectEdges)>(surviving.Count);
        var finalEdges = new List<NodeEdges>(surviving.Count);
        var emptyCount = 0;
        for (var i = 0; i < surviving.Count; i++)
        {
            var directEdges = allDirectEdges[i];
            if (directEdges.Types.Count == 0 && directEdges.Members.Count == 0)
            {
                emptyCount++;
                if (!options.IncludeEmpty)
                {
                    continue;
                }
            }

            nodesWithDirectEdges.Add((surviving[i].Context, directEdges));
            finalEdges.Add(allFinalEdges[i]);
        }

        // 6. Count edges.
        int transitiveEdgeCount;
        if (options.Transitive)
        {
            transitiveEdgeCount = finalEdges.Sum(e => e.Types.Count(d => !d.Direct) + e.Members.Count(d => !d.Direct));
        }
        else
        {
            transitiveEdgeCount = 0;
        }

        // 7. Assemble nodes.
        var nodes = new List<DependencyNode>();
        for (var i = 0; i < nodesWithDirectEdges.Count; i++)
        {
            var (ctx, _) = nodesWithDirectEdges[i];
            nodes.Add(CreateNode(ctx.Symbol, finalEdges[i]));
        }

        var bodyDependencies = new List<DependencyNode>();
        if (options.Transitive)
        {
            var emittedIds = nodes.Select(n => n.Id).ToHashSet();
            var dependencies = finalEdges
                .SelectMany(e => e.Types.Concat(e.Members))
                .Where(d => !d.IsExternal && (options.Granularity switch
                {
                    Granularity.Member => d.Kind != Models.SymbolKind.Type,
                    Granularity.Type => d.Kind == Models.SymbolKind.Type,
                    _ => true,
                }))
                .OrderBy(d => d.Id, StringComparer.Ordinal);
            foreach (var dependency in dependencies)
            {
                if (emittedIds.Add(dependency.Id) && lookup.TryGet(dependency.Id, out var context))
                {
                    bodyDependencies.Add(CreateNode(context.Symbol, new NodeEdges([], [])));
                }
            }
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
            BodyDependencies = bodyDependencies,
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

    private static DependencyNode CreateNode(ISymbol symbol, NodeEdges edges) => new()
    {
        Id = SymbolId.Create(symbol),
        Name = symbol.Name,
        Kind = ToModelKind(symbol),
        ContainingType = symbol.ContainingType?.Name,
        Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
            ? ""
            : symbol.ContainingNamespace?.ToDisplayString() ?? "",
        Location = ToSourceLocation(symbol),
        Edges = edges,
        Declarations = symbol switch
        {
            IMethodSymbol { PartialImplementationPart: { } implementation } =>
                symbol.DeclaringSyntaxReferences.AddRange(implementation.DeclaringSyntaxReferences),
            IPropertySymbol { PartialImplementationPart: { } implementation } =>
                symbol.DeclaringSyntaxReferences.AddRange(implementation.DeclaringSyntaxReferences),
            _ => symbol.DeclaringSyntaxReferences,
        },
    };

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
