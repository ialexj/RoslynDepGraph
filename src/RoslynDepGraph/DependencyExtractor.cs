// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// The scope of extraction for a node.
/// </summary>
public enum ExtractionScope
{
    /// <summary>
    /// Rolled-up type scope: type declarations + all member signatures/bodies.
    /// Member-target edges are rolled up to their containing type.
    /// </summary>
    RolledUpType,

    /// <summary>
    /// Per-member scope: the single member's signature + body (or declared type +
    /// initializer for fields). No rollup.
    /// </summary>
    PerMember,
}

/// <summary>
/// Extracts outgoing dependency edges (referenced types and members) from a symbol
/// by walking its declaring syntax with a SemanticModel.
/// </summary>
public sealed class DependencyExtractor
{
    /// <summary>
    /// Per-project cache of the compilation task. Roslyn caches the <see cref="Compilation"/>
    /// internally on the <see cref="Project"/>, but each <see cref="Project.GetCompilationAsync"/>
    /// call still allocates an async state machine and does a lookup. Under parallel
    /// extraction over thousands of nodes in the same project, this avoids that per-call
    /// overhead by sharing a single task per project.
    /// </summary>
    /// <remarks>
    /// The factory passed to <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd"/>
    /// may run a few times under heavy contention on first access, but each call resolves
    /// to the same cached <see cref="Compilation"/> instance, so the only waste is a few
    /// extra discarded <see cref="Task"/> wrappers. The cancellation token is shared
    /// across all workers (the app's CT), so caching the task is safe.
    /// </remarks>
    private readonly ConcurrentDictionary<ProjectId, Task<Compilation?>> _compilationCache = new();

    /// <summary>
    /// Extract dependencies for the given symbol.
    /// </summary>
    public async Task<ExtractedDependencies> ExtractAsync(
        SymbolContext nodeContext,
        ExtractionScope scope,
        CancellationToken ct = default,
        Func<ISymbol, bool>? isExcluded = null)
    {
        var symbol = nodeContext.Symbol;
        var project = nodeContext.Project;

        // Use the project's Compilation rather than a single document's SemanticModel.
        // A type/member can be declared across multiple documents (partial classes,
        // partial methods), and querying a SemanticModel from one document about a
        // syntax node that lives in another throws "Syntax node is not within syntax
        // tree". The Compilation owns every tree in the project and can hand back the
        // correct SemanticModel per tree.
        var compilation = await GetCompilationAsync(project, ct).ConfigureAwait(false);
        if (compilation is null)
        {
            return ExtractedDependencies.Empty;
        }

        var collector = new ReferenceCollector(compilation);

        switch (scope)
        {
            case ExtractionScope.RolledUpType:
                await ExtractRolledUpTypeAsync(symbol, collector, isExcluded, ct).ConfigureAwait(false);
                break;
            case ExtractionScope.PerMember:
                await ExtractPerMemberAsync(symbol, collector, isExcluded, ct).ConfigureAwait(false);
                break;
        }

        return collector.Build();
    }

    /// <summary>
    /// Returns the <see cref="Compilation"/> for <paramref name="project"/>, cached
    /// per project so that thousands of nodes in the same project don't each pay the
    /// async state-machine + lookup cost of <see cref="Project.GetCompilationAsync"/>.
    /// </summary>
    private Task<Compilation?> GetCompilationAsync(Project project, CancellationToken ct)
    {
        return _compilationCache.GetOrAdd(
            project.Id,
            _ => project.GetCompilationAsync(ct));
    }

