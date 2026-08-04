// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynSourceMap.Models;

/// <summary>
/// A single parameter of a method, constructor, indexer, operator, or delegate.
/// </summary>
public sealed record ParameterEntry
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public required IReadOnlyList<string> Modifiers { get; init; }
    public string? DefaultValue { get; init; }
}
