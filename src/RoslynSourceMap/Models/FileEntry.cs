// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynSourceMap.Models;

/// <summary>
/// The source map for a single parsed C# file.
/// </summary>
public sealed record FileEntry
{
    public required string Path { get; init; }

    /// <summary>
    /// Top-level type declarations (those declared directly in a namespace or at
    /// file scope, not nested inside another type).
    /// </summary>
    public required IReadOnlyList<MemberEntry> Types { get; init; }

    /// <summary>
    /// Parse diagnostics produced while parsing this file (best-effort extraction
    /// still proceeds even when diagnostics are present).
    /// </summary>
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
