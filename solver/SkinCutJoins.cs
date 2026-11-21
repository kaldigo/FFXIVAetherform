using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class SkinCutJoins
{
    internal readonly record struct Sample(int Face, Point Barycentric, int ClothVertex);
    internal readonly record struct Corner(int Triangle,int Index);
    internal sealed record Result(List<Cut> Triangles, Dictionary<int, List<Corner>> Joins);

    private static double Distance2(Point a,Point b)
    {
        var d=a-b;
        return d.X*d.X+d.Y*d.Y;
    }

    // Barycentric nodes on a native indexed edge have two face-local addresses.
    // Propagate their canonical parameter across that edge before triangulation.
    internal static Result Conform(List<Cut> cuts, int[] indices, Sample[] samples, System.Numerics.Vector3[] nativeMm)
    {
        const double eps=1e-8;
        var knots=new Dictionary<int,List<Point>>();
        var onEdge=new Dictionary<(int,int),List<double>>();
        var incident=new Dictionary<(int,int),List<(int Face,int Start,int End)>>();
        Point[] corners=[new(0,0),new(1,0),new(0,1)];
        for(int f=0;f<indices.Length/3;f++)
            for(int e=0;e<3;e++)
            {
                int a=e,b=(e+1)%3;
                if(indices[3*f+a]>indices[3*f+b]) (a,b)=(b,a);
                var key=(indices[3*f+a],indices[3*f+b]);
                if(!incident.TryGetValue(key,out var list)) incident.Add(key,list=[]);
                list.Add((f,a,b));
            }
        void Insert(int face,Point p)
        {
            if(!knots.TryGetValue(face,out var list)) knots.Add(face,list=[]);
            if(!list.Any(old=>Distance2(old,p)<=eps*eps)) list.Add(p);
        }
        void Collect(int face,Point p)
        {
            Insert(face,p);
            double[] w=[1-p.X-p.Y,p.X,p.Y];
            int zero=Array.FindIndex(w,x=>Math.Abs(x)<=eps);
            if(zero<0) return;
            int a=(zero+1)%3,b=(zero+2)%3;
            if(w[a]<=eps || w[b]<=eps) return;
            if(indices[3*face+a]>indices[3*face+b]) (a,b)=(b,a);
            var key=(indices[3*face+a],indices[3*face+b]);
            if(!onEdge.TryGetValue(key,out var list)) onEdge.Add(key,list=[]);
            double t=w[b]/(w[a]+w[b]);
            if(!list.Any(old=>Math.Abs(t-old)<=eps)) list.Add(t);
        }
        foreach(var cut in cuts)
            foreach(var p in cut.Barycentric.Polygon) Collect(cut.Face,p);
        var prescribed=samples.Select(s=>(s.Face,s.Barycentric)).ToHashSet();
        foreach(var sample in samples) Collect(sample.Face,sample.Barycentric);
        foreach(var (key,parameters) in onEdge)
            foreach(var (face,a,b) in incident[key])
                foreach(double t in parameters) Insert(face,(1-t)*corners[a]+t*corners[b]);

        var triangles=new List<Cut>();
        var byFace=new Dictionary<int,List<int>>();
        foreach(var cut in cuts)
        {
            var pieces=new List<Triangle>{cut.Barycentric};
            foreach(var p in knots[cut.Face])
            {
                var weights=cut.Barycentric.Barycentric(p);
                double min=Math.Min(1-weights.X-weights.Y,Math.Min(weights.X,weights.Y));
                if(min < -1e-6 || min>1e-6 && !prescribed.Contains((cut.Face,p))) continue;
                int count=pieces.Count;
                for(int i=0;i<count;i++)
                {
                    var tri=pieces[i]; var v=tri.Polygon;
                    if(v.Any(c=>Distance2(c,p)<=eps*eps)) continue;
                    bool split=false;
                    for(int e=0;e<3;e++)
                    {
                        Point a=v[e],b=v[(e+1)%3],c=v[(e+2)%3];
                        var edge=b-a; double length2=Distance2(a,b);
                        if(length2<=eps*eps) continue;
                        double along=((p.X-a.X)*edge.X+(p.Y-a.Y)*edge.Y)/length2;
                        if(along<=0 || along>=1 || Distance2(a+along*edge,p)>eps*eps) continue;
                        var l=new Triangle(a,p,c); var r=new Triangle(p,b,c);
                        if(l.SignedArea<=0 || r.SignedArea<=0) continue;
                        pieces[i]=l; pieces.Add(r); split=true; break;
                    }
                    if(!split && prescribed.Contains((cut.Face,p)))
                    {
                        var bc=tri.Barycentric(p);
                        if(bc.X>0 && bc.Y>0 && bc.X+bc.Y<1)
                        {
                            pieces[i]=new(v[0],v[1],p);
                            pieces.Add(new(v[1],v[2],p)); pieces.Add(new(v[2],v[0],p));
                        }
                    }
                }
            }
            if(!byFace.TryGetValue(cut.Face,out var ids)) byFace.Add(cut.Face,ids=[]);
            foreach(var piece in pieces)
            {
                ids.Add(triangles.Count); triangles.Add(new(cut.Face,piece,cut.Domain));
            }
        }
        var joins=new Dictionary<int,List<Corner>>();
        foreach(var s in samples)
        {
            var addresses=new List<(int Face,Point P)>{(s.Face,s.Barycentric)};
            double[] w=[1-s.Barycentric.X-s.Barycentric.Y,s.Barycentric.X,s.Barycentric.Y];
            for(int e=0;e<3;e++)
            {
                if(Math.Abs(w[e])>eps) continue;
                int a=(e+1)%3,b=(e+2)%3;
                if(indices[3*s.Face+a]>indices[3*s.Face+b]) (a,b)=(b,a);
                foreach(var (face,i,j) in incident[(indices[3*s.Face+a],indices[3*s.Face+b])])
                    addresses.Add((face,w[a]*corners[i]+w[b]*corners[j]));
            }
            var matches=new HashSet<Corner>();
            foreach(var (face,p) in addresses)
                if(byFace.TryGetValue(face,out var ids))
                    foreach(int i in ids)
                    {
                        var v=triangles[i].Barycentric.Polygon;
                        for(int j=0;j<3;j++)
                            if(Distance2(p,v[j])<=4*eps*eps) matches.Add(new(i,j));
                    }
            if(matches.Count==0 && byFace.TryGetValue(s.Face,out var sameFace))
            {
                // Retain the existing cut knot when both addresses on this AF face
                // agree within the offline join budget (0.0001 mm).
                int t=3*s.Face;
                var ab=nativeMm[t+1]-nativeMm[t]; var ac=nativeMm[t+2]-nativeMm[t];
                foreach(int i in sameFace)
                {
                    var vertices=triangles[i].Barycentric.Polygon;
                    for(int j=0;j<3;j++)
                    {
                        var d=vertices[j]-s.Barycentric;
                        if(((float)d.X*ab+(float)d.Y*ac).LengthSquared()<=1e-8f)
                            matches.Add(new(i,j));
                    }
                }
            }
            if(matches.Count==0) throw new InvalidDataException($"Missing AF cut join for cloth vertex {s.ClothVertex} on native face {s.Face}.");
            if(!joins.TryGetValue(s.ClothVertex,out var group)) joins.Add(s.ClothVertex,group=[]);
            group.AddRange(matches);
        }
        return new(triangles,joins);
    }
}

