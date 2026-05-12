using System.Threading.Tasks;
using AzureTray.Notifications;
using AzureTray.Plugin.Contracts;
using Xunit;

namespace AzureTray.Tests.Notifications;

public sealed class NotificationViewModelTests
{
    [Fact]
    public async Task YesNo_YesCommand_CompletesWithAccepted()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(new YesNoRequest("T", "M"), tcs);

        vm.YesCommand.Execute(null);

        var result = await tcs.Task;
        var yn = Assert.IsType<YesNoResult>(result);
        Assert.True(yn.Accepted);
    }

    [Fact]
    public async Task YesNo_NoCommand_CompletesWithRejected()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(new YesNoRequest("T", "M"), tcs);

        vm.NoCommand.Execute(null);

        var result = await tcs.Task;
        var yn = Assert.IsType<YesNoResult>(result);
        Assert.False(yn.Accepted);
    }

    [Fact]
    public async Task Choice_WithSelectedItem_CompletesWithSelection()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(
            new ChoiceRequest("T", "M", new[] { "A", "B", "C" }),
            tcs);

        vm.SelectedChoice = "B";
        Assert.True(vm.SubmitCommand.CanExecute(null));
        vm.SubmitCommand.Execute(null);

        var result = await tcs.Task;
        var choice = Assert.IsType<ChoiceResult>(result);
        Assert.Equal("B", choice.SelectedChoice);
        Assert.Null(choice.OtherText);
    }

    [Fact]
    public async Task Choice_WithOther_CompletesWithOtherText()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(
            new ChoiceRequest("T", "M", new[] { "A", "B" }, AllowOther: true),
            tcs);

        vm.IsOtherSelected = true;
        vm.OtherText = "Custom answer";
        Assert.True(vm.SubmitCommand.CanExecute(null));
        vm.SubmitCommand.Execute(null);

        var result = await tcs.Task;
        var choice = Assert.IsType<ChoiceResult>(result);
        Assert.Null(choice.SelectedChoice);
        Assert.Equal("Custom answer", choice.OtherText);
    }

    [Fact]
    public void Choice_CannotSubmit_WithoutSelectionOrOther()
    {
        var vm = new NotificationViewModel(
            new ChoiceRequest("T", "M", new[] { "A" }, AllowOther: true),
            NewCompletion());

        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.IsOtherSelected = true; // Other selected but no text yet
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.OtherText = "  ";
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.OtherText = "hi";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Choice_SelectingFromListDeselectsOther()
    {
        var vm = new NotificationViewModel(
            new ChoiceRequest("T", "M", new[] { "A", "B" }, AllowOther: true),
            NewCompletion());

        vm.IsOtherSelected = true;
        vm.OtherText = "x";

        vm.SelectedChoice = "A";

        Assert.False(vm.IsOtherSelected);
    }

    [Fact]
    public void Choice_SelectingOtherDeselectsList()
    {
        var vm = new NotificationViewModel(
            new ChoiceRequest("T", "M", new[] { "A", "B" }, AllowOther: true),
            NewCompletion());

        vm.SelectedChoice = "A";

        vm.IsOtherSelected = true;

        Assert.Null(vm.SelectedChoice);
    }

    [Fact]
    public async Task TextInput_PicksUpInitialValueAndSubmits()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(
            new TextInputRequest("T", "M", InitialValue: "preset"),
            tcs);

        Assert.Equal("preset", vm.InputText);
        Assert.True(vm.SubmitCommand.CanExecute(null));
        vm.SubmitCommand.Execute(null);

        var result = await tcs.Task;
        var ti = Assert.IsType<TextInputResult>(result);
        Assert.Equal("preset", ti.Text);
    }

    [Fact]
    public void TextInput_CannotSubmit_WhenBlank()
    {
        var vm = new NotificationViewModel(
            new TextInputRequest("T", "M"),
            NewCompletion());

        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.InputText = "  ";
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.InputText = "hello";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task DismissCommand_ResolvesWithDismissed()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(new YesNoRequest("T", "M"), tcs);

        vm.DismissCommand.Execute(null);

        var result = await tcs.Task;
        Assert.IsType<DismissedResult>(result);
    }

    [Fact]
    public async Task OnWindowClosed_AfterAnswer_DoesNotOverrideResult()
    {
        var tcs = NewCompletion();
        var vm = new NotificationViewModel(new YesNoRequest("T", "M"), tcs);

        vm.YesCommand.Execute(null);
        vm.OnWindowClosed();

        var result = await tcs.Task;
        Assert.IsType<YesNoResult>(result);
    }

    private static TaskCompletionSource<NotificationResult> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
