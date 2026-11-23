using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels;

/// <summary>One Explore tool on the Tools page: DIE, ExifTool or CyberChef (per-user download, no admin).</summary>
public sealed partial class ExploreToolRow : ObservableObject
{
    private readonly ExploreToolLocator _locator;
    private readonly Action? _changed;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _actionLabel = "Install";

    public ExploreToolRow(ExploreToolSpec spec, string description, ExploreToolLocator locator, Action? changed = null)
    {
        Spec = spec;
        Description = description;
        _locator = locator;
        _changed = changed;
        Refresh();
    }

    public ExploreToolSpec Spec { get; }
    public string Name => Spec.Name;
    public string Description { get; }
    public string License => Spec.License;
    public string AutomationKey => Spec.Tool.ToString();

    public void Refresh()
    {
        var i = _locator.Get(Spec.Tool);
        IsInstalled = i.IsInstalled;
        Status = !i.IsInstalled ? "Not installed" : i.IsCurrent ? $"Installed, version {i.Version ?? Spec.Version}" : $"Installed, version {i.Version ?? "unknown"} (update to {Spec.Version} available)";
        ActionLabel = !i.IsInstalled ? "Install" : i.IsCurrent ? "Reinstall" : "Update";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        IsBusy = true; Message = ""; Progress = 0;
        InstallCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged();
        try
        {
            using var http = AppServices.Current.CreateHttpClient();
            await ExploreToolSetup.InstallAsync(http, Spec, _locator, null, new Progress<double>(p => Progress = p)).ConfigureAwait(true);
            Message = $"{Spec.Name} {Spec.Version} installed.";
        }
        catch (Exception ex) { Message = $"Could not install {Spec.Name}: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            Refresh();
            InstallCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged();
            _changed?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        Message = ExploreToolSetup.Uninstall(_locator, Spec.Tool) ? $"{Spec.Name} removed." : $"{Spec.Name} could not be fully removed (a file may be in use).";
        Refresh();
        RemoveCommand.NotifyCanExecuteChanged();
        _changed?.Invoke();
    }

    private bool CanRun() => !IsBusy;
    private bool CanRemove() => !IsBusy && IsInstalled;
    partial void OnIsInstalledChanged(bool value) => RemoveCommand.NotifyCanExecuteChanged();
}

/// <summary>The "Explore tools" card of the Tools page.</summary>
public sealed class ExploreToolsViewModel
{
    public ExploreToolsViewModel(Action? changed = null)
    {
        var loc = new ExploreToolLocator();
        Rows.Add(new ExploreToolRow(ExploreToolReleases.Die, "Detect It Easy: identifies the file type, packer and compiler.", loc, changed));
        Rows.Add(new ExploreToolRow(ExploreToolReleases.ExifTool, "ExifTool: reads file metadata (documents, images, media, executables).", loc, changed));
        Rows.Add(new ExploreToolRow(ExploreToolReleases.CyberChef, "CyberChef: decodes and transforms data. Runs offline inside EZ Forensics.", loc, changed));
    }

    public ObservableCollection<ExploreToolRow> Rows { get; } = new();
    public bool AllInstalled => Rows.All(r => r.IsInstalled);
    public void Refresh() { foreach (var r in Rows) r.Refresh(); }
}
