using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetherform.Geometry;

// Clone manifests rather than reconstructing option groups. Unknown metadata,
// IMC edits, combining containers, priorities and option IDs survive unchanged.
public static class StandaloneRefit
{
    public const string Prefix = "AF Standalone Fit ";
    public const string Folder = "Aetherform/Refits/YAB and Rue to Aetherform";
    public sealed record Model(string GamePath,string File,string Slot,ushort Race,Dictionary<string,List<string>> Settings);
    private static readonly Regex Equipment = new(@"^chara/equipment/e(?<set>\d{4})/model/c(?<race>\d{4})e\k<set>(?<slot>_top|_dwn|_glv|_sho)\.mdl$",RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented=true };
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string DirectoryName(string source,string shape,string revision) => Prefix+Hash(Path.GetFullPath(source))[..12]+" "+Hash(shape+revision)[..16];
    public static Guid Identity(string directory) => new(SHA256.HashData(Encoding.UTF8.GetBytes(directory)).AsSpan(0,16));

    public static JsonObject ReadManifest(string directory)
    {
        var meta=JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory,"meta.json"))) as JsonObject
            ?? throw new InvalidDataException("Invalid source mod manifest.");
        if(meta["Groups"] is not JsonArray)
        {
            var groups=new JsonArray();
            foreach(string path in Directory.EnumerateFiles(directory,"group_*.json").Order(StringComparer.OrdinalIgnoreCase))
                groups.Add(JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid group: "+path));
            meta["Groups"]=groups;
            string defaults=Path.Combine(directory,"default_mod.json");
            if(File.Exists(defaults)) meta["DefaultData"]=JsonNode.Parse(File.ReadAllBytes(defaults));
        }
        meta["FileVersion"]=4;
        // Penumbra v3 used Swaps; v4 uses FileSwaps. Page and PageNames
        // already belong to the manifest/group and must retain their values.
        foreach(var obj in Objects(meta).ToArray())
            if(obj["Swaps"] is JsonObject swaps)
            {
                if(obj["FileSwaps"]==null) obj["FileSwaps"]=swaps.DeepClone();
                obj.Remove("Swaps");
            }
        return meta;
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if(node is JsonObject obj)
        {
            yield return obj;
            foreach(var child in obj.ToArray()) foreach(var nested in Objects(child.Value)) yield return nested;
        }
        else if(node is JsonArray array)
            foreach(var child in array) foreach(var nested in Objects(child)) yield return nested;
    }

    // Include every variant and asset, not just the equipped model or selection.
    public static string Revision(string directory)
    {
        var meta=ReadManifest(directory);
        var paths=Objects(meta).Where(o=>o["Files"] is JsonObject)
            .SelectMany(o=>((JsonObject)o["Files"]!).Select(p=>Path.GetFullPath(Path.Combine(directory,p.Value!.GetValue<string>()))))
            .Concat(Objects(meta).Where(o=>o["Image"] is JsonValue).Select(o=>Path.GetFullPath(Path.Combine(directory,o["Image"]!.GetValue<string>()))))
            .Concat(Directory.EnumerateFiles(directory,"*.json")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return Hash(string.Join('\n',paths.Select(p=>new FileInfo(p) is {Exists:true} f ? p+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks : p+"|missing")));
    }

    public static bool IsSupported(string gamePath,out string slot,out ushort race)
    {
        var match=Equipment.Match(gamePath);
        slot=match.Groups["slot"].Value;
        race=match.Success ? ushort.Parse(match.Groups["race"].Value) : (ushort)0;
        return match.Success && match.Groups["set"].Value is not ("0000" or "9903") &&
            race is 201 or 401 or 601 or 801 or 1001 or 1401 or 1601 or 1801;
    }

    public static Dictionary<string,List<string>> Defaults(JsonObject manifest)
    {
        var result=new Dictionary<string,List<string>>();
        foreach(var group in ((JsonArray)manifest["Groups"]!).OfType<JsonObject>())
        {
            if(group["Name"] is not JsonValue name || group["Options"] is not JsonArray options) continue;
            ulong mask=group["DefaultSettings"]?.GetValue<ulong>() ?? 0;
            bool multi=group["Type"]?.GetValue<string>() is "Multi" or "Combining" or "Imc";
            result[name.GetValue<string>()]=options.Select((o,i)=>(o,i))
                .Where(p=>multi ? p.i<64 && (mask & (1UL<<p.i))!=0 : (ulong)p.i==mask)
                .Select(p=>p.o!["Name"]!.GetValue<string>()).ToList();
        }
        return result;
    }

    public static int ModelCount(string source) => Objects(ReadManifest(source))
        .Where(o=>o["Files"] is JsonObject).Sum(o=>((JsonObject)o["Files"]!).Count(p=>IsSupported(p.Key,out _,out _)));

    // Keep the original available until the new mod is configured and enabled.
    public static void Activate(Action configure,Action<bool> enabled,bool wasEnabled,
        IReadOnlyList<Action<bool>> superseded,Action<Exception>? recoveryFailure=null)
    {
        var attempted=new List<Action<bool>>();
        bool enabling=false;
        try
        {
            configure();
            enabling=true;enabled(true);
            foreach(var toggle in superseded) { attempted.Add(toggle);toggle(false); }
        }
        catch
        {
            foreach(var toggle in attempted.AsEnumerable().Reverse())
                try { toggle(true); } catch(Exception ex) { recoveryFailure?.Invoke(ex); }
            if(enabling) try { enabled(wasEnabled); } catch(Exception ex) { recoveryFailure?.Invoke(ex); }
            throw;
        }
    }

    public static void Build(string source,string destination,string shape,string revision,
        Dictionary<string,List<string>> selected,Func<Model,byte[]> convert,CancellationToken token=default)
    {
        if(Directory.Exists(destination))
        {
            var existing=ReadManifest(destination);
            if(existing["Identifier"]?.GetValue<string>()!=Identity(Path.GetFileName(destination)).ToString() ||
                existing["AetherformShape"]?.GetValue<string>()!=shape || existing["AetherformRevision"]?.GetValue<string>()!=revision)
                throw new IOException("The standalone output directory belongs to another mod or revision.");
            // A complete export is immutable. Preserve any later user edits.
            foreach(var obj in Objects(existing).Where(o=>o["Files"] is JsonObject))
            foreach(var file in (JsonObject)obj["Files"]!)
                if(!File.Exists(Path.Combine(destination,file.Value!.GetValue<string>())))
                    throw new IOException("An existing standalone mod has a missing asset: "+file.Key);
            return;
        }
        string staging=destination+".building-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            string sourceRevision=Revision(source);
            var meta=ReadManifest(source);
            var defaults=Defaults(meta);
            var contexts=new Dictionary<JsonObject,Dictionary<string,List<string>>>();
            Dictionary<string,List<string>> Context()=>defaults.ToDictionary(p=>p.Key,p=>p.Value.ToList());
            if(meta["DefaultData"] is JsonObject data) contexts[data]=Context();
            foreach(var group in ((JsonArray)meta["Groups"]!).OfType<JsonObject>())
            {
                if(group["Options"] is not JsonArray options || group["Name"] is not JsonValue groupName) continue;
                string name=groupName.GetValue<string>();
                if(group["Containers"] is JsonArray containers)
                {
                    for(int i=0;i<containers.Count;i++) if(containers[i] is JsonObject container)
                    {
                        var settings=Context();
                        settings[name]=options.Where((o,j)=>j<31 && (i & (1<<j))!=0).Select(o=>o!["Name"]!.GetValue<string>()).ToList();
                        contexts[container]=settings;
                    }
                }
                else foreach(var option in options.OfType<JsonObject>())
                {
                    var settings=Context();settings[name]=[option["Name"]!.GetValue<string>()];contexts[option]=settings;
                }
                // Preserve schema and option indices, with the user's active selection as the portable default.
                if(selected.TryGetValue(name,out var active))
                {
                    bool multi=group["Type"]?.GetValue<string>() is "Multi" or "Combining" or "Imc";
                    ulong setting=0;
                    for(int i=0;i<options.Count;i++) if(active.Contains(options[i]!["Name"]!.GetValue<string>()))
                        setting=multi ? setting | (i<64 ? 1UL<<i : 0) : (ulong)i;
                    group["DefaultSettings"]=setting;
                }
            }
            var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var converted=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var obj in Objects(meta).ToArray())
            {
                token.ThrowIfCancellationRequested();
                if(obj["Image"] is JsonValue image && image.GetValue<string>() is {Length:>0} imagePath)
                {
                    string path=Path.GetFullPath(Path.Combine(source,imagePath));
                    if(File.Exists(path))
                    {
                        string preview="images/"+Hash(path)+Path.GetExtension(path);
                        Directory.CreateDirectory(Path.Combine(staging,"images"));
                        File.Copy(path,Path.Combine(staging,preview),true);
                        obj["Image"]=preview;
                    }
                }
                if(obj["FileSwaps"] is JsonObject swaps && swaps.Any(p=>IsSupported(p.Key,out _,out _)))
                    throw new InvalidDataException("An equipment model file swap must be resolved before standalone conversion.");
                if(obj["Files"] is not JsonObject files) continue;
                foreach(var entry in files.ToArray())
                {
                    string path=Path.GetFullPath(Path.Combine(source,entry.Value!.GetValue<string>()));
                    if(!File.Exists(path)) throw new FileNotFoundException("A source mod asset is missing.",path);
                    string relative;
                    if(IsSupported(entry.Key,out var slot,out var race))
                    {
                        var settings=contexts.GetValueOrDefault(obj) ?? Context();
                        string key=entry.Key+"|"+path+"|"+JsonSerializer.Serialize(settings.OrderBy(p=>p.Key));
                        if(!converted.TryGetValue(key,out relative!))
                        {
                            relative="models/"+Hash(key)+".mdl";
                            Directory.CreateDirectory(Path.Combine(staging,"models"));
                            var model=convert(new(entry.Key,path,slot,race,settings));
                            if(model.Length==0) throw new InvalidDataException("An option produced an empty model: "+entry.Key);
                            token.ThrowIfCancellationRequested();
                            File.WriteAllBytes(Path.Combine(staging,relative),model);
                            converted.Add(key,relative);
                        }
                    }
                    else if(!copied.TryGetValue(path,out relative!))
                    {
                        relative="files/"+Hash(path)+Path.GetExtension(path);
                        Directory.CreateDirectory(Path.Combine(staging,"files"));
                        File.Copy(path,Path.Combine(staging,relative));copied.Add(path,relative);
                    }
                    files[entry.Key]=relative;
                }
            }
            meta["Identifier"]=Identity(Path.GetFileName(destination)).ToString();
            meta["Name"]=(meta["Name"]?.GetValue<string>() ?? Path.GetFileName(source))+" — Aetherform";
            meta["AetherformSource"]=Path.GetFileName(source);
            meta["AetherformShape"]=shape;
            meta["AetherformRevision"]=revision;
            meta["AetherformStandaloneVersion"]=1;
            foreach(string cover in new[]{"cover.jpg","cover.png"})
                if(File.Exists(Path.Combine(source,cover))) File.Copy(Path.Combine(source,cover),Path.Combine(staging,cover));
            File.WriteAllText(Path.Combine(staging,"meta.json"),meta.ToJsonString(Json));
            token.ThrowIfCancellationRequested();
            if(Revision(source)!=sourceRevision) throw new IOException("The source mod changed during standalone conversion.");
            Directory.Move(staging,destination);
        }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging,true); }
    }
}
