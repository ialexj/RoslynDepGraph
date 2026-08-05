using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynPrune;

// Reproduce the de-qualification bug.
//
// Scenario: a file has `using VBCore;` and a fully-qualified `new VBCore.GShared()`
// reference that MUST stay. It ALSO has an unused member that gets pruned, which
// triggers Formatter.Format on the tree. Does Formatter.Format de-qualify
// `VBCore.GShared` -> `GShared`?

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PruneQualProbe", "Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Program.cs: has using VBCore, a used fully-qualified reference, AND an unused member.
        var program = @"using VBCore;

namespace QualificationFixture;

public static class Program
{
    public static void Main()
    {
        var g = new VBCore.GShared();
        g.DoWork();
    }

    public static void UnusedMethod()
    {
        System.Console.WriteLine(""unused"");
    }
}
";
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), program);

        // GShared.cs: defines VBCore.GShared (used, must stay).
        File.WriteAllText(Path.Combine(tempDir, "GShared.cs"),
            "namespace VBCore;\n\npublic class GShared\n{\n    public void DoWork() { }\n}\n");

        File.WriteAllText(Path.Combine(tempDir, "QualificationFixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");

        var programPath = Path.Combine(tempDir, "Program.cs");
        Console.WriteLine("=== BEFORE Program.cs ===");
        Console.WriteLine(File.ReadAllText(programPath));

        var projectPath = Path.Combine(tempDir, "QualificationFixture.csproj");
        var (solution, kind) = await WorkspaceLoader.LoadAsync(projectPath, Console.Error, CancellationToken.None);

        var orchestrator = new PruneOrchestrator();
        var report = await orchestrator.RunAsync(
            solution,
            Array.Empty<string>(),
            Array.Empty<string>(),
            apply: true,
            Console.Error,
            CancellationToken.None);

        Console.WriteLine("=== REPORT ===");
        Console.WriteLine($"RemovedTypes: {report.Summary.RemovedTypeCount}");
        foreach (var t in report.RemovedTypes) Console.WriteLine($"  TYPE: {t.FullyQualifiedName}");
        Console.WriteLine($"RemovedMembers: {report.Summary.RemovedMemberCount}");
        foreach (var m in report.RemovedMembers) Console.WriteLine($"  MEMBER: {m.FullyQualifiedName}");
        Console.WriteLine($"RemovedUsings: {report.Summary.RemovedUsingCount}");
        foreach (var u in report.RemovedUsings) Console.WriteLine($"  USING: [{u.Reason}] {u.Directive} @ {u.File}:{u.Line}");
        Console.WriteLine($"WrittenFiles: {report.Summary.WrittenFileCount}");
        foreach (var f in report.WrittenFiles) Console.WriteLine($"  WRITTEN: {f}");

        Console.WriteLine("=== AFTER Program.cs ===");
        Console.WriteLine(File.ReadAllText(programPath));

        return 0;
    }
}
