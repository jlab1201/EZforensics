using System.Windows.Controls;

namespace EzForensics.App.Controls;

/// <summary>Three-state check that cycles all (null) → checked → unchecked → all.</summary>
public sealed class CycleCheckBox : CheckBox
{
    public CycleCheckBox() => IsThreeState = true;

    protected override void OnToggle() =>
        SetCurrentValue(IsCheckedProperty, IsChecked switch { null => true, true => false, _ => (bool?)null });
}
