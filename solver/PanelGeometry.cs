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

    // Cloth spanning the sternum carries its authored curvature between the
    // breasts. Sample displacement on either side of each point in the same
    // source layer, so embroidery and intentional folds keep their relief.
    internal static void BridgeChest(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> pinned)
    {
        const float span=.030f;
        var triangles=new Dictionary<(int,int),List<int>>();
        static int Cell(float value)=>(int)MathF.Floor(value/span);
        for(int j=0;j<faces.Length;j+=3)
        {
            var a=rest[faces[j]]; var b=rest[faces[j+1]]; var c=rest[faces[j+2]];
            var lo=Vector3.Min(a,Vector3.Min(b,c)); var hi=Vector3.Max(a,Vector3.Max(b,c));
            if(hi.Z<.04f || hi.Y<1.16f || lo.Y>1.35f || hi.X<-.12f || lo.X>.12f) continue;
            for(int x=Cell(lo.X);x<=Cell(hi.X);x++)
            for(int y=Cell(lo.Y);y<=Cell(hi.Y);y++)
            {
                if(!triangles.TryGetValue((x,y),out var list)) triangles[(x,y)]=list=[];
                list.Add(j);
            }
        }
        float? Offset(Vector3 p,float x)
        {
            if(!triangles.TryGetValue((Cell(x),Cell(p.Y)),out var list)) return null;
            float nearest=.018f; float? offset=null;
            foreach(int j in list)
            {
                int a=faces[j],b=faces[j+1],c=faces[j+2];
                var origin=rest[a]; var u=rest[b]-origin; var v=rest[c]-origin;
                float det=u.X*v.Y-u.Y*v.X;
                if(Math.Abs(det)<1e-10f) continue;
                float s=((x-origin.X)*v.Y-(p.Y-origin.Y)*v.X)/det;
                float t=((p.Y-origin.Y)*u.X-(x-origin.X)*u.Y)/det;
                if(s<0 || t<0 || s+t>1) continue;
                float z=origin.Z+s*u.Z+t*v.Z;
                float distance=Math.Abs(z-p.Z);
                if(distance>=nearest) continue;
                nearest=distance;
                offset=fit[a].Z+s*(fit[b].Z-fit[a].Z)+t*(fit[c].Z-fit[a].Z)-z;
            }
            return offset;
        }
        var points=faces.Distinct().Where(i=>!pinned.ContainsKey(i) && Math.Abs(rest[i].X)<.075f &&
            rest[i].Z>.045f && rest[i].Y>1.16f && rest[i].Y<1.35f).ToArray();
        var next=new float[points.Length];
        for(int pass=0;pass<4;pass++)
        {
            for(int j=0;j<points.Length;j++)
            {
                int i=points[j];var p=rest[i];next[j]=fit[i].Z;
                if(Offset(p,p.X-span) is not float left || Offset(p,p.X+span) is not float right) continue;
                float blend=(1-Ramp(.025f,.075f,Math.Abs(p.X)))*Ramp(1.16f,1.20f,p.Y)*(1-Ramp(1.31f,1.35f,p.Y));
                next[j]+=blend*Math.Max(0,p.Z+(left+right)*.5f-fit[i].Z);
            }
            for(int j=0;j<points.Length;j++) fit[points[j]].Z=next[j];
        }
    }

    // Carry the authored underside across the narrow space between the thighs.
    // Relax displacement on connected cloth; sewn edges and the surrounding
    // fit supply the boundary conditions for the bridge.
    internal static void BridgeGusset(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> pinned)
    {
        var groups=faces.Distinct().GroupBy(i=>Vector3.Round(rest[i]*100000)).ToArray();
        var node=new int[rest.Length];
        var centers=new Vector3[groups.Length];
        var moves=new Vector3[groups.Length];
        for(int n=0;n<groups.Length;n++)
        {
            int first=groups[n].First(); centers[n]=rest[first]; moves[n]=fit[first]-rest[first];
            foreach(int i in groups[n]) node[i]=n;
        }
        var rings=new Dictionary<int,HashSet<int>>();
        for(int j=0;j<faces.Length;j+=3)
        {
            int a=faces[j],b=faces[j+1],c=faces[j+2];
            var normal=Vector3.Cross(rest[b]-rest[a],rest[c]-rest[a]);
            if(normal.LengthSquared()<1e-18f || Math.Abs(normal.Y)<.40f*normal.Length()) continue;
            for(int k=0;k<3;k++)
            {
                int n=node[faces[j+k]]; var p=centers[n];
                if(Math.Abs(p.X)>.035f || p.Y<.80f || p.Y>.90f || p.Z<-.055f || p.Z>.095f ||
                   groups[n].Any(pinned.ContainsKey)) continue;
                if(!rings.TryGetValue(n,out var ring)) rings[n]=ring=[];
                ring.Add(node[faces[j+(k+1)%3]]); ring.Add(node[faces[j+(k+2)%3]]);
            }
        }
        var next=(Vector3[])moves.Clone();
        for(int pass=0;pass<48;pass++)
        {
            float change=0;
            foreach(var (n,ring) in rings)
            {
                float total=0; Vector3 sum=default;
                foreach(int k in ring)
                {
                    float weight=1/Math.Max(.0001f,Vector3.Distance(centers[n],centers[k]));
                    total+=weight; sum+=weight*moves[k];
                }
                next[n]=Vector3.Lerp(moves[n],sum/total,.8f);
                change=Math.Max(change,Vector3.DistanceSquared(next[n],moves[n]));
            }
            (moves,next)=(next,moves);
            if(change<1e-12f) break;
        }
        foreach(int n in rings.Keys)
            foreach(int i in groups[n]) fit[i]=rest[i]+moves[n];
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
