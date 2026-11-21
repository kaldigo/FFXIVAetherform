using System.Reflection;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.Json;
using System.Security.Cryptography;

if(args.Length==3 && args[0]=="--visible-clearance")
{
    using var input=new BinaryReader(File.OpenRead(args[1]));
    using var expected=new BinaryReader(File.OpenRead(args[2]));
    Vector3 ReadPoint(BinaryReader r)=>new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
    Vector3[] ReadFaces()=>Enumerable.Range(0,3*input.ReadInt32()).Select(_=>ReadPoint(input)).ToArray();
    var cloth=ReadFaces();var skin=ReadFaces();var carrier=ReadFaces();
    var result=Aetherform.Geometry.VisibleClearance.Inspect(cloth,skin,carrier,input.ReadBytes(cloth.Length/3).Select(b=>b!=0).ToArray());
    for(int i=0;i<result.Samples.Length;i++)
        if(expected.ReadInt32()!=result.Samples[i] || ReadPoint(expected)!=result.Directions[i])
            throw new Exception($"Visibility differs at cloth face {i}.");
    Console.WriteLine($"{cloth.Length/3} cloth faces match the offline visibility raster.");
    return;
}

if(args.Length>0 && args[0]=="--skin-coverage")
{
    foreach(string file in args.Skip(1))
        SkinCoverageCheck.Run(file);
    return;
}

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
    var smooth = solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.PanelGeometry",true)!
        .GetMethod("Smooth",BindingFlags.NonPublic|BindingFlags.Static)!;
    Vector3[] panel = [new(-1,0,0),new(1,0,0),new(1,2,0),new(-1,2,0),new(0,1,0)];
    int[] triangles = [0,1,4,1,2,4,2,3,4,3,0,4];
    var flat = panel.Select(p => p+Vector3.UnitZ).ToArray();
    smooth.Invoke(null,[panel,flat,triangles]);
    if (!flat.SequenceEqual(panel.Select(p=>p+Vector3.UnitZ))) throw new Exception("Smoothing changed a translated panel.");
    var spike = (Vector3[])panel.Clone();
    spike[4].Z=.02f;
    smooth.Invoke(null,[panel,spike,triangles]);
    if(spike[4].Z>=.01f || !spike.Take(4).SequenceEqual(panel.Take(4)))
        throw new Exception("Smoothing failed to relax the interior displacement or moved a straight boundary.");
    var bridge=solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.PanelGeometry",true)!
        .GetMethod("BridgeChest",BindingFlags.NonPublic|BindingFlags.Static)!;
    var chestGrid=new List<Vector3>();
    var indices=new List<int>();
    for(int row=0;row<3;row++)
    for(int column=0;column<9;column++)
    {
        int vertex=chestGrid.Count;
        chestGrid.Add(new((column-4)*.015f,1.23f+row*.01f,.09f+.001f*Math.Abs(column-4)));
        if(row<2 && column<8) indices.AddRange([vertex,vertex+1,vertex+9,vertex+1,vertex+10,vertex+9]);
    }
    var reference=chestGrid.ToArray();
    var shaped=reference.ToArray();
    var hold=new Dictionary<int,Vector3>();
    bridge.Invoke(null,[reference,shaped,indices.ToArray(),hold]);
    if(shaped.Where((p,i)=>Vector3.Distance(p,reference[i])>1e-6f).Any())
        throw new Exception("Chest bridging changed the authored relief.");
    foreach(int i in new[]{4,13,22}) shaped[i].Z-=.009f;
    hold[4]=shaped[4];
    bridge.Invoke(null,[reference,shaped,indices.ToArray(),hold]);
    if(shaped[13].Z<reference[13].Z-.003f || shaped[4]!=hold[4])
        throw new Exception("Chest bridging lost a sewn anchor or left the new center fold.");
    var bridgeGusset=solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.PanelGeometry",true)!
        .GetMethod("BridgeGusset",BindingFlags.NonPublic|BindingFlags.Static)!;
    var underside=reference.Select(p=>new Vector3(p.X,.852f+p.Z-.09f,p.Y-1.23f)).ToArray();
    var fabric=underside.Select(p=>p+new Vector3(0,.005f,0)).ToArray();
    var rim=Enumerable.Range(0,fabric.Length).Where(i=>Math.Abs(underside[i].X)>.029f)
        .ToDictionary(i=>i,i=>fabric[i]);
    fabric[13].Y+=.012f;
    bridgeGusset.Invoke(null,[underside,fabric,indices.ToArray(),rim]);
    if(Math.Abs(fabric[13].Y-underside[13].Y-.005f)>.00015f || rim.Any(e=>fabric[e.Key]!=e.Value))
        throw new Exception("Gusset bridge lost its authored relief or moved the leg opening.");
    var bridgeLower=solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.PanelGeometry",true)!
        .GetMethod("BridgeLowerPanel",BindingFlags.NonPublic|BindingFlags.Static)!;
    var front=reference.Select(p=>new Vector3(p.X,p.Y-.33f,p.Z)).ToArray();
    var forward=front.Select(p=>p+new Vector3(0,0,.004f)).ToArray();
    var openings=Enumerable.Range(0,front.Length).Where(i=>Math.Abs(front[i].X)>.029f)
        .ToDictionary(i=>i,i=>forward[i]);
    forward[13].Z-=.012f;
    bridgeLower.Invoke(null,[front,forward,indices.ToArray(),openings,true]);
    if(Math.Abs(forward[13].Z-front[13].Z-.004f)>.00015f || openings.Any(e=>forward[e.Key]!=e.Value))
        throw new Exception("Front bridge left a center groove or moved the leg openings.");
    var carryTrim=solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.PanelGeometry",true)!
        .GetMethod("CarryTrim",BindingFlags.NonPublic|BindingFlags.Static)!;
    Vector3[] trimRest=[new(.06f,.70f,0),new(.10f,.70f,0),new(.06f,.74f,0),
        new(.064f,.704f,.002f),new(.084f,.704f,.002f),new(.064f,.724f,.002f)];
    var trimFit=trimRest.Select(p=>p+new Vector3(.002f,0,.008f)).ToArray();
    trimFit[3].Z-=.005f;
    carryTrim.Invoke(null,[trimRest,trimFit,new[]{0,1,2,3,4,5},new Dictionary<int,Vector3>{{0,trimFit[0]}}]);
    if(trimFit.Skip(3).Any(p=>Math.Abs(p.Z-.010f)>1e-6f))
        throw new Exception("Stocking trim lost its source stand-off from its support.");
    var children=solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.ClothSubdivision",true)!
        .GetMethod("Children",BindingFlags.NonPublic|BindingFlags.Static)!;
    Vector2[] points=[new(0,0),new(2,0),new(0,2),new(1,0),new(1,1),new(0,1)];
    for(int mask=0;mask<8;mask++)
    {
        int Mid(int edge)=>(mask&(1<<edge))==0?-1:3+edge;
        var tris=(int[])children.Invoke(null,[0,1,2,Mid(0),Mid(1),Mid(2)])!;
        float area=0;
        foreach(var t in tris.Chunk(3))
        {
            var a=points[t[1]]-points[t[0]]; var b=points[t[2]]-points[t[0]];
            float cross=a.X*b.Y-a.Y*b.X;
            if(cross<=0) throw new Exception("Reversed or degenerate seam triangle.");
            area+=cross;
        }
        if(area!=4 || Enumerable.Range(0,3).Any(e=>tris.Contains(3+e)!=(Mid(e)>=0)))
            throw new Exception("Subdivision changed patch area or split a protected rim.");
    }
    // Ask two nearby panels to move through each other. Both sample the
    // same deformation and the attachment on the shared body join stays put.
    var cage = solver.DeclaringType.Assembly.GetType("Aetherform.Geometry.GarmentDeformation",true)!;
    var apply = cage.GetMethod("Apply",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var argsTypes = apply.GetParameters();
    var panels = (System.Collections.IList)Activator.CreateInstance(argsTypes[0].ParameterType)!;
    var patch = panels.GetType().GetGenericArguments()[0];
    Vector3[] inner = [new(0,1,0),new(.035f,1,0),new(0,1.035f,0)];
    Vector3[] outer = inner.Select(p=>p+.002f*Vector3.UnitZ).ToArray();
    var inside = inner.Select(p=>p+.02f*Vector3.UnitZ).ToArray();
    var outside = outer.Select(p=>p-.02f*Vector3.UnitZ).ToArray();
    foreach(var (rest,fit) in new[]{(inner,inside),(outer,outside)})
        panels.Add(Activator.CreateInstance(patch,[rest,fit,new[]{0,1,2},new[]{0,1,2},new Dictionary<int,Vector3>()]));
    apply.Invoke(Activator.CreateInstance(cage),[panels,new[]{(inner[0],inner[1])},null,
        Activator.CreateInstance(argsTypes[3].ParameterType)]);
    if(inside[0]!=inner[0] || inside[1]!=inner[1] || outside.Where((p,i)=>p.Z<=inside[i].Z).Any())
        throw new Exception("Shared deformation lost layer order or moved an authored waist attachment.");
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
    System.Collections.IDictionary? expectedKnots=null;
    var game = File.ReadAllBytes(test.GetProperty("model").GetString()!);
    LodWriterCheck.Run(solver.DeclaringType!.Assembly,game);
    var slot = test.GetProperty("slot").GetString()!;
    var body = test.GetProperty("originals").Deserialize<Dictionary<string,string>>()!;
    var targets = test.GetProperty("targets").Deserialize<Dictionary<string,string>>()!;
    var options = test.GetProperty("legOptions").Deserialize<string[]>()!;
    ushort race = test.TryGetProperty("race", out var id) ? id.GetUInt16() : (ushort)201;
    byte[]? FileBytes(string property) => test.TryGetProperty(property, out var path) ? File.ReadAllBytes(path.GetString()!) : null;
    var classification = test.TryGetProperty("classification",out var recordPath)
        ? solver.DeclaringType!.Assembly.GetType("Aetherform.Geometry.GarmentClassification")!.GetMethod("Read")!.Invoke(null,
            [File.ReadAllBytes(recordPath.GetString()!),Path.GetFileNameWithoutExtension(recordPath.GetString()!)]) : null;
    if (test.TryGetProperty("nativeMorphOutput", out var nativeOutput))
    {
        var morphType = solver.DeclaringType!.Assembly.GetType("Aetherform.Geometry.NativeMorph",true)!;
        var reference = targets.ToDictionary(k => k.Key,k => File.ReadAllBytes(k.Value));
        var mapper = Activator.CreateInstance(morphType,BindingFlags.Instance|BindingFlags.NonPublic,null,[reference],null)!;
        var native = (byte[])morphType.GetMethod("Apply",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(mapper,[game])!;
        File.WriteAllBytes(nativeOutput.GetString()!,native);
        if (test.TryGetProperty("auditSkinRegistration",out var enabled) && enabled.GetBoolean())
        {
            var asm=solver.DeclaringType.Assembly;
            var chart=Activator.CreateInstance(asm.GetType("Aetherform.Geometry.NativeSkinChart",true)!,
                BindingFlags.Instance|BindingFlags.NonPublic,null,[reference[slot]],null)!;
            var register=asm.GetType("Aetherform.Geometry.GarmentSkin",true)!.GetMethod("Register",BindingFlags.Static|BindingFlags.NonPublic)!;
            var registered=(System.Collections.ICollection)register.Invoke(null,[native,chart,asm.GetType("Aetherform.Geometry.SkinRoutes",true)!.GetMethod("For",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[game])])!;
            var prepared=asm.GetType("Aetherform.Geometry.CutSkinAssembly",true)!.GetMethod("Prepare",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[game,native,chart])!;
            var knots=(System.Collections.IDictionary)prepared.GetType().GetProperty("Knots")!.GetValue(prepared)!;
            expectedKnots=knots;
            Console.WriteLine($"{test.GetProperty("model").GetString()}: {registered.Count} registered skin faces, {knots.Count} cloth knots.");
        }
    }
    var result = (byte[])solver.Invoke(null,[game,slot,race,body,targets,options,FileBytes("pbd"),FileBytes("rig"),Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"bindings"),classification,log])!;
    if(expectedKnots!=null) LodWriterCheck.CheckJoins(solver.DeclaringType!.Assembly,result,expectedKnots);
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
