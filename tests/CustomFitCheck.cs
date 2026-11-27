using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

internal static class CustomFitCheck
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private sealed class Server : HttpMessageHandler
    {
        internal readonly Dictionary<string,byte[]> Files=[];
        internal readonly List<string> Requests=[];
        internal string Etag="\"one\"";
        internal bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            string path=request.RequestUri!.AbsolutePath.Split("/custom-fits/")[1];Requests.Add(path);
            if(Offline) throw new HttpRequestException("Offline test");
            if(path=="index.json" && request.Headers.IfNoneMatch.Any(v=>v.ToString()==Etag))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response=new HttpResponseMessage(Files.ContainsKey(path)?HttpStatusCode.OK:HttpStatusCode.NotFound)
            {Content=new ByteArrayContent(Files.GetValueOrDefault(path,[]))};
            if(path=="index.json") response.Headers.ETag=EntityTagHeaderValue.Parse(Etag);
            return Task.FromResult(response);
        }
    }
    internal static void Run(Assembly plugin,Assembly engine)
    {
        var recipeType=engine.GetType("Aetherform.Geometry.CustomFitRecipe",true)!;
        var libraryType=engine.GetType("Aetherform.Geometry.CustomFitLibrary",true)!;
        var bundleType=engine.GetType("Aetherform.Geometry.CustomFitBundle",true)!;
        var optionsType=engine.GetType("Aetherform.Geometry.CustomFitOptions",true)!;
        var storeType=plugin.GetType("Aetherform.CustomFitStore",true)!;
        const string game="chara/equipment/e0717/model/c0201e0717_top.mdl";
        byte[] source=[1,2,3],replacement=[4,5,6,7];
        byte[] Recipe(string kind)=>JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema=1,Id="test-coat",GamePath=game,SourceKind=kind,SourceHash=Hash(source),
            Default=new {Id="default",Name="Corrected",Edits=new[]{new {Mesh=0,Vertices=new[]{0},DeltaMm=new[]{0f,0f,1f}}}},
            Variants=new[]{new {Id="open",Name="Open",Mesh=new {Path="meshes/open.mdl",Sha256=Hash(replacement)}}}
        });
        object Read(byte[] bytes)=>recipeType.GetMethod("Read")!.Invoke(null,[bytes])!;
        object Library(string kind)
        {
            byte[] bytes=Recipe(kind);
            var bundle=Activator.CreateInstance(bundleType,[Read(bytes),Hash(bytes),new Dictionary<string,byte[]>{{Hash(replacement),replacement}}])!;
            var array=Array.CreateInstance(bundleType,1);array.SetValue(bundle,0);
            return Activator.CreateInstance(libraryType,[array])!;
        }
        void Reject(Action action)
        {
            try {action();} catch(TargetInvocationException ex) when(ex.InnerException is InvalidDataException) {return;}
            throw new Exception("Invalid custom fit data was accepted");
        }
        var invalid=JsonNode.Parse(Recipe("family"))!;invalid["Variants"]![0]!["Mesh"]!["Path"]="../escape.mdl";
        Reject(()=>Read(Encoding.UTF8.GetBytes(invalid.ToJsonString())));
        invalid=JsonNode.Parse(Recipe("family"))!;invalid["Schema"]=99;
        Reject(()=>Read(Encoding.UTF8.GetBytes(invalid.ToJsonString())));
        var finished=JsonNode.Parse(Recipe("vanilla"))!;finished["Schema"]=2;
        finished["Default"]!["Baked"]=JsonSerializer.SerializeToNode(new[]{new {
            Chest="Medium",Legs="Large",LegOptions=new[]{"Hip Dips","Soft Butt"},
            Targets=new Dictionary<string,string[]>{{"_top",[Hash(source)]},{"_dwn",[Hash(replacement)]}},
            Mesh=new {Path="meshes/finished.mdl",Sha256=Hash(replacement)} }});
        var fitted=Read(Encoding.UTF8.GetBytes(finished.ToJsonString()));
        object Plan()=>recipeType.GetProperty("Default")!.GetValue(fitted)!;
        var hashes=new Dictionary<string,string>{{"_top",Hash(source)},{"_dwn",Hash(replacement)}};
        object? Select(string chest,string legs,string[] options)=>recipeType.GetMethod("SelectBaked")!.Invoke(null,[Plan(),hashes,chest,legs,options]);
        if(Select("Yiggle - Medium","Yiggle - Large - A",["Soft Butt","Hip Dips"])==null ||
            Select("Yiggle - Medium","Yiggle - Large - C",["Hip Dips","Soft Butt"])==null ||
            Select("Yiggle - Small","Yiggle - Large - B",["Hip Dips","Soft Butt"])!=null ||
            Select("Yiggle - Medium","Yiggle - Medium - B",["Hip Dips","Soft Butt"])!=null ||
            Select("Yiggle - Medium","Yiggle - Large - B",["Hip Dips"])!=null)
            throw new Exception("Finished fit did not select by body size and shape options independently of genital label");
        hashes["_top"]=new string('a',64);
        if(Select("Yiggle - Medium","Yiggle - Large - B",["Hip Dips","Soft Butt"])!=null)
            throw new Exception("Finished fit accepted an unreviewed target body");
        var duplicate=finished.DeepClone();duplicate["Default"]!["Baked"]!.AsArray().Add(duplicate["Default"]!["Baked"]![0]!.DeepClone());
        Reject(()=>Read(Encoding.UTF8.GetBytes(duplicate.ToJsonString())));
        var legacy=finished.DeepClone();legacy["Schema"]=1;Reject(()=>Read(Encoding.UTF8.GetBytes(legacy.ToJsonString())));
        var library=Library("family");
        if(libraryType.GetMethod("Match")!.Invoke(library,[game,"family",new byte[]{9}])!=null ||
            libraryType.GetMethod("Match")!.Invoke(library,[game,"vanilla",source])!=null)
            throw new Exception("Custom recipe matched the wrong source hash/family");
        string Revision(object l,string path)=>(string)libraryType.GetMethod("Revision")!.Invoke(l,[new[]{path}])!;
        if(Revision(library,game).Length!=64 || Revision(library,game.Replace("0717","0001"))!="")
            throw new Exception("Custom revision affected unrelated items");
        string root=Path.Combine(AppContext.BaseDirectory,"test-output","custom-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root,"source.mdl"),source);
            var meta=JsonNode.Parse("""
            {"FileVersion":4,"PageNames":{"0":"Fit","5":"Textures"},"Groups":[
              {"Name":"Chest Size","Type":"Single","Page":0,"Priority":10,"DefaultSettings":0,
               "Options":[{"Id":"keep","Name":"Medium","Files":{},"Manipulations":[{"Keep":true}]}]},
              {"Name":"Details","Type":"Combining","Page":5,"Options":[{"Name":"Bow"}],"Containers":[{},{}]}]}
            """)!.AsObject();
            meta["Groups"]![0]!["Options"]![0]!["Files"]![game]="source.mdl";
            var original=meta.DeepClone();var options=Activator.CreateInstance(optionsType)!;
            optionsType.GetMethod("Append")!.Invoke(options,[meta,root,library]);
            if(!JsonNode.DeepEquals(original["PageNames"],meta["PageNames"]) ||
                !JsonNode.DeepEquals(original["Groups"]![0]!["Options"]![0],meta["Groups"]![0]!["Options"]![0]) ||
                !JsonNode.DeepEquals(original["Groups"]![1],meta["Groups"]![1]))
                throw new Exception("Custom variants changed existing options or tabs");
            string name=meta["Groups"]![0]!["Options"]![1]!["Name"]!.GetValue<string>();
            var normalized=(Dictionary<string,List<string>>)optionsType.GetMethod("Normalize")!.Invoke(null,[new Dictionary<string,List<string>>{{"Chest Size",[name]}}])!;
            if(normalized["Chest Size"].Single()!="Medium") throw new Exception("Custom option did not preserve its source selection");
            var active=new Dictionary<string,List<string>>{{"Chest Size",[name]}};
            var restore=optionsType.GetMethod("Selections")!;
            if(((Dictionary<string,List<string>>)restore.Invoke(null,[meta,active,library])!)["Chest Size"].Single()!=name ||
                ((Dictionary<string,List<string>>)restore.Invoke(null,[original,active,library])!)["Chest Size"].Single()!="Medium")
                throw new Exception("A removed/incompatible recipe did not restore the original source option");
            // Run the actual standalone builder with the augmented option tree.
            string sourceMod=Directory.CreateDirectory(Path.Combine(root,"source-mod")).FullName;
            File.WriteAllBytes(Path.Combine(sourceMod,"source.mdl"),source);
            File.WriteAllText(Path.Combine(sourceMod,"meta.json"),original.ToJsonString());
            string output=Path.Combine(root,"export");var exportOptions=Activator.CreateInstance(optionsType)!;
            var modelType=engine.GetType("Aetherform.Geometry.StandaloneRefit+Model",true)!;
            int converted=0,customized=0;
            Aetherform.Geometry.StandaloneRefit.Build(sourceMod,output,"test-shape","test-recipe",new Dictionary<string,List<string>>(),m=>
            {
                converted++;
                var engineModel=Activator.CreateInstance(modelType,[m.GamePath,m.File,m.Slot,m.Race,m.Settings])!;
                var binding=optionsType.GetMethod("For")!.Invoke(exportOptions,[engineModel]);
                if(binding!=null) {customized++;return replacement;}
                return File.ReadAllBytes(m.File);
            },augment:node=>optionsType.GetMethod("Append")!.Invoke(exportOptions,[node,sourceMod,library]));
            if(converted!=2 || customized!=1) throw new Exception("Standalone custom variant did not reach its own conversion context");
            var export=JsonNode.Parse(File.ReadAllText(Path.Combine(output,"meta.json")))!;
            if(!JsonNode.DeepEquals(export["PageNames"],original["PageNames"]) ||
                !File.ReadAllBytes(Path.Combine(output,export["Groups"]![0]!["Options"]![1]!["Files"]![game]!.GetValue<string>())).SequenceEqual(replacement))
                throw new Exception("Standalone export lost a custom model or original tabs");
            // Combining containers stay untouched; variants get their own page.
            var combining=original.DeepClone().AsObject();combining["Groups"]![0]!["Type"]="Multi";
            combining["DefaultData"]=new JsonObject {["Files"]=new JsonObject {[game]="source.mdl"}};
            optionsType.GetMethod("Append")!.Invoke(Activator.CreateInstance(optionsType),[combining,root,library]);
            if(combining["PageNames"]!["5"]!.GetValue<string>()!="Textures" || combining["Groups"]!.AsArray().Count!=3)
                throw new Exception("Custom default variants lost pages or combining groups");
            // The vanilla selector retains IDs/indices and retires stale redirects.
            File.WriteAllText(Path.Combine(root,"meta.json"),original.ToJsonString());File.WriteAllBytes(Path.Combine(root,"fit.mdl"),replacement);
            var vtype=optionsType.GetNestedType("VanillaVariant")!;
            var varray=Array.CreateInstance(vtype,1);varray.SetValue(Activator.CreateInstance(vtype,["test/open","Open",Path.Combine(root,"fit.mdl")]),0);
            optionsType.GetMethod("SaveVanilla")!.Invoke(null,[root,game,varray]);
            var saved=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"meta.json")))!;
            string optionId=saved["Groups"]![2]!["Options"]![1]!["Id"]!.GetValue<string>();
            optionsType.GetMethod("SaveVanilla")!.Invoke(null,[root,game,Array.CreateInstance(vtype,0)]);
            saved=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"meta.json")))!;
            if(saved["Groups"]![2]!["Options"]![1]!["Id"]!.GetValue<string>()!=optionId || saved["Groups"]![2]!["Options"]![1]!["Files"]!.AsObject().Count!=0)
                throw new Exception("Retired custom variants moved option indices or left stale geometry");

            var server=new Server();byte[] recipe=Encoding.UTF8.GetBytes(finished.ToJsonString());server.Files["meshes/finished.mdl"]=replacement;server.Files["recipes/coat.json"]=recipe;server.Files["meshes/open.mdl"]=replacement;
            byte[] Index(string hash)=>JsonSerializer.SerializeToUtf8Bytes(new {Schema=1,Entries=new[]{
                new {Id="test-coat",GamePath=game,SourceKind="vanilla",SourceHash=Hash(source),Recipe="recipes/coat.json",Sha256=hash},
                new {Id="unrelated",GamePath=game.Replace("0717","0119"),SourceKind="vanilla",SourceHash=Hash(source),Recipe="recipes/unrelated.json",Sha256=new string('b',64)}}});
            server.Files["index.json"]=Index(Hash(recipe));
            var warnings=new List<string>();
            object NewStore(HttpMessageHandler handler)=>Activator.CreateInstance(storeType,All,null,[root,(Action<string>)warnings.Add,handler],null)!;
            object Current(object s)=>storeType.GetProperty("Current",All)!.GetValue(s)!;
            void Poll(object s)=>storeType.GetMethod("Poll",All)!.Invoke(s,null);
            void Finish(object s)
            {
                var timer=System.Diagnostics.Stopwatch.StartNew();
                while(storeType.GetField("work",All)!.GetValue(s) is Task t && !t.IsCompleted)
                {if(timer.Elapsed.TotalSeconds>5) throw new Exception("Custom download did not complete");Thread.Sleep(5);}
                Poll(s);
            }
            object store=NewStore(server);
            try
            {
                storeType.GetMethod("Request",All)!.Invoke(store,[game]);Poll(store);Finish(store);
                string good=Revision(Current(store),game);
                if(good.Length!=64 || server.Requests.Count!=3) throw new Exception("Custom assets were not fetched on demand");
                int count=server.Requests.Count;Poll(store);Finish(store);
                if(server.Requests.Count!=count) throw new Exception("Cached custom fit caused repeated network requests");
                storeType.GetField("nextCheck",All)!.SetValue(store,0L);Poll(store);Finish(store);
                if(server.Requests.Count!=count+1 || Revision(Current(store),game)!=good) throw new Exception("ETag refresh redownloaded unchanged assets");
                // Broken update retains the last good recipe, including offline restart.
                server.Etag="\"two\"";server.Files["index.json"]=Index(new string('a',64));
                storeType.GetField("nextCheck",All)!.SetValue(store,0L);Poll(store);Finish(store);
                if(Revision(Current(store),game)!=good || warnings.Count==0) throw new Exception("Bad update replaced the good custom fit");
            }
            finally {((IDisposable)store).Dispose();}
            var offline=new Server {Offline=true};store=NewStore(offline);
            try
            {
                if(Revision(Current(store),game).Length!=64) throw new Exception("Offline restart lost the last good custom fit");
                Poll(store);Finish(store);
                if(Revision(Current(store),game).Length!=64) throw new Exception("Offline refresh lost custom fits");
            }
            finally {((IDisposable)store).Dispose();}
        }
        finally {Directory.Delete(root,true);}
        Console.WriteLine("Custom fit source matching, variants/tabs, cache revisions, on-demand downloads, ETags, corrupt-update rollback and offline persistence passed.");
    }
}
