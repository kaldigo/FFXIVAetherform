using System.Reflection;
using System.Numerics;

internal static class RetainedSkinMotionCheck
{
    internal static void Run(Assembly engine,byte[] source,byte[] output,byte[] baseline)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        var writer=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!;
        object Parse(byte[] bytes)=>writer.GetMethod("Parse",flags)!.Invoke(null,[bytes])!;
        var a=Parse(source);var b=Parse(output);var before=Parse(baseline);
        T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,flags)!.GetValue(obj)!;
        int Int(object obj,string name)=>Convert.ToInt32(obj.GetType().GetField(name,flags)!.GetValue(obj));
        var meshType=engine.GetType("Aetherform.Geometry.GarmentClearance+Mesh",true)!;
        object Draw(object model,int mesh)=>Activator.CreateInstance(meshType,flags,null,[model,mesh],null)!;
        var read=engine.GetType("Aetherform.Geometry.SurfaceSkinning",true)!.GetMethod("Read",flags)!;
        Dictionary<string,int> Weights(byte[] bytes,object model,int mesh,int vertex)=>
            ((Dictionary<string,float>)read.Invoke(null,[bytes,model,mesh,vertex])!).ToDictionary(p=>p.Key,p=>(int)MathF.Round(255*p.Value));
        bool Equal(Dictionary<string,int> x,Dictionary<string,int> y)=>x.Keys.Union(y.Keys).All(n=>x.GetValueOrDefault(n)==y.GetValueOrDefault(n));
        int start=Int(a,"Lod0MeshIndex"),count=Int(a,"Lod0MeshCount");
        var materials=Field<List<string>>(a,"MatNames");
        bool Skin(int mesh)=>(bool)writer.GetMethod("IsBodySkinMaterial",flags)!
            .Invoke(null,[materials[BitConverter.ToUInt16(source,Int(a,"MeshStart")+36*mesh+8)]])!;
        var draws=Enumerable.Range(start,count).ToDictionary(m=>m,m=>Draw(a,m));
        var contacts=new List<(int Mesh,int Vertex,Vector3 Point,Dictionary<string,int> Weights)>();
        foreach(int m in Enumerable.Range(start,count).Where(m=>!Skin(m)))
        foreach(int v in Field<int[]>(draws[m],"Indices").Distinct())
            contacts.Add((m,v,Field<Vector3[]>(draws[m],"P")[v],Weights(source,a,m,v)));
        int pairs=0;double oldWorst=0,newWorst=0;bool converted=false;
        foreach(int m in Enumerable.Range(start,count).Where(Skin))
        {
            var p=Field<Vector3[]>(draws[m],"P");var q=Field<Vector3[]>(Draw(b,m),"P");
            if(p.Length!=q.Length) throw new Exception("Retained-skin fixture changed its skin topology");
            foreach(int v in Field<int[]>(draws[m],"Indices").Distinct())
            {
                var authored=Weights(source,a,m,v);
                if(!authored.Keys.Any(n=>n.Contains("mune",StringComparison.Ordinal))) continue;
                var contact=contacts.MinBy(c=>Vector3.DistanceSquared(c.Point,p[v]));
                if(Vector3.Distance(contact.Point,p[v])>1 || !Equal(authored,contact.Weights)) continue;
                var skin=Weights(output,b,m,v);var cloth=Weights(output,b,contact.Mesh,contact.Vertex);
                if(!Equal(skin,cloth)) throw new Exception("Retained breast skin and its authored co-moving clothing acquired different motion weights");
                converted|=skin.Keys.Except(authored.Keys).Any();pairs++;
                double Stress(byte[] bytes,object model)
                {
                    var left=Weights(bytes,model,m,v);var right=Weights(bytes,model,contact.Mesh,contact.Vertex);
                    var delta=Field<Vector3[]>(Draw(model,m),"P")[v]-Field<Vector3[]>(Draw(model,contact.Mesh),"P")[contact.Vertex];
                    return left.Keys.Union(right.Keys).SelectMany(n=>new[]{-1,1}.Select(sign=>
                        (delta+Vector3.UnitZ*(sign*10*(left.GetValueOrDefault(n)-right.GetValueOrDefault(n))/255f)).Length())).Max();
                }
                oldWorst=Math.Max(oldWorst,Stress(baseline,before));newWorst=Math.Max(newWorst,Stress(output,b));
            }
        }
        if(pairs<3 || !converted || oldWorst<=newWorst+.1) throw new Exception("Retained-skin regression did not exercise the old breast/clothing mismatch");
        LodWriterCheck.CheckOptionTables(baseline,output);
        foreach(int m in Enumerable.Range(start,count))
            if(!Field<Vector3[]>(Draw(before,m),"P").SequenceEqual(Field<Vector3[]>(Draw(b,m),"P")))
                throw new Exception("The retained-skin motion correction changed neutral geometry");
        Console.WriteLine($"{pairs} retained breast/cloth contacts share packed weights; synthetic ±10 mm bone-translation gap {oldWorst:F3} -> {newWorst:F3} mm; neutral geometry, UVs and options unchanged.");
    }
}
