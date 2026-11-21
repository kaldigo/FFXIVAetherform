using System.Numerics;
using Proteus.Services;
using E=Proteus.Services.SecondSkinWriter.VElem;
using Source=Proteus.Services.SecondSkinWriter.Source;

namespace Aetherform.Geometry;

internal static class ClothSubdivision
{
    private static int I(byte[] b,int o)=>BitConverter.ToInt32(b,o);
    private static int U(byte[] b,int o)=>BitConverter.ToUInt16(b,o);
    private sealed class Layout(Source src,int index)
    {
        internal readonly byte[] Data=src.S;
        internal readonly int Header=src.MeshStart+index*36;
        internal readonly E[] Fields=src.Decls[index];
        internal readonly string Material=src.MatNames[U(src.S,src.MeshStart+index*36+8)];
        internal int Count=>U(Data,Header);
        internal int First=>I(Data,Header+16);
        internal int Stride(int s)=>Data[Header+32+s];
        internal int Stream(int s)=>src.Vb+I(Data,Header+20+4*s);
        private ushort[] Palette => src.BoneTables[U(Data,Header+14)];
        internal Dictionary<string,double> Weights(int vertex)
        {
            var weights=Fields.First(e=>e.Usage==1);
            var bones=Fields.First(e=>e.Usage==2);
            var result=new Dictionary<string,double>();
            for(int k=0;k<(weights.Type==17?8:4);k++)
            {
                int strength=Data[Stream(weights.Stream)+weights.Offset+vertex*Stride(weights.Stream)+k];
                if(strength==0) continue;
                int bone=Data[Stream(bones.Stream)+bones.Offset+vertex*Stride(bones.Stream)+k];
                string name=src.BoneNames[Palette[bone]];
                result[name]=result.GetValueOrDefault(name)+strength/255.0;
            }
            return result;
        }
        internal ushort[] Indices()
        {
            var values=new ushort[I(Data,Header+4)];
            Buffer.BlockCopy(Data,src.Ib+2*First,values,0,values.Length*2);
            return values;
        }
        internal Vector3[] Positions(int count)
        {
            var pos=Fields.First(e=>e.Usage==0);
            var points=new Vector3[count];
            Span<float> v=stackalloc float[4];
            for(int i=0;i<count;i++)
            {
                SecondSkinWriter.ReadTyped(Data,Stream(pos.Stream)+Stride(pos.Stream)*i+pos.Offset,pos.Type,v);
                points[i]=new(v[0],v[1],v[2]);
            }
            return points;
        }
    }

