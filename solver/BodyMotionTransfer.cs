using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class BodyMotionTransfer
{
    private sealed class BodySurface
    {
        private readonly ClearanceSurface surface;
        private readonly Dictionary<string,float>[] weights;
        private readonly Dictionary<Vector3,Dictionary<string,MotionWeights.Coefficient>> samples=[];
        internal readonly HashSet<string> Bones=[];

        internal BodySurface(IEnumerable<byte[]> bodies,bool includeUnderwear=false)
        {
            var points=new List<Vector3>();var influences=new List<Dictionary<string,float>>();
            foreach(var body in bodies)
            {
                var layout=SecondSkinWriter.Parse(body);
                for(int mesh=layout.Lod0MeshIndex;mesh<layout.Lod0MeshIndex+layout.Lod0MeshCount;mesh++)
                {
                    int material=BitConverter.ToUInt16(body,layout.MeshStart+36*mesh+8);
                    if(!includeUnderwear && !SecondSkinWriter.IsBodySkinMaterial(layout.MatNames[material])) continue;
                    var geometry=new GarmentClearance.Mesh(layout,mesh);
                    var perVertex=new Dictionary<int,Dictionary<string,float>>();
                    foreach(int vertex in geometry.Indices)
                    {
                        if(!perVertex.TryGetValue(vertex,out var skinning))
                        {
                            skinning=SurfaceSkinning.Read(body,layout,mesh,vertex);
                            perVertex.Add(vertex,skinning);Bones.UnionWith(skinning.Keys);
                        }
                        points.Add(geometry.P[vertex]);influences.Add(skinning);
                    }
                }
            }
            surface=new(points.ToArray());weights=influences.ToArray();
        }

        internal Dictionary<string,MotionWeights.Coefficient> Sample(Vector3 position)
        {
            if(samples.TryGetValue(position,out var cached)) return cached;
            var hit=surface.Nearest(position);int t=3*hit.Face;
            BrushTransfer.ClosestOnTriangle(position,surface.Points[t],surface.Points[t+1],surface.Points[t+2],
                out float a,out float b,out float c);
            float[] blend=[a,b,c];
            var result=new Dictionary<string,MotionWeights.Coefficient>();
            for(int j=0;j<3;j++)
                foreach(var (bone,weight) in weights[t+j])
                {
                    float mass=blend[j]*weight;var old=result.GetValueOrDefault(bone);
                    result[bone]=new(old.Mass+mass,old.Moment+mass*surface.Points[t+j]);
                }
            samples.Add(position,result);return result;
        }
    }

    internal static byte[] Apply(byte[] original,byte[] morphed,NativeMorph morph,
        IReadOnlyDictionary<string,string> originals,Dictionary<string,byte[]> targets)
        // Retained outfit skin must use the same body-motion conversion as its
        // clothing. UV/material conversion alone leaves it on the vanilla rig.
        // NativeMorph's vanilla reference includes the stock bra and briefs.
        // Those cover missing skin faces and carry their actual body motion;
        // sampling exposed skin alone projects breast supports onto distant rims.
        => Apply(original,morphed,morph.MotionSupport,new[]{"_top","_dwn"}.Select(slot=>File.ReadAllBytes(originals[slot])),targets,true,
            sourceUnderwear:true);

    internal static byte[] Apply(byte[] original,byte[] morphed,
        Func<Vector3,(Vector3 Source,Vector3 Target,float Weight)[]> motionSupport,
        IEnumerable<byte[]> originals,Dictionary<string,byte[]> targets,bool includeSkin,
        IReadOnlyDictionary<int,IReadOnlySet<int>>? held=null,bool sourceUnderwear=false)
    {
        var oldBody=new BodySurface(originals,sourceUnderwear);
        var newBody=new BodySurface(new[]{"_top","_dwn"}.Select(slot=>targets[slot]));
        var source=SecondSkinWriter.Parse(original);var fitted=SecondSkinWriter.Parse(morphed);
        var joins=ClothSubdivision.IdentifyJoins(source);
        var transfers=new Dictionary<int,Dictionary<int,Dictionary<string,float>>>();
        for(int mesh=source.Lod0MeshIndex;mesh<source.Lod0MeshIndex+source.Lod0MeshCount;mesh++)
        {
            int material=BitConverter.ToUInt16(original,source.MeshStart+36*mesh+8);
            bool skin=SecondSkinWriter.IsBodySkinMaterial(source.MatNames[material]);
            if(skin && !includeSkin) continue;
            var rest=new GarmentClearance.Mesh(source,mesh);var next=new GarmentClearance.Mesh(fitted,mesh);
            var field=fitted.Decls[mesh].First(e=>e.Usage==1);
            int capacity=field.Type==17?8:4;
            var authoredWeights=Enumerable.Range(0,rest.P.Length)
                .Select(v=>SurfaceSkinning.Read(original,source,mesh,v)).ToArray();
            var rigid=skin ? new HashSet<int>() : RigidVertices(rest,authoredWeights);
            foreach(var edge in joins.Keys.Where(e=>e.Mesh==mesh))
            {
                if(includeSkin) {rigid.Remove(edge.A);rigid.Remove(edge.B);}
                else {rigid.Add(edge.A);rigid.Add(edge.B);}
                foreach(int vertex in new[]{edge.A,edge.B})
                    if(rest.ShapeTargets.TryGetValue(vertex,out var variants))
                    {
                        if(includeSkin) rigid.ExceptWith(variants);
                        else rigid.UnionWith(variants);
                    }
            }
            var vertices=new Dictionary<int,Dictionary<string,float>>();
            var pinned=held?.GetValueOrDefault(mesh);
            // Include authored shape replacement vertices as well as base vertices.
            for(int vertex=0;vertex<rest.P.Length;vertex++)
            {
                if(rigid.Contains(vertex) || pinned?.Contains(vertex)==true) continue;
                var oldCoefficients=new Dictionary<string,MotionWeights.Coefficient>();
                var newCoefficients=new Dictionary<string,MotionWeights.Coefficient>();
                foreach(var support in motionSupport(rest.P[vertex]))
                {
                    Add(oldCoefficients,oldBody.Sample(support.Source),support.Weight);
                    Add(newCoefficients,newBody.Sample(support.Target),support.Weight);
                }
                var authored=authoredWeights[vertex];
                // 25 mm is the provisional translation/rotation balance for the
                // coefficient objective, not a garment-to-body distance cutoff.
                var weights=MotionWeights.Fit(rest.P[vertex],next.P[vertex],authored,
                    oldCoefficients,newCoefficients,oldBody.Bones,capacity,25);
                if(weights.Keys.Union(authored.Keys).Any(b=>Math.Abs(weights.GetValueOrDefault(b)-authored.GetValueOrDefault(b))>1e-6f))
                    vertices.Add(vertex,weights);
            }
            if(vertices.Count>0) transfers.Add(mesh,vertices);
        }
        return SurfaceSkinning.Apply(morphed,transfers,new[]{targets["_top"],targets["_dwn"]});
    }

    private static HashSet<int> RigidVertices(GarmentClearance.Mesh mesh,Dictionary<string,float>[] weights)
    {
        var parent=Enumerable.Range(0,mesh.P.Length).ToArray();
        int Root(int i)
        {
            while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}
            return i;
        }
        for(int t=0;t<mesh.Indices.Length;t+=3)
        {
            int root=Root(mesh.Indices[t]);
            parent[Root(mesh.Indices[t+1])]=root;parent[Root(mesh.Indices[t+2])]=root;
        }
        var rigid=new HashSet<int>();
        foreach(var component in mesh.Indices.Distinct().GroupBy(Root))
        {
            var names=component.SelectMany(v=>weights[v].Keys).Distinct().Take(2).ToArray();
            if(names.Length!=1) continue;
            rigid.UnionWith(component);
            foreach(int v in component)
                if(mesh.ShapeTargets.TryGetValue(v,out var variants)) rigid.UnionWith(variants);
        }
        return rigid;
    }

    private static void Add(Dictionary<string,MotionWeights.Coefficient> output,
        Dictionary<string,MotionWeights.Coefficient> input,float factor)
    {
        foreach(var (bone,c) in input)
        {
            var old=output.GetValueOrDefault(bone);
            output[bone]=new(old.Mass+factor*c.Mass,old.Moment+factor*c.Moment);
        }
    }
}
