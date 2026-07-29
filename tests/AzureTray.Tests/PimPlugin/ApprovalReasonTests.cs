using System;
using System.Linq;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The requestor's reason as it is rendered for the approver's popup.
// MessageLine always carries something printable — it goes straight into the
// visible Message slot — and ClampedFullText is non-null only when the visible
// copy had to be truncated, which is the sole reason a Details expander appears.
//
// Every length assertion is written against MaxMessageLength rather than the
// literal so that retuning the clamp cannot silently invalidate these tests.
public sealed class ApprovalReasonTests
{
    private const int Max = ApprovalReason.MaxMessageLength;
    private const string NoReasonLine = "No reason was given for this request.";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \t\r\n \r\n ")]
    public void From_WithNothingUsable_RendersTheNoReasonLine(string? justification)
    {
        var reason = ApprovalReason.From(justification);

        Assert.Equal(NoReasonLine, reason.MessageLine);
        Assert.Null(reason.ClampedFullText);
    }

    [Fact]
    public void From_ShortReason_QuotesItInline()
    {
        var reason = ApprovalReason.From("Incident #42 needs Owner");

        Assert.Equal("Reason: \"Incident #42 needs Owner\"", reason.MessageLine);
        Assert.Null(reason.ClampedFullText);
    }

    // Free text arrives with newlines, tabs and padding; every run of
    // whitespace collapses to a single space so the Message slot stays a
    // predictable few lines and the clamp measures something meaningful.
    [Fact]
    public void From_CollapsesEveryRunOfWhitespace()
    {
        var reason = ApprovalReason.From("  Incident\t#42\r\n\r\n  needs   Owner  \t");

        Assert.Equal("Reason: \"Incident #42 needs Owner\"", reason.MessageLine);
        Assert.Null(reason.ClampedFullText);
    }

    // Only-long-because-of-blank-lines text must survive intact: the clamp is
    // applied after collapsing, not before.
    [Fact]
    public void From_LongOnlyBecauseOfWhitespace_IsNotClamped()
    {
        var words = Enumerable.Repeat("line", 40).ToArray();
        var padded = string.Join("\r\n\r\n", words);   // longer than the limit
        var collapsed = string.Join(' ', words);       // shorter than the limit

        Assert.True(padded.Length > Max && collapsed.Length <= Max, "fixture no longer straddles the limit");

        var reason = ApprovalReason.From(padded);

        Assert.Equal($"Reason: \"{collapsed}\"", reason.MessageLine);
        Assert.Null(reason.ClampedFullText);
    }

    [Fact]
    public void From_ReasonExactlyAtTheLimit_IsNotClamped()
    {
        var text = new string('a', Max);

        var reason = ApprovalReason.From(text);

        Assert.Equal($"Reason: \"{text}\"", reason.MessageLine);
        Assert.Null(reason.ClampedFullText);
        Assert.DoesNotContain("…", reason.MessageLine, StringComparison.Ordinal);
    }

    [Fact]
    public void From_ReasonOneCharacterOverTheLimit_IsClamped()
    {
        var text = new string('a', Max + 1);

        var reason = ApprovalReason.From(text);

        Assert.Equal($"Reason: \"{new string('a', Max)}…\"", reason.MessageLine);
        Assert.EndsWith("…\"", reason.MessageLine, StringComparison.Ordinal);
        Assert.Equal(text, reason.ClampedFullText);
    }

    // The expander copy must be the complete reason — a clamp that lost the
    // tail would leave the approver with no way to read the whole thing.
    [Fact]
    public void From_ClampedReason_KeepsTheWholeCollapsedTextForTheExpander()
    {
        var raw = "Because\n" + new string('b', Max) + "\ttail";
        var collapsed = "Because " + new string('b', Max) + " tail";

        var reason = ApprovalReason.From(raw);

        Assert.Equal(collapsed, reason.ClampedFullText);
        Assert.Equal($"Reason: \"{collapsed[..Max]}…\"", reason.MessageLine);
        Assert.True(reason.ClampedFullText!.Length > Max);
    }

    // The cut can land on a space; the ellipsis must not float away from the
    // last word.
    [Fact]
    public void From_ClampedReason_TrimsTheSpaceBeforeTheEllipsis()
    {
        var text = new string('a', Max - 1) + " tail";

        var reason = ApprovalReason.From(text);

        Assert.Equal($"Reason: \"{new string('a', Max - 1)}…\"", reason.MessageLine);
        Assert.DoesNotContain(" …", reason.MessageLine, StringComparison.Ordinal);
        Assert.Equal(text, reason.ClampedFullText);
    }
}
