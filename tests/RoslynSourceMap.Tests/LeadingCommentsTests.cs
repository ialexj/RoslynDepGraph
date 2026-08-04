// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RoslynSourceMap.Tests;

/// <summary>
/// Verifies the leading-comments extraction algorithm in
/// <see cref="MemberExtractor.GetLeadingComments"/>.
/// </summary>
public class LeadingCommentsTests
{
    [Fact]
    public async Task Single_line_comment_is_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    // hello\n    void M() { }\n}");

        var m = entry.Types[0].Members.Single();
        Assert.Equal("// hello", m.LeadingComments);
    }

    [Fact]
    public async Task Multiple_consecutive_comments_are_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    // one\n    // two\n    void M() { }\n}");

        var m = entry.Types[0].Members.Single();
        Assert.Equal("// one\n// two", m.LeadingComments);
    }

    [Fact]
    public async Task Multi_line_comment_is_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    /* block\n       comment */\n    void M() { }\n}");

        var m = entry.Types[0].Members.Single();
        Assert.Equal("/* block\n       comment */", m.LeadingComments);
    }

    [Fact]
    public async Task Documentation_comment_is_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    /// <summary>Doc</summary>\n    void M() { }\n}");

        var m = entry.Types[0].Members.Single();
        Assert.Equal("/// <summary>Doc</summary>", m.LeadingComments);
    }

    [Fact]
    public async Task Blank_line_separates_comment_from_member()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    // far away\n\n    void M() { }\n}");

        var m = entry.Types[0].Members.Single();
        Assert.Null(m.LeadingComments);
    }

    [Fact]
    public async Task Comment_after_previous_member_brace_is_not_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "class Foo {\n    void A() { } // trailing\n    void B() { }\n}");

        var b = entry.Types[0].Members.Single(m => m.Name == "B");
        Assert.Null(b.LeadingComments);
    }

    [Fact]
    public async Task Class_level_comment_is_captured()
    {
        var entry = await TestHarness.ExtractAsync(
            "// Generated from: Optiware/Auto.ctl:61\nclass Foo { }");

        var foo = entry.Types.Single();
        Assert.Equal("// Generated from: Optiware/Auto.ctl:61", foo.LeadingComments);
    }
}
