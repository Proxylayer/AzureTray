namespace AzureTray.Plugin.PIM.Groups;

// Identity of a PIM for Groups activation policy. Every onboarded group carries
// exactly two policies — one for member access, one for owner access — so the
// group id alone is not a key and neither is the access id. Mirrors
// ArmRolePolicyKey: object ids and access ids both come back with inconsistent
// casing, so the factory normalizes and equality is effectively
// OrdinalIgnoreCase.
internal readonly record struct GroupRolePolicyKey
{
    private GroupRolePolicyKey(string groupId, string accessId)
    {
        GroupId = groupId;
        AccessId = accessId;
    }

    public string GroupId { get; }

    public string AccessId { get; }

    public static GroupRolePolicyKey For(string? groupId, string? accessId)
        => new(Normalize(groupId), GroupAccess.NormalizeForKey(accessId));

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
