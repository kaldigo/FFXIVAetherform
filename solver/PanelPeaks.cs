using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

// Round only the extra curvature introduced by fitting. All distances are millimetres.
internal static class PanelPeaks
{
    internal static void Apply(byte[] source,byte[] morph,byte[] fit,GarmentClassification? classification,
        ClearanceSurface body,Dictionary<int,ClothSubdivision.Origins> origins)
    {
        var original=SecondSkinWriter.Parse(source);var mapped=SecondSkinWriter.Parse(morph);var target=SecondSkinWriter.Parse(fit);
        var skinRims=new Lazy<(Vector3,Vector3)[]>(()=>SkinSeamCoverage.SlotEdges(source));
        foreach(var (mesh,lineage) in origins)
        {
            var a=new GarmentClearance.Mesh(original,mesh);var b=new GarmentClearance.Mesh(mapped,mesh);
            var draw=new GarmentClearance.Mesh(target,mesh);
            var nodes=new Dictionary<(Vector3,Vector3),int>();
            var id=a.P.Select((p,i)=>{
                var key=(p,b.P[i]);if(!nodes.TryGetValue(key,out int v)) nodes.Add(key,v=nodes.Count);return v;
            }).ToArray();
            int count=nodes.Count;
            var ring=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
            var rest=new Vector3[count];var dst=new Vector3[count];var na=new Vector3[count];var nb=new Vector3[count];
            var locked=new bool[count];var edges=new Dictionary<(int,int),(int Count,Vector3 Normal)>();
            var eligible=new bool[a.Indices.Length/3];
            for(int f=0;f<eligible.Length;f++)
            {
                var idx=a.Indices.AsSpan(3*f,3);int i=idx[0],j=idx[1],k=idx[2];
                var n=Vector3.Cross(a.P[j]-a.P[i],a.P[k]-a.P[i]);var m=Vector3.Cross(b.P[j]-b.P[i],b.P[k]-b.P[i]);
                if(n.LengthSquared()<1e-8f || m.LengthSquared()<1e-8f) continue;
                var normal=Vector3.Normalize(n);
                bool? close=classification?.ClosePanel(mesh-original.Lod0MeshIndex,f);
                eligible[f]=close ?? FollowsBody(a.P[i],a.P[j],a.P[k],normal,body);
                for(int l=0;l<3;l++)
                {
                    int u=idx[l],v=id[u],w=id[idx[(l+1)%3]];rest[v]=a.P[u];dst[v]=b.P[u];
                    na[v]+=n;nb[v]+=m;ring[v].Add(w);ring[w].Add(v);
                    if(!eligible[f] || a.AmbiguousShapeBases.Contains(u)) locked[v]=true;
                    var edge=(Math.Min(v,w),Math.Max(v,w));
                    if(edges.TryGetValue(edge,out var prior))
                    {
                        if(prior.Count>1 || Vector3.Dot(normal,prior.Normal)<.72f) locked[v]=locked[w]=true;
                        edges[edge]=(prior.Count+1,prior.Normal);
                    }
                    else edges[edge]=(1,normal);
                }
            }
            foreach(var (edge,info) in edges) if(info.Count!=2) locked[edge.Item1]=locked[edge.Item2]=true;
            var strength=new float[count];var retreat=new float[count];
            for(int v=0;v<count;v++)
            {
                if(na[v].LengthSquared()<1e-8f || nb[v].LengthSquared()<1e-8f) continue;
                na[v]=Vector3.Normalize(na[v]);nb[v]=Vector3.Normalize(nb[v]);
                if(locked[v] || ring[v].Count<3) continue;
                // Opposed samples distinguish an isolated peak from tilt or a linear ridge.
                var deltas=new List<float>();
                foreach(int i in ring[v])
                {
                    var x=rest[i]-rest[v];var axis=x-Vector3.Dot(x,na[v])*na[v];float l=axis.Length();
                    if(l<1 || l>55) continue;
                    int partner=-1;float best=-.45f,otherLength=0;
                    foreach(int j in ring[v])
                    {
                        var y=rest[j]-rest[v];y-=Vector3.Dot(y,na[v])*na[v];float r=y.Length();
                        if(r<1 || r>55) continue;
                        float cos=Vector3.Dot(axis,y)/(l*r);if(cos<best){best=cos;partner=j;otherLength=r;}
                    }
                    if(partner<0)continue;
                    float Bend(Vector3[] p,Vector3 normal)=>Vector3.Dot(p[v]-(otherLength*p[i]+l*p[partner])/(otherLength+l),normal);
                    deltas.Add((Bend(dst,nb[v])-Bend(rest,na[v]))*20/Math.Max(10,(l+otherLength)*.5f));
                }
                if(deltas.Count<3)continue;
                deltas.Sort();float extra=deltas[deltas.Count/4];
                strength[v]=Math.Clamp((extra-.35f)/1.6f,0,1);
                retreat[v]=Math.Clamp(extra*.35f,0,2.3f)*strength[v];
            }
            var moves=new Dictionary<int,Vector3>();var pinned=new HashSet<int>();
            for(int f=0;f<lineage.Parents.Length;f++)
            {
                int parent=lineage.Parents[f];var ids=a.Indices.AsSpan(3*parent,3);
                int i=id[ids[0]],j=id[ids[1]],k=id[ids[2]];
                for(int c=0;c<3;c++)
                {
                    int v=draw.Indices[3*f+c];if(!eligible[parent] || draw.AmbiguousShapeBases.Contains(v)){pinned.Add(v);continue;}
                    if(moves.ContainsKey(v) || Math.Max(strength[i],Math.Max(strength[j],strength[k]))<=0)continue;
                    var p=lineage.Positions[v]*1000;
                    BrushTransfer.ClosestOnTriangle(p,rest[i],rest[j],rest[k],out float x,out float y,out float z);
                    if((x>.999f && locked[i]) || (y>.999f && locked[j]) || (z>.999f && locked[k])){pinned.Add(v);continue;}
                    float mask=x*(locked[i]?0:1)+y*(locked[j]?0:1)+z*(locked[k]?0:1);
                    // Boundaries retain the authored line even when an adjacent panel is eligible.
                    if((x<.001f&&locked[j]&&locked[k])||(y<.001f&&locked[k]&&locked[i])||(z<.001f&&locked[i]&&locked[j])){pinned.Add(v);continue;}
                    var normal=Vector3.Normalize(x*nb[i]+y*nb[j]+z*nb[k]);
                    if(!float.IsFinite(normal.X))continue;
                    // Cubic triangular patch with tangent-plane edge controls. Subtract the same
                    // lift on the source so authored curvature survives. Edge values are shared.
                    float Curve(Vector3[] q,Vector3[] n,Vector3 direction)
                    {
                        float Edge(int v0,int v1)=>-Vector3.Dot(q[v1]-q[v0],n[v0])*Vector3.Dot(n[v0],direction);
                        float ab=Edge(i,j),ac=Edge(i,k),ba=Edge(j,i),bc=Edge(j,k),ca=Edge(k,i),cb=Edge(k,j);
                        return x*x*(y*ab+z*ac)+y*y*(x*ba+z*bc)+z*z*(x*ca+y*cb)+.5f*x*y*z*(ab+ac+ba+bc+ca+cb);
                    }
                    float activation=x*strength[i]+y*strength[j]+z*strength[k];
                    float added=Curve(dst,nb,normal)-Curve(rest,na,Vector3.Normalize(x*na[i]+y*na[j]+z*na[k]));
                    float change=mask*activation*Math.Clamp(added,-4,4)-(x*retreat[i]+y*retreat[j]+z*retreat[k]);
                    if(float.IsFinite(change)) moves[v]=change*normal;
                }
            }
            foreach(int v in pinned)moves.Remove(v);
            BustPanelDrape.Apply(original,mesh,a,draw,lineage,id,rest,dst,eligible,strength,moves,skinRims);
            var shading=new Dictionary<int,(Vector3 Normal,float Blend)>();
            BustCurve.Apply(original,mapped,mesh,a,draw,lineage.Parents,id,dst,nb,strength,eligible,ring,pinned,moves,shading);
            ModelPanelEdits.Write(fit,target,mesh,draw,moves,shading);
        }
    }

    private static bool FollowsBody(Vector3 a,Vector3 b,Vector3 c,Vector3 normal,ClearanceSurface body)
    {
        foreach(var p in new[]{(a+b+c)/3,(a+b)/2,(a+c)/2,(b+c)/2})
        {
            var h=body.Nearest(p);
            if(h.Distance>10 || Math.Abs(Vector3.Dot(h.Normal,normal))<.72f) return false;
        }
        return true;
    }
}
