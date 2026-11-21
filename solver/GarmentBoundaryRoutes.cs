using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

// Follow each verified source hem through the cut AF chart. Sample every cut
// triangle crossing so cloth and skin receive an identical edge subdivision.
internal static class GarmentBoundaryRoutes
{
    internal static List<SkinRoutes.ClothKnot> Trace(byte[] original,NativeSkinChart chart,
        List<GarmentSkin.Face> faces,List<Cut> cuts)
    {
        var output=new List<SkinRoutes.ClothKnot>();
        if(faces.Count==0) return output;
        var src=SecondSkinWriter.Parse(original);
        var joins=ClothSubdivision.IdentifyJoins(src);
        var owners=new Dictionary<(int,int,int),(int Start,int A,int B)>();
        foreach(int m in joins.Keys.Select(k=>k.Mesh).Distinct())
        {
            int h=src.MeshStart+36*m,first=BitConverter.ToInt32(original,h+16);
            for(int i=0;i<BitConverter.ToInt32(original,h+4);i+=3)
            {
                int[] v=Enumerable.Range(0,3).Select(k=>(int)BitConverter.ToUInt16(original,src.Ib+2*(first+i+k))).ToArray();
                for(int k=0;k<3;k++)
                {
                    int b=(k+1)%3,a=k;
                    if(v[a]>v[b]) (a,b)=(b,a);
                    var key=(m,v[a],v[b]);
                    if(joins.ContainsKey(key)) owners[key]=(i,a,b);
                }
            }
        }
        var borders=faces.SelectMany(f=>Enumerable.Range(0,3).Select(a=>
            (Key:(f.Mesh,Math.Min(f.Vertices[a],f.Vertices[(a+1)%3]),Math.Max(f.Vertices[a],f.Vertices[(a+1)%3])),Face:f)))
            .ToLookup(k=>k.Key,k=>k.Face);
        var triangles=cuts.ToLookup(c=>c.Domain);
        var corners=new Dictionary<(int Mesh,int Vertex),int>(); int variable=0;
        Point[] basis=[new(0,0),new(1,0),new(0,1)];
        foreach(var (key,rim) in joins)
        {
            var face=borders[(rim.Mesh,Math.Min(rim.A,rim.B),Math.Max(rim.A,rim.B))].Single();
            var uv=face.UV.Polygon;
            Point a=uv[Array.IndexOf(face.Vertices,rim.A)],b=uv[Array.IndexOf(face.Vertices,rim.B)];
            var spans=new List<IslandBoundary.Segment>();
            foreach(var cut in triangles[face.Domain])
            {
                var native=chart.Cutting[cut.Face];
                Point ToUv(Point x)=>native.A+x.X*(native.B-native.A)+x.Y*(native.C-native.A);
                var tri=new Triangle(ToUv(cut.Barycentric.A),ToUv(cut.Barycentric.B),ToUv(cut.Barycentric.C));
                if(Math.Abs(tri.SignedArea)<1e-16) continue;
                var p=tri.Barycentric(a);var q=tri.Barycentric(b);
                double lo=0,hi=1;
                foreach(var (start,end) in new[]{(1-p.X-p.Y,1-q.X-q.Y),(p.X,q.X),(p.Y,q.Y)})
                {
                    double s=Math.Abs(start)<1e-9?0:start,e=Math.Abs(end)<1e-9?0:end;
                    if(s<0 && e<0){ hi=-1;break; }
                    if(s<0) lo=Math.Max(lo,-s/(e-s));
                    if(e<0) hi=Math.Min(hi,-s/(e-s));
                }
                if(hi-lo>1e-9) spans.Add(new(lo,hi,cut.Face,native.Barycentric(a+lo*(b-a)),native.Barycentric(a+hi*(b-a))));
            }
            spans.Sort((x,y)=>x.Lo.CompareTo(y.Lo));
            double cursor=0;
            foreach(var span in spans.ToArray())
            {
                if(span.Lo>cursor+1e-7) spans.AddRange(IslandBoundary.Walk(chart,triangles[face.Domain],a+cursor*(b-a),a+span.Lo*(b-a),
                        cursor,span.Lo,3000*System.Numerics.Vector3.Distance(rim.Start,rim.End)+10));
                cursor=Math.Max(cursor,span.Hi);
            }
            if(cursor<1-1e-7) spans.AddRange(IslandBoundary.Walk(chart,triangles[face.Domain],a+cursor*(b-a),b,cursor,1,3000*System.Numerics.Vector3.Distance(rim.Start,rim.End)+10));
            var (first,ca,cb)=owners[key];double prior=-1;
            foreach(var u in spans.SelectMany(s=>new[]{s.Lo,s.Hi}).Append(0).Append(1).Order())
            {
                double t=u<1e-8?0:u>1-1e-8?1:u;
                if(t-prior<1e-8) continue;prior=t;
                var interval=spans.First(s=>s.Lo<=t+1e-7 && s.Hi>=t-1e-7);
                int index;
                if(t==0 || t==1)
                {
                    var endpoint=(key.Mesh,t==0?key.A:key.B);
                    if(!corners.TryGetValue(endpoint,out index)) corners[endpoint]=index=variable++;
                }
                else index=variable++;
                var bary=interval.From+Math.Clamp((t-interval.Lo)/(interval.Hi-interval.Lo),0,1)*(interval.To-interval.From);
                output.Add(new(key.Mesh,first,index,basis[ca]+t*(basis[cb]-basis[ca]),interval.Face,bary));
            }
        }
        return output;
    }
}