    internal readonly record struct Rim(int Mesh,int A,int B,Vector3 Start,Vector3 End);
    internal static Dictionary<(int Mesh,int A,int B),Rim> IdentifyJoins(Source source) => IdentifyJoins(source,out _,false);
    internal static Dictionary<(int Mesh,int A,int B),Rim> IdentifyJoins(Source source,out bool unmatchedCut,bool inspectCut=true)
    {
        unmatchedCut=false;
        var skin=new List<Rim>(); var cloth=new List<Rim>();
        var layouts=new Dictionary<int,Layout>();
        for(int m=source.Lod0MeshIndex;m<source.Lod0MeshIndex+source.Lod0MeshCount;m++)
        {
            var layout=new Layout(source,m);layouts.Add(m,layout);
            var edges=new Dictionary<(int,int),int>();
            bool isSkin=SecondSkinWriter.IsBodySkinMaterial(layout.Material);
            var positions=layout.Positions(layout.Count);
            var ix=layout.Indices();
            for(int j=0;j<ix.Length;j+=3)
            {
                if(Vector3.Cross(positions[ix[j+1]]-positions[ix[j]],positions[ix[j+2]]-positions[ix[j]]).LengthSquared()<=4e-28f) continue;
                for(int k=0;k<3;k++)
                {
                    int a=ix[j+k],b=ix[j+(k+1)%3];
                    var key=(Math.Min(a,b),Math.Max(a,b));edges[key]=edges.GetValueOrDefault(key)+1;
                }
            }
            foreach(var ((a,b),count) in edges)
                if(count==1) (isSkin?skin:cloth).Add(new(m,a,b,positions[a],positions[b]));
        }
        var weights=new Dictionary<(int,int),Dictionary<string,double>>();
        Dictionary<string,double> At(int m,int i)
        {
            if(!weights.TryGetValue((m,i),out var w)) weights[(m,i)]=w=layouts[m].Weights(i);
            return w;
        }
        bool SameMotion(int a,int b,Vector3 p,int c,int d,Vector3 q)
        {
            var lhs=At(a,b);var rhs=At(c,d);
            foreach(string name in lhs.Keys.Union(rhs.Keys))
            {
                double u=lhs.GetValueOrDefault(name),v=rhs.GetValueOrDefault(name);
                var diff=1000*((float)u*p-(float)v*q);
                if(diff.LengthSquared()+(u-v)*(u-v)>4e-6) return false;
            }
            return true;
        }
        var result=new Dictionary<(int,int,int),Rim>();
        foreach(var edge in cloth)
        {
            var matches=new List<Rim>();
            foreach(var candidate in skin)
            {
                var s=candidate;
                if(Vector3.DistanceSquared(edge.Start,s.End)<Vector3.DistanceSquared(edge.Start,s.Start))
                    s=new(s.Mesh,s.B,s.A,s.End,s.Start);
                if(Vector3.DistanceSquared(edge.Start,s.Start)>1e-12f || Vector3.DistanceSquared(edge.End,s.End)>1e-12f) continue;
                if(SameMotion(edge.Mesh,edge.A,edge.Start,s.Mesh,s.A,s.Start) &&
                   SameMotion(edge.Mesh,edge.B,edge.End,s.Mesh,s.B,s.End)) matches.Add(s);
            }
            if(matches.Count==1) result[(edge.Mesh,edge.A,edge.B)]=matches[0];
        }
        if(!inspectCut)return result;
        // Vertices split by a material, UV island, visibility mask or skinning
        // weights need not have the same tessellation on both sides of a hem.
        // A complete source edge supported by the opposite rim is a join even
        // if it cannot be represented by the one-to-one replacement schedule.
        bool Touch(Vector3 point,Rim edge)
        {
            var ab=edge.End-edge.Start;
            float t=Math.Clamp(Vector3.Dot(point-edge.Start,ab)/Math.Max(1e-14f,ab.LengthSquared()),0,1);
            return Vector3.DistanceSquared(point,edge.Start+t*ab)<1e-8f;
        }
        bool Follows(Rim edge,List<Rim> opposite)
        {
            if(Vector3.DistanceSquared(edge.Start,edge.End)<4e-6f)return false;
            var center=(edge.Start+edge.End)/2;
            if(!opposite.Any(e=>Touch(center,e)))return false;
            return opposite.Any(e=>Touch(edge.Start,e)) && opposite.Any(e=>Touch(edge.End,e));
        }
        var paired=result.Values.Select(e=>(e.Mesh,Math.Min(e.A,e.B),Math.Max(e.A,e.B))).ToHashSet();
        unmatchedCut=cloth.Any(e=>!result.ContainsKey((e.Mesh,e.A,e.B)) && Follows(e,skin))
            || skin.Any(e=>!paired.Contains((e.Mesh,e.A,e.B)) && Follows(e,cloth));
        return result;
    }

    // Keep the cloth and skin edge identical under animation. A midpoint with
    // blended weights generally moves off the straight edge of the other mesh.
    internal static int[] Children(int a,int b,int c,int ab,int bc,int ca)
    {
        if(ab>=0 && bc>=0 && ca>=0) return [a,ab,ca, ab,b,bc, ca,bc,c, ab,bc,ca];
        if(ab<0 && bc<0 && ca<0) return [a,b,c];
        if(ab>=0 && bc<0 && ca<0) return [a,ab,c, ab,b,c];
        if(bc>=0 && ca<0 && ab<0) return [b,bc,a, bc,c,a];
        if(ca>=0 && ab<0 && bc<0) return [c,ca,b, ca,a,b];
        if(ab<0) return [a,b,bc, a,bc,ca, ca,bc,c];
        if(bc<0) return [b,c,ca, b,ca,ab, ab,ca,a];
        return [c,a,ab, c,ab,bc, bc,ab,b];
    }

    internal sealed record Origins(Vector3[] Positions,int[] Parents);
}
