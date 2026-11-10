// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynDepGraph;

/// <summary>
/// Reports every progress update as a full, newline-terminated line on stderr,
/// whether attached to a terminal or redirected. Stdout (the actual report)
/// is never touched.
/// </summary>
public sealed class ProgressReporter
{
    private readonly object _lock = new();

    /// <summary>
    /// Reports progress for the item at <paramref name="index"/> (0-based) out of
    /// <paramref name="total"/>, with a human-readable <paramref name="label"/>.
    /// Thread-safe: may be called concurrently from parallel workers.
    /// </summary>
    public void Report(int index, int total, string label)
    {
        if (total <= 0)
        {
            return;
        }

        lock (_lock)
        {
            Console.Error.WriteLine($"[{index + 1}/{total}] {label}");
        }
    }

    /// <summary>
    /// Reports completion from a dynamically expanding analysis queue.
    /// </summary>
    public void ReportQueueProgress(int completed, int queued, int added, string label)
    {
        lock (_lock)
        {
            var message = $"[{completed} complete; +{added} queued, {queued} total] {label}";
            Console.Error.WriteLine(message);
        }
    }

    /// <summary>
    /// Writes a final newline-terminated message without clearing prior progress.
    /// Thread-safe.
    /// </summary>
    public void Complete(string message)
    {
        lock (_lock)
        {
            Console.Error.WriteLine(message);
        }
    }
}
