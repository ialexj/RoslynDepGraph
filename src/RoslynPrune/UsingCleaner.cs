// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using RoslynPrune.Models;

namespace RoslynPrune;

/// <summary>
/// Removes using directives that are no longer needed after pruning:
/// aliases targeting pruned types, <c>using static</c> directives whose target
/// was pruned, and plain/global namespace usings that no remaining code uses.
/// </summary>
public sealed class UsingCleaner
{
    private readonly ProgressReporter? _progress;

    public UsingCleaner(ProgressReporter? progress = null)
    {
        _progress = progress;
    }

    /// <summary>
    /// Returns a new solution with orphaned using directives removed, plus the
    /// list of removed usings and the set of changed document IDs.
    /// </summary>
    public async Task<(Solution Solution, IReadOnlyList<RemovedUsing> Removed, IReadOnlySet<DocumentId> Changed)> CleanAsync(
        Solution solution, CancellationToken ct = default)
    {
        var removed = new List<RemovedUsing>();
        var changed = new HashSet<DocumentId>();
        var current = solution;
        var csharpProjects = current.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        var projectIndex = 0;

        foreach (var project in csharpProjects)
        {
            projectIndex++;
            _progress?.Phase($"Cleaning usings: project {projectIndex}/{csharpProjects.Count}: {project.Name}");

            // A global using directive makes its namespace available to EVERY
            // document in the project, not just the one that declares it. So the
            // "is this namespace still used?" check for global usings must span
            // the whole project. Compute the set of namespaces referenced
            // anywhere in the project once, then reuse it for every document.
            var projectUsedNamespaces = await GetProjectUsedNamespacesAsync(project, ct, _progress).ConfigureAwait(false);

            var docs = project.Documents.ToList();
            var docIndex = 0;
            foreach (var document in docs)
            {
                ct.ThrowIfCancellationRequested();
                var n = Interlocked.Increment(ref docIndex);
                _progress?.Report(n - 1, docs.Count, document.FilePath ?? document.Name);

                if (GeneratedFile.IsGenerated(document.FilePath))
                {
                    continue;
                }
                var result = await CleanDocumentAsync(current, document, projectUsedNamespaces, ct).ConfigureAwait(false);
                if (result.Removed.Count > 0)
                {
                    removed.AddRange(result.Removed);
                    changed.Add(document.Id);
                    current = result.Solution;
                }
            }
        }

        _progress?.Complete($"Cleaned usings: {removed.Count} directive(s) removed across {changed.Count} document(s).");
        return (current, removed, changed);
    }

    private static async Task<(Solution Solution, IReadOnlyList<RemovedUsing> Removed)> CleanDocumentAsync(
        Solution solution, Document document, IReadOnlySet<string> projectUsedNamespaces, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return (solution, Array.Empty<RemovedUsing>());
        }

        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var toRemove = new List<UsingDirectiveSyntax>();
        var removed = new List<RemovedUsing>();

        foreach (var usingDirective in usings)
        {
            var reason = ClassifyUsing(semanticModel, usingDirective, projectUsedNamespaces);
            if (reason is null)
            {
                continue;
            }

            var lineSpan = usingDirective.GetLocation().GetLineSpan();
            removed.Add(new RemovedUsing
            {
                File = document.FilePath ?? "",
                Line = lineSpan.StartLinePosition.Line + 1,
                Directive = usingDirective.ToString().Trim(),
                Reason = reason.Value,
            });
            toRemove.Add(usingDirective);
        }

        if (toRemove.Count == 0)
        {
            return (solution, Array.Empty<RemovedUsing>());
        }

        var newRoot = root.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepNoTrivia);
        if (newRoot is null)
        {
            return (solution, Array.Empty<RemovedUsing>());
        }
        newRoot = Formatter.Format(newRoot, solution.Workspace);
        var newSolution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        return (newSolution, removed);
    }

    /// <summary>
    /// Returns the removal reason for a using directive, or null if it should be
    /// kept.
    /// </summary>
    private static UsingRemovalReason? ClassifyUsing(
        SemanticModel semanticModel, UsingDirectiveSyntax usingDirective,
        IReadOnlySet<string> projectUsedNamespaces)
    {
        var isGlobal = usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
        if (usingDirective.Name is null)
        {
            return null;
        }

        if (usingDirective.Alias is not null)
        {
            // using X = T;  — remove if the target type no longer resolves.
            var symbolInfo = semanticModel.GetSymbolInfo(usingDirective.Name);
            if (symbolInfo.Symbol is null)
            {
                return isGlobal ? UsingRemovalReason.OrphanedGlobalAlias : UsingRemovalReason.AliasTargetPruned;
            }
            return null;
        }

        if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
            // using static T;  — remove if the target type no longer resolves.
            var symbolInfo = semanticModel.GetSymbolInfo(usingDirective.Name);
            if (symbolInfo.Symbol is null)
            {
                return isGlobal ? UsingRemovalReason.OrphanedGlobalStatic : UsingRemovalReason.StaticTargetPruned;
            }
            return null;
        }

        // Plain namespace using: using NS;  — remove if no symbol from NS is used.
        var nsSymbol = semanticModel.GetSymbolInfo(usingDirective.Name).Symbol as INamespaceSymbol;
        if (nsSymbol is null)
        {
            return isGlobal ? UsingRemovalReason.OrphanedGlobalNamespace : UsingRemovalReason.OrphanedNamespace;
        }

        var nsName = nsSymbol.ToDisplayString();

        // A GLOBAL using makes NS available to every document in the project,
        // so it is kept if ANY document references a symbol from NS. A non-global
        // using only affects its own document, so the document-scoped check is
        // correct there.
        var used = isGlobal
            ? projectUsedNamespaces.Contains(nsName)
            : NamespaceIsUsed(semanticModel, nsName);

        return used
            ? null
            : (isGlobal ? UsingRemovalReason.OrphanedGlobalNamespace : UsingRemovalReason.OrphanedNamespace);
    }

    /// <summary>
    /// Returns the set of namespace display names referenced by at least one
    /// non-generated document in the project. Used to decide whether a global
    /// namespace using can be removed: a global using is project-scoped, so its
    /// namespace is "used" if any document in the project references a symbol
    /// whose containing namespace matches.
    /// </summary>
    private static async Task<HashSet<string>> GetProjectUsedNamespacesAsync(
        Project project, CancellationToken ct, ProgressReporter? progress = null)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var docs = project.Documents.ToList();
        var index = 0;
        foreach (var document in docs)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref index);
            progress?.Report(n - 1, docs.Count, $"scanning {document.FilePath ?? document.Name}");

            if (GeneratedFile.IsGenerated(document.FilePath))
            {
                continue;
            }
            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(name);
                var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (symbol is null)
                {
                    continue;
                }
                var ns = symbol.ContainingNamespace?.ToDisplayString();
                if (ns is not null)
                {
                    used.Add(ns);
                }
            }
        }
        return used;
    }

    /// <summary>
    /// True when any name in the document resolves to a symbol whose containing
    /// namespace equals <paramref name="nsName"/>.
    /// </summary>
    private static bool NamespaceIsUsed(SemanticModel semanticModel, string nsName)
    {
        var root = semanticModel.SyntaxTree.GetRoot();
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(name);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is null)
            {
                continue;
            }
            if (symbol.ContainingNamespace?.ToDisplayString() == nsName)
            {
                return true;
            }
        }
        return false;
    }
}
