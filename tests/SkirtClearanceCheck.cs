using System.Reflection;
using System.Numerics;

internal static class SkirtClearanceCheck
{
    internal static void Run(Assembly engine,byte[] original,byte[] output)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        LodWriterCheck.CheckOptionTables(original,output);
        LodWriterCheck.CheckWeights(output);
        var parse=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!.GetMethod("Parse",flags)!;
        var a=parse.Invoke(null,[original])!;var b=parse.Invoke(null,[output])!;
        object Field(object o,string name)=>o.GetType().GetField(name,flags)!.GetValue(o)!;
        int Int(object o,string name)=>Convert.ToInt32(Field(o,name));
        if(((IEnumerable<string>)Field(a,"BoneNames")).Except((IEnumerable<string>)Field(b,"BoneNames")).Any())
            throw new Exception("Clearance removed animation bones");
        var read=engine.GetType("Aetherform.Geometry.SurfaceSkinning",true)!.GetMethod("Read",flags)!;
        var identify=engine.GetType("Aetherform.Geometry.ClothSubdivision",true)!.GetMethods(flags)
            .Single(method=>method.Name=="IdentifyJoins" && method.GetParameters().Length==1);
        var joins=(System.Collections.IDictionary)identify.Invoke(null,[a])!;
        var sewn=new Dictionary<int,HashSet<int>>();
        foreach(object edge in joins.Keys)
        {
            var (mesh,left,right)=((int,int,int))edge;
            if(!sewn.TryGetValue(mesh,out var vertices)) sewn[mesh]=vertices=[];
            vertices.Add(left);vertices.Add(right);
        }
        var meshType=engine.GetType("Aetherform.Geometry.GarmentClearance+Mesh",true)!;
        int moved=0;float worst=0;
        for(int m=Int(a,"Lod0MeshIndex");m<Int(a,"Lod0MeshIndex")+Int(a,"Lod0MeshCount");m++)
        {
            int ha=Int(a,"MeshStart")+36*m,hb=Int(b,"MeshStart")+36*m,count=BitConverter.ToUInt16(original,ha);
            for(int v=0;v<count;v++)
            {
                var before=(Dictionary<string,float>)read.Invoke(null,[original,a,m,v])!;
                var after=(Dictionary<string,float>)read.Invoke(null,[output,b,m,v])!;
                foreach(string bone in before.Keys.Union(after.Keys).Where(n=>n.StartsWith("j_sk_") || n.Contains("skirt",StringComparison.OrdinalIgnoreCase)))
                    if(before.GetValueOrDefault(bone)!=after.GetValueOrDefault(bone))
                        throw new Exception("Movement clearance changed a skirt animation weight");
                if(sewn.GetValueOrDefault(m)?.Contains(v)==true && before.Keys.Union(after.Keys)
                    .Any(n=>before.GetValueOrDefault(n)!=after.GetValueOrDefault(n)))
                    throw new Exception("Movement clearance changed a sewn boundary's body weights");
            }
            var oldDraw=Activator.CreateInstance(meshType,flags,null,[a,m],null)!;
            var newDraw=Activator.CreateInstance(meshType,flags,null,[b,m],null)!;
            var p=(Vector3[])Field(oldDraw,"P");var q=(Vector3[])Field(newDraw,"P");
            var materials=((IEnumerable<string>)Field(a,"MatNames")).ToArray();
            bool skin=(bool)Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!
                .GetMethod("IsBodySkinMaterial",flags)!.Invoke(null,[materials[BitConverter.ToUInt16(original,ha+8)]])!;
            if(skin && !p.SequenceEqual(q)) throw new Exception("Skirt clearance moved body skin");
            var indices=(int[])Field(oldDraw,"Indices");
            for(int face=0;face<indices.Length;face+=3)
            {
                int x=indices[face],y=indices[face+1],z=indices[face+2];
                var old=Vector3.Cross(p[y]-p[x],p[z]-p[x]);var next=Vector3.Cross(q[y]-q[x],q[z]-q[x]);
                if(old.LengthSquared()>1e-8f && Vector3.Dot(old,next)<=0)
                    throw new Exception("Skirt clearance reversed a triangle");
            }
            var shapes=(Dictionary<int,List<int>>)Field(oldDraw,"ShapeTargets");
            foreach(var (basis,variants) in shapes)
                foreach(int v in variants)
                    if(Vector3.Distance(q[v]-q[basis],p[v]-p[basis])>.001f)
                        throw new Exception("Clearance changed an authored shape delta");
            for(int v=0;v<count;v++)
            {
                float delta=Vector3.Distance(p[v],q[v]);
                if(delta>.001f) moved++;
                worst=Math.Max(worst,delta);
            }
        }
        if(moved==0) throw new Exception("Skirt clearance regression did not exercise a correction");
        Console.WriteLine($"Skirt clearance moved {moved} vertices by at most {worst:F3} mm; skirt animation weights, shapes, UVs and options retained.");
    }
}
