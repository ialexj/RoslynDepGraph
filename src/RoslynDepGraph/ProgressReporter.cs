// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace RoslynDepGraph;

/// <summary>
/// Reports progress to stderr in a redirection-aware way.
///
/// When stderr is attached to a terminal, progress is written as an in-place
/// updating line (carriage-return animation) so the screen doesn't flood.
/// When stderr is redirected (file, pipe, 2&gt;&amp;1), the animation is suppressed
/// and progress is emitted as newline-terminated summary lines at a throttled
/// cadence, so logs stay readable and don't bloat.
///
/// Either way, stdout (the actual report) is never touched.
/// </summary>
public sealed class ProgressReporter
{
    private readonly bool _isInteractive;
    private readonly TimeSpan _minInterval;
    private DateTime _lastWrite;
    private string _lastLabel = "";

    public ProgressReporter(TimeSpan? minInterval = null)
    {
        _isInteractive = !Console.IsErrorRedirected;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(250);
        _lastWrite = DateTime.MinValue;
    }

    /// <summary>
    /// Reports progress for the item at <paramref name="index"/> (0-based) out of
    /// <paramref name="total"/>, with a human-readable <paramref name="label"/>.
    /// </summary>
    public void Report(int index, int total, string label)
    {
        if (total <= 0)
        {
            return;
        }

        if (_isInteractive)
        {
            // In-place animation. Pad/truncate so a shorter label fully overwrites
            // a longer previous one.
            var text = $"[{index + 1}/{total}] {label}";
            if (text.Length > 80)
            {
                text = text[..77] + "...";
            }
            Console.Error.Write($"\r{text}".PadRight(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80));
            _lastWrite = DateTime.UtcNow;
            _lastLabel = label;
            return;
        }

        // Redirected: throttle to one line per interval so logs don't bloat.
        var now = DateTime.UtcNow;
        if (now - _lastWrite < _minInterval)
        {
            return;
        }
        _lastWrite = now;
        Console.Error.WriteLine($"[{index + 1}/{total}] {label}");
    }

    /// <summary>
    /// Clears the in-place animation (if any) and writes a final newline-terminated
    /// message. Safe to call in both interactive and redirected modes.
    /// </summary>
    public void Complete(string message)
    {
        if (_isInteractive)
        {
            // Overwrite the animation line, then move down.
            Console.Error.Write("\r" + new string(' ', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80) + "\r");
        }
        Console.Error.WriteLine(message);
    }
}
