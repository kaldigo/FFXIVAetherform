using System.Numerics;
using Proteus.Services;
using E=Proteus.Services.SecondSkinWriter.VElem;
using Source=Proteus.Services.SecondSkinWriter.Source;

namespace Aetherform.Geometry;

internal static class ClothSubdivision
{
    private static int I(byte[] b,int o)=>BitConverter.ToInt32(b,o);
    private static int U(byte[] b,int o)=>BitConverter.ToUInt16(b,o);
    private static void W(byte[] b,int o,int value)=>BitConverter.TryWriteBytes(b.AsSpan(o),value);
    private static void Short(byte[] b,int o,int value)=>BitConverter.TryWriteBytes(b.AsSpan(o),checked((ushort)value));

    private sealed class Layout(Source src,int index)
    {
        internal readonly byte[] Data=src.S;
        internal readonly int Header=src.MeshStart+index*36;
        internal readonly E[] Fields=src.Decls[index];
        internal readonly string Material=src.MatNames[U(src.S,src.MeshStart+index*36+8)];
        internal int Count=>U(Data,Header);
        internal int First=>I(Data,Header+16);
        internal int Stride(int s)=>Data[Header+32+s];
        internal int Stream(int s)=>src.Vb+I(Data,Header+20+4*s);
        private ushort[] Palette => src.BoneTables[U(Data,Header+14)];
        internal void RestoreWeights(int vertex,Layout original)
        {
            var w=Fields.FirstOrDefault(e=>e.Usage==1); var j=Fields.FirstOrDefault(e=>e.Usage==2);
            var sw=original.Fields.FirstOrDefault(e=>e.Usage==1); var sj=original.Fields.FirstOrDefault(e=>e.Usage==2);
            if(w.Usage!=1 || j.Usage!=2 || sw.Usage!=1 || sj.Usage!=2 ||
                !PanelStreams.Supported(w) || !PanelStreams.Supported(j) ||
                !PanelStreams.Supported(sw) || !PanelStreams.Supported(sj)) return;
            int Offset(Layout layout,E field)=>layout.Stream(field.Stream)+field.Offset+vertex*layout.Stride(field.Stream);
            int width=w.Type==17?8:4, sourceWidth=sw.Type==17?8:4;
            if((j.Type==17)!=(w.Type==17) || (sj.Type==17)!=(sw.Type==17)) return;
            Span<byte> weights=stackalloc byte[8]; Span<byte> bones=stackalloc byte[8];
            weights.Clear(); bones.Clear();
            int count=0;
            for(int k=0;k<sourceWidth;k++)
            {
                byte strength=original.Data[Offset(original,sw)+k];
                if(strength==0) continue;
                int local=original.Data[Offset(original,sj)+k];
                if(local>=original.Palette.Length || count==width) return;
                string name=original.BoneName(local);
                int mapped=Array.FindIndex(Palette,b=>src.BoneNames[b]==name);
                if(mapped is <0 or >255) return;
                weights[count]=strength; bones[count++]=(byte)mapped;
            }
            if(count==0) return;
            weights[..width].CopyTo(Data.AsSpan(Offset(this,w)));
            bones[..width].CopyTo(Data.AsSpan(Offset(this,j)));
        }
        private string BoneName(int local)=>src.BoneNames[Palette[local]];
        internal ushort[] Indices()
        {
            var values=new ushort[I(Data,Header+4)];
            Buffer.BlockCopy(Data,src.Ib+2*First,values,0,values.Length*2);
            return values;
        }
        internal Vector3[] Positions(int count)
        {
            var pos=Fields.First(e=>e.Usage==0);
            var points=new Vector3[count];
            Span<float> v=stackalloc float[4];
            for(int i=0;i<count;i++)
            {
                SecondSkinWriter.ReadTyped(Data,Stream(pos.Stream)+Stride(pos.Stream)*i+pos.Offset,pos.Type,v);
                points[i]=new(v[0],v[1],v[2]);
            }
            return points;
        }
    }

