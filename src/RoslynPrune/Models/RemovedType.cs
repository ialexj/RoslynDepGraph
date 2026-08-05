// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynPrune.Models;

/// <summary>
/// A type that was (or would be) removed because nothing in the solution
/// references it.
/// </summary>
public sealed record RemovedType
{
    public required string FullyQualifiedName { get; init; }

    public required string Kind { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }
}
