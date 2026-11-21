using System.Numerics;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class IslandBoundary
{
    internal sealed record Segment(double Lo,double Hi,int Face,Point From,Point To);
    private sealed record Edge(int A,int B,int Face,Point P,Point Q,double Length);

    // Walk the existing cut perimeter. A connection exists only between
    // coincident UV and physical endpoints on this draw domain.
    internal static List<Segment> Walk(NativeSkinChart chart,IEnumerable<Cut> cuts, Point start,Point end,double lo,double hi,double maxMm)
    {
        var nodes=new Dictionary<(long,long,long,long,long),int>();
        var coordinates=new List<Point>();var xyz=new List<Vector3>();
        var occurrences=new Dictionary<(int,int),List<Edge>>();
        int Node(int f,Point b)
        {
            var tri=chart.Cutting[f];var uv=tri.A+b.X*(tri.B-tri.A)+b.Y*(tri.C-tri.A);
            var p=chart.PositionsMm[3*f]+(float)b.X*(chart.PositionsMm[3*f+1]-chart.PositionsMm[3*f])+(float)b.Y*(chart.PositionsMm[3*f+2]-chart.PositionsMm[3*f]);
            long Q(double v,double scale)=>(long)Math.Round(v*scale);
            var key=(Q(uv.X,1e9),Q(uv.Y,1e9),Q(p.X,1e3),Q(p.Y,1e3),Q(p.Z,1e3));
            if(!nodes.TryGetValue(key,out int id)){id=nodes.Count;nodes.Add(key,id);coordinates.Add(uv);xyz.Add(p);}
            return id;
        }
        foreach(var cut in cuts)
        {
            var p=cut.Barycentric.Polygon;var id=p.Select(b=>Node(cut.Face,b)).ToArray();
            for(int i=0;i<3;i++)
            {
                int j=(i+1)%3,a=id[i],b=id[j];var key=(Math.Min(a,b),Math.Max(a,b));
                if(a==b) continue;
                if(!occurrences.TryGetValue(key,out var es)) occurrences.Add(key,es=[]);
                es.Add(new(a,b,cut.Face,p[i],p[j],Vector3.Distance(xyz[a],xyz[b])));
            }
        }
        var graph=new Dictionary<int,List<Edge>>();int left=-1,right=-2;
        void Link(int a,int b,Edge e,double from,double to)
        {
            var x=e.P+from*(e.Q-e.P);var y=e.P+to*(e.Q-e.P);
            void Put(Edge v){if(!graph.TryGetValue(v.A,out var set)) graph[v.A]=set=[];set.Add(v);}
            double length=e.Length*Math.Abs(from-to);
            Put(new(a,b,e.Face,x,y,length));Put(new(b,a,e.Face,y,x,length));
        }
        foreach(var list in occurrences.Values.Where(v=>v.Count==1))
        {
            var e=list[0];Link(e.A,e.B,e,0,1);
            var a=coordinates[e.A];var d=coordinates[e.B]-a;var den=d.X*d.X+d.Y*d.Y;
            if(den<=1e-24) continue;
            double? Parameter(Point p)
            {
                var v=p-a;double t=Math.Clamp((v.X*d.X+v.Y*d.Y)/den,0,1);var r=v-t*d;
                return r.X*r.X+r.Y*r.Y<4e-12?t:null;
            }
            var u=Parameter(start);var v=Parameter(end);
            if(u.HasValue){Link(left,e.A,e,u.Value,0);Link(left,e.B,e,u.Value,1);}
            if(v.HasValue){Link(right,e.A,e,v.Value,0);Link(right,e.B,e,v.Value,1);}
            if(u.HasValue && v.HasValue) Link(left,right,e,u.Value,v.Value);
        }
        var queue=new PriorityQueue<int,double>();queue.Enqueue(left,0);
        var dist=new Dictionary<int,double>{{left,0}};var prev=new Dictionary<int,Edge>();
        while(queue.TryDequeue(out int node,out double cost))
        {
            if(cost!=dist[node]) continue;
            if(node==right) break;
            if(!graph.TryGetValue(node,out var neighbours)) continue;
            foreach(var edge in neighbours)
            {
                double next=cost+edge.Length;
                if(next>=dist.GetValueOrDefault(edge.B,double.PositiveInfinity) || next>maxMm) continue;
                dist[edge.B]=next;prev[edge.B]=edge;queue.Enqueue(edge.B,next);
            }
        }
        if(!dist.TryGetValue(right,out double total) || total<=1e-12) throw new SkinRegistrationException("No source-local continuous AF boundary path for the garment hem.");
        var path=new Stack<Edge>();int current=right;
        while(current!=left){var e=prev[current];path.Push(e);current=e.A;}
        double cursor=lo;var result=new List<Segment>();
        foreach(var e in path.Where(e=>e.Length>1e-9))
        {
            double finish=cursor+(hi-lo)*e.Length/total;
            result.Add(new(cursor,finish,e.Face,e.P,e.Q));cursor=finish;
        }
        return result;
    }
}
