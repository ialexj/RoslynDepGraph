// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynPrune.Models;

/// <summary>
/// A using directive that was (or would be) removed.
/// </summary>
public sealed record RemovedUsing
{
    public required string File { get; init; }

    public required int Line { get; init; }

    public required string Directive { get; init; }

    public required UsingRemovalReason Reason { get; init; }
}
