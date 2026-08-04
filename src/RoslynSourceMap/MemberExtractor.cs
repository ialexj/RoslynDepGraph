// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSourceMap.Models;

namespace RoslynSourceMap;

/// <summary>
/// Parses a C# file with Roslyn's syntax-only API (no semantic model) and
/// produces a <see cref="FileEntry"/> describing every type and member
/// declaration, including each declaration's leading comments.
/// </summary>
public static class MemberExtractor
{
    /// <summary>
    /// Parses <paramref name="file"/> and extracts its type/member map.
    /// </summary>
    public static async Task<FileEntry> ExtractAsync(string file, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        var tree = CSharpSyntaxTree.ParseText(
            text,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: file);

        var diagnostics = tree.GetDiagnostics()
            .Select(d => d.ToString())
            .ToArray();

        var root = (CompilationUnitSyntax)await tree.GetRootAsync(ct).ConfigureAwait(false);

        var types = new List<MemberEntry>();
        foreach (var member in root.Members)
        {
            if (member is BaseNamespaceDeclarationSyntax ns)
            {
                CollectNamespaceTypes(ns, types);
            }
            else
            {
                TryBuildType(member, containingNamespace: null, types);
            }
        }

        return new FileEntry
        {
            Path = file,
            Types = types,
            Diagnostics = diagnostics,
        };
    }

    private static void CollectNamespaceTypes(
        BaseNamespaceDeclarationSyntax ns, List<MemberEntry> sink)
    {
        foreach (var member in ns.Members)
        {
            if (member is BaseNamespaceDeclarationSyntax childNs)
            {
                CollectNamespaceTypes(childNs, sink);
            }
            else if (TryBuildType(member, ns, sink))
            {
                // TryBuildType appends to sink directly.
            }
        }
    }

    private static bool TryBuildType(
        MemberDeclarationSyntax member,
        BaseNamespaceDeclarationSyntax? containingNamespace,
        List<MemberEntry> sink)
    {
        if (member is TypeDeclarationSyntax typeDecl)
        {
            sink.Add(BuildType(typeDecl, containingNamespace));
            return true;
        }
        if (member is DelegateDeclarationSyntax delegateDecl)
        {
            sink.Add(BuildDelegate(delegateDecl, containingNamespace));
            return true;
        }
        return false;
    }

    private static MemberEntry BuildType(
        TypeDeclarationSyntax typeDecl, BaseNamespaceDeclarationSyntax? containingNamespace)
    {
        var kind = typeDecl.Kind() switch
        {
            SyntaxKind.ClassDeclaration => "class",
            SyntaxKind.StructDeclaration => "struct",
            SyntaxKind.InterfaceDeclaration => "interface",
            SyntaxKind.EnumDeclaration => "enum",
            SyntaxKind.RecordDeclaration => "record",
            SyntaxKind.RecordStructDeclaration => "record",
            _ => "type",
        };

        var modifiers = typeDecl.Modifiers.Select(t => t.ValueText).ToArray();
        var isRecord = typeDecl.Kind() is SyntaxKind.RecordDeclaration or SyntaxKind.RecordStructDeclaration;

        var parameters = typeDecl is RecordDeclarationSyntax recordDecl
            ? BuildParameters(recordDecl.ParameterList)
            : Array.Empty<ParameterEntry>();

        var members = new List<MemberEntry>();
        foreach (var member in typeDecl.Members)
        {
            if (TryBuildMember(member, typeDecl, containingNamespace, members))
            {
                // appended to members.
            }
        }

        var name = typeDecl.Identifier.ValueText;

        return new MemberEntry
        {
            Kind = kind,
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = null,
            Namespace = containingNamespace?.Name.ToString(),
            IsRecord = isRecord,
            Modifiers = modifiers,
            ReturnType = null,
            Parameters = parameters,
            LeadingComments = GetLeadingComments(typeDecl),
            Location = LocationOf(typeDecl),
            Members = members,
        };
    }

