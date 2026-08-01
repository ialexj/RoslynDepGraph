// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynDepGraph;

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
        Solution solution, CancellationToken ct = default)
    {
        var results = new List<SymbolContext>();

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

            // Walk all namespaces in the source assembly, collecting named types
            // that have at least one source location.
            var sourceTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            CollectSourceTypes(compilation.Assembly.GlobalNamespace, sourceTypes);

            foreach (var type in sourceTypes)
            {
                var doc = await GetFirstSourceDocumentAsync(project, type, ct).ConfigureAwait(false);
                if (doc is null)
                {
                    continue;
                }
                results.Add(new SymbolContext(type, project, doc));
            }
        }

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
            if (!IsReportableMember(member))
            {
                continue;
            }
            if (!member.Locations.Any(l => l.IsInSource))
            {
                continue;
            }

            var doc = await GetFirstSourceDocumentAsync(project, member, ct).ConfigureAwait(false);
            if (doc is null)
            {
                continue;
            }
            results.Add(new SymbolContext(member, project, doc));
        }

        return results;
    }

    internal static bool IsReportableMember(ISymbol member)
    {
        return member.Kind switch
        {
            SymbolKind.Method => true,
            SymbolKind.Property => true,
            SymbolKind.Field => true,
            SymbolKind.Event => true,
            _ => false,
        };
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
