using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Proteus.Services;

namespace Aetherform.Geometry;

internal sealed class VanillaAfWrap
{
    private sealed record Part(int Corners, byte[] Signature);
    internal readonly record struct Binding(int Corner, Vector3 Weights, Vector3 Offset);
    private sealed record Patch(Vector3 Lo, Vector3 Hi, (int Corner, Vector3 Center)[] Faces);
    private sealed class Reference
    {
        internal readonly List<Part> Parts = [];
        internal readonly Vector3[] Corners;
        internal readonly Patch[] Patches;
        internal readonly string Id;
        internal Reference()
        {
            using var stream = typeof(VanillaAfWrap).Assembly.GetManifestResourceStream("Aetherform.VanillaAF.map.gz")
                ?? throw new InvalidDataException("Missing baked vanilla to AF map.");
            Id = Convert.ToHexString(SHA256.HashData(stream))[..16];
            stream.Position = 0;
            using var gzip = new GZipStream(stream,CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual("AFM1"u8)) throw new InvalidDataException("Invalid map header.");
            var corners = new List<Vector3>();
            for (int slot=0;slot<2;slot++)
            {
                int n=reader.ReadInt32();
                if (n<=0 || n%3!=0 || n>1_000_000) throw new InvalidDataException("Invalid map size.");
                Parts.Add(new(n,reader.ReadBytes(32)));
                for(int i=0;i<n;i++) corners.Add(new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
            }
            if(gzip.ReadByte()!=-1) throw new InvalidDataException("Unexpected trailing map data.");
            Corners=corners.ToArray();
            Patches=Enumerable.Range(0,Corners.Length/3).Select(t=>(Corner:3*t,Center:
                (Corners[3*t]+Corners[3*t+1]+Corners[3*t+2])/3))
                .GroupBy(f=>new Vector3(MathF.Floor(f.Center.X*20),MathF.Floor(f.Center.Y*20),MathF.Floor(f.Center.Z*20)))
                .Select(g=>new Patch(g.Key/20,(g.Key+Vector3.One)/20,g.ToArray())).ToArray();
        }
    }

    private static readonly Lazy<Reference> Baked=new(()=>new());
    private static readonly object Gate=new();
    private static (string Key,VanillaAfWrap Map)? cached;
    private readonly Vector3[] target;
    private readonly (Vector3 A,Vector3 B)[] waist;
    internal IReadOnlyList<(Vector3 A,Vector3 B)> Waist => waist;

    internal static VanillaAfWrap Get(Dictionary<string,byte[]> bodies)
    {
        string key=Convert.ToHexString(SHA256.HashData(bodies["_top"]))+Convert.ToHexString(SHA256.HashData(bodies["_dwn"]));
        lock(Gate)
        {
            if(cached is {} hit && hit.Key==key) return hit.Map;
            var map=new VanillaAfWrap(bodies);
            cached=(key,map);
            return map;
        }
    }

    private VanillaAfWrap(Dictionary<string,byte[]> bodies)
    {
        target=new Vector3[Baked.Value.Corners.Length];
        int offset=0,part=0;
        foreach(var slot in new[]{"_top","_dwn"})
        {
            var reference=Baked.Value.Parts[part++];
            if(!SecondSkinWriter.TryReadLod0Geometry(bodies[slot],out var p,out var uv,out var tri,
                n=>n.EndsWith("_bibo.mtrl",StringComparison.OrdinalIgnoreCase)) || tri.Length!=reference.Corners)
                throw new InvalidDataException($"AF {slot} topology does not match the baked map.");
            var chart=new Vector2[tri.Length];
            for(int j=0;j<tri.Length;j++)
            {
                int i=tri[j];
                chart[j]=new(uv[2*i],uv[2*i+1]);
                target[offset+j]=new(p[3*i],p[3*i+1],p[3*i+2]);
            }
            if(!SHA256.HashData(MemoryMarshal.AsBytes(chart.AsSpan())).AsSpan().SequenceEqual(reference.Signature))
                throw new InvalidDataException($"AF {slot} triangle order or UV layout does not match the baked map.");
            offset+=tri.Length;
        }
        // The compatible AF bodies retain the vanilla upper/lower body join.
        // Locate it from the coincident corners of the selected meshes.
        int split=Baked.Value.Parts[0].Corners;
        static Vector3 Key(Vector3 p)=>Vector3.Round(p*100000);
        var upper=target.Take(split).Select(Key).ToHashSet();
        var joint=new HashSet<(Vector3,Vector3)>();
        for(int i=split;i<target.Length;i+=3)
            for(int j=0;j<3;j++)
            {
                Vector3 a=target[i+j],b=target[i+(j+1)%3];
                if(a!=b && upper.Contains(Key(a)) && upper.Contains(Key(b)) && !joint.Contains((b,a)))
                    joint.Add((a,b));
            }
        waist=joint.ToArray();
    }

    internal static string MapId => Baked.Value.Id;
    internal static int CornerCount => Baked.Value.Corners.Length;
    internal static Binding Bind(Vector3 p,bool legsOnly)
    {
        var data=Baked.Value;
        var nearest=new PriorityQueue<int,float>();
        float limit=float.PositiveInfinity;
        foreach(var patch in data.Patches)
        {
            if(Vector3.DistanceSquared(p,Vector3.Clamp(p,patch.Lo,patch.Hi))>limit) continue;
            foreach(var face in patch.Faces)
            {
                if(legsOnly && face.Corner<data.Parts[0].Corners) continue;
                float d=Vector3.DistanceSquared(p,face.Center);
                if(d>=limit) continue;
                if(nearest.Count==64) nearest.Dequeue();
                nearest.Enqueue(face.Corner,-d);
                if(nearest.Count==64 && nearest.TryPeek(out _,out float bound)) limit=-bound;
            }
        }
        float best=float.PositiveInfinity;
        Binding result=default;
        foreach(var entry in nearest.UnorderedItems)
        {
            int t=entry.Element;
            var contact=BrushTransfer.ClosestOnTriangle(p,data.Corners[t],data.Corners[t+1],data.Corners[t+2],
                out float a,out float b,out float c);
            float d=Vector3.DistanceSquared(p,contact);
            if(d>=best || !float.IsFinite(d)) continue;
            best=d;
            result=new(t,new(a,b,c),p-contact);
        }
        if(!float.IsFinite(best)) throw new InvalidDataException("Could not bind garment vertex.");
        return result;
    }

    internal Vector3 Apply(Binding binding,bool skin)
    {
        var w=binding.Weights;
        int t=binding.Corner;
        var surface=w.X*target[t]+w.Y*target[t+1]+w.Z*target[t+2];
        return skin ? surface : surface+binding.Offset;
    }
}
