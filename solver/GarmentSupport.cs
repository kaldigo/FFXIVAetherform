using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class GarmentSupport
{
    private sealed record Face(int Mesh,int Index,int[] V);
    internal static byte[] Apply(byte[] original,byte[] morphed,GarmentClassification? classes)
    {
        if(classes==null || classes.Hosts.Count==0) return morphed;
        var src=SecondSkinWriter.Parse(original);var dst=SecondSkinWriter.Parse(morphed);
        var rest=new Dictionary<int,GarmentClearance.Mesh>();var fit=new Dictionary<int,GarmentClearance.Mesh>();
        var selected=new Dictionary<(int,int),Face>();var counts=new Dictionary<Vector3,int>();
        for(int m=src.Lod0MeshIndex;m<src.Lod0MeshIndex+src.Lod0MeshCount;m++)
        {
            int id=m-src.Lod0MeshIndex;rest[id]=new(src,m);fit[id]=new(dst,m);var mesh=rest[id];
            for(int t=0;t<mesh.Indices.Length;t+=3)
            {
                var tri=mesh.Indices[t..(t+3)];
                foreach(int v in tri) counts[mesh.P[v]]=counts.GetValueOrDefault(mesh.P[v])+1;
                if(classes.Hosts.ContainsKey(id+"/"+(t/3))) selected[(id,t/3)]=new(id,t/3,tri);
            }
        }
        var groups=new Dictionary<Vector3,List<Face>>();
        foreach(var face in selected.Values)
        foreach(int v in face.V)
        {
            var p=rest[face.Mesh].P[v];
            if(!groups.TryGetValue(p,out var list)) groups[p]=list=[];
            list.Add(face);
        }
        var result=(byte[])morphed.Clone();var seen=new HashSet<Face>();
        foreach(var seed in selected.Values)
        {
            if(!seen.Add(seed)) continue;
            var part=new List<Face>();var pending=new Queue<Face>();pending.Enqueue(seed);
            while(pending.TryDequeue(out var f))
            {
                part.Add(f);
                foreach(int v in f.V)
                    foreach(var other in groups[rest[f.Mesh].P[v]]) if(seen.Add(other)) pending.Enqueue(other);
            }
            bool Fold(Face f,Func<int,Vector3> after)
            {
                var p=rest[f.Mesh].P;int[] v=f.V;
                var before=Vector3.Cross(p[v[1]]-p[v[0]],p[v[2]]-p[v[0]]);
                var now=Vector3.Cross(after(v[1])-after(v[0]),after(v[2])-after(v[0]));
                return before.LengthSquared()>1e-12f && Vector3.Dot(before,now)<0;
            }
            if(!part.Any(f=>Fold(f,v=>fit[f.Mesh].P[v]))) continue;
            var roots=part.SelectMany(f=>classes.Hosts[f.Mesh+"/"+f.Index]).Select(r=>(Mesh:r[0],First:3*r[1])).Distinct().ToArray();
            if(roots.Length==0 || roots.Any(r=>part.Any(f=>f.Mesh==r.Mesh && 3*f.Index==r.First))) continue;
            var nodes=part.SelectMany(f=>f.V.Select(v=>rest[f.Mesh].P[v])).Distinct().ToArray();
            var members=nodes.Select((p,i)=>(p,i)).ToDictionary(x=>x.p,x=>x.i);
            var neighbours=nodes.Select(_=>new HashSet<int>()).ToArray();var pinned=new bool[nodes.Length];
            var desired=new Vector3[nodes.Length];var residual=new Vector3[nodes.Length];var uses=new int[nodes.Length];
            foreach(var f in part)
            for(int c=0;c<3;c++)
            {
                int a=members[rest[f.Mesh].P[f.V[c]]],b=members[rest[f.Mesh].P[f.V[(c+1)%3]]];
                if(a!=b){neighbours[a].Add(b);neighbours[b].Add(a);}uses[a]++;
            }
            for(int i=0;i<nodes.Length;i++)
            {
                var p=nodes[i];pinned[i]=uses[i]!=counts[p];float best=float.PositiveInfinity;
                foreach(var (m,t) in roots)
                {
                    var v=rest[m].Indices;var r=rest[m].P;var q=fit[m].P;
                    int a=v[t],b=v[t+1],c=v[t+2];
                    var hit=BrushTransfer.ClosestOnTriangle(p,r[a],r[b],r[c],out float x,out float y,out float z);
                    float d=Vector3.DistanceSquared(hit,p);if(d>=best) continue;
                    best=d;desired[i]=p+x*(q[a]-r[a])+y*(q[b]-r[b])+z*(q[c]-r[c]);
                }
                if(pinned[i])
                {
                    var f=groups[p][0];int v=f.V.First(v=>rest[f.Mesh].P[v]==p);
                    residual[i]=fit[f.Mesh].P[v]-desired[i];
                }
            }
            if(pinned.Any(b=>b))
                for(int iter=0;iter<2000;iter++)
                {
                    float change=0;
                    for(int i=0;i<nodes.Length;i++)
                    {
                        if(pinned[i]) continue;float den=0;var value=Vector3.Zero;
                        foreach(int j in neighbours[i]){float w=1/Math.Max(1e-4f,Vector3.Distance(nodes[i],nodes[j]));value+=w*residual[j];den+=w;}
                        if(den<=0) continue;value/=den;change=Math.Max(change,Vector3.Distance(value,residual[i]));residual[i]=value;
                    }
                    if(change<1e-6f) break;
                }
            Vector3 Goal(Face f,int v){int i=members[rest[f.Mesh].P[v]];return desired[i]+residual[i];}
            if(part.Any(f=>Fold(f,v=>Goal(f,v)))) continue;
            foreach(var f in part) foreach(int v in f.V) fit[f.Mesh].Write(result,v,Goal(f,v));
        }
        return result;
    }
}
