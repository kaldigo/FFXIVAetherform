using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetherform.Geometry;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Aetherform;

internal sealed class EquipRefits : IDisposable
{
    private string synced="";
    private int priority;
    private static readonly Regex Equipment = new(@"^chara/equipment/e(?<set>\d{4})/model/c(?<race>\d{4})e\k<set>(?<slot>_top|_dwn|_glv|_sho)\.mdl$", RegexOptions.Compiled);
    private static readonly Dictionary<string,string> Groups = new()
    {
        ["_top"] = "Chest - Smallclothes", ["_dwn"] = "Legs - Smallclothes",
        ["_glv"] = "Hands - Smallclothes", ["_sho"] = "Feet - Smallclothes",
    };

    private readonly IDalamudPluginInterface pi;
    private readonly IDataManager data;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly string cache;
    private readonly string familyCache;
    private readonly ClassificationStore classifications;
    private readonly string gameVersion;
    private string manifestPath = "";
    private DateTime manifestTime;
    private Dictionary<string,Dictionary<string,string>> choices = [];
    private readonly Dictionary<string,string> defaults = [];
    private readonly HashSet<string> failures = [];
    private sealed record Item(string Path, string Output, string Slot, ushort Race, ClassificationStore.Entry Classification,
        FamilyRefit.Input? Family = null)
    {
        internal string Revision => Family?.Revision ?? Classification.Revision;
    }
    private Dictionary<string,string> installed = [];
    private Dictionary<string,(bool Enabled,int Priority,Dictionary<string,List<string>> Options,bool Inherited,bool Temporary)> collectionSettings = [];
    private readonly Dictionary<(string Directory,string Path),FamilyRefit.Input?> familySources = [];
    private readonly Dictionary<string,FamilyRefit.Input?> winningFamilySources = [];
    private readonly Dictionary<string,IReadOnlyDictionary<string,string>> resolvedSources = [];
    private string? sourceBodyRevision;
    private readonly Dictionary<string,(long Checked,string Revision)> sourceRevisions = [];
    private sealed record Snapshot(Guid Collection, string Root, string Shape, Dictionary<string,string> Targets,
        string ChestOption, string LegOption, string[] LegOptions, string BodyDirectory, string Preset, Item[] Items, string Key);
    private sealed record Fit(Item Item, string Model);
    private sealed record Export(string Directory, FamilyRefit.Input Source, int Priority);
    private sealed record Batch(Snapshot Inputs, List<Fit> Fits, List<Export> Exports, List<(string Item, string Reason)> Errors);
    private Task<Batch>? job;
    private CancellationTokenSource? jobStop;
    private string? jobKey;
    private readonly CancellationTokenSource stop = new();
    private Func<int,int,string?>? names;
    private string committed = "";
    private readonly HashSet<Guid> migrated = [];
    private readonly HashSet<string> organizedFamilyMods = [];
    public string Status { get; private set; } = "";

    public EquipRefits(IDalamudPluginInterface pi, IDataManager data, IObjectTable objects, IPluginLog log, string config)
    {
        this.pi = pi; this.data = data; this.objects = objects; this.log = log;
        classifications = new(config, message => log.Warning("{Message}",message));
        cache = VanillaToAetherform.Cache(config);
        familyCache=Path.Combine(config,"refits","YAB-Rue","Aetherform");
        var versionFile = Path.Combine(data.GameData.DataPath.FullName, "..", "ffxivgame.ver");
        gameVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : data.GameData.DataPath.LastWriteTimeUtc.Ticks.ToString();
    }

    private static string Hash(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private static string Bare(string slot) => VanillaToAetherform.Bare(slot);
    private static bool GeneratedFit(string directory) => directory.StartsWith("AF Vanilla Fit ",StringComparison.Ordinal) ||
        directory.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal) || directory.StartsWith("AF Family Fit ",StringComparison.Ordinal) || directory.StartsWith("Vanilla to Aetherform ",StringComparison.Ordinal);

    private void ReadChoices(string folder)
    {
        var path = Path.Combine(folder, "meta.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        if (manifestPath == path && manifestTime == stamp) return;
        var found = new Dictionary<string,Dictionary<string,string>>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var (slot, groupName) in Groups)
        {
            var group = doc.RootElement.GetProperty("Groups").EnumerateArray().First(g => g.GetProperty("Name").GetString() == groupName);
            var options = group.GetProperty("Options");
            var list = new Dictionary<string,string>();
            foreach (var option in options.EnumerateArray())
                if (option.TryGetProperty("Files", out var files) && files.TryGetProperty(Bare(slot), out var file))
                    list.Add(option.GetProperty("Name").GetString()!, Path.Combine(folder, file.GetString()!));
            found[slot] = list;
            defaults[slot] = options[group.TryGetProperty("DefaultSettings", out var setting) ? setting.GetInt32() : 0].GetProperty("Name").GetString()!;
        }
        choices = found; manifestPath = path; manifestTime = stamp;
    }

