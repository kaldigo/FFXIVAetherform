using System.Numerics;
using System.Reflection;

internal static class BodyMotionReferenceCheck
{
    internal static void Run(Assembly engine,IEnumerable<string> models)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        var writer=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!;
        var surfaceType=engine.GetType("Aetherform.Geometry.BodyMotionTransfer+BodySurface",true)!;
        var meshType=engine.GetType("Aetherform.Geometry.GarmentClearance+Mesh",true)!;
        var read=engine.GetType("Aetherform.Geometry.SurfaceSkinning",true)!.GetMethod("Read",flags)!;
        T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,flags)!.GetValue(value)!;
        int Int(object value,string name)=>Convert.ToInt32(value.GetType().GetField(name,flags)!.GetValue(value));
        int covered=0;float worstOldGap=0,worstFixedGap=0;
        foreach(string file in models)
        {
            var bytes=File.ReadAllBytes(file);
            var layout=writer.GetMethod("Parse",flags)!.Invoke(null,[bytes])!;
            object Surface(bool underwear)
            {
                // Fall back to the old constructor so the regression fails on
                // the actual missing-surface behavior before the fix.
                var ctor=surfaceType.GetConstructor(flags,null,[typeof(IEnumerable<byte[]>),typeof(bool)],null);
                return ctor!=null?ctor.Invoke([new[]{bytes},underwear]):
                    Activator.CreateInstance(surfaceType,flags,null,[new[]{bytes}],null)!;
            }
            var source=Surface(true);var skin=Surface(false);
            object Tree(object surface)=>surfaceType.GetField("surface",flags)!.GetValue(surface)!;
            float Gap(object surface,Vector3 point)
            {
                var tree=Tree(surface);var hit=tree.GetType().GetMethod("Nearest",flags)!.Invoke(tree,[point])!;
                return (float)hit.GetType().GetProperty("Distance")!.GetValue(hit)!;
            }
            var materials=Field<List<string>>(layout,"MatNames");
            int start=Int(layout,"Lod0MeshIndex"),count=Int(layout,"Lod0MeshCount");
            var sites=new List<(Vector3 Point,Dictionary<string,float> Weights,bool Skin)>();
            int expectedSkinCorners=0;
            for(int mesh=start;mesh<start+count;mesh++)
            {
                string material=materials[BitConverter.ToUInt16(bytes,Int(layout,"MeshStart")+36*mesh+8)];
                bool isSkin=(bool)writer.GetMethod("IsBodySkinMaterial",flags)!.Invoke(null,[material])!;
                var draw=Activator.CreateInstance(meshType,flags,null,[layout,mesh],null)!;
                var p=Field<Vector3[]>(draw,"P");var ix=Field<int[]>(draw,"Indices");
                if(isSkin)expectedSkinCorners+=ix.Length;
                foreach(int v in ix.Distinct())
                    sites.Add((p[v],(Dictionary<string,float>)read.Invoke(null,[bytes,layout,mesh,v])!,isSkin));
            }
            // The target/family reference must still exclude non-skin surfaces
            // such as piercings; source underwear is a vanilla-only opt-in.
            if(Field<Vector3[]>(Tree(skin),"Points").Length!=expectedSkinCorners)
                throw new Exception("Skin-only motion reference included non-skin triangles");
            foreach(var site in sites.Where(s=>!s.Skin))
            {
                float oldGap=Gap(skin,site.Point);
                if(oldGap<10)continue;
                float fixedGap=Gap(source,site.Point);
                worstOldGap=Math.Max(worstOldGap,oldGap);worstFixedGap=Math.Max(worstFixedGap,fixedGap);
                if(fixedGap>.001f)
                    throw new Exception($"Vanilla motion reference skipped its underwear surface at {site.Point}: {fixedGap:F3} mm gap");
                // Skip coincident vertices with genuinely different authored
                // payloads; an exact nearest-triangle tie has no unique answer.
                bool Equal(Dictionary<string,float> a,Dictionary<string,float> b)=>
                    a.Keys.Union(b.Keys).All(n=>Math.Abs(a.GetValueOrDefault(n)-b.GetValueOrDefault(n))<1e-6f);
                if(sites.Any(s=>s.Point==site.Point && !Equal(s.Weights,site.Weights)))continue;
                var coefficients=(System.Collections.IDictionary)surfaceType.GetMethod("Sample",flags)!.Invoke(source,[site.Point])!;
                var sampled=new Dictionary<string,float>();var moment=Vector3.Zero;
                foreach(System.Collections.DictionaryEntry entry in coefficients)
                {
                    var c=entry.Value!;
                    sampled[(string)entry.Key]=Convert.ToSingle(c.GetType().GetProperty("Mass")!.GetValue(c));
                    moment+=(Vector3)c.GetType().GetProperty("Moment")!.GetValue(c)!;
                }
                if(sampled.Keys.Union(site.Weights.Keys).Any(n=>Math.Abs(sampled.GetValueOrDefault(n)-site.Weights.GetValueOrDefault(n))>1e-5f) ||
                    Vector3.Distance(moment,site.Point)>.002f)
                    throw new Exception("Vanilla underwear motion sample lost its actual bone weights or rest-position moment");
                covered++;
            }
        }
        if(covered<10 || worstOldGap<20)throw new Exception("Motion-reference fixtures did not exercise a substantial covered-body gap");
        Console.WriteLine($"{covered} covered-body samples preserve their native weights and position moments; maximum exposed-skin fallback gap {worstOldGap:F3} -> {worstFixedGap:F6} mm. Skin-only references remain isolated.");
    }
}
