using System.Reflection;
using System.Collections;
using System.Numerics;

internal static class LodWriterCheck
{
    internal static void CheckJoins(Assembly assembly,byte[] mdl,IDictionary expected)
    {
        var type=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!;
        var model=type.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[mdl])!;
        object Field(string name)=>model.GetType().GetField(name)!.GetValue(model)!;
        int Get(string name)=>Convert.ToInt32(Field(name));
        if (BitConverter.ToUInt16(mdl,14) != Get("MatCount")) throw new InvalidDataException("File and model material counts disagree.");
        var points=new Dictionary<(int X,int Y,int Z,string Weights),int>();
        var names=(List<string>)Field("MatNames");var bones=(string[])Field("BoneNames");
        var palettes=(ushort[][])Field("BoneTables");
        int U(int at)=>BitConverter.ToUInt16(mdl,at);
        int I(int at)=>BitConverter.ToInt32(mdl,at);
        for(int m=Get("Lod0MeshIndex");m<Get("Lod0MeshIndex")+Get("Lod0MeshCount");m++)
        {
            int h=Get("MeshStart")+36*m;int flags=names[U(h+8)].EndsWith("_bibo.mtrl")?1:2;
            int decl=0x44+136*m;
            int Find(byte usage)=>Enumerable.Range(0,17).Select(i=>decl+8*i).First(i=>mdl[i]!=255 && mdl[i+3]==usage);
            int p=Find(0),w=Find(1),b=Find(2),palette=U(h+14);int width=mdl[w+2]==17?8:4;
            int At(int e,int v)=>Get("Vb")+I(h+20+4*mdl[e])+v*mdl[h+32+mdl[e]]+mdl[e+1];
            var used=Enumerable.Range(0,I(h+4)).Select(i=>U(Get("Ib")+2*(I(h+16)+i))).Distinct();
            foreach(int v in used)
            {
                int pp=At(p,v);float F(int k)=>mdl[p+2]==14?(float)BitConverter.ToHalf(mdl,pp+2*k):BitConverter.ToSingle(mdl,pp+4*k);
                var q=1000*new Vector3(F(0),F(1),F(2));
                var packed=new SortedDictionary<string,int>(StringComparer.Ordinal);
                for(int j=0;j<width;j++)
                {
                    int weight=mdl[At(w,v)+j];if(weight==0) continue;
                    string bone=bones[palettes[palette][mdl[At(b,v)+j]]];packed[bone]=packed.GetValueOrDefault(bone)+weight;
                }
                var key=((int)MathF.Round(q.X*1000),(int)MathF.Round(q.Y*1000),(int)MathF.Round(q.Z*1000),string.Join('|',packed.Select(p=>p.Key+"="+p.Value)));
                points[key]=points.GetValueOrDefault(key)|flags;
            }
        }
        foreach(DictionaryEntry item in expected)
        {
            var knot=item.Value!;
            var pos=(Vector3)knot.GetType().GetProperty("PositionMm")!.GetValue(knot)!;
            var influences=(IEnumerable)knot.GetType().GetProperty("Weights")!.GetValue(knot)!;
            string weights=string.Join('|',influences.Cast<object>().Select(p=>p.GetType().GetProperty("Bone")!.GetValue(p)+"="+p.GetType().GetProperty("Weight")!.GetValue(p)));
            int x=(int)MathF.Round(pos.X*1000),y=(int)MathF.Round(pos.Y*1000),z=(int)MathF.Round(pos.Z*1000);
            bool found=false;
            for(int a=-1;a<=1;a++)for(int b=-1;b<=1;b++)for(int c=-1;c<=1;c++)
                found|=points.GetValueOrDefault((x+a,y+b,z+c,weights))==3;
            if(!found) throw new Exception($"Emitted cloth and AF skin disagree at shared knot {item.Key} ({pos}) using {weights}.");
        }
    }
    internal static void Run(Assembly solver,byte[] mdl)
    {
        var writer=solver.GetType("Aetherform.Geometry.LodMeshWriter",true)!;
        var drawType=writer.GetNestedType("Draw",BindingFlags.NonPublic)!;
        var replace=writer.GetMethod("Replace",BindingFlags.Static|BindingFlags.NonPublic)!;
        var actual=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(drawType))!;
        var parser=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!
            .GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!;
        object Parse(byte[] b)=>parser.Invoke(null,[b])!;
        int U(byte[] b,int at)=>BitConverter.ToUInt16(b,at);
        int I(byte[] b,int at)=>BitConverter.ToInt32(b,at);
        (int[] Layout, Dictionary<string,(ushort,ushort)[]>[] Shapes) Read(byte[] b)
        {
            var model=Parse(b);
            int Get(string name)=>Convert.ToInt32(model.GetType().GetField(name)!.GetValue(model));
            int mh=Get("Mh"),s=Get("ShapeBlock"),n=U(b,mh+16),ms=s+16*n,vs=ms+12*U(b,mh+18);
            var dict=Enumerable.Range(0,3).Select(_=>new Dictionary<string,(ushort,ushort)[]>()).ToArray();
            for(int j=0;j<n;j++)
            {
                int h=s+16*j;
                int offset=Get("StrBlock")+I(b,h);
                string name=System.Text.Encoding.UTF8.GetString(b,offset,Array.IndexOf(b,(byte)0,offset)-offset);
                for(int l=0;l<3;l++)
                for(int k=0;k<U(b,h+10+2*l);k++)
                {
                    int record=ms+12*(U(b,h+4+2*l)+k);
                    int v=vs+4*I(b,record+8);
                    var vals=Enumerable.Range(0,I(b,record+4)).Select(i=>((ushort)U(b,v+4*i),(ushort)U(b,v+4*i+2))).ToArray();
                    dict[l].Add(name+"/"+I(b,record),vals);
                }
            }
            return ([Get("MeshStart"),Get("SubmeshStart"),Get("Vb"),Get("Ib"),Get("Lod0MeshIndex"),Get("Lod0MeshCount")],dict);
        }
        var (src,shapes)=Read(mdl);
        for(int m=src[4];m<src[4]+src[5];m++)
        {
            int h=src[0]+36*m,v=U(mdl,h),f=I(mdl,h+16);
            var streams=Enumerable.Range(0,3).Select(s=>mdl.AsSpan(src[2]+I(mdl,h+20+4*s),v*mdl[h+32+s]).ToArray()).ToArray();
            var ix=Enumerable.Range(0,I(mdl,h+4)).Select(i=>(ushort)U(mdl,src[3]+2*(i+f))).ToArray();
            var sub=Enumerable.Range(U(mdl,h+10),U(mdl,h+12)).Select(i=>(I(mdl,src[1]+16*i)-f,I(mdl,src[1]+16*i+4))).ToArray();
            var keys=shapes[0].Where(pair=>pair.Key.EndsWith("/"+f)).ToDictionary(p=>p.Key[..p.Key.LastIndexOf('/')],p=>p.Value);
            actual.Add(Activator.CreateInstance(drawType,[m,v,streams,ix,sub,keys,null,null]));
        }
        var emitted=(byte[])replace.Invoke(null,[mdl,actual])!;
        var (dst,newShapes)=Read(emitted);
        for(int m=src[4];m<src[4]+src[5];m++)
        {
            int h=src[0]+36*m,k=dst[0]+36*m,oldFirst=I(mdl,h+16),first=I(emitted,k+16);
            for(int s=0;s<3;s++)
            {
                int size=U(mdl,h)*mdl[h+32+s];
                if(!mdl.AsSpan(src[2]+I(mdl,h+20+4*s),size).SequenceEqual(emitted.AsSpan(dst[2]+I(emitted,k+20+4*s),size)))
                    throw new Exception("Changed vertex payload.");
            }
            if(!mdl.AsSpan(src[3]+2*oldFirst,2*I(mdl,h+4)).SequenceEqual(emitted.AsSpan(dst[3]+2*first,2*I(emitted,k+4))))
                throw new Exception("Changed index payload.");
            foreach(var p in shapes[0].Where(p=>p.Key.EndsWith("/"+oldFirst)))
                if(!p.Value.SequenceEqual(newShapes[0][p.Key[..p.Key.LastIndexOf('/')]+"/"+first])) throw new Exception("Lost LOD0 shape.");
        }
        for(int lod=1;lod<mdl[64];lod++)
        {
            foreach(var (at,size) in new[]{(16,40),(28,52)})
                if(!mdl.AsSpan(I(mdl,at+4*lod),I(mdl,size+4*lod)).SequenceEqual(emitted.AsSpan(I(emitted,at+4*lod),I(emitted,size+4*lod))))
                    throw new Exception("Changed lower LOD buffer.");
            foreach(var p in shapes[lod]) if(!p.Value.SequenceEqual(newShapes[lod][p.Key])) throw new Exception("Changed lower LOD shape.");
        }
    }
}
