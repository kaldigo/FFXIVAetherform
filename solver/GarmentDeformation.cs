using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

// All layers sample one continuous displacement in the garment's authored space.
// Bound changes between adjacent nodes so a clearance request cannot pull a
// single corner away from its neighbours or send two close layers opposite ways.
internal sealed class GarmentDeformation
{
    private const float Cell=.032f;
    internal sealed record Panel(Vector3[] Rest,Vector3[] Fit,int[] Tri,int[] ClearanceTri,Dictionary<int,Vector3> Seams);
    private sealed class Node
    {
        public Vector3 Move, Push;
        public float Weight;
        public bool Fixed;
    }
    private readonly Dictionary<(int X,int Y,int Z),Node> nodes=[];
    private readonly List<(Node A,Node B)> neighbours=[];
    private readonly record struct Sample(Node[] Nodes,float[] Weights)
    {
        public Vector3 Move()
        {
            Vector3 sum=default;
            for(int i=0;i<8;i++) sum+=Nodes[i].Move*Weights[i];
            return sum;
        }
    }

    private readonly Dictionary<Vector3,Sample> sampled=[];
    private Sample At(Vector3 point)
    {
        if(sampled.TryGetValue(point,out var found)) return found;
        var v=point/Cell;
        int x=(int)MathF.Floor(v.X),y=(int)MathF.Floor(v.Y),z=(int)MathF.Floor(v.Z);
        var f=v-new Vector3(x,y,z);
        var sample=new Sample(new Node[8],new float[8]);
        for(int i=0;i<8;i++)
        {
            int dx=i&1,dy=(i>>1)&1,dz=i>>2;
            var key=(x+dx,y+dy,z+dz);
            if(!nodes.TryGetValue(key,out var node)) nodes.Add(key,node=new());
            sample.Nodes[i]=node;
            sample.Weights[i]=(dx==0?1-f.X:f.X)*(dy==0?1-f.Y:f.Y)*(dz==0?1-f.Z:f.Z);
        }
        sampled.Add(point,sample);
        return sample;
    }

    internal static float SourceDepth(Vector3 p,List<BodySurface> bodies)
    {
        float best=.15f,depth=float.PositiveInfinity;
        foreach(var body in bodies)
        {
            if(!body.Nearest(p,best,out var hit)) continue;
            best=hit.Distance;
            depth=Vector3.Dot(p-hit.Point,hit.Normal);
        }
        return depth;
    }

