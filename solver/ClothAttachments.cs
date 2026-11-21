using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

internal static class ClothAttachments
{
    // Carry a reviewed overlay in the local frame of the subdivided supporting cloth.
    // Source and destination share triangle lineage even when density varies by preset.
    internal static void Apply(byte[] original, byte[] output, Dictionary<int, ClothSubdivision.Origins> origins,
        GarmentClassification? classification)
    {
        if (classification == null || classification.SurfaceAttachments.Count == 0) return;
        var src = SecondSkinWriter.Parse(original);
        var dst = SecondSkinWriter.Parse(output);
        var draws = new Dictionary<int, GarmentClearance.Mesh>();
        var sources = new Dictionary<int, Vector3[]>();
        var children = new Dictionary<(int,int), List<int>>();
        for (int m = dst.Lod0MeshIndex; m < dst.Lod0MeshIndex + dst.Lod0MeshCount; m++)
        {
            int mesh = m - dst.Lod0MeshIndex;
            var draw = draws[mesh] = new(dst, m);
            bool split = origins.TryGetValue(m, out var lineage);
            sources[mesh] = split ? lineage!.Positions.Select(p => 1000*p).ToArray() : new GarmentClearance.Mesh(src,m).P;
            for (int f = 0; f < draw.Indices.Length/3; f++)
            {
                var key = (mesh, split ? lineage!.Parents[f] : f);
                if (!children.TryGetValue(key,out var list)) children.Add(key,list=[]);
                list.Add(f);
            }
        }
        var offsets = new Dictionary<int,Dictionary<int,Vector3>>();
        foreach (var (key, hosts) in classification.SurfaceAttachments)
        {
            var address = key.Split('/');
            int mesh = int.Parse(address[0]), parent = int.Parse(address[1]);
            var draw = draws[mesh];
            if (!offsets.TryGetValue(mesh,out var moves)) offsets[mesh] = moves = [];
            foreach (int face in children[(mesh,parent)])
            foreach (int v in draw.Indices.AsSpan(3*face,3))
            {
                if (moves.ContainsKey(v) || draw.AmbiguousShapeBases.Contains(v)) continue;
                var point = sources[mesh][v];
                float best = float.PositiveInfinity;
                foreach (var root in hosts)
                foreach (int host in children[(root[0],root[1])])
                {
                    var support = draws[root[0]];
                    var ids = support.Indices.AsSpan(3*host,3);
                    var rest = sources[root[0]];
                    var a=rest[ids[0]]; var b=rest[ids[1]]; var c=rest[ids[2]];
                    var q=support.P; var u=q[ids[0]]; var w=q[ids[1]]; var t=q[ids[2]];
                    var n=Vector3.Cross(b-a,c-a); var m=Vector3.Cross(w-u,t-u);
                    if(n.LengthSquared()<1e-8f || m.LengthSquared()<1e-8f) continue;
                    var hit=BrushTransfer.ClosestOnTriangle(point,a,b,c,out float x,out float y,out float z);
                    var offset=point-hit;
                    if(offset.LengthSquared()>=best) continue;
                    best=offset.LengthSquared();
                    n=Vector3.Normalize(n); m=Vector3.Normalize(m);
                    var e=Vector3.Normalize(b-a); var h=Vector3.Normalize(w-u);
                    var rotated=Vector3.Dot(offset,e)*h + Vector3.Dot(offset,n)*m
                        + Vector3.Dot(offset,Vector3.Cross(n,e))*Vector3.Cross(m,h);
                    moves[v]=x*u+y*w+z*t+rotated-draw.P[v];
                }
            }
        }
        foreach(var (mesh,moves) in offsets)
            ModelPanelEdits.Write(output,dst,mesh+dst.Lod0MeshIndex,draws[mesh],moves);
    }
}
