using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Per-window VM. Renders the supplied NotificationRequest and resolves the
// supplied TaskCompletionSource on user input or dismissal. The instance is
// single-use — once a result is produced, subsequent inputs are ignored.
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly TaskCompletionSource<NotificationResult> _completion;
    private bool _completed;

    public NotificationRequest Request { get; }

    public string Title => Request.Title;
    public string Message => Request.Message;

    public bool IsYesNo => Request is YesNoRequest;
    public bool IsChoice => Request is ChoiceRequest;
    public bool IsTextInput => Request is TextInputRequest;
    public bool IsInformation => Request is InformationRequest;
    public bool IsAction => Request is ActionRequest;
    public bool IsSubmittable => IsChoice || IsTextInput;

    // Resolved label for the ActionRequest primary button. Null for any
    // other request type so the XAML binding stays well-defined.
    public string? ActionLabel => (Request as ActionRequest)?.ActionLabel;

    // Drives the severity-tinted accent stripe at the top of the
    // notification card. Mapped to brushes in NotificationWindow.xaml.
    public NotificationSeverity Severity => Request.Severity;

    public IReadOnlyList<string>? Choices { get; }
    public bool AllowOther { get; }
    public string? Placeholder { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string? _selectedChoice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isOtherSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _otherText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _inputText = string.Empty;

    public NotificationViewModel(
        NotificationRequest request,
        TaskCompletionSource<NotificationResult> completion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completion);

        Request = request;
        _completion = completion;

        if (request is ChoiceRequest choice)
        {
            Choices = choice.Choices;
            AllowOther = choice.AllowOther;
        }
        if (request is TextInputRequest text)
        {
            Placeholder = text.Placeholder;
            InputText = text.InitialValue ?? string.Empty;
        }
    }

    [RelayCommand]
    private void Yes() => Complete(new YesNoResult(true));

    [RelayCommand]
    private void No() => Complete(new YesNoResult(false));

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        switch (Request)
        {
            case ChoiceRequest:
                if (IsOtherSelected)
                {
                    Complete(new ChoiceResult(SelectedChoice: null, OtherText: OtherText));
                }
                else
                {
                    Complete(new ChoiceResult(SelectedChoice: SelectedChoice, OtherText: null));
                }
                break;
            case TextInputRequest:
                Complete(new TextInputResult(InputText));
                break;
        }
    }

    [RelayCommand]
    private void InvokeAction() => Complete(new ActionResult(ActionInvoked: true));

    [RelayCommand]
    private void Dismiss() => Complete(new DismissedResult());

    // Called by the window when it closes without a result already set
    // (user pressed X, Alt+F4, etc).
    public void OnWindowClosed() => Complete(new DismissedResult());

    // Raised when the VM has resolved its result (Yes/No/Submit/Dismiss).
    // The hosting window subscribes to close itself — without this, clicking
    // Submit / Yes / No leaves the dialog on screen even though the awaiter
    // already got its result.
    public event Action? Completed;

    private void Complete(NotificationResult result)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(result);
        Completed?.Invoke();
    }

    private bool CanSubmit()
    {
        return Request switch
        {
            ChoiceRequest =>
                (IsOtherSelected && !string.IsNullOrWhiteSpace(OtherText)) ||
                (!IsOtherSelected && SelectedChoice is not null),
            TextInputRequest => !string.IsNullOrWhiteSpace(InputText),
            _ => false,
        };
    }

    // Selecting a list choice deselects the "Other" radio (and vice versa).
    partial void OnSelectedChoiceChanged(string? value)
    {
        if (value is not null && IsOtherSelected)
        {
            IsOtherSelected = false;
        }
    }

    partial void OnIsOtherSelectedChanged(bool value)
    {
        if (value && SelectedChoice is not null)
        {
            SelectedChoice = null;
        }
    }
}
