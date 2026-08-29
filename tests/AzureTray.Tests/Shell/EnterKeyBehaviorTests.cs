using AzureTray.Shell;
using Xunit;

namespace AzureTray.Tests.Shell;

public sealed class EnterKeyBehaviorTests
{
    // ---- Command wins when it can execute ----

    [Fact]
    public void Decide_CommandCanExecute_ExecutesCommand_EvenWithMoveFocusNext()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.MoveFocusNext, hasCommand: true, commandCanExecute: true);

        Assert.Equal(EnterKeyBehavior.Decision.ExecuteCommand, decision);
    }

    [Fact]
    public void Decide_CommandCanExecute_NoAction_ExecutesCommand()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.None, hasCommand: true, commandCanExecute: true);

        Assert.Equal(EnterKeyBehavior.Decision.ExecuteCommand, decision);
    }

    // ---- Command can't execute: falls through to focus move ----

    [Fact]
    public void Decide_CommandCannotExecute_WithMoveFocusNext_FallsThroughToFocusMove()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.MoveFocusNext, hasCommand: true, commandCanExecute: false);

        Assert.Equal(EnterKeyBehavior.Decision.MoveFocusNext, decision);
    }

    [Fact]
    public void Decide_CommandCannotExecute_NoAction_NotHandled()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.None, hasCommand: true, commandCanExecute: false);

        Assert.Equal(EnterKeyBehavior.Decision.NotHandled, decision);
    }

    // ---- No command ----

    [Fact]
    public void Decide_NoCommand_MoveFocusNext_MovesFocus()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.MoveFocusNext, hasCommand: false, commandCanExecute: false);

        Assert.Equal(EnterKeyBehavior.Decision.MoveFocusNext, decision);
    }

    [Fact]
    public void Decide_NothingConfigured_NotHandled()
    {
        var decision = EnterKeyBehavior.Decide(EnterAction.None, hasCommand: false, commandCanExecute: false);

        Assert.Equal(EnterKeyBehavior.Decision.NotHandled, decision);
    }
}
