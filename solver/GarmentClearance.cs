using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class GarmentClearance
{
    internal sealed class Mesh
    {
        internal readonly Vector3[] P,N;
        internal readonly int[] Indices;
        internal readonly byte[] Data;
        internal readonly Dictionary<int,List<int>> ShapeTargets=[];
        internal readonly HashSet<int> AmbiguousShapeBases=[];
        internal readonly int Header,Vb;
        internal readonly SecondSkinWriter.VElem Position;
        internal Mesh(SecondSkinWriter.Source src,int id)
        {
            Data=src.S;Vb=src.Vb;Header=src.MeshStart+36*id;
            int n=BitConverter.ToUInt16(Data,Header);P=new Vector3[n];N=new Vector3[n];
            Position=src.Decls[id].First(f=>f.Usage==0);var normal=src.Decls[id].First(f=>f.Usage==3);
            var components=new float[4];
            for(int i=0;i<n;i++)
            {
                SecondSkinWriter.ReadTyped(Data,At(Position,i),Position.Type,components);P[i]=1000*new Vector3(components[0],components[1],components[2]);
                SecondSkinWriter.ReadTyped(Data,At(normal,i),normal.Type,components);N[i]=new(components[0],components[1],components[2]);if(normal.Type==8) N[i]=2*N[i]-Vector3.One;
            }
            int first=BitConverter.ToInt32(Data,Header+16);
            Indices=Enumerable.Range(0,BitConverter.ToInt32(Data,Header+4)).Select(i=>(int)BitConverter.ToUInt16(Data,src.Ib+2*(first+i))).ToArray();
            var byReplacement=new Dictionary<int,HashSet<int>>();
            foreach(var shape in src.Shapes.Values.SelectMany(e=>e).Where(e=>e.MeshIndexOffset==first))
                foreach(var (at,replacement) in shape.Values)
                {
                    int basis=Indices[at];if(basis==replacement) continue;
                    if(!byReplacement.TryGetValue(replacement,out var bases)) byReplacement[replacement]=bases=[];
                    bases.Add(basis);
                }
            var drawn=Indices.ToHashSet();
            foreach(var (v,bases) in byReplacement)
            {
                if(bases.Count>1 || drawn.Contains(v)){AmbiguousShapeBases.UnionWith(bases);AmbiguousShapeBases.Add(v);continue;}
                int basis=bases.First();
                if(!ShapeTargets.TryGetValue(basis,out var list)) ShapeTargets[basis]=list=[];
                list.Add(v);
            }
        }
        private int At(SecondSkinWriter.VElem e,int i)=>Vb+BitConverter.ToInt32(Data,Header+20+4*e.Stream)+i*Data[Header+32+e.Stream]+e.Offset;
        internal void Write(byte[] dest,int i,Vector3 mm)
        {
            var p=mm/1000;
            SecondSkinWriter.WriteXYZ(dest,At(Position,i),Position.Type,p.X,p.Y,p.Z);
            if(ShapeTargets.TryGetValue(i,out var targets))
                foreach(int v in targets)
                {
                    var q=(P[v]+(mm-P[i]))/1000;
                    SecondSkinWriter.WriteXYZ(dest,At(Position,v),Position.Type,q.X,q.Y,q.Z);
                }
        }
    }
    private sealed record Panel(int Id,Mesh Draw,Vector3[] Source,int[] Labels,bool[] Protected);
    private sealed record Island(ClearanceSurface Old,ClearanceSurface Carrier,ClearanceSurface Target);
    internal static byte[] Apply(byte[] original,byte[] morphed,byte[] cloth,CutSkinAssembly.Assembly cut,
        Dictionary<int,ClothSubdivision.Origins> origins,GarmentClassification? classification,SkinDisplacement.Topology topology)
    {
        if(cut.Corners.Length==0) return cloth;
        var src=SecondSkinWriter.Parse(original);var dst=SecondSkinWriter.Parse(cloth);var mr=SecondSkinWriter.Parse(morphed);
        var panels=new List<Panel>();var references=new List<(int Panel,int Face)>();
        var clothTriangles=new List<Vector3>();var tracked=new List<bool>();
        var carrierTri=new List<Vector3>();var islands=new Dictionary<int,Island>();
        var stockMeshes=new Dictionary<int,Mesh>();
        var cutTri=cut.Corners.Select(c=>c.Skin.PositionMm).ToArray();
        var pins=ClothSubdivision.IdentifyJoins(src).Keys.ToLookup(k=>k.Mesh);
        var cutGroups=Enumerable.Range(0,cut.Cut.Triangles.Count).ToLookup(i=>cut.Corners[3*i].Carrier.Source.Mesh);
        for(int id=src.Lod0MeshIndex;id<src.Lod0MeshIndex+src.Lod0MeshCount;id++)
        {
            var old=new Mesh(src,id);
            if(SecondSkinWriter.IsBodySkinMaterial(src.MatNames[BitConverter.ToUInt16(original,old.Header+8)]))
            {
                stockMeshes[id]=old;
                var mor=new Mesh(mr,id);
                var rest=new List<Vector3>();var carrier=new List<Vector3>();var target=new List<Vector3>();
                for(int j=0;j<old.Indices.Length;j+=3)
                {
                    int a=old.Indices[j],b=old.Indices[j+1],c=old.Indices[j+2];
                    if(Vector3.Dot(Vector3.Cross(old.P[b]-old.P[a],old.P[c]-old.P[a]),old.N[a]+old.N[b]+old.N[c])<0) (b,c)=(c,b);
                    foreach(int v in new[]{a,b,c}) {rest.Add(old.P[v]);carrier.Add(mor.P[v]);}
                }
                carrierTri.AddRange(carrier);
                foreach(int t in cutGroups[id])
                {
                    int a=3*t,b=a+1,c=a+2;
                    if(Vector3.Dot(Vector3.Cross(cutTri[b]-cutTri[a],cutTri[c]-cutTri[a]),cut.Corners[a].Skin.Normal)<0) (b,c)=(c,b);
                    target.AddRange([cutTri[a],cutTri[b],cutTri[c]]);
                }
                if(target.Count>0) islands.Add(id,new(new(rest.ToArray()),new(carrier.ToArray()),new(target.ToArray())));
                continue;
            }
            var mesh=new Mesh(dst,id);
            origins.TryGetValue(id,out var orig);
            var source=orig==null?old.P:orig.Positions.Select(p=>1000*p).ToArray();
            var parent=Enumerable.Range(0,mesh.P.Length).ToArray();
            int Root(int i){while(i!=parent[i]){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            var locked=new bool[parent.Length];var aliases=new Dictionary<(Vector3,Vector3),int>();
            foreach(var e in pins[id]){locked[e.A]=true;locked[e.B]=true;}
            foreach(int v in mesh.AmbiguousShapeBases) locked[v]=true;
            for(int i=0;i<mesh.Indices.Length;i+=3)
            {
                int f=i/3;bool cap=classification?.Closure(id-src.Lod0MeshIndex,orig?.Parents[f]??f)==true;
                references.Add((panels.Count,f));tracked.Add(!cap);
                for(int k=0;k<3;k++)
                {
                    int v=mesh.Indices[i+k];
                    parent[Root(v)]=Root(mesh.Indices[i]);
                    if(aliases.TryGetValue((mesh.P[v],source[v]),out int other)) parent[Root(other)]=Root(v);
                    else aliases[(mesh.P[v],source[v])]=v;
                    locked[v]|=cap;clothTriangles.Add(mesh.P[v]);
                }
            }
            panels.Add(new(id,mesh,source,parent.Select(Root).ToArray(),locked));
        }
        if(islands.Count==0 || references.Count==0) return cloth;
        var baseline=clothTriangles.ToArray();var oldCarrier=carrierTri.ToArray();var track=tracked.ToArray();
        var before=VisibleClearance.Inspect(baseline,cutTri,oldCarrier,track);
        if(!before.Samples.Any(n=>n>0)) return cloth;
        var active=new Dictionary<(int Panel,int Component),HashSet<int>>();
        for(int f=0;f<references.Count;f++)
        {
            if(before.Samples[f]==0) continue;
            var (p,j)=references[f];var panel=panels[p];
            var center=(baseline[3*f]+baseline[3*f+1]+baseline[3*f+2])/3;
            int id=islands.MinBy(k=>k.Value.Target.Nearest(center).Distance).Key;
            var key=(p,panel.Labels[panel.Draw.Indices[3*j]]);
            if(!active.TryGetValue(key,out var set)) active.Add(key,set=[]);
            set.Add(id);
        }
        var output=(byte[])cloth.Clone();var candidate=(Vector3[])baseline.Clone();
        foreach(var (key,groups) in active)
        {
            var panel=panels[key.Panel];var mesh=panel.Draw;
            var nodes=mesh.Indices.Distinct().Where(v=>panel.Labels[v]==key.Component).ToArray();
            var locked=nodes.Where(v=>panel.Protected[v]).Select(v=>(mesh.P[v],panel.Source[v])).ToHashSet();
            var moves=new Dictionary<int,Vector3>();
            foreach(var sel in nodes.GroupBy(v=>groups.Count==1?groups.First():groups.MinBy(i=>islands[i].Target.Nearest(mesh.P[v]).Distance)))
            {
                var s=islands[sel.Key];var candidates=new List<(int Vertex,float Loss,Vector3 Normal)>();
                float cap=0;
                foreach(int v in sel)
                {
                    if(locked.Contains((mesh.P[v],panel.Source[v]))) continue;
                    var old=s.Old.Nearest(panel.Source[v]);if(old.Distance>=1) continue;
                    float gap=Vector3.Dot(old.Normal,panel.Source[v]-old.Point);if(gap<=0 || gap>=1) continue;
                    var hit=s.Target.Nearest(mesh.P[v]);var carrier=s.Carrier.Nearest(mesh.P[v]);
                    cap=Math.Max(cap,Math.Max(s.Carrier.Nearest(hit.Point).Distance,s.Target.Nearest(carrier.Point).Distance));
                    float lost=gap-Vector3.Dot(hit.Normal,mesh.P[v]-hit.Point);
                    if(lost>0) candidates.Add((v,lost,hit.Normal));
                }
                foreach(var (v,loss,n) in candidates) moves[v]=mesh.P[v]+Math.Min(cap,loss)*n;
            }
            if(moves.Count==0) continue;
            var changed=new List<int>();bool oriented=true;
            for(int f=0;f<references.Count;f++)
            {
                var (p,j)=references[f];if(p!=key.Panel) continue;
                bool touched=false;
                for(int k=0;k<3;k++)
                    if(moves.TryGetValue(mesh.Indices[3*j+k],out var point)){candidate[3*f+k]=point;touched=true;}
                if(!touched) continue;
                changed.Add(f);int t=3*f;
                var n=Vector3.Cross(baseline[t+1]-baseline[t],baseline[t+2]-baseline[t]);
                if(n.LengthSquared()>1e-12f && Vector3.Dot(n,Vector3.Cross(candidate[t+1]-candidate[t],candidate[t+2]-candidate[t]))<=0) oriented=false;
            }
            var after=oriented?VisibleClearance.Inspect(candidate,cutTri,oldCarrier,track):null;
            if(after!=null && after.Samples.Sum()<before.Samples.Sum() && !after.Samples.Where((n,i)=>n>0 && before.Samples[i]==0).Any())
            {
                foreach(var (v,point) in moves) mesh.Write(output,v,point);
                before=after;
            }
            else foreach(int f in changed) Array.Copy(baseline,3*f,candidate,3*f,3);
        }
        var skinSurface=new ClearanceSurface(cutTri);
        var stockCorners=new Vector3[cutTri.Length];var stockNormals=new Vector3[cutTri.Length];
        for(int j=0;j<cutTri.Length;j++)
        {
            var sample=cut.Corners[j].Carrier;var face=sample.Source;var stock=stockMeshes[face.Mesh];
            var v=face.Vertices;var tr=face.SourceBarycentric;var u=sample.Barycentric;
            var b=tr.A+u.X*(tr.B-tr.A)+u.Y*(tr.C-tr.A);
            stockCorners[j]=stock.P[v[0]]+(float)b.X*(stock.P[v[1]]-stock.P[v[0]])+(float)b.Y*(stock.P[v[2]]-stock.P[v[0]]);
            stockNormals[j]=stock.N[v[0]]+(float)b.X*(stock.N[v[1]]-stock.N[v[0]])+(float)b.Y*(stock.N[v[2]]-stock.N[v[0]]);
        }
        var neighbourhood=new List<int>();
        for(int step=0;step<12;step++)
        {
            var motions=new Dictionary<(int Panel,Vector3 Source,Vector3 Base),Vector3>();
            for(int f=0;f<references.Count;f++)
            {
                if(before.Samples[f]==0 || !track[f]) continue;
                var (p,j)=references[f];var panel=panels[p];
                var v=panel.Draw.Indices[(3*j)..(3*j+3)];var tri=candidate[(3*f)..(3*f+3)];
                var srcTri=v.Select(i=>panel.Source[i]).ToArray();
                skinSurface.Overlap(Vector3.Min(tri[0],Vector3.Min(tri[1],tri[2])),Vector3.Max(tri[0],Vector3.Max(tri[1],tri[2])),neighbourhood);
                foreach(int s in neighbourhood)
                {
                    int t=3*s;var goals=ClothContact.Lost(tri,srcTri,cutTri[t..(t+3)],stockCorners[t..(t+3)],cut.Corners[t].Skin.Normal,stockNormals[t]);
                    foreach(var (b,n,deficit) in goals)
                    {
                        float den=0,achieved=0;
                        for(int k=0;k<3;k++)
                        {
                            if(panel.Protected[v[k]]) continue;
                            den+=b[k]*b[k];var key=(p,srcTri[k],panel.Draw.P[v[k]]);
                            achieved+=b[k]*Vector3.Dot(motions.GetValueOrDefault(key),n);
                        }
                        if(den<1e-5f || achieved>=deficit) continue;
                        for(int k=0;k<3;k++)
                        {
                            if(panel.Protected[v[k]]) continue;
                            var key=(p,srcTri[k],panel.Draw.P[v[k]]);
                            motions[key]=motions.GetValueOrDefault(key)+((deficit-achieved)*b[k]/den)*n;
                        }
                    }
                }
            }
            if(motions.Count==0) break;
            foreach(var (panel,p) in panels.Select((panel,p)=>(panel,p)))
                for(int v=0;v<panel.Protected.Length;v++) if(panel.Protected[v]) motions.Remove((p,panel.Source[v],panel.Draw.P[v]));
            var moves=new Vector3[candidate.Length];var changed=new List<int>();
            for(int f=0;f<references.Count;f++)
            {
                var (p,j)=references[f];var panel=panels[p];bool moved=false;
                for(int k=0;k<3;k++)
                {
                    int v=panel.Draw.Indices[3*j+k];moves[3*f+k]=motions.GetValueOrDefault((p,panel.Source[v],panel.Draw.P[v]));
                    moved|=moves[3*f+k].LengthSquared()>1e-14f;
                }
                if(moved) changed.Add(f);
            }
            if(changed.Count==0) break;
            bool applied=false;
            foreach(float scale in new[]{1f,.5f,.25f,.125f,.0625f,.03125f,.015625f})
            {
                var trial=(Vector3[])candidate.Clone();bool good=true;
                foreach(int f in changed)
                {
                    int t=3*f;for(int k=0;k<3;k++) trial[t+k]+=moves[t+k]*scale;
                    var n=Vector3.Cross(candidate[t+1]-candidate[t],candidate[t+2]-candidate[t]);
                    var m=Vector3.Cross(trial[t+1]-trial[t],trial[t+2]-trial[t]);
                    if(n.LengthSquared()>1e-14f && Vector3.Dot(n,m)<=0){good=false;break;}
                }
                if(!good) continue;
                var after=VisibleClearance.Inspect(trial,cutTri,oldCarrier,track);
                if(after.Samples.Sum()>=before.Samples.Sum() || after.Samples.Where((x,i)=>x>0 && before.Samples[i]==0).Any()) continue;
                foreach(int f in changed)
                {
                    var (p,j)=references[f];var mesh=panels[p].Draw;
                    for(int k=0;k<3;k++) mesh.Write(output,mesh.Indices[3*j+k],trial[3*f+k]);
                }
                before=after;candidate=trial;applied=true;break;
            }
            if(!applied) break;
        }        CoveredSkinClearance.Restore(cut,topology,candidate,cutTri,oldCarrier,track);
        return output;
    }
}