    public void Update(string root, string? body, HashSet<Guid> disabled,int configuredPriority)
    {
        priority=configuredPriority;
        classifications.Poll();
        var current = Capture(root, body, disabled, out bool drawing);
        if (drawing) return;
        if (job != null && jobKey != current?.Key) jobStop?.Cancel();

        if (job is { IsCompleted: true })
        {
            try
            {
                var batch = job.GetAwaiter().GetResult();
                if (jobStop?.IsCancellationRequested == false && batch.Inputs.Key == current?.Key)
                {
                    Install(batch);
                    foreach (var (path, reason) in batch.Errors)
                        log.Warning("Automatic refit skipped {Path}: {Reason}", path, reason);
                }
            }
            catch (OperationCanceledException) when (jobStop?.IsCancellationRequested == true) { }
            finally { job = null; jobStop?.Dispose(); jobStop = null; jobKey = null; }
        }
        if (current == null) { Status = ""; committed=""; return; }
        if (current.Items.Length == 0)
        {
            committed="";
            Status = "Penumbra is applying the available fits.";
            return;
        }
        if (current.Key == committed || job != null) return;
        Status = current.Items.Any(i=>i.Family!=null) ? "Fitting gear and option variants…" : "Fitting equipped gear…";
        jobStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        jobKey = current.Key;
        var token = jobStop.Token;
        job = Task.Run(() => Generate(current, token), token);
    }

