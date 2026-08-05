// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynPrune;

/// <summary>
/// Selects the set of candidate types eligible for pruning, based on the
/// <c>--files</c> and <c>--types</c> filters. Members inherit candidacy from
/// their containing type.
/// </summary>
public sealed class CandidateSelector
{
    private readonly FileFilter _fileFilter;
    private readonly TypeFilter _typeFilter;
    private readonly ProgressReporter? _progress;

    public CandidateSelector(IReadOnlyList<string> fileGlobs, IReadOnlyList<string> typeGlobs, ProgressReporter? progress = null)
    {
        _fileFilter = new FileFilter(fileGlobs);
        _typeFilter = new TypeFilter(typeGlobs);
        _progress = progress;
    }

    /// <summary>
    /// Returns the candidate types: source-defined types whose declaring file
    /// matches the file filter AND whose fully-qualified name matches the type
    /// filter.
    /// </summary>
    public async Task<IReadOnlyCollection<SymbolContext>> SelectAsync(
        Solution solution, CancellationToken ct = default)
    {
        _progress?.Phase("Enumerating source types...");
        var allTypes = await SymbolEnumerator.GetSourceTypesAsync(solution, ct, _progress).ConfigureAwait(false);
        
        _progress?.Phase($"Filtering {allTypes.Count} type(s) against file/type globs...");
        var candidates = new ConcurrentBag<SymbolContext>();
        var processed = 0;

        await Parallel.ForEachAsync(allTypes, async (typeContext, ct) =>
        {
            var done = Interlocked.Increment(ref processed);
            _progress?.Report(done - 1, allTypes.Count, typeContext.Symbol.Name);

            if (IsCandidate(typeContext))
            {
                candidates.Add(typeContext);
            }
        });

        _progress?.Complete($"Selected {candidates.Count} candidate type(s).");
        return candidates;
    }

    private bool IsCandidate(SymbolContext typeContext)
    {
        var filePath = typeContext.Document.FilePath;
        if (GeneratedFile.IsGenerated(filePath))
        {
            return false;
        }
        if (filePath is not null && !_fileFilter.Matches(filePath))
        {
            return false;
        }

        var fqn = FullyQualifiedName(typeContext.Symbol);
        return _typeFilter.Matches(fqn);
    }

    /// <summary>
    /// Builds a dotted fully-qualified name (Namespace.Type) for a type symbol.
    /// </summary>
    internal static string FullyQualifiedName(ISymbol symbol)
    {
        var parts = new List<string>();
        for (var s = symbol; s is not null; s = s.ContainingSymbol)
        {
            if (s is INamespaceSymbol ns && ns.IsGlobalNamespace)
            {
                break;
            }
            if (s is INamedTypeSymbol or INamespaceSymbol)
            {
                parts.Add(s.Name);
            }
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
