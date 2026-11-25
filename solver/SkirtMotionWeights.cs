namespace Aetherform.Geometry;

internal static class SkirtMotionWeights
{
    // Transfer the source-relative body masses onto the actual emitted skin.
    // Garment-owned channels are fixed, including their exact byte masses.
    internal static Dictionary<string,float> Fit(IReadOnlyDictionary<string,float> current,
        IReadOnlyDictionary<string,float> authored,IReadOnlyDictionary<string,float> oldBody,
        IReadOnlyDictionary<string,float> newBody,IReadOnlySet<string> owned,int capacity=8)
    {
        var locked=current.Where(p=>owned.Contains(p.Key)).ToDictionary();
        int budget=255-locked.Values.Sum(w=>(int)MathF.Round(255*w)),slots=capacity-locked.Count;
        if(budget<=0 || slots<=0) return current.ToDictionary();
        var preferred=authored.Keys.Union(oldBody.Keys).Union(newBody.Keys).Union(current.Keys)
            .Where(n=>!owned.Contains(n)).Select(n=>(Name:n,Value:(double)authored.GetValueOrDefault(n)+newBody.GetValueOrDefault(n)-oldBody.GetValueOrDefault(n)))
            .OrderByDescending(p=>p.Value).ThenBy(p=>p.Name,StringComparer.Ordinal).Take(slots).ToArray();
        if(preferred.Length==0) return current.ToDictionary();
        double mass=budget/255d,low=preferred.Min(p=>p.Value)-mass,high=preferred.Max(p=>p.Value);
        for(int i=0;i<64;i++)
        {
            double cut=(low+high)/2;
            if(preferred.Sum(p=>Math.Max(0,p.Value-cut))>mass) low=cut;else high=cut;
        }
        var values=preferred.Select(p=>Math.Max(0,p.Value-high)*255).ToArray();
        var packed=values.Select(v=>(int)Math.Floor(v)).ToArray();
        foreach(int i in Enumerable.Range(0,packed.Length).OrderByDescending(i=>values[i]-packed[i])
            .ThenBy(i=>preferred[i].Name,StringComparer.Ordinal).Take(budget-packed.Sum())) packed[i]++;
        var result=locked.ToDictionary();
        for(int i=0;i<preferred.Length;i++) if(packed[i]>0) result[preferred[i].Name]=packed[i]/255f;
        return result;
    }
}
