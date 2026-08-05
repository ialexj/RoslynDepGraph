// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynPrune;

/// <summary>
/// Filters candidate types by glob patterns on fully-qualified names.
/// Uses the same symbol-name glob semantics as RoslynDepGraph's NodeFilter:
/// <c>*</c> matches any sequence (including dots), <c>?</c> matches a single
/// character, matching is case-sensitive and anchored to the full name.
/// </summary>
public sealed class TypeFilter
{
    private readonly IReadOnlyList<Regex> _regexes;

    public TypeFilter(IReadOnlyList<string> globs)
    {
        _regexes = globs.Select(GlobToRegex).ToList();
    }

    /// <summary>
    /// True when the fully-qualified name matches at least one glob. An empty
    /// glob list matches everything.
    /// </summary>
    public bool Matches(string fullyQualifiedName)
    {
        if (_regexes.Count == 0)
        {
            return true;
        }
        return _regexes.Any(r => r.IsMatch(fullyQualifiedName));
    }

    internal static Regex GlobToRegex(string glob)
    {
        var pattern = new StringBuilder();
        pattern.Append('^');
        foreach (var ch in glob)
        {
            switch (ch)
            {
                case '*':
                    pattern.Append(".*");
                    break;
                case '?':
                    pattern.Append('.');
                    break;
                default:
                    pattern.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
    }
}
