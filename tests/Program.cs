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
bool editCheck = args[0] == "--editable";
var config = editCheck ? default : JsonDocument.Parse(File.ReadAllBytes(args[0])).RootElement;
var lib = editCheck ? Path.GetFullPath(args[1]) : config.GetProperty("library").GetString()!;
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
if (editCheck)
{

    var map = solver.DeclaringType!.Assembly.GetType("Aetherform.Geometry.VanillaAfWrap",true)!;
    int corners = (int)map.GetProperty("CornerCount",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    if (corners != 3*(14030+10260)) throw new Exception("Shipped reference topology changed.");
    var save = solver.DeclaringType!.GetMethod("SaveEditable")!;
    var folder = Directory.CreateDirectory(Path.Combine(lib,"test-output",Guid.NewGuid().ToString("N"))).FullName;
    try
    {
        const string path = "chara/equipment/e0170/model/c0201e0170_dwn.mdl";
        var input = Path.Combine(folder,"generated.mdl");
        File.WriteAllBytes(input,[1,2,3]);
        File.WriteAllText(Path.Combine(folder,"meta.json"),JsonSerializer.Serialize(new { FileVersion = 4, Identifier = Guid.NewGuid(), Name = "Fixture" }));
        bool Save(string option) => (bool)save.Invoke(null,[folder,path,path,"_dwn","Aetherform",option,option,
            new Dictionary<string,string> { ["_dwn"] = "large.mdl" },input])!;
        string Sidecar() => File.ReadAllText(Path.Combine(folder,"Proteus","bodyretarget.json"));
        if (!Save("Medium")) throw new Exception("Fit not created.");
        using var record = JsonDocument.Parse(Sidecar());
        var entry = record.RootElement.GetProperty("Options")[0];
        var edited = Path.Combine(folder,entry.GetProperty("File").GetString()!);
        File.WriteAllBytes(edited,[7,8,9]);
        if (Save("Medium") || !Save("Large") || Save("Medium") || !File.ReadAllBytes(edited).SequenceEqual(new byte[]{7,8,9}))
            throw new Exception("Re-equipping or adding a shape overwrote a correction.");
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder,"meta.json")));
        var options = meta.RootElement.GetProperty("Groups")[0].GetProperty("Options");
        if (options.GetArrayLength()!=3 || !entry.GetProperty("FromGame").GetBoolean() ||
            entry.GetProperty("Targets").GetProperty("_dwn").GetString() != "large.mdl")
            throw new Exception("Missing native fit options or provenance.");
        Console.WriteLine("Native Fit options, source record and edit persistence passed.");
    }
    finally { Directory.Delete(folder,true); }
    return;
}
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
    var result = (byte[])solver.Invoke(null,[game,slot,race,body,targets,options,FileBytes("pbd"),FileBytes("rig"),Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"bindings"),log])!;
    if (test.TryGetProperty("output", out var output)) File.WriteAllBytes(output.GetString()!,result);
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
