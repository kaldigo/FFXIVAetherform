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

    internal static byte[] Apply(byte[] vanilla,byte[] prior,byte[] wrapped,VanillaAfWrap map,GarmentBindings bindings,string slot)
    {
        var src=SecondSkinWriter.Parse(vanilla); var dst=SecondSkinWriter.Parse(wrapped);
        var before=SecondSkinWriter.Parse(prior);
        // Keep edge-geometry streams intact on models with a separate edge LOD.
        if(wrapped[66]!=0 || dst.Lod0MeshCount==0) return wrapped;
        var skin=new BodySurface(ModelPartReader.Read(wrapped)!, .025f);
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
        var metadata=(byte[])wrapped.Clone();
        using var vertices=new MemoryStream(); using var indices=new MemoryStream();
        int vbSize=I(wrapped,40),ibSize=I(wrapped,52);
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
            int Mid(int a,int b)
            {
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
                int[] children=[a,ab,ca, ab,b,bc, ca,bc,c, ab,bc,ca];
                panel.AddRange(children); newIndices.AddRange(children);
            }
            starts[^1]=newIndices.Count;
            int count=mesh.Count+edges.Count;
            if(count>ushort.MaxValue) continue;
            var fit=mesh.Positions(mesh.Count);
            var pinned=new Dictionary<int,Vector3>();
            var donor=new Layout(before,dst.Lod0MeshIndex+ordinal).Positions(mesh.Count);
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
                if(skinEdges.Contains((ka,kb)) || skinEdges.Contains((kb,ka)))
                {
                    pinned[a]=fit[a]; pinned[b]=fit[b]; pinned[mid]=linear;
                }
                var bound=map.Apply(bindings.Get(rest[mid],false),false);
                // Carry existing seam corrections (particularly on tight bottoms).
                var mappedEnds=(map.Apply(bindings.Get(rest[a],false),false)+map.Apply(bindings.Get(rest[b],false),false))/2;
                fit[mid]=linear+bound-mappedEnds;
                PanelStreams.Midpoint(streams,strides,fields,a,b,mid);
            }
            var faces=panel.ToArray();
            PanelGeometry.Smooth(rest,fit,faces);
            PanelGeometry.Clear(fit,donor,rest,faces,skin,slot=="_top");
            foreach(var (i,p) in pinned) fit[i]=p;
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
        }
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
