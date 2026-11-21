using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

// Register a morphed source skin point only within its AF UV island.
// Render-seam points use the boundary of the same island.
internal sealed class SkinRegistration
{
    internal readonly record struct Address(int Face,Point Barycentric,Point Chart);
    private readonly NativeSkinChart skin;
    private readonly record struct Edge(int Face,int A,int B);
    private readonly Dictionary<int,int[]> components;
    private readonly Dictionary<int,Edge[]> rims;
    private readonly int[] component;
    private readonly Dictionary<int,double> widths;
    private static Point Project(Point p,Point a,Point b,out double fraction)
    {
        var ab=b-a;
        double squared=ab.X*ab.X+ab.Y*ab.Y;
        fraction=squared==0 ? 0 : Math.Clamp(((p.X-a.X)*ab.X+(p.Y-a.Y)*ab.Y)/squared,0,1);
        return a+fraction*ab;
    }
    private static double Squared(Point a,Point b)
    {
        var d=a-b;return d.X*d.X+d.Y*d.Y;
    }
    internal SkinRegistration(NativeSkinChart skin)
    {
        this.skin=skin;
        int n=skin.Cutting.Length;
        var roots=Enumerable.Range(0,n).ToArray();
        int Root(int f)
        {
            while(f!=roots[f]) {roots[f]=roots[roots[f]];f=roots[f];} return f;
        }
        var edges=new Dictionary<(int,int),List<Edge>>();
        for(int f=0;f<n;f++) for(int a=0;a<3;a++)
        {
            int b=(a+1)%3,u=skin.RenderCorners[3*f+a],v=skin.RenderCorners[3*f+b];
            var key=(Math.Min(u,v),Math.Max(u,v));
            if(!edges.TryGetValue(key,out var list)) edges.Add(key,list=[]);
            if(list.Count>0) roots[Root(f)]=Root(list[0].Face);
            list.Add(new(f,a,b));
        }
        component=Enumerable.Range(0,n).Select(Root).ToArray();
        components=Enumerable.Range(0,n).GroupBy(f=>component[f]).ToDictionary(g=>g.Key,g=>g.ToArray());
        rims=edges.Values.Where(e=>e.Count==1).Select(e=>e[0]).GroupBy(e=>component[e.Face]).ToDictionary(g=>g.Key,g=>g.ToArray());
        widths=components.ToDictionary(g=>g.Key,g=>
        {
            var xy=g.Value.SelectMany(f=>skin.Cutting[f].Polygon).ToArray();
            return Math.Max(xy.Max(p=>p.X)-xy.Min(p=>p.X),xy.Max(p=>p.Y)-xy.Min(p=>p.Y));
        });
    }

    internal Address NearestChart(int previous,Point uv,bool seam)
    {
        int domain=component[previous];double best=double.PositiveInfinity;Address? selected=null;
        Point[] basis=[new(0,0),new(1,0),new(0,1)];
        void Edge(int f,int a,int b)
        {
            var poly=skin.Cutting[f].Polygon;
            var point=Project(uv,poly[a],poly[b],out double frac);
            double score=Squared(uv,point);
            if(score<best){best=score;selected=new(f,(1-frac)*basis[a]+frac*basis[b],point);}
        }
        if(seam) foreach(var e in rims[domain]) Edge(e.Face,e.A,e.B);
        else foreach(int f in components[domain])
        {
            var t=skin.Cutting[f];var b=t.Barycentric(uv);
            if(b.X>=0 && b.Y>=0 && b.X+b.Y<=1) return new(f,b,uv);
            for(int j=0;j<3;j++) Edge(f,j,(j+1)%3);
        }
        return selected ?? throw new SkinRegistrationException("No continuous replacement skin chart.");
    }
    internal Address Locate(Vector3 positionMm,Point sourceUV,bool seam)
    {
        double bestUV=double.PositiveInfinity;int domain=-1;
        for(int f=0;f<skin.Cutting.Length;f++)
        {
            var t=skin.Cutting[f];
            double gap;
            var bc=t.Barycentric(sourceUV);
            if(bc.X>=0 && bc.Y>=0 && bc.X+bc.Y<=1) gap=0;
            else gap=Math.Min(Squared(sourceUV,Project(sourceUV,t.A,t.B,out _)),
                Math.Min(Squared(sourceUV,Project(sourceUV,t.B,t.C,out _)),Squared(sourceUV,Project(sourceUV,t.C,t.A,out _))));
            if(gap>=bestUV) continue;
            bestUV=gap;domain=component[f];
            if(gap==0) break;
        }
        if(domain<0) throw new SkinRegistrationException("No AF skin chart for source UV.");
        var candidates=new List<(int Face,Point Bary,double Distance)>();
        if(seam)
        {
            if(!rims.TryGetValue(domain,out var edges)) throw new SkinRegistrationException("A source seam maps to a closed AF chart.");
            var distances=edges.Select(e=>
            {
                var uv=skin.Cutting[e.Face].Polygon;
                return Math.Sqrt(Squared(sourceUV,Project(sourceUV,uv[e.A],uv[e.B],out _)));
            }).ToArray();
            double limit=distances.Min()+widths[domain]/6;
            Point[] unit=[new(0,0),new(1,0),new(0,1)];
            for(int i=0;i<edges.Length;i++)
            {
                if(distances[i]>limit) continue;
                var e=edges[i];int first=3*e.Face;
                var a=skin.PositionsMm[first+e.A];var b=skin.PositionsMm[first+e.B];var d=b-a;
                float u=Math.Clamp(Vector3.Dot(positionMm-a,d)/Math.Max(1e-30f,d.LengthSquared()),0,1);
                candidates.Add((e.Face,(1-u)*unit[e.A]+u*unit[e.B],Vector3.Distance(positionMm,a+u*d)));
            }
        }
        else
        {
            foreach(int face in components[domain])
            {
                int i=3*face;
                var hit=BrushTransfer.ClosestOnTriangle(positionMm,skin.PositionsMm[i],skin.PositionsMm[i+1],skin.PositionsMm[i+2],
                    out _,out float b,out float c);
                candidates.Add((face,new(b,c),Vector3.Distance(positionMm,hit)));
            }
        }
        double near=candidates.Min(c=>c.Distance)+(seam ? 1e-4 : 1e-5);
        double score=double.PositiveInfinity;Address? result=null;
        foreach(var c in candidates)
        {
            if(c.Distance>near) continue;
            var t=skin.Cutting[c.Face];
            var xy=t.A+c.Bary.X*(t.B-t.A)+c.Bary.Y*(t.C-t.A);
            double error=Squared(xy,sourceUV);
            if(error<score){score=error;result=new(c.Face,c.Bary,xy);}
        }
        return result ?? throw new SkinRegistrationException("AF skin registration produced no finite candidate.");
    }
}
