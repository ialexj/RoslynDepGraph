// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynDepGraph.Models;

/// <summary>
/// The kind of a symbol, as reported in the dependency graph.
/// </summary>
public enum SymbolKind
{
    Type,
    Method,
    Property,
    Field,
    Event,
}
