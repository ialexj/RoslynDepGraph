// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Filters extracted dependency edges: drops self-references, applies the
/// system/external filter, and performs member→type rollup for granularity=type.
/// </summary>
public static class DependencyFilter
{
    /// <summary>
    /// Apply edge filtering to a node's extracted dependencies.
    /// </summary>
    /// <param name="extracted">Raw extracted dependencies.</param>
    /// <param name="nodeSymbol">The node whose edges are being filtered.</param>
    /// <param name="includeSystem">If false, restrict targets to source symbols.</param>
    /// <param name="excludeSelf">If true (member nodes only), drop sibling members + containing type.</param>
    /// <param name="rollupToType">If true (granularity=type), map member-targets to their containing type.</param>
    public static (IReadOnlyList<Dependency> Types, IReadOnlyList<Dependency> Members) ApplyEdges(
        ExtractedDependencies extracted,
        ISymbol nodeSymbol,
        bool includeSystem,
        bool excludeSelf,
        bool rollupToType)
    {
        var nodeKey = SymbolId.Create(nodeSymbol);
        var containingType = nodeSymbol.ContainingType;
        var containingTypeKey = containingType is null
            ? null
            : SymbolId.Create(containingType);
        var isMemberNode = nodeSymbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Method
            or Microsoft.CodeAnalysis.SymbolKind.Property
            or Microsoft.CodeAnalysis.SymbolKind.Field
            or Microsoft.CodeAnalysis.SymbolKind.Event;

        // ---- Types ----
        var filteredTypes = new List<Dependency>();
        foreach (var kv in extracted.Types)
        {
            var (key, collected) = (kv.Key, kv.Value);
            var sym = collected.Symbol;

            // Self-edge: drop the node itself.
            if (key == nodeKey)
            {
                continue;
            }

            // For member nodes with excludeSelf: drop the containing type.
            if (excludeSelf && isMemberNode && containingTypeKey is not null && key == containingTypeKey)
            {
                continue;
            }

            // System/external filter.
            if (!includeSystem && !IsSourceSymbol(sym))
            {
                continue;
            }

            filteredTypes.Add(ToDependency(sym, collected.References, direct: true));
        }

        // ---- Members ----
        var filteredMembers = new List<Dependency>();
        foreach (var kv in extracted.Members)
        {
            var (key, collected) = (kv.Key, kv.Value);
            var sym = collected.Symbol;

            // Self-edge: drop the node itself.
            if (key == nodeKey)
            {
                continue;
            }

            // For member nodes with excludeSelf: drop sibling members of the same containing type.
            if (excludeSelf && isMemberNode && containingType is not null)
            {
                var memberContaining = sym.ContainingType;
                if (memberContaining is not null &&
                    SymbolId.Create(memberContaining) == containingTypeKey)
                {
                    continue;
                }
            }

            // System/external filter.
            if (!includeSystem && !IsSourceSymbol(sym))
            {
                continue;
            }

            // Rollup: for granularity=type, map member-targets to their containing type.
            if (rollupToType)
            {
                var targetType = sym.ContainingType;
                if (targetType is null)
                {
                    continue;
                }
                var targetKey = SymbolId.Create(targetType);
                // Don't add a self-edge to the node's own type.
                if (targetKey == nodeKey)
                {
                    continue;
                }
                // Merge into the types list (dedup by key).
                var existing = filteredTypes.FirstOrDefault(d => d.Id == targetKey);
                if (existing is null)
                {
                    filteredTypes.Add(ToDependency(targetType, collected.References, direct: true));
                }
                else
                {
                    // Merge references.
                    var mergedRefs = existing.References.Concat(collected.References).ToList();
                    var idx = filteredTypes.IndexOf(existing);
                    filteredTypes[idx] = existing with { References = mergedRefs };
                }
                continue;
            }

            filteredMembers.Add(ToDependency(sym, collected.References, direct: true));
        }

        // Dedup types after potential rollup merges.
        var dedupedTypes = DedupTypes(filteredTypes);

        return (dedupedTypes, filteredMembers);
    }

    private static bool IsSourceSymbol(ISymbol symbol)
    {
        if (symbol is ITypeSymbol t && t.SpecialType != SpecialType.None)
        {
            return false;
        }
        return symbol.Locations.Any(l => l.IsInSource);
    }

    private static Dependency ToDependency(ISymbol symbol, IReadOnlyList<SourceLocation> references, bool direct)
    {
        // Dedup reference locations (the walker visits nested nodes, producing duplicates).
        var dedupedRefs = references
            .Distinct()
            .OrderBy(r => r.File)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();

        var containing = symbol.ContainingType;
        return new Dependency
        {
            Id = SymbolId.Create(symbol),
            Name = symbol.Name,
            Kind = ToModelKind(symbol),
            ContainingType = containing?.Name,
            Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? ""
                : symbol.ContainingNamespace?.ToDisplayString() ?? "",
            Assembly = symbol.ContainingAssembly?.Name ?? "",
            IsExternal = !symbol.Locations.Any(l => l.IsInSource),
            Direct = direct,
            References = dedupedRefs,
        };
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

    private static List<Dependency> DedupTypes(List<Dependency> types)
    {
        var byId = new Dictionary<string, Dependency>();
        foreach (var d in types)
        {
            if (!byId.TryGetValue(d.Id, out var existing))
            {
                byId[d.Id] = d;
            }
            else
            {
                var mergedRefs = existing.References.Concat(d.References).ToList();
                byId[d.Id] = existing with { References = mergedRefs };
            }
        }
        return byId.Values.ToList();
    }
}
