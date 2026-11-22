using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class GarmentSkinWriter
{
    internal static byte[] Replace(byte[] original,byte[] morphed,byte[] cloth,byte[] body,byte[] bare,Dictionary<int,ClothSubdivision.Origins> origins,GarmentClassification? classification,bool fitLegs)
    {
        var src=SecondSkinWriter.Parse(morphed);
        var meshes=Enumerable.Range(src.Lod0MeshIndex,src.Lod0MeshCount)
            .Where(m=>SecondSkinWriter.IsBodySkinMaterial(src.MatNames[BitConverter.ToUInt16(morphed,src.MeshStart+36*m+8)])).ToArray();
        if(meshes.Length==0)
        {
            ClothAttachments.Apply(original,cloth,origins,classification);
            return cloth;
        }
        var chart=new NativeSkinChart(body);
        var assembly=CutSkinAssembly.PrepareForFit(original,morphed,chart,bare);
        var topology=SkinDisplacement.Apply(chart,assembly,morphed,classification);
        cloth=GarmentClearance.Apply(original,morphed,cloth,assembly,origins,classification,topology);
        ClothAttachments.Apply(original,cloth,origins,classification);
        if(fitLegs)StockingContour.Apply(assembly,original,src,cloth,topology);
        var ids=Enumerable.Range(0,assembly.Cut.Triangles.Count)
            .ToLookup(i=>assembly.Corners[3*i].Carrier.Source.Mesh);
        var target=SecondSkinWriter.Parse(body);
        string material=target.MatNames[BitConverter.ToUInt16(body,target.MeshStart+36*chart.Mesh+8)];
        var bones=meshes.ToDictionary(m=>m,m=>ids[m].SelectMany(i=>assembly.Corners.AsSpan(3*i,3).ToArray())
            .SelectMany(c=>c.Skin.Weights).Select(w=>w.Bone).Distinct().ToArray());
        var schedules=assembly.Schedule.GroupBy(k=>k.Mesh).ToArray();
        foreach(var g in schedules) bones.Add(g.Key,g.SelectMany(k=>assembly.Knots[k.Variable].Weights).Select(w=>w.Bone).Distinct().ToArray());
        byte[] rebound=SkinDrawBindings.Add(cloth,body,material,bones,meshes.ToHashSet());
        rebound=SkinSurfaceContact.Apply(original,rebound,origins,assembly);
        var metadata=SecondSkinWriter.Parse(rebound);
        var format=new SkinVertexFormat(body,chart);
        var draws=new List<LodMeshWriter.Draw>();
        foreach(int mesh in meshes)
        {
            int h=src.MeshStart+36*mesh,first=BitConverter.ToInt32(morphed,h+16);
            int sub=BitConverter.ToUInt16(morphed,h+10),count=BitConverter.ToUInt16(morphed,h+12);
            var palette=metadata.BoneTables[BitConverter.ToUInt16(morphed,h+14)]
                .Select((b,i)=>(Name:metadata.BoneNames[b],Index:checked((byte)i))).GroupBy(p=>p.Name)
                .ToDictionary(g=>g.Key,g=>g.First().Index);
            var shapeMaps=src.Shapes.SelectMany(s=>s.Value.Where(e=>e.MeshIndexOffset==first)
                .Select(e=>(Name:s.Key,Values:e.Values.ToDictionary(v=>(int)v.Base,v=>(int)v.Replace))))
                .ToDictionary(e=>e.Name,e=>e.Values);
            var replacements=shapeMaps.Keys.ToDictionary(name=>name,name=>new List<(ushort,ushort)>());
            var rowId=new Dictionary<string,ushort>(StringComparer.Ordinal);
            var rows=new List<byte[]>();var indices=new List<ushort>();var subs=new (int,int)[count];
            ushort Intern(byte[] bytes)
            {
                string key=Convert.ToBase64String(bytes);
                if(rowId.TryGetValue(key,out var found)) return found;
                ushort id=checked((ushort)rows.Count);rowId.Add(key,id);rows.Add(bytes);return id;
            }
            var pe=src.Decls[mesh].First(e=>e.Usage==0);
            var pos=new Dictionary<int,Vector3>();var temp=new float[4];
            Vector3 PositionMm(int v)
            {
                if(pos.TryGetValue(v,out var q)) return q;
                int at=src.Vb+BitConverter.ToInt32(morphed,h+20+4*pe.Stream)+v*morphed[h+32+pe.Stream]+pe.Offset;
                SecondSkinWriter.ReadTyped(morphed,at,pe.Type,temp);
                return pos[v]=1000*new Vector3(temp[0],temp[1],temp[2]);
            }
            foreach(int domain in count==0?new[]{-1}:Enumerable.Range(sub,count))
            {
                int start=indices.Count;
                foreach(int t in ids[mesh].Where(i=>assembly.Cut.Triangles[i].Domain==domain))
                {
                    var cut=assembly.Cut.Triangles[t];
                    var bary=cut.Barycentric.Polygon;
                    for(int j=0;j<3;j++)
                    {
                        var corner=assembly.Corners[3*t+j];
                        var skin=corner.Skin;
                        var sample=corner.Carrier;
                        var parent=sample.Source;
                        var p=parent.SourceBarycentric;
                        var b=p.A+sample.Barycentric.X*(p.B-p.A)+sample.Barycentric.Y*(p.C-p.A);
                        double[] w=[1-b.X-b.Y,b.X,b.Y];
                        ushort id=Intern(format.Encode(cut.Face,bary[j],skin,palette));
                        foreach(var (name,map) in shapeMaps)
                        {
                            var shift=Vector3.Zero;
                            for(int k=0;k<3;k++)
                                if(map.TryGetValue(parent.First+k,out int v))
                                    shift+=(float)w[k]*(PositionMm(v)-PositionMm(parent.Vertices[k]));
                            if(shift==Vector3.Zero) continue;
                            ushort variant=Intern(format.Encode(cut.Face,bary[j],skin with {PositionMm=skin.PositionMm+shift},palette));
                            if(variant!=id) replacements[name].Add((checked((ushort)indices.Count),variant));
                        }
                        indices.Add(id);
                    }
                }
                if(count!=0) subs[domain-sub]=(start,indices.Count-start);
            }
            draws.Add(new(mesh,rows.Count,format.Streams(rows),indices.ToArray(),subs,
                replacements.ToDictionary(p=>p.Key,p=>p.Value.ToArray()),format.Declaration,format.Strides));
        }
        foreach(var g in schedules) draws.Add(ClothJoinWriter.Build(morphed,rebound,g.Key,g.ToArray(),assembly));
        return LodMeshWriter.Replace(rebound,draws);
    }
}
