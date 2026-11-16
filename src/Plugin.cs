using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Aetherform;

internal sealed class Profiles
{
    public Dictionary<Guid, int[]> Applied { get; set; } = [];
    public int RefitPriority { get; set; } = -1;
    public HashSet<Guid> RefitDisabled { get; set; } = [];
    public Dictionary<string, int[]> Named { get; set; } = [];
}

public sealed partial class Plugin : IDalamudPlugin
{
    private const string Tag = "Aetherform physics preset";
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly ITextureProvider textures;
    private readonly EquipRefits fits;
    private readonly string settings;
    private PhysicsBank? bank;
    private readonly string payloadDirectory, cacheDirectory;
    private readonly Profiles profiles;
    private readonly Dictionary<Guid, (string Key, int Priority)> active = [];
    private Dictionary<Guid, string> collections = [];
    private string? body;
    private readonly Dictionary<string, (DateTime LastWrite, bool Current)> versions = [];
    private Guid selected;
    private Guid? installing;
    private long deadline, nextPoll;
    private string status = "";
    private int[] draft;

    public Plugin(IDalamudPluginInterface pi, IFramework framework, ICommandManager commands, IPluginLog log, ITextureProvider textures, IDataManager data, IObjectTable objects)
    {
        this.pi = pi;
        this.framework = framework;
        this.commands = commands;
        this.log = log;
        this.textures = textures;
        var folder = pi.AssemblyLocation.DirectoryName!;
        
        var config = pi.GetPluginConfigDirectory();
        Directory.CreateDirectory(config);
        settings = Path.Combine(config, "profiles.json");
        profiles = File.Exists(settings) ? JsonSerializer.Deserialize<Profiles>(File.ReadAllText(settings))! : new();
        payloadDirectory = File.Exists(Path.Combine(folder, "physics.zip")) ? folder : Path.Combine(config, "payload");
        cacheDirectory = Path.Combine(config, "physics");
        fits = new(pi, data, objects, log, config);
        draft = [];
        shell.Close = () => visible = false;
        commands.AddHandler("/aetherform", new CommandInfo((_, _) => Open()) { HelpMessage = "Open the Aetherform installer and physics builder." });
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenMainUi += Open;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.GetIpcSubscriber<object>("Penumbra.Initialized").Subscribe(Reset);
        framework.Update += Tick;
    }

