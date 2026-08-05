// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynPrune.Models;

namespace RoslynPrune;

/// <summary>
/// Orchestrates a prune run: select candidates, analyze references, remove
/// unused types/members to a fixpoint, clean up orphaned using directives, and
/// optionally write the changed files to disk.
/// </summary>
public sealed class PruneOrchestrator
{
    public async Task<PruneReport> RunAsync(
        Solution solution,
        IReadOnlyList<string> fileGlobs,
        IReadOnlyList<string> typeGlobs,
        bool apply,
        TextWriter? errorWriter,
        CancellationToken ct = default)
    {
        var progress = new ProgressReporter();
        var selector = new CandidateSelector(fileGlobs, typeGlobs, progress);
        var analyzer = new PruneAnalyzer(progress);

        var removedTypes = new List<RemovedType>();
        var removedMembers = new List<RemovedMember>();
        var removedFiles = new HashSet<string>();
        var passes = 0;
        var current = solution;

        // Fixpoint loop: removing a type may make other types/members unused.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            passes++;
            progress.Phase($"--- Pass {passes} ---");

            var candidates = await selector.SelectAsync(current, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                progress.Complete($"Pass {passes}: no candidates; fixpoint reached.");
                break;
            }

            var plan = await analyzer.AnalyzeAsync(current, candidates, ct).ConfigureAwait(false);
            if (plan.IsEmpty)
            {
                progress.Complete($"Pass {passes}: nothing removable; fixpoint reached.");
                break;
            }

            removedTypes.AddRange(plan.RemovedTypes);
            removedMembers.AddRange(plan.RemovedMembers);

            var remover = new SymbolRemover(progress);
            var (newSolution, changed, passRemovedFiles) = await remover.ApplyAsync(current, plan, ct).ConfigureAwait(false);
            foreach (var f in passRemovedFiles)
            {
                removedFiles.Add(f);
            }

            // Defensive backstop: if a pass reported removals but the solution
            // did not actually change (e.g. a symbol couldn't be re-resolved or
            // its node couldn't be located), stop to avoid an infinite loop.
            if (changed.Count == 0)
            {
                progress.Complete(
                    $"Warning: pass {passes} reported {plan.RemovedTypes.Count} type(s) and "
                    + $"{plan.RemovedMembers.Count} member(s) for removal, but no files changed; stopping.");
                errorWriter?.WriteLine(
                    $"Warning: pass {passes} reported {plan.RemovedTypes.Count} type(s) and "
                    + $"{plan.RemovedMembers.Count} member(s) for removal, but no files changed; stopping.");
                break;
            }

            progress.Phase($"Pass {passes}: removed {plan.RemovedTypes.Count} type(s), {plan.RemovedMembers.Count} member(s).");
            current = newSolution;
        }

        // Clean up orphaned using directives.
        progress.Phase("Cleaning orphaned using directives...");
        var cleaner = new UsingCleaner(progress);
        var (cleanedSolution, removedUsings, _) = await cleaner.CleanAsync(current, ct).ConfigureAwait(false);
        current = cleanedSolution;

        // Write changed files (and delete emptied files) if --apply was requested.
        var writtenFiles = new List<string>();
        if (apply)
        {
            progress.Phase($"Writing changed files to disk...");
            writtenFiles.AddRange(await WriteChangedFilesAsync(current, solution, progress, ct).ConfigureAwait(false));
            foreach (var file in removedFiles)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    writtenFiles.Add(file);
                }
            }
            progress.Complete($"Wrote {writtenFiles.Count} file(s) to disk.");
        }

        progress.Phase("Computing final candidate count...");
        var candidateCount = (await selector.SelectAsync(solution, ct).ConfigureAwait(false)).Count;
        progress.Complete($"Done: {removedTypes.Count} type(s), {removedMembers.Count} member(s), {removedUsings.Count} using(s) removed in {passes} pass(es).");

        return new PruneReport
        {
            Tool = "RoslynPrune",
            SchemaVersion = "1.0",
            Applied = apply,
            Summary = new PruneSummary
            {
                CandidateTypes = candidateCount,
                RemovedTypeCount = removedTypes.Count,
                RemovedMemberCount = removedMembers.Count,
                RemovedUsingCount = removedUsings.Count,
                WrittenFileCount = writtenFiles.Count,
                Passes = passes,
            },
            RemovedTypes = removedTypes,
            RemovedMembers = removedMembers,
            RemovedUsings = removedUsings,
            WrittenFiles = writtenFiles,
            Diagnostics = Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Writes the text of every document that differs from the original solution
    /// to disk, returning the list of written file paths.
    /// </summary>
    private static async Task<IReadOnlyList<string>> WriteChangedFilesAsync(
        Solution newSolution, Solution originalSolution, ProgressReporter? progress, CancellationToken ct)
    {
        var written = new List<string>();
        var allDocs = newSolution.Projects.SelectMany(p => p.Documents).ToList();
        var index = 0;
        foreach (var document in allDocs)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref index);
            progress?.Report(n - 1, allDocs.Count, document.FilePath ?? document.Name);

            if (document.FilePath is null)
            {
                continue;
            }
            var originalDoc = originalSolution.GetDocument(document.Id);
            if (originalDoc is null)
            {
                continue;
            }

            var newText = await document.GetTextAsync(ct).ConfigureAwait(false);
            var originalText = await originalDoc.GetTextAsync(ct).ConfigureAwait(false);
            if (newText.ToString() == originalText.ToString())
            {
                continue;
            }

            await File.WriteAllTextAsync(document.FilePath, newText.ToString(), ct).ConfigureAwait(false);
            written.Add(document.FilePath);
        }
        return written;
    }
}
