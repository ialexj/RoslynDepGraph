// Licensed to the project Authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynDepGraph.Models;

public enum Granularity { Type, Member, Both }
public enum OutputFormat { Text, Json, Mermaid }

public sealed record ReportOptions
{
    public required Granularity Granularity { get; init; }
    public bool Transitive { get; init; }
    public bool IncludeSystem { get; init; }
    public bool ExcludeSelf { get; init; }
    public bool IncludeEmpty { get; init; }
    public required IReadOnlyList<string> Include { get; init; }
    public required IReadOnlyList<string> Exclude { get; init; }
}

public sealed record ReportSource
{
    public required string Path { get; init; }
    public required string Kind { get; init; } // "project" | "solution"
}

public sealed record ReportSummary
{
    public required int NodeCount { get; init; }
    public required int EdgeCount { get; init; }
    public required int DirectEdges { get; init; }
    public required int TransitiveEdges { get; init; }
    public required int NodesExcludedByFilter { get; init; }
    public required int NodesOmittedEmpty { get; init; }
}

public sealed record DependencyReport
{
    public required string SchemaVersion { get; init; }
    public required string Tool { get; init; }
    public required string GeneratedAt { get; init; }
    public required ReportOptions Options { get; init; }
    public required ReportSource Source { get; init; }
    public required IReadOnlyList<DependencyNode> Nodes { get; init; }
    public required ReportSummary Summary { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
