using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class StockingContour
{
    private const int Bearings=72, Samples=25;
    private readonly record struct Section(Vector2 A,Vector2 B,int First,Vector3 From,Vector3 To);
    private sealed record Profile(float Rim,float[] Shift);
    private static Vector2 Ground(Vector3 p)=>new(p.X,p.Z);

    // Collect closed physical skin boundaries. UV and material splits may repeat vertices.
    private static IEnumerable<int[]> Rings(GarmentClearance.Mesh skin)
    {
        var edges=new Dictionary<(int,int),int>();var reps=new Dictionary<Vector3,int>();
        int Rep(int v){if(reps.TryGetValue(skin.P[v],out int id))return id;reps[skin.P[v]]=v;return v;}
        var ix=skin.Indices;
        for(int f=0;f<ix.Length;f+=3)
        {
            if(Vector3.Cross(skin.P[ix[f+1]]-skin.P[ix[f]],skin.P[ix[f+2]]-skin.P[ix[f]]).LengthSquared()<1e-10f)continue;
            for(int k=0;k<3;k++)
            {
                int a=Rep(ix[f+k]),b=Rep(ix[f+(k+1)%3]);var edge=(Math.Min(a,b),Math.Max(a,b));
                edges[edge]=edges.GetValueOrDefault(edge)+1;
            }
        }
        var rim=edges.Where(e=>e.Value==1).Select(e=>e.Key).ToArray();
        var links=rim.Concat(rim.Select(e=>(e.Item2,e.Item1))).ToLookup(e=>e.Item1,e=>e.Item2);
        var seen=new HashSet<int>();
        foreach(var group in links)
        {
            if(seen.Contains(group.Key))continue;
            var vertices=new List<int>();int start=group.Key,at=start,previous=-1;
            while(seen.Add(at))
            {
                vertices.Add(at);var next=links[at].ToArray();if(next.Length!=2)break;
                (previous,at)=(at,next[0]==previous?next[1]:next[0]);
            }
            if(at==start && vertices.Count>=6)yield return vertices.ToArray();
        }
    }

    // Intersect the garment with vertical half-planes around the limb.
    private static Vector2? Crossing(Vector3 a,Vector3 b,Vector2 center,Vector2 ray)
    {
        var side=new Vector2(-ray.Y,ray.X);
        float x=Vector2.Dot(Ground(a)-center,side),y=Vector2.Dot(Ground(b)-center,side);
        if((x<=0)==(y<=0))return null;
        var p=Vector3.Lerp(a,b,x/(x-y));return new(Vector2.Dot(Ground(p)-center,ray),p.Y);
    }
    private static List<Section> Slice(Vector3[] points,int[] indices,Vector2 center,Vector2 ray)
    {
        var sections=new List<Section>();
        for(int i=0;i<indices.Length;i+=3)
        {
            Vector2? first=null;var bary=Vector3.Zero;
            for(int k=0;k<3;k++)
            {
                var hit=Crossing(points[indices[i+k]],points[indices[i+(k+1)%3]],center,ray);
                if(hit==null)continue;
                var a=Ground(points[indices[i+k]])-center;var edge=Ground(points[indices[i+(k+1)%3]])-Ground(points[indices[i+k]]);
                float fraction=-(a.X*ray.Y-a.Y*ray.X)/(edge.X*ray.Y-edge.Y*ray.X);
                var weights=Vector3.Zero;weights[k]=1-fraction;weights[(k+1)%3]=fraction;
                if(first==null){first=hit;bary=weights;}
                else if(Math.Max(first.Value.X,hit.Value.X)>0)sections.Add(new(first.Value,hit.Value,i,bary,weights));
            }
        }
        return sections;
    }
    private static Vector2? Rim(Vector3[] p,int[] loop,Vector2 center,Vector2 ray)
    {
        for(int i=0;i<loop.Length;i++)
            if(Crossing(p[loop[i]],p[loop[(i+1)%loop.Length]],center,ray) is {} hit && hit.X>0)return hit;
        return null;
    }
    private static float Radius(List<Section> skin,float y,float reference)=>Radius(skin,y,reference,out _,out _);
    private static float Radius(List<Section> skin,float y,float reference,out int first,out Vector3 weights)
    {
        first=-1;weights=default;
        float best=float.NaN;
        foreach(var (a,b,face,from,to) in skin)
        {
            if((a.Y<=y)==(b.Y<=y))continue;
            float x=a.X+(b.X-a.X)*(y-a.Y)/(b.Y-a.Y);
            if(x>0 && Math.Abs(x-reference)<.45f*reference && (!float.IsFinite(best)||Math.Abs(x-reference)<Math.Abs(best-reference))){best=x;first=face;weights=Vector3.Lerp(from,to,(y-a.Y)/(b.Y-a.Y));}
        }
        return best;
    }
    internal static void Apply(CutSkinAssembly.Assembly assembly,byte[] original,SecondSkinWriter.Source morphed,byte[] cloth,SkinDisplacement.Topology topology)
    {
        if(assembly.Knots.Count==0)return;
        bool Thigh(string bone)=>bone is "j_asi_a_l" or "j_asi_a_r";
        var sewn=assembly.Knots.Values.Where(k=>k.Weights.Any(w=>Thigh(w.Bone)&&w.Weight>96)).ToArray();
        if(sewn.Length<6)return;
        var ids=topology.Nodes;var pinned=topology.Anchors;
        var native=new Vector3[pinned.Length];var solved=new Vector3[pinned.Length];
        var normals=new Vector3[pinned.Length];var weights=new float[pinned.Length];
        for(int j=0;j<ids.Length;j++)native[ids[j]]=assembly.Corners[j].Skin.PositionMm;
        ClearanceSurface? clothing=null;
        var src=SecondSkinWriter.Parse(original);

        foreach(int mesh in assembly.Corners.Select(c=>c.Carrier.Source.Mesh).Distinct())
        {
            var skin=new GarmentClearance.Mesh(src,mesh);var mapped=new GarmentClearance.Mesh(morphed,mesh);
            foreach(var loop in Rings(skin))
            {
                if(loop.Any(i=>!sewn.Any(q=>Vector3.DistanceSquared(q.PositionMm,mapped.P[i])<.25f)))continue;
                Vector2 center=loop.Aggregate(Vector2.Zero,(s,i)=>s+Ground(skin.P[i]))/loop.Length;
                Vector2 dst=loop.Aggregate(Vector2.Zero,(s,i)=>s+Ground(mapped.P[i]))/loop.Length;
                float radius=loop.Average(i=>Vector2.Distance(center,Ground(skin.P[i])));
                if(radius<1 || loop.Max(i=>skin.P[i].Y)-loop.Min(i=>skin.P[i].Y)>radius*.8f)continue;
                float length=radius*.64f;
                var knot=sewn.MinBy(k=>Vector3.DistanceSquared(k.PositionMm,mapped.P[loop[0]]))!;
                string thigh=knot.Weights.Where(w=>Thigh(w.Bone)).MaxBy(w=>w.Weight).Bone;
                // The first pass fits vertices. Subsequent passes distribute sampled
                // face-interior errors to their three supporting vertices.
                var baseline=solved.ToArray();
                for(int pass=0;pass<5;pass++)
                {
                    var fit=native.Select((p,i)=>p+solved[i]).ToArray();
                    var profiles=new Profile?[Bearings];
                    var residual=new float[solved.Length];var mass=new float[solved.Length];
                    for(int b=0;b<Bearings;b++)
                    {
                        float angle=2*MathF.PI*b/Bearings;var ray=new Vector2(MathF.Cos(angle),MathF.Sin(angle));
                        if(Rim(skin.P,loop,center,ray) is not {} a || Rim(mapped.P,loop,dst,ray) is not {} t)continue;
                        var old=Slice(skin.P,skin.Indices,center,ray);var current=Slice(fit,ids,dst,ray);
                        float end=Radius(old,a.Y+length,a.X),targetEnd=Radius(current,t.Y+length,t.X);
                        if(!float.IsFinite(end+targetEnd))continue;
                        var shifts=new float[Samples+1];var hits=new (int First,Vector3 Weights)[Samples+1];bool complete=true;
                        for(int k=1;k<Samples;k++)
                        {
                            float u=(float)k/Samples;
                            float bow=Radius(old,a.Y+u*length,a.X)-float.Lerp(a.X,end,u);
                            float fitted=Radius(current,t.Y+u*length,t.X,out hits[k].First,out hits[k].Weights)-float.Lerp(t.X,targetEnd,u);
                            if(!float.IsFinite(bow+fitted)){complete=false;break;}
                            shifts[k]=Math.Clamp(bow-fitted,-3,3);
                        }
                        if(!complete)continue;
                        profiles[b]=new(t.Y,shifts);
                        if(pass==0)continue;
                        for(int k=1;k<Samples;k++)
                        {
                            var (first,w)=hits[k];
                            if(first<0)continue;
                            for(int l=0;l<3;l++)
                            {
                                int i=ids[first+l];
                                residual[i]+=w[l]*shifts[k];
                                mass[i]+=w[l];
                            }
                        }
                    }
                    var seen=new HashSet<int>();
                    for(int j=0;j<ids.Length;j++)
                    {
                        int i=ids[j];var corner=assembly.Corners[j];
                        if(corner.Carrier.Source.Mesh!=mesh || !seen.Add(i) || !corner.Skin.Weights.Any(w=>w.Bone==thigh && w.Weight>96))continue;
                        var r=Ground(fit[i])-dst;float dist=r.Length();
                        if(dist<radius*.5f || dist>radius*1.5f)continue;
                        float angle=MathF.Atan2(r.Y,r.X)*Bearings/(2*MathF.PI);if(angle<0)angle+=Bearings;
                        int b=(int)angle%Bearings;float blend=angle-MathF.Floor(angle);
                        if(profiles[b] is not {} left || profiles[(b+1)%Bearings] is not {} right)continue;
                        float u=(fit[i].Y-float.Lerp(left.Rim,right.Rim,blend))/length;
                        if(u<0 || u>=1)continue;
                        float index=u*Samples;int k=Math.Min((int)index,Samples-1);
                        float Shift(Profile p)=>float.Lerp(p.Shift[k],p.Shift[k+1],index-k);
                        if(!pinned[i])
                        {
                            var axis=new Vector3(r.X/dist,0,r.Y/dist);
                            float step=pass==0?float.Lerp(Shift(left),Shift(right),blend):residual[i]/Math.Max(.01f,mass[i]);
                            float prior=Vector3.Dot(solved[i]-baseline[i],axis);
                            solved[i]+=axis*(Math.Clamp(prior+step,-3,3)-prior);
                        }
                        var carrier=corner.Carrier;var f=carrier.Source;var t=f.SourceBarycentric;var bc=carrier.Barycentric;
                        var q=t.A+bc.X*(t.B-t.A)+bc.Y*(t.C-t.A);
                        var normal=(float)(1-q.X-q.Y)*mapped.N[f.Vertices[0]]+(float)q.X*mapped.N[f.Vertices[1]]+(float)q.Y*mapped.N[f.Vertices[2]];
                        if(normal.LengthSquared()<.1f)continue;
                        normal=Vector3.Normalize(normal);
                        if(Vector3.Dot(normal,corner.Skin.Normal)<.75f)continue;
                        float fade=Math.Clamp((1-u)*3,0,1);
                        normals[i]=normal;weights[i]=fade*fade*(3-2*fade);
                    }
                }
                clothing??=ClothSurface(cloth);
                for(int i=0;i<solved.Length;i++)
                {
                    var shift=solved[i]-baseline[i];float distance=shift.Length();
                    if(distance<.0001f)continue;
                    float limit=clothing.Nearest(native[i]+baseline[i]).Distance*.45f;
                    if(distance>limit)solved[i]=baseline[i]+shift*(limit/distance);
                }
                PreserveOrientation(ids,native,baseline,solved);
            }
        }
        for(int j=0;j<ids.Length;j++)
        {
            var c=assembly.Corners[j];int i=ids[j];
            if(weights[i]==0 && solved[i]==Vector3.Zero)continue;
            var normal=Vector3.Normalize(Vector3.Lerp(c.Skin.Normal,normals[i],weights[i]));
            assembly.Corners[j]=c with {Skin=c.Skin with {PositionMm=c.Skin.PositionMm+solved[i],Normal=normal}};
        }
    }
    private static ClearanceSurface ClothSurface(byte[] cloth)
    {
        var model=SecondSkinWriter.Parse(cloth);var tri=new List<Vector3>();
        for(int m=model.Lod0MeshIndex;m<model.Lod0MeshIndex+model.Lod0MeshCount;m++)
        {
            if(SecondSkinWriter.IsBodySkinMaterial(model.MatNames[BitConverter.ToUInt16(cloth,model.MeshStart+36*m+8)]))continue;
            var mesh=new GarmentClearance.Mesh(model,m);tri.AddRange(mesh.Indices.Select(i=>mesh.P[i]));
        }
        return new(tri.ToArray());
    }
    private static void PreserveOrientation(int[] ids,Vector3[] native,Vector3[] before,Vector3[] after)
    {
        var rollback=new HashSet<int>();
        for(int step=0;step<12;step++)
        {
            rollback.Clear();
            for(int f=0;f<ids.Length;f+=3)
            {
                int a=ids[f],b=ids[f+1],c=ids[f+2];
                var ab=native[b]-native[a];var ac=native[c]-native[a];
                var prior=Vector3.Cross(ab+before[b]-before[a],ac+before[c]-before[a]);
                var current=Vector3.Cross(ab+after[b]-after[a],ac+after[c]-after[a]);
                if(Vector3.Dot(prior,current)>=.15f*prior.LengthSquared())continue;
                rollback.Add(a);rollback.Add(b);rollback.Add(c);
            }
            if(rollback.Count==0)return;
            foreach(int i in rollback)after[i]=Vector3.Lerp(before[i],after[i],.5f);
        }
        // A chain of nearly degenerate cut triangles may resist a local repair.
        before.CopyTo(after,0);
    }
}
