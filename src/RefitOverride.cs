namespace Aetherform;

internal static class RefitOverride
{
    // An explicit priority above the automatic minimum is a user's override. Keep
    // both enabled and disabled overrides untouched. A newly added mod has no
    // collection settings yet; its default priority must not block activation.
    internal static bool Protected(bool configured,int actualPriority,int automaticMinimum)
        => configured && actualPriority>automaticMinimum;
}
