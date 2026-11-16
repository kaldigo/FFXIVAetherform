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
        BridgeLowerPanel(rest,fit,faces,pinned,false);
    }

    internal static void BridgeLowerPanel(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> anchors,bool front)
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
            if(normal.LengthSquared()<1e-18f || (front ? Math.Abs(normal.Z) : Math.Abs(normal.Y))<.40f*normal.Length()) continue;
            for(int k=0;k<3;k++)
            {
                int n=node[faces[j+k]]; var p=centers[n];
                bool within=front
                    ? Math.Abs(p.X)<.045f && p.Y>.86f && p.Y<.985f && p.Z>.035f && p.Z<.12f
                    : Math.Abs(p.X)<=.035f && p.Y>=.80f && p.Y<=.90f && p.Z>=-.055f && p.Z<=.095f;
                if(!within || groups[n].Any(anchors.ContainsKey)) continue;
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

    // Local clearance corrections can leave a ridge between newly inserted
    // vertices. Relax the displacement across the bust, retaining hard joins.
    internal static void RelaxBust(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> anchors,
        List<BodySurface> stock,List<BodySurface> targets)
    {
        var smooth=(Vector3[])fit.Clone();
        Smooth(rest,smooth,faces);
        foreach(int i in faces.Distinct())
        {
            var p=rest[i]; float x=Math.Abs(p.X);
            var blend=Ramp(.008f,.035f,x)*(1-Ramp(.105f,.135f,x))*
                Ramp(1.14f,1.18f,p.Y)*(1-Ramp(1.29f,1.34f,p.Y))*Ramp(.04f,.065f,p.Z);
            if(blend<=0 || anchors.ContainsKey(i)) continue;
            var candidate=Vector3.Lerp(fit[i],smooth[i],blend);
            // Restore any additional deficit introduced by relaxation. Existing
            // deficits were already handled by the triangle clearance pass.
            var oldPush=WrapGarment.Contact(p,fit[i],stock,targets,true)-fit[i];
            var newPush=WrapGarment.Contact(p,candidate,stock,targets,true)-candidate;
            float length=newPush.Length();
            fit[i]=length>oldPush.Length() ? candidate+newPush*(1-oldPush.Length()/length) : candidate;
        }
    }

    // Keep a hanging rear panel in its authored position when that position
    // already clears the selected body. Blend by local surface clearance,
    // so the tighter fabric approaching the waist can still follow the fit.
    internal static void RearDrape(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> anchors,
        List<BodySurface> targets)
    {
        Vector3 ClearBody(Vector3 point)
        {
            // Displace only where the seated panel would touch or enter the target.
            float distance=.035f;
            Vector3 push=default;
            foreach(var body in targets)
            {
                if(!body.Nearest(point,distance,out var hit)) continue;
                distance=hit.Distance;
                float gap=Vector3.Dot(point-hit.Point,hit.Normal);
                push=hit.Normal*Math.Max(0,.003f-gap);
            }
            return point+push;
        }
        var support=(Vector3[])fit.Clone();
        var weights=new Dictionary<int,float>();
        foreach(int i in faces.Distinct())
        {
            var p=rest[i];
            if(anchors.ContainsKey(i) || p.Y>=1.08f || p.Z>=-.025f) continue;
            // A fitted body can recede under the original drape. Its available room controls the transition.
            float clearance=GarmentDeformation.SourceDepth(p,targets);
            float loose=Ramp(.005f,.025f,clearance);
            float rear=Ramp(.025f,.075f,-p.Z)*(1-Ramp(.98f,1.08f,p.Y));
            fit[i]=Vector3.Lerp(fit[i],p,loose*rear);
            support[i]=fit[i];
            float weight=Ramp(.015f,.070f,-p.Z)*Ramp(.82f,.88f,p.Y)*(1-Ramp(1.00f,1.08f,p.Y));
            if(weight<=0) continue;
            // Start from the authored panel and carry only the motion needed
            // for body clearance. Spread that motion along the cloth topology.
            support[i]=Vector3.Lerp(fit[i],ClearBody(p),weight);
            weights[i]=weight;
        }
        Smooth(rest,support,faces);
        foreach(var (i,weight) in weights)
        {
            var candidate=Vector3.Lerp(fit[i],support[i],weight);
            fit[i]=ClearBody(candidate);
        }
    }

    // The support for a loose trim is the garment layer under it. Reuse that
    // layer's local displacement where the two are close in the original mesh.
    internal static void CarryTrim(Vector3[] rest,Vector3[] fit,int[] faces,Dictionary<int,Vector3> rim)
    {
        if(rim.Count==0) return;
        var parent=new Dictionary<Vector3,Vector3>();
        Vector3 Root(Vector3 p)
        {
            if(!parent.TryGetValue(p,out var next)) { parent.Add(p,p); return p; }
            while(next!=p) { parent[p]=parent[next]; p=next; next=parent[p]; }
            return p;
        }
        for(int j=0;j<faces.Length;j+=3)
        {
            var root=Root(rest[faces[j]]);
            parent[Root(rest[faces[j+1]])]=root;
            parent[Root(rest[faces[j+2]])]=root;
        }
        var sewn=rim.Keys.Select(i=>Root(rest[i])).ToHashSet();
        const float reach=.035f;
        static Vector3 Cell(Vector3 p)=>new(MathF.Floor(p.X/reach),MathF.Floor(p.Y/reach),MathF.Floor(p.Z/reach));
        var bins=new Dictionary<Vector3,List<int>>();
        for(int j=0;j<faces.Length;j+=3)
        {
            var a=rest[faces[j]]; var b=rest[faces[j+1]]; var c=rest[faces[j+2]];
            if(!sewn.Contains(Root(a))) continue;
            var lo=Cell(Vector3.Min(a,Vector3.Min(b,c))-new Vector3(reach));
            var hi=Cell(Vector3.Max(a,Vector3.Max(b,c))+new Vector3(reach));
            for(float x=lo.X;x<=hi.X;x++)
            for(float y=lo.Y;y<=hi.Y;y++)
            for(float z=lo.Z;z<=hi.Z;z++)
            {
                var key=new Vector3(x,y,z);
                if(!bins.TryGetValue(key,out var list)) bins[key]=list=[];
                list.Add(j);
            }
        }
        var trim=faces.Chunk(3).Where(t=>!sewn.Contains(Root(rest[t[0]]))).ToArray();
        bool Crossing(Vector3[] positions,int[] t,int j)
        {
            for(int k=0;k<3;k++)
            {
                if(Pierces(positions[faces[j]],positions[faces[j+1]],positions[faces[j+2]],positions[t[k]],positions[t[(k+1)%3]]) ||
                   Pierces(positions[t[0]],positions[t[1]],positions[t[2]],positions[faces[j+k]],positions[faces[j+(k+1)%3]])) return true;
            }
            return false;
        }
        var supports=new HashSet<Vector3>();
        foreach(var t in trim)
        {
            var center=(rest[t[0]]+rest[t[1]]+rest[t[2]])/3;
            if(!bins.TryGetValue(Cell(center),out var nearby)) continue;
            foreach(int j in nearby)
            {
                var support=Root(rest[faces[j]]);
                if(!supports.Contains(support) && Crossing(fit,t,j) && !Crossing(rest,t,j)) supports.Add(support);
            }
        }
        if(supports.Count==0) return;
        // Carry companion details (such as bows) with a corrected cuff, using
        // only the support layers that actually introduced an intersection.
        foreach(var list in bins.Values) list.RemoveAll(j=>!supports.Contains(Root(rest[faces[j]])));
        foreach(int i in faces.Distinct())
        {
            var p=rest[i];
            if(sewn.Contains(Root(p)) || !bins.TryGetValue(Cell(p),out var list)) continue;
            float nearest=reach*reach; Vector3 mapped=fit[i];
            foreach(int j in list)
            {
                int a=faces[j],b=faces[j+1],c=faces[j+2];
                if(p.X*rest[a].X<0 && p.X*rest[b].X<0 && p.X*rest[c].X<0) continue;
                var point=BrushTransfer.ClosestOnTriangle(p,rest[a],rest[b],rest[c],out float wa,out float wb,out float wc);
                float d=Vector3.DistanceSquared(p,point);
                if(d>=nearest || !float.IsFinite(d)) continue;
                nearest=d;
                mapped=p+wa*(fit[a]-rest[a])+wb*(fit[b]-rest[b])+wc*(fit[c]-rest[c]);
            }
            fit[i]=Vector3.Lerp(mapped,fit[i],Ramp(.018f,reach,MathF.Sqrt(nearest)));
        }
        for(int pass=0;pass<8;pass++)
        {
            var pushes=new Dictionary<Vector3,Vector3>();
            foreach(var t in trim)
            {
                var center=(rest[t[0]]+rest[t[1]]+rest[t[2]])/3;
                if(!bins.TryGetValue(Cell(center),out var nearby)) continue;
                var lo=Vector3.Min(fit[t[0]],Vector3.Min(fit[t[1]],fit[t[2]]));
                var hi=Vector3.Max(fit[t[0]],Vector3.Max(fit[t[1]],fit[t[2]]));
                foreach(int j in nearby)
                {
                    int a=faces[j],b=faces[j+1],c=faces[j+2];
                    var min=Vector3.Min(fit[a],Vector3.Min(fit[b],fit[c]));
                    var max=Vector3.Max(fit[a],Vector3.Max(fit[b],fit[c]));
                    if(max.X<lo.X || max.Y<lo.Y || max.Z<lo.Z || min.X>hi.X || min.Y>hi.Y || min.Z>hi.Z ||
                       !Crossing(fit,t,j) || Crossing(rest,t,j)) continue;
                    var normal=Vector3.Cross(fit[b]-fit[a],fit[c]-fit[a]);
                    if(normal.LengthSquared()<1e-18f) continue;
                    normal=Vector3.Normalize(normal);
                    var sourceNormal=Vector3.Cross(rest[b]-rest[a],rest[c]-rest[a]);
                    if(Vector3.Dot(center-rest[a],sourceNormal)<0) normal=-normal;
                    foreach(int i in t)
                    {
                        float distance=Vector3.Dot(fit[i]-fit[a],normal);
                        if(distance>=0) continue;
                        var push=normal*(.00005f-distance);
                        if(push.LengthSquared()>pushes.GetValueOrDefault(rest[i]).LengthSquared()) pushes[rest[i]]=push;
                    }
                }
            }
            if(pushes.Count==0) break;
            foreach(int i in trim.SelectMany(t=>t).Distinct())
                fit[i]+=pushes.GetValueOrDefault(rest[i]);
        }
    }

    // Test a segment against the interior of a face; coplanar contact and
    // shared endpoints are intentional and do not require a separation.
    private static bool Pierces(Vector3 a,Vector3 b,Vector3 c,Vector3 x,Vector3 y)
    {
        var u=b-a; var v=c-a; var segment=y-x;
        var h=Vector3.Cross(segment,v); float det=Vector3.Dot(u,h);
        if(Math.Abs(det)<1e-12f) return false;
        var s=x-a; float alpha=Vector3.Dot(s,h)/det;
        var q=Vector3.Cross(s,u); float beta=Vector3.Dot(segment,q)/det;
        float along=Vector3.Dot(v,q)/det;
        return alpha>0 && beta>0 && alpha+beta<1 && along>0 && along<1;
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
