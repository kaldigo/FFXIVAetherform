using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

internal static class SkinOverlays
{
    // A flush marking and its independently skinned host must retain the authored
    // triangles together. Subdividing just the marking changes its posed surface.
    internal static HashSet<int> Find(byte[] model)
    {
        var result=new HashSet<int>();
        var layout=SecondSkinWriter.Parse(model);
        var meshes=Enumerable.Range(layout.Lod0MeshIndex,layout.Lod0MeshCount);
        bool Skin(int i)=>SecondSkinWriter.IsBodySkinMaterial(layout.MatNames[BitConverter.ToUInt16(model,layout.MeshStart+36*i+8)]);
        var triangles=new List<Vector3>();
        foreach(int i in meshes.Where(Skin))
        {
            var skin=new GarmentClearance.Mesh(layout,i);
            triangles.AddRange(skin.Indices.Select(v=>skin.P[v]));
        }
        if(!Enumerable.Range(0,triangles.Count/3).Any(f=>Vector3.Cross(triangles[3*f+1]-triangles[3*f],triangles[3*f+2]-triangles[3*f]).LengthSquared()>1e-16f)) return result;
        ClearanceSurface? surface=null;
        foreach(int i in meshes.Where(i=>!Skin(i)))
        {
            var cloth=new GarmentClearance.Mesh(layout,i);
            var vertices=cloth.Indices.Distinct().ToArray();
            if(vertices.Length<3) continue;
            surface??=new(triangles.ToArray());
            int flush=0,parallel=0;
            foreach(int v in vertices)
            {
                var hit=surface.Nearest(cloth.P[v]);
                if(hit.Distance>4){flush=0;break;}
                if(hit.Distance<1.3f) flush++;
                if(Math.Abs(Vector3.Dot(hit.Normal,cloth.N[v]))>.75f) parallel++;
            }
            if(flush>=.85f*vertices.Length && parallel>=.8f*vertices.Length) result.Add(i);
        }
        return result;
    }
}
