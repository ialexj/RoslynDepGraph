// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynDepGraph.Models;

/// <summary>
/// A dependency edge target: a type or member referenced by a node, with all
/// the source locations where the reference occurs.
/// </summary>
public sealed record Dependency
{
    /// <summary>Stable, run-to-run consistent id (SymbolKey string); dedup + graph-link key.</summary>
    public required string Id { get; init; }

    /// <summary>Simple metadata name of the symbol.</summary>
    public required string Name { get; init; }

    /// <summary>Kind of the symbol.</summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>Containing type name, or null for type nodes.</summary>
    public string? ContainingType { get; init; }

    /// <summary>Fully-qualified namespace, or "" for the global namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>Source assembly name OR metadata assembly name.</summary>
    public required string Assembly { get; init; }

    /// <summary>false = defined in the loaded solution (has source location).</summary>
    public required bool IsExternal { get; init; }

    /// <summary>false = only reached via --transitive closure.</summary>
    public required bool Direct { get; init; }

    /// <summary>All source locations where the node references this target.</summary>
    public required IReadOnlyList<SourceLocation> References { get; init; }
}
