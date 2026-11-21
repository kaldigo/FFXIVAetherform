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
        internal readonly int Header,Vb;
        internal readonly SecondSkinWriter.VElem Position;
        internal Mesh(SecondSkinWriter.Source src,int id)
        {
            Data=src.S;Vb=src.Vb;Header=src.MeshStart+36*id;
            int n=BitConverter.ToUInt16(Data,Header);P=new Vector3[n];N=new Vector3[n];
            Position=src.Decls[id].First(f=>f.Usage==0);var normal=src.Decls[id].First(f=>f.Usage==3);
            var v=new float[4];
            for(int i=0;i<n;i++)
            {
                SecondSkinWriter.ReadTyped(Data,At(Position,i),Position.Type,v);P[i]=1000*new Vector3(v[0],v[1],v[2]);
                SecondSkinWriter.ReadTyped(Data,At(normal,i),normal.Type,v);N[i]=new(v[0],v[1],v[2]);if(normal.Type==8) N[i]=2*N[i]-Vector3.One;
            }
            int first=BitConverter.ToInt32(Data,Header+16);
            Indices=Enumerable.Range(0,BitConverter.ToInt32(Data,Header+4)).Select(i=>(int)BitConverter.ToUInt16(Data,src.Ib+2*(first+i))).ToArray();
        }
        private int At(SecondSkinWriter.VElem e,int i)=>Vb+BitConverter.ToInt32(Data,Header+20+4*e.Stream)+i*Data[Header+32+e.Stream]+e.Offset;
        internal void Write(byte[] dest,int i,Vector3 mm)
        {
            var p=mm/1000;
            SecondSkinWriter.WriteXYZ(dest,At(Position,i),Position.Type,p.X,p.Y,p.Z);
        }
    }
    private sealed record Panel(int Id,Mesh Draw,Vector3[] Source,int[] Labels,bool[] Protected);
    private sealed record Island(ClearanceSurface Old,ClearanceSurface Carrier,ClearanceSurface Target);
    internal static byte[] Apply(byte[] original,byte[] morphed,byte[] cloth,CutSkinAssembly.Assembly cut,
        Dictionary<int,ClothSubdivision.Origins> origins,GarmentClassification? classification)
    {
        if(cut.Corners.Length==0) return cloth;
        var src=SecondSkinWriter.Parse(original);var dst=SecondSkinWriter.Parse(cloth);var mr=SecondSkinWriter.Parse(morphed);
        var panels=new List<Panel>();var references=new List<(int Panel,int Face)>();
        var clothTriangles=new List<Vector3>();var tracked=new List<bool>();
        var carrierTri=new List<Vector3>();var islands=new Dictionary<int,Island>();
        var cutTri=cut.Corners.Select(c=>c.Skin.PositionMm).ToArray();
        var pins=ClothSubdivision.IdentifyJoins(src).Keys.ToLookup(k=>k.Mesh);
        var cutGroups=Enumerable.Range(0,cut.Cut.Triangles.Count).ToLookup(i=>cut.Corners[3*i].Carrier.Source.Mesh);
        for(int id=src.Lod0MeshIndex;id<src.Lod0MeshIndex+src.Lod0MeshCount;id++)
        {
            var old=new Mesh(src,id);
            if(SecondSkinWriter.IsBodySkinMaterial(src.MatNames[BitConverter.ToUInt16(original,old.Header+8)]))
            {
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
            var keys=src.Shapes.Where(s=>s.Value.Any(e=>e.MeshIndexOffset==BitConverter.ToInt32(original,old.Header+16)));
            // Shape endpoints have to travel with their base point.
            foreach(var e in keys.SelectMany(s=>s.Value).Where(e=>e.MeshIndexOffset==BitConverter.ToInt32(original,old.Header+16)))
                foreach(var (at,_) in e.Values) locked[old.Indices[at]]=true;
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
        return output;
    }
}
