using System.Text.Json.Nodes;
using Aetherform.Geometry;

internal static class StandaloneRefitCheck
{
    internal static void Run()
    {
        if(!Aetherform.RefitOverride.Protected(true,0,-1) ||
            !Aetherform.RefitOverride.Protected(true,1,0) ||
            Aetherform.RefitOverride.Protected(false,0,-1) ||
            Aetherform.RefitOverride.Protected(true,-1,-1) ||
            Aetherform.RefitOverride.Protected(true,-2,-1) ||
            Aetherform.RefitOverride.Protected(true,0,0) ||
            !Aetherform.RefitOverride.Protected(true,int.MaxValue,int.MinValue))
            throw new Exception("Manual generated overrides or first-time managed activation were misclassified");
        Console.WriteLine("Explicit higher-priority overrides are protected; new and normal managed fits remain automatic.");
        if(!FamilyRefitOrder.Prefer(true,.020f,.025f,false,.001f,.002f) ||
            FamilyRefitOrder.Prefer(false,.001f,.002f,true,.020f,.025f) ||
            !FamilyRefitOrder.Prefer(true,.005f,.010f,true,.020f,.025f) ||
            FamilyRefitOrder.Prefer(true,.020f,.025f,true,.005f,.010f) ||
            !FamilyRefitOrder.Prefer(false,.005f,.010f,false,.020f,.025f) ||
            !FamilyRefitOrder.Prefer(true,.005f,.008f,true,.005f,.010f))
            throw new Exception("Topology priority, closest-size selection or UV fallback ordering failed");
        Console.WriteLine("Direct topology outranks closer UV candidates; closest-size ordering and UV fallback passed.");
        var steps=new List<string>();
        bool originalEnabled=true,newEnabled=false;
        StandaloneRefit.Activate(()=>steps.Add("configured"),value=>{newEnabled=value;steps.Add("new="+value);},false,
            [(Action<bool>)(value=>{originalEnabled=value;steps.Add("original="+value);})]);
        if(originalEnabled || !newEnabled || !steps.SequenceEqual(new[]{"configured","new=True","original=False"}))
            throw new Exception("Original was not disabled after replacement activation");
        originalEnabled=true;newEnabled=false;
        try
        {
            StandaloneRefit.Activate(()=>throw new IOException("settings failed"),value=>newEnabled=value,false,
                [(Action<bool>)(value=>originalEnabled=value)]);
            throw new Exception("Configuration failure was swallowed");
        }
        catch(IOException) { }
        if(!originalEnabled || newEnabled) throw new Exception("Configuration failure disabled original");
        try
        {
            StandaloneRefit.Activate(()=>{},value=>newEnabled=value,false,
                [(Action<bool>)(value=>{originalEnabled=value;if(!value) throw new IOException("disable failed");})]);
            throw new Exception("Handoff failure was swallowed");
        }
        catch(IOException) { }
        if(!originalEnabled || newEnabled) throw new Exception("Failed handoff did not restore the original");
        Console.WriteLine("Standalone activation order and failed-handoff recovery passed.");
        string root=Path.Combine(AppContext.BaseDirectory,"test-output","standalone-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source=Directory.CreateDirectory(Path.Combine(root,"source")).FullName;
            string external=Path.Combine(root,"external.tex");File.WriteAllBytes(external,[4,5,6]);
            foreach(string file in new[]{"base.mdl","small.mdl","large.mdl","covered.mdl","open.mdl","male.mdl","material.mtrl","cover.jpg","preview.png"})
                File.WriteAllBytes(Path.Combine(source,file),[1,2,3]);
            const string game="chara/equipment/e0543/model/c0201e0543_top.mdl";
            var meta=JsonNode.Parse("""
            {
              "FileVersion":4,"Identifier":"32ec24be-2898-46cf-8983-0218a33f2715","Name":"Outfit","Author":"Original author",
              "PageNames":{"0":"Fit","2":"Details","5":"Textures"},"ModTags":["gear"],"CustomField":{"Keep":true},
              "DefaultData":{"Files":{},"FileSwaps":{"chara/texture/a.tex":"chara/texture/b.tex"},"Manipulations":[{"Type":"Imc","Entry":{"MaterialId":3}}]},
              "Groups":[
                {"Name":"Breast Size","Type":"Single","Id":"size","Page":0,"Priority":10,"DefaultSettings":0,
                  "Options":[{"Name":"Small","Image":"preview.png","Files":{}},{"Name":"Large","Files":{}}]},
                {"Name":"Details","Type":"Multi","Page":2,"Priority":20,"DefaultSettings":0,
                  "Options":[{"Name":"Accessory","Priority":3,"Files":{}},{"Name":"Dye","Files":{}}]},
                {"Name":"Cut","Type":"Combining","Page":2,"DefaultSettings":0,
                  "Options":[{"Name":"Open"}],"Containers":[{"Files":{}},{"Files":{}}]},
                {"Name":"Textures","Type":"Single","Page":5,"DefaultSettings":0,
                  "Options":[{"Name":"Original","Files":{}}]},
                {"Name":"Visibility","Type":"Imc","Page":2,"DefaultSettings":1,
                  "Options":[{"Name":"Hide trim","AttributeMask":4}],"Identifier":{"ObjectType":"Equipment","PrimaryId":543}}
              ]
            }
            """)!.AsObject();
            var groups=meta["Groups"]!.AsArray();
            meta["DefaultData"]!["Files"]![game]="base.mdl";
            groups[0]!["Options"]![0]!["Files"]![game]="small.mdl";
            groups[0]!["Options"]![1]!["Files"]![game]="large.mdl";
            groups[1]!["Options"]![0]!["Files"]!["chara/texture/accessory.tex"]=external;
            groups[1]!["Options"]![1]!["Files"]!["chara/material/a.mtrl"]="material.mtrl";
            groups[2]!["Containers"]![0]!["Files"]![game]="covered.mdl";
            groups[2]!["Containers"]![1]!["Files"]![game]="open.mdl";
            groups[3]!["Options"]![0]!["Files"]!["chara/equipment/e0543/model/c0101e0543_top.mdl"]="male.mdl";
            string manifest=Path.Combine(source,"meta.json");File.WriteAllText(manifest,meta.ToJsonString());
            if(StandaloneRefit.ModelCount(source)!=5)
                throw new Exception("Queue sizing lost default/combining models or counted textures and unsupported races");
            byte[] original=File.ReadAllBytes(manifest);
            string revision=StandaloneRefit.Revision(source);
            string destination=Path.Combine(root,StandaloneRefit.DirectoryName(source,"shape",revision));
            var active=new Dictionary<string,List<string>>{{"Breast Size",["Large"]},{"Details",["Accessory","Dye"]},{"Cut",["Open"]},{"Visibility",["Hide trim"]}};
            var calls=new List<StandaloneRefit.Model>();
            StandaloneRefit.Build(source,destination,"shape",revision,active,model=>
            {
                calls.Add(model);
                if(Path.GetFileName(model.File)=="open.mdl" && !model.Settings["Cut"].SequenceEqual(new[]{"Open"}))
                    throw new Exception("Combining model lost its flag context");
                if(Path.GetFileName(model.File)=="small.mdl" && !model.Settings["Breast Size"].SequenceEqual(new[]{"Small"}))
                    throw new Exception("Inactive size did not receive its own source context");
                return [9,8,7];
            });
            if(calls.Count!=5 || !calls.Select(m=>Path.GetFileName(m.File)).ToHashSet().SetEquals(new[]{"base.mdl","small.mdl","large.mdl","covered.mdl","open.mdl"}))
                throw new Exception("Not all clothing variants were converted");
            var emitted=StandaloneRefit.ReadManifest(destination);
            var emittedGroups=emitted["Groups"]!.AsArray();
            if(!JsonNode.DeepEquals(meta["PageNames"],emitted["PageNames"]) ||
                !JsonNode.DeepEquals(meta["CustomField"],emitted["CustomField"]) ||
                !JsonNode.DeepEquals(meta["DefaultData"]!["Manipulations"],emitted["DefaultData"]!["Manipulations"]) ||
                !JsonNode.DeepEquals(meta["DefaultData"]!["FileSwaps"],emitted["DefaultData"]!["FileSwaps"]) ||
                !JsonNode.DeepEquals(groups[4],emittedGroups[4]))
                throw new Exception("Tabs, unknown metadata, swaps or IMC controls were lost");
            for(int i=0;i<groups.Count;i++)
            {
                if(groups[i]!["Page"]!.GetValue<int>()!=emittedGroups[i]!["Page"]!.GetValue<int>() ||
                    groups[i]!["Name"]!.GetValue<string>()!=emittedGroups[i]!["Name"]!.GetValue<string>())
                    throw new Exception("Tab/group ordering changed");
                var before=groups[i]!["Options"]!.AsArray();var after=emittedGroups[i]!["Options"]!.AsArray();
                if(!before.Select(o=>o!["Name"]!.GetValue<string>()).SequenceEqual(after.Select(o=>o!["Name"]!.GetValue<string>())))
                    throw new Exception("Option ordering changed");
            }
            var defaults=StandaloneRefit.Defaults(emitted);
            foreach(var (name,options) in active)
                if(!defaults[name].SequenceEqual(options)) throw new Exception("Active choices were not copied into defaults");
            string texture=emittedGroups[1]!["Options"]![0]!["Files"]!["chara/texture/accessory.tex"]!.GetValue<string>();
            string preview=emittedGroups[0]!["Options"]![0]!["Image"]!.GetValue<string>();
            if(!File.Exists(Path.Combine(destination,preview))) throw new Exception("Option preview image was lost");
            if(Path.IsPathFullyQualified(texture) || !File.ReadAllBytes(Path.Combine(destination,texture)).SequenceEqual(new byte[]{4,5,6}))
                throw new Exception("An external asset still depends on its original location");
            if(!File.Exists(Path.Combine(destination,"cover.jpg")) || !File.ReadAllBytes(manifest).SequenceEqual(original))
                throw new Exception("The cover was lost or source mod was changed");
            // Removing source and external assets must leave the export usable.
            Directory.Move(source,source+"-moved");File.Move(external,external+"-moved");
            foreach(var model in calls)
            {
                // Every converted option points to a local file; inspect independently below.
                if(File.Exists(model.File)) throw new Exception("Source relocation fixture failed");
            }
            void CheckFiles(JsonNode? node)
            {
                if(node is JsonObject obj)
                {
                    if(obj["Files"] is JsonObject files)
                        foreach(var file in files)
                            if(Path.IsPathFullyQualified(file.Value!.GetValue<string>()) || !File.Exists(Path.Combine(destination,file.Value.GetValue<string>())))
                                throw new Exception("Export depends on original mod assets");
                    foreach(var child in obj) CheckFiles(child.Value);
                }
                else if(node is JsonArray array) foreach(var child in array) CheckFiles(child);
            }
            CheckFiles(emitted);
            Directory.Move(source+"-moved",source);File.Move(external+"-moved",external);
            string fitted=emitted["DefaultData"]!["Files"]![game]!.GetValue<string>();
            File.WriteAllBytes(Path.Combine(destination,fitted),[6,6,6]);
            StandaloneRefit.Build(source,destination,"shape",revision,active,_=>throw new Exception("Existing export was regenerated"));
            if(!File.ReadAllBytes(Path.Combine(destination,fitted)).SequenceEqual(new byte[]{6,6,6})) throw new Exception("Edited standalone model overwritten");
            File.AppendAllText(Path.Combine(source,"open.mdl"),"changed");
            if(StandaloneRefit.Revision(source)==revision) throw new Exception("Inactive variant changes did not invalidate the export");
            string failed=Path.Combine(root,"failed");
            try { StandaloneRefit.Build(source,failed,"shape",revision,active,_=>throw new InvalidDataException("declined"));throw new Exception("Conversion failure ignored"); }
            catch(InvalidDataException) { }
            if(Directory.Exists(failed) || Directory.EnumerateDirectories(root,"*.building-*").Any() || !File.ReadAllBytes(manifest).SequenceEqual(original))
                throw new Exception("Failed export left partial output or changed the source");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try { StandaloneRefit.Build(source,failed,"shape",revision,active,_=>[1],cancelled.Token);throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { }
            if(Directory.Exists(failed) || Directory.EnumerateDirectories(root,"*.building-*").Any()) throw new Exception("Cancelled export left partial output");
            try
            {
                bool changed=false;
                StandaloneRefit.Build(source,failed,"shape",revision,active,_=>
                {
                    if(!changed) { File.AppendAllText(Path.Combine(source,"base.mdl"),"concurrent edit");changed=true; }
                    return [1,2,3];
                });
                throw new Exception("Source edit during export was ignored");
            }
            catch(IOException) { }
            if(Directory.Exists(failed) || Directory.EnumerateDirectories(root,"*.building-*").Any()) throw new Exception("A concurrent source edit left a partial export");
            meta["DefaultData"]!["FileSwaps"]![game]="chara/equipment/e0543/model/c0201e0543_dwn.mdl";
            File.WriteAllText(manifest,meta.ToJsonString());
            try { StandaloneRefit.Build(source,failed,"shape",revision,active,_=>[1]);throw new Exception("Unconverted equipment file swap was exported"); }
            catch(InvalidDataException) { }
            if(Directory.Exists(failed)) throw new Exception("Unconverted file swap left an incomplete export");
            string legacy=Directory.CreateDirectory(Path.Combine(root,"legacy")).FullName;
            File.WriteAllText(Path.Combine(legacy,"meta.json"),"""{"FileVersion":3,"Name":"Old mod","PageNames":{"3":"Legacy tab"}}""");
            File.WriteAllText(Path.Combine(legacy,"group_001.json"),"""{"Name":"Legacy group","Type":"Single","Page":3,"Options":[{"Name":"Normal","Files":{},"Swaps":{"a":"b"}}]}""");
            File.WriteAllText(Path.Combine(legacy,"default_mod.json"),"""{"Files":{},"Swaps":{"x":"y"},"Manipulations":[]}""");
            var normalized=StandaloneRefit.ReadManifest(legacy);
            if(normalized["DefaultData"]!["FileSwaps"]!["x"]!.GetValue<string>()!="y" ||
                normalized["Groups"]![0]!["Options"]![0]!["FileSwaps"]!["a"]!.GetValue<string>()!="b" ||
                normalized["Groups"]![0]!["Page"]!.GetValue<int>()!=3 || normalized["PageNames"]!["3"]!.GetValue<string>()!="Legacy tab")
                throw new Exception("Legacy mod tabs or swaps were lost");
            Console.WriteLine("Standalone tabs, page names, group/option ordering, all model variants, assets, metadata and selections passed.");
            Console.WriteLine("Source independence, edited-output preservation, inactive-variant invalidation, failure/cancellation cleanup and legacy tabs passed.");
        }
        finally { Directory.Delete(root,true); }
    }
}
