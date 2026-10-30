using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace EzForensics.App.Controls;

/// <summary>A Border that shows up in UI Automation as a Pane (plain Borders have no automation peer).</summary>
public sealed class AutomationPane : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer
    {
        public Peer(AutomationPane owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(AutomationPane);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => false;
    }
}
