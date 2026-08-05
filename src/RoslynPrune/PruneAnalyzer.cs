// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynPrune.Models;

namespace RoslynPrune;

/// <summary>
/// The result of one analysis pass: the types and members that are unused.
/// </summary>
public sealed record PrunePlan(
    IReadOnlyCollection<RemovedType> RemovedTypes,
    IReadOnlyCollection<RemovedMember> RemovedMembers)
{
    public bool IsEmpty => RemovedTypes.Count == 0 && RemovedMembers.Count == 0;
}

/// <summary>
/// Determines which candidate types and members are unused by searching the
/// whole solution for references. A reference located inside a using directive
/// does not count as usage.
/// </summary>
public sealed class PruneAnalyzer
{
    private readonly ProgressReporter? _progress;

    public PruneAnalyzer(ProgressReporter? progress = null)
    {
        _progress = progress;
    }

    /// <summary>
    /// Analyzes the candidate types and returns a plan of unused types/members.
    /// </summary>
    public async Task<PrunePlan> AnalyzeAsync(
        Solution solution,
        IReadOnlyCollection<SymbolContext> candidateTypes,
        CancellationToken ct = default)
    {
        // Entry points (Main methods) are roots: they are never pruned, even
        // though SymbolFinder reports no source references to them.
        var entryPoints = await CollectEntryPointsAsync(solution, ct).ConfigureAwait(false);

        // Analysis is embarrassingly parallel: each candidate type's reference
        // search is independent of every other's, and Roslyn's Solution/Compilation
        // are thread-safe for concurrent reads (SymbolFinder.FindReferencesAsync is
        // explicitly designed for concurrent invocation). Results are collected
        // concurrently via a thread-safe bag; output order is not significant.
        var removedTypes = new ConcurrentBag<RemovedType>();
        var removedMembers = new ConcurrentBag<RemovedMember>();
        var processed = 0;

        await Parallel.ForEachAsync(candidateTypes, new ParallelOptions { CancellationToken = ct },
            async (typeContext, token) =>
            {
                if (typeContext.Symbol is not INamedTypeSymbol type)
                {
                    return;
                }

                var done = Interlocked.Increment(ref processed);
                _progress?.Report(done - 1, candidateTypes.Count, type.Name);

                // Whole-type check: if nothing references the type (outside usings),
                // the entire type is unused. A type that contains an entry point is
                // a root and is never removed.
                //
                // NOTE: a type may have NO direct references to itself yet still be
                // in use through its members. The canonical case is a static class
                // hosting C# 14 extension methods: callers write `receiver.Member`
                // which resolves to a reduced extension method on the static class,
                // so the type symbol itself is never textually named at the call
                // site. We therefore also check the type's reportable members: if
                // any of them is referenced, the type is considered used and is
                // kept (its unused members are still pruned individually below).
                if (!ContainsEntryPoint(type, entryPoints)
                    && await IsTypeUnusedAsync(solution, typeContext, type, entryPoints, token).ConfigureAwait(false))
                {
                    removedTypes.Add(BuildRemovedType(typeContext));
                    return;
                }

                // Otherwise check each reportable member.
                var members = await SymbolEnumerator.GetSourceMembersAsync(typeContext, token).ConfigureAwait(false);
                foreach (var memberContext in members)
                {
                    token.ThrowIfCancellationRequested();
                    if (await IsUnusedAsync(solution, memberContext.Symbol, entryPoints, token).ConfigureAwait(false))
                    {
                        removedMembers.Add(BuildRemovedMember(memberContext));
                    }
                }
            }).ConfigureAwait(false);

        _progress?.Complete($"Candidates: {candidateTypes.Count}; Removable Types: {removedTypes.Count}; Members: {removedMembers.Count}.");
        return new PrunePlan(removedTypes, removedMembers);
    }

