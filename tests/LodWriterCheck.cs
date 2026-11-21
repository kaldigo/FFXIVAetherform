using System.Reflection;

internal static class LodWriterCheck
{
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
