using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class PanelGeometry
{
    // Relax only the displacement field. Boundary and crease vertices take their
    // neighbours along that line, so smoothing cannot round across an authored fold.
    internal static void Smooth(Vector3[] rest,Vector3[] fit,int[] faces)
    {
        var id=new Dictionary<Vector3,int>();
        var nodes=new int[rest.Length];
        var points=new List<Vector3>();
        for(int i=0;i<rest.Length;i++)
        {
            if(!id.TryGetValue(rest[i],out int j)) { id[rest[i]]=j=points.Count; points.Add(rest[i]); }
            nodes[i]=j;
        }
        var adjacent=points.Select(_=>new HashSet<int>()).ToArray();
        var crease=points.Select(_=>new HashSet<int>()).ToArray();
        var edges=new Dictionary<(int,int),(Vector3 Normal,int Count,bool Sharp)>();
        for(int i=0;i<faces.Length;i+=3)
        {
            int a=nodes[faces[i]],b=nodes[faces[i+1]],c=nodes[faces[i+2]];
            var n=Vector3.Cross(points[b]-points[a],points[c]-points[a]);
            if(n.LengthSquared()<1e-18f) continue;
            n=Vector3.Normalize(n);
            Edge(a,b,n); Edge(b,c,n); Edge(c,a,n);
        }
        void Edge(int a,int b,Vector3 n)
        {
            adjacent[a].Add(b); adjacent[b].Add(a);
            var key=(Math.Min(a,b),Math.Max(a,b));
            if(edges.TryGetValue(key,out var prev))
                edges[key]=(prev.Normal,prev.Count+1,prev.Sharp || Vector3.Dot(prev.Normal,n)<.65f);
            else edges[key]=(n,1,false);
        }
        foreach(var ((a,b),e) in edges)
            if(e.Count!=2 || e.Sharp) { crease[a].Add(b); crease[b].Add(a); }
        var delta=new Vector3[points.Count];
        var count=new int[points.Count];
        for(int i=0;i<rest.Length;i++) { delta[nodes[i]]+=fit[i]-rest[i]; count[nodes[i]]++; }
        for(int i=0;i<delta.Length;i++) delta[i]/=count[i];
        for(int pass=0;pass<5;pass++)
        {
            var next=(Vector3[])delta.Clone();
            for(int i=0;i<delta.Length;i++)
            {
                var ring=crease[i].Count>0?crease[i]:adjacent[i];
                if(ring.Count==0 || (crease[i].Count>0 && ring.Count!=2)) continue;
                Vector3 sum=default; float total=0;
                foreach(int j in ring)
                {
                    float weight=1/Math.Max(.001f,Vector3.Distance(points[i],points[j]));
                    sum+=delta[j]*weight; total+=weight;
                }
                next[i]=Vector3.Lerp(delta[i],sum/total,.6f);
            }
            delta=next;
        }
        foreach(int i in faces.Distinct()) fit[i]=rest[i]+delta[nodes[i]];
    }

    private static float Ramp(float lo,float hi,float value)
    {
        float t=Math.Clamp((value-lo)/(hi-lo),0,1);
        return t*t*(3-2*t);
    }

    internal static void TorsoReserve(Vector3[] fit,Vector3[] prior,int[] tri,BodySurface skin,bool top)
    {
        if(skin.IsEmpty) return;
        // In normalized female body space, reuse the first pass's outward reserve
        // over the ribcage/bust. This also protects spans between sparse skin islands.
        if(top)
        foreach(int i in tri.Distinct())
        {
            var p=fit[i];
            float area=Ramp(1.055f,1.085f,p.Y)*(1-Ramp(1.288f,1.338f,p.Y))
                *(1-Ramp(.118f,.174f,Math.Abs(p.X)))*Ramp(-.018f,.021f,p.Z);
            if(area>0 && Vector3.Dot(prior[i]-p,new(p.X,0,p.Z))>0)
                fit[i]=Vector3.Lerp(p,prior[i],area);
        }
    }
}
