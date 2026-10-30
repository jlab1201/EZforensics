using EzForensics.App.Views;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.Services;

/// <summary>"Event logs already parsed" question shown before EvtxECmd runs (decision logic: <see cref="EvtxOutputPolicy"/>).</summary>
public static class EvtxRerunPrompt
{
    public static EvtxRerunChoice Ask(IReadOnlyList<string> existing, int runs)
    {
        var names = string.Join("\n", existing.Take(6).Select(f => "  " + f)) + (existing.Count > 6 ? $"\n  … and {existing.Count - 6} more" : "");
        var r = Dialogs.Show("Event logs already parsed", "This host already has event log output.", EzDialogSeverity.Question,
            names, new[] { "Replace", "Keep both", "Cancel" }, primary: 0, cancel: 2);
        return r switch { 0 => EvtxRerunChoice.Replace, 1 => EvtxRerunChoice.CreateNew, _ => EvtxRerunChoice.Cancel };
    }
}
