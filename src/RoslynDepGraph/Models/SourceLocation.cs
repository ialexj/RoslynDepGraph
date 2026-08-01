// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynDepGraph.Models;

/// <summary>
/// A source location (file path + 1-based line/column) where a dependency is referenced.
/// </summary>
public sealed record SourceLocation(string File, int Line, int Column);
