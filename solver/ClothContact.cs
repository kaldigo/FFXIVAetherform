using System.Numerics;

namespace Aetherform.Geometry;

internal static class ClothContact
{
    // Intersect a cloth triangle's footprint with the finite skin triangle in
    // both states. Return the original gap at the same cloth coordinates.
    internal static List<(Vector3 Bary,Vector3 Normal,float Deficit)> Lost(
        Vector3[] cloth,Vector3[] source,Vector3[] target,Vector3[] reference,Vector3 skinNormal,Vector3 originalNormal)
    {
        var result=new List<(Vector3,Vector3,float)>();
        var face=Vector3.Cross(target[1]-target[0],target[2]-target[0]);
        var native=Vector3.Cross(reference[1]-reference[0],reference[2]-reference[0]);
        if(face.LengthSquared()<1e-14f || native.LengthSquared()<1e-14f) return result;
        var newN=Vector3.Normalize(face);if(Vector3.Dot(newN,skinNormal)<0) newN=-newN;
        var oldN=Vector3.Normalize(native);if(Vector3.Dot(oldN,originalNormal)<0) oldN=-oldN;
        var gap=cloth.Select(p=>Vector3.Dot(p-target[0],newN)).ToArray();
        if(gap.Min()>=-.001f || gap.Max()<=-.001f) return result;
        var old=source.Select(p=>Vector3.Dot(p-reference[0],oldN)).ToArray();
        var polygon=new List<Vector3>{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ};
        for(int k=0;k<3 && polygon.Count>0;k++)
        {
            var side=Vector3.Cross(face,target[(k+1)%3]-target[k]);
            polygon=FiniteClearance.Clip(polygon,cloth.Select(p=>Vector3.Dot(side,p-target[k])).ToArray());
            var oldSide=Vector3.Cross(native,reference[(k+1)%3]-reference[k]);
            polygon=FiniteClearance.Clip(polygon,source.Select(p=>Vector3.Dot(oldSide,p-reference[k])).ToArray());
        }
        if(polygon.Count<3) return result;
        foreach(var b in polygon)
        {
            float goal=b.X*old[0]+b.Y*old[1]+b.Z*old[2];
            float lost=goal-(b.X*gap[0]+b.Y*gap[1]+b.Z*gap[2]);
            if(goal>=-.001f && lost>.001f) result.Add((b,newN,lost));
        }
        return result;
    }
}
