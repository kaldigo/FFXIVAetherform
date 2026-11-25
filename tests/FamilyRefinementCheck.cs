using System.Reflection;
using System.Numerics;

internal static class FamilyRefinementCheck
{
    internal static void CheckSurface(Assembly engine,byte[] source,Dictionary<string,string> targets)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        var density=Activator.CreateInstance(engine.GetType("Aetherform.Geometry.BodyDensity",true)!,flags,null,
            [targets.Where(p=>p.Key is "_top" or "_dwn").Select(p=>File.ReadAllBytes(p.Value))],null)!;
        var originsType=engine.GetType("Aetherform.Geometry.ClothSubdivision+Origins",true)!;
        var lineage=(System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int),originsType))!;
        var output=(byte[])engine.GetType("Aetherform.Geometry.DensitySubdivision",true)!.GetMethod("Apply",flags)!
            .Invoke(null,[source,source,density,lineage,null,null])!;
        LodWriterCheck.CheckOptionTables(source,output,true);
        var parser=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!.GetMethod("Parse",flags)!;
        var before=parser.Invoke(null,[source])!;var after=parser.Invoke(null,[output])!;
        var mesh=engine.GetType("Aetherform.Geometry.GarmentClearance+Mesh",true)!;
        object Draw(object model,int id)=>Activator.CreateInstance(mesh,flags,null,[model,id],null)!;
        T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,flags)!.GetValue(obj)!;
        int children=0;
        foreach(System.Collections.DictionaryEntry entry in lineage)
        {
            var a=Draw(before,(int)entry.Key);var b=Draw(after,(int)entry.Key);
            var oldP=Field<Vector3[]>(a,"P");var newP=Field<Vector3[]>(b,"P");
            var oldIX=Field<int[]>(a,"Indices");var newIX=Field<int[]>(b,"Indices");
            var parents=(int[])originsType.GetProperty("Parents")!.GetValue(entry.Value)!;
            for(int i=0;i<oldP.Length;i++) if(oldP[i]!=newP[i]) throw new Exception("Refinement moved an authored source vertex");
            var areas=new double[oldIX.Length/3];
            for(int i=0;i<parents.Length;i++)
            {
                int parent=parents[i];
                var u=oldP[oldIX[3*parent]];var v=oldP[oldIX[3*parent+1]];var w=oldP[oldIX[3*parent+2]];
                var ab=v-u;var ac=w-u;var normal=Vector3.Cross(ab,ac);
                double area=normal.Length();
                if(area<1e-6) continue;
                double aa=Vector3.Dot(ab,ab),bb=Vector3.Dot(ab,ac),cc=Vector3.Dot(ac,ac),det=aa*cc-bb*bb;
                if(det<1e-6) continue;
                for(int k=0;k<3;k++)
                {
                    var delta=newP[newIX[3*i+k]]-u;
                    double x=Vector3.Dot(delta,ab),y=Vector3.Dot(delta,ac);
                    double s=(cc*x-bb*y)/det,t=(aa*y-bb*x)/det;
                    if(Math.Abs(Vector3.Dot(delta,normal)/area)>.001 || s<-.001 || t<-.001 || s+t>1.001)
                        throw new Exception("A refined vertex left its authored source triangle");
                }
                var x0=newP[newIX[3*i]];var x1=newP[newIX[3*i+1]];var x2=newP[newIX[3*i+2]];
                var child=Vector3.Cross(x1-x0,x2-x0);
                if(Vector3.Dot(normal,child)<-1e-5) throw new Exception("Refinement flipped a source triangle");
                areas[parent]+=child.Length();children++;
            }
            for(int i=0;i<areas.Length;i++)
            {
                var normal=Vector3.Cross(oldP[oldIX[3*i+1]]-oldP[oldIX[3*i]],oldP[oldIX[3*i+2]]-oldP[oldIX[3*i]]);
                double area=normal.Length();
                if(area>.01 && Math.Abs(areas[i]-area)>.001*area+.01)
                    throw new Exception("Refinement changed a source triangle's covered area");
            }
        }
        if(children==0) throw new Exception("Refinement test did not add geometry");
        Console.WriteLine($"{children} refined faces remain on their authored source surface, with winding, area, UVs and option controls preserved.");
    }

    internal static void Run(Assembly engine)
    {
        var proteus=Assembly.Load("Proteus");
        var fieldType=proteus.GetType("Proteus.Services.SecondSkinWriter+VElem",true)!;
        var fields=Array.CreateInstance(fieldType,7);
        byte[,] layout={{0,2,0},{12,2,3},{24,1,4},{32,8,7},{36,3,5},{52,17,1},{60,17,2}};
        for(int i=0;i<7;i++) fields.SetValue(Activator.CreateInstance(fieldType,[(byte)0,layout[i,0],layout[i,1],layout[i,2],(byte)0]),i);
        const int stride=68;
        var stream=new byte[3*stride];
        void Float(int vertex,int offset,float value)=>BitConverter.TryWriteBytes(stream.AsSpan(vertex*stride+offset),value);
        for(int i=0;i<3;i++)
        {
            Float(i,0,i==1?2:0);Float(i,4,i==2?2:0);
            Float(i,12+4*i,1); // Authored replacement normals differ at each corner.
            Float(i,24,10+2*i);Float(i,28,20+4*i);
            stream[i*stride+32]=(byte)(40+40*i);stream[i*stride+35]=255;
            Float(i,36,1);Float(i,48,i==1?-1:1);
            stream[i*stride+52]=255;stream[i*stride+60]=(byte)(i+1);
        }
        var row=(byte[])engine.GetType("Aetherform.Geometry.PanelStreams",true)!.GetMethod("ShapeRow",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[new byte[][]{stream,[],[]},new[]{stride,0,0},fields,new[]{0,1,2},new[]{.25,.5,.25}])!;
        float Read(int offset)=>BitConverter.ToSingle(row,offset);
        if(Math.Abs(Read(0)-1)>1e-6 || Math.Abs(Read(4)-.5)>1e-6 || Math.Abs(Read(24)-12)>1e-6 || Math.Abs(Read(28)-24)>1e-6)
            throw new Exception("Refined shape did not interpolate authored positions and UVs");
        var expected=Vector3.Normalize(new(.25f,.5f,.25f));
        if(Vector3.Distance(expected,new(Read(12),Read(16),Read(20)))>1e-6 || Read(48)!=-1 || row[32]!=80 || row[35]!=255)
            throw new Exception("Refined shape lost its normal, tangent handedness or colour");
        if(row.Skip(52).Take(8).Sum(b=>(int)b)!=255 || row[60]!=2 || row[52]!=128 || row[61]!=1 || row[62]!=3)
            throw new Exception("Refined shape lost or denormalized its authored skin weights");
        Console.WriteLine("Refined shape positions, UVs, normals, colours, tangent handedness and eight-weight interpolation passed.");
    }
}
