using System.Numerics;

namespace Aetherform.Geometry;

// Clip in the plane of the cloth triangle while keeping barycentrics on the
// skin triangle. Each witness can be replayed on the original skin carrier.
internal static class FiniteClearance
{
    private sealed record Witness(int A,int B,int C,Vector3 Bary,Vector3 Normal,float Upper);
    private static List<Vector3> Clip(List<Vector3> input,float[] line)
    {
        var output=new List<Vector3>(6);
        for(int i=0;i<input.Count;i++)
        {
            var a=input[i];var b=input[(i+1)%input.Count];
            float Side(Vector3 p)=>p.X*line[0]+p.Y*line[1]+p.Z*line[2];
            var x=Side(a);var y=Side(b);
            if(x>=0) output.Add(a);
            if((x>=0)!=(y>=0)) output.Add(a+x/(x-y)*(b-a));
        }
        return output;
    }
    internal static void Solve(ClearanceSurface shell,int[] ids,IReadOnlyList<Vector3> native,
        Vector3[] field,Vector3[] carrier,bool[] pinned,Vector3[] outward)
    {
        var matches=new List<int>();
        var constraints=new List<Witness>();
        for(int scan=0;scan<8;scan++)
        {
            float initial=0;
            for(int f=0;f<ids.Length;f+=3)
            {
                int[] v=[ids[f],ids[f+1],ids[f+2]];
                if(v.All(i=>pinned[i])) continue;
                var p=v.Select(i=>native[i]+field[i]).ToArray();
                var old=v.Select(i=>carrier[i]).ToArray();
                var direction=outward[v[0]]+outward[v[1]]+outward[v[2]];
                var lo=Vector3.Min(p[0],Vector3.Min(p[1],p[2]));var hi=Vector3.Max(p[0],Vector3.Max(p[1],p[2]));
                foreach(var point in old){lo=Vector3.Min(lo,point);hi=Vector3.Max(hi,point);}
                shell.Overlap(lo,hi,matches);
                foreach(int other in matches)
                {
                    int j=3*other;Vector3[] cloth=[shell.Points[j],shell.Points[j+1],shell.Points[j+2]];
                    var normal=Vector3.Normalize(Vector3.Cross(cloth[1]-cloth[0],cloth[2]-cloth[0]));
                    if(Vector3.Dot(direction,normal)<0) normal=-normal;
                    var current=p.Select(q=>Vector3.Dot(normal,q-cloth[0])).ToArray();
                    var reference=old.Select(q=>Vector3.Dot(normal,q-cloth[0])).ToArray();
                    if(current.Max()<=.001f || reference.Min()>.001f) continue;
                    // Restrict both the proposed contact and its inherited
                    // allowance to the finite cloth face.
                    var poly=new List<Vector3>{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ};
                    var geometric=Vector3.Cross(cloth[1]-cloth[0],cloth[2]-cloth[0]);
                    for(int k=0;k<3 && poly.Count>0;k++)
                    {
                        var side=Vector3.Cross(geometric,cloth[(k+1)%3]-cloth[k]);
                        poly=Clip(poly,p.Select(q=>Vector3.Dot(side,q-cloth[k])).ToArray());
                        poly=Clip(poly,old.Select(q=>Vector3.Dot(side,q-cloth[k])).ToArray());
                    }
                    foreach(var b in poly)
                    {
                        float upper=Math.Max(0,Vector3.Dot(b,new(reference[0],reference[1],reference[2])));
                        float excess=Vector3.Dot(b,new(current[0],current[1],current[2]))-upper;
                        if(excess<=.001f) continue;
                        float denominator=0;
                        for(int k=0;k<3;k++) if(!pinned[v[k]]) denominator+=b[k]*b[k];
                        if(denominator<1e-5f) continue;
                        var point=b.X*native[v[0]]+b.Y*native[v[1]]+b.Z*native[v[2]];
                        constraints.Add(new(v[0],v[1],v[2],b,normal,upper-Vector3.Dot(point-cloth[0],normal)));
                        initial=Math.Max(initial,excess);
                    }
                }
            }
            if(initial<=.001f) break;
            for(int pass=0;pass<32;pass++)
            {
                float worst=0;
                foreach(var c in constraints)
                {
                    var b=c.Bary;float a=pinned[c.A]?0:b.X,d=pinned[c.B]?0:b.Y,e=pinned[c.C]?0:b.Z;
                    float violation=Vector3.Dot(b.X*field[c.A]+b.Y*field[c.B]+b.Z*field[c.C],c.Normal)-c.Upper;
                    if(violation<=.0005f) continue;
                    var correction=violation/(a*a+d*d+e*e)*c.Normal;
                    field[c.A]-=a*correction;field[c.B]-=d*correction;field[c.C]-=e*correction;
                    worst=Math.Max(worst,violation);
                }
                if(worst<=.001f) break;
            }
        }
    }
}
