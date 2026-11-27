using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aetherform.Geometry;

// Append variants to their source option, retaining all copied assets and
// manipulations. Original option indices, IDs, defaults and pages stay fixed.
public sealed class CustomFitOptions
{
    public sealed record Binding(string Group,string Name,string Original,CustomFitBundle Bundle,CustomFitVariant Variant);
    private readonly List<Binding> bindings=[];
    public IReadOnlyList<Binding> Bindings=>bindings;
    public static string Suffix(CustomFitBundle b,CustomFitVariant v)=>" — "+v.Name+" [AF:"+b.Recipe.Id+"/"+v.Id+"]";
    private const string ExtraPrefix="Aetherform Custom Fits: ";
    public static Dictionary<string,List<string>> Normalize(Dictionary<string,List<string>> settings)=>settings.Where(p=>!p.Key.StartsWith(ExtraPrefix,StringComparison.Ordinal)).ToDictionary(p=>p.Key,
        p=>p.Value.Select(OriginalName).ToList());
    private static string OriginalName(string name)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,@" \[AF:[a-z0-9-]+/[a-z0-9-]+\]$")) return name;
        int at=name.LastIndexOf(" — ",StringComparison.Ordinal);return at>=0?name[..at]:name;
    }
    public static Dictionary<string,List<string>> NormalizeKnown(Dictionary<string,List<string>> settings,CustomFitLibrary library)
    {
        var suffixes=library.Bundles.SelectMany(b=>b.Recipe.Variants.Select(v=>Suffix(b,v))).OrderByDescending(s=>s.Length).ToArray();
        var known=settings.ToDictionary(p=>p.Key,p=>p.Value.Select(n=>
        {
            var suffix=suffixes.FirstOrDefault(s=>n.EndsWith(s,StringComparison.Ordinal));
            return suffix==null?n:n[..^suffix.Length];
        }).ToList());
        return Normalize(known);
    }
    public Binding? For(StandaloneRefit.Model model)=>bindings.SingleOrDefault(b=>b.Bundle.Recipe.GamePath==model.GamePath &&
        model.Settings.TryGetValue(b.Group,out var options) && options.Contains(b.Name));
    public static Dictionary<string,List<string>> Selections(JsonObject meta,Dictionary<string,List<string>> previous,CustomFitLibrary library)
    {
        var result=StandaloneRefit.Defaults(meta);
        var normalized=NormalizeKnown(previous,library);
        foreach(var group in ((JsonArray)meta["Groups"]!).OfType<JsonObject>())
        {
            string name=group["Name"]!.GetValue<string>();
            if(!previous.TryGetValue(name,out var active) || group["Options"] is not JsonArray options) continue;
            var available=options.OfType<JsonObject>().Select(o=>o["Name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var restored=active.Where(available.Contains).ToList();
            if(restored.Count!=active.Count && normalized.TryGetValue(name,out var original)) restored=original.Where(available.Contains).ToList();
            if(restored.Count>0 || group["Type"]?.GetValue<string>() is "Multi" or "Combining" or "Imc") result[name]=restored;
        }
        return result;
    }
    public void Append(JsonObject meta,string source,CustomFitLibrary library)
    {
        var groups=(JsonArray)meta["Groups"]!;
        foreach(var g in groups.OfType<JsonObject>().ToArray())
        {
            // Combining/IMC groups have container semantics; never invent a
            // new flag or shift those existing indices.
            if(g["Type"]?.GetValue<string>()!="Single" || g["Options"] is not JsonArray options) continue;
            string group=g["Name"]!.GetValue<string>();
            foreach(var original in options.OfType<JsonObject>().ToArray())
            {
                if(original["Files"] is not JsonObject files) continue;
                foreach(var file in files.ToArray().Where(f=>StandaloneRefit.IsSupported(f.Key,out _,out _)))
                {
                    var path=Path.Combine(source,file.Value!.GetValue<string>());
                    if(!File.Exists(path)) continue;
                    var bundle=library.Match(file.Key,"family",File.ReadAllBytes(path));
                    if(bundle==null) continue;
                    foreach(var variant in bundle.Recipe.Variants)
                    {
                        string name=original["Name"]!.GetValue<string>()+Suffix(bundle,variant);
                        if(options.OfType<JsonObject>().Any(o=>o["Name"]?.GetValue<string>()==name)) throw new InvalidDataException("Duplicate custom fit option.");
                        var added=(JsonObject)original.DeepClone();added["Name"]=name;
                        added["Id"]=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(group+"|"+name)).AsSpan(0,16)).ToString();
                        options.Add(added);
                        bindings.Add(new(group,name,original["Name"]!.GetValue<string>(),bundle,variant));
                    }
                }
            }
        }
        // Default models and combining/IMC containers do not have appendable
        // single options. Offer explicit reference variants in separate groups.
        var candidates=new List<(string Game,string File)>();
        void Visit(JsonNode? node)
        {
            if(node is JsonObject o)
            {
                if(o["Files"] is JsonObject files)
                    candidates.AddRange(files.Where(f=>StandaloneRefit.IsSupported(f.Key,out _,out _)).Select(f=>(f.Key,f.Value!.GetValue<string>())));
                foreach(var p in o.ToArray()) Visit(p.Value);
            }
            else if(node is JsonArray a) foreach(var n in a.ToArray()) Visit(n);
        }
        Visit(meta);
        foreach(var file in candidates.Distinct().ToArray())
        {
            var path=Path.Combine(source,file.File);if(!File.Exists(path)) continue;
            var bundle=library.Match(file.Game,"family",File.ReadAllBytes(path));
            if(bundle==null || bundle.Recipe.Variants.Length==0 || bindings.Any(b=>b.Bundle.Recipe.Id==bundle.Recipe.Id)) continue;
            string group=ExtraPrefix+bundle.Recipe.Id;
            if(groups.OfType<JsonObject>().Any(g=>g["Name"]?.GetValue<string>()==group)) throw new InvalidDataException("Custom fit group name conflicts with the source mod.");
            var options=new JsonArray(new JsonObject { ["Name"]="Automatic" });
            foreach(var v in bundle.Recipe.Variants)
            {
                options.Add(new JsonObject {["Id"]=StableId(group+v.Id),["Name"]=v.Name,["Files"]=new JsonObject {[file.Game]=file.File}});
                bindings.Add(new(group,v.Name,"",bundle,v));
            }
            int priority=groups.OfType<JsonObject>().Select(g=>g["Priority"]?.GetValue<int>()??0).DefaultIfEmpty().Max();
            groups.Add(new JsonObject {["Id"]=StableId(group),["Name"]=group,["Type"]="Single",["Priority"]=checked(priority+1),
                ["DefaultSettings"]=0,["Page"]=CustomPage(meta),["Options"]=options});
        }
    }

    private static string StableId(string value)=>new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16)).ToString();
    private static int CustomPage(JsonObject meta)
    {
        if(meta["PageNames"] is not JsonObject pages) meta["PageNames"]=pages=new();
        foreach(var p in pages) if(p.Value?.GetValue<string>()=="Aetherform Custom Fits" && int.TryParse(p.Key,out int existing)) return existing;
        int largest=pages.Select(p=>int.TryParse(p.Key,out int n)?n:0)
            .Concat(((JsonArray)meta["Groups"]!).OfType<JsonObject>().Select(g=>g["Page"]?.GetValue<int>()??0)).DefaultIfEmpty().Max();
        int page=checked(largest+1);pages[page.ToString()]="Aetherform Custom Fits";return page;
    }

    public sealed record VanillaVariant(string Id,string Name,string File);
    public static bool SaveVanilla(string folder,string gamePath,IReadOnlyList<VanillaVariant> variants)
    {
        string path=Path.Combine(folder,"meta.json");var meta=JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var groups=meta["Groups"] as JsonArray ?? throw new InvalidDataException("Generated fit has no option groups.");
        const string name="Aetherform Custom Fits";
        string id=StableId("vanilla-custom|"+gamePath);
        var group=groups.OfType<JsonObject>().FirstOrDefault(g=>g["Name"]?.GetValue<string>()==name);
        if(group==null && variants.Count==0) return false;
        string before=meta.ToJsonString();
        if(group==null)
        {
            int priority=groups.OfType<JsonObject>().Select(g=>g["Priority"]?.GetValue<int>()??0).DefaultIfEmpty().Max();
            group=new JsonObject {["Id"]=id,["Name"]=name,["Type"]="Single",["Priority"]=checked(priority+1),
                ["DefaultSettings"]=0,["Page"]=CustomPage(meta),["Options"]=new JsonArray(new JsonObject {["Name"]="Automatic"})};
            groups.Add(group);
        }
        if(group["Id"]?.GetValue<string>()!=id) throw new InvalidDataException("Custom fit group belongs to the user.");
        var options=group["Options"]!.AsArray();
        // Keep option indices stable, but retire obsolete model redirects.
        foreach(var option in options.OfType<JsonObject>().Skip(1)) option["Files"]=new JsonObject();
        foreach(var variant in variants)
        {
            string optionId=StableId(gamePath+"|"+variant.Id);
            var option=options.OfType<JsonObject>().FirstOrDefault(o=>o["Id"]?.GetValue<string>()==optionId);
            if(option==null) {option=new JsonObject {["Id"]=optionId,["Name"]=variant.Name};options.Add(option);}
            byte[] bytes=File.ReadAllBytes(variant.File);string file="custom-fits/"+CustomFitRecipe.Hash(bytes)+".mdl";
            Directory.CreateDirectory(Path.Combine(folder,"custom-fits"));
            string target=Path.Combine(folder,file);if(!File.Exists(target)) File.WriteAllBytes(target,bytes);
            option["Files"]=new JsonObject {[gamePath]=file};
        }
        if(before==meta.ToJsonString()) return false;
        string temp=path+".custom.tmp";File.WriteAllText(temp,meta.ToJsonString(new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));File.Move(temp,path,true);return true;
    }
}
