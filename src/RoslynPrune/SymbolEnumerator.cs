// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynPrune;

/// <summary>
/// A symbol plus the project/document that declares it, for downstream
/// semantic-model lookup.
/// </summary>
public sealed record SymbolContext(ISymbol Symbol, Project Project, Document Document);

/// <summary>
/// Enumerates source-defined types and members across a solution.
/// </summary>
public static class SymbolEnumerator
{
    /// <summary>
    /// All source-defined named types across all C# projects in the solution.
    /// Includes classes, structs, interfaces, enums, delegates.
    /// </summary>
    public static async Task<IReadOnlyList<SymbolContext>> GetSourceTypesAsync(
        Solution solution, CancellationToken ct = default, ProgressReporter? progress = null)
    {
        var results = new List<SymbolContext>();
        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        var projectIndex = 0;

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            projectIndex++;
            progress?.Phase($"Compiling project {projectIndex}/{projects.Count}: {project.Name}");

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            // Walk all namespaces in the source assembly, collecting named types
            // that have at least one source location.
            var sourceTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            CollectSourceTypes(compilation.Assembly.GlobalNamespace, sourceTypes);

            var typeIndex = 0;
            foreach (var type in sourceTypes)
            {
                typeIndex++;
                progress?.Report(typeIndex - 1, sourceTypes.Count, type.Name);

                var doc = await GetFirstSourceDocumentAsync(project, type, ct).ConfigureAwait(false);
                if (doc is null)
                {
                    continue;
                }
                results.Add(new SymbolContext(type, project, doc));
            }
        }

        progress?.Complete($"Enumerated {results.Count} source type(s) across {projects.Count} project(s).");
        return results;
    }

    /// <summary>
    /// Source-defined members (methods, properties, fields, events) of a type.
    /// </summary>
    public static async Task<IReadOnlyList<SymbolContext>> GetSourceMembersAsync(
        SymbolContext typeContext, CancellationToken ct = default)
    {
        var results = new List<SymbolContext>();
        if (typeContext.Symbol is not INamedTypeSymbol type)
        {
            return results;
        }

        var project = typeContext.Project;

        foreach (var member in type.GetMembers())
        {
            // A C# 14 extension block is represented by Roslyn as a synthetic
            // nested type with an empty name (e.g. `extension(IComStub)`). The
            // user-declared members inside the block (properties, methods) live
            // on that synthetic container, NOT on the containing static class.
            // Since we skip the synthetic container as a candidate type (see
            // CollectSourceTypesRec), we must enumerate its members here, as
            // members of the containing class, so that genuinely unused
            // extension members can be pruned individually.
            if (member is INamedTypeSymbol nested
                && string.IsNullOrEmpty(nested.Name))
            {
                foreach (var extMember in nested.GetMembers())
                {
                    if (!IsReportableExtensionMember(extMember))
                    {
                        continue;
                    }
                    if (!extMember.Locations.Any(l => l.IsInSource))
                    {
                        continue;
                    }
                    var doc = await GetFirstSourceDocumentAsync(project, extMember, ct).ConfigureAwait(false);
                    if (doc is null)
                    {
                        continue;
                    }
                    results.Add(new SymbolContext(extMember, project, doc));
                }
                continue;
            }

            // Skip compiler-generated members (e.g. the reduced extension
            // methods `get__X`/`set__X` that Roslyn synthesizes on a static
            // class for each C# 14 extension block member). They are not
            // independently removable — the user-declared member inside the
            // extension block is the unit of removal — and reporting them as
            // candidates would cause them to be "pruned" but never re-resolved
            // (their display string does not round-trip), spinning the fixpoint
            // loop. They ARE still consulted by the whole-type reference check
            // via FindReferencesAsync, which is what keeps the containing type
            // alive when an extension member is used.
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }
            if (!IsReportableMember(member))
            {
                continue;
            }
            if (!member.Locations.Any(l => l.IsInSource))
            {
                continue;
            }

            var memberDoc = await GetFirstSourceDocumentAsync(project, member, ct).ConfigureAwait(false);
            if (memberDoc is null)
            {
                continue;
            }
            results.Add(new SymbolContext(member, project, memberDoc));
        }

        return results;
    }

    internal static bool IsReportableMember(ISymbol member)
    {
        return member.Kind switch
        {
            // Skip property/event accessors (get/set/add/remove/raise): they are
            // not independently removable — the whole property/event is the unit
            // of removal. Also skip constructors, destructors, operators, and
            // conversion operators: their "usage" semantics differ from regular
            // methods (e.g. a constructor is used by `new`, an operator by its
            // symbol). Only ordinary methods are reported as prune candidates.
            SymbolKind.Method => member is IMethodSymbol method
                && method.MethodKind == MethodKind.Ordinary,
            SymbolKind.Property => true,
            SymbolKind.Field => true,
            SymbolKind.Event => true,
            _ => false,
        };
    }

    /// <summary>
    /// Reportability check for members declared inside a C# 14 extension block
    /// (which live on the synthetic empty-named container type). Same rules as
    /// <see cref="IsReportableMember"/> but also skips implicitly-declared
    /// members — the container itself is compiler-generated, so we only want
    /// the user-declared properties/methods/etc.
    /// </summary>
    private static bool IsReportableExtensionMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
        {
            return false;
        }
        return IsReportableMember(member);
    }

    private static void CollectSourceTypes(INamespaceSymbol ns, HashSet<INamedTypeSymbol> sink)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            CollectSourceTypesRec(type, sink);
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            CollectSourceTypes(child, sink);
        }
    }

    private static void CollectSourceTypesRec(INamedTypeSymbol type, HashSet<INamedTypeSymbol> sink)
    {
        // Skip the synthetic container type that Roslyn generates for a C# 14
        // extension block. It is named `extension(...)` with an empty `Name`
        // and is not a user-declared type — it is the compiler's representation
        // of the extension block's scope. Treating it as a candidate would
        // report a phantom `Namespace.Type.` (trailing dot) type for removal
        // that cannot be re-resolved by display string, and pruning it would
        // tear the extension block out of its containing static class.
        if (string.IsNullOrEmpty(type.Name))
        {
            return;
        }
        if (!sink.Add(type))
        {
            return;
        }
        // Recurse into nested types.
        foreach (var nested in type.GetTypeMembers())
        {
            CollectSourceTypesRec(nested, sink);
        }
    }

    private static async Task<Document?> GetFirstSourceDocumentAsync(
        Project project, ISymbol symbol, CancellationToken ct)
    {
        foreach (var loc in symbol.Locations)
        {
            if (!loc.IsInSource)
            {
                continue;
            }
            var tree = loc.SourceTree;
            if (tree is null)
            {
                continue;
            }
            var doc = project.GetDocument(tree);
            if (doc is not null)
            {
                return doc;
            }
        }
        // Fallback: first C# document (rare; e.g. symbols without a direct tree).
        return await Task.FromResult<Document?>(project.Documents.FirstOrDefault());
    }
}
