// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RoslynSourceMap.Models;
using Xunit;

namespace RoslynSourceMap.Tests;

/// <summary>
/// End-to-end test against the on-disk <c>Fixtures/SampleMembers.cs</c> fixture,
/// verifying the full pipeline (parse → extract → JSON) and that leading
/// comments round-trip through the JSON output.
/// </summary>
public class EndToEndTests
{
    [Fact]
    public async Task Fixture_produces_expected_types_and_members()
    {
        var file = TestPaths.FixtureFile("SampleMembers.cs");
        var entry = await MemberExtractor.ExtractAsync(file);

        Assert.Empty(entry.Diagnostics);
        Assert.Equal(2, entry.Types.Count);

        var autoForm = entry.Types.Single(t => t.Name == "AutoForm");
        Assert.Equal("class", autoForm.Kind);
        Assert.Equal("SampleApp", autoForm.Namespace);

        // Command1_Click carries the "Generated from" comment.
        var click = autoForm.Members.Single(m => m.Name == "Command1_Click");
        Assert.Equal("// Generated from: Optiware/Auto.ctl:61", click.LeadingComments);

        // Nested type appears in AutoForm's members.
        var nested = autoForm.Members.Single(m => m.Name == "Nested");
        Assert.Equal("class", nested.Kind);
        Assert.Equal("AutoForm", nested.ContainingType);

        // Record with primary constructor parameters.
        var point = entry.Types.Single(t => t.Name == "Point");
        Assert.True(point.IsRecord);
        Assert.Equal(2, point.Parameters.Count);
    }

    [Fact]
    public async Task Fixture_json_round_trips_leading_comments()
    {
        var file = TestPaths.FixtureFile("SampleMembers.cs");
        var entry = await MemberExtractor.ExtractAsync(file);

        var report = new SourceMapReport
        {
            Tool = "RoslynSourceMap",
            SchemaVersion = "1.0",
            Files = new[] { entry },
        };

        var json = OutputFormatter.ToJson(report);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("RoslynSourceMap", root.GetProperty("tool").GetString());
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());

        var files = root.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        var types = files[0].GetProperty("types");
        Assert.Equal(2, types.GetArrayLength());

        var autoForm = types.EnumerateArray().First(t => t.GetProperty("name").GetString() == "AutoForm");
        var click = autoForm.GetProperty("members").EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == "Command1_Click");
        Assert.Equal(
            "// Generated from: Optiware/Auto.ctl:61",
            click.GetProperty("leadingComments").GetString());
    }
}
