using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Proteus.Services;

namespace Aetherform.Geometry;

// Downloaded fit data is declarative. It never executes scripts or assemblies.
public sealed record CustomFitAsset(string Path,string Sha256);
public sealed class CustomFitEdit
{
    public int Mesh { get; init; }
    public int[] Vertices { get; init; } = [];
    public float[]? DeltaMm { get; init; }
    public Dictionary<string,float>? Weights { get; init; }
}
public sealed class CustomFitBaked
{
    public string Chest { get; init; } = "";
    public string Legs { get; init; } = "";
    public string[] LegOptions { get; init; } = [];
    public Dictionary<string,string[]> Targets { get; init; } = [];
    public CustomFitAsset Mesh { get; init; } = null!;
}
public sealed class CustomFitVariant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public CustomFitAsset? Mesh { get; init; }
    public CustomFitAsset? Classification { get; init; }
    public CustomFitEdit[] Edits { get; init; } = [];
    public CustomFitBaked[] Baked { get; init; } = [];
}
public sealed class CustomFitRecipe
{
    public int Schema { get; init; }
    public string Id { get; init; } = "";
    public string GamePath { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public CustomFitVariant? Default { get; init; }
    public CustomFitVariant[] Variants { get; init; } = [];
    public static bool HashValid(string hash)=>Regex.IsMatch(hash,@"\A[0-9a-f]{64}\z");
    public static bool IdValid(string id)=>Regex.IsMatch(id,@"\A[a-z0-9][a-z0-9-]{0,79}\z");
    public static bool GameValid(string path)=>StandaloneRefit.IsSupported(path,out _,out _) ||
        (path.Contains("/c0101e") && StandaloneRefit.IsSupported(path.Replace("/c0101e","/c0201e"),out _,out _));
    public static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static bool PathValid(string path)=>path.Length is >0 and <240 && !path.Contains('\\') &&
        !path.StartsWith('/') && path.Split('/').All(p=>Regex.IsMatch(p,@"\A[a-zA-Z0-9_.-]+\z") && p is not ("." or ".."));
    public static CustomFitRecipe Read(byte[] bytes)
    {
        var r=JsonSerializer.Deserialize<CustomFitRecipe>(bytes) ?? throw new InvalidDataException("Empty custom fit recipe.");
        if(r.Schema is not (1 or 2) || !IdValid(r.Id) || !HashValid(r.SourceHash) ||
            !GameValid(r.GamePath) || r.SourceKind is not ("vanilla" or "family") ||
            r.Variants==null || r.Variants.Length>32 || (r.Default==null && r.Variants.Length==0) ||
            r.Variants.Select(v=>v.Id).Distinct().Count()!=r.Variants.Length)
            throw new InvalidDataException("Invalid custom fit recipe.");
        foreach(var v in r.Variants.Concat(r.Default==null?[]:new[]{r.Default}))
        {
            if(!IdValid(v.Id) || string.IsNullOrWhiteSpace(v.Name) || v.Name.Length>100 || v.Edits==null || v.Edits.Length>4096 || v.Baked==null || v.Baked.Length>128 || (r.Schema==1 && v.Baked.Length!=0))
                throw new InvalidDataException("Invalid custom fit variant.");
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var f in v.Baked)
                if(r.SourceKind!="vanilla" || f.Chest is not ("XS" or "Small" or "Medium" or "Large") || f.Legs is not ("Medium" or "Large") ||
                    f.Mesh==null || f.LegOptions==null || f.LegOptions.Any(o=>o is not ("Hip Dips" or "Soft Butt")) ||
                    f.LegOptions.Distinct().Count()!=f.LegOptions.Length || f.Targets==null || f.Targets.Count!=2 ||
                    !f.Targets.ContainsKey("_top") || !f.Targets.ContainsKey("_dwn") ||
                    f.Targets.Values.Any(h=>h==null || h.Length is <1 or >8 || h.Any(x=>!HashValid(x))) ||
                    !keys.Add(f.Chest+"|"+f.Legs+"|"+string.Join('|',f.LegOptions.Order())))
                    throw new InvalidDataException("Invalid or ambiguous finished custom fit.");
            foreach(var a in new[]{v.Mesh,v.Classification}.OfType<CustomFitAsset>().Concat(v.Baked.Select(f=>f.Mesh)))
                if(!PathValid(a.Path) || !HashValid(a.Sha256)) throw new InvalidDataException("Invalid custom fit asset.");
            if(r.SourceKind=="family" && v.Classification!=null) throw new InvalidDataException("Classifications apply only to vanilla recipes.");
            foreach(var e in v.Edits)
                if(e.Mesh<0 || e.Vertices==null || e.Vertices.Length==0 || e.Vertices.Any(i=>i<0) ||
                    (e.DeltaMm==null && e.Weights==null) ||
                    (e.DeltaMm!=null && (e.DeltaMm.Length!=3 || e.DeltaMm.Any(x=>!float.IsFinite(x)))) ||
                    (e.Weights!=null && (e.Weights.Count==0 || !float.IsFinite(e.Weights.Values.Sum()) || e.Weights.Any(p=>string.IsNullOrEmpty(p.Key) || !float.IsFinite(p.Value) || p.Value<=0))))
                    throw new InvalidDataException("Invalid custom fit rule.");
        }
        return r;
    }