    private static MemberEntry BuildDelegate(
        DelegateDeclarationSyntax delegateDecl, BaseNamespaceDeclarationSyntax? containingNamespace)
    {
        var name = delegateDecl.Identifier.ValueText;
        var parameters = BuildParameters(delegateDecl.ParameterList);

        return new MemberEntry
        {
            Kind = "delegate",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = null,
            Namespace = containingNamespace?.Name.ToString(),
            IsRecord = false,
            Modifiers = delegateDecl.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = delegateDecl.ReturnType.ToString(),
            Parameters = parameters,
            LeadingComments = GetLeadingComments(delegateDecl),
            Location = LocationOf(delegateDecl),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static bool TryBuildMember(
        MemberDeclarationSyntax member,
        TypeDeclarationSyntax containingType,
        BaseNamespaceDeclarationSyntax? containingNamespace,
        List<MemberEntry> sink)
    {
        switch (member)
        {
            case TypeDeclarationSyntax nestedType:
                sink.Add(BuildNestedType(nestedType, containingType, containingNamespace));
                return true;
            case DelegateDeclarationSyntax nestedDelegate:
                sink.Add(BuildNestedDelegate(nestedDelegate, containingType, containingNamespace));
                return true;
            case MethodDeclarationSyntax method:
                sink.Add(BuildMethod(method, containingType));
                return true;
            case ConstructorDeclarationSyntax ctor:
                sink.Add(BuildConstructor(ctor, containingType));
                return true;
            case DestructorDeclarationSyntax dtor:
                sink.Add(BuildDestructor(dtor, containingType));
                return true;
            case PropertyDeclarationSyntax property:
                sink.Add(BuildProperty(property, containingType));
                return true;
            case IndexerDeclarationSyntax indexer:
                sink.Add(BuildIndexer(indexer, containingType));
                return true;
            case FieldDeclarationSyntax field:
                BuildFields(field, containingType, sink);
                return true;
            case EventFieldDeclarationSyntax eventField:
                BuildEventFields(eventField, containingType, sink);
                return true;
            case EventDeclarationSyntax eventDecl:
                sink.Add(BuildEvent(eventDecl, containingType));
                return true;
            case OperatorDeclarationSyntax op:
                sink.Add(BuildOperator(op, containingType));
                return true;
            case ConversionOperatorDeclarationSyntax conv:
                sink.Add(BuildConversionOperator(conv, containingType));
                return true;
            default:
                return false;
        }
    }

    private static MemberEntry BuildNestedType(
        TypeDeclarationSyntax nestedType,
        TypeDeclarationSyntax containingType,
        BaseNamespaceDeclarationSyntax? containingNamespace)
    {
        var entry = BuildType(nestedType, containingNamespace);
        return entry with { ContainingType = containingType.Identifier.ValueText };
    }

    private static MemberEntry BuildNestedDelegate(
        DelegateDeclarationSyntax nestedDelegate,
        TypeDeclarationSyntax containingType,
        BaseNamespaceDeclarationSyntax? containingNamespace)
    {
        var entry = BuildDelegate(nestedDelegate, containingNamespace);
        return entry with { ContainingType = containingType.Identifier.ValueText };
    }

    private static MemberEntry BuildMethod(
        MethodDeclarationSyntax method, TypeDeclarationSyntax containingType)
    {
        var name = method.Identifier.ValueText;
        var parameters = BuildParameters(method.ParameterList);

        return new MemberEntry
        {
            Kind = "method",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = method.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = method.ReturnType.ToString(),
            Parameters = parameters,
            LeadingComments = GetLeadingComments(method),
            Location = LocationOf(method),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildConstructor(
        ConstructorDeclarationSyntax ctor, TypeDeclarationSyntax containingType)
    {
        var name = ctor.Identifier.ValueText;
        var parameters = BuildParameters(ctor.ParameterList);

        return new MemberEntry
        {
            Kind = "constructor",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = ctor.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = null,
            Parameters = parameters,
            LeadingComments = GetLeadingComments(ctor),
            Location = LocationOf(ctor),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildDestructor(
        DestructorDeclarationSyntax dtor, TypeDeclarationSyntax containingType)
    {
        var name = dtor.Identifier.ValueText;

        return new MemberEntry
        {
            Kind = "destructor",
            Name = name,
            FullName = name,
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = dtor.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = null,
            Parameters = Array.Empty<ParameterEntry>(),
            LeadingComments = GetLeadingComments(dtor),
            Location = LocationOf(dtor),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildProperty(
        PropertyDeclarationSyntax property, TypeDeclarationSyntax containingType)
    {
        var name = property.Identifier.ValueText;

        return new MemberEntry
        {
            Kind = "property",
            Name = name,
            FullName = name,
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = property.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = property.Type.ToString(),
            Parameters = Array.Empty<ParameterEntry>(),
            LeadingComments = GetLeadingComments(property),
            Location = LocationOf(property),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildIndexer(
        IndexerDeclarationSyntax indexer, TypeDeclarationSyntax containingType)
    {
        var name = "this";
        var parameters = BuildParameters(indexer.ParameterList);

        return new MemberEntry
        {
            Kind = "indexer",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = indexer.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = indexer.Type.ToString(),
            Parameters = parameters,
            LeadingComments = GetLeadingComments(indexer),
            Location = LocationOf(indexer),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static void BuildFields(
        FieldDeclarationSyntax field, TypeDeclarationSyntax containingType, List<MemberEntry> sink)
    {
        var modifiers = field.Modifiers.Select(t => t.ValueText).ToArray();
        var type = field.Declaration.Type.ToString();
        var leadingComments = GetLeadingComments(field);
        var location = LocationOf(field);

        foreach (var variable in field.Declaration.Variables)
        {
            var name = variable.Identifier.ValueText;
            sink.Add(new MemberEntry
            {
                Kind = "field",
                Name = name,
                FullName = name,
                ContainingType = containingType.Identifier.ValueText,
                Namespace = null,
                IsRecord = false,
                Modifiers = modifiers,
                ReturnType = type,
                Parameters = Array.Empty<ParameterEntry>(),
                LeadingComments = leadingComments,
                Location = location,
                Members = Array.Empty<MemberEntry>(),
            });
        }
    }

    private static void BuildEventFields(
        EventFieldDeclarationSyntax eventField, TypeDeclarationSyntax containingType, List<MemberEntry> sink)
    {
        var modifiers = eventField.Modifiers.Select(t => t.ValueText).ToArray();
        var type = eventField.Declaration.Type.ToString();
        var leadingComments = GetLeadingComments(eventField);
        var location = LocationOf(eventField);

        foreach (var variable in eventField.Declaration.Variables)
        {
            var name = variable.Identifier.ValueText;
            sink.Add(new MemberEntry
            {
                Kind = "event-field",
                Name = name,
                FullName = name,
                ContainingType = containingType.Identifier.ValueText,
                Namespace = null,
                IsRecord = false,
                Modifiers = modifiers,
                ReturnType = type,
                Parameters = Array.Empty<ParameterEntry>(),
                LeadingComments = leadingComments,
                Location = location,
                Members = Array.Empty<MemberEntry>(),
            });
        }
    }

    private static MemberEntry BuildEvent(
        EventDeclarationSyntax eventDecl, TypeDeclarationSyntax containingType)
    {
        var name = eventDecl.Identifier.ValueText;

        return new MemberEntry
        {
            Kind = "event",
            Name = name,
            FullName = name,
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = eventDecl.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = eventDecl.Type.ToString(),
            Parameters = Array.Empty<ParameterEntry>(),
            LeadingComments = GetLeadingComments(eventDecl),
            Location = LocationOf(eventDecl),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildOperator(
        OperatorDeclarationSyntax op, TypeDeclarationSyntax containingType)
    {
        var name = op.OperatorToken.ValueText;
        var parameters = BuildParameters(op.ParameterList);

        return new MemberEntry
        {
            Kind = "operator",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = op.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = op.ReturnType.ToString(),
            Parameters = parameters,
            LeadingComments = GetLeadingComments(op),
            Location = LocationOf(op),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static MemberEntry BuildConversionOperator(
        ConversionOperatorDeclarationSyntax conv, TypeDeclarationSyntax containingType)
    {
        var name = conv.ImplicitOrExplicitKeyword.ValueText;
        var parameters = BuildParameters(conv.ParameterList);

        return new MemberEntry
        {
            Kind = "conversion-operator",
            Name = name,
            FullName = BuildFullName(name, parameters),
            ContainingType = containingType.Identifier.ValueText,
            Namespace = null,
            IsRecord = false,
            Modifiers = conv.Modifiers.Select(t => t.ValueText).ToArray(),
            ReturnType = conv.Type.ToString(),
            Parameters = parameters,
            LeadingComments = GetLeadingComments(conv),
            Location = LocationOf(conv),
            Members = Array.Empty<MemberEntry>(),
        };
    }

    private static IReadOnlyList<ParameterEntry> BuildParameters(BaseParameterListSyntax? parameterList)
    {
        if (parameterList is null)
        {
            return Array.Empty<ParameterEntry>();
        }

        var result = new List<ParameterEntry>();
        foreach (var parameter in parameterList.Parameters)
        {
            result.Add(new ParameterEntry
            {
                Name = parameter.Identifier.ValueText,
                Type = parameter.Type?.ToString(),
                Modifiers = parameter.Modifiers.Select(t => t.ValueText).ToArray(),
                DefaultValue = parameter.Default?.Value.ToString(),
            });
        }
        return result;
    }

    /// <summary>
    /// Builds a member's full name: the name followed by its comma-separated
    /// parameter types in parentheses, e.g. <c>WriteNode(TextWriter, DependencyNode)</c>.
    /// Members without parameters (properties, fields, events) return just the name.
    /// </summary>
    private static string BuildFullName(string name, IReadOnlyList<ParameterEntry> parameters)
    {
        if (parameters.Count == 0)
        {
            return name;
        }

        var types = string.Join(", ", parameters.Select(p => p.Type ?? "?"));
        return $"{name}({types})";
    }

    private static SourceLocation LocationOf(SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var start = lineSpan.StartLinePosition;
        return new SourceLocation(
            lineSpan.Path,
            start.Line + 1,
            start.Character + 1);
    }

    /// <summary>
    /// Extracts the leading comment trivia immediately preceding a declaration.
    /// Walks the node's leading trivia backward from the end, collecting comment
    /// trivia and skipping whitespace and the end-of-line that terminates a
    /// comment line. Stops at a blank line or any non-comment, non-whitespace
    /// trivia (e.g. a preprocessor directive).
    /// </summary>
    internal static string? GetLeadingComments(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        if (trivia.Count == 0)
        {
            return null;
        }

        var captured = new List<SyntaxTrivia>();
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var t = trivia[i];
            if (IsComment(t))
            {
                captured.Add(t);
                continue;
            }

            if (t.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            if (t.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                // A blank line: the previous trivia is also an end-of-line (or we
                // are at the start). Stop collecting.
                if (i == 0 || trivia[i - 1].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    break;
                }
                // Otherwise this EOL terminates a comment line; keep going.
                continue;
            }

            // Any other trivia (directive, etc.) stops collection.
            break;
        }

        if (captured.Count == 0)
        {
            return null;
        }

        captured.Reverse();
        return string.Join("\n", captured.Select(t => t.ToFullString().TrimEnd('\r', '\n')));
    }

    private static bool IsComment(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
}
