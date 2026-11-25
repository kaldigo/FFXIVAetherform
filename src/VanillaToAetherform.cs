namespace Aetherform;

// Game-body paths and the vanilla refit's persistent storage layout.
internal static class VanillaToAetherform
{
    internal const string SourceBody = "Vanilla";
    internal const string TargetBody = "Aetherform";
    internal static string Cache(string config) => Path.Combine(config, "refits", SourceBody, TargetBody);
    internal const string Folder = "Aetherform/Refits/Vanilla to Aetherform";
    internal static string Bare(string slot, ushort race = 201) => $"chara/equipment/e0000/model/c{race:D4}e0000{slot}.mdl";

    internal static ushort Race(byte race, byte clan) => race switch
    {
        1 => clan == 2 ? (ushort)401 : (ushort)201,
        2 => 601, 4 => 801, 5 => 1001, 6 => 1401, 7 => 1601, 8 => 1801, _ => 0,
    };

    // An enabled body can lose its paths to another mod. Check the shared shape
    // and the wearer's own race before using a body file as a target.
    internal static bool Supplies(string slot, ushort race, IReadOnlyDictionary<string,string> targets, Func<string,string> resolve)
    {
        if (!targets.TryGetValue(slot, out var target) || !SameFile(resolve(Bare(slot)), target)) return false;
        if (race == 201) return true;
        string racial = Bare(slot,race);
        string resolved = resolve(racial);
        return string.Equals(resolved,racial,StringComparison.OrdinalIgnoreCase) || SameFile(resolved,target);
    }

    private static bool SameFile(string resolved, string expected) =>
        Path.IsPathFullyQualified(resolved) &&
        string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
}