    /// <summary>
    /// True when the type (or a nested type) contains an entry-point member.
    /// </summary>
    private static bool ContainsEntryPoint(INamedTypeSymbol type, HashSet<ISymbol> entryPoints)
    {
        foreach (var member in type.GetMembers())
        {
            if (entryPoints.Contains(member))
            {
                return true;
            }
            if (member is INamedTypeSymbol nested && ContainsEntryPoint(nested, entryPoints))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Collects the entry-point (Main) symbols for every C# project in the
    /// solution. These are treated as roots and never pruned.
    /// </summary>
    private static async Task<HashSet<ISymbol>> CollectEntryPointsAsync(
        Solution solution, CancellationToken ct)
    {
        var entryPoints = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
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
            var entryPoint = compilation.GetEntryPoint(ct);
            if (entryPoint is not null)
            {
                entryPoints.Add(entryPoint);
            }
        }
        return entryPoints;
    }

    /// <summary>
    /// True when the type has no references outside of using directives AND none
    /// of its members are referenced. The member check is necessary because a
    /// type can be reachable only through its members — most notably a static
    /// class hosting C# 14 extension methods, where call sites write
    /// `receiver.Member` (resolving to a reduced extension method on the static
    /// class) and never name the type itself.
    /// </summary>
    /// <remarks>
    /// The member scan deliberately inspects ALL members of the type, including
    /// implicitly-declared ones (e.g. the reduced extension methods `get__X`/
    /// `set__X` that Roslyn synthesizes for each extension block member). Those
    /// implicit methods are exactly the symbols that <c>FindReferencesAsync</c>
    /// reports call-site references against, so they must be included here even
    /// though they are excluded from prune candidacy by
    /// <see cref="SymbolEnumerator.GetSourceMembersAsync"/>.
    /// </remarks>
    private static async Task<bool> IsTypeUnusedAsync(
        Solution solution,
        SymbolContext typeContext,
        INamedTypeSymbol type,
        HashSet<ISymbol> entryPoints,
        CancellationToken ct)
    {
        if (await IsUnusedAsync(solution, type, entryPoints, ct).ConfigureAwait(false))
        {
            // The type symbol itself is unreferenced. Before declaring the whole
            // type unused, make sure no member of the type is referenced — if
            // any member is used, the type must be kept (its unused members are
            // pruned individually by the caller). We scan ALL members here,
            // including implicitly-declared ones, because those are the symbols
            // that extension-method call sites resolve to.
            foreach (var member in type.GetMembers())
            {
                ct.ThrowIfCancellationRequested();
                // Nested types are handled as their own candidates; skip them
                // here to avoid double-counting (and to avoid treating a nested
                // type's presence as "usage" of its container).
                if (member is INamedTypeSymbol)
                {
                    continue;
                }
                if (!await IsUnusedAsync(solution, member, entryPoints, ct).ConfigureAwait(false))
                {
                    // A member is referenced → the type is in use.
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when the symbol has no references outside of using directives and is
    /// not an entry point.
    /// </summary>
    /// <remarks>
    /// A reference located within the symbol's OWN declaration is a
    /// self-reference, not external usage, and is ignored. The canonical case is
    /// a type that names itself in its own base list or type arguments, e.g.
    /// <c>public class ImageMgr : IControlStub&lt;ImageMgr&gt; { }</c> — the
    /// <c>ImageMgr</c> type argument is reported by <c>SymbolFinder</c> as a
    /// reference, but it lives inside the type's own declaration and does not
    /// make the type reachable from the outside.
    /// </remarks>
    private static async Task<bool> IsUnusedAsync(
        Solution solution, ISymbol symbol, HashSet<ISymbol> entryPoints, CancellationToken ct)
    {
        if (entryPoints.Contains(symbol))
        {
            return false;
        }
        var declarationSpans = await GetDeclarationSpansAsync(symbol, ct).ConfigureAwait(false);
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                if (location.IsImplicit)
                {
                    continue;
                }
                if (IsInsideUsingDirective(location.Location))
                {
                    continue;
                }
                if (IsSelfReference(location.Location, declarationSpans))
                {
                    continue;
                }
                // A real (non-using, non-self) reference exists.
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The source spans (file path + text span) of every location that declares
    /// the symbol — i.e. the symbol's own declaration sites. A reference whose
    /// location falls inside one of these spans is a self-reference.
    /// </summary>
    /// <remarks>
    /// <see cref="ISymbol.Locations"/> for a type returns just the identifier
    /// token span (e.g. the <c>ImageMgr</c> in <c>public class ImageMgr</c>),
    /// which does NOT cover the base list or type arguments. A self-reference
    /// like <c>IControlStub&lt;ImageMgr&gt;</c> in the base list lives outside
    /// that token span. We therefore use <see cref="ISymbol.DeclaringSyntaxReferences"/>
    /// and take each reference's full syntax-node span (e.g. the whole
    /// <c>ClassDeclarationSyntax</c>, which includes the base list), so that
    /// references inside the declaration are correctly recognized as
    /// self-references.
    /// </remarks>
    private static async Task<List<(string FilePath, TextSpan Span)>> GetDeclarationSpansAsync(
        ISymbol symbol, CancellationToken ct)
    {
        var spans = new List<(string, TextSpan)>();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var tree = syntaxRef.SyntaxTree;
            if (tree is null)
            {
                continue;
            }
            var node = await syntaxRef.GetSyntaxAsync(ct).ConfigureAwait(false);
            spans.Add((tree.FilePath, node.Span));
        }
        return spans;
    }

    /// <summary>
    /// True when the reference location falls inside one of the symbol's own
    /// declaration spans (a self-reference, e.g. a type naming itself in its
    /// base list or type arguments).
    /// </summary>
    private static bool IsSelfReference(
        Location location, List<(string FilePath, TextSpan Span)> declarationSpans)
    {
        if (declarationSpans.Count == 0)
        {
            return false;
        }
        if (!location.IsInSource || location.SourceTree is null)
        {
            return false;
        }
        var filePath = location.SourceTree.FilePath;
        var span = location.SourceSpan;
        foreach (var decl in declarationSpans)
        {
            if (!string.Equals(decl.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // A reference is a self-reference when it lies within the symbol's
            // own declaration span (the declaration node fully contains the
            // reference).
            if (span.Start >= decl.Span.Start && span.End <= decl.Span.End)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the location's enclosing node is a using directive (or one of
    /// its name parts). A using directive is not a usage.
    /// </summary>
    private static bool IsInsideUsingDirective(Location location)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return false;
        }
        var node = location.SourceTree.GetRoot().FindNode(location.SourceSpan);
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is UsingDirectiveSyntax)
            {
                return true;
            }
        }
        return false;
    }

    private static RemovedType BuildRemovedType(SymbolContext context)
    {
        var (file, line) = FirstSourceLocation(context.Symbol);
        return new RemovedType
        {
            FullyQualifiedName = CandidateSelector.FullyQualifiedName(context.Symbol),
            Kind = context.Symbol.Kind.ToString(),
            File = file,
            Line = line,
        };
    }

    private static RemovedMember BuildRemovedMember(SymbolContext context)
    {
        var (file, line) = FirstSourceLocation(context.Symbol);
        var containing = context.Symbol.ContainingType is { } ct
            ? CandidateSelector.FullyQualifiedName(ct)
            : "";
        return new RemovedMember
        {
            FullyQualifiedName = context.Symbol.ToDisplayString(),
            ContainingType = containing,
            Kind = context.Symbol.Kind.ToString(),
            File = file,
            Line = line,
        };
    }

    private static (string File, int Line) FirstSourceLocation(ISymbol symbol)
    {
        foreach (var loc in symbol.Locations)
        {
            if (loc.IsInSource && loc.SourceTree is not null)
            {
                var lineSpan = loc.GetLineSpan();
                return (loc.SourceTree.FilePath, lineSpan.StartLinePosition.Line + 1);
            }
        }
        return ("", 0);
    }
}