    private static async Task ExtractRolledUpTypeAsync(
        ISymbol symbol, ReferenceCollector collector,
        Func<ISymbol, bool>? isExcluded, CancellationToken ct)
    {
        if (symbol is not INamedTypeSymbol type)
        {
            return;
        }

        // Walk the type declaration node (declarations only).
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
            if (syntax is not TypeDeclarationSyntax typeDecl)
            {
                continue;
            }

            // Declaration-level: base list, constraints, attributes, declared types of fields/properties/events.
            var declWalker = new DeclarationWalker(collector, isExcluded);
            declWalker.Visit(typeDecl);

            // Plus every member's signature + body.
            foreach (var member in type.GetMembers())
            {
                if (!SymbolEnumerator.IsReportableMember(member) || isExcluded?.Invoke(member) == true)
                {
                    continue;
                }
                foreach (var memberRef in member.DeclaringSyntaxReferences)
                {
                    var memberSyntax = await memberRef.GetSyntaxAsync(ct).ConfigureAwait(false);
                    var memberWalker = new MemberBodyWalker(collector, isExcluded);
                    memberWalker.Visit(memberSyntax);
                }
            }
        }
    }

    private static async Task ExtractPerMemberAsync(
        ISymbol symbol, ReferenceCollector collector,
        Func<ISymbol, bool>? isExcluded, CancellationToken ct)
    {
        foreach (var declRef in symbol.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);

            // For fields (and event fields), the declaring syntax reference points to the
            // VariableDeclaratorSyntax (just the name), not the enclosing FieldDeclarationSyntax
            // which holds the declared type. Walk the ancestor so the type is captured.
            if (symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.Field ||
                symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.Event)
            {
                syntax = syntax.FirstAncestorOrSelf<FieldDeclarationSyntax>()
                    ?? syntax.FirstAncestorOrSelf<EventFieldDeclarationSyntax>()
                    ?? syntax;
            }

            var walker = new MemberBodyWalker(collector, isExcluded);
            walker.Visit(syntax);
        }
    }
}

/// <summary>
/// Raw extracted dependencies before filtering/rollup.
/// </summary>
public sealed record ExtractedDependencies
{
    /// <summary>Referenced type symbols (deduped by SymbolId).</summary>
    public required IReadOnlyDictionary<string, CollectedSymbol> Types { get; init; }

    /// <summary>Referenced member symbols (deduped by SymbolId).</summary>
    public required IReadOnlyDictionary<string, CollectedSymbol> Members { get; init; }

    public static ExtractedDependencies Empty { get; } = new()
    {
        Types = new Dictionary<string, CollectedSymbol>(),
        Members = new Dictionary<string, CollectedSymbol>(),
    };
}

public sealed record CollectedSymbol(ISymbol Symbol, List<SourceLocation> References);

/// <summary>
/// Collects referenced type and member symbols (with reference locations) while walking syntax.
/// </summary>
internal sealed class ReferenceCollector
{
    private readonly Compilation _compilation;
    private readonly Dictionary<string, CollectedSymbol> _types = new();
    private readonly Dictionary<string, CollectedSymbol> _members = new();

    public ReferenceCollector(Compilation compilation) => _compilation = compilation;

    public bool IsExcludedDeclaration(SyntaxNode node, Func<ISymbol, bool>? isExcluded)
    {
        if (isExcluded is null || node is not (MemberDeclarationSyntax or VariableDeclaratorSyntax))
        {
            return false;
        }

        var model = _compilation.GetSemanticModel(node.SyntaxTree);
        var symbol = model.GetDeclaredSymbol(node);
        if (symbol is not null && isExcluded(symbol))
        {
            return true;
        }

        // Field declarations can share a type and attributes across several variables.
        return node is BaseFieldDeclarationSyntax field &&
            field.Declaration.Variables.All(v =>
                model.GetDeclaredSymbol(v) is { } variable && isExcluded(variable));
    }

    public ExtractedDependencies Build() => new()
    {
        Types = _types,
        Members = _members,
    };

    public void Consider(SyntaxNode node)
    {
        // Resolve the SemanticModel that owns this node's syntax tree. A node may
        // belong to any document in the project (partial classes/methods across
        // files), so we must look it up per-tree rather than reusing one model.
        var model = _compilation.GetSemanticModel(node.SyntaxTree);

        // Type info (for expressions / type syntax).
        var typeInfo = model.GetTypeInfo(node, CancellationToken.None);
        AddType(typeInfo.Type, node);
        AddType(typeInfo.ConvertedType, node);

        // Symbol info (for identifiers / member access / invocations).
        var symbolInfo = model.GetSymbolInfo(node, CancellationToken.None);
        AddSymbol(symbolInfo.Symbol, node);
        foreach (var cand in symbolInfo.CandidateSymbols)
        {
            AddSymbol(cand, node);
        }
    }

