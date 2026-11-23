using System.Windows.Controls;

namespace EzForensics.App.Views.Jobs;

/// <summary>Status / reason, meta, command and log of the selected job (DataContext: JobListViewModel).</summary>
public partial class JobDetailsPane : UserControl
{
    public JobDetailsPane() => InitializeComponent();
}
