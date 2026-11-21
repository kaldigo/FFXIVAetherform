using System.Numerics;

namespace Aetherform.Geometry;

internal sealed class BodyDensity
{
    private readonly record struct Site(Vector3 Point,float Area);
    private sealed record Node(Site Site,int Axis,Node? Left,Node? Right);
    private readonly Node root;
    internal BodyDensity(IEnumerable<byte[]> targets)
    {
        var sites=new List<Site>();
        foreach(var body in targets)
        {
            var mesh=new NativeSkinChart(body);var p=mesh.PositionsMm;
            for(int k=0;k<p.Length;k+=3)
            {
                float area=Vector3.Cross(p[k+1]-p[k],p[k+2]-p[k]).Length()*.5f;
                if(area>1e-6f) sites.Add(new((p[k]+p[k+1]+p[k+2])/3,area));
            }
        }
        var arr=sites.ToArray();root=Build(0,arr.Length,0)!;
        Node? Build(int start,int count,int axis)
        {
            if(count==0) return null;
            Array.Sort(arr,start,count,Comparer<Site>.Create((a,b)=>a.Point[axis].CompareTo(b.Point[axis])));
            int n=count/2,next=(axis+1)%3;
            return new(arr[start+n],axis,Build(start,n,next),Build(start+n+1,count-1-n,next));
        }
    }
    internal float Target(Vector3 center)
    {
        float[] dist=new float[12],areas=new float[12];Array.Fill(dist,float.PositiveInfinity);
        void Visit(Node? n)
        {
            if(n==null) return;
            float d=Vector3.DistanceSquared(center,n.Site.Point);
            if(d<dist[^1])
            {
                int i=11;while(i>0 && dist[i-1]>d){dist[i]=dist[i-1];areas[i]=areas[i-1];i--;}
                dist[i]=d;areas[i]=n.Site.Area;
            }
            float delta=center[n.Axis]-n.Site.Point[n.Axis];bool left=delta<0;
            Visit(left?n.Left:n.Right);
            if(delta*delta<=dist[^1]) Visit(left?n.Right:n.Left);
        }
        Visit(root);Array.Sort(areas);return .5f*(areas[5]+areas[6]);
    }
}
