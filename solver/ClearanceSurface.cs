using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

// Exact nearest-triangle queries. All coordinates and distances are in mm.
internal sealed class ClearanceSurface
{
    internal readonly record struct Hit(int Face,Vector3 Point, Vector3 Normal,float Distance);
    private sealed record Node(Vector3 Lo,Vector3 Hi,int Start,int Count,Node? Left,Node? Right);
    internal readonly Vector3[] Points;
    private readonly int[] order;
    private readonly Node root;
    internal ClearanceSurface(Vector3[] triangles)
    {
        Points=triangles;
        order=Enumerable.Range(0,triangles.Length/3).Where(i=>Vector3.Cross(triangles[3*i+1]-triangles[3*i],triangles[3*i+2]-triangles[3*i]).LengthSquared()>1e-16f).ToArray();
        if(order.Length==0) throw new InvalidDataException("Empty clearance surface.");
        root=Build(0,order.Length);
    }
    private Node Build(int start,int count)
    {
        var lo=new Vector3(float.PositiveInfinity);var hi=-lo;
        for(int j=start;j<start+count;j++)
            for(int k=0;k<3;k++){var p=Points[3*order[j]+k];lo=Vector3.Min(lo,p);hi=Vector3.Max(hi,p);}
        if(count<=16) return new(lo,hi,start,count,null,null);
        var d=hi-lo;int axis=d.X>d.Y?(d.X>d.Z?0:2):(d.Y>d.Z?1:2);
        float Center(int f)=>Points[3*f][axis]+Points[3*f+1][axis]+Points[3*f+2][axis];
        Array.Sort(order,start,count,Comparer<int>.Create((a,b)=>Center(a).CompareTo(Center(b))));
        int half=count/2;
        return new(lo,hi,start,count,Build(start,half),Build(start+half,count-half));
    }
    internal void Overlap(Vector3 lo,Vector3 hi,List<int> matches)
    {
        matches.Clear();
        void Visit(Node n)
        {
            if(n.Lo.X>hi.X || n.Lo.Y>hi.Y || n.Lo.Z>hi.Z || n.Hi.X<lo.X || n.Hi.Y<lo.Y || n.Hi.Z<lo.Z) return;
            if(n.Left!=null){Visit(n.Left);Visit(n.Right!);return;}
            for(int j=n.Start;j<n.Start+n.Count;j++)
            {
                int f=order[j],i=3*f;var a=Points[i];var b=Points[i+1];var c=Points[i+2];
                var low=Vector3.Min(a,Vector3.Min(b,c));var high=Vector3.Max(a,Vector3.Max(b,c));
                if(low.X<=hi.X && low.Y<=hi.Y && low.Z<=hi.Z && high.X>=lo.X && high.Y>=lo.Y && high.Z>=lo.Z) matches.Add(f);
            }
        }
        Visit(root);
    }
    internal bool Ray(Vector3 origin,Vector3 direction,out Vector3 outward,float maximumDistance=float.PositiveInfinity)
    {
        double best=maximumDistance;Vector3 normal=default;
        bool Intersects(Node n)
        {
            double near=-1e-6,far=best;
            for(int k=0;k<3;k++)
            {
                double v=direction[k];
                if(Math.Abs(v)<1e-12){if(origin[k]<n.Lo[k] || origin[k]>n.Hi[k]) return false;continue;}
                double a=(n.Lo[k]-origin[k])/v,b=(n.Hi[k]-origin[k])/v;
                if(a>b) (a,b)=(b,a);near=Math.Max(near,a);far=Math.Min(far,b);
            }
            return near<=far;
        }
        void Visit(Node n)
        {
            if(!Intersects(n)) return;
            if(n.Left!=null){Visit(n.Left);Visit(n.Right!);return;}
            for(int j=n.Start;j<n.Start+n.Count;j++)
            {
                int t=3*order[j];var a=Points[t];var e=Points[t+1]-a;var f=Points[t+2]-a;
                var h=Vector3.Cross(direction,f);double det=Vector3.Dot(e,h);
                if(det >= -1e-12) continue;
                var s=origin-a;double u=Vector3.Dot(s,h)/det;
                if(u < -1e-9 || u>1+1e-9) continue;
                var q=Vector3.Cross(s,e);double v=Vector3.Dot(direction,q)/det;
                if(v < -1e-9 || u+v>1+1e-9) continue;
                double distance=Vector3.Dot(f,q)/det;
                if(distance < -1e-6 || distance>=best) continue;
                best=Math.Max(0,distance);normal=Vector3.Normalize(Vector3.Cross(e,f));
            }
        }
        Visit(root);outward=normal;return normal.LengthSquared()>0;
    }
    internal Hit Nearest(Vector3 point)
    {
        float best=float.PositiveInfinity;int face=-1;Vector3 hit=default;
        float Lower(Node n)=>Vector3.DistanceSquared(point,Vector3.Clamp(point,n.Lo,n.Hi));
        void Visit(Node n)
        {
            if(Lower(n)>best) return;
            if(n.Left!=null)
            {
                var a=n.Left;var b=n.Right!;
                if(Lower(b)<Lower(a)) (a,b)=(b,a);
                Visit(a);Visit(b);return;
            }
            for(int j=n.Start;j<n.Start+n.Count;j++)
            {
                int i=order[j],t=3*i;
                var p=BrushTransfer.ClosestOnTriangle(point,Points[t],Points[t+1],Points[t+2],out _,out _,out _);
                var dist=Vector3.DistanceSquared(p,point);
                if(dist<best){best=dist;face=i;hit=p;}
            }
        }
        Visit(root);int k=3*face;
        return new(face,hit,Vector3.Normalize(Vector3.Cross(Points[k+1]-Points[k],Points[k+2]-Points[k])),MathF.Sqrt(best));
    }
}
