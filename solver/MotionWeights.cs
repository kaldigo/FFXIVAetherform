using System.Numerics;

namespace Aetherform.Geometry;

// A linear skinning coefficient contains both mass and its rest-position moment.
// Keeping these separate also works on barycentric samples of a skinned triangle.
internal static class MotionWeights
{
    internal readonly record struct Coefficient(double Mass, Vector3 Moment);

    internal static Dictionary<string,float> Fit(Vector3 source, Vector3 target,
        IReadOnlyDictionary<string,float> authored,
        IReadOnlyDictionary<string,Coefficient> oldBody,
        IReadOnlyDictionary<string,Coefficient> newBody,
        IReadOnlySet<string> bodyBones, int capacity, double translationScaleMm)
    {
        if (!(translationScaleMm > 0) || !double.IsFinite(translationScaleMm))
            throw new ArgumentOutOfRangeException(nameof(translationScaleMm));
        var names=authored.Keys.Union(oldBody.Keys).Union(newBody.Keys).Order(StringComparer.Ordinal).ToArray();
        Vector3 BodyPosition(IReadOnlyDictionary<string,Coefficient> body) => body.Values.Aggregate(Vector3.Zero,(p,c)=>p+c.Moment);
        var residual=target-source-(BodyPosition(newBody)-BodyPosition(oldBody));
        var displacement=target-source;
        double scale2=translationScaleMm*translationScaleMm;
        double diagonal=scale2+displacement.LengthSquared();
        var desired=new Dictionary<string,Coefficient>();
        foreach (string name in names)
        {
            double weight=authored.GetValueOrDefault(name);
            var a=oldBody.GetValueOrDefault(name);var b=newBody.GetValueOrDefault(name);
            // Recenter all moments at the original garment vertex. The added
            // residual makes the target coefficients sum to the emitted position.
            desired[name]=new(weight+b.Mass-a.Mass,
                b.Moment-a.Moment-(float)(b.Mass-a.Mass)*source+(float)weight*residual);
        }
        double Preferred(string name)
        {
            var c=desired[name];
            return (scale2*c.Mass+Vector3.Dot(displacement,c.Moment))/diagonal;
        }
        // Bones absent from the source body belong to the garment's own rig.
        var locked=authored.Where(p=>!bodyBones.Contains(p.Key)).ToDictionary();
        int available=capacity-locked.Count;
        double mass=1-locked.Values.Sum(v=>(double)v);
        if (available<=0 || mass<=1e-8) return authored.ToDictionary();
        var active=names.Where(n=>!locked.ContainsKey(n)).OrderByDescending(Preferred)
            .ThenBy(n=>n,StringComparer.Ordinal).Take(available).ToArray();
        if(active.Length==0) return authored.ToDictionary();
        // Exact projection onto a simplex. Every active diagonal is the same.
        var preferred=active.Select(Preferred).ToArray();
        double low=preferred.Min()-mass, high=preferred.Max();
        for(int iteration=0;iteration<64;iteration++)
        {
            double threshold=(low+high)/2;
            if(preferred.Sum(v=>Math.Max(0,v-threshold))>mass) low=threshold;
            else high=threshold;
        }
        var candidate=locked.ToDictionary();
        for(int i=0;i<active.Length;i++)
        {
            float value=(float)Math.Max(0,preferred[i]-high);
            if(value>0) candidate[active[i]]=value;
        }
        // Compare the actual byte-weight result. Locked garment influences keep
        // their original byte masses even when body influences need rounding.
        var free=candidate.Keys.Where(n=>!locked.ContainsKey(n)).ToArray();
        var packed=free.Select(n=>(int)Math.Floor(255d*candidate[n])).ToArray();
        int remaining=255-locked.Values.Sum(v=>(int)Math.Round(255d*v))-packed.Sum();
        foreach(int i in Enumerable.Range(0,free.Length)
            .OrderByDescending(i=>255d*candidate[free[i]]-packed[i]).ThenBy(i=>free[i],StringComparer.Ordinal).Take(remaining))
            packed[i]++;
        for(int i=0;i<free.Length;i++) candidate[free[i]]=packed[i]/255f;
        candidate=candidate.Where(p=>p.Value>0).ToDictionary();
        double Error(IReadOnlyDictionary<string,float> weights)
        {
            double error=0;
            foreach(string name in names)
            {
                double w=weights.GetValueOrDefault(name);var c=desired[name];
                error+=scale2*(w-c.Mass)*(w-c.Mass)+((float)w*displacement-c.Moment).LengthSquared();
            }
            return error;
        }
        return Error(candidate)<Error(authored)-1e-9 ? candidate : authored.ToDictionary();
    }
}
