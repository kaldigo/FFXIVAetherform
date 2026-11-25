using Proteus.Services;

namespace Aetherform.Geometry;

// Transfer contact weights through the same host triangle used for placement.
internal static class SurfaceSkinning
{
    internal static byte[] PromoteInfluences(byte[] model)
    {
        var layout=SecondSkinWriter.Parse(model);
        var draws=new List<LodMeshWriter.Draw>();
        for(int mesh=layout.Lod0MeshIndex;mesh<layout.Lod0MeshIndex+layout.Lod0MeshCount;mesh++)
        {
            var weight=layout.Decls[mesh].First(e=>e.Usage==1);
            if(weight.Type==17) continue;
            var bone=layout.Decls[mesh].First(e=>e.Usage==2);
            if(weight.Type is not (5 or 8) || bone.Type!=5) throw new InvalidDataException("Unsupported surface skinning format.");
            var geometry=new GarmentClearance.Mesh(layout,mesh);
            var format=new SkinVertexFormat(model,mesh,geometry.Indices);
            int header=layout.MeshStart+36*mesh, count=geometry.P.Length;
            var streams=new byte[3][];
            for(int stream=0;stream<3;stream++)
            {
                int stride=model[header+32+stream];
                streams[stream]=new byte[count*format.Strides[stream]];
                int start=layout.Vb+BitConverter.ToInt32(model,header+20+4*stream);
                var ends=layout.Decls[mesh].Where(e=>e.Stream==stream && e.Usage is 1 or 2)
                    .Select(e=>e.Offset+4).Order().ToArray();
                for(int vertex=0;vertex<count;vertex++)
                {
                    int from=0,to=vertex*format.Strides[stream];
                    foreach(int end in ends)
                    {
                        Buffer.BlockCopy(model,start+vertex*stride+from,streams[stream],to,end-from);
                        to+=end-from+4;from=end;
                    }
                    Buffer.BlockCopy(model,start+vertex*stride+from,streams[stream],to,stride-from);
                }
            }
            int first=BitConverter.ToInt32(model,header+16);
            var submeshes=Enumerable.Range(BitConverter.ToUInt16(model,header+10),BitConverter.ToUInt16(model,header+12))
                .Select(sub=>(BitConverter.ToInt32(model,layout.SubmeshStart+16*sub)-first,BitConverter.ToInt32(model,layout.SubmeshStart+16*sub+4))).ToArray();
            var shapes=layout.Shapes.SelectMany(s=>s.Value.Where(e=>e.MeshIndexOffset==first).Select(e=>(s.Key,e.Values)))
                .GroupBy(s=>s.Key).ToDictionary(g=>g.Key,g=>g.SelectMany(s=>s.Values).ToArray());
            draws.Add(new(mesh,count,streams,geometry.Indices.Select(i=>checked((ushort)i)).ToArray(),submeshes,shapes,format.Declaration,format.Strides));
        }
        return LodMeshWriter.Replace(model,draws);
    }

    private static int Address(byte[] data,SecondSkinWriter.Source layout,int mesh,int vertex,SecondSkinWriter.VElem field)
    {
        int header=layout.MeshStart+36*mesh;
        return layout.Vb+BitConverter.ToInt32(data,header+20+4*field.Stream)+field.Offset+vertex*data[header+32+field.Stream];
    }

    internal static Dictionary<string,float> Read(byte[] data,SecondSkinWriter.Source layout,int mesh,int vertex)
    {
        var weight=layout.Decls[mesh].First(f=>f.Usage==1);
        var bone=layout.Decls[mesh].First(f=>f.Usage==2);
        if(!(weight.Type is 5 or 8 && bone.Type==5 || weight.Type==17 && bone.Type==17))
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

    internal static byte[] Apply(byte[] model,Dictionary<int,Dictionary<int,Dictionary<string,float>>> transfers,
        IEnumerable<byte[]>? boneDonors=null,IReadOnlySet<string>? lockedBones=null)
    {
        if(transfers.Count==0)return model;
        var original=SecondSkinWriter.Parse(model);
        var required=transfers.ToDictionary(p=>p.Key,p=>p.Value.Values.SelectMany(w=>w.Keys).Distinct().ToArray());
        foreach(var donor in boneDonors ?? [])
        {
            var names=SecondSkinWriter.Parse(donor).BoneNames.ToHashSet(StringComparer.Ordinal);
            var supplied=required.ToDictionary(p=>p.Key,p=>p.Value.Where(names.Contains).ToArray());
            model=SkinDrawBindings.Add(model,donor,original.MatNames[0],supplied,new HashSet<int>());
        }
        var result=SkinDrawBindings.Add(model,model,original.MatNames[0],required,new HashSet<int>());
        var layout=SecondSkinWriter.Parse(result);
        foreach(var (mesh,vertices) in transfers)
        {
            var weight=layout.Decls[mesh].First(f=>f.Usage==1);
            var bone=layout.Decls[mesh].First(f=>f.Usage==2);
            if(!(weight.Type is 5 or 8 && bone.Type==5 || weight.Type==17 && bone.Type==17))
                throw new InvalidDataException("Unsupported surface skinning format.");
            int count=weight.Type==17?8:4;
            var palette=layout.BoneTables[BitConverter.ToUInt16(result,layout.MeshStart+36*mesh+14)];
            var names=palette.Select(i=>layout.BoneNames[i]).ToArray();
            foreach(var (vertex,weights) in vertices)
            {
                var locked=lockedBones==null ? new Dictionary<string,float>() : Read(original.S,original,mesh,vertex)
                    .Where(p=>lockedBones.Contains(p.Key)).ToDictionary();
                if(locked.Count>count) throw new InvalidDataException("Too many protected garment influences.");
                var selected=locked.Concat(weights.Where(p=>p.Value>0 && !locked.ContainsKey(p.Key))
                    .OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).Take(count-locked.Count))
                    .OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal).ToArray();
                float total=selected.Sum(p=>p.Value);
                if(!(total>0))throw new InvalidDataException("Empty contact skinning.");
                int remaining=255-locked.Values.Sum(w=>(int)MathF.Round(255*w));
                double freeTotal=selected.Where(p=>!locked.ContainsKey(p.Key)).Sum(p=>(double)p.Value);
                var scaled=selected.Select(p=>locked.TryGetValue(p.Key,out float fixedValue) ? Math.Round(255d*fixedValue) :
                    lockedBones==null ? 255d*p.Value/total : freeTotal>0 ? remaining*p.Value/freeTotal : 0).ToArray();
                var packed=scaled.Select(v=>(int)Math.Floor(v)).ToArray();
                foreach(int i in Enumerable.Range(0,packed.Length).Where(i=>!locked.ContainsKey(selected[i].Key))
                    .OrderByDescending(i=>scaled[i]-packed[i]).ThenBy(i=>selected[i].Key,StringComparer.Ordinal).Take(255-packed.Sum()))packed[i]++;
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
