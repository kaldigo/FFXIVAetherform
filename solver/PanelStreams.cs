using System.Numerics;
using Proteus.Services;
using E = Proteus.Services.SecondSkinWriter.VElem;

namespace Aetherform.Geometry;

// LOD0 streams keep their original declarations. Midpoints belong to a mesh-local
// indexed edge, so UV splits, material boundaries and attributes stay independent.
internal static class PanelStreams
{
    // Rotate the authored shading frame by the change in the local geometric
    // normal. Keep split normals, tangent handedness, and the fourth component.
    internal static void UpdateShading(byte[][] streams,int[] strides,E[] fields,
        Vector3[] before,Vector3[] after,int[] triangles)
    {
        var from=new Vector3[before.Length];
        var to=new Vector3[after.Length];
        for(int j=0;j<triangles.Length;j+=3)
        {
            int a=triangles[j],b=triangles[j+1],c=triangles[j+2];
            var n=Vector3.Cross(before[b]-before[a],before[c]-before[a]);
            var m=Vector3.Cross(after[b]-after[a],after[c]-after[a]);
            from[a]+=n; from[b]+=n; from[c]+=n;
            to[a]+=m; to[b]+=m; to[c]+=m;
        }
        var frame=fields.Where(e=>e.Usage is 3 or 5 or 6).ToArray();
        Span<float> components=stackalloc float[4];
        foreach(int i in triangles.Distinct())
        {
            if(from[i].LengthSquared()<1e-18f || to[i].LengthSquared()<1e-18f) continue;
            var n=Vector3.Normalize(from[i]); var m=Vector3.Normalize(to[i]);
            float dot=Vector3.Dot(n,m);
            if(dot>.999999f || dot<-.9999f) continue;
            var rotation=Quaternion.Normalize(new Quaternion(Vector3.Cross(n,m),1+dot));
            foreach(var e in frame)
            {
                var s=streams[e.Stream]; int offset=i*strides[e.Stream]+e.Offset;
                SecondSkinWriter.ReadTyped(s,offset,e.Type,components);
                var v=new Vector3(components[0],components[1],components[2]);
                if(e.Type==8) v=2*v-Vector3.One;
                if(v.LengthSquared()<1e-10f) continue;
                v=Vector3.Transform(Vector3.Normalize(v),rotation);
                SecondSkinWriter.WriteNormal(s,offset,e.Type,v.X,v.Y,v.Z);
            }
        }
    }

    internal static bool Supported(E e) => e.Usage switch
    {
        1 or 2 => e.Type is 5 or 8 or 17,
        3 or 5 or 6 => e.Type is 2 or 3 or 14 or 8,
        0 => e.Type is 2 or 3 or 14,
        4 or 7 => e.Type is 0 or 1 or 2 or 3 or 5 or 8 or 13 or 14,
        _ => false
    };

    internal static void Midpoint(byte[][] streams,int[] stride,E[] fields,int a,int b,int mid)
    {
        Span<float> va=stackalloc float[4], vb=stackalloc float[4];
        foreach(var e in fields)
        {
            if(e.Usage is 1 or 2) continue;
            byte[] s=streams[e.Stream];
            int offset=mid*stride[e.Stream]+e.Offset;
            SecondSkinWriter.ReadTyped(s,a*stride[e.Stream]+e.Offset,e.Type,va);
            SecondSkinWriter.ReadTyped(s,b*stride[e.Stream]+e.Offset,e.Type,vb);
            for(int k=0;k<4;k++) va[k]=(va[k]+vb[k])*.5f;
            if(e.Usage is 3 or 5 or 6)
            {
                var n=new Vector3(va[0],va[1],va[2]);
                if(e.Type==8) n=2*n-Vector3.One;
                if(n.LengthSquared()>1e-10f) n=Vector3.Normalize(n);
                if(e.Type==8) n=(n+Vector3.One)*.5f;
                va[0]=n.X; va[1]=n.Y; va[2]=n.Z;
                // Preserve discrete tangent handedness rather than averaging signs.
                if(e.Usage is 5 or 6) va[3]=vb[3];
            }
            int nComponents=e.Type switch { 0=>1, 1 or 13=>2, 2=>3, _=>4 };
            for(int k=0;k<nComponents;k++)
            {
                if(e.Type is 13 or 14) BitConverter.TryWriteBytes(s.AsSpan(offset+2*k),(Half)va[k]);
                else if(e.Type is 5 or 8) s[offset+k]=(byte)Math.Clamp(MathF.Round(va[k]*(e.Type==8?255:1)),0,255);
                else BitConverter.TryWriteBytes(s.AsSpan(offset+4*k),va[k]);
            }
        }
        var w=fields.First(e=>e.Usage==1); var j=fields.First(e=>e.Usage==2);
        int width=w.Type==17?8:4;
        var bones=new Dictionary<byte,int>();
        foreach(int v in new[]{a,b})
        for(int k=0;k<width;k++)
        {
            byte bone=streams[j.Stream][v*stride[j.Stream]+j.Offset+k];
            int weight=streams[w.Stream][v*stride[w.Stream]+w.Offset+k];
            if(weight>0) bones[bone]=bones.GetValueOrDefault(bone)+weight;
        }
        var sorted=bones.OrderByDescending(kv=>kv.Value).ThenBy(kv=>kv.Key).Take(width).ToArray();
        int sum=sorted.Sum(kv=>kv.Value), used=0, total=0;
        for(int k=0;k<width;k++)
        {
            if(k<sorted.Length) total+=sorted[k].Value;
            int cumulative=sum==0?0:(total*255+sum/2)/sum;
            streams[w.Stream][mid*stride[w.Stream]+w.Offset+k]=(byte)(cumulative-used);
            streams[j.Stream][mid*stride[j.Stream]+j.Offset+k]=k<sorted.Length?sorted[k].Key:(byte)0;
            used=cumulative;
        }
    }
}