    public static CustomFitAsset? SelectBaked(CustomFitVariant plan,IReadOnlyDictionary<string,string> hashes,
        string chestOption,string legOption,string[] legOptions)
    {
        string chest=chestOption.Split(" - ")[^1];
        string? legs=legOption.Split(" - ").FirstOrDefault(s=>s is "Medium" or "Large");
        var options=legOptions.ToHashSet(StringComparer.Ordinal);
        return plan.Baked.SingleOrDefault(f=>f.Chest==chest && f.Legs==legs && options.SetEquals(f.LegOptions) &&
            f.Targets.All(p=>hashes.TryGetValue(p.Key,out var hash) && p.Value.Contains(hash,StringComparer.Ordinal)))?.Mesh;
    }

    // Reference-space edits run before subdivision and fitting. A replacement
    // may have entirely different topology but must remain in the source pose,
    // with the source body's rig/material references and matching equipment cuts.
    public static byte[] Apply(byte[] source,CustomFitVariant variant,Func<CustomFitAsset,byte[]> asset)
        =>ApplyCore(source,variant,asset,false);
    internal static byte[] ApplyBaked(byte[] source,CustomFitAsset mesh,Func<CustomFitAsset,byte[]> asset)
        =>ApplyCore(source,new CustomFitVariant{Mesh=mesh},asset,true);
    private static byte[] ApplyCore(byte[] source,CustomFitVariant variant,Func<CustomFitAsset,byte[]> asset,bool finished)
    {
        var result=(byte[])(variant.Mesh==null?source:asset(variant.Mesh)).Clone();
        var layout=SecondSkinWriter.Parse(result);
        if(ModelPartReader.Read(result)==null) throw new InvalidDataException("Unreadable custom reference mesh.");
        if(variant.Mesh!=null)
        {
            var original=SecondSkinWriter.Parse(source);
            bool materials=finished
                ? original.MatNames.Where(n=>!SecondSkinWriter.IsBodySkinMaterial(n)).SequenceEqual(layout.MatNames.Where(n=>!SecondSkinWriter.IsBodySkinMaterial(n)))
                : original.MatNames.SequenceEqual(layout.MatNames);
            if(!materials || original.AttrNames.Except(layout.AttrNames).Any() ||
                original.Shapes.Keys.Except(layout.Shapes.Keys).Any())
                throw new InvalidDataException("Custom reference mesh must preserve source material paths, attributes and equipment shape names.");
            for(int m=layout.Lod0MeshIndex;m<layout.Lod0MeshIndex+layout.Lod0MeshCount;m++)
            {
                int material=BitConverter.ToUInt16(result,layout.MeshStart+36*m+8);
                if(SecondSkinWriter.IsBodySkinMaterial(layout.MatNames[material]) || layout.Decls[m].First(e=>e.Usage==1).Type!=17) continue;
                bool authoredEight=Enumerable.Range(original.Lod0MeshIndex,original.Lod0MeshCount).Any(old=>
                    BitConverter.ToUInt16(source,original.MeshStart+36*old+8)==material && original.Decls[old].First(e=>e.Usage==1).Type==17);
                if(!authoredEight) throw new InvalidDataException("Custom reference widened a four-influence clothing material.");
            }
        }
        var weights=new Dictionary<int,Dictionary<int,Dictionary<string,float>>>();
        foreach(var group in variant.Edits.GroupBy(e=>e.Mesh))
        {
            int m=group.Key;
            if(m<layout.Lod0MeshIndex || m>=layout.Lod0MeshIndex+layout.Lod0MeshCount) throw new InvalidDataException("Custom fit mesh is out of range.");
            var draw=new GarmentClearance.Mesh(layout,m);
            var moves=new Dictionary<int,Vector3>();
            foreach(var edit in group)
            foreach(int v in edit.Vertices.Distinct())
            {
                if(v>=draw.P.Length) throw new InvalidDataException("Custom fit vertex is out of range.");
                if(edit.DeltaMm is {} d) moves[v]=moves.GetValueOrDefault(v)+new Vector3(d[0],d[1],d[2]);
                if(edit.Weights is {} w)
                {
                    int capacity=layout.Decls[m].First(e=>e.Usage==1).Type==17?8:4;
                    var available=layout.BoneTables[BitConverter.ToUInt16(result,layout.MeshStart+36*m+14)].Select(i=>layout.BoneNames[i]).ToHashSet();
                    if(w.Count>capacity || w.Keys.Any(n=>!available.Contains(n))) throw new InvalidDataException("Custom weights exceed the reference mesh's rig/format.");
                    if(!weights.TryGetValue(m,out var vertices)) weights[m]=vertices=[];
                    vertices[v]=w;
                    foreach(int shape in draw.ShapeTargets.GetValueOrDefault(v) ?? []) vertices[shape]=w;
                }
            }
            ModelPanelEdits.Write(result,layout,m,draw,moves);
        }
        return SurfaceSkinning.Apply(result,weights);
    }
}

