namespace AzureTray.Plugin.PIM.Watchers;

// Which PIM provider a row came from. Persisted by name into the eligible-role
// cache, so members are only ever appended — never renamed, never reordered.
internal enum PimSource
{
    // Entra ID directory roles, via Graph's roleManagement/directory resources.
    EntraId,

    // Azure RBAC roles on ARM scopes (subscriptions, resource groups,
    // management groups).
    AzureRbac,

    // Membership and ownership of PIM-onboarded Entra groups, via Graph's
    // identityGovernance/privilegedAccess/group resources. The "role" is the
    // access id (member / owner) and the scope is the group.
    EntraGroup,
}
