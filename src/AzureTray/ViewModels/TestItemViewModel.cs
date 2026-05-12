using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureTray.Plugin.Contracts;

namespace AzureTray.ViewModels;

public enum TestStatus { NotRun, Running, Passed, Failed, Canceled }

// One row in the Test Runner. Wraps a PluginTest so the host can drive the
// underlying delegate while binding UI state for the user.
public sealed partial class TestItemViewModel : ObservableObject, IDisposable
{
    private readonly PluginTest _test;
    private CancellationTokenSource? _cts;

    public TestItemViewModel(PluginTest test)
    {
        _test = test;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public string Name => _test.Name;
    public string? Description => _test.Description;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private TestStatus _status = TestStatus.NotRun;

    [ObservableProperty]
    private string? _message;

    public bool IsRunning => Status == TestStatus.Running;

    public string StatusGlyph => Status switch
    {
        TestStatus.Running => "…",
        TestStatus.Passed => "✓",
        TestStatus.Failed => "✕",
        TestStatus.Canceled => "⊘",
        _ => "—",
    };

    partial void OnStatusChanged(TestStatus value) => OnPropertyChanged(nameof(StatusGlyph));

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        // Replace any existing token source. Should only happen if a prior
        // run completed without disposing — defensive.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Status = TestStatus.Running;
        Message = null;
        try
        {
            var result = await _test.Run(_cts.Token).ConfigureAwait(true);
            Status = result.Passed ? TestStatus.Passed : TestStatus.Failed;
            Message = result.Message;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Status = TestStatus.Canceled;
            Message = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = TestStatus.Failed;
            Message = $"Threw {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => Status != TestStatus.Running;
    private bool CanCancel() => Status == TestStatus.Running;
}
