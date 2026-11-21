using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class DensitySubdivision
{
    private readonly record struct Face(int A,int B,int C,int Parent,Triangle Bary);
    private readonly record struct Edge(int A,int B)
    {
        internal static Edge Of(int a,int b)=>a<=b?new(a,b):new(b,a);
    }
    internal static byte[] Apply(byte[] source,byte[] morph,BodyDensity density,Dictionary<int,ClothSubdivision.Origins> origins,IReadOnlySet<int>? keepTopology=null)
    {
        var src=SecondSkinWriter.Parse(source);var refit=SecondSkinWriter.Parse(morph);
        var rims=ClothSubdivision.IdentifyJoins(src).Keys.ToLookup(k=>k.Mesh);
        var draws=new List<LodMeshWriter.Draw>();
        for(int m=src.Lod0MeshIndex;m<src.Lod0MeshIndex+src.Lod0MeshCount;m++)
        {
            if(keepTopology?.Contains(m)==true) continue;
            int h=src.MeshStart+36*m;
            if(SecondSkinWriter.IsBodySkinMaterial(src.MatNames[BitConverter.ToUInt16(source,h+8)])) continue;
            if(src.Decls[m].Any(e=>!PanelStreams.Supported(e))) continue;
            var original=new GarmentClearance.Mesh(src,m);var fit=new GarmentClearance.Mesh(refit,m);
            var p=fit.P.ToList();var rest=original.P.ToList();var ix=fit.Indices;
            if(ix.Length==0) continue;
            var data=new List<(int A,int B)>();
            var oldShapes=src.Shapes.SelectMany(shape=>shape.Value.Where(e=>e.MeshIndexOffset==BitConverter.ToInt32(source,h+16))
                .Select(e=>(shape.Key,Map:e.Values.ToDictionary(v=>(int)v.Base,v=>(int)v.Replace)))).ToDictionary(s=>s.Key,s=>s.Map);
            var locked=rims[m].Select(e=>Edge.Of(e.A,e.B)).ToHashSet();
            var unit=new Triangle(new(0,0),new(1,0),new(0,1));
            var faces=Enumerable.Range(0,ix.Length/3).Select(i=>new Face(ix[3*i],ix[3*i+1],ix[3*i+2],i,unit)).ToList();
            var physical=new Dictionary<Vector3,int>();var ids=p.Select(Index).ToList();
            int Index(Vector3 v){if(!physical.TryGetValue(v,out int i)) physical.Add(v,i=physical.Count);return i;}
            var fixedEdges=locked.Select(e=>Edge.Of(ids[e.A],ids[e.B])).ToHashSet();
            var done=new HashSet<(int,int,int)>();
            for(int step=0;step<20;step++)
            {
                var marked=new HashSet<Edge>();
                foreach(var f in faces)
                {
                    var key=(f.A,f.B,f.C);if(done.Contains(key)) continue;
                    var a=p[f.A];var b=p[f.B];var c=p[f.C];float area=.5f*Vector3.Cross(b-a,c-a).Length();
                    if(area<=1e-6f || area<=1.42f*density.Target((a+b+c)/3)){done.Add(key);continue;}
                    var edge=Edge.Of(f.A,f.B);float longest=Vector3.DistanceSquared(a,b);
                    float bc=Vector3.DistanceSquared(b,c),ca=Vector3.DistanceSquared(c,a);
                    if(bc>longest){edge=Edge.Of(f.B,f.C);longest=bc;}
                    if(ca>longest) edge=Edge.Of(f.C,f.A);
                    edge=Edge.Of(ids[edge.A],ids[edge.B]);
                    if(!fixedEdges.Contains(edge)) marked.Add(edge);
                }
                if(marked.Count==0) break;
                var splits=new Dictionary<Edge,int>();int saved=p.Count;
                int Mid(int a,int b)
                {
                    if(!marked.Contains(Edge.Of(ids[a],ids[b]))) return -1;
                    var edge=Edge.Of(a,b);if(splits.TryGetValue(edge,out int i)) return i;
                    var mid=(p[a]+p[b])*.5f;i=p.Count;p.Add(mid);rest.Add((rest[a]+rest[b])*.5f);
                    ids.Add(Index(mid));data.Add((a,b));splits.Add(edge,i);return i;
                }
                var next=new List<Face>();
                foreach(var f in faces)
                {
                    int ab=Mid(f.A,f.B),bc=Mid(f.B,f.C),ca=Mid(f.C,f.A);
                    if(ab<0 && bc<0 && ca<0){next.Add(f);continue;}
                    Point B(int i)=>i==f.A?f.Bary.A:i==f.B?f.Bary.B:i==f.C?f.Bary.C:i==ab?.5*(f.Bary.A+f.Bary.B):
                        i==bc?.5*(f.Bary.B+f.Bary.C):.5*(f.Bary.C+f.Bary.A);
                    var child=ClothSubdivision.Children(f.A,f.B,f.C,ab,bc,ca);
                    for(int i=0;i<child.Length;i+=3) next.Add(new(child[i],child[i+1],child[i+2],f.Parent,new(B(child[i]),B(child[i+1]),B(child[i+2]))));
                }
                if(p.Count>=65535 || (oldShapes.Count!=0 && next.Count*3>65535))
                {
                    int excess=p.Count-saved;p.RemoveRange(saved,excess);rest.RemoveRange(saved,excess);data.RemoveRange(data.Count-excess,excess);
                    break;
                }
                faces=next;
            }
            if(data.Count==0) continue;
            var stride=Enumerable.Range(0,3).Select(s=>(int)morph[h+32+s]).ToArray();
            var streams=stride.Select(s=>new byte[p.Count*s]).ToArray();
            for(int s=0;s<3;s++)
                if(stride[s]>0) Buffer.BlockCopy(morph,refit.Vb+BitConverter.ToInt32(morph,h+20+4*s),streams[s],0,fit.P.Length*stride[s]);
            for(int i=0;i<data.Count;i++)
                PanelStreams.Midpoint(streams,stride,refit.Decls[m],data[i].A,data[i].B,fit.P.Length+i);
            var indices=new List<ushort>();var variantRows=new List<byte[]>();var variantIds=new Dictionary<string,ushort>();
            var changes=oldShapes.Keys.ToDictionary(n=>n,n=>new List<(ushort,ushort)>());
            int[] starts=new int[ix.Length/3+1];int parent=-1;
            var field=refit.Decls[m].First(e=>e.Usage==0);
            foreach(var f in faces)
            {
                if(parent!=f.Parent){parent=f.Parent;starts[parent]=indices.Count;}
                var v=new[]{f.A,f.B,f.C};var bary=f.Bary.Polygon;
                for(int k=0;k<3;k++)
                {
                    int id=v[k];var u=bary[k];double[] w=[1-u.X-u.Y,u.X,u.Y];
                    foreach(var (name,map) in oldShapes)
                    {
                        bool affected=false;var shift=Vector3.Zero;int exact=-1;
                        for(int j=0;j<3;j++)
                            if(w[j]>0 && map.TryGetValue(3*parent+j,out int r))
                            {
                                affected=true;shift+=(float)w[j]*(fit.P[r]-fit.P[ix[3*parent+j]]);
                                if(w[j]==1) exact=r;
                            }
                        if(!affected) continue;
                        ushort variant;
                        if(exact>=0) variant=checked((ushort)exact);
                        else
                        {
                            var row=new byte[stride.Sum()];int at=0;
                            for(int s=0;s<3;s++){Buffer.BlockCopy(streams[s],id*stride[s],row,at,stride[s]);at+=stride[s];}
                            var q=(p[id]+shift)/1000;
                            SecondSkinWriter.WriteXYZ(row,stride.Take(field.Stream).Sum()+field.Offset,field.Type,q.X,q.Y,q.Z);
                            string key=Convert.ToBase64String(row);
                            if(!variantIds.TryGetValue(key,out variant))
                            {
                                variant=checked((ushort)(p.Count+variantRows.Count));variantRows.Add(row);variantIds.Add(key,variant);
                            }
                        }
                        changes[name].Add((checked((ushort)indices.Count),variant));
                    }
                    indices.Add(checked((ushort)id));
                }
            }
            starts[^1]=indices.Count;
            for(int s=0;s<3;s++)
            {
                int offset=stride.Take(s).Sum();Array.Resize(ref streams[s],(p.Count+variantRows.Count)*stride[s]);
                for(int i=0;i<variantRows.Count;i++) Buffer.BlockCopy(variantRows[i],offset,streams[s],(p.Count+i)*stride[s],stride[s]);
            }
            var subs=Enumerable.Range(BitConverter.ToUInt16(source,h+10),BitConverter.ToUInt16(source,h+12)).Select(s=>
            {
                int sh=src.SubmeshStart+16*s;int a=(BitConverter.ToInt32(source,sh)-BitConverter.ToInt32(source,h+16))/3;
                int b=a+BitConverter.ToInt32(source,sh+4)/3;return(starts[a],starts[b]-starts[a]);
            }).ToArray();
            draws.Add(new(m,p.Count+variantRows.Count,streams,indices.ToArray(),subs,changes.ToDictionary(s=>s.Key,s=>s.Value.ToArray())));
            var positions=rest.Select(x=>x/1000).ToList();positions.AddRange(Enumerable.Repeat(Vector3.Zero,variantRows.Count));
            origins.Add(m,new(positions.ToArray(),faces.Select(f=>f.Parent).ToArray()));
        }
        return LodMeshWriter.Replace(morph,draws);
    }
}