    private void Open() { visible = true; nextPoll = 0; }
    private void Reset() { active.Clear(); fits.Reset(); nextPoll = 0; }
    private void Save()
    {
        File.WriteAllText(settings + ".tmp", JsonSerializer.Serialize(profiles));
        File.Move(settings + ".tmp", settings, true);
    }
    private void Select(Guid id)
    {
        selected = id;
        draft = profiles.Applied.TryGetValue(id, out var picks) && bank?.Valid(picks) == true ? [.. picks] : bank?.Defaults ?? [];
    }
    private void Tick(IFramework _)
    {

        if (Environment.TickCount64 < nextPoll) return;
        nextPoll = Environment.TickCount64 + 2000;
        try
        {
            if (bank == null)
            {
                var path = Path.Combine(payloadDirectory, "physics.zip");
                if (!File.Exists(path)) return;
                bank = new(path, cacheDirectory);
                Select(selected);
            }
            var mods = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
            if (!mods.HasFunction) { collections.Clear(); active.Clear(); fits.Reset(); body = null; return; }
            collections = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5").InvokeFunc();
            if (!collections.ContainsKey(selected))
            {
                var player = pi.GetIpcSubscriber<int,(bool,bool,(Guid,string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
                Select(player.Item1 ? player.Item3.Item1 : Guid.Empty);
            }
            var root = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc();
            body = mods.InvokeFunc().Where(m => m.Value == "Aetherform").Select(m => m.Key)
                .FirstOrDefault(dir => CurrentBody(Path.Combine(root, dir, "meta.json")));
            if (installing is Guid target)
            {
                if (body != null) { installing = null; Enable(target); }
                else if (Environment.TickCount64 > deadline) { installing = null; status = "Import timed out. Check the import in Penumbra."; }
            }
            fits.Update(root, body, profiles.RefitDisabled,profiles.RefitPriority);
            foreach (var id in active.Keys.Except(collections.Keys).ToArray()) active.Remove(id);
            foreach (var id in profiles.Applied.Keys.Where(collections.ContainsKey)) Sync(id);
        }
        catch (Exception ex) { Report(ex); nextPoll += 8000; }
    }

    private bool CurrentBody(string path)
    {
        if (!File.Exists(path)) return false;
        var modified = File.GetLastWriteTimeUtc(path);
        if (versions.TryGetValue(path, out var cached) && cached.LastWrite == modified) return cached.Current;
        using var meta = JsonDocument.Parse(File.ReadAllBytes(path));
        var current = meta.RootElement.TryGetProperty("Version", out var version) && version.GetString() == "0.3.0";
        versions[path] = (modified, current);
        return current;
    }

    private void Install()
    {
        if (body != null) { Enable(selected); return; }
        var package = Path.Combine(payloadDirectory, "Aetherform.pmp");
        if (!File.Exists(package)) throw new FileNotFoundException("The body package is missing.", package);
        Check(pi.GetIpcSubscriber<string,int>("Penumbra.InstallMod.V5").InvokeFunc(package));
        installing = selected;
        deadline = Environment.TickCount64 + 300000;
        status = "Import queued. Complete any import prompt in Penumbra.";
    }
    private void Enable(Guid id)
    {
        Check(pi.GetIpcSubscriber<Guid,string,string,bool,int>("Penumbra.TrySetMod.V5").InvokeFunc(id, body!, "", true));
        status = "Aetherform enabled in " + collections[id] + ".";
    }
    private void Sync(Guid id, bool requireBody = false)
    {
        var read = pi.GetIpcSubscriber<Guid,string,string,bool,bool,int,
            (int,(bool,int,Dictionary<string,List<string>>,bool,bool)?)>("Penumbra.GetCurrentModSettingsWithTemp");
        var settingsResult = body == null ? default : read.InvokeFunc(id, body, "", false, false, 0);
        if (settingsResult.Item1 != 0 || settingsResult.Item2 is not { Item1: true } values)
        {
            Remove(id);
            if (requireBody) throw new InvalidOperationException("Enable the Aetherform body in this collection first.");
            return;
        }
        var picks = profiles.Applied[id];
        var key = string.Concat(picks);
        var priority = checked(values.Item2 + 1);
        if (active.TryGetValue(id, out var previous) && previous == (key, priority)) return;
        var files = bank!.Resolve(picks);
        if (previous.Priority != priority) Remove(id);
        Check(pi.GetIpcSubscriber<string,Guid,Dictionary<string,string>,string,int,int>("Penumbra.AddTemporaryMod.V5")
            .InvokeFunc(Tag, id, files, "", priority));
        active[id] = (key, priority);
    }
    private void Remove(Guid id)
    {
        if (!active.TryGetValue(id, out var previous)) return;
        var remove = pi.GetIpcSubscriber<string,Guid,int,int>("Penumbra.RemoveTemporaryMod.V5");
        if (remove.HasFunction) Check(remove.InvokeFunc(Tag, id, previous.Priority));
        active.Remove(id);
    }
    private static void Check(int result)
    {
        if (result is not (0 or 1)) throw new InvalidOperationException($"Penumbra returned error {result}.");
    }
    private void Apply()
    {
        profiles.Applied.TryGetValue(selected, out var prior);
        profiles.Applied[selected] = [..draft];
        try { Sync(selected, true); Save(); }
        catch
        {
            if (prior != null) { profiles.Applied[selected] = prior; Sync(selected); }
            else { profiles.Applied.Remove(selected); Remove(selected); }
            throw;
        }
        status = "Applied to " + collections[selected] + ". Redraw your character to preview.";
    }
    private void Report(Exception ex)
    {
        status = ex.Message;
        log.Warning(ex, "Aetherform operation failed.");
    }
    public void Dispose()
    {
        framework.Update -= Tick;
        pi.GetIpcSubscriber<object>("Penumbra.Initialized").Unsubscribe(Reset);
        pi.UiBuilder.Draw -= Draw;
        pi.UiBuilder.OpenMainUi -= Open;
        pi.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/aetherform");
        foreach (var id in active.Keys.ToArray())
            try { Remove(id); } catch (Exception ex) { Report(ex); }
        fits.Dispose();
        bank?.Dispose();
    }
}
