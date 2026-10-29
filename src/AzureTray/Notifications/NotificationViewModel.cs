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
    public bool IsDuration => Request is DurationRequest;
    public bool IsSubmittable => IsChoice || IsTextInput || IsDuration;

    // Resolved label for the ActionRequest primary button. Null for any
    // other request type so the XAML binding stays well-defined.
    public string? ActionLabel => (Request as ActionRequest)?.ActionLabel;

    // Drives the severity-tinted accent stripe at the top of the
    // notification card. Mapped to brushes in NotificationWindow.xaml.
    public NotificationSeverity Severity => Request.Severity;

    // Glyph rendered next to the title, tinted with the same severity brush.
    // Plain Unicode keeps the binding font-agnostic — Segoe UI on Win10/11
    // renders all glyphs cleanly without Segoe MDL2 Assets.
    public string SeverityIcon => Request.Severity switch
    {
        NotificationSeverity.Success => "✓",   // ✓
        NotificationSeverity.Error => "✕",     // ✕
        NotificationSeverity.Warning => "⚠",   // ⚠
        NotificationSeverity.Update => "⬆",    // ⬆
        _ => "ⓘ",                              // ⓘ (Info default)
    };

    // Pass-through of NotificationRequest.Details. The XAML renders these
    // inside a collapsed Expander beneath Message so verbose error context
    // doesn't bloat the default toast size.
    public IReadOnlyList<NotificationDetail>? Details => Request.Details;
    public bool HasDetails => Request.Details is { Count: > 0 };

    public IReadOnlyList<string>? Choices { get; }
    public bool AllowOther { get; }
    public string? Placeholder { get; }

    // Slider geometry for DurationRequest, in whole minutes — WPF's Slider
    // binds doubles, and minutes keep a 30-minute Step an exact integer
    // (ticks-as-double would lose snapping to rounding).
    public double SliderMinimum { get; }
    public double SliderMaximum { get; }
    public double SliderStep { get; }

    // End-of-track captions under the slider.
    public string SliderMinimumLabel => FormatDuration(TimeSpan.FromMinutes(SliderMinimum));
    public string SliderMaximumLabel => FormatDuration(TimeSpan.FromMinutes(SliderMaximum));

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

    // The slider thumb's position, in minutes. The window binds the Slider's
    // Value here; SelectedDurationLabel re-renders on every move so the prompt
    // always states the duration the thumb is on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDurationLabel))]
    private double _selectedMinutes;

    public string SelectedDurationLabel => FormatDuration(TimeSpan.FromMinutes(SelectedMinutes));

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
        if (request is DurationRequest duration)
        {
            // Degenerate ranges collapse to a single stop rather than
            // throwing: the slider then renders pinned at the one legal value.
            SliderMinimum = Math.Max(0, duration.Minimum.TotalMinutes);
            SliderMaximum = Math.Max(SliderMinimum, duration.Maximum.TotalMinutes);
            SliderStep = Math.Max(1, duration.Step.TotalMinutes);
            SelectedMinutes = Math.Clamp(
                duration.InitialValue.TotalMinutes, SliderMinimum, SliderMaximum);
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
            case DurationRequest:
                Complete(new DurationResult(TimeSpan.FromMinutes(SelectedMinutes)));
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
            // The slider is always on a legal value, so Submit is always live.
            DurationRequest => true,
            _ => false,
        };
    }

    // Mirrors the compact duration wording plugins use in menu rows ("30 min",
    // "1h 30m", "2d") so the slider's readout and the tray text agree.
    internal static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return d.Minutes == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalHours}h {d.Minutes}m";
        var days = (int)d.TotalDays;
        var rem = d - TimeSpan.FromDays(days);
        if (rem == TimeSpan.Zero) return $"{days}d";
        return rem.Minutes == 0 ? $"{days}d {(int)rem.TotalHours}h" : $"{days}d {(int)rem.TotalHours}h {rem.Minutes}m";
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
