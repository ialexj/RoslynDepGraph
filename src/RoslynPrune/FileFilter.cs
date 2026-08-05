// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynPrune;

/// <summary>
/// Filters candidate files by glob patterns on file paths.
/// Uses the same path-glob semantics as RoslynSourceMap's FileResolver:
/// <c>**</c> matches across directory separators, <c>*</c> and <c>?</c> do not,
/// matching is case-insensitive, and separators are normalized to <c>/</c>.
/// </summary>
public sealed class FileFilter
{
    private readonly IReadOnlyList<Regex> _regexes;

    public FileFilter(IReadOnlyList<string> globs)
    {
        _regexes = globs.Select(GlobToRegex).ToList();
    }

    /// <summary>
    /// True when the path matches at least one glob. An empty glob list matches
    /// everything.
    /// </summary>
    public bool Matches(string path)
    {
        if (_regexes.Count == 0)
        {
            return true;
        }
        var normalized = path.Replace('\\', '/');
        return _regexes.Any(r => r.IsMatch(normalized));
    }

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
