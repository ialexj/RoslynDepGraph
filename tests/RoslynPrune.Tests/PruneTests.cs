// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynPrune.Models;
using Xunit;

namespace RoslynPrune.Tests;

public class PruneTests
{
    private static readonly string[] NoFilters = Array.Empty<string>();

    private static async Task<(PruneReport Report, IReadOnlyDictionary<string, string> Rewritten)> RunAsync(
        string fixture, bool apply = false, string[]? types = null, string[]? files = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await TestHarness.RunAsync(
            fixture, files ?? NoFilters, types ?? NoFilters, apply, cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task Unused_member_is_removed_but_used_member_is_kept()
    {
        var (report, _) = await RunAsync("UnusedMemberFixture");

        var removed = Assert.Single(report.RemovedMembers);
        Assert.Equal("UnusedMemberFixture.Service.Unused()", removed.FullyQualifiedName);
        Assert.DoesNotContain(report.RemovedMembers, m => m.FullyQualifiedName.Contains("Used"));
    }

    [Fact]
    public async Task Property_accessors_are_not_reported_as_separate_members()
    {
        // Regression: property get/set accessors were reported as separate
        // "methods" (e.g. "Allocator.get") that could not be re-resolved by
        // display string, causing the fixpoint loop to spin forever. The whole
        // property must be the unit of removal, never its accessors.
        var (report, _) = await RunAsync("PropertyAccessorFixture");

        // No member should have ".get" or ".set" in its name.
        Assert.DoesNotContain(report.RemovedMembers, m => m.FullyQualifiedName.Contains(".get"));
        Assert.DoesNotContain(report.RemovedMembers, m => m.FullyQualifiedName.Contains(".set"));

        // The three unused properties are removed; the used one is kept.
        Assert.Contains(report.RemovedMembers, m => m.FullyQualifiedName == "PropertyAccessorFixture.Service.UnusedProperty");
        Assert.Contains(report.RemovedMembers, m => m.FullyQualifiedName == "PropertyAccessorFixture.Service.UnusedReadOnly");
        Assert.Contains(report.RemovedMembers, m => m.FullyQualifiedName == "PropertyAccessorFixture.Service.UnusedWriteOnly");
        Assert.DoesNotContain(report.RemovedMembers, m => m.FullyQualifiedName.Contains("UsedProperty"));

        // Must terminate (not loop forever).
        Assert.True(report.Summary.Passes < 10, "Fixpoint should converge quickly.");
    }

    [Fact]
    public async Task Unused_type_is_removed_but_used_type_is_kept()
    {
        var (report, _) = await RunAsync("UnusedTypeFixture");

        var removed = Assert.Single(report.RemovedTypes);
        Assert.Equal("UnusedTypeFixture.UnusedType", removed.FullyQualifiedName);
        Assert.DoesNotContain(report.RemovedTypes, t => t.FullyQualifiedName.Contains("UsedType"));
    }

    [Fact]
    public async Task Cascade_removes_type_that_is_only_used_by_an_unused_type()
    {
        var (report, _) = await RunAsync("CascadeFixture");

        // Both A and B are unused (B references A, but nothing references B).
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "CascadeFixture.A");
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "CascadeFixture.B");
        Assert.True(report.Summary.Passes >= 2, "Expected a fixpoint cascade across multiple passes.");
    }

