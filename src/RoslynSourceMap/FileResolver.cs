// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynSourceMap;

/// <summary>
/// Resolves a user-supplied path into a concrete list of <c>.cs</c> files.
/// Accepts a single file, a directory (recursively searched for <c>*.cs</c>),
/// or a glob pattern (e.g. <c>**/*.cs</c>).
/// </summary>
public static class FileResolver
{
    /// <summary>
    /// Resolves <paramref name="path"/> to a list of <c>.cs</c> files.
    /// </summary>
    /// <returns>The resolved files, or an empty list when nothing matched.</returns>
    public static IReadOnlyList<string> Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<string>();
        }

        if (File.Exists(path))
        {
            return IsCsFile(path) ? new[] { path } : Array.Empty<string>();
        }

        if (Directory.Exists(path))
        {
            return Directory
                .EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        // Treat as a glob pattern.
        return ResolveGlob(path);
    }

    private static bool IsCsFile(string path) =>
        string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ResolveGlob(string pattern)
    {
        // Split the pattern into a non-glob base directory and the remaining glob.
        var (baseDir, remainder) = SplitBaseDir(pattern);
        if (baseDir.Length == 0 || !Directory.Exists(baseDir))
        {
            return Array.Empty<string>();
        }

        var regex = GlobToRegex(remainder);
        var normalizedPattern = Normalize(remainder);

        return Directory
            .EnumerateFiles(baseDir, "*", SearchOption.AllDirectories)
            .Where(p => IsCsFile(p))
            .Where(p => regex.IsMatch(Normalize(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Splits a glob into the longest leading path segment that contains no glob
    /// metacharacters (the base directory) and the remaining glob pattern.
    /// </summary>
    private static (string BaseDir, string Remainder) SplitBaseDir(string pattern)
    {
        var normalized = Normalize(pattern);
        var parts = normalized.Split('/');
        var baseParts = new List<string>();
        var i = 0;
        for (; i < parts.Length; i++)
        {
            if (ContainsGlobMeta(parts[i]))
            {
                break;
            }
            baseParts.Add(parts[i]);
        }

        var baseDir = string.Join(Path.DirectorySeparatorChar, baseParts);
        if (baseDir.Length == 0)
        {
            baseDir = ".";
        }

        var remainder = string.Join("/", parts.Skip(i));
        return (baseDir, remainder);
    }

    private static bool ContainsGlobMeta(string segment) =>
        segment.IndexOfAny(new[] { '*', '?' }) >= 0;

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Converts a glob pattern to an anchored, case-insensitive regex.
    /// <c>**</c> matches across directory separators; <c>*</c> and <c>?</c> do not.
    /// </summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // "**" — consume the second star and match across separators.
                        i++;
                        sb.Append(".*");
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
