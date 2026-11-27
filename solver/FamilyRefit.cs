using System.Numerics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Proteus.Services;
using XivLiveMesh;

namespace Aetherform.Geometry;

public static class FamilyRefit
{
    private sealed record Resolution(string Stamp,string Settings,IReadOnlyDictionary<string,string> Files);
    private static readonly ConcurrentDictionary<string,Resolution> Resolutions = new(StringComparer.OrdinalIgnoreCase);
    private sealed record GroupSnapshot(string Stamp,List<(string Name,JsonElement Group)> Groups,IReadOnlyDictionary<string,string> Defaults);
    private static readonly ConcurrentDictionary<string,GroupSnapshot> GroupSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,BodyModIndex> BodyIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,string[]> CompatibleBodies = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CatalogSnapshot(string Stamp,BodySizeCatalog Catalog);
    private static readonly ConcurrentDictionary<string,CatalogSnapshot> Catalogs = new(StringComparer.OrdinalIgnoreCase);

    private static string ManifestStamp(string directory) => string.Join('|',Directory.EnumerateFiles(directory,"*.json")
        .Order(StringComparer.OrdinalIgnoreCase).Select(p=>p+"|"+File.GetLastWriteTimeUtc(p).Ticks+"|"+new FileInfo(p).Length));

    private static BodySizeCatalog Catalog(string directory)
    {
        string stamp=ManifestStamp(directory);
        if(Catalogs.TryGetValue(directory,out var cached) && cached.Stamp==stamp) return cached.Catalog;
        var catalog=BodySizeCatalog.Read(directory);
        Catalogs[directory]=new(stamp,catalog);
        return catalog;
    }

