using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

// Contact regions follow their skin host through subdivision and skin replacement.
internal static class SkinSurfaceContact
{
    internal static byte[] Apply(byte[] source,byte[] fit,
        Dictionary<int,ClothSubdivision.Origins>? origins=null,CutSkinAssembly.Assembly? assembly=null)
    {
        var old=SecondSkinWriter.Parse(source);var next=SecondSkinWriter.Parse(fit);
        var authoredRest=new List<Vector3>();
        var authoredWeights=new List<Dictionary<string,float>>();
        var hostWeights=new List<Dictionary<string,float>>();
        var transfers=new Dictionary<int,Dictionary<int,Dictionary<string,float>>>();
        var rest=new List<Vector3>();var target=new List<Vector3>();var panels=new List<(int,GarmentClearance.Mesh,GarmentClearance.Mesh)>();
        if(old.Lod0MeshCount!=next.Lod0MeshCount) return fit;
        if(assembly!=null)
        {
            var sourceDraws=new Dictionary<int,GarmentClearance.Mesh>();
            foreach(var corner in assembly.Corners)
            {
                var carrier=corner.Carrier;
                var face=carrier.Source;
                if(!sourceDraws.TryGetValue(face.Mesh,out var draw))
                    sourceDraws[face.Mesh]=draw=new GarmentClearance.Mesh(old,face.Mesh);
                var basis=face.SourceBarycentric;
                var uv=basis.A+carrier.Barycentric.X*(basis.B-basis.A)+carrier.Barycentric.Y*(basis.C-basis.A);
                var p=draw.P;
                rest.Add((float)(1-uv.X-uv.Y)*p[face.Vertices[0]]+(float)uv.X*p[face.Vertices[1]]+(float)uv.Y*p[face.Vertices[2]]);
                target.Add(corner.Skin.PositionMm);
                hostWeights.Add(corner.Skin.Weights.GroupBy(w=>w.Bone).ToDictionary(g=>g.Key,g=>(float)g.Sum(w=>w.Weight)));
            }
        }
        var references=new Dictionary<int,Vector3[]>();
        for(int id=0;id<old.Lod0MeshCount;id++)
        {
            int m=old.Lod0MeshIndex+id,k=next.Lod0MeshIndex+id;
            var a=new GarmentClearance.Mesh(old,m);var b=new GarmentClearance.Mesh(next,k);
            if(SecondSkinWriter.IsBodySkinMaterial(old.MatNames[BitConverter.ToUInt16(source,a.Header+8)]))
            {
                foreach(int vertex in a.Indices)
                {
                    authoredRest.Add(a.P[vertex]);
                    authoredWeights.Add(SurfaceSkinning.Read(source,old,m,vertex));
                }
                if(assembly!=null || !a.Indices.SequenceEqual(b.Indices) || a.P.Length!=b.P.Length) continue;
                foreach(int vertex in a.Indices) hostWeights.Add(SurfaceSkinning.Read(fit,next,k,vertex));
                rest.AddRange(a.Indices.Select(i=>a.P[i]));target.AddRange(b.Indices.Select(i=>b.P[i]));
            }
            else
            {
                Vector3[] points;
                if(origins!=null && origins.TryGetValue(k,out var lineage))
                    points=lineage.Positions.Select(p=>1000*p).ToArray();
                else if(a.P.Length==b.P.Length && a.Indices.SequenceEqual(b.Indices)) points=a.P;
                else continue;
                if(points.Length!=b.P.Length) continue;
                references[k]=points;
                panels.Add((k,a,b));
            }
        }
        if(rest.Count==0) return fit;
        var skin=new ClearanceSurface(rest.ToArray());
        ClearanceSurface? fittedSkin=null;
        var authored=new ClearanceSurface(authoredRest.ToArray());
        foreach(var (m,a,b) in panels)
        {
            // Join UV seam copies for component detection only.
            var points=references[m];
            var parent=Enumerable.Range(0,points.Length).ToArray();
            int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            var aliases=new Dictionary<Vector3,int>();
            foreach(int i in b.Indices){if(aliases.TryGetValue(points[i],out int j)) parent[Root(i)]=Root(j);else aliases[points[i]]=i;}
            for(int t=0;t<b.Indices.Length;t+=3)
            {
                int root=Root(b.Indices[t]);parent[Root(b.Indices[t+1])]=root;parent[Root(b.Indices[t+2])]=root;
            }
            var moves=new Dictionary<int,Vector3>();
            var weights=new Dictionary<int,Dictionary<string,float>>();
            foreach(var part in b.Indices.Distinct().GroupBy(Root))
            {
                var hits=part.Select(i=>(i,h:skin.Nearest(points[i]))).ToArray();
                if(hits.Length<3 || hits.Any(p=>b.AmbiguousShapeBases.Contains(p.i))) continue;
                int parallel=hits.Count(p=>Math.Abs(Vector3.Dot(p.h.Normal,b.N[p.i]))>.70f);
                bool flush=hits.All(p=>p.h.Distance<=2) && hits.Count(p=>p.h.Distance<=1)>=.85*hits.Length && parallel>=.8*hits.Length;
                // A skin-following band can include an outer wall a few mm above
                // its contact face. Require authored skinning agreement as well
                // as contact coverage so nearby rigid ornaments stay independent.
                bool band=false;
                var authoredHits=hits.Select(p=>(p.i,h:authored.Nearest(points[p.i]))).ToArray();
                if(!flush && authoredHits.All(p=>p.h.Distance<=4) && authoredHits.Count(p=>p.h.Distance<=1)>=.3*hits.Length && parallel>=.75*hits.Length)
                {
                    int agreed=0,total=0;
                    foreach(var (vertex,hit) in authoredHits)
                    {
                        var support=authored.Nearest(points[vertex]);int at=3*support.Face;
                        BrushTransfer.ClosestOnTriangle(points[vertex],authoredRest[at],authoredRest[at+1],authoredRest[at+2],out float u,out float v,out float w);
                        var existingWeights=SurfaceSkinning.Read(fit,next,m,vertex);
                        var expected=new Dictionary<string,float>();
                        foreach(var (corner,factor) in new[]{(at,u),(at+1,v),(at+2,w)})
                            foreach(var (bone,strength) in authoredWeights[corner])
                                expected[bone]=expected.GetValueOrDefault(bone)+factor*strength;
                        float difference=existingWeights.Keys.Union(expected.Keys).Sum(bone=>Math.Abs(existingWeights.GetValueOrDefault(bone)-expected.GetValueOrDefault(bone)))/2;
                        total++;
                        if(difference<=.025f) agreed++;
                    }
                    band=agreed>=.9*total;
                }
                if(!flush && !band) continue;
                foreach(var (i,h) in hits)
                {
                    int t;
                    float x,y,z;
                    if(flush)
                    {
                        t=3*h.Face;
                        BrushTransfer.ClosestOnTriangle(points[i],rest[t],rest[t+1],rest[t+2],out x,out y,out z);
                        var n=Vector3.Cross(target[t+1]-target[t],target[t+2]-target[t]);
                        if(n.LengthSquared()<1e-12f)continue;
                        var authoredHit=authored.Nearest(points[i]);
                        var gap=Vector3.Dot(points[i]-authoredHit.Point,authoredHit.Normal);
                        var goal=x*target[t]+y*target[t+1]+z*target[t+2]+Vector3.Normalize(n)*gap;
                        moves[i]=goal-b.P[i];
                    }
                    else
                    {
                        // Thick bands already have a fitted cross-section and clearance.
                        // Bind that geometry to the actual drawn skin beneath it.
                        fittedSkin??=new ClearanceSurface(target.ToArray());
                        t=3*fittedSkin.Nearest(b.P[i]).Face;
                        BrushTransfer.ClosestOnTriangle(b.P[i],target[t],target[t+1],target[t+2],out x,out y,out z);
                    }
                    var blend=new Dictionary<string,float>();
                    foreach(var (corner,factor) in new[]{(t,x),(t+1,y),(t+2,z)})
                    {
                        foreach(var (bone,weight) in hostWeights[corner])
                            blend[bone]=blend.GetValueOrDefault(bone)+factor*weight;
                    }
                    weights[i]=blend;
                    if(b.ShapeTargets.TryGetValue(i,out var variants))
                        foreach(int vertex in variants) weights[vertex]=blend;
                }
            }
            ModelPanelEdits.Write(fit,next,m,b,moves);
            if(weights.Count>0) transfers[m]=weights;
        }
        return SurfaceSkinning.Apply(fit,transfers);
    }
}
