// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynDepGraph.Models;

/// <summary>
/// A node in the dependency graph: a reported symbol with its outgoing edges.
/// </summary>
public sealed record DependencyNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public string? ContainingType { get; init; }
    public required string Namespace { get; init; }
    public required SourceLocation Location { get; init; }
    public required NodeEdges Edges { get; init; }
}

public sealed record NodeEdges(IReadOnlyList<Dependency> Types, IReadOnlyList<Dependency> Members);
