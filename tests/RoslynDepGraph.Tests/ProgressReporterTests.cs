// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RoslynDepGraph.Tests;

[CollectionDefinition("Console output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
}

[Collection("Console output")]
public sealed class ProgressReporterTests
{
    [Fact]
    public void Reports_preserve_every_full_line_and_completion()
    {
        var originalError = Console.Error;
        var originalOut = Console.Out;
        using var error = new StringWriter();
        using var output = new StringWriter();
        try
        {
            Console.SetError(error);
            Console.SetOut(output);
            var reporter = new ProgressReporter();
            var label = new string('x', 120);

            reporter.Report(0, 2, label);
            reporter.Report(1, 2, "short");
            reporter.ReportQueueProgress(1, 3, 2, label);
            reporter.ReportQueueProgress(2, 3, 0, "short");
            reporter.Complete("Done.");

            Assert.Equal(
                $"[1/2] {label}{Environment.NewLine}" +
                $"[2/2] short{Environment.NewLine}" +
                $"[1 complete; +2 queued, 3 total] {label}{Environment.NewLine}" +
                $"[2 complete; +0 queued, 3 total] short{Environment.NewLine}" +
                $"Done.{Environment.NewLine}",
                error.ToString());
            Assert.Equal("", output.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Concurrent_reports_preserve_all_lines()
    {
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var reporter = new ProgressReporter();

            Parallel.For(0, 100, i => reporter.ReportQueueProgress(i + 1, 100, 0, $"Node{i}"));

            var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var expected = Enumerable.Range(0, 100)
                .Select(i => $"[{i + 1} complete; +0 queued, 100 total] Node{i}");
            Assert.Equal(expected.OrderBy(line => line), lines.OrderBy(line => line));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
