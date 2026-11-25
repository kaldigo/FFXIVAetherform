using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class ClothJoinWriter
{
    private sealed record Node(double T,int Variable);
    internal static LodMeshWriter.Draw Build(byte[] morphed,byte[] model,int mesh,
        IReadOnlyList<SkinRoutes.ClothKnot> schedule,CutSkinAssembly.Assembly cut,
        Dictionary<int,ClothSubdivision.Origins>? origins=null)
    {
        var source=SecondSkinWriter.Parse(morphed);var src=SecondSkinWriter.Parse(model);
        int h=src.MeshStart+36*mesh,originalHeader=source.MeshStart+36*mesh;
        int old=BitConverter.ToInt32(morphed,originalHeader+16),first=BitConverter.ToInt32(model,h+16);
        int[] sourceTriangle(int start)=>Enumerable.Range(0,3)
            .Select(k=>(int)BitConverter.ToUInt16(morphed,source.Ib+2*(old+start+k))).ToArray();
        var ix=Enumerable.Range(0,BitConverter.ToInt32(model,h+4)).Select(k=>(int)BitConverter.ToUInt16(model,src.Ib+2*(first+k))).ToArray();
        var format=new SkinVertexFormat(model,mesh,ix);
        var edges=new Dictionary<(int,int),List<Node>>();var pins=new Dictionary<int,int>();
        foreach(var knot in schedule)
        {
            int[] v=sourceTriangle(knot.First);
            double[] w=[1-knot.Source.X-knot.Source.Y,knot.Source.X,knot.Source.Y];
            int full=Array.FindIndex(w,x=>x>1-1e-9);
            if(full>=0){pins[v[full]]=knot.Variable;continue;}
            int empty=Array.FindIndex(w,x=>Math.Abs(x)<1e-9);
            if(empty<0) throw new InvalidDataException("Expected a source garment boundary knot.");
            int a=(empty+1)%3,b=(empty+2)%3;
            if(v[a]>v[b]) (a,b)=(b,a);
            var key=(v[a],v[b]);
            if(!edges.TryGetValue(key,out var list)) edges.Add(key,list=[]);
            if(list.All(n=>n.Variable!=knot.Variable)) list.Add(new(w[b]/(w[a]+w[b]),knot.Variable));
        }
        var palette=src.BoneTables[BitConverter.ToUInt16(model,h+14)].Select(id=>src.BoneNames[id]).ToArray();
        var byName=palette.Select((name,i)=>(name,i)).GroupBy(p=>p.name).ToDictionary(g=>g.Key,g=>(byte)g.First().i);
        var fields=src.Decls[mesh];var pe=fields.First(e=>e.Usage==0);var we=fields.First(e=>e.Usage==1);var be=fields.First(e=>e.Usage==2);
        int At(SecondSkinWriter.VElem e,int v)=>src.Vb+BitConverter.ToInt32(model,h+20+4*e.Stream)+e.Offset+v*model[h+32+e.Stream];
        var pos=new Vector3[BitConverter.ToUInt16(model,h)];
        var weights=new NativeSkinChart.Influence[pos.Length][];var floats=new float[4];
        for(int v=0;v<pos.Length;v++)
        {
            SecondSkinWriter.ReadTyped(model,At(pe,v),pe.Type,floats);
            pos[v]=1000*new Vector3(floats[0],floats[1],floats[2]);
            weights[v]=Enumerable.Range(0,we.Type==17?8:4).Where(i=>model[At(we,v)+i]!=0)
                .Select(i=>new NativeSkinChart.Influence(palette[model[At(be,v)+i]],model[At(we,v)+i])).ToArray();
        }
        var shapeMaps=src.Shapes.SelectMany(s=>s.Value.Where(e=>e.MeshIndexOffset==first)
            .Select(e=>(Name:s.Key,Values:e.Values.ToDictionary(v=>(int)v.Base,v=>(int)v.Replace))))
            .ToDictionary(e=>e.Name,e=>e.Values);
        var changes=shapeMaps.Keys.ToDictionary(name=>name,name=>new List<(ushort,ushort)>());
        var rows=new List<byte[]>();var registry=new Dictionary<string,ushort>();
        var lineage=origins?.GetValueOrDefault(mesh);
        var restRows=new List<Vector3>();var parentRows=new List<int>();
        ushort Intern(byte[] payload,Vector3 rest)
        {
            string key=Convert.ToBase64String(payload);
            if(lineage!=null) key+="|"+BitConverter.SingleToInt32Bits(rest.X)+","+BitConverter.SingleToInt32Bits(rest.Y)+","+BitConverter.SingleToInt32Bits(rest.Z);
            if(registry.TryGetValue(key,out var id)) return id;
            id=checked((ushort)rows.Count);rows.Add(payload);restRows.Add(rest);registry.Add(key,id);return id;
        }
        var output=new List<ushort>();var starts=new int[ix.Length/3+1];
        Point[] basis=[new(0,0),new(1,0),new(0,1)];
        var used=new HashSet<int>();
        for(int i=0;i<ix.Length;i+=3)
        {
            starts[i/3]=output.Count;
            int[] v=[ix[i],ix[i+1],ix[i+2]];
            var knots=new Dictionary<Point,int>();
            var pieces=new List<(Point A,Point B,Point C)>{(basis[0],basis[1],basis[2])};
            for(int k=0;k<3;k++)
            {
                int next=(k+1)%3;
                if(pins.TryGetValue(v[k],out int pin)) knots[basis[k]]=pin;
                if(!edges.TryGetValue((Math.Min(v[k],v[next]),Math.Max(v[k],v[next])),out var list)) continue;
                Point prev=basis[k],end=basis[next];
                foreach(var n in list.OrderBy(n=>v[k]<v[next]?n.T:1-n.T))
                {
                    double t=v[k]<v[next]?n.T:1-n.T;var p=basis[k]+t*(end-basis[k]);
                    if(knots.ContainsKey(p)) continue;
                    int piece=pieces.FindIndex(z=>(z.A==prev && z.B==end)||(z.B==prev && z.C==end)||(z.C==prev && z.A==end));
                    if(piece<0) throw new InvalidDataException("Missing cloth boundary segment.");
                    var z=pieces[piece];var opposite=z.A==prev?z.C:z.B==prev?z.A:z.B;
                    pieces[piece]=(prev,p,opposite);pieces.Add((p,end,opposite));knots[p]=n.Variable;prev=p;
                }
            }
            foreach(var z in pieces)
            {
                if(lineage!=null) parentRows.Add(lineage.Parents[i/3]);
                foreach(var p in new[]{z.A,z.B,z.C})
                {
                    var known=knots.TryGetValue(p,out int id);
                    int primary=p==basis[0]?0:p==basis[1]?1:2;
                    var payload=known?cut.Knots[id]:new CutSkinAssembly.Knot(pos[v[primary]],weights[v[primary]]);
                    if(known) used.Add(id);
                    var skin=new NativeSkinChart.Payload(payload.PositionMm,default,default,payload.Weights);
                    double[] w=[1-p.X-p.Y,p.X,p.Y];
                    var rest=lineage==null ? Vector3.Zero : (float)w[0]*lineage.Positions[v[0]]+(float)w[1]*lineage.Positions[v[1]]+(float)w[2]*lineage.Positions[v[2]];
                    ushort index=Intern(format.Encode(i/3,p,skin,byName,false),rest);
                    foreach(var (name,map) in shapeMaps)
                    {
                        var shift=Vector3.Zero;int[] alt=[v[0],v[1],v[2]];bool changed=false;
                        for(int k=0;k<3;k++)
                            if(w[k]>0 && map.TryGetValue(i+k,out var replacement))
                            {
                                alt[k]=replacement;changed=true;
                                shift+=(float)w[k]*(pos[replacement]-pos[v[k]]);
                            }
                        if(!changed) continue;
                        var activeWeights=skin.Weights;
                        if(!known) activeWeights=weights[alt[primary]];
                        ushort key=Intern(format.Encode(i/3,p,skin with {PositionMm=skin.PositionMm+shift,Weights=activeWeights},byName,false,alt),rest);
                        if(key!=index) changes[name].Add((checked((ushort)output.Count),key));
                    }
                    output.Add(index);
                }
            }
        }
        if(schedule.Any(k=>!used.Contains(k.Variable))) throw new InvalidDataException($"Subdivision lost a scheduled seam in mesh {mesh}.");
        starts[^1]=output.Count;
        var subs=Enumerable.Range(BitConverter.ToUInt16(model,h+10),BitConverter.ToUInt16(model,h+12)).Select(sub=>
        {
            int s=src.SubmeshStart+16*sub,a=(BitConverter.ToInt32(model,s)-first)/3,b=a+BitConverter.ToInt32(model,s+4)/3;
            return (starts[a],starts[b]-starts[a]);
        }).ToArray();
        if(lineage!=null) origins![mesh]=new(restRows.ToArray(),parentRows.ToArray());
        return new(mesh,rows.Count,format.Streams(rows),output.ToArray(),subs,
            changes.ToDictionary(p=>p.Key,p=>p.Value.ToArray()),format.Declaration,format.Strides);
    }
}
