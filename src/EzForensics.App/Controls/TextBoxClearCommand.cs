using System.Windows.Controls;
using System.Windows.Input;

namespace EzForensics.App.Controls;

/// <summary>Command used by the ClearableTextBox style: clears the TextBox passed as parameter and keeps focus in it.</summary>
public sealed class TextBoxClearCommand : ICommand
{
    public static readonly TextBoxClearCommand Instance = new();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is TextBox;
    public void Execute(object? parameter)
    {
        if (parameter is not TextBox tb) return;
        tb.Clear();
        tb.Focus();
    }
}
