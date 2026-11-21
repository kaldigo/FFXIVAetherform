using System.IO.Compression;
using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal sealed class NativeMorph
{
    private readonly record struct Point(double X,double Y,double Z)
    {
        public static Point operator +(Point a,Point b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Point operator -(Point a,Point b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point operator *(double s,Point a)=>new(s*a.X,s*a.Y,s*a.Z);
        public double Dot(Point b)=>X*b.X+Y*b.Y+Z*b.Z;
        public Vector3 ModelUnits()=>new((float)(X/1000),(float)(Y/1000),(float)(Z/1000));
    }

    private static double Closest(Point p,Point a,Point b,Point c,out Vector3 weights)
    {
        var v=b-a;var w=c-a;var d=p-a;
        double vv=v.Dot(v), vw=v.Dot(w), ww=w.Dot(w), dv=d.Dot(v), dw=d.Dot(w);
        double den=vv*ww-vw*vw;
        double u=(dv*ww-dw*vw)/Math.Max(den,1e-30), z=(dw*vv-dv*vw)/Math.Max(den,1e-30);
        var bc=new Vector3((float)(1-u-z),(float)u,(float)z);
        var diff=p-((1-u-z)*a+u*b+z*c);
        double best=den>1e-20 && u>=0 && z>=0 && 1-u-z>=0 ? diff.Dot(diff) : double.PositiveInfinity;
        void Edge(Point start,Point end,Vector3 wa,Vector3 wb)
        {
            var edge=end-start;
            double f=Math.Clamp((p-start).Dot(edge)/Math.Max(edge.Dot(edge),1e-30),0,1);
            var gap=p-(start+f*edge);
            double s=gap.Dot(gap);
            if(s<best) {best=s;bc=Vector3.Lerp(wa,wb,(float)f);}
        }
        Edge(a,b,Vector3.UnitX,Vector3.UnitY);
        Edge(b,c,Vector3.UnitY,Vector3.UnitZ);
        Edge(c,a,Vector3.UnitZ,Vector3.UnitX);
        weights=bc;return best;
    }

    private sealed record Patch(Vector3 Min, Vector3 Max, int[] Faces);
    private sealed class Reference
    {
        internal readonly Point[] Rest;
        internal readonly Vector3[] Weights, Residual;
        internal readonly int[] Corners, Hosts;
        internal readonly int Upper, Lower;
        internal readonly Patch[] Patches;

        internal Reference()
        {
            using var stream = typeof(NativeMorph).Assembly.GetManifestResourceStream("Aetherform.NativeMorph.map.gz")
                ?? throw new InvalidDataException("Missing baked Wrap correspondence.");
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual("AFW2"u8))
                throw new InvalidDataException("Unsupported Wrap correspondence.");
            int vertices = reader.ReadInt32(), faces = reader.ReadInt32();
            Upper = reader.ReadInt32(); Lower = reader.ReadInt32();
            Vector3 ReadVector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Rest = Enumerable.Range(0, vertices).Select(_ => new Point(reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble())).ToArray();
            Corners = Enumerable.Range(0, 3 * faces).Select(_ => reader.ReadInt32()).ToArray();
            Hosts = Enumerable.Range(0, vertices).Select(_ => reader.ReadInt32()).ToArray();
            Weights = Enumerable.Range(0, vertices).Select(_ => ReadVector()).ToArray();
            Residual = Enumerable.Range(0, vertices).Select(_ => ReadVector()).ToArray();
            Patches = Enumerable.Range(0, faces).GroupBy(f =>
            {
                int t = 3*f;
                var cell = (Rest[Corners[t]] + Rest[Corners[t+1]] + Rest[Corners[t+2]]).ModelUnits() / .12f;
                return new Vector3(MathF.Floor(cell.X),MathF.Floor(cell.Y),MathF.Floor(cell.Z));
            }).Select(g =>
            {
                var min = new Vector3(float.PositiveInfinity);
                var max = new Vector3(float.NegativeInfinity);
                foreach (int f in g)
                    for (int j = 0; j < 3; ++j)
                    {
                        var p = Rest[Corners[3*f+j]].ModelUnits();
                        min = Vector3.Min(min,p); max = Vector3.Max(max,p);
                    }
                return new Patch(min,max,g.ToArray());
            }).ToArray();
        }
    }

    private static readonly Lazy<Reference> Atlas = new(() => new());
    private readonly Vector3[] delta;

    private sealed record SizeBinding(int[] Hosts, Vector3[] Weights, Vector3[] Residual);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SizeBinding> Sizes = new();

    private static SizeBinding LoadSize(string key)
    {
        var map = Atlas.Value;
        if (key == "Small-Medium") return new(map.Hosts, map.Weights, map.Residual);
        using var stream = typeof(NativeMorph).Assembly.GetManifestResourceStream($"Aetherform.WrapSizes.{key}.gz")
            ?? throw new InvalidDataException($"Missing independent Wrap for {key}.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("AFS1"u8) || reader.ReadInt32() != map.Rest.Length ||
            reader.ReadInt32() != map.Upper || reader.ReadInt32() != map.Lower)
            throw new InvalidDataException($"Incompatible Wrap for {key}.");
        var hosts = Enumerable.Range(0,map.Rest.Length).Select(_ => reader.ReadInt32()).ToArray();
        Vector3[] Vectors() => Enumerable.Range(0,hosts.Length)
            .Select(_ => new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())).ToArray();
        return new(hosts,Vectors(),Vectors());
    }

    internal NativeMorph(Dictionary<string,byte[]> bodies) : this(bodies,"Small","Medium - B") { }

    internal NativeMorph(Dictionary<string,byte[]> bodies, string chestOption, string legOption)
    {
        var map = Atlas.Value;
        var chest = chestOption.Split(" - ")[^1];
        var leg = legOption.Split(" - ").FirstOrDefault(s => s is "Large" or "Medium");
        if (chest is not ("XS" or "Small" or "Medium" or "Large") || leg == null)
            throw new InvalidDataException($"Unknown AF wrap sizes: {chestOption}, {legOption}.");
        var size = Sizes.GetOrAdd($"{chest}-{leg}",LoadSize);
        var corners = new List<Vector3>(3*(map.Upper+map.Lower));
        foreach (var slot in new[]{"_top","_dwn"})
        {
            if (!SecondSkinWriter.TryReadLod0Geometry(bodies[slot], out var p, out _, out var indices,
                    name => name.EndsWith("_bibo.mtrl",StringComparison.OrdinalIgnoreCase)) ||
                indices.Length != 3*(slot=="_top" ? map.Upper : map.Lower))
                throw new InvalidDataException($"Incompatible native AF {slot} triangle layout.");
            foreach (int i in indices)
                corners.Add(new Vector3(p[3*i],p[3*i+1],p[3*i+2]));
        }
        delta = new Vector3[map.Rest.Length];
        for (int i=0;i<delta.Length;i++)
        {
            int t=3*size.Hosts[i];
            var w=size.Weights[i];
            delta[i]=corners[t]*w.X+corners[t+1]*w.Y+corners[t+2]*w.Z+size.Residual[i]-map.Rest[i].ModelUnits();
        }
    }

    internal Vector3 Map(Vector3 p)
    {
        var map=Atlas.Value;
        double best=double.PositiveInfinity;
        var point=new Point((double)p.X*1000,(double)p.Y*1000,(double)p.Z*1000);
        int chosen=int.MaxValue;
        Vector3 displacement=default;
        foreach (var patch in map.Patches)
        {
            if (Vector3.DistanceSquared(p,Vector3.Clamp(p,patch.Min,patch.Max))*1e6>best+0.1) continue;
            foreach (int face in patch.Faces)
            {
                int t=3*face,a=map.Corners[t],b=map.Corners[t+1],c=map.Corners[t+2];
                double distance=Closest(point,map.Rest[a],map.Rest[b],map.Rest[c],out var bary);
                if (!double.IsFinite(distance) || distance>best || distance==best && face>=chosen) continue;
                best=distance; chosen=face; displacement=bary.X*delta[a]+bary.Y*delta[b]+bary.Z*delta[c];
            }
        }
        if (chosen==int.MaxValue) throw new InvalidDataException("No valid Wrap face.");
        return p+displacement;
    }

    internal byte[] Apply(byte[] source)
    {
        var output=(byte[])source.Clone();
        var model=SecondSkinWriter.Parse(output);
        var mapped=new Dictionary<Vector3,Vector3>();
        Span<float> value=stackalloc float[4];
        for(int mesh=model.Lod0MeshIndex;mesh<model.Lod0MeshIndex+model.Lod0MeshCount;mesh++)
        {
            int header=model.MeshStart+36*mesh, count=BitConverter.ToUInt16(output,header);
            var pos=model.Decls[mesh].First(e=>e.Usage==0);
            if(pos.Type is not (2 or 3 or 14)) throw new InvalidDataException("Unsupported garment position format.");
            int start=model.Vb+BitConverter.ToInt32(output,header+20+4*pos.Stream)+pos.Offset;
            int stride=output[header+32+pos.Stream];
            var before = new Vector3[count];
            var after = new Vector3[count];
            for(int i=0;i<count;i++)
            {
                int offset=start+i*stride;
                SecondSkinWriter.ReadTyped(source,offset,pos.Type,value);
                var p=new Vector3(value[0],value[1],value[2]);
                if(!mapped.TryGetValue(p,out var q)) mapped.Add(p,q=Map(p));
                before[i]=p; after[i]=q;
                SecondSkinWriter.WriteXYZ(output,offset,pos.Type,q.X,q.Y,q.Z);
            }
            var strides = Enumerable.Range(0,3).Select(s=>(int)output[header+32+s]).ToArray();
            var starts = Enumerable.Range(0,3).Select(s=>model.Vb+BitConverter.ToInt32(output,header+20+4*s)).ToArray();
            var streams = Enumerable.Range(0,3).Select(s=>strides[s]==0 ? Array.Empty<byte>() :
                output.AsSpan(starts[s],strides[s]*count).ToArray()).ToArray();
            int first = model.Ib+2*BitConverter.ToInt32(output,header+16);
            var indices = Enumerable.Range(0,BitConverter.ToInt32(output,header+4))
                .Select(i=>(int)BitConverter.ToUInt16(output,first+2*i)).ToArray();
            PanelStreams.UpdateShading(streams,strides,model.Decls[mesh],before,after,indices);
            for(int s=0;s<3;s++) if(strides[s]>0) streams[s].CopyTo(output,starts[s]);
        }
        return output;
    }
}

