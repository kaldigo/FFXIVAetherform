using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetherform.Geometry;

public sealed class GarmentClassification
{
    public bool PreserveSkinCut { get; init; }
    public Dictionary<string,int[][]> SurfaceAttachments { get; init; } = [];
    public int Schema { get; init; }
    public string Model { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public Dictionary<int,byte[]> Meshes { get; init; } = [];
    public Dictionary<int,byte[]>? ClosePanels { get; init; }
    internal bool? ClosePanel(int mesh,int face) => ClosePanels != null && ClosePanels.TryGetValue(mesh,out var bits) && face/8 < bits.Length ? (bits[face/8] & (1 << (face%8))) != 0 : null;
    public Dictionary<string,int[][]> Hosts { get; init; } = [];

    public static GarmentClassification Read(byte[] bytes, string name)
    {
        var record = JsonSerializer.Deserialize<GarmentClassification>(bytes) ?? throw new InvalidDataException(name);
        if (record.Schema != 1 || record.Model != name ||
            !Regex.IsMatch(name, @"^c[0-9]{4}e[0-9]{4}_(top|dwn|glv|sho)$") ||
            !Regex.IsMatch(record.SourceHash, @"\A[0-9a-fA-F]{64}\z") || record.Meshes is null || record.Hosts is null || record.SurfaceAttachments is null ||
            record.Meshes.Count == 0 || record.Meshes.Any(p => p.Key < 0 || p.Value == null || p.Value.Any(b => b > 15)))
            throw new InvalidDataException($"Invalid garment classification for {name}.");
        if(record.ClosePanels != null && record.ClosePanels.Any(p => !record.Meshes.TryGetValue(p.Key,out var flags) ||
            p.Value == null || p.Value.Length != (flags.Length+7)/8)) throw new InvalidDataException($"Invalid close-fit mask for {name}.");
        foreach (var (key, roots) in record.Hosts.Concat(record.SurfaceAttachments))
        {
            var parts = key.Split('/');
            bool Valid(int m,int f) => record.Meshes.TryGetValue(m,out var flags) && (uint)f < (uint)flags.Length;
            if (parts.Length != 2 || !int.TryParse(parts[0],out var m) || !int.TryParse(parts[1],out var f) ||
                !Valid(m,f) || roots is null || roots.Any(a => a is not {Length:2} || !Valid(a[0],a[1])))
                throw new InvalidDataException($"Invalid garment reference in {name}.");
        }
        return record;
    }

    public bool Matches(byte[] source) => Convert.ToHexString(SHA256.HashData(source)).Equals(SourceHash,StringComparison.OrdinalIgnoreCase);
    internal bool Closure(int mesh,int face) => Meshes.TryGetValue(mesh,out var flags) &&
        (uint)face < (uint)flags.Length && (flags[face] & 1) != 0;
}
