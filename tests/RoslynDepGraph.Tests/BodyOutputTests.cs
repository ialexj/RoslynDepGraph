// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

public class BodyOutputTests
{
    [Theory]
    [InlineData("body")]
    [InlineData("Body")]
    [InlineData("BODY")]
    public void Cli_accepts_body_format(string format)
    {
        var result = CliOptions.CreateRootCommand().Parse(["sample.csproj", "--format", format]);

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Body, result.GetValue(CliOptions.FormatOption));
    }

    [Fact]
    public async Task Member_output_has_exact_header_range_and_full_body_without_enclosing_class()
    {
        const string source = """
            namespace Demo;

            class Service
            {
                // Outside the declaration.
                public int Run(int value)
                {
                    // Inside the body.
                    return value + 1;
                }

                public int Other() => 0;
            }
            """;
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, ["Demo.Service.Run"], source);

        var output = await OutputFormatter.ToBodyAsync(report);

        var expected = """
            // Demo.Service.Run(int value), Source1.cs, 6-10
            public int Run(int value)
                {
                    // Inside the body.
                    return value + 1;
                }
            """;
        Assert.Equal(expected.ReplaceLineEndings() + Environment.NewLine + Environment.NewLine, output);
    }

    [Theory]
    [InlineData(Granularity.Type, true, false)]
    [InlineData(Granularity.Member, false, true)]
    [InlineData(Granularity.Both, true, true)]
    public async Task Granularity_controls_which_declarations_are_printed(
        Granularity granularity, bool expectType, bool expectMember)
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, granularity, [],
            "namespace Demo; class Service { public int Run() => 1; }");

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Equal(expectType, output.Contains($"// Demo.Service, Source1.cs, 1-1{Environment.NewLine}class Service"));
        Assert.Equal(expectType, output.Contains("class Service"));
        Assert.Equal(expectMember, output.Contains($"// Demo.Service.Run(), Source1.cs, 1-1{Environment.NewLine}public int Run()"));
        Assert.Contains("public int Run() => 1;", output);
        Assert.DoesNotContain("Summary:", output);
        Assert.DoesNotContain("Declarations", OutputFormatter.ToJson(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fields_events_properties_and_constructors_include_complete_declarations()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, [], """
            namespace Demo;
            class Service
            {
                public int First = 1, Second = 2;
                public event System.Action Changed;
                public int Count { get { return First; } set { First = value; } }
                public Service() { First = 3; }
            }
            """);

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Contains("// Demo.Service.First, Source1.cs, 4-4", output);
        Assert.Contains("// Demo.Service.Second, Source1.cs, 4-4", output);
        Assert.Contains("public int First = 1, Second = 2;", output);
        Assert.Contains("// Demo.Service.Changed, Source1.cs, 5-5", output);
        Assert.Contains("public event System.Action Changed;", output);
        Assert.Contains("// Demo.Service.Count, Source1.cs, 6-6", output);
        Assert.Contains("public int Count { get { return First; } set { First = value; } }", output);
        Assert.Contains("public Service() { First = 3; }", output);
        Assert.DoesNotContain("class Service", output);
    }

    [Fact]
    public async Task Partial_types_and_methods_print_each_declaration_and_implementation()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Both, [], """
            namespace Demo;
            partial class Service
            {
                public partial int Run();
            }
            """, """
            namespace Demo;
            partial class Service
            {
                public partial int Run() => 42;
            }
            """);

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Contains("// Demo.Service, Source1.cs, 2-5", output);
        Assert.Contains("// Demo.Service, Source2.cs, 2-5", output);
        Assert.Contains("// Demo.Service.Run(), Source1.cs, 4-4", output);
        Assert.Contains("// Demo.Service.Run(), Source2.cs, 4-4", output);
        Assert.Contains("public partial int Run() => 42;", output);
    }

    [Fact]
    public async Task Partial_properties_include_the_implementation()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, ["Demo.Service.Count"], """
            namespace Demo;
            partial class Service
            {
                public partial int Count { get; }
            }
            """, """
            namespace Demo;
            partial class Service
            {
                public partial int Count => 42;
            }
            """);

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Contains("// Demo.Service.Count, Source1.cs, 4-4", output);
        Assert.Contains("// Demo.Service.Count, Source2.cs, 4-4", output);
        Assert.Contains("public partial int Count { get; }", output);
        Assert.Contains("public partial int Count => 42;", output);
        Assert.DoesNotContain("class Service", output);
    }

    [Theory]
    [InlineData("public enum Choice { First, Second }", "Choice")]
    [InlineData("public delegate int Compute(int value);", "Compute")]
    [InlineData("public record Item(int Value);", "Item")]
    public async Task Type_output_supports_other_named_type_declarations(string source, string typeName)
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Type, [], "namespace Demo; " + source);

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Equal(
            $"// Demo.{typeName}, Source1.cs, 1-1{Environment.NewLine}"
            + source + Environment.NewLine + Environment.NewLine,
            output);
    }

    [Fact]
    public async Task Primary_constructors_never_print_the_enclosing_type_in_member_mode()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, [], """
            namespace Demo;
            class Service(int value)
            {
                public int Run() => value;
            }
            """);

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Contains("public int Run() => value;", output);
        Assert.DoesNotContain("class Service", output);
    }

    [Fact]
    public async Task Empty_node_omission_is_preserved()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, [],
            "namespace Demo; class Service { public void Run() { } }");
        var solution = workspace.CurrentSolution;
        var omitted = await new ReportBuilder().BuildAsync(
            solution, "sample.csproj", "project", report.Options with { IncludeEmpty = false });

        Assert.Empty(omitted.Nodes);
        Assert.Equal("", await OutputFormatter.ToBodyAsync(omitted));
        Assert.NotEmpty(await OutputFormatter.ToBodyAsync(report));
    }

    [Fact]
    public async Task Body_output_observes_cancellation()
    {
        using var workspace = new AdhocWorkspace();
        var report = await BuildAsync(workspace, Granularity.Member, [],
            "namespace Demo; class Service { public void Run() { } }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OutputFormatter.ToBodyAsync(report, cts.Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Transitive_member_output_includes_reachable_bodies_once_but_not_whole_classes(
        bool transitive, bool excludeWorkers)
    {
        using var workspace = new AdhocWorkspace();
        await BuildAsync(workspace, Granularity.Member, [], """
            namespace Demo;
            class Entry
            {
                public int Run() => Worker.Next() + Worker.Leaf();
            }
            class Worker
            {
                public static int Next() => Last() + Leaf();
                public static int Last() => Next() + Value;
                public static int Leaf() => 1;
                public static int Value = 42;
                public static int Unrelated() => 99;
            }
            """);
        var report = await new ReportBuilder().BuildAsync(
            workspace.CurrentSolution, "sample.csproj", "project",
            ReportTestHarness.DefaultOptions() with
            {
                Granularity = Granularity.Member,
                Include = ["Demo.Entry.Run"],
                Exclude = excludeWorkers ? ["Demo.Worker.*"] : [],
                Transitive = transitive,
            });

        var output = await OutputFormatter.ToBodyAsync(report);
        var headers = output.Split(Environment.NewLine)
            .Where(line => line.StartsWith("// Demo.", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(report.Nodes);
        Assert.Equal("Demo.Entry.Run()", report.Nodes[0].Id);
        var includeDependencies = transitive && !excludeWorkers;
        Assert.Equal(includeDependencies ? 5 : 1, headers.Length);
        Assert.Equal(headers.Length, headers.Distinct().Count());
        Assert.Equal("// Demo.Entry.Run(), Source1.cs, 4-4", headers[0]);
        Assert.Equal(includeDependencies, output.Contains("public static int Next() => Last() + Leaf();"));
        Assert.Equal(includeDependencies, output.Contains("public static int Last() => Next() + Value;"));
        Assert.Equal(includeDependencies, output.Contains("public static int Leaf() => 1;"));
        Assert.Equal(includeDependencies, output.Contains("public static int Value = 42;"));
        Assert.DoesNotContain("class Entry", output);
        Assert.DoesNotContain("class Worker", output);
        Assert.DoesNotContain("Unrelated", output);
        Assert.DoesNotContain("bodyDependencies", OutputFormatter.ToJson(report));
        Assert.DoesNotContain("public static int Leaf()", OutputFormatter.ToText(report));
        Assert.Equal(output, await OutputFormatter.ToBodyAsync(report));
    }

    [Theory]
    [InlineData(Granularity.Type)]
    [InlineData(Granularity.Both)]
    public async Task Transitive_type_output_includes_dependency_types(Granularity granularity)
    {
        using var workspace = new AdhocWorkspace();
        await BuildAsync(workspace, granularity, [], """
            namespace Demo;
            class Entry { public int Run() => Worker.Next(); }
            class Worker { public static int Next() => Last.Leaf(); }
            class Last { public static int Leaf() => 42; }
            class Unrelated { }
            """);
        var report = await new ReportBuilder().BuildAsync(
            workspace.CurrentSolution, "sample.csproj", "project",
            ReportTestHarness.DefaultOptions() with
            {
                Granularity = granularity,
                Include = ["Demo.Entry"],
                Transitive = true,
            });

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Single(report.Nodes);
        Assert.Contains($"// Demo.Entry, Source1.cs, 2-2{Environment.NewLine}class Entry", output);
        Assert.Contains($"// Demo.Worker, Source1.cs, 3-3{Environment.NewLine}class Worker", output);
        Assert.Contains($"// Demo.Last, Source1.cs, 4-4{Environment.NewLine}class Last", output);
        Assert.DoesNotContain("class Unrelated", output);
        Assert.Equal(granularity == Granularity.Both,
            output.Contains($"// Demo.Worker.Next(), Source1.cs, 3-3{Environment.NewLine}public static int Next()"));
        Assert.Equal(granularity == Granularity.Both,
            output.Contains($"// Demo.Last.Leaf(), Source1.cs, 4-4{Environment.NewLine}public static int Leaf()"));
    }

    [Fact]
    public async Task Transitive_member_output_includes_partial_implementations_and_skips_external_bodies()
    {
        using var workspace = new AdhocWorkspace();
        await BuildAsync(workspace, Granularity.Member, [], """
            namespace Demo;
            partial class Service
            {
                public string Run() => Next().ToString();
                public partial int Next();
            }
            """, """
            namespace Demo;
            partial class Service
            {
                public partial int Next() => 42;
            }
            """);
        var report = await new ReportBuilder().BuildAsync(
            workspace.CurrentSolution, "sample.csproj", "project",
            ReportTestHarness.DefaultOptions() with
            {
                Granularity = Granularity.Member,
                Include = ["Demo.Service.Run"],
                IncludeSystem = true,
                Transitive = true,
            });

        var output = await OutputFormatter.ToBodyAsync(report);

        Assert.Contains("// Demo.Service.Next(), Source1.cs, 5-5", output);
        Assert.Contains("// Demo.Service.Next(), Source2.cs, 4-4", output);
        Assert.Contains("public partial int Next() => 42;", output);
        Assert.DoesNotContain("class Service", output);
        Assert.Equal(3, output.Split(Environment.NewLine)
            .Count(line => line.StartsWith("// Demo.", StringComparison.Ordinal)));
    }

    private static async Task<DependencyReport> BuildAsync(
        AdhocWorkspace workspace, Granularity granularity, string[] includes, params string[] sources)
    {
        var project = workspace.AddProject("BodyFixture", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        for (var i = 0; i < sources.Length; i++)
        {
            project = project.AddDocument(
                $"Source{i + 1}.cs", SourceText.From(sources[i].ReplaceLineEndings()),
                filePath: $@"C:\Fixtures\Source{i + 1}.cs").Project;
        }
        Assert.True(workspace.TryApplyChanges(project.Solution));

        return await new ReportBuilder().BuildAsync(
            workspace.CurrentSolution, "sample.csproj", "project",
            ReportTestHarness.DefaultOptions() with
            {
                Granularity = granularity,
                Include = includes,
                IncludeEmpty = true,
            });
    }
}
