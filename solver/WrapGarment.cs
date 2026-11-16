using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class WrapGarment
{
    private sealed record Mesh(string Material,int Count,int Start,int Stride,byte Type,ushort[] Indices)
    {
        public bool Skin => Material.Contains("b0001_",StringComparison.OrdinalIgnoreCase);
        public Vector3 Read(byte[] data,int i)
        {
            Span<float> tmp=stackalloc float[4];
            SecondSkinWriter.ReadTyped(data,Start+i*Stride,Type,tmp);
            return new(tmp[0],tmp[1],tmp[2]);
        }
        public void Write(byte[] data,int i,Vector3 p) =>
            SecondSkinWriter.WriteXYZ(data,Start+i*Stride,Type,p.X,p.Y,p.Z);
    }

    private static IEnumerable<Mesh> Meshes(byte[] data)
    {
        var src=SecondSkinWriter.Parse(data);
        for(int i=src.Lod0MeshIndex;i<src.Lod0MeshIndex+src.Lod0MeshCount;i++)
        {
            int mo=src.MeshStart+i*36;
            int count=BitConverter.ToUInt16(data,mo), length=BitConverter.ToInt32(data,mo+4);
            if(count==0 || length==0) continue;
            var pos=src.Decls[i].First(e=>e.Usage==0);
            if(pos.Type is not (2 or 3 or 14)) continue;
            var indices=new ushort[length];
            Buffer.BlockCopy(data,checked((int)src.Ib+BitConverter.ToInt32(data,mo+16)*2),indices,0,length*2);
            yield return new(src.MatNames[BitConverter.ToUInt16(data,mo+8)],count,
                checked((int)src.Vb+BitConverter.ToInt32(data,mo+20+4*pos.Stream)+pos.Offset),
                data[mo+32+pos.Stream],pos.Type,indices);
        }
    }

    // A sewn opening depends on both sides retaining the same cut and bone weights.
    internal static bool HasSkinJoin(byte[] garment)
    {
        var skin = new HashSet<(Vector3,Vector3)>();
        var cloth = new HashSet<(Vector3,Vector3)>();
        foreach (var mesh in Meshes(garment))
        {
            var boundary = new HashSet<(Vector3,Vector3)>();
            var points = Enumerable.Range(0,mesh.Count)
                .Select(i => Vector3.Round(mesh.Read(garment,i)*100000)).ToArray();
            for (int i=0;i<mesh.Indices.Length;i+=3)
            for (int j=0;j<3;j++)
            {
                var a=points[mesh.Indices[i+j]];
                var b=points[mesh.Indices[i+(j+1)%3]];
                if (a==b) continue;
                var edge=(a.X,a.Y,a.Z).CompareTo((b.X,b.Y,b.Z))<0 ? (a,b) : (b,a);
                if (!boundary.Add(edge)) boundary.Remove(edge);
            }
            (mesh.Skin ? skin : cloth).UnionWith(boundary);
        }
        return skin.Overlaps(cloth);
    }

    // Match the local distance on the same body part. A nearby opposing thigh
    // or the underside of a cut body island is a poor reference for this point.
    internal static Vector3 Contact(Vector3 source,Vector3 fitted,List<BodySurface> stock,List<BodySurface> targets,bool minimumOnly=false)
    {
        float nearest=minimumOnly ? 1f : .05f, gap=0;
        int part=-1;
        Vector3 normal=default;
        for(int i=0;i<stock.Count;i++)
        {
            if(!stock[i].Nearest(source,nearest,out var hit)) continue;
            nearest=hit.Distance;
            normal=hit.Normal; gap=Vector3.Dot(source-hit.Point,normal); part=i;
        }
        if(part<0 || !targets[part].Nearest(fitted,Math.Max(.10f,nearest+.05f),out var target) ||
           Vector3.Dot(normal,target.Normal)<.6f) return fitted;
        float displacement=gap-Vector3.Dot(fitted-target.Point,target.Normal);
        return minimumOnly && displacement<=0 ? fitted : fitted+target.Normal*displacement;
    }

    internal static void Apply(byte[] vanilla,byte[] result,VanillaAfWrap map,GarmentBindings bindings,string slot,bool attached,List<BodySurface> stock,List<BodySurface> targets)
    {
        var meshes=Meshes(vanilla).ToArray();
        var output=Meshes(result).ToArray();
        // Skin grafts can change vertex order and draw topology. Only transfer matching meshes.
        var memo=new Dictionary<(Vector3,bool),Vector3>();
        var corrections=new Dictionary<Vector3,List<(Vector3 P,Vector3 Delta)>>();
        const float reach=.035f;
        static Vector3 Cell(Vector3 p) => new(MathF.Floor(p.X/reach),MathF.Floor(p.Y/reach),MathF.Floor(p.Z/reach));
        // Bottoms with exposed skin carry its correction into the adjoining cloth.
        // Process skin first even on models which put their clothing meshes first.
        foreach(int m in Enumerable.Range(0,Math.Min(meshes.Length,output.Length)).OrderByDescending(i=>meshes[i].Skin))
        {
            var src=meshes[m]; var dst=output[m];
            if(src.Material!=dst.Material && !(src.Skin && dst.Skin)) continue;
            if(src.Count<dst.Count || !src.Indices.SequenceEqual(dst.Indices)) continue;
            for(int j=0;j<dst.Count;j++)
            {
                var p=src.Read(vanilla,j);
                if(!memo.TryGetValue((p,src.Skin),out var fitted))
                    memo[(p,src.Skin)]=fitted=map.Apply(bindings.Get(p,slot=="_dwn" && src.Skin),src.Skin);
                if(slot=="_dwn" || attached)
                {
                    if(attached && src.Skin) fitted=Contact(p,fitted,stock,targets);
                    var prior=map.Apply(bindings.Get(p,false),false);
                    var cell=Cell(p);
                    if(src.Skin)
                    {
                        if(!corrections.TryGetValue(cell,out var list)) corrections[cell]=list=[];
                        list.Add((p,fitted-prior));
                    }
                    else if(corrections.Count>0)
                    {
                        var near=new List<(float D,Vector3 Delta)>();
                        for(int x=-1;x<=1;x++)
                        for(int y=-1;y<=1;y++)
                        for(int z=-1;z<=1;z++)
                            if(corrections.TryGetValue(cell+new Vector3(x,y,z),out var list))
                                foreach(var sample in list)
                                {
                                    float d=Vector3.DistanceSquared(p,sample.P);
                                    if(d<reach*reach) near.Add((d,sample.Delta));
                                }
                        fitted=attached ? Contact(p,prior,stock,targets) : prior;
                        if(near.Count>0)
                        {
                            float total=0;
                            Vector3 delta=default;
                            foreach(var sample in near.OrderBy(s=>s.D).Take(8))
                            {
                                float w=MathF.Pow(sample.D+1e-9f,-1.5f);
                                total+=w; delta+=w*sample.Delta;
                            }
                            float fade=1-MathF.Sqrt(near.Min(s=>s.D))/reach;
                            fitted=Vector3.Lerp(fitted,prior+delta/total,fade*fade);
                        }
                    }
                }
                dst.Write(result,j,fitted);
            }
        }
    }
}
