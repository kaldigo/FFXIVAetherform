using System.Reflection;
using System.Text.Json;

internal static class FamilyRefitCheck
{
    internal static void Run(Assembly assembly)
    {
        var api=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location)!,"Penumbra.Api.dll"));
        var subscriber=api.GetType("Penumbra.Api.IpcSubscribers.GetAllModSettings",true)!;
        var invoke=subscriber.GetMethod("Invoke")!;
        if(!invoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Guid),typeof(bool),typeof(bool),typeof(int)}))
            throw new Exception("Collection settings IPC signature differs from the installed API");
        var setOptions=api.GetType("Penumbra.Api.IpcSubscribers.TrySetModSettings",true)!.GetMethod("Invoke")!;
        if(!setOptions.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Guid),typeof(string),typeof(string),typeof(IReadOnlyList<string>),typeof(string)}))
            throw new Exception("Multi-option setting IPC differs from the installed API");
        var type=assembly.GetType("Aetherform.Geometry.FamilyRefit",true)!;
        var inputType=type.GetNestedType("Input")!;
        var directory=Path.Combine(Path.GetDirectoryName(assembly.Location)!,"test-output","family-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string game="chara/equipment/e0123/model/c0201e0123_top.mdl";
        object Option(string name,string file)=>new {Name=name,Files=new Dictionary<string,string>{{game,file}}};
        try
        {
            File.WriteAllText(Path.Combine(directory,"meta.json"),JsonSerializer.Serialize(new
            {
                FileVersion=4,
                DefaultData=new {Files=new Dictionary<string,string>{{game,"default.mdl"}}},
                Groups=new object[]
                {
                    new {Name="Chest Size",Type="Single",Priority=10,DefaultSettings=0,Options=new[]
                    {
                        Option("YAB S - Covered","small.mdl"),Option("YAB L - Covered","large.mdl"),
                        Option("Rue M - Covered","rue.mdl"),Option("YAB L - Open","open.mdl"),Option("Yiggle YAB L - Covered","yiggle.mdl")
                    }},
                    new {Name="Coverage",Type="Single",Priority=20,DefaultSettings=0,
                        Options=new object[]{new {Name="Original",Files=new Dictionary<string,string>()},Option("Alternate","coverage.mdl")}}
                }
            }));
            var settings=new Dictionary<string,List<string>>{{"Chest Size",["YAB S - Covered"]},{"Coverage",["Original"]}};
            var resolve=type.GetMethod("Resolve")!;
            string Read()=>(string)resolve.Invoke(null,[directory,game,settings])!;
            if(Read()!=Path.Combine(directory,"small.mdl")) throw new Exception("Selected size did not resolve");
            var input=Activator.CreateInstance(inputType,[directory,Read(),"test",settings,Array.Empty<string>()]);
            var candidates=(string[])type.GetMethod("Candidates")!.Invoke(null,[input,game])!;
            if(!candidates.Select(Path.GetFileName).Order().SequenceEqual(new[]{"large.mdl","rue.mdl","small.mdl"}))
                throw new Exception("Size candidates changed coverage or excluded the alternate body family");
            settings["Coverage"]=["Alternate"];
            if(Read()!=Path.Combine(directory,"coverage.mdl")) throw new Exception("Higher-priority coverage was overwritten");
            input=Activator.CreateInstance(inputType,[directory,Read(),"test",settings,Array.Empty<string>()]);
            candidates=(string[])type.GetMethod("Candidates")!.Invoke(null,[input,game])!;
            if(candidates.Length!=1 || candidates[0]!=Read()) throw new Exception("Size selection replaced the selected style");
            var combined=Directory.CreateDirectory(Path.Combine(directory,"combining")).FullName;
            File.WriteAllText(Path.Combine(combined,"meta.json"),JsonSerializer.Serialize(new
            {
                FileVersion=4,Groups=new[]{new
                {
                    Name="Fit and sleeves",Type="Combining",DefaultSettings=2,
                    Options=new[]{new {Name="Large breasts"},new {Name="Sleeveless"},new {Name="Chest accessory"}},
                    Containers=new[]{Option("","small-sleeved.mdl"),Option("","large-sleeved.mdl"),
                        Option("","small-sleeveless.mdl"),Option("","large-sleeveless.mdl"),
                        Option("","small-sleeved-accessory.mdl"),Option("","large-sleeved-accessory.mdl"),
                        Option("","small-sleeveless-accessory.mdl"),Option("","large-sleeveless-accessory.mdl")}
                }}
            }));
            var flags=new Dictionary<string,List<string>>{{"Fit and sleeves",["Sleeveless","Chest accessory"]}};
            var combinedFile=(string)resolve.Invoke(null,[combined,game,flags])!;
            input=Activator.CreateInstance(inputType,[combined,combinedFile,"test",flags,Array.Empty<string>()]);
            candidates=(string[])type.GetMethod("Candidates")!.Invoke(null,[input,game])!;
            if(!candidates.Select(Path.GetFileName).Order().SequenceEqual(new[]{"large-sleeveless-accessory.mdl","small-sleeveless-accessory.mdl"}))
                throw new Exception("Combining size selection changed the sleeve option");
            flags["Fit and sleeves"].Add("Large breasts");
            if((string)resolve.Invoke(null,[combined,game,flags])! != Path.Combine(combined,"large-sleeveless-accessory.mdl"))
                throw new Exception("Combining flags did not resolve to the selected container");
            var defaultSource=Directory.CreateDirectory(Path.Combine(directory,"default-source")).FullName;
            File.WriteAllText(Path.Combine(defaultSource,"meta.json"),JsonSerializer.Serialize(new
            {
                FileVersion=4,DefaultData=new {Files=new Dictionary<string,string>{{game,"original-large.mdl"}}},
                Groups=new[]{new {Name="Size",Type="Single",DefaultSettings=0,Options=new object[]
                {
                    new {Name="Original",Files=new Dictionary<string,string>()},Option("YAB S","small.mdl"),
                    Option("Rue M","medium.mdl"),Option("YAB L - Open","open.mdl")
                }}}
            }));
            var defaultSettings=new Dictionary<string,List<string>>();
            var defaultModel=(string)resolve.Invoke(null,[defaultSource,game,defaultSettings])!;
            input=Activator.CreateInstance(inputType,[defaultSource,defaultModel,"test",defaultSettings,Array.Empty<string>()]);
            candidates=(string[])type.GetMethod("Candidates")!.Invoke(null,[input,game])!;
            if(!candidates.Select(Path.GetFileName).Order().SequenceEqual(new[]{"medium.mdl","original-large.mdl","small.mdl"}))
                throw new Exception("A default-data model could not use compatible size options");
            var sourceManifest=File.ReadAllBytes(Path.Combine(directory,"meta.json"));
            var generated=Directory.CreateDirectory(Path.Combine(directory,"generated")).FullName;
            File.WriteAllText(Path.Combine(generated,"meta.json"),JsonSerializer.Serialize(new
                {FileVersion=4,Identifier=Guid.NewGuid(),Name="Family fixture",FamilySourceDirectory="Original outfit",FamilyGamePath=game}));
            var model=Path.Combine(directory,"model.mdl");File.WriteAllBytes(model,[1,2,3]);
            bool Save(string option)=>(bool)type.GetMethod("SaveEditable")!.Invoke(null,
                [generated,game,"Aetherform",option,option,new Dictionary<string,string>{{"_top","chest.mdl"}},model,"Original mod","large.mdl"])!;
            if(!Save("Medium")) throw new Exception("Family fit was not saved");
            using var record=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated,"Proteus","bodyretarget.json")));
            var entry=record.RootElement.GetProperty("Options")[0];
            var edited=Path.Combine(generated,entry.GetProperty("File").GetString()!);
            File.WriteAllBytes(edited,[9,8,7]);
            if(Save("Medium") || !Save("Large") || !File.ReadAllBytes(edited).SequenceEqual(new byte[]{9,8,7}) ||
                !File.ReadAllBytes(Path.Combine(directory,"meta.json")).SequenceEqual(sourceManifest) || entry.GetProperty("FromGame").GetBoolean())
                throw new Exception("Saving a family fit changed source options, provenance or an edited fit");
            using var generatedMeta=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated,"meta.json")));
            if(generatedMeta.RootElement.GetProperty("FamilySourceDirectory").GetString()!="Original outfit")
                throw new Exception("Saving lost the source-mod link needed to track option changes");
            Console.WriteLine("Family source resolution, group priority, cross-family size candidates and coverage preservation passed.");
            Console.WriteLine("Original mod options, family provenance and edited-fit persistence passed.");
            Console.WriteLine("Combining model variants and physics-option preservation passed.");
        }
        finally {Directory.Delete(directory,true);}
    }
}
