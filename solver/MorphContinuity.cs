using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

internal static class MorphContinuity
{
    internal static void Apply(byte[] original,byte[] output)
    {
        var src=SecondSkinWriter.Parse(original);var dst=SecondSkinWriter.Parse(output);
        var fields=new Dictionary<Vector3,Vector3>();var suspects=new HashSet<Vector3>();
        var meshes=new List<(GarmentClearance.Mesh Old,GarmentClearance.Mesh Fit,int Id)>();
        for(int m=src.Lod0MeshIndex;m<src.Lod0MeshIndex+src.Lod0MeshCount;m++)
        {
            var a=new GarmentClearance.Mesh(src,m);var b=new GarmentClearance.Mesh(dst,m);
            meshes.Add((a,b,m));
            foreach(int i in a.Indices)fields.TryAdd(a.P[i],b.P[i]-a.P[i]);
            for(int f=0;f<a.Indices.Length;f+=3)
                for(int k=0;k<3;k++)
                {
                    int i=a.Indices[f+k],j=a.Indices[f+(k+1)%3];
                    float span=Vector3.Distance(a.P[i],a.P[j]);
                    if(span<1 || span>40)continue;
                    float jump=Vector3.Distance(fields[a.P[i]],fields[a.P[j]]);
                    if(jump>6+span*.65f){suspects.Add(a.P[i]);suspects.Add(a.P[j]);}
                }
        }
        if(suspects.Count==0)return;
        var edits=new Dictionary<Vector3,Vector3>();var samples=fields.ToArray();
        // Closely spaced vertices on overlapping seam allowances can have the same
        // displacement even when only one is adjacent to the steep edge.
        (int,int,int) Cell(Vector3 p)=>((int)MathF.Floor(p.X/32),(int)MathF.Floor(p.Y/32),(int)MathF.Floor(p.Z/32));
        var bins=samples.ToLookup(e=>Cell(e.Key));
        IEnumerable<KeyValuePair<Vector3,Vector3>> Nearby(Vector3 p)
        {
            var (x,y,z)=Cell(p);
            for(int i=x-1;i<=x+1;i++)for(int j=y-1;j<=y+1;j++)for(int k=z-1;k<=z+1;k++)
                foreach(var entry in bins[(i,j,k)])yield return entry;
        }
        var seeds=suspects.ToArray();
        foreach(var seed in seeds)
            foreach(var entry in Nearby(seed))
                if(Vector3.DistanceSquared(seed,entry.Key)<25 && Vector3.DistanceSquared(fields[seed],entry.Value)<16)
                    suspects.Add(entry.Key);
        foreach(var p in suspects)
        {
            var neighbours=Nearby(p).Where(e=>Vector3.DistanceSquared(e.Key,p)<32*32 && Vector3.DistanceSquared(e.Key,p)>5*5).ToArray();
            if(neighbours.Length<4)continue;
            float Median(Func<Vector3,float> axis)=>neighbours.Select(e=>axis(e.Value)).Order().ElementAt(neighbours.Length/2);
            var typical=new Vector3(Median(v=>v.X),Median(v=>v.Y),Median(v=>v.Z));
            var diff=typical-fields[p];float error=diff.Length();if(error<5)continue;
            edits[p]=diff*Math.Clamp((error-5)/6,0,1);
        }
        if(edits.Count==0)return;
        foreach(var (a,b,m) in meshes)
        {
            var offsets=new Dictionary<int,Vector3>();
            foreach(int i in a.Indices.Distinct())
                if(edits.TryGetValue(a.P[i],out var shift) && !b.AmbiguousShapeBases.Contains(i))offsets[i]=shift;
            ModelPanelEdits.Write(output,dst,m,b,offsets);
        }
    }
}
