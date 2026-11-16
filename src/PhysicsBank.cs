using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetherform;

internal sealed record Area(string Name, string[] Values, int DefaultIndex);
internal sealed record PhysicsIndex(Area[] Areas, string[] Races, Dictionary<string, string[]> Rows);

internal sealed class PhysicsBank : IDisposable
{
    private readonly ZipArchive bank;
    private readonly Dictionary<string, string[]> heights;
    private readonly string cache;
    public PhysicsIndex Index { get; }
    public int[] Defaults => Index.Areas.Select(a => a.DefaultIndex).ToArray();

    public PhysicsBank(string package, string cache)
    {
        this.cache = cache;
        bank = ZipFile.OpenRead(package);
        try
        {
            Index = Read<PhysicsIndex>("index.json");
            heights = Read<Dictionary<string, string[]>>("heights.json");
        }
        catch { bank.Dispose(); throw; }
    }

    private T Read<T>(string name)
    {
        using var stream = bank.GetEntry(name)!.Open();
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public bool Valid(int[] picks) => picks.Length == Index.Areas.Length &&
        picks.Select((v, i) => v >= 0 && v < Index.Areas[i].Values.Length).All(v => v);

    public Dictionary<string, string> Resolve(int[] picks)
    {
        if (!Valid(picks)) throw new InvalidDataException("Unknown physics selection.");
        var row = Index.Rows[string.Concat(picks.Where((_, i) => i != 1))];
        var paths = new Dictionary<string, string>();
        Directory.CreateDirectory(cache);
        for (int i = 0; i < Index.Races.Length; ++i)
        {
            var race = Index.Races[i];
            var hash = heights[row[i]][picks[1]];
            var file = Path.Combine(cache, hash + ".phyb");
            if (!File.Exists(file) || !Matches(File.ReadAllBytes(file), hash))
            {
                using var stream = bank.GetEntry("bank/" + hash + ".phyb")!.Open();
                using var data = new MemoryStream();
                stream.CopyTo(data);
                var bytes = data.ToArray();
                if (!Matches(bytes, hash)) throw new InvalidDataException("Damaged physics bank.");
                File.WriteAllBytes(file + ".tmp", bytes);
                File.Move(file + ".tmp", file, true);
            }
            paths.Add($"chara/human/c{race}/skeleton/base/b0001/phy_c{race}b0001.phyb", file);
        }
        return paths;
    }
    private static bool Matches(byte[] bytes, string hash) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes)).StartsWith(hash, StringComparison.Ordinal);
    public void Dispose() => bank.Dispose();
}
