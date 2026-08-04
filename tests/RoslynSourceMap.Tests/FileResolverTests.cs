// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RoslynSourceMap.Tests;

/// <summary>
/// Verifies <see cref="FileResolver"/> path resolution: single file, directory,
/// and glob patterns.
/// </summary>
public class FileResolverTests
{
    [Fact]
    public void Existing_cs_file_resolves_to_itself()
    {
        var tmp = Path.GetTempFileName() + ".cs";
        try
        {
            File.WriteAllText(tmp, "class A { }");
            var files = FileResolver.Resolve(tmp);
            Assert.Equal(new[] { tmp }, files);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void Non_cs_file_resolves_to_empty()
    {
        var tmp = Path.GetTempFileName() + ".txt";
        try
        {
            File.WriteAllText(tmp, "hello");
            Assert.Empty(FileResolver.Resolve(tmp));
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void Directory_resolves_recursively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rsm-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "A.cs");
            var sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            var b = Path.Combine(sub, "B.cs");
            File.WriteAllText(a, "class A { }");
            File.WriteAllText(b, "class B { }");

            var files = FileResolver.Resolve(dir);
            Assert.Equal(2, files.Count);
            Assert.Contains(a, files);
            Assert.Contains(b, files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Glob_pattern_resolves_matching_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rsm-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "A.cs");
            var sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            var b = Path.Combine(sub, "B.cs");
            File.WriteAllText(a, "class A { }");
            File.WriteAllText(b, "class B { }");

            var pattern = Path.Combine(dir, "**", "*.cs");
            var files = FileResolver.Resolve(pattern);
            Assert.Equal(2, files.Count);
            Assert.Contains(a, files);
            Assert.Contains(b, files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nonexistent_path_resolves_to_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rsm-missing-" + Guid.NewGuid().ToString("N") + ".cs");
        Assert.Empty(FileResolver.Resolve(missing));
    }
}
