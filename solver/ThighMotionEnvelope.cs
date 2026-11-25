using System.Numerics;

namespace Aetherform.Geometry;

// A bounded kinematic clearance model, not a playback of a particular idle.
// Each thigh group may rotate ten degrees and translate five mm independently.
// Descendant weights are grouped together; common motion cancels in the moments.
internal static class ThighMotionEnvelope
{
    internal const float RotationRadians = MathF.PI/18;
    internal const float TranslationMm = 5;
    // Reserve room for the host normal changing across neighbouring skin faces
    // as well as packed vertex/weight precision during the articulated motion.
    internal const float MarginMm = 2;
    private static readonly float Sin=MathF.Sin(RotationRadians),Cos=MathF.Cos(RotationRadians),Tan=MathF.Tan(RotationRadians);
    internal readonly record struct Group(float Mass,Vector3 Moment)
    {
        public static Group operator -(Group a,Group b)=>new(a.Mass-b.Mass,a.Moment-b.Moment);
    }

    internal static int Side(string bone)
    {
        if(!bone.StartsWith("j_asi_",StringComparison.Ordinal) && !bone.Contains("daitai",StringComparison.Ordinal)) return -1;
        return bone.EndsWith("_l",StringComparison.Ordinal) ? 0 : bone.EndsWith("_r",StringComparison.Ordinal) ? 1 : -1;
    }
    internal static bool IsThigh(string bone) => bone.StartsWith("j_asi_a_",StringComparison.Ordinal) ||
        bone.Contains("daitai",StringComparison.Ordinal);
    internal static bool IsSkirt(string bone) => bone.StartsWith("j_sk_",StringComparison.Ordinal) ||
        bone.Contains("skirt",StringComparison.OrdinalIgnoreCase);

    internal static bool Exterior(Vector3 normal,Vector3 point,Vector3 center)
    {
        var radial=point-center;radial.Y=0;
        return radial.LengthSquared()>1 && Vector3.Dot(normal,radial)>.2f*radial.Length();
    }

    internal static float Closure(Vector3 normal,Vector3 pivot,Group body,Group cloth)
    {
        float mass=body.Mass-cloth.Mass;
        var moment=body.Moment-cloth.Moment-mass*pivot;
        float along=Vector3.Dot(normal,moment);
        float across=MathF.Sqrt(MathF.Max(0,moment.LengthSquared()-along*along));
        float rotational=along>0 && across<=along*Tan ? MathF.Sqrt(moment.LengthSquared())-along : along*(Cos-1)+across*Sin;
        return MathF.Max(0,rotational)+TranslationMm*MathF.Abs(mass);
    }
}
