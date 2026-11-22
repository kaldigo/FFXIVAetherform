using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

// Flush components on gloves and tops follow the retained skin in position and skinning.
internal static class GloveSkinContact
{
    internal static byte[] Apply(byte[] source,byte[] fit)
    {
        var old=SecondSkinWriter.Parse(source);var next=SecondSkinWriter.Parse(fit);
        var host=new List<(int Mesh,int Vertex)>();
        var transfers=new Dictionary<int,Dictionary<int,Dictionary<string,float>>>();
        var rest=new List<Vector3>();var target=new List<Vector3>();var panels=new List<(int,GarmentClearance.Mesh,GarmentClearance.Mesh)>();
        if(old.Lod0MeshCount!=next.Lod0MeshCount) return fit;
        for(int id=0;id<old.Lod0MeshCount;id++)
        {
            int m=old.Lod0MeshIndex+id,k=next.Lod0MeshIndex+id;
            var a=new GarmentClearance.Mesh(old,m);var b=new GarmentClearance.Mesh(next,k);
            if(!a.Indices.SequenceEqual(b.Indices) || a.P.Length!=b.P.Length) continue;
            if(SecondSkinWriter.IsBodySkinMaterial(old.MatNames[BitConverter.ToUInt16(source,a.Header+8)]))
            {
                host.AddRange(a.Indices.Select(i=>(k,i)));
                rest.AddRange(a.Indices.Select(i=>a.P[i]));target.AddRange(b.Indices.Select(i=>b.P[i]));
            }
            else panels.Add((k,a,b));
        }
        if(rest.Count==0) return fit;
        var skin=new ClearanceSurface(rest.ToArray());
        foreach(var (m,a,b) in panels)
        {
            // Join UV seam copies for component detection only.
            var parent=Enumerable.Range(0,a.P.Length).ToArray();
            int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            var aliases=new Dictionary<Vector3,int>();
            foreach(int i in a.Indices){if(aliases.TryGetValue(a.P[i],out int j)) parent[Root(i)]=Root(j);else aliases[a.P[i]]=i;}
            for(int t=0;t<a.Indices.Length;t+=3)
            {
                int root=Root(a.Indices[t]);parent[Root(a.Indices[t+1])]=root;parent[Root(a.Indices[t+2])]=root;
            }
            var moves=new Dictionary<int,Vector3>();
            var weights=new Dictionary<int,Dictionary<string,float>>();
            foreach(var part in a.Indices.Distinct().GroupBy(Root))
            {
                var hits=part.Select(i=>(i,h:skin.Nearest(a.P[i]))).ToArray();
                if(hits.Length<3 || hits.Any(p=>p.h.Distance>2 || b.AmbiguousShapeBases.Contains(p.i)) ||
                    hits.Count(p=>p.h.Distance<=1)<.85*hits.Length ||
                    hits.Count(p=>Math.Abs(Vector3.Dot(p.h.Normal,a.N[p.i]))>.70f)<.8*hits.Length) continue;
                foreach(var (i,h) in hits)
                {
                    int t=3*h.Face;
                    BrushTransfer.ClosestOnTriangle(a.P[i],rest[t],rest[t+1],rest[t+2],out var x,out var y,out var z);
                    var n=Vector3.Cross(target[t+1]-target[t],target[t+2]-target[t]);
                    if(n.LengthSquared()<1e-12f)continue;
                    var goal=x*target[t]+y*target[t+1]+z*target[t+2]+Vector3.Normalize(n)*Vector3.Dot(a.P[i]-h.Point,h.Normal);
                    moves[i]=goal-b.P[i];
                    var blend=new Dictionary<string,float>();
                    foreach(var (corner,factor) in new[]{(t,x),(t+1,y),(t+2,z)})
                    {
                        var (mesh,vertex)=host[corner];
                        foreach(var (bone,weight) in SurfaceSkinning.Read(fit,next,mesh,vertex))
                            blend[bone]=blend.GetValueOrDefault(bone)+factor*weight;
                    }
                    weights[i]=blend;
                    if(b.ShapeTargets.TryGetValue(i,out var variants))
                        foreach(int vertex in variants) weights[vertex]=blend;
                }
            }
            ModelPanelEdits.Write(fit,next,m,b,moves);
            if(weights.Count>0) transfers[m]=weights;
        }
        return SurfaceSkinning.Apply(fit,transfers);
    }
}
