// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Threading.Tasks;
using RoslynSourceMap.Models;

namespace RoslynSourceMap.Tests;

/// <summary>
/// Shared helpers that drive the public RoslynSourceMap pipeline
/// (<see cref="MemberExtractor.ExtractAsync"/>) from test code.
/// </summary>
internal static class TestHarness
{
    /// <summary>
    /// Extracts a <see cref="FileEntry"/> from an in-memory source string by
    /// writing it to a temp file and running the normal pipeline.
    /// </summary>
    public static async Task<FileEntry> ExtractAsync(string source)
    {
        var tmp = Path.GetTempFileName() + ".cs";
        try
        {
            await File.WriteAllTextAsync(tmp, source);
            return await MemberExtractor.ExtractAsync(tmp);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
