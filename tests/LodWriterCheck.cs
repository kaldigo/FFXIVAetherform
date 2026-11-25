using System.Reflection;
using System.Collections;
using System.Numerics;

internal static class LodWriterCheck
{
    internal static void CheckHeldEnds(Assembly engine,byte[] original,byte[] emitted,string slot)
    {
        if(slot is not ("_glv" or "_sho")) return;
        var proteus=Assembly.Load("Proteus");
        var reader=proteus.GetType("Proteus.Services.ModelPartReader",true)!.GetMethod("Read")!;
        var source=reader.Invoke(null,[original])!;var target=reader.Invoke(null,[emitted])!;
        var keepEnds=proteus.GetType("Proteus.Services.KeepEnds",true)!.GetMethods(BindingFlags.Static|BindingFlags.NonPublic)
            .Single(m=>m.Name=="Read" && m.GetParameters().Length==3);
        var reading=keepEnds.Invoke(null,[slot,source,original])!;
        var held=(IReadOnlySet<int>)reading.GetType().GetProperty("Ends")!.GetValue(reading)!;
        var before=(float[])source.GetType().GetProperty("Positions")!.GetValue(source)!;
        var after=(float[])target.GetType().GetProperty("Positions")!.GetValue(target)!;
        IEnumerable<object> Spans(object model)=>(model.GetType().GetProperty("MeshSpans")!.GetValue(model) as IEnumerable)!.Cast<object>();
        int SpanValue(object span,string name)=>Convert.ToInt32(span.GetType().GetProperty(name)!.GetValue(span));
        var targetStarts=Spans(target).ToDictionary(s=>SpanValue(s,"Mesh"),s=>SpanValue(s,"BaseVertex"));
        var spans=Spans(source).ToArray();
        foreach(int vertex in held)
        {
            var span=spans.Single(s=>vertex>=SpanValue(s,"BaseVertex") && vertex<SpanValue(s,"BaseVertex")+SpanValue(s,"Count"));
            int mapped=targetStarts[SpanValue(span,"Mesh")]+vertex-SpanValue(span,"BaseVertex");
            for(int axis=0;axis<3;axis++)
                if(Math.Abs(before[3*vertex+axis]-after[3*mapped+axis])>1e-6f)
                    throw new Exception($"Refit changed hand/foot vertex {vertex}, axis {axis}, by {1000*(after[3*mapped+axis]-before[3*vertex+axis])} mm");
        }
        var parse=proteus.GetType("Proteus.Services.SecondSkinWriter",true)!.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!;
        var oldLayout=parse.Invoke(null,[original]);var newLayout=parse.Invoke(null,[emitted]);
        var weights=engine.GetType("Aetherform.Geometry.SurfaceSkinning",true)!.GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var span in (IEnumerable)source.GetType().GetProperty("MeshSpans")!.GetValue(source)!)
        {
            int mesh=(int)span.GetType().GetProperty("Mesh")!.GetValue(span)!;
            int start=(int)span.GetType().GetProperty("BaseVertex")!.GetValue(span)!;
            int count=(int)span.GetType().GetProperty("Count")!.GetValue(span)!;
            foreach(int vertex in held.Where(v=>v>=start && v<start+count))
            {
                var a=(Dictionary<string,float>)weights.Invoke(null,[original,oldLayout,mesh,vertex-start])!;
                var b=(Dictionary<string,float>)weights.Invoke(null,[emitted,newLayout,mesh,vertex-start])!;
                if(a.Keys.Union(b.Keys).Any(bone=>Math.Abs(a.GetValueOrDefault(bone)-b.GetValueOrDefault(bone))>1e-6f))
                    throw new Exception("Refit changed authored hand or foot weights");
            }
        }
        Console.WriteLine($"{held.Count} hand/foot vertices retain their positions and weights.");
    }

    internal static void CheckOptionTables(byte[] original,byte[] emitted,bool refined=false)
    {
        var type=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!;
        object Parse(byte[] bytes)=>type.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bytes])!;
        object Field(object model,string name)=>model.GetType().GetField(name)!.GetValue(model)!;
        int Get(object model,string name)=>Convert.ToInt32(Field(model,name));
        var source=Parse(original);var target=Parse(emitted);
        if(!((string[])Field(source,"AttrNames")).SequenceEqual((string[])Field(target,"AttrNames")))
            throw new Exception("Refit changed the option attribute table");
        if(!((IEnumerable<string>)Field(source,"MatNames")).SequenceEqual((IEnumerable<string>)Field(target,"MatNames")))
            throw new Exception("Refit changed source material paths used by texture or shader options");
        var sourceMeshes=new Dictionary<uint,int>();var targetMeshes=new Dictionary<uint,int>();
        int count=Get(source,"Lod0MeshCount");
        if(count!=Get(target,"Lod0MeshCount")) throw new Exception("Refit changed the number of option-bearing meshes");
        for(int ordinal=0;ordinal<count;ordinal++)
        {
            int a=Get(source,"MeshStart")+36*(Get(source,"Lod0MeshIndex")+ordinal);
            int b=Get(target,"MeshStart")+36*(Get(target,"Lod0MeshIndex")+ordinal);
            sourceMeshes.Add(BitConverter.ToUInt32(original,a+16),ordinal);
            targetMeshes.Add(BitConverter.ToUInt32(emitted,b+16),ordinal);
            if(BitConverter.ToUInt16(original,a+8)!=BitConverter.ToUInt16(emitted,b+8))
                throw new Exception("Refit changed an option-bearing mesh's material assignment");
            int vertices=BitConverter.ToUInt16(original,a);
            int nextVertices=BitConverter.ToUInt16(emitted,b);
            if(refined ? nextVertices<vertices : nextVertices!=vertices) throw new Exception("Refit changed option-bearing vertex identities");
            int oldDecl=0x44+136*(Get(source,"Lod0MeshIndex")+ordinal);
            int newDecl=0x44+136*(Get(target,"Lod0MeshIndex")+ordinal);
            var elements=Enumerable.Range(0,17).Select(i=>oldDecl+8*i).TakeWhile(e=>original[e]!=255).ToArray();
            foreach(int element in elements.Where(e=>original[e+3] is 4 or 7))
            {
                int replacement=newDecl+element-oldDecl,stream=original[element],offset=original[element+1];
                int end=elements.Where(e=>original[e]==stream && original[e+1]>offset).Select(e=>(int)original[e+1])
                    .DefaultIfEmpty(original[a+32+stream]).Min();
                int oldBuffer=Get(source,"Vb")+BitConverter.ToInt32(original,a+20+4*stream)+offset;
                int newStream=emitted[replacement];
                int newBuffer=Get(target,"Vb")+BitConverter.ToInt32(emitted,b+20+4*newStream)+emitted[replacement+1];
                for(int vertex=0;vertex<vertices;vertex++)
                    if(!original.AsSpan(oldBuffer+vertex*original[a+32+stream],end-offset)
                        .SequenceEqual(emitted.AsSpan(newBuffer+vertex*emitted[b+32+newStream],end-offset)))
                        throw new Exception("Refit changed texture coordinates or vertex colors used by source options");
            }
            int n=BitConverter.ToUInt16(original,a+12);
            if(n!=BitConverter.ToUInt16(emitted,b+12)) throw new Exception("Refit dropped a visibility-controlled submesh");
            for(int sub=0;sub<n;sub++)
            {
                int oldSub=Get(source,"SubmeshStart")+16*(BitConverter.ToUInt16(original,a+10)+sub);
                int newSub=Get(target,"SubmeshStart")+16*(BitConverter.ToUInt16(emitted,b+10)+sub);
                if(BitConverter.ToUInt32(original,oldSub+8)!=BitConverter.ToUInt32(emitted,newSub+8))
                    throw new Exception("Refit changed an option's submesh visibility mask");
            }
        }
        SortedDictionary<string,string> Shapes(object model,Dictionary<uint,int> meshes)
        {
            var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach(DictionaryEntry shape in (IDictionary)Field(model,"Shapes"))
            foreach(var entry in (IEnumerable)shape.Value!)
            {
                uint offset=(uint)entry.GetType().GetProperty("MeshIndexOffset")!.GetValue(entry)!;
                if(!meshes.TryGetValue(offset,out int mesh)) continue;
                var values=((ushort,ushort)[])entry.GetType().GetProperty("Values")!.GetValue(entry)!;
                result.Add(shape.Key+"/"+mesh,string.Join(',',values.Select(v=>$"{v.Item1}>{v.Item2}")));
            }
            return result;
        }
        if(!refined && !Shapes(source,sourceMeshes).SequenceEqual(Shapes(target,targetMeshes)))
            throw new Exception("Refit changed shape replacement indices");
        if(refined)
        {
            // Original corners keep their authored shape replacements, even
            // though subdivision changes their index-buffer positions.
            SortedSet<string> OriginalCorners(object model,byte[] bytes,Dictionary<uint,int> meshes)
            {
                var result=new SortedSet<string>(StringComparer.Ordinal);
                foreach(DictionaryEntry shape in (IDictionary)Field(model,"Shapes"))
                foreach(var entry in (IEnumerable)shape.Value!)
                {
                    uint offset=(uint)entry.GetType().GetProperty("MeshIndexOffset")!.GetValue(entry)!;
                    if(!meshes.TryGetValue(offset,out int ordinal)) continue;
                    int sourceHeader=Get(source,"MeshStart")+36*(Get(source,"Lod0MeshIndex")+ordinal);
                    int originalCount=BitConverter.ToUInt16(original,sourceHeader);
                    foreach(var (at,replacement) in ((ushort,ushort)[])entry.GetType().GetProperty("Values")!.GetValue(entry)!)
                    {
                        int basis=BitConverter.ToUInt16(bytes,Get(model,"Ib")+2*((int)offset+at));
                        if(basis<originalCount) result.Add($"{shape.Key}/{ordinal}/{basis}/{replacement}");
                    }
                }
                return result;
            }
            if(!OriginalCorners(source,original,sourceMeshes).SetEquals(OriginalCorners(target,emitted,targetMeshes)))
                throw new Exception("Refinement lost an authored corner's shape replacement");
            var sourceNames=((IDictionary)Field(source,"Shapes")).Keys.Cast<string>().Order();
            var targetNames=((IDictionary)Field(target,"Shapes")).Keys.Cast<string>().Order();
            if(!sourceNames.SequenceEqual(targetNames)) throw new Exception("Refinement changed named shape options");
        }
    }

    internal static void CheckWeights(byte[] mdl)
    {
        var type=Assembly.Load("Proteus").GetType("Proteus.Services.SecondSkinWriter",true)!;
        var model=type.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[mdl])!;
        object Field(string name)=>model.GetType().GetField(name)!.GetValue(model)!;
        int Get(string name)=>Convert.ToInt32(Field(name));
        var palettes=(ushort[][])Field("BoneTables");
        var bones=(string[])Field("BoneNames");
        int U(int at)=>BitConverter.ToUInt16(mdl,at);
        int I(int at)=>BitConverter.ToInt32(mdl,at);
        for(int mesh=Get("Lod0MeshIndex");mesh<Get("Lod0MeshIndex")+Get("Lod0MeshCount");mesh++)
        {
            int h=Get("MeshStart")+36*mesh,decl=0x44+136*mesh;
            int Find(byte usage)=>Enumerable.Range(0,17).Select(i=>decl+8*i).First(i=>mdl[i]!=255 && mdl[i+3]==usage);
            int w=Find(1),b=Find(2),width=mdl[w+2]==17?8:4;
            int At(int e,int v)=>Get("Vb")+I(h+20+4*mdl[e])+v*mdl[h+32+mdl[e]]+mdl[e+1];
            var palette=palettes[U(h+14)];
            for(int vertex=0;vertex<U(h);vertex++)
            {
                int total=0;
                for(int j=0;j<width;j++)
                {
                    int weight=mdl[At(w,vertex)+j];total+=weight;
                    if(weight==0) continue;
                    int local=mdl[At(b,vertex)+j];
                    if(local>=palette.Length || palette[local]>=bones.Length)
                        throw new Exception($"Invalid bone reference in mesh {mesh}, vertex {vertex}");
                }
                if(total!=0 && total!=255) throw new Exception($"Unnormalized weights in mesh {mesh}, vertex {vertex}");
            }
        }
    }

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
