namespace Aetherform.Geometry;

// Work in the chart of a single native AF triangle. This keeps coincident
// render seams independent and makes the cut reusable across AF presets.
internal static class SkinCoverage
{
    internal readonly record struct Point(double X, double Y)
    {
        public static Point operator +(Point a, Point b) => new(a.X+b.X,a.Y+b.Y);
        public static Point operator -(Point a, Point b) => new(a.X-b.X,a.Y-b.Y);
        public static Point operator *(double k, Point a) => new(k*a.X,k*a.Y);
        internal double Cross(Point b) => X*b.Y-Y*b.X;
    }

    internal readonly record struct Triangle(Point A, Point B, Point C)
    {
        internal double SignedArea => (B-A).Cross(C-A)*.5;
        internal Point[] Polygon => [A,B,C];
        internal Point Barycentric(Point p)
        {
            var d=p-A;
            double determinant=2*SignedArea;
            return new(d.Cross(C-A)/determinant,(B-A).Cross(d)/determinant);
        }
    }

    // Each point stores the B and C barycentric weights in the native face.
    internal readonly record struct Cut(int Face, Triangle Barycentric, int Domain=0);
    internal readonly record struct Region(Triangle UV,int Domain);

    private static double Area(List<Point> polygon)
    {
        double sum=0;
        for(int j=2;j<polygon.Count;j++)
            sum+=(polygon[j-1]-polygon[0]).Cross(polygon[j]-polygon[0]);
        return sum*.5;
    }

    private static List<Point> HalfPlane(List<Point> polygon, Point a, Point b, int side, double tolerance)
    {
        var output=new List<Point>(polygon.Count+1);
        if(polygon.Count==0) return output;
        var edge=b-a;
        for(int i=0;i<polygon.Count;i++)
        {
            var last=polygon[i]; var p=polygon[(i+1)%polygon.Count];
            double previous=side*edge.Cross(last-a),current=side*edge.Cross(p-a);
            if(previous>=-tolerance) output.Add(last);
            if((current>=-tolerance)!=(previous>=-tolerance))
                output.Add(last+previous/(previous-current)*(p-last));
        }
        return output;
    }

    private static List<Point> Clip(List<Point> polygon, IReadOnlyList<Point> window, int sign)
    {
        for(int j=0;j<window.Count && polygon.Count>=3;j++)
            polygon=HalfPlane(polygon,window[j],window[(j+1)%window.Count],sign,0);
        return polygon;
    }

    private static IEnumerable<List<Point>> Difference(List<Point> polygon,List<Point> old)
    {
        int side=Math.Sign(Area(old));
        if(Math.Abs(Area(Clip(polygon,old,side)))<1e-13)
        {
            yield return polygon;
            yield break;
        }
        for(int j=0;j<old.Count && polygon.Count>=3;j++)
        {
            var a=old[j]; var b=old[(j+1)%old.Count];
            var outside=HalfPlane(polygon,a,b,-side,0);
            if(Math.Abs(Area(outside))>1e-14) yield return outside;
            polygon=HalfPlane(polygon,a,b,side,0);
        }
    }

    private static IEnumerable<(int X,int Y)> Cells(Triangle t)
    {
        int Cell(double x)=>checked((int)Math.Floor(x*64));
        for(int x=Cell(Math.Min(t.A.X,Math.Min(t.B.X,t.C.X)));x<=Cell(Math.Max(t.A.X,Math.Max(t.B.X,t.C.X)));x++)
        for(int y=Cell(Math.Min(t.A.Y,Math.Min(t.B.Y,t.C.Y)));y<=Cell(Math.Max(t.A.Y,Math.Max(t.B.Y,t.C.Y)));y++)
            yield return (x,y);
    }

    internal static List<Cut> Compile(Triangle[] native, Triangle[] registered) =>
        Compile(native, registered.Select(t=>new Region(t,0)));

    internal static List<Cut> Compile(Triangle[] native, IEnumerable<Region> registered)
    {
        var buckets=new Dictionary<(int,int),List<int>>();
        for(int face=0;face<native.Length;face++)
        {
            if(Math.Abs(native[face].SignedArea)<1e-12) continue;
            foreach(var cell in Cells(native[face]))
            {
                if(!buckets.TryGetValue(cell,out var values)) buckets.Add(cell,values=[]);
                values.Add(face);
            }
        }
        var unions=new SortedDictionary<(int Face,int Domain),List<List<Point>>>();
        var candidates=new HashSet<int>();
        foreach(var (source,domain) in registered)
        {
            candidates.Clear();
            foreach(var cell in Cells(source))
                if(buckets.TryGetValue(cell,out var values)) candidates.UnionWith(values);
            foreach(int face in candidates)
            {
                var target=native[face];
                var poly=Clip([..source.Polygon],target.Polygon,Math.Sign(target.SignedArea));
                if(Math.Abs(Area(poly))<1e-13) continue;
                if(!unions.TryGetValue((face,domain),out var pieces)) unions.Add((face,domain),pieces=[]);
                var pending=new List<List<Point>> {poly};
                foreach(var old in pieces)
                {
                    pending=pending.SelectMany(piece=>Difference(piece,old)).ToList();
                    if(pending.Count==0) break;
                }
                pieces.AddRange(pending);
            }
        }
        var result=new List<Cut>();
        foreach(var ((face,domain),pieces) in unions)
        {
            var target=native[face];
            double whole=Math.Abs(target.SignedArea), total=pieces.Sum(p=>Math.Abs(Area(p)));
            if(total>whole*(1+1e-7)) throw new InvalidDataException($"Overlapping AF cut on face {face}.");
            if(Math.Abs(total-whole)<=whole*1e-7)
            {
                result.Add(new(face,new(new(0,0),new(1,0),new(0,1)),domain));
                continue;
            }
            foreach(var polygon in pieces)
            {
                if(Math.Sign(Area(polygon))!=Math.Sign(target.SignedArea)) polygon.Reverse();
                var a=target.Barycentric(polygon[0]);
                for(int j=2;j<polygon.Count;j++)
                {
                    var tri=new Triangle(a,target.Barycentric(polygon[j-1]),target.Barycentric(polygon[j]));
                    if(tri.SignedArea>5e-13) result.Add(new(face,tri,domain));
                }
            }
        }
        return result;
    }
}