    [Fact]
    public async Task Using_alias_targeting_pruned_type_is_removed()
    {
        var (report, _) = await RunAsync("UsingAliasFixture");

        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "UsingAliasFixture.UnusedType");
        var removedUsing = Assert.Single(report.RemovedUsings);
        Assert.Equal(UsingRemovalReason.AliasTargetPruned, removedUsing.Reason);
        Assert.Contains("Alias", removedUsing.Directive);
    }

    [Fact]
    public async Task Orphaned_namespace_using_is_removed()
    {
        var (report, _) = await RunAsync("OrphanedUsingFixture");

        var removedUsing = Assert.Single(report.RemovedUsings);
        Assert.Equal(UsingRemovalReason.OrphanedNamespace, removedUsing.Reason);
        Assert.Contains("System.Linq", removedUsing.Directive);
    }

    [Fact]
    public async Task Orphaned_global_using_is_removed()
    {
        var (report, _) = await RunAsync("GlobalUsingFixture");

        var removedUsing = Assert.Single(report.RemovedUsings);
        Assert.Equal(UsingRemovalReason.OrphanedGlobalNamespace, removedUsing.Reason);
        Assert.Contains("global using", removedUsing.Directive);
    }

    [Fact]
    public async Task Global_using_referenced_in_other_file_is_kept()
    {
        // Regression: a `global using NS;` is project-scoped, so it must be kept
        // when ANY document in the project references a type from NS — even if
        // the file that declares the using references nothing from NS. The
        // document-scoped check wrongly removed `global using VB;` here because
        // Usings.cs has no VB symbols, breaking __AppObjects.cs which uses
        // VB.Global.
        var (report, rewritten) = await RunAsync("GlobalUsingUsedInOtherFileFixture", apply: true);

        Assert.Empty(report.RemovedUsings);

        // Usings.cs must be unchanged (not in the rewritten set) — or, if it
        // appears, it must still carry the global using.
        if (rewritten.TryGetValue("Usings.cs", out var usingsText))
        {
            Assert.Contains("global using VB;", usingsText);
        }
    }

    [Fact]
    public async Task Types_filter_restricts_candidates()
    {
        var (report, _) = await RunAsync("FilterFixture", types: new[] { "FilterFixture.KeepMe" });

        // Only KeepMe is a candidate; DropMe is not pruned.
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "FilterFixture.KeepMe");
        Assert.DoesNotContain(report.RemovedTypes, t => t.FullyQualifiedName == "FilterFixture.DropMe");
    }

    [Fact]
    public async Task Apply_writes_rewritten_files()
    {
        var (report, rewritten) = await RunAsync("UnusedMemberFixture", apply: true);

        Assert.True(report.Applied);
        Assert.True(report.Summary.WrittenFileCount > 0);
        Assert.True(rewritten.Count > 0);

        // The rewritten Service.cs must no longer contain the unused method.
        var service = rewritten["Service.cs"];
        Assert.DoesNotContain("public void Unused()", service);
        Assert.Contains("public void Used()", service);
    }

    [Fact]
    public async Task Dry_run_does_not_write_files()
    {
        var (report, rewritten) = await RunAsync("UnusedMemberFixture", apply: false);

        Assert.False(report.Applied);
        Assert.Equal(0, report.Summary.WrittenFileCount);
        Assert.Empty(rewritten);
    }

    [Fact]
    public async Task File_with_file_scoped_namespace_becoming_empty_is_removed()
    {
        // Regression: when the only type in a file is pruned, the file must be
        // removed entirely rather than left with just "namespace Foo;".
        var (report, rewritten) = await RunAsync("EmptyFileFixture", apply: true);

        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "EmptyFileFixture.OnlyTypeInFile");
        // OnlyType.cs should be deleted (not present in rewritten files, which
        // only contain files that were modified and still exist on disk).
        Assert.DoesNotContain(rewritten.Keys, k => k == "OnlyType.cs");
    }

    [Fact]
    public async Task File_with_block_namespace_becoming_empty_is_removed()
    {
        // Same as above but with a regular (block-scoped) namespace.
        var (report, rewritten) = await RunAsync("EmptyBlockNsFixture", apply: true);

        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "EmptyBlockNsFixture.OnlyTypeInFile");
        Assert.DoesNotContain(rewritten.Keys, k => k == "OnlyType.cs");
    }

    [Fact]
    public async Task Extension_block_class_with_referenced_members_is_kept()
    {
        // Regression: a static class hosting C# 14 extension methods was pruned
        // even when its extension members were referenced, because call sites
        // write `receiver.Member` (resolving to a reduced extension method on
        // the static class) and never name the type itself. The whole-type
        // reference check found zero references and removed the entire class,
        // tearing the extension block out and breaking the call sites.
        var (report, rewritten) = await RunAsync("ExtensionBlockFixture", apply: true);

        // The static class and its extension block must be KEPT.
        Assert.DoesNotContain(report.RemovedTypes, t => t.FullyQualifiedName == "ExtensionBlockFixture._VB6Extensions");
        // The synthetic extension container type (empty name) must NOT be
        // reported as a separate removable type.
        Assert.DoesNotContain(report.RemovedTypes, t => t.FullyQualifiedName.EndsWith("._VB6Extensions."));

        // The referenced extension member _Version must NOT be pruned.
        Assert.DoesNotContain(report.RemovedMembers, m => m.FullyQualifiedName.Contains("_Version"));

        // The genuinely unused members ARE pruned, individually — proving the
        // fixpoint still converges and does not over-retain.
        Assert.Contains(report.RemovedMembers, m => m.FullyQualifiedName.Contains("_UnusedExtensionMember"));
        Assert.Contains(report.RemovedMembers, m => m.FullyQualifiedName.Contains("UnusedRegularMethod"));

        // The extension block survives in the rewritten file.
        var extensions = rewritten["Extensions.cs"];
        Assert.Contains("extension(IComStub stub)", extensions);
        Assert.Contains("public int _Version", extensions);
        Assert.DoesNotContain("_UnusedExtensionMember", extensions);
        Assert.DoesNotContain("UnusedRegularMethod", extensions);

        // Must terminate (not loop forever).
        Assert.True(report.Summary.Passes < 10, "Fixpoint should converge quickly.");
    }

    [Fact]
    public async Task Type_referencing_itself_in_base_list_is_pruned()
    {
        // Regression: a type that names itself in its own base list or type
        // arguments (e.g. `public class ImageMgr : IControlStub<ImageMgr> { }`)
        // was never pruned, because SymbolFinder.FindReferencesAsync reports the
        // self-reference in the type arguments and the analyzer counted it as
        // external usage. A reference located within the symbol's own
        // declaration is a self-reference, not usage, and must be ignored.
        var (report, _) = await RunAsync("SelfReferenceFixture");

        // ImageMgr is only "referenced" by itself (in IControlStub<ImageMgr>);
        // it must be pruned.
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "SelfReferenceFixture.ImageMgr");
        // Derived is only "referenced" by itself (in Base<Derived>); it must be
        // pruned.
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "SelfReferenceFixture.Derived");
        // IControlStub is only referenced by the now-pruned ImageMgr; it must
        // cascade-prune in a later pass (proving the fixpoint still converges).
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "SelfReferenceFixture.IControlStub");
        Assert.Contains(report.RemovedTypes, t => t.FullyQualifiedName == "SelfReferenceFixture.Base");
        // UsedType is referenced from Program.Main and must be kept.
        Assert.DoesNotContain(report.RemovedTypes, t => t.FullyQualifiedName.Contains("UsedType"));
        // Must terminate (not loop forever).
        Assert.True(report.Summary.Passes < 10, "Fixpoint should converge quickly.");
    }
}
