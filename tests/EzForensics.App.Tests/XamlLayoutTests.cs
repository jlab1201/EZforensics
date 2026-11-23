using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace EzForensics.App.Tests;

/// <summary>Static checks over every .xaml in the app: Grid.Row/Column out of range, and unintended overlaps.</summary>
public class XamlLayoutTests
{
    private readonly ITestOutputHelper _out;
    public XamlLayoutTests(ITestOutputHelper output) => _out = output;

    // Intentional overlays, as "relative\file.xaml:line" of the Grid element. Add a reason per entry.
    private static readonly HashSet<string> OverlayAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "EzForensics.App")))
            dir = dir.Parent;
        dir.Should().NotBeNull("repo root with src/EzForensics.App must be above the test output");
        return Path.Combine(dir!.FullName, "src", "EzForensics.App");
    }

    private static IEnumerable<(string File, XDocument Doc)> AllXaml()
    {
        var root = SourceRoot();
        var sep = Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}bin{sep}")) continue;
            XDocument doc;
            try { doc = XDocument.Load(f, LoadOptions.SetLineInfo); } catch { continue; }
            yield return (Path.GetRelativePath(root, f), doc);
        }
    }

    private static bool IsGrid(XElement e) => e.Name.LocalName == "Grid";
    private static bool IsPropertyElement(XElement e) => e.Name.LocalName.Contains('.');
    private static int Line(XElement e) => ((IXmlLineInfo)e).LineNumber;
    private static string Where(string file, XElement e) => $"{file}:{Line(e)}";

    private static int? Literal(XElement e, string attr)
    {
        var a = e.Attribute(attr);
        if (a == null) return 0;                                    // default
        return int.TryParse(a.Value.Trim(), out var n) ? n : null;  // binding/expression -> unknown
    }

    /// <summary>Number of definitions (0 counts as 1), or null when the grid gets them from a style/code.</summary>
    private static int? DefCount(XElement grid, string propName)
    {
        var def = grid.Elements().FirstOrDefault(c => c.Name.LocalName == $"Grid.{propName}");
        if (def != null) return Math.Max(1, def.Elements().Count());
        if (grid.Attribute("Style") != null) return null;
        return 1;
    }

    [Fact]
    public void GridChildren_StayWithinDefinedRowsAndColumns()
    {
        var problems = new List<string>();
        foreach (var (file, doc) in AllXaml())
            foreach (var grid in doc.Descendants().Where(IsGrid))
            {
                var rows = DefCount(grid, "RowDefinitions");
                var cols = DefCount(grid, "ColumnDefinitions");
                foreach (var child in grid.Elements().Where(c => !IsPropertyElement(c)))
                {
                    var r = Literal(child, "Grid.Row");
                    var c = Literal(child, "Grid.Column");
                    if (rows != null && r != null && r >= rows)
                        problems.Add($"{Where(file, child)} <{child.Name.LocalName}> Grid.Row={r} but grid at line {Line(grid)} has {rows} row(s)");
                    if (cols != null && c != null && c >= cols)
                        problems.Add($"{Where(file, child)} <{child.Name.LocalName}> Grid.Column={c} but grid at line {Line(grid)} has {cols} column(s)");
                }
            }
        problems.ForEach(_out.WriteLine);
        problems.Should().BeEmpty();
    }

    [Fact]
    public void GridChildren_DoNotUnintentionallyShareACell()
    {
        var problems = new List<string>();
        foreach (var (file, doc) in AllXaml())
            foreach (var grid in doc.Descendants().Where(IsGrid))
            {
                if (OverlayAllowList.Contains(Where(file, grid))) continue;
                var groups = grid.Elements().Where(c => !IsPropertyElement(c))
                    .Where(c => c.Name.LocalName is not ("GridSplitter" or "Thumb")) // column resize grips overlay their header by design
                    .Select(c => (El: c, R: Literal(c, "Grid.Row"), C: Literal(c, "Grid.Column")))
                    .Where(t => t.R != null && t.C != null)
                    .GroupBy(t => (t.R, t.C)).Where(g => g.Count() > 1);
                foreach (var g in groups)
                {
                    var els = g.Select(t => t.El).ToList();
                    if (els.Any(e => e.Attribute("Visibility") != null)) continue;
                    if (els.Any(e => e.Attribute("Panel.ZIndex") != null)) continue;
                    if (els.Any(e => e.Attribute("IsHitTestVisible")?.Value == "False")) continue; // overlay by design
                    var aligns = els.Select(e => (e.Attribute("HorizontalAlignment")?.Value ?? "", e.Attribute("VerticalAlignment")?.Value ?? "")).Distinct().Count();
                    if (aligns > 1) continue;
                    // background layer: a childless Border/Rectangle shape
                    if (els.Any(e => e.Name.LocalName is "Rectangle" or "Border" or "Ellipse" or "Path" && !e.Elements().Any(x => !IsPropertyElement(x)))) continue;
                    // elements whose visibility is toggled by a style trigger
                    if (els.Any(e => e.Descendants().Any(d => d.Name.LocalName == "DataTrigger" || d.Name.LocalName == "Trigger"))) continue;
                    problems.Add($"{Where(file, grid)} cell ({g.Key.R},{g.Key.C}): " +
                        string.Join(", ", els.Select(e => $"<{e.Name.LocalName}>@{Line(e)}")));
                }
            }
        problems.ForEach(_out.WriteLine);
        problems.Should().BeEmpty();
    }
}
