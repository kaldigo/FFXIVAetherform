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
    private readonly ZipArchive? motion;
    private sealed record Catalog(Dictionary<int, Dictionary<string, Dictionary<int, string[]>>> Areas, Dictionary<string, string[]> Sources);
    private readonly Dictionary<string, ZipArchive> packages = [];
    private readonly Catalog? catalog;
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, string[]>>> extra = [];
    private readonly int[] originalCounts;
    internal sealed record ChestStyle(string Name, int[] Indices, string[] Variations);
    public ChestStyle[] ChestStyles { get; }
    public bool HasMotion => motion != null;
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
            originalCounts = Index.Areas.Select(a => a.Values.Length).ToArray();
            var path = Path.Combine(Path.GetDirectoryName(package)!, "tiramisu2110.zip");
            if (File.Exists(path))
            {
                motion = ZipFile.OpenRead(path);
                using var data = typeof(PhysicsBank).Assembly.GetManifestResourceStream("Aetherform.motion.json.gz")!;
                using var index = new GZipStream(data, CompressionMode.Decompress);
                catalog = JsonSerializer.Deserialize<Catalog>(index)!;
                foreach (var (i, choices) in catalog.Areas)
                {
                    var area = Index.Areas[i];
                    int n = originalCounts[i];
                    extra[i] = [];
                    foreach (var levels in choices.Values) extra[i].Add(n++, levels);
                    Index.Areas[i] = area with { Values = [..area.Values, ..choices.Keys] };
                }
            }
            ChestStyles = Index.Areas[0].Values
                .Select((name, index) => (Parts: name.Split(" / ", 2), Index: index))
                .GroupBy(v => v.Parts[0])
                .Select(g => new ChestStyle(g.Key, g.Select(v => v.Index).ToArray(),
                    g.Select(v => v.Parts.Length == 2 ? v.Parts[1] : "Standard").ToArray())).ToArray();
        }
        catch { motion?.Dispose(); bank.Dispose(); throw; }
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
        var basePicks = (int[])picks.Clone();
        for (int i = 0; i < picks.Length; i++)
            if (picks[i] >= originalCounts[i])
                basePicks[i] = i == 0 ? 2 : Index.Areas[i].DefaultIndex;
        var row = Index.Rows[string.Concat(basePicks.Where((_, i) => i != 1))];
        var paths = new Dictionary<string, string>();
        Directory.CreateDirectory(cache);
        for (int i = 0; i < Index.Races.Length; ++i)
        {
            var race = Index.Races[i];
            var hash = heights[row[i]][picks[1]];
            MotionFile? body = null;
            for (int area = 0; area < picks.Length; area++)
            {
                if (picks[area] < originalCounts[area] || (area >= 7 && race != "0801")) continue;
                var levels = extra[area][picks[area]];
                var exact = levels.TryGetValue(picks[1], out var files);
                var source = (files ?? levels[3])[i];
                body ??= new MotionFile(ReadBytes(bank, "bank/" + hash + ".phyb", hash));
                body.Replace(area, new MotionFile(ReadMotion(source)),
                    picks[2] != 0, area == 0 && !exact && picks[1] != 3);
            }
            var mixed = body?.Encode();
            if (mixed != null) hash = Convert.ToHexStringLower(SHA256.HashData(mixed));
            var file = Path.Combine(cache, hash + ".phyb");
            if (!File.Exists(file) || !Matches(File.ReadAllBytes(file), hash))
            {
                var bytes = mixed ?? ReadBytes(bank, "bank/" + hash + ".phyb", hash);
                File.WriteAllBytes(file + ".tmp", bytes);
                File.Move(file + ".tmp", file, true);
            }
            paths.Add($"chara/human/c{race}/skeleton/base/b0001/phy_c{race}b0001.phyb", file);
        }
        return paths;
    }
    private static bool Matches(byte[] bytes, string hash) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes)).StartsWith(hash, StringComparison.Ordinal);
    private static byte[] ReadBytes(ZipArchive zip, string name, string hash)
    {
        using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException($"Missing physics entry {name}.")).Open();
        using var data = new MemoryStream();
        stream.CopyTo(data);
        var bytes = data.ToArray();
        if (!Matches(bytes, hash)) throw new InvalidDataException("Damaged physics asset.");
        return bytes;
    }
    private byte[] ReadMotion(string hash)
    {
        var path = catalog!.Sources[hash];
        if (!packages.TryGetValue(path[0], out var package))
        {
            using var entry = (motion!.GetEntry(path[0]) ?? throw new InvalidDataException("Expected the original Tiramisu2 2110 archive.")).Open();
            var buffer = new MemoryStream();
            entry.CopyTo(buffer);
            buffer.Position = 0;
            packages[path[0]] = package = new ZipArchive(buffer, ZipArchiveMode.Read);
        }
        return ReadBytes(package, path[1], hash);
    }
    public void Dispose()
    {
        foreach (var package in packages.Values) package.Dispose();
        motion?.Dispose();
        bank.Dispose();
    }
}
