// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading.Tasks;
using RoslynSourceMap.Models;
using Xunit;

namespace RoslynSourceMap.Tests;

/// <summary>
/// Verifies that <see cref="MemberExtractor"/> correctly classifies type and
/// member declarations from syntax alone (no semantic model).
/// </summary>
public class MemberExtractorTests
{
    [Fact]
    public async Task Class_with_members_is_extracted()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { public void Bar() { } public int Baz; }");

        var foo = Assert.Single(entry.Types);
        Assert.Equal("class", foo.Kind);
        Assert.Equal("Foo", foo.Name);
        Assert.Empty(foo.Modifiers);
        Assert.Equal(2, foo.Members.Count);

        var bar = foo.Members[0];
        Assert.Equal("method", bar.Kind);
        Assert.Equal("Bar", bar.Name);
        Assert.Equal("Foo", bar.ContainingType);
        Assert.Equal(new[] { "public" }, bar.Modifiers);
        Assert.Equal("void", bar.ReturnType);

        var baz = foo.Members[1];
        Assert.Equal("field", baz.Kind);
        Assert.Equal("Baz", baz.Name);
        Assert.Equal("int", baz.ReturnType);
    }

    [Fact]
    public async Task Namespace_is_reported_for_block_scoped_namespace()
    {
        var entry = await TestHarness.ExtractAsync(
            "namespace A.B { class Foo { } }");

        var foo = Assert.Single(entry.Types);
        Assert.Equal("A.B", foo.Namespace);
    }

    [Fact]
    public async Task Namespace_is_reported_for_file_scoped_namespace()
    {
        var entry = await TestHarness.ExtractAsync(
            "namespace A.B;\nclass Foo { }");

        var foo = Assert.Single(entry.Types);
        Assert.Equal("A.B", foo.Namespace);
    }

    [Fact]
    public async Task Nested_type_appears_in_parent_members()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Outer { class Inner { public void Run() { } } }");

        var outer = Assert.Single(entry.Types);
        var inner = Assert.Single(outer.Members, m => m.Kind == "class");
        Assert.Equal("Inner", inner.Name);
        Assert.Equal("Outer", inner.ContainingType);
        Assert.Single(inner.Members);
    }

    [Fact]
    public async Task Field_with_multiple_declarators_yields_one_entry_per_name()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { public int A, B; }");

        var foo = Assert.Single(entry.Types);
        var fields = foo.Members.Where(m => m.Kind == "field").ToArray();
        Assert.Equal(2, fields.Length);
        Assert.Equal(new[] { "A", "B" }, fields.Select(f => f.Name));
        Assert.All(fields, f => Assert.Equal("int", f.ReturnType));
        Assert.All(fields, f => Assert.Equal(new[] { "public" }, f.Modifiers));
    }

    [Fact]
    public async Task Record_is_detected_with_primary_constructor_parameters()
    {
        var entry = await TestHarness.ExtractAsync(
            "public record Point(int X, int Y);");

        var point = Assert.Single(entry.Types);
        Assert.Equal("record", point.Kind);
        Assert.True(point.IsRecord);
        Assert.Equal(new[] { "public" }, point.Modifiers);
        Assert.Equal(2, point.Parameters.Count);
        Assert.Equal(new[] { "X", "Y" }, point.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { "int", "int" }, point.Parameters.Select(p => p.Type));
    }

    [Fact]
    public async Task Delegate_is_extracted_with_return_type_and_parameters()
    {
        var entry = await TestHarness.ExtractAsync(
            "public delegate int Handler(string s);");

        var handler = Assert.Single(entry.Types);
        Assert.Equal("delegate", handler.Kind);
        Assert.Equal("Handler", handler.Name);
        Assert.Equal("int", handler.ReturnType);
        var p = Assert.Single(handler.Parameters);
        Assert.Equal("s", p.Name);
        Assert.Equal("string", p.Type);
    }

    [Fact]
    public async Task Property_indexer_constructor_and_event_are_classified()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { public Foo() { } public int this[int i] => i; public int P { get; set; } public event System.EventHandler? E; }");

        var foo = Assert.Single(entry.Types);
        Assert.Contains(foo.Members, m => m.Kind == "constructor" && m.Name == "Foo");
        Assert.Contains(foo.Members, m => m.Kind == "indexer" && m.Name == "this");
        Assert.Contains(foo.Members, m => m.Kind == "property" && m.Name == "P");
        Assert.Contains(foo.Members, m => m.Kind == "event-field" && m.Name == "E");
    }

    [Fact]
    public async Task Method_parameters_include_modifiers_and_defaults()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { void M(int a, string b = \"x\", params int[] rest) { } }");

        var foo = Assert.Single(entry.Types);
        var m = Assert.Single(foo.Members);
        Assert.Equal(3, m.Parameters.Count);
        Assert.Equal("a", m.Parameters[0].Name);
        Assert.Equal("b", m.Parameters[1].Name);
        Assert.Equal("\"x\"", m.Parameters[1].DefaultValue);
        Assert.Equal(new[] { "params" }, m.Parameters[2].Modifiers);
    }

    [Fact]
    public async Task Method_full_name_includes_parameter_types()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { void WriteNode(TextWriter writer, DependencyNode node) { } }");

        var foo = Assert.Single(entry.Types);
        var m = Assert.Single(foo.Members);
        Assert.Equal("WriteNode(TextWriter, DependencyNode)", m.FullName);
    }

    [Fact]
    public async Task Full_name_for_members_without_parameters_is_just_the_name()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { public int P { get; set; } public int F; }");

        var foo = Assert.Single(entry.Types);
        var p = foo.Members.Single(m => m.Name == "P");
        var f = foo.Members.Single(m => m.Name == "F");
        Assert.Equal("P", p.FullName);
        Assert.Equal("F", f.FullName);
    }

    [Fact]
    public async Task Constructor_and_indexer_full_names_include_parameter_types()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { public Foo(int a, string b) { } public int this[int i] => i; }");

        var foo = Assert.Single(entry.Types);
        var ctor = foo.Members.Single(m => m.Kind == "constructor");
        var indexer = foo.Members.Single(m => m.Kind == "indexer");
        Assert.Equal("Foo(int, string)", ctor.FullName);
        Assert.Equal("this(int)", indexer.FullName);
    }

    [Fact]
    public async Task Parse_diagnostics_are_reported_but_extraction_continues()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo { void M( { }");

        Assert.NotEmpty(entry.Diagnostics);
        Assert.Single(entry.Types);
    }
}
