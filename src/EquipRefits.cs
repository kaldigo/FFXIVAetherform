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
    private const string Source = "Aetherform vanilla refits";
    private const int SettingsKey = 0x414652;
    // Existing libraries can have handmade refits at negative priorities as well.
    private const int Priority = int.MinValue;
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
    private readonly string gameVersion;
    private string manifestPath = "";
    private DateTime manifestTime;
    private Dictionary<string,Dictionary<string,string>> choices = [];
    private readonly Dictionary<string,string> defaults = [];
    private readonly HashSet<string> failures = [];
    private sealed record Item(string Path, string Output, string Slot, ushort Race);
    private sealed record Snapshot(Guid Collection, string Root, string Shape, Dictionary<string,string> Targets,
        string[] LegOptions, string BodyDirectory, string Preset, Item[] Items, string Key);
    private sealed record Fit(Item Item, string Model);
    private sealed record Batch(Snapshot Inputs, List<Fit> Fits, List<(string Item, string Reason)> Errors);
    private Task<Batch>? job;
    private CancellationTokenSource? jobStop;
    private string? jobKey;
    private readonly CancellationTokenSource stop = new();
    private Guid? worn;
    private readonly HashSet<string> activeMods = new(StringComparer.OrdinalIgnoreCase);
    private Func<int,int,string?>? names;
    private Dictionary<string,Item> installed = [];
    private string committed = "";
    private readonly HashSet<Guid> migrated = [];
    public string Status { get; private set; } = "";

    public EquipRefits(IDalamudPluginInterface pi, IDataManager data, IObjectTable objects, IPluginLog log, string config)
    {
        this.pi = pi; this.data = data; this.objects = objects; this.log = log;
        cache = VanillaToAetherform.Cache(config);
        var versionFile = Path.Combine(data.GameData.DataPath.FullName, "..", "ffxivgame.ver");
        gameVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : data.GameData.DataPath.LastWriteTimeUtc.Ticks.ToString();
    }

    private static string Hash(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private static string Bare(string slot) => VanillaToAetherform.Bare(slot);

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

    public void Update(string root, string? body, HashSet<Guid> disabled)
    {
        var current = Capture(root, body, disabled, out bool drawing);
        if (drawing) return;
        if (job != null && jobKey != current?.Key) jobStop?.Cancel();
        if (worn is { } previous && current?.Collection != previous) Deactivate();
        if (job is { IsCompleted: true })
        {
            try
            {
                var batch = job.GetAwaiter().GetResult();
                if (jobStop?.IsCancellationRequested == false && batch.Inputs.Key == current?.Key)
                {
                    Install(batch);
                    foreach (var (path, reason) in batch.Errors)
                        log.Warning("Vanilla refit skipped {Path}: {Reason}", path, reason);
                }
            }
            catch (OperationCanceledException) when (jobStop?.IsCancellationRequested == true) { }
            finally { job = null; jobStop?.Dispose(); jobStop = null; jobKey = null; }
        }
        if (current == null) { Status = ""; Deactivate(); return; }
        if (current.Items.Length == 0)
        {
            Deactivate();
            Status = "No vanilla body gear to refit on your character.";
            return;
        }
        if (current.Key == committed || job != null) return;
        Status = "Fitting equipped vanilla gear…";
        jobStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        jobKey = current.Key;
        var token = jobStop.Token;
        job = Task.Run(() => Generate(current, token), token);
    }

    private Snapshot? Capture(string root, string? body, HashSet<Guid> disabled, out bool drawing)
    {
        drawing = false;
        var player = objects.LocalPlayer;
        if (body == null || player == null || player.Customize[1] != 1) return null;
        ushort wearerRace = VanillaToAetherform.Race(player.Customize[0], player.Customize[4]);
        if (wearerRace == 0) return null;
        var (valid, _, (id, _)) = pi.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        if (!valid || id == Guid.Empty || disabled.Contains(id)) return null;
        var (code, setting) = pi.GetIpcSubscriber<Guid,string,string,bool,bool,int,
            (int,(bool,int,Dictionary<string,List<string>>,bool,bool)?)>("Penumbra.GetCurrentModSettingsWithTemp").InvokeFunc(id, body, "", false, false, 0);
        if (code != 0 || setting is not { Item1: true } effective) return null;
        ReadChoices(Path.Combine(root,body));
        var targets = new Dictionary<string,string>();
        var labels = new List<string>();
        foreach (var (slot, group) in Groups)
        {
            var selected = effective.Item3.GetValueOrDefault(group)?.FirstOrDefault() ?? defaults[slot];
            if (choices[slot].TryGetValue(selected, out var path)) targets[slot] = path;
            if (slot is "_top" or "_dwn") labels.Add(selected);
        }
        bool Supplied(string slot) => VanillaToAetherform.Supplies(slot, wearerRace, targets, path => Resolved(id,path));
        if (!Supplied("_top") || !Supplied("_dwn")) return null;
        foreach (string end in new[] { "_glv", "_sho" })
            if (!Supplied(end)) targets.Remove(end);
        var legs = effective.Item3.GetValueOrDefault("Leg Options")?.Order().ToArray() ?? [];
        var shape = Hash("Vanilla/Aetherform/v1|" + gameVersion + "|" + string.Join('|', legs) + "|" +
            string.Join('|', targets.Select(t => t.Key + t.Value + File.GetLastWriteTimeUtc(t.Value).Ticks)));
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
            if (!targets.ContainsKey(slot)) continue;
            if (worn == id && installed.TryGetValue(path, out var old) &&
                (OurFile(root, actual) || (actual.Equals(path, StringComparison.OrdinalIgnoreCase) && OurFile(root, Resolved(id,path)))))
            {
                if (old.Path != path && !Vanilla(id, old.Path)) continue;
                pieces[path] = old;
                continue;
            }
            // A newly registered female model may still be awaiting its first redraw from the shared male path.
            if (worn == id && actual.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                var shared = installed.Values.FirstOrDefault(i => i.Path == path && i.Output != path);
                if (shared != null && Vanilla(id,path) && OurFile(root,Resolved(id,shared.Output)))
                {
                    pieces[shared.Output] = shared;
                    continue;
                }
            }
            if (!actual.Equals(path, StringComparison.OrdinalIgnoreCase) || !Vanilla(id, path)) continue;
            ushort race = ushort.Parse(m.Groups["race"].Value);
            if (race != 101 && race is not (201 or 401 or 601 or 801 or 1001 or 1401 or 1601 or 1801)) continue;
            string output = race == 101 ? path.Replace("/c0101e", "/c0201e") : path;
            if (output != path && !Vanilla(id, output) && !installed.ContainsKey(output)) continue;
            pieces[output] = new(path, output, slot, race);
        }
        var items = pieces.Values.OrderBy(x => x.Path).ToArray();
        var key = Hash(id + "|" + root + "|" + shape + "|" + string.Join('|',items.Select(i => i.Path)));
        return new(id, root, shape, targets, legs, body, string.Join(" / ",labels.Concat(legs)), items, key);
    }

    private bool OurFile(string root, string path) => worn.HasValue && Path.IsPathFullyQualified(path) && activeMods.Any(dir =>
        Path.GetFullPath(path).StartsWith(Path.Combine(root, dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private bool Vanilla(Guid id, string path) => string.Equals(Resolved(id,path),path,StringComparison.OrdinalIgnoreCase);

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
        Directory.CreateDirectory(cache);
        foreach (var item in request.Items)
        {
            token.ThrowIfCancellationRequested();
            var key = Hash((item.Slot is "_top" or "_dwn" ? "panel-subdivision-v1|" : "") + request.Shape + item.Path);
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
                        item.Race == 201 ? null : Game($"chara/human/c{rig}/skeleton/base/b0001/skl_c{rig}b0001.sklb"), Path.Combine(cache,"bindings"), log);
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
        return new(request,fits,errors);
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
        if (batch.Fits.Count == 0)
        {
            Deactivate(); committed = snapshot.Key;
            Status = batch.Errors.FirstOrDefault().Reason ?? "No matching garments.";
            return;
        }
        var mods = pi.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList").InvokeFunc();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    Name = name, Author = "Aetherform", Version = "1", Description = "Editable vanilla refit for " + stem }));
            }
            using (var prior = JsonDocument.Parse(File.ReadAllBytes(meta)))
                if (prior.RootElement.GetProperty("Identifier").GetGuid() != identity)
                    throw new IOException("The refit directory belongs to another mod: " + dir);
            string option = snapshot.Preset.Replace(" / ", " + ") + " [" + snapshot.Shape[..12] + "]";
            if (item.Slot is "_top" or "_dwn") option += " Cloth Detail";
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
            IReadOnlyDictionary<string,IReadOnlyList<string>> options =
                new Dictionary<string,IReadOnlyList<string>> { ["Fit"] = new[] { option } };
            Ok(pi.GetIpcSubscriber<Guid,string,string,(bool,bool,int,IReadOnlyDictionary<string,IReadOnlyList<string>>),string,int,int>(
                "Penumbra.SetTemporaryModSettings.V5").InvokeFunc(snapshot.Collection,dir,"",(false,true,Priority,options),Source,SettingsKey));
            activeMods.Add(dir);
            desired.Add(dir);
            worn = snapshot.Collection;
        }
        foreach (var dir in activeMods.Except(desired).ToArray()) Release(snapshot.Collection,dir);
        installed = batch.Fits.ToDictionary(f => f.Item.Output,f => f.Item);
        committed = snapshot.Key;
        Status = $"Equipped vanilla fits: {installed.Count}." + (batch.Errors.Count > 0 ? $" Skipped: {batch.Errors.Count}. See Dalamud log." : "");
        pi.GetIpcSubscriber<int,int,object>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
    }

    private static void Ok(int result)
    {
        if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra refit registration returned {result}.");
    }

    private void Deactivate()
    {
        if (worn is Guid collection)
        {
            foreach (var dir in activeMods.ToArray()) Release(collection,dir);
            if (objects.LocalPlayer != null && !stop.IsCancellationRequested)
                pi.GetIpcSubscriber<int,int,object>("Penumbra.RedrawObject.V5").InvokeAction(0,0);
        }
        Reset();
    }
    private void Release(Guid collection, string dir)
    {
        var remove = pi.GetIpcSubscriber<Guid,string,string,int,int>("Penumbra.RemoveTemporaryModSettings.V5");
        if (remove.HasFunction)
        {
            int code = remove.InvokeFunc(collection,dir,"",SettingsKey);
            if (code is not (0 or 1 or 2 or 3)) Ok(code);
        }
        activeMods.Remove(dir);
    }
    public void Reset() { worn = null; activeMods.Clear(); committed = ""; installed.Clear(); }
    public void Dispose()
    {
        stop.Cancel();
        try { job?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally { try { Deactivate(); } finally { jobStop?.Dispose(); stop.Dispose(); } }
    }
}
