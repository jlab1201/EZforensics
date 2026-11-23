using System.Globalization;
using System.Windows.Data;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Views.Findings;

/// <summary>The Details lane choice in plain words: Tactic (MITRE ATT&CK), Host, Source (artifact type).</summary>
public sealed class LaneModeNameConverter : IValueConverter
{
    public static string Name(TimelineLaneMode mode) => mode switch
    {
        TimelineLaneMode.Tactic => "Tactic",
        TimelineLaneMode.Host => "Host",
        _ => "Source",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is TimelineLaneMode m ? Name(m) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