    private static GroupSnapshot Groups(string directory)
    {
        string stamp=ManifestStamp(directory);
        if(GroupSnapshots.TryGetValue(directory,out var cached) && cached.Stamp==stamp) return cached;
        var manifest=StandaloneRefit.ReadManifest(directory);
        using var document=JsonDocument.Parse(manifest.ToJsonString());
        var groups=document.RootElement.GetProperty("Groups").EnumerateArray()
            .Where(g=>g.TryGetProperty("Name",out _)).Select(g=>(g.GetProperty("Name").GetString()!,g.Clone())).ToList();
        var defaults=new Dictionary<string,string>();
        if(document.RootElement.TryGetProperty("DefaultData",out var data) && data.TryGetProperty("Files",out var files))
            defaults=files.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!);
        var snapshot=new GroupSnapshot(stamp,groups,defaults);
        GroupSnapshots[directory]=snapshot;
        return snapshot;
    }

    public static string BodyRevision(string[] directories)
    {
        var versions=new List<string>();
        foreach(string directory in directories.Where(Directory.Exists))
        {
            versions.Add(ManifestStamp(directory));
            var catalog=Catalog(directory);
            foreach(string path in catalog.Options.Select(catalog.PathOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file=new FileInfo(path);
                versions.Add(file.Exists ? path+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks : path+"|missing");
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',versions))));
    }
    public static bool SaveEditable(string folder,string output,string body,string option,string shape,
        Dictionary<string,string> targets,string model,string sourceDirectory,string sourceFile)
    {
        var record=BodyRetargetWriter.ReadRecord(folder);
        var existing=record?.Options.FirstOrDefault(e=>e.Group=="Fit" && e.Name==option && e.GamePath==output);
        if(existing!=null && File.Exists(Path.Combine(folder,existing.File))) return false;
        var saved=BodyRetargetWriter.Save(folder,"Fit",output,body,"YAB/Rue body",
            [new(option,File.ReadAllBytes(model),shape)],auto:true,fromGame:false,
            provenance:new(sourceDirectory,sourceFile,targets));
        if(!saved.Ok) throw new IOException(saved.Message);
        return true;
    }
    public sealed record Input(string Directory, string File, string Revision,
        Dictionary<string,List<string>> Settings, string[] Bodies)
    {
        public Dictionary<string,string> Installed { get; init; } = [];
        public bool FamilyHint { get; init; } = true;
        public int SourcePriority { get; init; }
        public Func<byte[],byte[]>? Prepare { get; init; }
        public string PreparationKey { get; init; } = "";
        public bool PinSource { get; init; }
    }
    public sealed record Result(byte[] Model, string SourceFile, string Option, string Chest, string Legs,
        float DistanceMm, bool SameTopology,string BodyDirectory,string BodyFile,bool NoFitNeeded=false);
    private sealed record Body(BodyOption Option, string Path);
    private sealed record Choice(string Name, string File, byte[] Bytes, ModelParts Garment,
        Body Chest, Body Legs, float Distance, float GuideDistance,bool SameTopology,KeepEnds.Reading? Ends);

    // These groups vary anatomical fit. Other groups keep the collection's choices.
    private static readonly Regex SizeGroup = new(@"\b(size|sizes|breast|breasts|bust|chest|legs|thighs|body|build|yab|rue)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsBody(string name) => Regex.IsMatch(name,
        @"^(Yet Another Body\+?|YAB\+?|Rue\+?)(\s.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsGear(string name) => Regex.IsMatch(name,
        @"\b(yab|rue)\b|yet another", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string[] KnownBodyDirectories(string root) => CompatibleBodies.GetValueOrDefault(root,[]);

    private static string[] DiscoverBodies(Input input,IReadOnlyDictionary<string,string> targets)
    {
        if(input.Installed.Count==0) return input.Bodies;
        string root=Path.GetDirectoryName(input.Directory)!;
        var index=BodyIndexes.GetOrAdd(root,_=>new BodyModIndex());
        var result=input.Bodies.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in index.Refresh(input.Installed,root))
        {
            string directory=Path.Combine(root,entry.Dir);
            if(result.Contains(directory) || targets.Values.Any(p=>p.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))) continue;
            foreach(string slot in new[]{"_top","_dwn"})
            {
                var sample=entry.Catalog.For(slot,"0201").FirstOrDefault();
                if(sample==null || !targets.TryGetValue(slot,out var targetPath)) continue;
                string sourcePath=entry.Catalog.PathOf(sample);
                if(!File.Exists(sourcePath)) continue;
                byte[] sourceBytes=File.ReadAllBytes(sourcePath);
                byte[] targetBytes=File.ReadAllBytes(targetPath);
                var source=ModelPartReader.Read(sourceBytes);var target=ModelPartReader.Read(targetBytes);
                if(source!=null && target!=null && IdentityCorrespondence.TryBuild(source,target,slot,out _,out _,
                    BodyRetarget.Uv(sourceBytes),BodyRetarget.Uv(targetBytes)))
                {
                    result.Add(directory);
                    break;
                }
            }
        }
        var directories=result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        CompatibleBodies[root]=directories;
        return directories;
    }

    public static string[] Candidates(Input input, string gamePath)
        => Options(input, gamePath).Select(p=>p.File).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string? Resolve(string directory, string gamePath, Dictionary<string,List<string>> settings)
        => ResolveFiles(directory,settings).GetValueOrDefault(gamePath);

    public static IReadOnlyDictionary<string,string> ResolveFiles(string directory,Dictionary<string,List<string>> settings)
    {
        string stamp=ManifestStamp(directory);
        string selection=JsonSerializer.Serialize(settings.OrderBy(p=>p.Key).Select(p=>new {p.Key,Values=p.Value.Order().ToArray()}));
        if(Resolutions.TryGetValue(directory,out var previous) && previous.Stamp==stamp && previous.Settings==selection) return previous.Files;
        var resolved=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void AddFiles(IReadOnlyDictionary<string,string> files)
        {
            foreach(var (gamePath,file) in files) resolved.TryAdd(gamePath,Path.GetFullPath(Path.Combine(directory,file)));
        }
        var snapshot=Groups(directory);
        var groups = snapshot.Groups
            .Select((g,i)=>(g.Name,g.Group,Index:i))
            .OrderByDescending(g=>BodyRetargetWriter.GroupPriority(g.Group))
            .ThenByDescending(g=>g.Index);
        foreach (var group in groups)
        {
            if(group.Group.TryGetProperty("Type",out var groupType) && groupType.GetString()=="Imc") continue;
            var options = PenumbraModMeta.FileOptionsOf(group.Group) ?? [];
            var selected = settings.GetValueOrDefault(group.Name);
            if (selected == null)
            {
                ulong defaults = group.Group.TryGetProperty("DefaultSettings",out var value) ? value.GetUInt64() : 0;
                bool multi = group.Group.TryGetProperty("Type",out var kind) && kind.GetString() is "Multi" or "Combining";
                selected = options.Where((o,i)=>multi ? i<64 && (defaults & (1UL<<i))!=0 : (ulong)i==defaults)
                    .Select(o=>o.Name).ToList();
            }
            if(group.Group.TryGetProperty("Containers",out var containers))
            {
                ulong mask=0;
                for(int i=0;i<options.Count && i<64;i++) if(selected.Contains(options[i].Name)) mask|=1UL<<i;
                if(mask<(ulong)containers.GetArrayLength() && containers[(int)mask].TryGetProperty("Files",out var files))
                    AddFiles(files.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!));
                continue;
            }
            var raw=group.Group.TryGetProperty("Options",out var entries) ? entries.EnumerateArray().ToArray() : [];
            foreach (var option in options.Select((o,i)=>(Option:o,Index:i))
                .Where(o=>selected.Contains(o.Option.Name))
                .OrderByDescending(o=>o.Index<raw.Length ? BodyRetargetWriter.GroupPriority(raw[o.Index]) : 0).ThenBy(o=>o.Index))
                AddFiles(option.Option.Files);
        }
        AddFiles(snapshot.Defaults);
        Resolutions[directory]=new(stamp,selection,resolved);
        return resolved;
    }

    private static string Style(string name)
    {
        var withoutSizes=Regex.Replace(name,
            @"\b(yab|rue|xs|s|m|l|xl|small|medium|large|mini|yanilla|watermelon|skull|skullcrusher|skullcrushers|crushers?|booty|wc|sc|regular|(non[ -]?)?buff)\b",
            "",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        return Regex.Replace(withoutSizes,@"[\W_]+","").ToLowerInvariant();
    }

    private static List<(string Name,string File)> Options(Input input, string gamePath)
    {
        var result = new List<(string,string)> { ("Selected", input.File) };
        if(input.PinSource) return result;
        var snapshot=Groups(input.Directory);
        var groups=snapshot.Groups;
        bool defaultModel=snapshot.Defaults.TryGetValue(gamePath,out var defaultFile) &&
            string.Equals(Path.GetFullPath(Path.Combine(input.Directory,defaultFile)),Path.GetFullPath(input.File),StringComparison.OrdinalIgnoreCase);
        var anchors=groups.Where(g=>SizeGroup.IsMatch(g.Name)).SelectMany(g=>PenumbraModMeta.FileOptionsOf(g.Group) ?? [])
            .Where(o=>o.Files.TryGetValue(gamePath,out var path) && string.Equals(Path.GetFullPath(Path.Combine(input.Directory,path)),
                Path.GetFullPath(input.File),StringComparison.OrdinalIgnoreCase)).Select(o=>Style(o.Name)).ToHashSet();
        foreach (var group in groups)
        {
            var options = PenumbraModMeta.FileOptionsOf(group.Group) ?? [];
            if(group.Group.TryGetProperty("Containers",out var containers))
            {
                ulong active=group.Group.TryGetProperty("DefaultSettings",out var defaults) ? defaults.GetUInt64() : 0;
                if(input.Settings.TryGetValue(group.Name,out var flags))
                {
                    active=0;
                    for(int i=0;i<options.Count && i<64;i++) if(flags.Contains(options[i].Name)) active|=1UL<<i;
                }
                if(active>=(ulong)containers.GetArrayLength() || !containers[(int)active].TryGetProperty("Files",out var currentFiles) ||
                    !currentFiles.TryGetProperty(gamePath,out var current) ||
                    !string.Equals(Path.GetFullPath(Path.Combine(input.Directory,current.GetString()!)),Path.GetFullPath(input.File),StringComparison.OrdinalIgnoreCase)) continue;
                ulong sizes=0;
                for(int i=0;i<options.Count && i<64;i++)
                {
                    string name=options[i].Name;
                    bool anatomical=Style(name).Length==0 && Regex.IsMatch(name,@"[a-z]",RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(name,@"\b(breasts?|bust|chest|thighs?|hips?|butt|waist|legs?)\b",RegexOptions.IgnoreCase) &&
                        Regex.IsMatch(name,@"\b(size|small|medium|large|mini|narrow|wide|soft|dips)\b",RegexOptions.IgnoreCase);
                    if(anatomical) sizes|=1UL<<i;
                }
                for(int i=0;i<containers.GetArrayLength();i++)
                {
                    if(((ulong)i & ~sizes)!=(active & ~sizes)) continue;
                    if(!containers[i].TryGetProperty("Files",out var files) || !files.TryGetProperty(gamePath,out var file)) continue;
                    string label=string.Join(" + ",options.Where((o,j)=>j<64 && ((ulong)i & (1UL<<j))!=0).Select(o=>o.Name));
                    result.Add((group.Name+" / "+label,Path.Combine(input.Directory,file.GetString()!)));
                }
                continue;
            }
            if (!SizeGroup.IsMatch(group.Name)) continue;
            // A size group must contain the active model. This excludes unrelated
            // sizes for another piece and variants overridden by a style group.
            foreach (var option in options)
                if ((anchors.Contains(Style(option.Name)) || anchors.Count==0 && defaultModel && Style(option.Name).Length==0) &&
                    option.Files.TryGetValue(gamePath,out var file))
                    result.Add((group.Name+" / "+option.Name,Path.Combine(input.Directory,file)));
        }
        return result.DistinctBy(p=>p.Item2,StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Result Generate(Input input, string gamePath, string slot,
        IReadOnlyDictionary<string,string> targets, string[] legOptions, IPluginLog log,
        CancellationToken token = default, ushort modelRace = 201, byte[]? deformer = null, byte[]? skeleton = null)
        => GenerateCore(input,gamePath,slot,targets,legOptions,log,token,modelRace,deformer,skeleton,null);

    // The cache belongs to one standalone export and one target shape. Separate
    // size options often select the same closest source; solve that model once.
    public static Result GenerateCached(Input input,string gamePath,string slot,
        IReadOnlyDictionary<string,string> targets,string[] legOptions,IPluginLog log,
        CancellationToken token,ushort modelRace,byte[]? deformer,byte[]? skeleton,Dictionary<string,Result> cache)
    {
        token.ThrowIfCancellationRequested();
        // Gloves/shoes can be entirely protected. Keep their early identity
        // check specific to the requested model, including its nail/foot shape.
        if(slot is not ("_top" or "_dwn"))
            return GenerateCore(input,gamePath,slot,targets,legOptions,log,token,modelRace,deformer,skeleton,cache);
        var candidates=Options(input,gamePath).OrderBy(p=>p.File,StringComparer.OrdinalIgnoreCase).ToArray();
        var selected=SecondSkinWriter.Parse(File.ReadAllBytes(input.File));
        string signature=JsonSerializer.Serialize(new
        {
            slot,modelRace,input.FamilyHint,input.Revision,input.PreparationKey,input.PinSource,Candidates=candidates.Select(p=>p.File),
            Materials=selected.MatNames.Order(),Attributes=selected.AttrNames,Shapes=selected.Shapes.Keys.Order(),
            Bodies=input.Bodies.Order(),Targets=targets.OrderBy(p=>p.Key).Select(p=>new {p.Key,p.Value,Stamp=File.GetLastWriteTimeUtc(p.Value).Ticks}),
            LegOptions=legOptions.Order(),Deformer=deformer==null?"":Convert.ToHexString(SHA256.HashData(deformer)),
            Skeleton=skeleton==null?"":Convert.ToHexString(SHA256.HashData(skeleton))
        });
        string key="selection:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        if(cache.TryGetValue(key,out var reused))
            return reused with {Option=candidates.First(p=>string.Equals(p.File,reused.SourceFile,StringComparison.OrdinalIgnoreCase)).Name};
        var result=GenerateCore(input,gamePath,slot,targets,legOptions,log,token,modelRace,deformer,skeleton,cache);
        cache[key]=result;
        return result;
    }

    private static Result GenerateCore(Input input,string gamePath,string slot,
        IReadOnlyDictionary<string,string> targets,string[] legOptions,IPluginLog log,
        CancellationToken token,ushort modelRace,byte[]? deformer,byte[]? skeleton,Dictionary<string,Result>? cache)
    {
        if (slot is not ("_top" or "_dwn" or "_glv" or "_sho")) throw new InvalidDataException("Unsupported family refit slot.");
        bool keepEnds=slot is "_glv" or "_sho";
        if(keepEnds)
        {
            var original=File.ReadAllBytes(input.File);
            var garment=ModelPartReader.Read(original) ?? throw new InvalidDataException("Unreadable glove or shoe.");
            var reading=KeepEnds.Read(slot,garment,original) ?? throw new InvalidDataException("Unreadable hand or foot weights.");
            if(!reading.LimbView.Parts.Any(p=>p.Triangles.Length>0))
            {
                var prepared=input.Prepare?.Invoke(original)??original;
                var edited=ModelPartReader.Read(prepared) ?? throw new InvalidDataException("Unreadable custom hand/foot mesh.");
                var ends=KeepEnds.Read(slot,edited,prepared) ?? throw new InvalidDataException("Unreadable custom hand/foot weights.");
                if(ends.LimbView.Parts.Any(p=>p.Triangles.Length>0))
                    throw new InvalidDataException("A protected hand/foot reference cannot introduce an unsupported body-fit region.");
                return new(prepared,input.File,"Selected","","",0,false,"","",prepared.SequenceEqual(original));
            }
        }
        input=input with {Bodies=DiscoverBodies(input,targets)};
        var route=new RacialModelBake.Route(modelRace,201,true);
        RacialModelBake.Wearer? wearer=null;
        if(modelRace!=201)
        {
            var rig=skeleton==null ? null : HavokSkeleton.Read(skeleton);
            if(rig==null || deformer==null) throw new InvalidDataException("Missing source race skeleton or deformer.");
            wearer=new(modelRace,rig.ParentMap(),new PbdFile(deformer));
        }
        var bodies = new List<Body>();
        foreach (var directory in input.Bodies)
        {
            var catalog = Catalog(directory);
            foreach (var option in catalog.Options.Where(o=>o.Slot is "_top" or "_dwn" && BodySizeCatalog.RaceOf(o.GamePath)=="0201"))
                if (!BodySizeCatalog.IsShape(option)) bodies.Add(new(option,catalog.PathOf(option)));
        }
        if (bodies.Count==0) throw new InvalidDataException("Install a YAB or Rue source body to identify garment sizes.");
        var primarySlot = slot switch {"_glv"=>"_top","_sho"=>"_dwn",_=>slot};
        var targetBytes = targets.ToDictionary(p=>p.Key,p=>File.ReadAllBytes(p.Value));
        GarmentFit.ApplyLegShapes(targetBytes["_dwn"],legOptions);
        var uv = new UVRemapService(log,AppContext.BaseDirectory);
        var pairs = new Dictionary<(string,string),BodyRetarget.SlotPair>();
        BodyRetarget.SlotPair Pair(Body body,string part)
        {
            var key=(body.Path,part);
            if (pairs.TryGetValue(key,out var cached)) return cached;
            var refusal = BodyRetarget.BuildPair(part,File.ReadAllBytes(body.Path),targetBytes[part],part,false,null,null,uv,out var pair);
            if (refusal!=null) throw new InvalidDataException(refusal);
            pairs.Add(key,pair);
            return pair;
        }
        Body? Detect(ModelParts garment,string part,HashSet<string> bones,string? directory=null,bool allowGuess=true)
        {
            var choices = bodies.Where(b=>b.Option.Slot==part && (directory==null || b.Path.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(b=>b.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            if(choices.Length==0) return null;
            var options = choices.Select(b=>b.Option with { Rel=b.Path }).ToArray();
            if(!input.FamilyHint)
            {
                var probes=BodySizeMatch.ProbeKeys(garment);
                if(probes.Length==0) return null;
                float sourceShare=choices.Max(b=>BodySizeMatch.SharedIndexed(probes,b.Path));
                float targetShare=BodySizeMatch.SharedIndexed(probes,targets[part]);
                if(sourceShare<AutoRefitDecisions.MinShared || targetShare>=sourceShare-1e-5f) return null;
            }
            var ranking = BodySizeMatch.Rank(garment,options,o=>o.Rel,bones);
            if (!ranking.Preselect)
                ranking = BodySizeMatch.RankByCloth(garment,options,o=>o.Rel) ?? ranking;
            if (ranking.Best is not { } best) return null;
            if(!allowGuess && !ranking.Preselect) return null;
            // Nearest-cloth readings are useful for a covered secondary slot.
            // A primary skin reading must actually describe the source family.
            if (!ranking.FromCloth && best.Rms>.012f) return null;
            return choices.First(b=>b.Path==best.Option.Rel);
        }
        float Distance(BodyRetarget.SlotPair pair)
        {
            var field=pair.Correspondence.Field.Where(p=>p.HasValue).Select(p=>p!.Value.LengthSquared()).ToArray();
            return field.Length==0 ? float.PositiveInfinity : MathF.Sqrt(field.Average());
        }
        var fields=new Dictionary<(string Chest,string Legs),BodyRetarget.SourceBody>();
        float Movement(ModelParts garment,Body chest,Body legs)
        {
            var key=(chest.Path,legs.Path);
            if(!fields.TryGetValue(key,out var field))
            {
                field=BodyRetarget.SourceBody.Build([Pair(chest,"_top"),Pair(legs,"_dwn")]);
                fields.Add(key,field);
            }
            var positions=garment.Positions;
            Vector3 At(int vertex)=>new(positions[3*vertex],positions[3*vertex+1],positions[3*vertex+2]);
            double areaSum=0,error=0;
            foreach(var part in garment.Parts.Where(p=>p.Island<0))
            for(int t=0;t<part.Triangles.Length;t+=3)
            {
                var a=At(part.Triangles[t]);var b=At(part.Triangles[t+1]);var c=At(part.Triangles[t+2]);
                double area=Vector3.Cross(b-a,c-a).Length();
                areaSum+=area;
                if(!field.TryNearest((a+b+c)/3,BodyRetarget.FarBand,out var delta,out float distance)) continue;
                float u=Math.Clamp((distance-BodyRetarget.NearBand)/(BodyRetarget.FarBand-BodyRetarget.NearBand),0,1);
                float follow=1-u*u*(3-2*u);
                error+=area*delta.LengthSquared()*follow*follow;
            }
            return areaSum>0 ? (float)Math.Sqrt(error/areaSum) : float.PositiveInfinity;
        }
        Choice? chosen=null;
        var selected=SecondSkinWriter.Parse(File.ReadAllBytes(input.File));
        var selectedMaterials=selected.MatNames.Order().ToArray();
        foreach (var (name,file) in Options(input,gamePath).OrderBy(p=>p.File,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if(!File.Exists(file)) continue;
            var bytes=File.ReadAllBytes(file);
            if(wearer!=null) bytes=route.Into(bytes,wearer,out var refusal) ?? throw new InvalidDataException(refusal);
            var layout=SecondSkinWriter.Parse(bytes);
            if (!layout.MatNames.Order().SequenceEqual(selectedMaterials)) continue;
            if(!selected.AttrNames.SequenceEqual(layout.AttrNames) ||
                selected.Shapes.Keys.Except(layout.Shapes.Keys,StringComparer.Ordinal).Any()) continue;
            var garment=ModelPartReader.Read(bytes);
            if (garment==null) continue;
            var ends=keepEnds ? KeepEnds.Read(slot,garment,bytes) : null;
            if(keepEnds && ends==null) continue;
            var view=ends?.LimbView ?? garment;
            var bones=layout.BoneNames.ToHashSet(StringComparer.Ordinal);
            var primary=Detect(view,primarySlot,bones);
            if(primary==null) continue;
            string sourceRoot=input.Bodies.First(root=>primary.Path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            var chest=primarySlot=="_top" ? primary : Detect(view,"_top",bones,sourceRoot,false);
            var legs=primarySlot=="_dwn" ? primary : Detect(view,"_dwn",bones,sourceRoot,false);
            // The companion body comes from the detected family's own catalogue.
            Body Companion(string part)
            {
                var family=bodies.Where(b=>b.Option.Slot==part &&
                input.Bodies.Any(root=>primary.Path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) &&
                    b.Path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))).ToArray();
                var companion=AutoRefitDecisions.Companion(family.Select(b=>b.Option).ToArray(),primary.Option,null);
                return companion==null ? throw new InvalidDataException("The source body lacks its companion slot.") : family.First(b=>b.Option==companion);
            }
            chest ??= Companion("_top"); legs ??= Companion("_dwn");
            var primaryPair=Pair(primary,primarySlot);
            // Rank the garment's own region. A secondary chest guide must not
            // demote a skirt whose source legs map directly to AF (and vice versa).
            bool direct=primaryPair.Correspondence.SameMesh;
            float guideDistance=Distance(primaryPair);
            float distance=Movement(view,chest,legs);
            if (chosen==null || FamilyRefitOrder.Prefer(direct,distance,guideDistance,chosen.SameTopology,chosen.Distance,chosen.GuideDistance))
                chosen=new(name,file,bytes,garment,chest,legs,distance,guideDistance,direct,ends);
        }
        if (chosen==null) throw new InvalidDataException("No gear option could be identified against the installed YAB/Rue bodies.");
        if(input.Prepare!=null)
        {
            var bytes=input.Prepare(File.ReadAllBytes(chosen.File));
            if(wearer!=null) bytes=route.Into(bytes,wearer,out var refusal) ?? throw new InvalidDataException(refusal);
            var originalLayout=SecondSkinWriter.Parse(chosen.Bytes);var editedLayout=SecondSkinWriter.Parse(bytes);
            if(!originalLayout.MatNames.SequenceEqual(editedLayout.MatNames) || originalLayout.AttrNames.Except(editedLayout.AttrNames).Any() ||
                originalLayout.Shapes.Keys.Except(editedLayout.Shapes.Keys).Any())
                throw new InvalidDataException("A custom reference mesh lost source materials, attributes or equipment shapes.");
            var edited=ModelPartReader.Read(bytes) ?? throw new InvalidDataException("Unreadable custom family mesh.");
            chosen=chosen with {Bytes=bytes,Garment=edited,Ends=keepEnds?KeepEnds.Read(slot,edited,bytes):null};
        }
        string solvedKey="solved:"+string.Join('|',slot,modelRace,Convert.ToHexString(SHA256.HashData(chosen.Bytes)),chosen.Chest.Path,chosen.Legs.Path,
            string.Join('|',targetBytes.OrderBy(p=>p.Key).Select(p=>p.Key+Convert.ToHexString(SHA256.HashData(p.Value)))),
            deformer==null ? "" : Convert.ToHexString(SHA256.HashData(deformer)),skeleton==null ? "" : Convert.ToHexString(SHA256.HashData(skeleton)));
        if(cache!=null && cache.TryGetValue(solvedKey,out var reused))
            return reused with {SourceFile=chosen.File,Option=chosen.Name};
        // Refine in the source pose, before solving. New vertices then sample
        // the body deformation themselves instead of staying on flat fitted faces.
        // Existing cuts remain sewn and authored hands/feet are never subdivided.
        var pinned=chosen.Garment.MeshSpans.ToDictionary(span=>span.Mesh,span=>(IReadOnlySet<int>)(chosen.Ends?.Ends ?? new HashSet<int>())
            .Where(v=>v>=span.BaseVertex && v<span.BaseVertex+span.Count).Select(v=>v-span.BaseVertex).ToHashSet());
        var refined=DensitySubdivision.Apply(chosen.Bytes,chosen.Bytes,
            new BodyDensity(targetBytes.Where(p=>p.Key is "_top" or "_dwn").Select(p=>p.Value)),[],held:pinned);
        var refinedGarment=ModelPartReader.Read(refined) ?? throw new InvalidDataException("Refined family garment failed read-back.");
        var refinedEnds=keepEnds ? KeepEnds.Read(slot,refinedGarment,refined) ??
            throw new InvalidDataException("Refined garment lost its hand/foot bindings.") : null;
        chosen=chosen with {Bytes=refined,Garment=refinedGarment,Ends=refinedEnds};
        token.ThrowIfCancellationRequested();
        BodyRetarget.SlotPair[] selectedPairs=[Pair(chosen.Chest,"_top"),Pair(chosen.Legs,"_dwn")];
        // The source's skin carries its option shapes and cuts. Move it through
        // correspondence and adapt weights without the legacy rebuild, which
        // discards all shape-replacement tables even on a weights-only rebuild.
        var solved=BodyRetarget.Solve(chosen.Garment,selectedPairs,slot,
            held:chosen.Ends?.Ends ?? new HashSet<int>(),replaceSkin:false,clearBody:false,
            keepShape:chosen.Ends?.FreePieces ?? RefitCore.ShapePieces(chosen.Garment,RefitCore.DefaultKeepShape(chosen.Garment),out _));
        var fitted=SurfaceSkinning.PromoteInfluences(MeshVolumeService.Inflate(chosen.Bytes,solved.Edit).Model);
        var sourcePoints=new List<Vector3>();var targetPoints=new List<Vector3>();
        foreach(var pair in selectedPairs)
        {
            var source=pair.Correspondence.Source;
            foreach(var part in source.Parts.Where(p=>p.Island<0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach(int vertex in part.Triangles)
            {
                var point=new Vector3(source.Positions[3*vertex],source.Positions[3*vertex+1],source.Positions[3*vertex+2]);
                sourcePoints.Add(1000*point);
                targetPoints.Add(1000*(point+(pair.Correspondence.Field[vertex] ?? Vector3.Zero)));
            }
        }
        var supportSurface=new ClearanceSurface(sourcePoints.ToArray());
        (Vector3 Source,Vector3 Target,float Weight)[] Support(Vector3 point)
        {
            int t=3*supportSurface.Nearest(point).Face;
            BrushTransfer.ClosestOnTriangle(point,sourcePoints[t],sourcePoints[t+1],sourcePoints[t+2],out float a,out float b,out float c);
            float[] weights=[a,b,c];
            return Enumerable.Range(0,3).Select(j=>(sourcePoints[t+j],targetPoints[t+j],weights[j])).ToArray();
        }
        var held=chosen.Garment.MeshSpans.ToDictionary(span=>span.Mesh,span=>(IReadOnlySet<int>)(chosen.Ends?.Ends ?? new HashSet<int>())
            .Where(v=>v>=span.BaseVertex && v<span.BaseVertex+span.Count).Select(v=>v-span.BaseVertex).ToHashSet());
        var weighted=BodyMotionTransfer.Apply(chosen.Bytes,fitted,Support,
            selectedPairs.Select(p=>p.SourceModel!),targetBytes,true,held);
        weighted=SkinSurfaceContact.Apply(chosen.Bytes,weighted,held:held);
        weighted=SkirtMotionClearance.Apply(chosen.Bytes,weighted,targetBytes,
            selectedPairs.Select(p=>p.SourceModel!),log,held:held);
        // Geometry and weights change while the original UVs and material paths
        // keep the source mod's texture and shader options connected.
        var model=weighted;
        if(ModelLodTrimmer.LodCount(model)>1)
            model=ModelLodTrimmer.KeepLod0(model,out var lodRefusal) ?? throw new InvalidDataException(lodRefusal);
        if(wearer!=null) model=route.Back(model,wearer,out var refusal) ?? throw new InvalidDataException(refusal);
        if (ModelPartReader.Read(model)==null) throw new InvalidDataException("Generated family refit failed read-back.");
        var emitted=SecondSkinWriter.Parse(model);
        var missingAttributes=selected.AttrNames.Except(emitted.AttrNames,StringComparer.Ordinal).ToArray();
        var missingShapes=selected.Shapes.Keys.Except(emitted.Shapes.Keys,StringComparer.Ordinal).ToArray();
        if(missingAttributes.Length>0 || missingShapes.Length>0)
            throw new InvalidDataException("The conversion lost option attributes ["+string.Join(", ",missingAttributes)+"] and shapes ["+string.Join(", ",missingShapes)+"].");
        string sourceBody=primarySlot=="_top" ? chosen.Chest.Path : chosen.Legs.Path;
        string sourceDirectory=input.Bodies.First(root=>sourceBody.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
        var result=new Result(model,chosen.File,chosen.Name,chosen.Chest.Option.Label,chosen.Legs.Option.Label,
            chosen.Distance*1000,chosen.SameTopology,sourceDirectory,Path.GetRelativePath(sourceDirectory,sourceBody));
        if(cache!=null) cache[solvedKey]=result;
        return result;
    }
}
