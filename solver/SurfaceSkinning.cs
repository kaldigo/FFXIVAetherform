using Proteus.Services;

namespace Aetherform.Geometry;

// Transfer contact weights through the same host triangle used for placement.
internal static class SurfaceSkinning
{
    private static int Address(byte[] data,SecondSkinWriter.Source layout,int mesh,int vertex,SecondSkinWriter.VElem field)
    {
        int header=layout.MeshStart+36*mesh;
        return layout.Vb+BitConverter.ToInt32(data,header+20+4*field.Stream)+field.Offset+vertex*data[header+32+field.Stream];
    }

    internal static Dictionary<string,float> Read(byte[] data,SecondSkinWriter.Source layout,int mesh,int vertex)
    {
        var weight=layout.Decls[mesh].First(f=>f.Usage==1);
        var bone=layout.Decls[mesh].First(f=>f.Usage==2);
        if(weight.Type is not (5 or 17) || bone.Type!=weight.Type)
            throw new InvalidDataException("Unsupported surface skinning format.");
        int at=Address(data,layout,mesh,vertex,weight),indices=Address(data,layout,mesh,vertex,bone);
        var palette=layout.BoneTables[BitConverter.ToUInt16(data,layout.MeshStart+36*mesh+14)];
        var result=new Dictionary<string,float>();
        for(int i=0;i<(weight.Type==17?8:4);i++)
        {
            if(data[at+i]==0)continue;
            string name=layout.BoneNames[palette[data[indices+i]]];
            result[name]=result.GetValueOrDefault(name)+data[at+i]/255f;
        }
        return result;
    }

    internal static byte[] Apply(byte[] model,Dictionary<int,Dictionary<int,Dictionary<string,float>>> transfers)
    {
        if(transfers.Count==0)return model;
        var original=SecondSkinWriter.Parse(model);
        var required=transfers.ToDictionary(p=>p.Key,p=>p.Value.Values.SelectMany(w=>w.Keys).Distinct().ToArray());
        var result=SkinDrawBindings.Add(model,model,original.MatNames[0],required,new HashSet<int>());
        var layout=SecondSkinWriter.Parse(result);
        foreach(var (mesh,vertices) in transfers)
        {
            var weight=layout.Decls[mesh].First(f=>f.Usage==1);
            var bone=layout.Decls[mesh].First(f=>f.Usage==2);
            if(weight.Type is not (5 or 17) || bone.Type!=weight.Type)
                throw new InvalidDataException("Unsupported surface skinning format.");
            int count=weight.Type==17?8:4;
            var palette=layout.BoneTables[BitConverter.ToUInt16(result,layout.MeshStart+36*mesh+14)];
            var names=palette.Select(i=>layout.BoneNames[i]).ToArray();
            foreach(var (vertex,weights) in vertices)
            {
                var selected=weights.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).Take(count).ToArray();
                float total=selected.Sum(p=>p.Value);
                if(!(total>0))throw new InvalidDataException("Empty contact skinning.");
                var scaled=selected.Select(p=>255d*p.Value/total).ToArray();
                var packed=scaled.Select(v=>(int)Math.Floor(v)).ToArray();
                foreach(int i in Enumerable.Range(0,packed.Length).OrderByDescending(i=>scaled[i]-packed[i]).Take(255-packed.Sum()))packed[i]++;
                int at=Address(result,layout,mesh,vertex,weight),indices=Address(result,layout,mesh,vertex,bone);
                Array.Clear(result,at,count);
                Array.Clear(result,indices,count);
                for(int i=0;i<selected.Length;i++)
                {
                    result[at+i]=checked((byte)packed[i]);
                    result[indices+i]=checked((byte)Array.IndexOf(names,selected[i].Key));
                }
            }
        }
        return result;
    }
}