    // A welded island carried entirely by one bone is rigid. Keep its authored
    // triangles, including when the same material also contains flexible panels.
    private static bool[] Flexible(Layout mesh,ushort[] indices,Vector3[] positions,E weight,E bone)
    {
        int[] root=Enumerable.Range(0,mesh.Count).ToArray();
        int Find(int a) { while(root[a]!=a) { root[a]=root[root[a]]; a=root[a]; } return a; }
        var seams=new Dictionary<Vector3,int>();
        foreach(int i in indices)
        {
            if(seams.TryGetValue(positions[i],out int j)) root[Find(i)]=Find(j);
            else seams[positions[i]]=i;
        }
        for(int i=0;i<indices.Length;i+=3)
        {
            root[Find(indices[i+1])]=Find(indices[i]);
            root[Find(indices[i+2])]=Find(indices[i]);
        }
        var bones=new Dictionary<int,HashSet<byte>>();
        foreach(int i in indices.Distinct())
        {
            int group=Find(i);
            if(!bones.TryGetValue(group,out var set)) bones[group]=set=[];
            for(int j=0;j<(weight.Type==17?8:4);j++)
                if(mesh.Data[mesh.Stream(weight.Stream)+weight.Offset+i*mesh.Stride(weight.Stream)+j]>0)
                    set.Add(mesh.Data[mesh.Stream(bone.Stream)+bone.Offset+i*mesh.Stride(bone.Stream)+j]);
        }
        return indices.Chunk(3).Select(t=>bones[Find(t[0])].Count>1).ToArray();
    }

    // Keep the cloth and skin edge identical under animation. A midpoint with
    // blended weights generally moves off the straight edge of the other mesh.
    internal static int[] Children(int a,int b,int c,int ab,int bc,int ca)
    {
        if(ab>=0 && bc>=0 && ca>=0) return [a,ab,ca, ab,b,bc, ca,bc,c, ab,bc,ca];
        if(ab<0 && bc<0 && ca<0) return [a,b,c];
        if(ab>=0 && bc<0 && ca<0) return [a,ab,c, ab,b,c];
        if(bc>=0 && ca<0 && ab<0) return [b,bc,a, bc,c,a];
        if(ca>=0 && ab<0 && bc<0) return [c,ca,b, ca,a,b];
        if(ab<0) return [a,b,bc, a,bc,ca, ca,bc,c];
        if(bc<0) return [b,c,ca, b,ca,ab, ab,ca,a];
        return [c,a,ab, c,ab,bc, bc,ab,b];
    }