    internal void Apply(List<Panel> panels,IReadOnlyList<(Vector3 A,Vector3 B)> waist,BodySurface? skin,List<BodySurface> stockBody)
    {
        if(panels.Count==0) return;
        foreach(var panel in panels)
        foreach(int i in panel.Tri.Distinct())
        {
            var p=panel.Rest[i]; var delta=panel.Fit[i]-p;
            var s=At(p);
            for(int j=0;j<8;j++) { s.Nodes[j].Move+=s.Weights[j]*delta; s.Nodes[j].Weight+=s.Weights[j]; }
        }
        foreach(var n in nodes.Values) if(n.Weight>0) n.Move/=n.Weight;
        // Only pin a body join where this garment actually contains an authored
        // attachment. A wide skirt passing the same height is not attached there.
        foreach(var panel in panels)
        foreach(int i in panel.Tri.Distinct())
        {
            var p=panel.Rest[i];
            foreach(var (a,b) in waist)
            {
                var ab=b-a; var q=a+ab*Math.Clamp(Vector3.Dot(p-a,ab)/ab.LengthSquared(),0,1);
                if(Vector3.DistanceSquared(p,q)>1e-8f) continue;
                panel.Seams[i]=q;
                var s=At(p);
                for(int j=0;j<8;j++) if(s.Weights[j]>1e-6f)
                { s.Nodes[j].Move=q-p; s.Nodes[j].Fixed=true; }
                break;
            }
        }
        foreach(var ((x,y,z),n) in nodes)
        foreach(var k in new[]{(x+1,y,z),(x,y+1,z),(x,y,z+1)})
            if(nodes.TryGetValue(k,out var other)) neighbours.Add((n,other));
        LimitStrain();
        // Correct the shared field, rather than applying independent pushes to
        // the panel that happened to encounter skin. Include triangle interiors.
        var depths=new Dictionary<Vector3,float>();
        for(int pass=0;pass<8 && skin is { IsEmpty:false };pass++)
        {
            bool changed=false;
            foreach(var panel in panels)
            {
                Vector3 Point(int i)=>panel.Seams.TryGetValue(i,out var pinned)?pinned:panel.Rest[i]+At(panel.Rest[i]).Move();
                void Probe(Vector3 p,Vector3 source,int a,int b,int c)
                {
                    if(!skin.Nearest(p,.025f,out var hit)) return;
                    var diff=p-hit.Point; float d=Vector3.Dot(diff,hit.Normal);
                    if(d>=.004f || d<-.023f || (diff-d*hit.Normal).LengthSquared()>.000025f) return;
                    if(!depths.TryGetValue(source,out float original))
                        depths[source]=original=SourceDepth(source,stockBody);
                    if(original<-.004f) return;
                    var push=hit.Normal*(.0043f-d);
                    void Scatter(int i)
                    {
                        var s=At(panel.Rest[i]);
                        for(int j=0;j<8;j++)
                        {
                            var n=s.Nodes[j];
                            if(!n.Fixed && s.Weights[j]>.015f && n.Push.LengthSquared()<push.LengthSquared())
                            { n.Push=push; changed=true; }
                        }
                    }
                    Scatter(a); if(a!=b) Scatter(b); if(c!=a && c!=b) Scatter(c);
                }
                foreach(int i in panel.ClearanceTri.Distinct()) Probe(Point(i),panel.Rest[i],i,i,i);
                for(int j=0;j<panel.ClearanceTri.Length;j+=3)
                {
                    int a=panel.ClearanceTri[j],b=panel.ClearanceTri[j+1],c=panel.ClearanceTri[j+2];
                    var x=Point(a);var y=Point(b);var z=Point(c);
                    var u=panel.Rest[a];var v=panel.Rest[b];var w=panel.Rest[c];
                    Probe((x+y+z)/3,(u+v+w)/3,a,b,c);
                    Probe((x+y)/2,(u+v)/2,a,b,a); Probe((y+z)/2,(v+w)/2,b,c,b); Probe((z+x)/2,(w+u)/2,c,a,c);
                    float span=MathF.Max(Vector3.Distance(x,y),MathF.Max(Vector3.Distance(y,z),Vector3.Distance(z,x)));
                    int divisions=Math.Clamp((int)MathF.Ceiling(span/.009f),3,12);
                    for(int s=1;s<divisions;s++)
                    for(int t=1;t<divisions-s;t++)
                    {
                        float l=(float)s/divisions, r=(float)t/divisions;
                        Probe(x+(y-x)*l+(z-x)*r,u+(v-u)*l+(w-u)*r,a,b,c);
                    }
                }
            }
            if(!changed) break;
            foreach(var n in nodes.Values) { n.Move+=n.Push; n.Push=default; }
            LimitStrain();
        }
        foreach(var panel in panels)
        foreach(int i in panel.Tri.Distinct())
            panel.Fit[i]=panel.Seams.TryGetValue(i,out var pin)?pin:panel.Rest[i]+At(panel.Rest[i]).Move();
    }

    private void LimitStrain()
    {
        const float maxChange=.40f*Cell;
        for(int pass=0;pass<24;pass++)
        {
            float worst=0;
            foreach(var (a,b) in neighbours)
            {
                var diff=b.Move-a.Move; float len=diff.Length();
                if(len<=maxChange || a.Fixed && b.Fixed) continue;
                float excess=len-maxChange; worst=Math.Max(worst,excess);
                var step=diff*(excess/len);
                if(!a.Fixed) a.Move+=b.Fixed?step:step*.5f;
                if(!b.Fixed) b.Move-=a.Fixed?step:step*.5f;
            }
            if(worst<.00001f) break;
        }
    }
}
