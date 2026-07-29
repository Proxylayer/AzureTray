using System;

namespace AzureTray.Plugin.PIM.Policies;

// The parts of a role's PIM policy that bear on a user self-activating it.
// Both members are nullable and null always means "not known" — never
// "unlimited" and never "not required". A policy read can fail with 403
// (the signed-in user holds none of the directory roles that permit reading
// policies), and a rule can be absent from the response; in both cases the
// caller degrades rather than assuming a permissive default.
internal sealed record RolePolicy(
    bool? ApprovalRequired,
    TimeSpan? MaxActivationDuration);
