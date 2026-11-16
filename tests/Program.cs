using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Security.Cryptography;

// Run the shipped headless entry point against locally owned game and mod files.
var selected = new Dictionary<string,string> { ["_top"] = @"D:\mods\AF\chest.mdl", ["_dwn"] = @"D:\mods\AF\legs.mdl" };
foreach (string part in selected.Keys)
{
    bool Gate(Func<string,string> resolver) => Aetherform.VanillaToAetherform.Supplies(part,801,selected,resolver);
    string shared = Aetherform.VanillaToAetherform.Bare(part);
    if (!Gate(p => p == shared ? selected[part] : p) ||
        Gate(p => p == shared ? @"D:\mods\YAB\other.mdl" : p) ||
        Gate(p => p == shared ? selected[part] : @"D:\mods\Rue\racial.mdl") ||
        Gate(p => p)) throw new Exception("Effective body gate failed for " + part);
}
Console.WriteLine("Effective body checks passed (AF, other shared body, other racial body, and vanilla).");
if (args.Length == 0) return;
var config = JsonDocument.Parse(File.ReadAllBytes(args[0])).RootElement;
var lib = config.GetProperty("library").GetString()!;
var dalamud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher/addon/Hooks/dev");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var root in new[]{lib,dalamud})
    {
        var file = Path.Combine(root,name.Name + ".dll");
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(file));
    }
    return null;
};
var solver = Assembly.LoadFrom(Path.Combine(lib,"Proteus.Refit.dll")).GetType("Aetherform.Geometry.GarmentFit")!.GetMethod("Generate")!;
var log = DispatchProxy.Create(solver.GetParameters()[^1].ParameterType, typeof(QuietLog));
var hashes = new Dictionary<string, HashSet<string>>();
foreach (var test in config.GetProperty("cases").EnumerateArray())
{
    var game = File.ReadAllBytes(test.GetProperty("model").GetString()!);
    var slot = test.GetProperty("slot").GetString()!;
    var body = test.GetProperty("originals").Deserialize<Dictionary<string,string>>()!;
    var targets = test.GetProperty("targets").Deserialize<Dictionary<string,string>>()!;
    var options = test.GetProperty("legOptions").Deserialize<string[]>()!;
    ushort race = test.TryGetProperty("race", out var id) ? id.GetUInt16() : (ushort)201;
    byte[]? FileBytes(string property) => test.TryGetProperty(property, out var path) ? File.ReadAllBytes(path.GetString()!) : null;
    var result = (byte[])solver.Invoke(null,[game,slot,race,body,targets,options,FileBytes("pbd"),FileBytes("rig"),log])!;
    if (result.SequenceEqual(game)) throw new Exception("Unexpected identity fit.");
    var hash = Convert.ToHexString(SHA256.HashData(result));
    string name = test.GetProperty("model").GetString()!;
    if (!hashes.TryGetValue(name,out var seen)) hashes.Add(name,seen = []);
    if (!seen.Add(hash)) throw new Exception("Different presets produced identical output for " + name);
    Console.WriteLine($"{Path.GetFileName(name)} -> {result.Length} bytes, {hash[..12]}");
}
public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method,object?[]? args) =>
        method!.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
