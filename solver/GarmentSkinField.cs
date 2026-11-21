using System.Numerics;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

// Sample the authored offset (for example the stocking indentation) in the
// registered chart. Only the offset is interpolated across coarse source faces.
internal sealed class GarmentSkinField
{
    private sealed record Patch(GarmentSkin.Face Face,Vector3[] Offset);
    private readonly Dictionary<(int Domain,int X,int Y),List<Patch>> buckets=[];
    private readonly NativeSkinChart target;
    private const int Scale=64;
    private static int Cell(double x)=>(int)Math.Floor(x*Scale);

    internal GarmentSkinField(NativeSkinChart target,IReadOnlyList<GarmentSkin.Face> faces)
    {
        this.target=target;
        foreach(var f in faces)
        {
            if(Math.Abs(f.UV.SignedArea)<1e-13) continue;
            var d=new Vector3[3];
            for(int i=0;i<3;i++)
            {
                var address=f.Address[i];
                int k=3*address.Face;
                d[i]=f.Position[i]-Interpolate(target.PositionsMm[k],target.PositionsMm[k+1],target.PositionsMm[k+2],address.Barycentric);
            }
            var patch=new Patch(f,d);var uv=f.UV.Polygon;
            for(int x=Cell(uv.Min(p=>p.X)-1e-6);x<=Cell(uv.Max(p=>p.X)+1e-6);x++)
            for(int y=Cell(uv.Min(p=>p.Y)-1e-6);y<=Cell(uv.Max(p=>p.Y)+1e-6);y++)
            {
                var key=(f.Domain,x,y);
                if(!buckets.TryGetValue(key,out var list)) buckets.Add(key,list=[]);
                list.Add(patch);
            }
        }
    }

    internal readonly record struct Sample(GarmentSkin.Face Source,Point Barycentric,Vector3 OffsetMm);

    internal Sample At(Cut cut,Point p)
    {
        var chart=target.Cutting[cut.Face];
        var uv=chart.A+p.X*(chart.B-chart.A)+p.Y*(chart.C-chart.A);
        if(!buckets.TryGetValue((cut.Domain,Cell(uv.X),Cell(uv.Y)),out var candidates))
            throw new InvalidDataException("AF cut outside source garment skin.");
        double best=double.NegativeInfinity;
        Point bc=default;
        Patch? selected=null;
        foreach(var patch in candidates)
        {
            var b=patch.Face.UV.Barycentric(uv);
            double score=Math.Min(1-b.X-b.Y,Math.Min(b.X,b.Y));
            if(score<=best) continue;
            best=score;selected=patch;bc=b;
        }
        if(selected==null) throw new InvalidDataException("Empty garment skin domain.");
        if(best < -2e-6)
        {
            double distance2=double.PositiveInfinity;
            Point original=default;
            Point[] basis=[new(0,0),new(1,0),new(0,1)];
            foreach(var patch in candidates)
            {
                var tri=patch.Face.UV.Polygon;
                for(int e=0;e<3;e++)
                {
                    int next=(e+1)%3;
                    var edge=tri[next]-tri[e];
                    var d=uv-tri[e];
                    double t=Math.Clamp((d.X*edge.X+d.Y*edge.Y)/(edge.X*edge.X+edge.Y*edge.Y),0,1);
                    var error=d-t*edge;
                    double dist=error.X*error.X+error.Y*error.Y;
                    if(dist>=distance2) continue;
                    distance2=dist;selected=patch;
                    original=patch.Face.UV.Barycentric(uv);
                    bc=basis[e]+t*(basis[next]-basis[e]);
                }
            }
            var v=selected.Face.Position;
            float gap=Vector3.Distance(Interpolate(v[0],v[1],v[2],original),Interpolate(v[0],v[1],v[2],bc));
            if(gap>.005f)
                throw new InvalidDataException($"AF face {cut.Face}, draw domain {cut.Domain} exceeds its source coverage by {gap:F6} mm.");
        }
        var delta=selected.Offset;
        return new(selected.Face,bc,Interpolate(delta[0],delta[1],delta[2],bc));
    }

    private static Vector3 Interpolate(Vector3 a,Vector3 b,Vector3 c,Point p)=>
        a+(float)p.X*(b-a)+(float)p.Y*(c-a);
}
