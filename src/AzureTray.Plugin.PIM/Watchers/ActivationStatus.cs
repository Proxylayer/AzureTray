using System;
using System.Linq;

namespace AzureTray.Plugin.PIM.Watchers;

// Classifies the status strings Graph and ARM report for a role-assignment
// schedule request. Both services share the vocabulary for the states that
// matter here; the union is small enough to keep in one place.
internal static class ActivationStatus
{
    // Terminal success: the assignment exists and the role is live.
    public const string Provisioned = "Provisioned";

    // Terminal failures — the request will never become active, so stop
    // tracking it. Includes both providers' spellings.
    private static readonly string[] TerminalFailures =
    {
        "Denied",
        "AdminDenied",
        "Failed",
        "FailedAsResourceIsLocked",
        "Canceled",
        "Cancelled",
        "Withdrawn",
        "Revoked",
        "TimedOut",
        "Invalid",
    };

    public static bool IsProvisioned(string? status)
        => string.Equals(status, Provisioned, StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminalFailure(string? status)
        => !string.IsNullOrWhiteSpace(status)
            && TerminalFailures.Contains(status, StringComparer.OrdinalIgnoreCase);
}
