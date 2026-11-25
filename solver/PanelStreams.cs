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
        Vector3[] before,Vector3[] after,int[] triangles,Dictionary<int,(Vector3 Normal,float Blend)>? shading=null)
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
        ShareSmoothFans(streams,strides,fields,before,after,triangles,from,to);
        var frame=fields.Where(e=>e.Usage is 3 or 5 or 6).ToArray();
        Span<float> components=stackalloc float[4];
        foreach(int i in triangles.Distinct())
        {
            if(from[i].LengthSquared()<1e-18f || to[i].LengthSquared()<1e-18f) continue;
            var n=Vector3.Normalize(from[i]); var m=Vector3.Normalize(to[i]);
            float dot=Vector3.Dot(n,m);
            if(dot<-.9999f) continue;
            bool moved=dot<=.999999f;
            var rotation=moved?Quaternion.Normalize(new Quaternion(Vector3.Cross(n,m),1+dot)):Quaternion.Identity;
            if(shading!=null && shading.TryGetValue(i,out var curve))
            {
                var normal=frame.First(e=>e.Usage==3);
                SecondSkinWriter.ReadTyped(streams[normal.Stream],i*strides[normal.Stream]+normal.Offset,normal.Type,components);
                var encoded=new Vector3(components[0],components[1],components[2]);
                if(normal.Type==8) encoded=2*encoded-Vector3.One;
                if(encoded.LengthSquared()>1e-10f)
                {
                    var current=Vector3.Transform(Vector3.Normalize(encoded),rotation);
                    if(Vector3.Dot(current,curve.Normal)>.7f)
                    {
                        var target=Vector3.Normalize(Vector3.Lerp(current,curve.Normal,curve.Blend));
                        var adjust=Quaternion.Normalize(new Quaternion(Vector3.Cross(current,target),1+Vector3.Dot(current,target)));
                        rotation=Quaternion.Normalize(adjust*rotation);
                        moved=true;
                    }
                }
            }
            if(!moved) continue;
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

    // UV and tangent splits may duplicate a smooth vertex. Combine the geometric
    // fan only across a shared edge that still meets after fitting. Each copy
    // retains its own tangent frame and its authored handedness.
    private static void ShareSmoothFans(byte[][] streams,int[] strides,E[] fields,
        Vector3[] before,Vector3[] after,int[] triangles,Vector3[] from,Vector3[] to)
    {
        var normal=fields.FirstOrDefault(f=>f.Usage==3);
        if(normal.Usage!=3)return;
        var drawn=triangles.Distinct().ToArray();
        var positions=new Dictionary<(Vector3,Vector3),int>();
        var position=new int[before.Length];
        var shading=new Vector3[before.Length];
        var groups=Enumerable.Range(0,before.Length).ToArray();
        Span<float> values=stackalloc float[4];
        foreach(int v in drawn)
        {
            var key=(before[v],after[v]);
            if(!positions.TryGetValue(key,out int id))positions.Add(key,id=positions.Count);
            position[v]=id;
            SecondSkinWriter.ReadTyped(streams[normal.Stream],v*strides[normal.Stream]+normal.Offset,normal.Type,values);
            shading[v]=new(values[0],values[1],values[2]);
        }
        if(positions.Count==drawn.Length)return;
        int Root(int v)
        {
            while(groups[v]!=v){groups[v]=groups[groups[v]];v=groups[v];}
            return v;
        }
        var owners=new Dictionary<(int At,int Other,Vector3 Normal),int>();
        void Connect(int a,int b)
        {
            if(position[a]==position[b])return;
            var edge=(position[a],position[b],shading[a]);
            if(owners.TryGetValue(edge,out int other))groups[Root(a)]=Root(other);
            else owners.Add(edge,a);
        }
        for(int f=0;f<triangles.Length;f+=3)
            for(int j=0;j<3;j++)
            {
                int a=triangles[f+j],b=triangles[f+(j+1)%3];
                Connect(a,b);Connect(b,a);
            }
        foreach(int v in drawn)
        {
            int root=Root(v);
            if(root!=v){from[root]+=from[v];to[root]+=to[v];}
        }
        foreach(int v in drawn){int root=Root(v);from[v]=from[root];to[v]=to[root];}
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

    // A new shape replacement carries the entire authored vertex, including
    // its UVs, frame, colours and weights, not just the shape's position delta.
    internal static byte[] ShapeRow(byte[][] streams,int[] stride,E[] fields,int[] corners,double[] weights)
    {
        var row=new byte[stride.Sum()];
        Span<float> sample=stackalloc float[4],sum=stackalloc float[4];
        int dominant=Array.IndexOf(weights,weights.Max());
        foreach(var e in fields)
        {
            if(e.Usage is 1 or 2) continue;
            sum.Clear();float handedness=0;
            for(int j=0;j<3;j++)
            {
                SecondSkinWriter.ReadTyped(streams[e.Stream],corners[j]*stride[e.Stream]+e.Offset,e.Type,sample);
                for(int k=0;k<4;k++) sum[k]+=(float)weights[j]*sample[k];
                if(j==dominant) handedness=sample[3];
            }
            if(e.Usage is 3 or 5 or 6)
            {
                var normal=new Vector3(sum[0],sum[1],sum[2]);
                if(e.Type==8) normal=2*normal-Vector3.One;
                if(normal.LengthSquared()>1e-10f) normal=Vector3.Normalize(normal);
                if(e.Type==8) normal=(normal+Vector3.One)*.5f;
                sum[0]=normal.X;sum[1]=normal.Y;sum[2]=normal.Z;
                if(e.Usage is 5 or 6) sum[3]=handedness;
            }
            int offset=stride.Take(e.Stream).Sum()+e.Offset;
            int count=e.Type switch {0=>1,1 or 13=>2,2=>3,_=>4};
            for(int k=0;k<count;k++)
                if(e.Type is 13 or 14) BitConverter.TryWriteBytes(row.AsSpan(offset+2*k),(Half)sum[k]);
                else if(e.Type is 5 or 8) row[offset+k]=(byte)Math.Clamp(MathF.Round(sum[k]*(e.Type==8?255:1)),0,255);
                else BitConverter.TryWriteBytes(row.AsSpan(offset+4*k),sum[k]);
        }
        var w=fields.First(e=>e.Usage==1);var bones=fields.First(e=>e.Usage==2);
        int width=w.Type==17?8:4;
        var influences=new Dictionary<byte,double>();
        for(int j=0;j<3;j++)
        for(int k=0;k<width;k++)
        {
            byte bone=streams[bones.Stream][corners[j]*stride[bones.Stream]+bones.Offset+k];
            int strength=streams[w.Stream][corners[j]*stride[w.Stream]+w.Offset+k];
            if(strength>0 && weights[j]>0) influences[bone]=influences.GetValueOrDefault(bone)+weights[j]*strength;
        }
        var selected=influences.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).Take(width).ToArray();
        double total=selected.Sum(p=>p.Value),cumulative=0;int used=0;
        int weightOffset=stride.Take(w.Stream).Sum()+w.Offset,boneOffset=stride.Take(bones.Stream).Sum()+bones.Offset;
        for(int k=0;k<width;k++)
        {
            if(k<selected.Length) cumulative+=selected[k].Value;
            int packed=total==0?0:(int)Math.Round(255*cumulative/total,MidpointRounding.AwayFromZero);
            row[weightOffset+k]=(byte)(packed-used);row[boneOffset+k]=k<selected.Length?selected[k].Key:(byte)0;used=packed;
        }
        return row;
    }
}
