// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynPrune.Models;

/// <summary>
/// Summary counts for a prune run.
/// </summary>
public sealed record PruneSummary
{
    public required int CandidateTypes { get; init; }

    public required int RemovedTypeCount { get; init; }

    public required int RemovedMemberCount { get; init; }

    public required int RemovedUsingCount { get; init; }

    public required int WrittenFileCount { get; init; }

    public required int Passes { get; init; }
}
