using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;
namespace Aetherform.Geometry;

// A UV split and a native body-slot rim can clip a narrow sliver off an otherwise
// covered AF triangle. A clothing hem is an intentional cut and cannot be extended.
internal sealed class SkinSeamCoverage
{
    private static readonly Point[] Basis=[new(0,0),new(1,0),new(0,1)];
    private static readonly Point[] Probes=[..Basis,new(.5,0),new(0,.5),new(.5,.5),new(1d/3,1d/3)];
    private readonly Dictionary<(int Face,int Domain),Cut[]> completed=[];
    private static (Vector3,Vector3) Edge(Vector3 a,Vector3 b) =>
        (a.X,a.Y,a.Z).CompareTo((b.X,b.Y,b.Z))<=0?(a,b):(b,a);
    private static Vector3 Pos(NativeSkinChart chart,int face,Point p)
    {
        int i=3*face;var v=chart.PositionsMm;
        return v[i]+(float)p.X*(v[i+1]-v[i])+(float)p.Y*(v[i+2]-v[i]);
    }
    internal static float Distance(Vector3 p,Vector3 a,Vector3 b)
    {
        var d=b-a;return Vector3.Distance(p,a+Math.Clamp(Vector3.Dot(p-a,d)/Math.Max(1e-12f,d.LengthSquared()),0,1)*d);
    }
    internal static (Vector3,Vector3)[] SlotEdges(byte[] bare)
    {
        var model=SecondSkinWriter.Parse(bare);var edges=new Dictionary<(Vector3,Vector3),int>();
        for(int m=model.Lod0MeshIndex;m<model.Lod0MeshIndex+model.Lod0MeshCount;m++)
        {
            if(!SecondSkinWriter.IsBodySkinMaterial(model.MatNames[BitConverter.ToUInt16(bare,model.MeshStart+36*m+8)]))continue;
            var mesh=new GarmentClearance.Mesh(model,m);
            for(int f=0;f<mesh.Indices.Length;f+=3)
            {
                var a=mesh.P[mesh.Indices[f]];var b=mesh.P[mesh.Indices[f+1]];var c=mesh.P[mesh.Indices[f+2]];
                if(Vector3.Cross(b-a,c-a).LengthSquared()<1e-12f)continue;
                for(int k=0;k<3;k++){var key=Edge(mesh.P[mesh.Indices[f+k]],mesh.P[mesh.Indices[f+(k+1)%3]]);edges[key]=edges.GetValueOrDefault(key)+1;}
            }
        }
        return edges.Where(x=>x.Value==1).Select(x=>x.Key).ToArray();
    }
    internal void Complete(List<Cut> cuts, List<GarmentSkin.Face> faces,NativeSkinChart target,byte[] source,byte[] bare)
    {
        var src=SecondSkinWriter.Parse(source);
        var rims=SlotEdges(bare);
        var permitted=new List<(Vector3 A,Vector3 B,int Domain)>();
        var slotRims=new List<(Vector3 A,Vector3 B,int Domain)>();
        var genuine=new List<(Vector3 A,Vector3 B,int Domain)>();
        foreach(var draw in faces.GroupBy(f=>f.Mesh))
        {
            var mesh=new GarmentClearance.Mesh(src,draw.Key);
            var sides=draw.SelectMany(f=>Enumerable.Range(0,3).Select(i=>(F:f,A:i,B:(i+1)%3)));
            foreach(var edge in sides.GroupBy(s=>Edge(mesh.P[s.F.Vertices[s.A]],mesh.P[s.F.Vertices[s.B]])))
            {
                var group=edge.ToArray();var (a,b)=edge.Key;
                bool shared=group.Length==2 && group[0].F.Domain==group[1].F.Domain;
                bool slot=group.Length==1 && rims.Any(r=>Distance(a,r.Item1,r.Item2)<.05f)
                    && rims.Any(r=>Distance(b,r.Item1,r.Item2)<.05f) && rims.Any(r=>Distance((a+b)/2,r.Item1,r.Item2)<.15f);
                if(shared)
                {
                    var t=group[0];var u=group[1];var uv=t.F.UV.Polygon;var other=u.F.UV.Polygon;
                    if(new[]{u.A,u.B}.Any(k=>Math.Abs(uv[t.A].X-other[k].X)+Math.Abs(uv[t.A].Y-other[k].Y)<1e-8)
                       && new[]{u.A,u.B}.Any(k=>Math.Abs(uv[t.B].X-other[k].X)+Math.Abs(uv[t.B].Y-other[k].Y)<1e-8))continue;
                }
                foreach(var s in group)
                {
                    if(slot)slotRims.Add((s.F.Position[s.A],s.F.Position[s.B],s.F.Domain));
                    (slot||shared?permitted:genuine).Add((s.F.Position[s.A],s.F.Position[s.B],s.F.Domain));
                }
            }
        }
        if(permitted.Count==0)return;
        var result=new List<Cut>();
        foreach(var batch in cuts.GroupBy(c=>(c.Face,c.Domain)))
        {
            var rows=batch.ToArray();var (f,domain)=batch.Key;
            if(rows.Length==1 && rows[0].Barycentric==new Triangle(Basis[0],Basis[1],Basis[2])){result.Add(rows[0]);continue;}
            var center=Pos(target,f,new(1d/3,1d/3));
            var near=permitted.Where(e=>e.Domain==domain && Distance(center,e.A,e.B)<35).ToArray();
            bool safe=near.Length>0;
            foreach(var p in Probes)
            {
                if(!safe)break;
                Clamp(target,f,p,rows,out float gap);if(gap<.0001f)continue;
                var q=Pos(target,f,p);
                bool rim=slotRims.Any(e=>e.Domain==domain && Distance(q,e.A,e.B)<2.5f && gap<Math.Min(8,Vector3.Distance(e.A,e.B)*.4f));
                safe=(rim || gap<4 && near.Any(e=>Distance(q,e.A,e.B)<5))
                    && !genuine.Any(e=>e.Domain==domain && Distance(q,e.A,e.B)<4);
            }
            if(safe){completed.Add(batch.Key,rows);result.Add(new(f,new(Basis[0],Basis[1],Basis[2]),domain));}
            else result.AddRange(rows);
        }
        cuts.Clear();cuts.AddRange(result);
    }
    private static Point Clamp(NativeSkinChart target,int f,Point p,Cut[] rows,out float gap)
    {
        var q=Pos(target,f,p);float best=float.PositiveInfinity;var closest=p;
        foreach(var row in rows)
        {
            var t=row.Barycentric;var b=t.Barycentric(p);
            if(b.X>=-1e-9 && b.Y>=-1e-9 && b.X+b.Y<=1+1e-9){gap=0;return p;}
            var h=BrushTransfer.ClosestOnTriangle(q,Pos(target,f,t.A),Pos(target,f,t.B),Pos(target,f,t.C),out float x,out float y,out float z);
            float d=Vector3.Distance(q,h);if(d<best){best=d;closest=x*t.A+y*t.B+z*t.C;}
        }
        gap=best;return closest;
    }
    internal Point Carrier(NativeSkinChart chart,Cut cut,Point p) =>
        completed.TryGetValue((cut.Face,cut.Domain),out var rows)?Clamp(chart,cut.Face,p,rows,out _):p;
}
