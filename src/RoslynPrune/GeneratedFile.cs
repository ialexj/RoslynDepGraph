// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace RoslynPrune;

/// <summary>
/// Helpers for identifying generated files that should never be pruned or
/// rewritten (e.g. files under <c>obj/</c> or <c>bin/</c>).
/// </summary>
public static class GeneratedFile
{
    /// <summary>
    /// True when the path points into a build-output directory (<c>obj</c> or
    /// <c>bin</c>), which contains compiler-generated files that must not be
    /// modified.
    /// </summary>
    public static bool IsGenerated(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }
}
