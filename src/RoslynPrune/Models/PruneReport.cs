// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynPrune.Models;

/// <summary>
/// The top-level JSON document produced by RoslynPrune.
/// </summary>
public sealed record PruneReport
{
    public required string Tool { get; init; }

    public required string SchemaVersion { get; init; }

    /// <summary>True when <c>--apply</c> was passed and files were written.</summary>
    public required bool Applied { get; init; }

    public required PruneSummary Summary { get; init; }

    public required IReadOnlyList<RemovedType> RemovedTypes { get; init; }

    public required IReadOnlyList<RemovedMember> RemovedMembers { get; init; }

    public required IReadOnlyList<RemovedUsing> RemovedUsings { get; init; }

    public required IReadOnlyList<string> WrittenFiles { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}
