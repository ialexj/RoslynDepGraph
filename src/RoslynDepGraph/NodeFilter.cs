// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynDepGraph;

/// <summary>
/// Filters the set of reported nodes by glob patterns on fully-qualified names.
/// </summary>
public static class NodeFilter
{
    /// <summary>
    /// Selects nodes whose fully-qualified name matches the include/exclude globs.
    /// </summary>
    /// <param name="nodes">Candidate nodes (fully-qualified name, symbol context).</param>
    /// <param name="includes">Include globs (OR'd; empty = include all).</param>
    /// <param name="excludes">Exclude globs (wins over includes).</param>
    /// <returns>Surviving nodes and the count removed.</returns>
    public static (IReadOnlyList<FilteredNode> Surviving, int Removed) Select(
        IReadOnlyList<FilteredNode> nodes,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes)
    {
        var includeRegexes = includes.Select(GlobToRegex).ToList();
        var excludeRegexes = excludes.Select(GlobToRegex).ToList();

        var surviving = new List<FilteredNode>();
        var removed = 0;

        foreach (var node in nodes)
        {
            var name = node.FullyQualifiedName;

            // Include filter: if any include patterns are specified, the name must match at least one.
            if (includeRegexes.Count > 0 && !includeRegexes.Any(r => r.IsMatch(name)))
            {
                removed++;
                continue;
            }

            // Exclude filter: if the name matches any exclude pattern, drop it.
            if (excludeRegexes.Any(r => r.IsMatch(name)))
            {
                removed++;
                continue;
            }

            surviving.Add(node);
        }

        return (surviving, removed);
    }

    /// <summary>
    /// Converts a glob pattern (* = any chars, ? = single char) to a regex.
    /// Anchored to the full string, case-sensitive.
    /// </summary>
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

/// <summary>
/// A node candidate with its fully-qualified name precomputed for filtering.
/// </summary>
public sealed record FilteredNode(SymbolContext Context, string FullyQualifiedName);
