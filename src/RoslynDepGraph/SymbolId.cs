// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis;

namespace RoslynDepGraph;

/// <summary>
/// Produces stable, run-to-run consistent string ids for symbols (used as dedup keys
/// and graph-link keys). Uses a fully-qualified display format.
/// </summary>
internal static class SymbolId
{
    private static readonly SymbolDisplayFormat Format = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Returns a stable string id for the given symbol.
    /// </summary>
    public static string Create(ISymbol symbol)
    {
        return symbol.ToDisplayString(Format);
    }
}
