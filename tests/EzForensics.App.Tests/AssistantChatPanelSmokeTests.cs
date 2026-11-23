using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Assistant;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The chat panel parses, binds and lays out with every kind of message and a proposal card, without binding errors.</summary>
[Collection("WpfApp")]
public sealed class AssistantChatPanelSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    [Fact]
    public void Panel_binds_and_shows_messages_and_a_proposal_card()
    {
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var vm = new AssistantChatViewModel(new AppSettings { AiModel = "qwen3:8b" }, new ApiKeyStore(Path.Combine(Path.GetTempPath(), "ezchat-smoke-keys")),
                    () => new System.Net.Http.HttpClient(), () => null, null, null, (_, _) => false, a => a(), (_, _) => Task.FromResult<AssistantExtraTools?>(null));
                vm.Messages.Add(new ChatMessage(ChatRole.You, "which logons look odd?"));
                vm.Messages.Add(new ChatMessage(ChatRole.Assistant, "Three logons from a new host."));
                vm.Messages.Add(new ChatMessage(ChatRole.Status, "Stopped."));
                vm.Messages.Add(new ChatMessage(ChatRole.Assistant, "", new ProposalCardViewModel(new FlagProposal("c", @"C:\x.csv", new long[] { 1, 2 }, "red", "odd source"), null)));
                var panel = new AssistantChatPanel { DataContext = vm };
                panel.Measure(new Size(360, 700)); panel.Arrange(new Rect(0, 0, 360, 700)); panel.UpdateLayout();

                var ids = Descendants(panel).OfType<FrameworkElement>().Select(AutomationProperties.GetAutomationId).Where(s => s.Length > 0).ToHashSet();
                ids.Should().Contain(new[] { "Assistant.Input", "Assistant.Send", "Assistant.NewChat", "Assistant.Messages", "Assistant.Apply", "Assistant.Dismiss", "Assistant.Model", "Assistant.Mode", "Assistant.Minimize" });
                vm.ModelLabel.Should().Be("Local · qwen3:8b");
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("Assistant panel smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }
}
