using System.Diagnostics;
using EzForensics.Core.Explore;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ExploreSourcesTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Hiding_the_case_folder_persists_and_ShowCase_restores_it()
    {
        var caseDir = Path.Combine(_tmp.Path, "CaseH");
        Directory.CreateDirectory(caseDir);
        var ev = Path.Combine(_tmp.Path, "evh");
        Directory.CreateDirectory(ev);

        var s = ExploreSources.Load(caseDir);
        s.Add(ev);
        s.Remove(ExploreSources.CaseSourceId).Should().BeTrue();
        s.All.Should().ContainSingle().Which.Kind.Should().Be(ExploreSourceKind.Folder);

        var again = ExploreSources.Load(caseDir);
        again.CaseHidden.Should().BeTrue();
        again.All.Should().ContainSingle().Which.Kind.Should().Be(ExploreSourceKind.Folder);

        again.ShowCase();
        var third = ExploreSources.Load(caseDir);
        third.CaseHidden.Should().BeFalse();
        third.All.Should().HaveCount(2);
        third.All[0].IsCase.Should().BeTrue();
    }

    [Fact]
    public void Adding_the_case_folder_path_shows_a_hidden_case_folder_without_a_duplicate()
    {
        var caseDir = Path.Combine(_tmp.Path, "CaseAdd");
        Directory.CreateDirectory(caseDir);
        var s = ExploreSources.Load(caseDir);
        s.Remove(ExploreSources.CaseSourceId).Should().BeTrue();
        s.CaseHidden.Should().BeTrue();

        var back = s.Add(caseDir + Path.DirectorySeparatorChar);
        back.IsCase.Should().BeTrue();
        s.CaseHidden.Should().BeFalse();
        s.All.Should().ContainSingle().Which.IsCase.Should().BeTrue();
        ExploreSources.Load(caseDir).CaseHidden.Should().BeFalse();
    }

    [Fact]
    public void Case_folder_is_always_first_and_added_sources_persist()
    {
        var caseDir = Path.Combine(_tmp.Path, "Case1");
        Directory.CreateDirectory(caseDir);
        var ev = Path.Combine(_tmp.Path, "evidence");
        Directory.CreateDirectory(ev);
        var file = Path.Combine(_tmp.Path, "one.bin");
        File.WriteAllText(file, "x");

        var s = ExploreSources.Load(caseDir);
        s.All.Should().ContainSingle().Which.Kind.Should().Be(ExploreSourceKind.Case);
        var a = s.Add(ev);
        a.Kind.Should().Be(ExploreSourceKind.Folder);
        a.Label.Should().Be("evidence");
        s.Add(file).Kind.Should().Be(ExploreSourceKind.File);
        s.Add(ev + "\\").Should().Be(a);                 // duplicate
        s.Add(caseDir).IsCase.Should().BeTrue();          // the case folder is never added twice
        File.Exists(ExplorePaths.SourcesFile(caseDir)).Should().BeTrue();

        var again = ExploreSources.Load(caseDir);
        again.All.Select(x => x.Kind).Should().Equal(ExploreSourceKind.Case, ExploreSourceKind.Folder, ExploreSourceKind.File);
        again.Owner(Path.Combine(ev, "sub", "x.txt"))!.Id.Should().Be(a.Id);
        again.Owner(Path.Combine(caseDir, "Host", "x.csv"))!.IsCase.Should().BeTrue();
        again.Remove(a.Id).Should().BeTrue();
        again.Remove("no-such-id").Should().BeFalse();
        ExploreSources.Load(caseDir).Added.Should().ContainSingle();

        ((Action)(() => again.Add(Path.Combine(_tmp.Path, "missing")))).Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Corrupt_sources_file_gives_just_the_case()
    {
        var caseDir = Path.Combine(_tmp.Path, "Case2");
        Directory.CreateDirectory(ExplorePaths.Root(caseDir));
        File.WriteAllText(ExplorePaths.SourcesFile(caseDir), "{ not json");
        ExploreSources.Load(caseDir).All.Should().ContainSingle();
    }

    [Fact]
    public void Enumerator_lists_hidden_files_excludes_names_and_pages()
    {
        var d = Path.Combine(_tmp.Path, "list");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        Directory.CreateDirectory(Path.Combine(d, "_explore"));
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(d, $"f{i}.txt"), new string('a', i));
        var hidden = Path.Combine(d, "h.dat");
        File.WriteAllText(hidden, "h");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var opts = new FolderEnumerationOptions { ExcludeNames = new[] { "_explore" } };
        var all = FolderEnumerator.Enumerate(d, opts).ToList();
        all.Should().HaveCount(7);
        all.Should().Contain(e => e.Name == "h.dat" && e.IsHidden && e.IsSystem);
        all.Single(e => e.Name == "sub").IsDirectory.Should().BeTrue();
        all.Single(e => e.Name == "f3.txt").Length.Should().Be(3);

        var p1 = FolderEnumerator.Page(d, 0, 4, opts);
        p1.Entries.Should().HaveCount(4);
        p1.HasMore.Should().BeTrue();
        var p2 = FolderEnumerator.Page(d, 4, 4, opts);
        p2.Entries.Should().HaveCount(3);
        p2.HasMore.Should().BeFalse();
        p1.Entries.Concat(p2.Entries).Select(e => e.Name).Should().OnlyHaveUniqueItems();
        FolderEnumerator.EnumeratePages(d, 3, opts).Select(p => p.Count).Should().Equal(3, 3, 1);
        File.SetAttributes(hidden, FileAttributes.Normal);
    }

    [Fact]
    public void Missing_folder_yields_empty_with_error()
    {
        var p = FolderEnumerator.Page(Path.Combine(_tmp.Path, "nope"), 0, 10);
        p.Entries.Should().BeEmpty();
        p.Error.Should().NotBeNull();
        FolderEnumerator.Enumerate(Path.Combine(_tmp.Path, "nope")).Should().BeEmpty();
    }

    [Fact]
    public void Enumerator_lists_reparse_points_but_never_follows_folder_links()
    {
        var d = Path.Combine(_tmp.Path, "rp");
        var target = Path.Combine(_tmp.Path, "target");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");
        File.WriteAllText(Path.Combine(d, "plain.txt"), "x");
        var junction = Path.Combine(d, "jn");
        if (!MakeJunction(junction, target)) return;   // no junction support here: nothing to test
        try
        {
            FolderEnumerator.Enumerate(d).Select(e => e.Name).Should().BeEquivalentTo("plain.txt", "jn");
            FolderEnumerator.Enumerate(d, new FolderEnumerationOptions { IncludeReparsePoints = false }).Select(e => e.Name).Should().Equal("plain.txt");
            var withRp = FolderEnumerator.Enumerate(d, new FolderEnumerationOptions { IncludeReparsePoints = true }).ToList();
            withRp.Single(e => e.Name == "jn").IsReparsePoint.Should().BeTrue();
            var page = FolderEnumerator.Page(junction, 0, 10);
            page.Entries.Should().BeEmpty();
            page.Error.Should().Contain("reparse point");
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void Enumerator_lists_every_child_including_hidden_and_file_symlinks()
    {
        var d = Path.Combine(_tmp.Path, "all");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(d, "a.txt"), "x");
        var hidden = Path.Combine(d, "h.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var link = Path.Combine(d, "link.txt");
        try { File.CreateSymbolicLink(link, Path.Combine(d, "a.txt")); } catch (Exception) { /* no symlink privilege */ }
        var expected = Directory.GetFileSystemEntries(d).Select(Path.GetFileName).ToList();
        FolderEnumerator.Enumerate(d).Select(e => e.Name).Should().BeEquivalentTo(expected);
        FolderEnumerator.Page(d, 0, 2).HasMore.Should().Be(expected.Count > 2);
    }

    private static bool MakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return Directory.Exists(link);
        }
        catch { return false; }
    }
}