    internal static byte[] Apply(byte[] vanilla,byte[] wrapped,string slot,List<BodySurface> stockBody,List<BodySurface> fittedBodies,GarmentClassification? classification)
    {
        var src=SecondSkinWriter.Parse(vanilla); var dst=SecondSkinWriter.Parse(wrapped);

        // Keep edge-geometry streams intact on models with a separate edge LOD.
        if(wrapped[66]!=0 || dst.Lod0MeshCount==0) return wrapped;

        static Vector3 SeamKey(Vector3 p)=>Vector3.Round(p*100000);
        var skinEdges=new HashSet<(Vector3,Vector3)>();
        for(int m=dst.Lod0MeshIndex;m<dst.Lod0MeshIndex+dst.Lod0MeshCount;m++)
        {
            var mesh=new Layout(dst,m);
            if(!SecondSkinWriter.IsBodySkinMaterial(mesh.Material)) continue;
            var points=mesh.Positions(mesh.Count).Select(SeamKey).ToArray();
            var corners=mesh.Indices();
            for(int j=0;j+2<corners.Length;j+=3)
                for(int k=0;k<3;k++)
                {
                    var a=points[corners[j+k]]; var b=points[corners[j+(k+1)%3]];
                    skinEdges.Add((a,b));
                }
        }
        var joins=new Dictionary<(Vector3,Vector3),(Vector3,Vector3)>();
        for(int i=0;i<Math.Min(src.Lod0MeshCount,dst.Lod0MeshCount);i++)
        {
            var originalSkin=new Layout(src,src.Lod0MeshIndex+i);
            var fittedSkin=new Layout(dst,dst.Lod0MeshIndex+i);
            if(!SecondSkinWriter.IsBodySkinMaterial(originalSkin.Material) || originalSkin.Material!=fittedSkin.Material) continue;
            var corners=originalSkin.Indices();
            if(!corners.SequenceEqual(fittedSkin.Indices())) continue;
            var rest=originalSkin.Positions(originalSkin.Count);
            var fit=fittedSkin.Positions(fittedSkin.Count);
            for(int j=0;j<corners.Length;j+=3)
            for(int k=0;k<3;k++)
            {
                int a=corners[j+k],b=corners[j+(k+1)%3];
                joins[(SeamKey(rest[a]),SeamKey(rest[b]))]=(fit[a],fit[b]);
            }
        }
        if(slot=="_dwn" && joins.Count>0)
        {
            // Restore the original common weights at sewn endpoints after the body
            // transfer. Bone palettes can have different orders on cloth and skin.
            var sewnPoints=new HashSet<Vector3>();
            var matching=new List<(Layout Original,Layout Fit,Vector3[] Points,ushort[] Corners)>();
            for(int i=0;i<Math.Min(src.Lod0MeshCount,dst.Lod0MeshCount);i++)
            {
                var original=new Layout(src,src.Lod0MeshIndex+i);
                var fit=new Layout(dst,dst.Lod0MeshIndex+i);
                var corners=original.Indices();
                if(original.Material!=fit.Material || !corners.SequenceEqual(fit.Indices())) continue;
                var points=original.Positions(original.Count).Select(SeamKey).ToArray();
                matching.Add((original,fit,points,corners));
                if(SecondSkinWriter.IsBodySkinMaterial(original.Material)) continue;
                for(int j=0;j<corners.Length;j+=3)
                for(int k=0;k<3;k++)
                {
                    var a=points[corners[j+k]]; var b=points[corners[j+(k+1)%3]];
                    if(!joins.ContainsKey((a,b)) && !joins.ContainsKey((b,a))) continue;
                    sewnPoints.Add(a); sewnPoints.Add(b);
                }
            }
            foreach(var (original,fit,points,corners) in matching)
                foreach(int index in corners.Distinct())
                    if(sewnPoints.Contains(points[index])) fit.RestoreWeights(index,original);
        }
        var metadata=(byte[])wrapped.Clone();
        using var vertices=new MemoryStream(); using var indices=new MemoryStream();
        int vbSize=I(wrapped,40),ibSize=I(wrapped,52);

        var emit=new List<Action>();
        for(int ordinal=0;ordinal<Math.Min(src.Lod0MeshCount,dst.Lod0MeshCount);ordinal++)
        {
            var mesh=new Layout(dst,dst.Lod0MeshIndex+ordinal);
            var original=new Layout(src,src.Lod0MeshIndex+ordinal);
            if(mesh.Material!=original.Material || !mesh.Material.Contains("_top_",StringComparison.OrdinalIgnoreCase) &&
                !mesh.Material.Contains("_dwn_",StringComparison.OrdinalIgnoreCase)) continue;
            var fields=mesh.Fields;
            var w=fields.FirstOrDefault(e=>e.Usage==1); var bone=fields.FirstOrDefault(e=>e.Usage==2);
            if(w.Usage!=1 || bone.Usage!=2 || (w.Type==17)!=(bone.Type==17) || fields.Any(e=>!PanelStreams.Supported(e))) continue;
            if(dst.Shapes.Values.SelectMany(s=>s).Any(e=>e.MeshIndexOffset==mesh.First)) continue;
            var old=mesh.Indices();
            if(old.Length==0 || old.Length%3!=0 || mesh.Count>original.Count || !old.SequenceEqual(original.Indices())) continue;
            var rest=original.Positions(mesh.Count);
            var selected=Flexible(mesh,old,rest,w,bone);
            if(!selected.Any(x=>x)) continue;
            var newIndices=new List<int>(); var panel=new List<int>();
            var edges=new Dictionary<(int,int),int>();
            var starts=new int[old.Length/3+1];
            var fixedRim=new Dictionary<int,Vector3>();
            int Mid(int a,int b)
            {
                if(slot=="_dwn")
                {
                    var ra=SeamKey(rest[a]); var rb=SeamKey(rest[b]);
                    if(joins.TryGetValue((ra,rb),out var seam))
                    { fixedRim[a]=seam.Item1; fixedRim[b]=seam.Item2; return -1; }
                    if(joins.TryGetValue((rb,ra),out seam))
                    { fixedRim[a]=seam.Item2; fixedRim[b]=seam.Item1; return -1; }
                }
                var key=(Math.Min(a,b),Math.Max(a,b));
                if(!edges.TryGetValue(key,out int j)) edges.Add(key,j=mesh.Count+edges.Count);
                return j;
            }
            for(int j=0;j<old.Length;j+=3)
            {
                starts[j/3]=newIndices.Count;
                int a=old[j],b=old[j+1],c=old[j+2];
                if(!selected[j/3]) { newIndices.AddRange([a,b,c]); continue; }
                int ab=Mid(a,b),bc=Mid(b,c),ca=Mid(c,a);
                int[] children=Children(a,b,c,ab,bc,ca);
                panel.AddRange(children); newIndices.AddRange(children);
            }
            starts[^1]=newIndices.Count;
            int count=mesh.Count+edges.Count;
            if(count>ushort.MaxValue) continue;
            var fit=mesh.Positions(mesh.Count);
            var pinned=new Dictionary<int,Vector3>(fixedRim);
            var donor=(Vector3[])fit.Clone();
            Array.Resize(ref rest,count); Array.Resize(ref fit,count); Array.Resize(ref donor,count);
            var strides=Enumerable.Range(0,3).Select(mesh.Stride).ToArray();
            var streams=strides.Select(s=>new byte[count*s]).ToArray();
            for(int s=0;s<3;s++) if(strides[s]>0)
                Buffer.BlockCopy(wrapped,mesh.Stream(s),streams[s],0,mesh.Count*strides[s]);
            foreach(var ((a,b),mid) in edges)
            {
                rest[mid]=(rest[a]+rest[b])/2;
                donor[mid]=(donor[a]+donor[b])/2;
                var linear=(fit[a]+fit[b])/2;
                var ka=SeamKey(fit[a]); var kb=SeamKey(fit[b]);
                var ra=SeamKey(rest[a]); var rb=SeamKey(rest[b]);
                if(joins.TryGetValue((ra,rb),out var seam) || joins.TryGetValue((rb,ra),out seam))
                {
                    if(!joins.ContainsKey((ra,rb))) seam=(seam.Item2,seam.Item1);
                    pinned[a]=seam.Item1; pinned[b]=seam.Item2; pinned[mid]=(seam.Item1+seam.Item2)/2;
                }
                else if(skinEdges.Contains((ka,kb)) || skinEdges.Contains((kb,ka)))
                {
                    pinned[a]=fit[a]; pinned[b]=fit[b]; pinned[mid]=linear;
                }
                fit[mid]=linear;
                PanelStreams.Midpoint(streams,strides,fields,a,b,mid);
            }
            // A source face that bridges through the body is an interior closure.
            // Its newly inserted vertices must follow the rim, not an independent
            // body-surface projection from the middle of the closure.
            var interior=new HashSet<int>();
            var exterior=new HashSet<int>();
            var clearanceFaces=new List<int>();
            for(int j=0;j<old.Length;j+=3)
            {
                int a=old[j],b=old[j+1],c=old[j+2];
                if(!selected[j/3]) continue;
                bool inside=classification?.Closure(ordinal,j/3)==true || GarmentDeformation.SourceDepth((rest[a]+rest[b]+rest[c])/3,stockBody)<-.015f;
                var set=inside?interior:exterior;
                for(int corner=starts[j/3];corner<starts[j/3+1];corner++)
                {
                    int vertex=newIndices[corner];
                    set.Add(vertex);
                    if(!inside) clearanceFaces.Add(vertex);
                }
            }
            interior.ExceptWith(exterior);
            foreach(var ((a,b),mid) in edges)
                if(interior.Contains(mid)) fit[mid]=(fit[a]+fit[b])/2;
            var faces=panel.ToArray();
            emit.Add(()=>
            {
                foreach(var ((a,b),mid) in edges)
                    if(interior.Contains(mid) && !pinned.ContainsKey(mid)) fit[mid]=(fit[a]+fit[b])/2;
                // The shared cage can move a close panel away from its body after
                // the first projection. Restore the authored contact before encoding.
                foreach(int vertex in clearanceFaces.Distinct())
                    if(!pinned.ContainsKey(vertex))
                        fit[vertex]=WrapGarment.Contact(rest[vertex],fit[vertex],stockBody,fittedBodies,minimumOnly:slot=="_top");
                // Vertex clearance alone misses a chord cutting across curved skin.
                // Restore deficits measured on the actual emitted triangles.
                var welded=faces.Distinct().GroupBy(i=>SeamKey(rest[i]))
                    .Where(group=>!group.Any(pinned.ContainsKey)).Select(group=>group.ToArray()).ToArray();
                var pushes=new Vector3[fit.Length];
                var samples=new int[fit.Length];
                for(int pass=0;pass<4;pass++)
                {
                    Array.Clear(pushes); Array.Clear(samples);
                    bool changed=false;
                    for(int j=0;j<clearanceFaces.Count;j+=3)
                    {
                        int a=clearanceFaces[j],b=clearanceFaces[j+1],c=clearanceFaces[j+2];
                        int free=3-(pinned.ContainsKey(a)?1:0)-(pinned.ContainsKey(b)?1:0)-(pinned.ContainsKey(c)?1:0);
                        if(free==0) continue;
                        var source=(rest[a]+rest[b]+rest[c])/3;
                        var center=(fit[a]+fit[b]+fit[c])/3;
                        var push=WrapGarment.Contact(source,center,stockBody,fittedBodies,true)-center;
                        if(push.LengthSquared()<1e-8f) continue;
                        push*=3f/free;
                        void Accumulate(int i)
                        {
                            if(pinned.ContainsKey(i)) return;
                            pushes[i]+=push; samples[i]++;
                        }
                        Accumulate(a); Accumulate(b); Accumulate(c);
                        changed=true;
                    }
                    if(!changed) break;
                    foreach(var copies in welded)
                    {
                        var sum=Vector3.Zero; int count=0;
                        foreach(int i in copies) { sum+=pushes[i]; count+=samples[i]; }
                        if(count>0) foreach(int i in copies) fit[i]+=sum/count;
                    }
                }
                PanelStreams.UpdateShading(streams,strides,fields,donor,fit,faces);
                var pe=fields.First(e=>e.Usage==0);
                foreach(int i in faces.Distinct())
                    SecondSkinWriter.WriteXYZ(streams[pe.Stream],i*strides[pe.Stream]+pe.Offset,pe.Type,fit[i].X,fit[i].Y,fit[i].Z);
                for(int s=0;s<3;s++) if(strides[s]>0)
                {
                    W(metadata,mesh.Header+20+4*s,checked(vbSize+(int)vertices.Length));
                    vertices.Write(streams[s]);
                }
                int first=checked((ibSize+(int)indices.Length)/2);
                using(var writer=new BinaryWriter(indices,System.Text.Encoding.UTF8,true))
                    foreach(int i in newIndices) writer.Write(checked((ushort)i));
                int sub=U(wrapped,mesh.Header+10),subs=U(wrapped,mesh.Header+12);
                for(int j=sub;j<sub+subs;j++)
                {
                    int h=dst.SubmeshStart+16*j, start=I(wrapped,h)-mesh.First,end=start+I(wrapped,h+4);
                    W(metadata,h,first+starts[start/3]); W(metadata,h+4,starts[end/3]-starts[start/3]);
                }
                Short(metadata,mesh.Header,count); W(metadata,mesh.Header+4,newIndices.Count); W(metadata,mesh.Header+16,first);
            });
        }

        foreach(var write in emit) write();
        if(vertices.Length==0) return wrapped;
        int dv=checked((int)vertices.Length),di=checked((int)indices.Length);
        int vEnd=dst.Vb+vbSize,iEnd=dst.Ib+ibSize;
        W(metadata,40,vbSize+dv); W(metadata,52,ibSize+di);
        W(metadata,dst.LodStart+44,vbSize+dv); W(metadata,dst.LodStart+48,ibSize+di);
        for(int lod=0;lod<wrapped[64];lod++)
        foreach(int offset in new[]{16+4*lod,28+4*lod,dst.LodStart+60*lod+52,dst.LodStart+60*lod+56})
        {
            int value=I(wrapped,offset);
            W(metadata,offset,value+(value>=vEnd?dv:0)+(value>=iEnd?di:0));
        }
        using var result=new MemoryStream(wrapped.Length+dv+di);
        result.Write(metadata,0,vEnd); vertices.WriteTo(result);
        result.Write(metadata,vEnd,iEnd-vEnd); indices.WriteTo(result);
        result.Write(metadata,iEnd,metadata.Length-iEnd);
        return result.ToArray();
    }
}