public sealed record CustomFitBundle(CustomFitRecipe Recipe,string Revision,IReadOnlyDictionary<string,byte[]> Assets)
{
    public byte[] Asset(CustomFitAsset asset)
    {
        var bytes=Assets[asset.Sha256];
        if(CustomFitRecipe.Hash(bytes)!=asset.Sha256) throw new InvalidDataException("Custom fit asset checksum mismatch.");
        return bytes;
    }
}

// Immutable snapshots pin every conversion to the same downloaded recipe set.
public sealed class CustomFitLibrary
{
    public static readonly CustomFitLibrary Empty=new([]);
    public IReadOnlyList<CustomFitBundle> Bundles { get; }
    public string CatalogRevision { get; }
    public CustomFitLibrary(IReadOnlyList<CustomFitBundle> bundles)
    {Bundles=bundles;CatalogRevision=Revision(bundles.Select(b=>b.Recipe.GamePath));}
    public string Revision(IEnumerable<string> paths)
    {
        var wanted=paths.ToHashSet(StringComparer.Ordinal);
        var entries=Bundles.Where(b=>wanted.Contains(b.Recipe.GamePath)).OrderBy(b=>b.Recipe.Id).Select(b=>b.Recipe.Id+":"+b.Revision).ToArray();
        return entries.Length==0?"":CustomFitRecipe.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join('|',entries)));
    }
    public CustomFitBundle? Match(string path,string kind,byte[] source)
    {
        string hash=CustomFitRecipe.Hash(source);
        return Bundles.SingleOrDefault(b=>b.Recipe.GamePath==path && b.Recipe.SourceKind==kind && b.Recipe.SourceHash==hash);
    }
    // Finished AF meshes bypass the geometric solver only for their exact
    // reviewed source, native target body files and shape options.
    public byte[]? Baked(string path,string kind,byte[] source,IReadOnlyDictionary<string,string> targets,
        string chestOption,string legOption,string[] legOptions,CustomFitBundle? chosen=null,CustomFitVariant? variant=null)
    {
        var bundle=chosen??Match(path,kind,source);
        if(bundle==null) return null;
        if(bundle.Recipe.GamePath!=path || bundle.Recipe.SourceKind!=kind || bundle.Recipe.SourceHash!=CustomFitRecipe.Hash(source))
            throw new InvalidDataException("Finished custom fit does not match its source.");
        var plan=variant??bundle.Recipe.Default;
        if(plan==null || plan.Baked.Length==0) return null;
        var hashes=targets.Where(p=>p.Key is "_top" or "_dwn").ToDictionary(p=>p.Key,p=>CustomFitRecipe.Hash(File.ReadAllBytes(p.Value)));
        var mesh=CustomFitRecipe.SelectBaked(plan,hashes,chestOption,legOption,legOptions);
        return mesh==null?null:CustomFitRecipe.ApplyBaked(source,mesh,bundle.Asset);
    }
    public byte[] Prepare(string path,string kind,byte[] source,CustomFitBundle? chosen=null,CustomFitVariant? variant=null)
    {
        var bundle=chosen ?? Match(path,kind,source);
        if(bundle==null) return source;
        if(bundle.Recipe.GamePath!=path || bundle.Recipe.SourceKind!=kind || bundle.Recipe.SourceHash!=CustomFitRecipe.Hash(source))
            throw new InvalidDataException("Custom fit does not match this source model.");
        var edit=variant ?? bundle.Recipe.Default;
        return edit==null?source:CustomFitRecipe.Apply(source,edit,bundle.Asset);
    }
}
