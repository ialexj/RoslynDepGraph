// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace RoslynSourceMap.Models;

/// <summary>
/// A type or member declaration in the source map. Types carry a recursive
/// <see cref="Members"/> list (nested types and members); non-type members have
/// an empty list.
/// </summary>
public sealed record MemberEntry
{
    /// <summary>
    /// The declaration kind: a type kind (<c>class|struct|interface|enum|delegate</c>)
    /// or a member kind (<c>method|constructor|destructor|property|indexer|field|
    /// event|event-field|operator|conversion-operator</c>).
    /// </summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The member's name plus its parameter types, e.g. <c>WriteNode(TextWriter,
    /// DependencyNode)</c> for a method. Members without parameters (properties,
    /// fields, events) have <see cref="FullName"/> equal to <see cref="Name"/>.
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// The nearest enclosing type's name, when this declaration is nested.
    /// </summary>
    public string? ContainingType { get; init; }

    /// <summary>
    /// The namespace this declaration lives in, when declared inside one.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// True for record class/struct declarations (has a <c>record</c> modifier).
    /// </summary>
    public bool IsRecord { get; init; }

    public required IReadOnlyList<string> Modifiers { get; init; }

    /// <summary>
    /// The declared return type, for methods, properties, indexers, operators,
    /// delegates, and fields.
    /// </summary>
    public string? ReturnType { get; init; }

    public required IReadOnlyList<ParameterEntry> Parameters { get; init; }

    /// <summary>
    /// The leading comment trivia (verbatim, including <c>//</c>, <c>/* */</c>,
    /// <c>///</c> markers) immediately preceding this declaration, or null when
    /// there are none.
    /// </summary>
    public string? LeadingComments { get; init; }

    public required SourceLocation Location { get; init; }

    /// <summary>
    /// Nested members. For type declarations this holds the type's members and
    /// nested types; for non-type members it is empty.
    /// </summary>
    public required IReadOnlyList<MemberEntry> Members { get; init; }
}
