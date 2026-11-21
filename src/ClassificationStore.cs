using Aetherform.Geometry;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetherform;

internal sealed class ClassificationStore
{
    internal sealed record Entry(string Revision, GarmentClassification? Data);
    internal static readonly Entry Unclassified = new("unclassified", null);
    private readonly Dictionary<string,Entry> bundled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,(long Stamp,long Length,Entry Value)> local = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;
    private readonly Action<string> warn;
    private readonly Dictionary<string,(long Stamp,long Length)> invalid = new(StringComparer.OrdinalIgnoreCase);
    private long nextPoll;
    internal long Generation { get; private set; }
    public ClassificationStore(string config, Action<string> warn)
    {
        this.warn=warn;
        directory=Path.Combine(config,"classifications");
        Directory.CreateDirectory(directory);
        var assembly=typeof(ClassificationStore).Assembly;
        const string prefix="Aetherform.classifications.";
        foreach (var name in assembly.GetManifestResourceNames().Where(s=>s.StartsWith(prefix,StringComparison.Ordinal)))
        {
            using var stream=assembly.GetManifestResourceStream(name)!;
            using var buffer=new MemoryStream();
            stream.CopyTo(buffer);
            var model=name[prefix.Length..^5];
            bundled.Add(model,Read(buffer.ToArray(),model));
        }
    }
    private static Entry Read(byte[] bytes,string name) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)),GarmentClassification.Read(bytes,name));
    internal Entry Get(string gamePath)
    {
        var key=Path.GetFileNameWithoutExtension(gamePath);
        return local.TryGetValue(key,out var entry)?entry.Value:bundled.GetValueOrDefault(key,Unclassified);
    }
    internal void Poll()
    {
        if(Environment.TickCount64<nextPoll)return;
        nextPoll=Environment.TickCount64+5000;
        var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in Directory.EnumerateFiles(directory,"*.json"))
        {
            var info=new FileInfo(path);var name=Path.GetFileNameWithoutExtension(path);
            found.Add(name);
            if(local.TryGetValue(name,out var old) && old.Stamp==info.LastWriteTimeUtc.Ticks && old.Length==info.Length)continue;
            var stamp=(info.LastWriteTimeUtc.Ticks, info.Length);
            if(invalid.TryGetValue(name,out var previousStamp) && stamp==previousStamp) continue;
            Entry entry;
            try
            {
                entry=Read(File.ReadAllBytes(path),name);
                invalid.Remove(name);
            }
            catch(IOException) { continue; }
            catch(Exception ex) when(ex is InvalidDataException or JsonException or FormatException)
            {
                invalid[name]=stamp;
                warn($"Keeping the previous classification for {name}. {ex.Message}");
                continue;
            }
            if(entry.Revision!=Get(name+".mdl").Revision)Generation++;
            local[name]=(info.LastWriteTimeUtc.Ticks,info.Length,entry);
        }
        foreach(var name in invalid.Keys.Where(k=>!found.Contains(k)).ToArray()) invalid.Remove(name);
        foreach(var removed in local.Keys.Where(k=>!found.Contains(k)).ToArray())
        {
            var previous=local[removed].Value;
            local.Remove(removed);
            if(previous.Revision!=Get(removed+".mdl").Revision)Generation++;
        }
    }
}
