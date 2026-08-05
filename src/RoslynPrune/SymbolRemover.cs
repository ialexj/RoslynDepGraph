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
/// Applies a <see cref="PrunePlan"/> to a solution by removing the unused types
/// and members from their declaring syntax trees.
/// </summary>
public sealed class SymbolRemover
{
    private readonly ProgressReporter? _progress;

    public SymbolRemover(ProgressReporter? progress = null)
    {
        _progress = progress;
    }

    /// <summary>
    /// Returns a new solution with the plan's removals applied, the set of
    /// document IDs that changed, and the set of file paths that became empty
    /// (only a namespace declaration or nothing) and should be deleted.
    /// </summary>
    public async Task<(Solution Solution, IReadOnlySet<DocumentId> Changed, IReadOnlySet<string> RemovedFiles)> ApplyAsync(
        Solution solution, PrunePlan plan, CancellationToken ct = default)
    {
        var changed = new HashSet<DocumentId>();
        var removedFiles = new HashSet<string>();

        // Group removals by document so we edit each tree once.
        var byDocument = new Dictionary<DocumentId, List<SyntaxNode>>();

        var totalDeclarations = plan.RemovedTypes.Count + plan.RemovedMembers.Count;
        _progress?.Phase($"Locating {totalDeclarations} declaration(s) across solution...");

        // Find all declaring nodes in parallel. The lookup is read-only on the
        // (immutable) solution, so concurrent calls are safe; the dictionary
        // mutation below happens sequentially after all tasks complete.
        var done = 0;
        var typeTasks = plan.RemovedTypes
            .Select(async removedType =>
            {
                var node = await FindDeclaringNodeAsync(solution, removedType, ct).ConfigureAwait(false);
                var n = Interlocked.Increment(ref done);
                _progress?.Report(n - 1, totalDeclarations, removedType.FullyQualifiedName);
                return node;
            })
            .ToArray();
        var memberTasks = plan.RemovedMembers
            .Select(async removedMember =>
            {
                var node = await FindDeclaringNodeAsync(solution, removedMember, ct).ConfigureAwait(false);
                var n = Interlocked.Increment(ref done);
                _progress?.Report(n - 1, totalDeclarations, removedMember.FullyQualifiedName);
                return node;
            })
            .ToArray();

        await Task.WhenAll(typeTasks.Concat(memberTasks)).ConfigureAwait(false);

        foreach (var node in typeTasks.Select(t => t.Result))
        {
            if (node is null)
            {
                continue;
            }
            AddToDocument(solution, byDocument, node);
        }

        foreach (var node in memberTasks.Select(t => t.Result))
        {
            if (node is null)
            {
                continue;
            }
            AddToDocument(solution, byDocument, node);
        }

        _progress?.Phase($"Applying edits to {byDocument.Count} document(s)...");
        var current = solution;
        var docIndex = 0;
        foreach (var (docId, nodes) in byDocument)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref docIndex);
            var document = current.GetDocument(docId);
            _progress?.Report(n - 1, byDocument.Count, document?.FilePath ?? docId.ToString());
            if (document is null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var newRoot = root.RemoveNodes(nodes, SyntaxRemoveOptions.KeepNoTrivia);
            if (newRoot is null || newRoot == root)
            {
                continue;
            }

            // If the document is now empty (no members — just a file-scoped or
            // regular namespace with no members, or nothing), remove the file
            // entirely rather than leaving a dangling namespace declaration.
            if (IsEmptyDocument(newRoot))
            {
                if (document.FilePath is not null)
                {
                    removedFiles.Add(document.FilePath);
                }
                current = current.RemoveDocument(docId);
                changed.Add(docId);
                continue;
            }

            newRoot = Formatter.Format(newRoot, document.Project.Solution.Workspace);
            current = current.WithDocumentSyntaxRoot(docId, newRoot);
            changed.Add(docId);
        }

        _progress?.Complete($"Applied edits to {changed.Count} document(s); {removedFiles.Count} file(s) emptied.");
        return (current, changed, removedFiles);
    }

    /// <summary>
    /// True when the compilation unit has no members, or only a namespace
    /// declaration (file-scoped or regular) with no members of its own.
    /// </summary>
    private static bool IsEmptyDocument(SyntaxNode root)
    {
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return false;
        }

        if (compilationUnit.Members.Count == 0)
        {
            return true;
        }

        // A single namespace (file-scoped or regular) with no members is empty.
        if (compilationUnit.Members.Count == 1
            && compilationUnit.Members[0] is BaseNamespaceDeclarationSyntax ns
            && ns.Members.Count == 0)
        {
            return true;
        }

        return false;
    }

    private static void AddToDocument(
        Solution solution,
        Dictionary<DocumentId, List<SyntaxNode>> byDocument,
        SyntaxNode node)
    {
        var tree = node.SyntaxTree;
        var docId = solution.GetDocumentId(tree);
        if (docId is null)
        {
            return;
        }
        if (!byDocument.TryGetValue(docId, out var list))
        {
            list = new List<SyntaxNode>();
            byDocument[docId] = list;
        }
        list.Add(node);
    }

    private static async Task<SyntaxNode?> FindDeclaringNodeAsync(
        Solution solution, RemovedType removedType, CancellationToken ct)
    {
        var symbol = await FindSymbolAsync(solution, removedType.FullyQualifiedName, ct).ConfigureAwait(false);
        return symbol is null ? null : FirstDeclaringNode(symbol);
    }

    private static async Task<SyntaxNode?> FindDeclaringNodeAsync(
        Solution solution, RemovedMember removedMember, CancellationToken ct)
    {
        var symbol = await FindSymbolAsync(solution, removedMember.FullyQualifiedName, ct).ConfigureAwait(false);
        return symbol is null ? null : FirstDeclaringNode(symbol);
    }

    private static async Task<ISymbol?> FindSymbolAsync(
        Solution solution, string displayString, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }
            var symbol = compilation.GetSymbolsWithName(
                name => true,
                SymbolFilter.TypeAndMember,
                ct).FirstOrDefault(s => s.ToDisplayString() == displayString);
            if (symbol is not null)
            {
                return symbol;
            }
        }
        return null;
    }

    private static SyntaxNode? FirstDeclaringNode(ISymbol symbol)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            // For fields/events with multiple declarators, climb to the
            // declaration so the whole statement is removed.
            if (node is VariableDeclaratorSyntax)
            {
                node = node.Parent?.Parent; // VariableDeclaration -> FieldDeclaration/EventFieldDeclaration
            }
            if (node is MemberDeclarationSyntax or TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax)
            {
                return node;
            }
        }
        return null;
    }
}
