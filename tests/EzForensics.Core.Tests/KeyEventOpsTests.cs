using EzForensics.Core.Analysis;

namespace EzForensics.Core.Tests;

public class KeyEventOpsTests
{
    private static DateTimeOffset D(int h) => new(2026, 11, 1, h, 0, 0, TimeSpan.Zero);

    private static AnalysisItem F(CaseAnalysis a, string id, string title, string sev, DateTimeOffset? first, params string[] mitre)
    {
        var i = new AnalysisItem { Id = id, Title = title, Severity = sev, FirstSeen = first, Mitre = mitre.ToList() };
        a.Items.Add(i);
        return i;
    }

    [Fact]
    public void AddToTimeline_OneFinding_DefaultsFromFinding()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "Lateral move", "High", D(10), "T1021");
        var k = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        Assert.Equal(D(10), k.Time);
        Assert.Equal("High", k.Severity);
        Assert.Equal("Lateral move", k.Title);
        Assert.Equal(new[] { "I-1" }, k.FindingIds);
    }

    [Fact]
    public void AddToTimeline_Twice_ReturnsSameKeyEvent()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "x", "Low", D(10));
        var k1 = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        var k2 = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        Assert.Same(k1, k2);
        Assert.Single(a.KeyEvents);
    }

    [Fact]
    public void Group_TwoFindings_EarliestTime_HighestSeverity_UnionMitre()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", D(10), "T1021");
        F(a, "I-2", "B", "Medium", D(9), "T1021", "T1078");
        var k = KeyEventOps.Group(a, new[] { "I-1", "I-2" }, AT.Now);
        var e = KeyEventOps.Effective(a, k);
        Assert.Equal(D(9), e.Time);
        Assert.Equal("High", e.Severity);
        Assert.Equal(new[] { "T1021", "T1078" }, e.Mitre);
        Assert.Equal("A", e.Title);
    }

    [Fact]
    public void Group_MovesFindingFromOtherKeyEvent_AndDismissesEmptyOne()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", D(10));
        F(a, "I-2", "B", "Low", D(11));
        var k1 = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        var k2 = KeyEventOps.Group(a, new[] { "I-1", "I-2" }, AT.Now);
        Assert.True(k1.Dismissed);
        Assert.Empty(k1.FindingIds);
        Assert.NotEmpty(k1.History);
        Assert.Same(k2, KeyEventOps.KeyEventOf(a, "I-1"));
        Assert.Equal(2, k2.FindingIds.Count);
    }

    [Fact]
    public void EditedTime_WinsOverDefault()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", D(10));
        var k = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        KeyEventEdits.SetTime(k, D(15), AT.Now);
        Assert.Equal(D(15), KeyEventOps.Effective(a, k).Time);
        Assert.True(k.IsEdited(KeyEventFields.Time));
        Assert.Equal("High", KeyEventOps.Effective(a, k).Severity);
    }

    [Fact]
    public void RemoveFinding_Last_DismissesKeyEvent()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", D(10));
        var k = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        Assert.True(KeyEventOps.RemoveFinding(a, k.Id, "I-1", AT.Now));
        Assert.True(k.Dismissed);
        Assert.True(k.IsEdited(KeyEventFields.Findings));
    }

    [Fact]
    public void Effective_NoTimes_TimeIsNull()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", null);
        var k = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        Assert.Null(KeyEventOps.Effective(a, k).Time);
    }

    [Fact]
    public void KeyEventOf_IgnoresDismissed()
    {
        var a = new CaseAnalysis();
        F(a, "I-1", "A", "High", D(10));
        var k = KeyEventOps.AddToTimeline(a, "I-1", AT.Now);
        KeyEventEdits.Remove(k, AT.Now);
        Assert.Null(KeyEventOps.KeyEventOf(a, "I-1"));
    }
}
