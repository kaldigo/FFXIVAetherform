using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;

namespace Aetherform.Geometry;

// Vanilla's mirrored half sheet and AF's Bibo layout share a UV parameterization.
internal sealed class VanillaAfWrap
{
    private const int Grid = 256;
    private readonly BakeBody wrapped;
    private readonly (Vector3 Lo,Vector3 Hi,(int T,Vector3 Center)[] Faces)[] patches;
    private readonly Vector3[] target;
    private static readonly object Gate = new();
    private static (string Key, VanillaAfWrap Map)? cached;

    internal static VanillaAfWrap Get(Dictionary<string,(byte[] Vanilla, byte[] Af)> bodies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var slot in new[] { "_top", "_dwn" })
        {
            hash.AppendData(SHA256.HashData(bodies[slot].Vanilla));
            hash.AppendData(SHA256.HashData(bodies[slot].Af));
        }
        string key = Convert.ToHexString(hash.GetHashAndReset());
        lock (Gate)
        {
            if (cached is { } hit && hit.Key == key) return hit.Map;
            var map = new VanillaAfWrap(bodies);
            cached = (key, map);
            return map;
        }
    }

    private sealed record Surface(Vector3[] P, Vector2[] Uv, int[] Tri)
    {
        public static Surface Read(byte[] mdl, string suffix)
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(mdl, out var p, out var uv, out var tri,
                name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !name.Contains("e0000_")))
                throw new InvalidDataException("Missing body surface for the vanilla to AF wrap.");
            return new(Enumerable.Range(0,p.Length/3).Select(i => new Vector3(p[3*i],p[3*i+1],p[3*i+2])).ToArray(),
                Enumerable.Range(0,uv.Length/2).Select(i => new Vector2(uv[2*i],uv[2*i+1])).ToArray(), tri);
        }
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X*b.Y-a.Y*b.X;

    private static Vector3?[] Raster(Surface s, bool vanilla)
    {
        var grid = new Vector3?[Grid*Grid];
        for (int i=0; i<s.Tri.Length; i+=3)
        {
            int a=s.Tri[i], b=s.Tri[i+1], c=s.Tri[i+2];
            float side=Math.Sign(s.P[a].X+s.P[b].X+s.P[c].X);
            Vector2 Chart(int j) => vanilla ? new(.5f+.5f*(s.Uv[j].X-1)*side,s.Uv[j].Y) : s.Uv[j];
            var start=Chart(a); var u=Chart(b)-start; var v=Chart(c)-start;
            float den=Cross(u,v);
            if (Math.Abs(den)<1e-12f) continue;
            var lo=Vector2.Min(start,Vector2.Min(Chart(b),Chart(c)))*Grid-new Vector2(.5f);
            var hi=Vector2.Max(start,Vector2.Max(Chart(b),Chart(c)))*Grid-new Vector2(.5f);
            for (int y=Math.Max(0,(int)MathF.Ceiling(lo.Y)); y<=Math.Min(Grid-1,(int)MathF.Floor(hi.Y)); y++)
            for (int x=Math.Max(0,(int)MathF.Ceiling(lo.X)); x<=Math.Min(Grid-1,(int)MathF.Floor(hi.X)); x++)
            {
                var p=new Vector2((x+.5f)/Grid,(y+.5f)/Grid)-start;
                float e=Cross(p,v)/den, f=Cross(u,p)/den;
                if(e>=0 && f>=0 && e+f<=1) grid[y*Grid+x]=s.P[a]+e*(s.P[b]-s.P[a])+f*(s.P[c]-s.P[a]);
            }
        }
        return grid;
    }

    private readonly record struct Landmark(Vector3 Point,Vector2 Uv,Vector3 Offset);

    // Group landmarks spatially. A box's minimum XYZ distance bounds the full XYZ+UV distance.
    private sealed record Block(Vector3 Lo,Vector3 Hi,Landmark[] Points);
    private static Vector3 Offset(Vector3 p,Vector2 uv,Block[] blocks)
    {
        Span<float> nearest=stackalloc float[12];
        Span<Vector3> offsets=stackalloc Vector3[12];
        nearest.Fill(float.PositiveInfinity);
        foreach(var block in blocks)
        {
            if(Vector3.DistanceSquared(p,Vector3.Clamp(p,block.Lo,block.Hi))>=nearest[^1]) continue;
            foreach(var sample in block.Points)
            {
                float d=Vector3.DistanceSquared(p,sample.Point)+.1225f*Vector2.DistanceSquared(uv,sample.Uv);
                if(d>=nearest[^1]) continue;
                int j=nearest.Length-1;
                while(j>0 && d<nearest[j-1]) { nearest[j]=nearest[j-1]; offsets[j]=offsets[j-1]; j--; }
                nearest[j]=d; offsets[j]=sample.Offset;
            }
        }
        Vector3 sum=default; float total=0;
        for(int j=0;j<nearest.Length;j++)
        {
            float d=nearest[j]+1e-6f, weight=1/(d*MathF.Sqrt(d));
            sum+=offsets[j]*weight; total+=weight;
        }
        return sum/total;
    }

    private VanillaAfWrap(Dictionary<string,(byte[] Vanilla,byte[] Af)> bodies)
    {
        var parts=new List<BakeBody.Part>();
        var targets=new List<Vector3>();
        foreach(var slot in new[]{"_top","_dwn"})
        {
            var body=Surface.Read(bodies[slot].Af,"_bibo.mtrl");
            var ag=Raster(body,false);
            var vg=Raster(Surface.Read(bodies[slot].Vanilla,"_a.mtrl"),true);
            var samples=new List<Landmark>();
            for(int i=0;i<ag.Length;i++)
                if(ag[i] is {} a && vg[i] is {} v)
                    samples.Add(new(a,new((i%Grid+.5f)/Grid,(i/Grid+.5f)/Grid),v-a));
            if(samples.Count<12) throw new InvalidDataException("Insufficient shared vanilla and AF UV coverage.");
            var blocks=samples.GroupBy(s=>new Vector3(MathF.Floor(s.Point.X*20),MathF.Floor(s.Point.Y*20),MathF.Floor(s.Point.Z*20))).Select(g=>
                new Block(g.Key/20,(g.Key+Vector3.One)/20,g.ToArray())).ToArray();
            var points=(Vector3[])body.P.Clone();
            foreach(var seam in body.Tri.Distinct().GroupBy(i=>Vector3.Round(body.P[i]*1e6f)))
            {
                Vector3 sum=default;
                foreach(int i in seam) sum+=body.P[i]+Offset(body.P[i],body.Uv[i],blocks);
                var value=sum/seam.Count();
                foreach(int i in seam) points[i]=value;
            }
            parts.Add(new(points.SelectMany(p=>new[]{p.X,p.Y,p.Z}).ToArray(),
                body.Uv.SelectMany(p=>new[]{p.X,p.Y}).ToArray(),body.Tri,[],[]));
            targets.AddRange(body.P);
        }
        target=targets.ToArray();
        wrapped=new(parts);
        patches=Enumerable.Range(0,wrapped.TriangleCount).Select(t=>(T:t,Center:
            (wrapped.P[wrapped.Tri[t*3]]+wrapped.P[wrapped.Tri[t*3+1]]+wrapped.P[wrapped.Tri[t*3+2]])/3))
            .GroupBy(f=>new Vector3(MathF.Floor(f.Center.X*20),MathF.Floor(f.Center.Y*20),MathF.Floor(f.Center.Z*20)))
            .Select(g=>(g.Key/20,(g.Key+Vector3.One)/20,g.ToArray())).ToArray();
    }

    internal Vector3 Fit(Vector3 p,bool skin)
    {
        // Use the same local candidate set as the offline wrap. A long, thin
        // triangle across a fold must not attract an unrelated cloth panel.
        var nearest=new PriorityQueue<int,float>();
        float limit=float.PositiveInfinity;
        foreach(var patch in patches)
        {
            if(Vector3.DistanceSquared(p,Vector3.Clamp(p,patch.Lo,patch.Hi))>limit) continue;
            foreach(var face in patch.Faces)
            {
                float d=Vector3.DistanceSquared(p,face.Center);
                if(d>=limit) continue;
                if(nearest.Count==64) nearest.Dequeue();
                nearest.Enqueue(face.T,-d);
                if(nearest.Count==64 && nearest.TryPeek(out _,out float bound)) limit=-bound;
            }
        }
        float best=float.PositiveInfinity;
        Vector3 fit=p;
        foreach(var entry in nearest.UnorderedItems)
        {
            int t=entry.Element*3, i=wrapped.Tri[t], j=wrapped.Tri[t+1], k=wrapped.Tri[t+2];
            var contact=BrushTransfer.ClosestOnTriangle(p,wrapped.P[i],wrapped.P[j],wrapped.P[k],out float a,out float b,out float c);
            float distance=Vector3.DistanceSquared(p,contact);
            if(distance>=best) continue;
            best=distance;
            var surface=a*target[i]+b*target[j]+c*target[k];
            fit=skin ? surface : p+surface-contact;
        }
        return fit;
    }
}
