using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureTray.Testing;

namespace AzureTray.ViewModels;

public sealed partial class TestRunnerViewModel : ObservableObject
{
    private readonly ITestRegistry _registry;

    public TestRunnerViewModel(ITestRegistry registry)
    {
        _registry = registry;
        Refresh();
    }

    public ObservableCollection<TestGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAllCommand))]
    private bool _isRunningAll;

    [RelayCommand]
    private void Refresh()
    {
        Groups.Clear();
        foreach (var g in _registry.GetGroups())
        {
            var groupVm = new TestGroupViewModel(g.Name);
            foreach (var t in g.Tests)
            {
                groupVm.Tests.Add(new TestItemViewModel(t));
            }
            Groups.Add(groupVm);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAll))]
    private async Task RunAllAsync()
    {
        IsRunningAll = true;
        try
        {
            foreach (var group in Groups)
            {
                foreach (var test in group.Tests)
                {
                    await test.RunAsync().ConfigureAwait(true);
                }
            }
        }
        finally
        {
            IsRunningAll = false;
        }
    }

    private bool CanRunAll() => !IsRunningAll;
}
