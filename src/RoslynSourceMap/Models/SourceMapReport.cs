// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynSourceMap.Models;

/// <summary>
/// The top-level JSON document produced by RoslynSourceMap.
/// </summary>
public sealed record SourceMapReport
{
    public required string Tool { get; init; }
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<FileEntry> Files { get; init; }
}
