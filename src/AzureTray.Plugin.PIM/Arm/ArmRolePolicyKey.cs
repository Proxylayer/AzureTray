namespace AzureTray.Plugin.PIM.Arm;

// Identity of an ARM role management policy: a policy is assigned to a role
// AT a scope, and the same role can carry different policies at different
// scopes, so the role definition id alone is not a key. Unlike Graph, ARM's
// roleDefinitionId is a full resource path
// ("/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions/{guid}"),
// and both it and the scope come back with inconsistent casing — the factory
// normalizes so equality is effectively OrdinalIgnoreCase.
internal readonly record struct ArmRolePolicyKey
{
    private ArmRolePolicyKey(string scope, string roleDefinitionId)
    {
        Scope = scope;
        RoleDefinitionId = roleDefinitionId;
    }

    public string Scope { get; }

    public string RoleDefinitionId { get; }

    public static ArmRolePolicyKey For(string? scope, string? roleDefinitionId)
        => new(Normalize(scope), Normalize(roleDefinitionId));

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().TrimEnd('/').ToLowerInvariant();
}