    private Snapshot? Capture(string root, string? body, HashSet<Guid> disabled, out bool drawing)
    {
        drawing = false;
        var player = objects.LocalPlayer;
        if (player == null) return null;
        var (valid, _, (id, _)) = pi.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if (!valid || id == Guid.Empty) return null;
        Snapshot? Inactive() { SyncOptions(root,id,null,null); return null; }
        if(body==null || disabled.Contains(id) || player.Customize[1]!=1) return Inactive();
        ushort wearerRace = VanillaToAetherform.Race(player.Customize[0], player.Customize[4]);
        if(wearerRace==0) return Inactive();
        var (code, setting) = pi.GetIpcSubscriber<Guid,string,string,bool,bool,int,
            (int,(bool,int,Dictionary<string,List<string>>,bool,bool)?)>("Penumbra.GetCurrentModSettingsWithTemp").InvokeFunc(id, body, "", false, false, 0);
        if (code != 0 || setting is not { Item1: true } effective) return Inactive();
        ReadChoices(Path.Combine(root,body));
        var targets = new Dictionary<string,string>();
        var labels = new Dictionary<string,string>();
        foreach (var (slot, group) in Groups)
        {
            var selected = effective.Item3.GetValueOrDefault(group)?.FirstOrDefault() ?? defaults[slot];
            if (choices[slot].TryGetValue(selected, out var path)) targets[slot] = path;
            if (slot is "_top" or "_dwn") labels[slot] = selected;
        }
        bool Supplied(string slot) => VanillaToAetherform.Supplies(slot, wearerRace, targets, path => Resolved(id,path));
        if (!Supplied("_top") || !Supplied("_dwn")) return Inactive();
        foreach (string end in new[] { "_glv", "_sho" })
            if (!Supplied(end)) targets.Remove(end);
        var legs = effective.Item3.GetValueOrDefault("Leg Options")?.Order().ToArray() ?? [];
        var shape = Hash("Vanilla/Aetherform/native-skin-v2|" + gameVersion + "|" + string.Join('|', legs) + "|" +
            string.Join('|', targets.Select(t => t.Key + t.Value + File.GetLastWriteTimeUtc(t.Value).Ticks)));
        var preset=string.Join(" / ",new[]{labels["_top"],labels["_dwn"]}.Concat(legs));
        installed = pi.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        var (settingsCode,allSettings) = pi.GetIpcSubscriber<Guid,bool,bool,int,
            (int,Dictionary<string,(bool,int,Dictionary<string,List<string>>,bool,bool)>?)>("Penumbra.GetAllModSettings")
            .InvokeFunc(id,false,false,0);
        if(settingsCode!=0 || allSettings==null) return Inactive();
        collectionSettings=allSettings;
        familySources.Clear();
        winningFamilySources.Clear();
        resolvedSources.Clear();
        sourceBodyRevision=null;
        if(SyncOptions(root,id,shape,preset)) { drawing=true; return null; }
        var tree = pi.GetIpcSubscriber<ushort[],Dictionary<string,HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
            .InvokeFunc([0]).FirstOrDefault();
        if (tree == null || !tree.Values.Any(paths => paths.Any(p => p.StartsWith("chara/equipment/", StringComparison.Ordinal) && p.EndsWith(".mdl", StringComparison.Ordinal))))
        {
            drawing = true;
            return null;
        }
        string legacy = Path.Combine(root,"Vanilla to Aetherform " + id.ToString("N")) + Path.DirectorySeparatorChar;
        if (!migrated.Contains(id) && tree.Any(row => Path.IsPathFullyQualified(row.Key) &&
            Path.GetFullPath(row.Key).StartsWith(legacy,StringComparison.OrdinalIgnoreCase) &&
            row.Value.Any(path => Equipment.IsMatch(path) && Vanilla(id,path))))
        {
            migrated.Add(id);
            pi.GetIpcSubscriber<int,int,object>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
            drawing = true;
            return null;
        }
        var pieces = new Dictionary<string,Item>();
        foreach (var (actual, paths) in tree)
        foreach (var path in paths)
        {
            var m = Equipment.Match(path);
            if (!m.Success || m.Groups["set"].Value is "0000" or "9903") continue;
            var slot = m.Groups["slot"].Value;
            ushort race = ushort.Parse(m.Groups["race"].Value);
            if (Path.IsPathFullyQualified(actual))
            {
                if (race is not (201 or 401 or 601 or 801 or 1001 or 1401 or 1601 or 1801)) continue;
                var relative=Path.GetRelativePath(root,actual);
                var directory=relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)[0];
                if(directory.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal))
                {
                    string manifest=Path.Combine(root,directory,"meta.json");
                    if(!File.Exists(manifest)) continue;
                    using var standalone=JsonDocument.Parse(File.ReadAllBytes(manifest));
                    var metadata=standalone.RootElement;
                    if(!metadata.TryGetProperty("Identifier",out var identifier) || identifier.GetGuid()!=StandaloneRefit.Identity(directory) ||
                        !metadata.TryGetProperty("AetherformSource",out var original)) continue;
                    string originalDirectory=original.GetString()!;
                    if(!Directory.Exists(Path.Combine(root,originalDirectory))) continue;
                    var active=collectionSettings.GetValueOrDefault(directory).Options ?? [];
                    var input=FamilySource(root,originalDirectory,path,true,active);
                    if(input!=null && (metadata.GetProperty("AetherformShape").GetString()!=shape ||
                        metadata.GetProperty("AetherformRevision").GetString()!=input.Revision))
                        pieces[path]=new(path,path,slot,race,ClassificationStore.Unclassified,
                            input with {SourcePriority=collectionSettings.GetValueOrDefault(directory).Priority});
                    continue;
                }
                var source=FamilySource(root,directory,path);
                if(source!=null && string.Equals(source.File,actual,StringComparison.OrdinalIgnoreCase))
                    pieces[path]=new(path,path,slot,race,ClassificationStore.Unclassified,source);
                continue;
            }
            if (!targets.ContainsKey(slot)) continue;
            if (!actual.Equals(path, StringComparison.OrdinalIgnoreCase) || !Vanilla(id, path)) continue;
            if (race != 101 && race is not (201 or 401 or 601 or 801 or 1001 or 1401 or 1601 or 1801)) continue;
            string output = race == 101 ? path.Replace("/c0101e", "/c0201e") : path;
            if (output != path && !Vanilla(id, output)) continue;
            pieces[output] = new(path, output, slot, race, classifications.Get(path));
        }
        var items = pieces.Values.OrderBy(x => x.Path).ToArray();
        var key = Hash(id + "|" + root + "|" + shape + "|" + string.Join('|',items.Select(i => i.Path + i.Revision + JsonSerializer.Serialize(i.Family?.Settings) + i.Family?.SourcePriority)));
        return new(id, root, shape, targets, labels["_top"], labels["_dwn"], legs, body, preset, items, key);
    }

    private static string Option(string preset,string shape,string slot,string revision,bool family=false) =>
        preset.Replace(" / ", " + ") + " [" + shape[..12] + "]" + (family ? " Family Fit 1 " : slot switch
        {
            "_top" or "_dwn" => " Motion Fit 1 ", "_glv" => " Skin Contact 4 ", "_sho" => " Skin Contact 1 ", _ => " "
        }) + revision[..Math.Min(12,revision.Length)];

    private bool SyncOptions(string root,Guid collection,string? shape,string? preset)
    {
        string key=root+"|"+collection+"|"+shape+"|"+priority+"|"+classifications.Generation;
        bool changed=false;
        var mods=pi.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        foreach(string directory in mods.Keys.Where(d=>d.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal) ||
            d.StartsWith("AF Family Fit ",StringComparison.Ordinal)))
        {
            if(organizedFamilyMods.Contains(Path.Combine(root,directory))) continue;
            string file=Path.Combine(root,directory,"meta.json");
            if(!File.Exists(file)) continue;
            using var metadata=JsonDocument.Parse(File.ReadAllBytes(file));
            if(metadata.RootElement.TryGetProperty("Identifier",out var identifier) && identifier.GetGuid()==StandaloneRefit.Identity(directory) &&
                (metadata.RootElement.TryGetProperty("AetherformSource",out _) || metadata.RootElement.TryGetProperty("FamilySourceDirectory",out _)))
                PlaceFamilyMod(root,directory);
        }
        var familyStates=new Dictionary<string,FamilyRefit.Input?>();
        foreach(var dir in mods.Keys.Where(s=>s.StartsWith("AF Family Fit ",StringComparison.Ordinal)))
        {
            string file=Path.Combine(root,dir,"meta.json");
            if(!File.Exists(file)) continue;
            using var meta=JsonDocument.Parse(File.ReadAllBytes(file));
            var identity=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(dir)).AsSpan(0,16));
            if(!meta.RootElement.TryGetProperty("Identifier",out var identifier) || identifier.GetGuid()!=identity ||
                !meta.RootElement.TryGetProperty("FamilySourceDirectory",out _) || !meta.RootElement.TryGetProperty("FamilyGamePath",out _)) continue;
            var sourceDirectory=meta.RootElement.GetProperty("FamilySourceDirectory").GetString()!;
            var gamePath=meta.RootElement.GetProperty("FamilyGamePath").GetString()!;
            var winner=shape==null ? null : WinningFamilySource(root,gamePath);
            familyStates[dir]=winner!=null && string.Equals(Path.GetFileName(winner.Directory),sourceDirectory,StringComparison.OrdinalIgnoreCase) ? winner : null;
            key+="|"+dir+familyStates[dir]?.Revision;
        }
        // A newly enabled source refit also supersedes a cached vanilla output.
        key+="|"+Hash(string.Join('|',collectionSettings.Where(p=>p.Value.Enabled &&
            (!GeneratedFit(p.Key) || p.Key.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal)))
            .OrderBy(p=>p.Key).Select(p=>p.Key+p.Value.Priority+JsonSerializer.Serialize(p.Value.Options))));
        if(key==synced) return false;
        foreach(var dir in mods.Keys.Where(s=>s.StartsWith("AF Vanilla Fit ",StringComparison.Ordinal) || familyStates.ContainsKey(s)))
        {
            var file=Path.Combine(root,dir,"meta.json");
            if(!File.Exists(file)) continue;
            using var doc=JsonDocument.Parse(File.ReadAllBytes(file));
            var meta=doc.RootElement;
            var identity=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(dir)).AsSpan(0,16));
            if(!meta.TryGetProperty("Identifier",out var identifier) || identifier.GetGuid()!=identity) continue;
            string? selected=null;
            int fitPriority=priority;
            if(shape!=null && meta.TryGetProperty("Groups",out var groups))
            {
                bool family=familyStates.TryGetValue(dir,out var source);
                string revision=family ? source?.Revision ?? "unavailable" : classifications.Get(dir["AF Vanilla Fit ".Length..]+".mdl").Revision;
                string option=Option(preset!,shape,dir[^4..],revision,family);
                if(source!=null) fitPriority=FamilyPriority(source);
                if(family) option=""; // Migrate legacy model-only overlays to standalone exports.
                if(!family)
                {
                    var stem=dir["AF Vanilla Fit ".Length..];
                    string gamePath=$"chara/equipment/e{stem.Substring(6,4)}/model/{stem}.mdl";
                    if(WinningFamilySource(root,gamePath)!=null || StandaloneSupplies(root,gamePath)) option="";
                }
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.GetProperty("Name").GetString() != "Fit") continue;
                    foreach (var candidate in group.GetProperty("Options").EnumerateArray())
                    {
                        if (candidate.GetProperty("Name").GetString() != option ||
                            !candidate.TryGetProperty("Files",out var files)) continue;
                        var paths=files.EnumerateObject().Select(f=>Path.Combine(root,dir,f.Value.GetString()!)).ToArray();
                        if (paths.Length>0 && paths.All(File.Exists)) selected=option;
                    }
                    break;
                }
            }
            changed |= ApplySettings(collection,dir,selected,fitPriority);
        }
        synced=key;
        if(changed) pi.GetIpcSubscriber<int,int,object>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        return changed;
    }

    private int FamilyPriority(FamilyRefit.Input source)
    {
        int sourcePriority=collectionSettings.GetValueOrDefault(Path.GetFileName(source.Directory)).Priority;
        return Math.Max(priority,sourcePriority==int.MaxValue ? int.MaxValue : sourcePriority+1);
    }

    private bool StandaloneSupplies(string root,string gamePath)
    {
        foreach(var (directory,settings) in collectionSettings.Where(p=>p.Value.Enabled && p.Key.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal)))
        {
            if(!resolvedSources.TryGetValue(directory,out var files))
                resolvedSources[directory]=files=FamilyRefit.ResolveFiles(Path.Combine(root,directory),settings.Options);
            if(files.ContainsKey(gamePath)) return true;
        }
        return false;
    }

    private bool ApplySettings(Guid collection,string dir,string? option,int? requestedPriority=null)
    {
        var (code,current)=pi.GetIpcSubscriber<Guid,string,string,bool,bool,int,
            (int,(bool,int,Dictionary<string,List<string>>,bool,bool)?)>("Penumbra.GetCurrentModSettingsWithTemp").InvokeFunc(collection,dir,"",false,true,0);
        Ok(code);
        bool changed=false;
        int fitPriority=requestedPriority ?? priority;
        if(option!=null)
        {
            if(current?.Item2!=fitPriority)
            { Ok(pi.GetIpcSubscriber<Guid,string,string,int,int>("Penumbra.TrySetModPriority.V5").InvokeFunc(collection,dir,"",fitPriority)); changed=true; }
            if(current?.Item3.GetValueOrDefault("Fit")?.FirstOrDefault()!=option)
            { Ok(pi.GetIpcSubscriber<Guid,string,string,string,string,int>("Penumbra.TrySetModSetting.V5").InvokeFunc(collection,dir,"","Fit",option)); changed=true; }
        }
        if((current?.Item1??false)!=(option!=null))
        { Ok(pi.GetIpcSubscriber<Guid,string,string,bool,int>("Penumbra.TrySetMod.V5").InvokeFunc(collection,dir,"",option!=null)); changed=true; }
        return changed;
    }

    private bool Vanilla(Guid id, string path) => string.Equals(Resolved(id,path),path,StringComparison.OrdinalIgnoreCase);

    private FamilyRefit.Input? FamilySource(string root,string directory,string gamePath,bool allowDisabled=false,Dictionary<string,List<string>>? selectedOptions=null)
    {
        var key=(directory,gamePath);
        if(!allowDisabled && familySources.TryGetValue(key,out var cached)) return cached;
        FamilyRefit.Input? source=null;
        if(!GeneratedFit(directory) &&
            installed.TryGetValue(directory,out var name) &&
            collectionSettings.TryGetValue(directory,out var setting) && (setting.Enabled || allowDisabled))
        {
            string folder=Path.Combine(root,directory);
            if(!Directory.Exists(folder)) {familySources[key]=null;return null;}
            if(selectedOptions!=null || !resolvedSources.TryGetValue(directory,out _))
            {
                resolvedSources[directory]=FamilyRefit.ResolveFiles(folder,selectedOptions ?? setting.Options);
            }
            var file=resolvedSources[directory].GetValueOrDefault(gamePath);
            if(file!=null && File.Exists(file))
            {
                var bodies=installed.Where(p=>FamilyRefit.IsBody(p.Value)).Select(p=>Path.Combine(root,p.Key))
                    .Concat(FamilyRefit.KnownBodyDirectories(root).Where(p=>installed.ContainsKey(Path.GetFileName(p))))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                var settings=(selectedOptions ?? setting.Options).ToDictionary(p=>p.Key,p=>p.Value.ToList());
                var input=new FamilyRefit.Input(folder,file,"",settings,bodies)
                {
                    Installed=installed.ToDictionary(),FamilyHint=FamilyRefit.IsGear(name),SourcePriority=setting.Priority
                };
                sourceBodyRevision ??= FamilyRefit.BodyRevision(bodies);
                if(!sourceRevisions.TryGetValue(folder,out var sourceStamp) || Environment.TickCount64-sourceStamp.Checked>=1000)
                    sourceRevisions[folder]=sourceStamp=(Environment.TickCount64,StandaloneRefit.Revision(folder));
                string revision=Hash("standalone-topology-first-v2|"+sourceBodyRevision+"|"+sourceStamp.Revision);
                source=input with {Revision=revision};
            }
        }
        familySources[key]=source;
        return source;
    }

    private FamilyRefit.Input? WinningFamilySource(string root,string gamePath)
    {
        if(winningFamilySources.TryGetValue(gamePath,out var cached)) return cached;
        var source=collectionSettings.Where(p=>p.Value.Enabled && !GeneratedFit(p.Key))
            .OrderByDescending(p=>p.Value.Priority).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase)
            .Select(p=>FamilySource(root,p.Key,gamePath)).FirstOrDefault(p=>p!=null);
        winningFamilySources[gamePath]=source;
        return source;
    }

    private string Resolved(Guid id, string path)
    {
        var (code, actual) = pi.GetIpcSubscriber<Guid,string,(int,string?)>("Penumbra.ResolvePath").InvokeFunc(id, path);
        return code == 0 ? actual ?? "" : "";
    }

    private static string[] Required(string slot) => slot switch
    {
        "_glv" => ["_glv", "_top"], "_sho" => ["_sho", "_dwn"],
        "_dwn" => ["_dwn", "_top"], _ => ["_top", "_dwn"],
    };

    private Batch Generate(Snapshot request, CancellationToken token)
    {
        names ??= GarmentFit.ItemLookup(data,log);
        var fits = new List<Fit>();
        var errors = new List<(string,string)>();
        var exports = new List<Export>();
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(familyCache);
        foreach(var group in request.Items.Where(i=>i.Family!=null).GroupBy(i=>i.Family!.Directory,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var family=group.First().Family!;
            string directory=StandaloneRefit.DirectoryName(family.Directory,request.Shape,family.Revision);
            string destination=Path.Combine(request.Root,directory);
            try
            {
                StandaloneRefit.Build(family.Directory,destination,request.Shape,family.Revision,family.Settings,model=>
                {
                    token.ThrowIfCancellationRequested();
                    var input=family with {File=model.File,Settings=model.Settings};
                    string key=Hash("standalone-model-v1|"+request.Shape+family.Revision+model.GamePath+model.File+JsonSerializer.Serialize(model.Settings));
                    string output=Path.Combine(familyCache,key+".mdl");
                    if(File.Exists(output)) return File.ReadAllBytes(output);
                    string rig=model.Race.ToString("D4");
                    var fit=FamilyRefit.Generate(input,model.GamePath,model.Slot,request.Targets,request.LegOptions,log,token,model.Race,
                        model.Race==201 ? null : Game("chara/xls/boneDeformer/human.pbd"),
                        model.Race==201 ? null : Game($"chara/human/c{rig}/skeleton/base/b0001/skl_c{rig}b0001.sklb"));
                    token.ThrowIfCancellationRequested();
                    Write(output,fit.Model);
                    log.Information("[Aetherform] Standalone {Path}: {Option}",model.GamePath,fit.Option);
                    return fit.Model;
                },token);
                exports.Add(new(directory,family,family.SourcePriority));
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            { errors.Add((Path.GetFileName(family.Directory),ex.GetBaseException().Message)); }
        }
        foreach (var item in request.Items.Where(i=>i.Family==null))
        {
            token.ThrowIfCancellationRequested();
            var key = Hash((item.Slot switch { "_top" => "motion-top-v1|", "_dwn" => "motion-bottom-v1|", "_glv" => "skin-contact-glove-v4|", "_sho" => "skin-contact-shoe-v1|", _ => "" }) + request.Shape + item.Path + "|native-skin-v2|" + item.Revision);
            var output = Path.Combine(cache,key + ".mdl");
            try
            {
                if (!File.Exists(output))
                {
                    if (failures.Contains(key)) { errors.Add((item.Path, "Previously declined for this shape.")); continue; }
                    var selected = new Dictionary<string,string>();
                    var originals = new Dictionary<string,string>();
                    foreach (var slot in Required(item.Slot))
                    {
                        string file = Path.Combine(cache, Hash(gameVersion + Bare(slot)) + ".mdl");
                        if (!File.Exists(file)) Write(file, Game(Bare(slot)));
                        originals[slot] = file;
                        selected[slot] = request.Targets.GetValueOrDefault(slot) ?? file;
                    }
                    string rig = item.Race == 101 ? "0201" : item.Race.ToString("D4");
                    var result = GarmentFit.Generate(Game(item.Path), item.Slot, item.Race, originals, selected, request.LegOptions,
                        item.Race == 201 ? null : Game("chara/xls/boneDeformer/human.pbd"),
                        item.Race == 201 ? null : Game($"chara/human/c{rig}/skeleton/base/b0001/skl_c{rig}b0001.sklb"), Path.Combine(cache,"bindings"), item.Classification.Data, log, request.ChestOption, request.LegOption);
                    token.ThrowIfCancellationRequested();
                    Write(output,result);
                }
                fits.Add(new(item, output));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is InvalidDataException) failures.Add(key);
                errors.Add((item.Path, ex.GetBaseException().Message));
            }
        }
        return new(request,fits,exports,errors);
    }

    private byte[] Game(string path) => data.GetFile(path)?.Data ?? throw new FileNotFoundException(path);
    private static void Write(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }

    private void Install(Batch batch)
    {
        var snapshot = batch.Inputs;
        if (batch.Fits.Count == 0 && batch.Exports.Count == 0)
        {
            committed = snapshot.Key;
            Status = batch.Errors.FirstOrDefault().Reason ?? "No refit is needed for this gear.";
            return;
        }
        var mods = pi.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        foreach(var export in batch.Exports)
            try { InstallStandalone(snapshot,export,mods); }
            catch(Exception ex) { batch.Errors.Add((export.Directory,"Could not activate standalone mod: "+ex.GetBaseException().Message)); }
        foreach (var fit in batch.Fits)
        {
            var item = fit.Item;
            var stem = Path.GetFileNameWithoutExtension(item.Path);
            string dir = "AF Vanilla Fit " + stem;
            string folder = Path.Combine(snapshot.Root,dir);
            int category = item.Slot switch { "_top" => 4, "_glv" => 5, "_dwn" => 7, "_sho" => 8, _ => 0 };
            var label = names?.Invoke(category,int.Parse(Equipment.Match(item.Path).Groups["set"].Value)) ?? stem;
            string name = label + " — Aetherform";
            var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(dir)).AsSpan(0,16));
            string meta = Path.Combine(folder,"meta.json");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                Write(meta, JsonSerializer.SerializeToUtf8Bytes(new { FileVersion = 4, Identifier = identity,
                    Name = name, Author = "Aetherform", Version = "1", Description = "Editable automatic refit for " + stem,
                    VanillaGamePath=item.Path }));
            }
            using (var prior = JsonDocument.Parse(File.ReadAllBytes(meta)))
                if (prior.RootElement.GetProperty("Identifier").GetGuid() != identity)
                    throw new IOException("The refit directory belongs to another mod: " + dir);
            string option=Option(snapshot.Preset,snapshot.Shape,item.Slot,item.Revision,item.Family!=null);
            string bodyFolder = Path.Combine(snapshot.Root,snapshot.BodyDirectory);
            var targets = Required(item.Slot).Where(snapshot.Targets.ContainsKey)
                .ToDictionary(k => k,k => Path.GetRelativePath(bodyFolder,snapshot.Targets[k]));
            // If the user edited a previously published 0.4.0.1 model, migrate those bytes.
            string legacy = Path.Combine(snapshot.Root,"Vanilla to Aetherform " + snapshot.Collection.ToString("N"),
                "models",Path.GetFileName(fit.Model));
            bool added = GarmentFit.SaveEditable(folder,item.Output,item.Path,item.Slot,snapshot.BodyDirectory,
                option,snapshot.Preset,targets,File.Exists(legacy) ? legacy : fit.Model);
            if (!mods.ContainsKey(dir))
            {
                Ok(pi.GetIpcSubscriber<string,int>("Penumbra.AddMod.V5").InvokeFunc(dir));
                Ok(pi.GetIpcSubscriber<string,string,string,int>("Penumbra.SetModPath.V5")
                    .InvokeFunc(dir,"",VanillaToAetherform.Folder + "/" + name + " (" + stem + ")"));
            }
            else if (added)
                Ok(pi.GetIpcSubscriber<string,string,int>("Penumbra.ReloadMod.V5").InvokeFunc(dir,""));
            ApplySettings(snapshot.Collection,dir,option);
        }
        committed = snapshot.Key;
        Status = $"Equipped fits: {batch.Fits.Count}. Standalone mods: {batch.Exports.Count}." + (batch.Errors.Count > 0 ? $" Skipped: {batch.Errors.Count}. See Dalamud log." : "");
        pi.GetIpcSubscriber<int,int,object>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
    }

    private void InstallStandalone(Snapshot snapshot,Export export,Dictionary<string,string> mods)
    {
        string directory=export.Directory;
        string source=Path.GetFileName(export.Source.Directory);
        if(!mods.ContainsKey(directory))
        {
            Ok(pi.GetIpcSubscriber<string,int>("Penumbra.AddMod.V5").InvokeFunc(directory));
        }
        PlaceFamilyMod(snapshot.Root,directory);
        var settings=StandaloneRefit.Defaults(StandaloneRefit.ReadManifest(Path.Combine(snapshot.Root,directory)));
        foreach(var pair in export.Source.Settings) settings[pair.Key]=pair.Value;
        void Enabled(string mod,bool enabled)
        {
            Ok(pi.GetIpcSubscriber<Guid,string,string,bool,int>("Penumbra.TrySetMod.V5")
                .InvokeFunc(snapshot.Collection,mod,"",enabled));
            var (code,state)=pi.GetIpcSubscriber<Guid,string,string,bool,bool,int,
                (int,(bool,int,Dictionary<string,List<string>>,bool,bool)?)>("Penumbra.GetCurrentModSettingsWithTemp")
                .InvokeFunc(snapshot.Collection,mod,"",false,false,0);
            Ok(code);
            if(state?.Item1!=enabled) throw new IOException("Penumbra did not apply the requested enabled state for "+mod);
        }
        var previous=new List<string>();
        foreach(var mod in mods.Keys.Where(d=>d!=directory && collectionSettings.GetValueOrDefault(d).Enabled))
        {
            if(mod==source) { previous.Add(mod);continue; }
            if(!mod.StartsWith(StandaloneRefit.Prefix,StringComparison.Ordinal) && !mod.StartsWith("AF Family Fit ",StringComparison.Ordinal)) continue;
            string file=Path.Combine(snapshot.Root,mod,"meta.json");
            if(!File.Exists(file)) continue;
            using var metadata=JsonDocument.Parse(File.ReadAllBytes(file));
            var root=metadata.RootElement;
            if(!root.TryGetProperty("Identifier",out var identifier) || identifier.GetGuid()!=StandaloneRefit.Identity(mod)) continue;
            string? original=root.TryGetProperty("AetherformSource",out var currentSource) ? currentSource.GetString() :
                root.TryGetProperty("FamilySourceDirectory",out var legacySource) ? legacySource.GetString() : null;
            if(string.Equals(original,source,StringComparison.OrdinalIgnoreCase)) previous.Add(mod);
        }
        bool wasEnabled=collectionSettings.GetValueOrDefault(directory).Enabled;
        StandaloneRefit.Activate(() =>
        {
            Ok(pi.GetIpcSubscriber<Guid,string,string,int,int>("Penumbra.TrySetModPriority.V5")
                .InvokeFunc(snapshot.Collection,directory,"",export.Priority));
            foreach(var (group,options) in settings)
                Ok(pi.GetIpcSubscriber<Guid,string,string,string,IReadOnlyList<string>,int>("Penumbra.TrySetModSettings.V5")
                    .InvokeFunc(snapshot.Collection,directory,"",group,options));
        },value=>Enabled(directory,value),wasEnabled,
            previous.Select(mod=>(Action<bool>)(value=>Enabled(mod,value))).ToArray(),
            ex=>log.Warning(ex,"Could not restore a mod after standalone activation failed"));
        synced="";
    }

    private void PlaceFamilyMod(string root,string directory)
    {
        string key=Path.Combine(root,directory);
        if(organizedFamilyMods.Contains(key)) return;
        Ok(pi.GetIpcSubscriber<string,string,string,int>("Penumbra.SetModPath.V5")
            .InvokeFunc(directory,"",StandaloneRefit.Folder+"/"+directory));
        organizedFamilyMods.Add(key);
    }

    private static void Ok(int result)
    {
        if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refit registration returned {result}.");
    }

    public void Reset() { synced=""; committed=""; }
    public void Dispose()
    {
        stop.Cancel();
        try { job?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally { jobStop?.Dispose(); stop.Dispose(); }
    }
}
