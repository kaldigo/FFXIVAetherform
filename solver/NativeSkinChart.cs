using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal sealed class NativeSkinChart
{
    internal readonly Triangle[] Cutting;
    internal readonly Triangle[] Render;
    internal readonly int[] PhysicalCorners;
    internal readonly int[] RenderCorners;
    internal readonly Vector3[] PositionsMm;
    private readonly Vector3[] cornerNormals;
    private readonly Dictionary<int,int>[] cornerWeights;
    private readonly string[] boneNames;
    internal readonly record struct Influence(string Bone, byte Weight);
    internal sealed record Payload(Vector3 PositionMm,Vector3 Normal,Point RenderUV,Influence[] Weights);

    internal Payload Evaluate(int face,Point bary)
    {
        int start=3*face;
        double[] coefficients=[1-bary.X-bary.Y,bary.X,bary.Y];
        var position=Vector3.Zero;var normal=Vector3.Zero;
        var uv=Render[face].Polygon; var texture=new Point(0,0);
        var weights=new SortedDictionary<string,double>(StringComparer.Ordinal);
        for(int j=0;j<3;j++)
        {
            int i=start+j; double w=coefficients[j];
            position+=(float)w*PositionsMm[i]; normal+=(float)w*cornerNormals[i];
            texture+=w*uv[j];
            foreach(var (bone,value) in cornerWeights[i])
                weights[boneNames[bone]]=weights.GetValueOrDefault(boneNames[bone])+w*value;
        }
        var masses=weights.Where(k=>k.Value>0).ToArray();
        double sum=masses.Sum(k=>k.Value);
        if(sum<=0 || normal.LengthSquared()<1e-20f) throw new InvalidDataException("Invalid cut skin payload.");
        var raw=masses.Select(k=>255*k.Value/sum).ToArray();
        var packed=raw.Select(v=>(int)Math.Floor(v)).ToArray();
        foreach(int i in Enumerable.Range(0,packed.Length).OrderByDescending(i=>raw[i]-packed[i]).Take(255-packed.Sum()))
            packed[i]++;
        var influences=masses.Select((k,i)=>new Influence(k.Key,checked((byte)packed[i]))).Where(w=>w.Weight>0).ToArray();
        return new(position,Vector3.Normalize(normal),texture,influences);
    }


    internal NativeSkinChart(byte[] body)
    {
        var src=SecondSkinWriter.Parse(body);
        int mesh=Enumerable.Range(src.Lod0MeshIndex,src.Lod0MeshCount)
            .Single(i=>src.MatNames[BitConverter.ToUInt16(body,src.MeshStart+36*i+8)].EndsWith("_bibo.mtrl",StringComparison.OrdinalIgnoreCase));
        int h=src.MeshStart+36*mesh, count=BitConverter.ToUInt16(body,h);
        var fields=src.Decls[mesh];
        var p=fields.First(e=>e.Usage==0); var n=fields.First(e=>e.Usage==3);
        var uv=fields.First(e=>e.Usage==4); var w=fields.First(e=>e.Usage==1); var b=fields.First(e=>e.Usage==2);
        int Address(SecondSkinWriter.VElem f,int i)=>src.Vb+BitConverter.ToInt32(body,h+20+4*f.Stream)+i*body[h+32+f.Stream]+f.Offset;
        var positions=new Vector3[count]; var normals=new Vector3[count];
        var coordinates=new Point[count]; var weights=new Dictionary<int,int>[count];
        Span<float> values=stackalloc float[4];
        for(int i=0;i<count;i++)
        {
            SecondSkinWriter.ReadTyped(body,Address(p,i),p.Type,values);positions[i]=new(values[0],values[1],values[2]);
            SecondSkinWriter.ReadTyped(body,Address(n,i),n.Type,values);
            var normal=new Vector3(values[0],values[1],values[2]); if(n.Type==8) normal=2*normal-Vector3.One;
            normals[i]=Vector3.Normalize(normal);
            SecondSkinWriter.ReadTyped(body,Address(uv,i),uv.Type,values);coordinates[i]=new(values[0],values[1]);
            weights[i]=[];
            for(int j=0;j<(w.Type==17?8:4);j++)
            {
                int weight=body[Address(w,i)+j],bone=body[Address(b,i)+j];
                if(weight>0) weights[i][bone]=weights[i].GetValueOrDefault(bone)+weight;
            }
        }
        var parents=Enumerable.Range(0,count).ToArray();
        int Root(int i)
        {
            while(i!=parents[i]) {parents[i]=parents[parents[i]];i=parents[i];}
            return i;
        }
        static float Ulps(float a,float b)
        {
            float m=Math.Max(Math.Abs(a),Math.Abs(b));
            return 2*(MathF.BitIncrement(m)-m);
        }
        var buckets=new Dictionary<(int,int,int),List<int>>();
        // Compare native render aliases at their storage precision in model units.
        const float cell=1e-6f;
        for(int i=0;i<count;i++)
        {
            var a=positions[i];
            int x=(int)MathF.Floor(a.X/cell),y=(int)MathF.Floor(a.Y/cell),z=(int)MathF.Floor(a.Z/cell);
            for(int dx=-1;dx<=1;dx++) for(int dy=-1;dy<=1;dy++) for(int dz=-1;dz<=1;dz++)
            {
                if(!buckets.TryGetValue((x+dx,y+dy,z+dz),out var near)) continue;
                foreach(int j in near)
                {
                    var other=positions[j];var diff=Vector3.Abs(a-other);
                    if(diff.LengthSquared()>1e-14f && (diff.X>Ulps(a.X,other.X) || diff.Y>Ulps(a.Y,other.Y) || diff.Z>Ulps(a.Z,other.Z))) continue;
                    if(Vector3.Dot(normals[i],normals[j])<.999f || weights[i].Count!=weights[j].Count ||
                        weights[i].Any(k=>weights[j].GetValueOrDefault(k.Key)!=k.Value)) continue;
                    int ri=Root(i),rj=Root(j); parents[Math.Max(ri,rj)]=Math.Min(ri,rj);
                }
            }
            if(!buckets.TryGetValue((x,y,z),out var group)) buckets[(x,y,z)]=group=[];
            group.Add(i);
        }
        int first=src.Ib+2*BitConverter.ToInt32(body,h+16);
        var indices=Enumerable.Range(0,BitConverter.ToInt32(body,h+4)).Select(i=>(int)BitConverter.ToUInt16(body,first+2*i)).ToArray();
        boneNames=src.BoneTables[BitConverter.ToUInt16(body,h+14)].Select(id=>src.BoneNames[id]).ToArray();
        cornerNormals=indices.Select(i=>normals[i]).ToArray();
        cornerWeights=indices.Select(i=>weights[i]).ToArray();
        RenderCorners=indices;
        PhysicalCorners=indices.Select(Root).ToArray();
        PositionsMm=indices.Select(i=>positions[i]*1000).ToArray();
        var chartId=new int[count];
        foreach(var physical in indices.Distinct().Order().GroupBy(Root))
        {
            var representatives=new List<int>();
            foreach(int i in physical)
            {
                int rep=representatives.FirstOrDefault(j=>Near(coordinates[i],coordinates[j]),-1);
                if(rep<0)
                { representatives.Add(i); rep=i; }
                chartId[i]=rep;
            }
        }
        var canonical=new Dictionary<int,Point>();
        foreach(int i in indices) canonical.TryAdd(chartId[i],coordinates[i]);
        Render=indices.Chunk(3).Select(t=>new Triangle(coordinates[t[0]],coordinates[t[1]],coordinates[t[2]])).ToArray();
        Cutting=indices.Chunk(3).Select(t=>new Triangle(canonical[chartId[t[0]]],canonical[chartId[t[1]]],canonical[chartId[t[2]]])).ToArray();
    }

    private static bool Near(Point a,Point b)
    {
        var d=a-b;return d.X*d.X+d.Y*d.Y<=1e-12;
    }
}
