using System.Collections.ObjectModel;

namespace AzureTray.ViewModels;

public sealed class TestGroupViewModel
{
    public TestGroupViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();
}