    private void AddSymbol(ISymbol? symbol, SyntaxNode node)
    {
        if (symbol is null)
        {
            return;
        }

        // Unwrap aliases / reduced extension methods to their underlying symbol.
        symbol = symbol.OriginalDefinition;

        switch (symbol.Kind)
        {
            case Microsoft.CodeAnalysis.SymbolKind.NamedType:
            case Microsoft.CodeAnalysis.SymbolKind.ArrayType:
            case Microsoft.CodeAnalysis.SymbolKind.PointerType:
            case Microsoft.CodeAnalysis.SymbolKind.TypeParameter:
                AddType(symbol as ITypeSymbol, node);
                return;
            case Microsoft.CodeAnalysis.SymbolKind.Method:
            case Microsoft.CodeAnalysis.SymbolKind.Property:
            case Microsoft.CodeAnalysis.SymbolKind.Field:
            case Microsoft.CodeAnalysis.SymbolKind.Event:
                AddMember(symbol, node);
                return;
            // Local symbols, parameters, namespaces, etc. are not dependencies.
        }
    }

    private void AddType(ITypeSymbol? type, SyntaxNode node)
    {
        if (type is null)
        {
            return;
        }

        // Unwrap arrays, nullable, pointers, and generic type arguments recursively.
        var named = UnwrapToNamedType(type);
        if (named is null)
        {
            return;
        }

        // Recurse into generic type arguments.
        if (named is INamedTypeSymbol { IsGenericType: true } generic)
        {
            foreach (var arg in generic.TypeArguments)
            {
                AddType(arg, node);
            }
        }

        AddTo(_types, named, node);
    }

    private void AddMember(ISymbol symbol, SyntaxNode node)
    {
        // Skip constructors of the node's own type when they're implicit? We keep them;
        // they're real references. Filtering of self happens later.
        AddTo(_members, symbol, node);
    }

    private void AddTo(Dictionary<string, CollectedSymbol> sink, ISymbol symbol, SyntaxNode node)
    {
        var key = SymbolId.Create(symbol);
        if (!sink.TryGetValue(key, out var existing))
        {
            existing = new CollectedSymbol(symbol, new List<SourceLocation>());
            sink[key] = existing;
        }
        existing.References.Add(ToSourceLocation(node));
    }

    private static INamedTypeSymbol? UnwrapToNamedType(ITypeSymbol type)
    {
        return type switch
        {
            INamedTypeSymbol named => named,
            IArrayTypeSymbol arr => UnwrapToNamedType(arr.ElementType),
            IPointerTypeSymbol ptr => UnwrapToNamedType(ptr.PointedAtType),
            _ => null,
        };
    }

    private static SourceLocation ToSourceLocation(SyntaxNode node)
    {
        var span = node.GetLocation();
        var lineSpan = span.GetLineSpan();
        var path = lineSpan.Path;
        return new SourceLocation(path, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
    }
}

/// <summary>
/// Walks only declaration-level nodes of a type: base list, constraints, attributes,
/// and declared types of fields/properties/events. Does NOT descend into member bodies.
/// </summary>
internal sealed class DeclarationWalker : CSharpSyntaxWalker
{
    private readonly ReferenceCollector _collector;
    private readonly Func<ISymbol, bool>? _isExcluded;

    public DeclarationWalker(
        ReferenceCollector collector, Func<ISymbol, bool>? isExcluded)
        : base(SyntaxWalkerDepth.StructuredTrivia)
    {
        _collector = collector;
        _isExcluded = isExcluded;
    }

    public override void Visit(SyntaxNode? node)
    {
        if (node is not null && _collector.IsExcludedDeclaration(node, _isExcluded))
        {
            return;
        }

        // For declaration scope, only consider type syntax (base list, constraints, declared types)
        // and attributes. Member bodies are walked separately by MemberBodyWalker.
        if (node is TypeSyntax or AttributeSyntax)
        {
            _collector.Consider(node);
        }
        base.Visit(node);
    }
}

/// <summary>
/// Walks the full member: signature + body (methods/properties) or declared type +
/// initializer (fields). No rollup.
/// </summary>
internal sealed class MemberBodyWalker : CSharpSyntaxWalker
{
    private readonly ReferenceCollector _collector;
    private readonly Func<ISymbol, bool>? _isExcluded;

    public MemberBodyWalker(ReferenceCollector collector, Func<ISymbol, bool>? isExcluded)
    {
        _collector = collector;
        _isExcluded = isExcluded;
    }

    // Visit all nodes; the collector decides what's a type/member reference.
    public override void Visit(SyntaxNode? node)
    {
        if (node is not null && _collector.IsExcludedDeclaration(node, _isExcluded))
        {
            return;
        }

        if (node is ExpressionSyntax or TypeSyntax or AttributeSyntax)
        {
            _collector.Consider(node);
        }
        base.Visit(node);
    }
}
