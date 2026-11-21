using System.Buffers.Binary;
using Proteus.Services;

namespace Aetherform.Geometry;

// Replace only LOD0 draw buffers. Shape entries refer to index occurrences in
// a draw, so callers supply the new occurrences for every edited shape mesh.
internal static class LodMeshWriter
{
    internal sealed record Draw(int Mesh, int Vertices, byte[][] Streams, ushort[] Indices,
        (int First,int Count)[] Submeshes,
        IReadOnlyDictionary<string,(ushort Base,ushort Replace)[]> Shapes, byte[]? Declaration=null,byte[]? Strides=null);

    private static int U(byte[] b,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static int I(byte[] b,int at)=>BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    private static void U(byte[] b,int at,int value)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),checked((ushort)value));
    private static void I(byte[] b,int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at),value);

    internal static byte[] Replace(byte[] mdl,IReadOnlyList<Draw> draws)
    {
        if(draws.Count==0) return mdl;
        var src=SecondSkinWriter.Parse(mdl);
        var meta=(byte[])mdl.Clone();
        int vSize=I(mdl,40),iSize=I(mdl,52);
        var byOld=new Dictionary<int,(int First,Draw Draw)>();
        using var vertices=new MemoryStream();
        using var indices=new MemoryStream();
        using var indexWriter=new BinaryWriter(indices,System.Text.Encoding.UTF8,true);
        foreach(var d in draws)
        {
            if(d.Mesh<src.Lod0MeshIndex || d.Mesh>=src.Lod0MeshIndex+src.Lod0MeshCount ||
                d.Streams.Length!=3 || d.Indices.Length%3!=0 || d.Indices.Any(i=>i>=d.Vertices))
                throw new InvalidDataException("Invalid replacement draw.");
            int h=src.MeshStart+36*d.Mesh,old=I(mdl,h+16);
            while((iSize+indices.Length)%4!=0) indices.WriteByte(0);
            int first=checked((iSize+(int)indices.Length)/2);
            byOld.Add(old,(first,d));
            U(meta,h,d.Vertices); I(meta,h+4,d.Indices.Length); I(meta,h+16,first);
            foreach(ushort i in d.Indices) indexWriter.Write(i);
            if(d.Declaration!=null)
            {
                if(d.Declaration.Length!=136 || d.Strides?.Length!=4) throw new InvalidDataException("Invalid replacement declaration.");
                d.Declaration.CopyTo(meta,0x44+136*d.Mesh); d.Strides.CopyTo(meta,h+32);
            }
            for(int s=0;s<3;s++)
            {
                if(d.Streams[s].Length!=d.Vertices*meta[h+32+s]) throw new InvalidDataException("Invalid vertex stream length.");
                if(d.Streams[s].Length==0) continue;
                while((vSize+vertices.Length)%4!=0) vertices.WriteByte(0);
                I(meta,h+20+4*s,checked(vSize+(int)vertices.Length));
                vertices.Write(d.Streams[s]);
            }
            if(d.Submeshes.Length!=U(mdl,h+12)) throw new InvalidDataException("Submesh count changed.");
            int sub=U(mdl,h+10);
            for(int j=0;j<d.Submeshes.Length;j++)
            {
                var (start,count)=d.Submeshes[j];
                if(start<0 || count<0 || start>d.Indices.Length-count || count%3!=0)
                    throw new InvalidDataException("Submesh exceeds its replacement draw.");
                int sh=src.SubmeshStart+16*(sub+j);
                I(meta,sh,first+start); I(meta,sh+4,count);
            }
        }
        int dv=checked((int)vertices.Length),di=checked((int)indices.Length);
        int ve=src.Vb+vSize,ie=src.Ib+iSize;
        if(ve>src.Ib) throw new InvalidDataException("Overlapping LOD0 vertex and index buffers.");
        I(meta,40,vSize+dv); I(meta,52,iSize+di);
        I(meta,src.LodStart+44,vSize+dv); I(meta,src.LodStart+48,iSize+di);
        for(int lod=0;lod<mdl[64];lod++)
        foreach(int at in new[]{16+4*lod,28+4*lod,src.LodStart+60*lod+52,src.LodStart+60*lod+56})
        {
            int value=I(mdl,at);
            I(meta,at,value+(value>=ve?dv:0)+(value>=ie?di:0));
        }

        int shapes=U(mdl,src.Mh+16),meshes=U(mdl,src.Mh+18),values=U(mdl,src.Mh+20);
        int meshTable=src.ShapeBlock+16*shapes,valueTable=meshTable+12*meshes;
        var edits=new Dictionary<int,(int Mesh,(ushort Base,ushort Replace)[] Values)>();
        var consumed=new HashSet<(int,string)>();
        for(int i=0;i<shapes;i++)
        {
            int shape=src.ShapeBlock+16*i;
            int offset=src.StrBlock+I(mdl,shape);
            string name=System.Text.Encoding.UTF8.GetString(mdl,offset,Array.IndexOf(mdl,(byte)0,offset)-offset);
            int start=U(mdl,shape+4),count=U(mdl,shape+10);
            for(int j=start;j<start+count;j++)
            {
                if(!byOld.TryGetValue(I(mdl,meshTable+12*j),out var edited)) continue;
                if(!edited.Draw.Shapes.TryGetValue(name,out var replacement))
                    throw new InvalidDataException($"Missing replacement for {name} on mesh {edited.Draw.Mesh}.");
                if(replacement.Any(v=>v.Base>=edited.Draw.Indices.Length || v.Replace>=edited.Draw.Vertices))
                    throw new InvalidDataException("Invalid shape vertex.");
                edits.Add(j,(edited.First,replacement));
                consumed.Add((edited.Draw.Mesh,name));
            }
        }
        if(draws.Any(d=>d.Shapes.Keys.Any(name=>!consumed.Contains((d.Mesh,name)))))
            throw new InvalidDataException("Unmatched shape entry.");
        using var shapeValues=new MemoryStream();
        using var sw=new BinaryWriter(shapeValues,System.Text.Encoding.UTF8,true);
        for(int j=0;j<meshes;j++)
        {
            int sh=meshTable+12*j;
            I(meta,sh+8,checked((int)shapeValues.Length/4));
            if(edits.TryGetValue(j,out var edit))
            {
                I(meta,sh,edit.Mesh); I(meta,sh+4,edit.Values.Length);
                foreach(var (a,b) in edit.Values){sw.Write(a);sw.Write(b);}
            }
            else shapeValues.Write(mdl,valueTable+4*I(mdl,sh+8),4*I(mdl,sh+4));
        }
        int delta=checked((int)shapeValues.Length-4*values);
        U(meta,src.Mh+20,checked((int)shapeValues.Length/4));
        I(meta,8,I(meta,8)+delta);
        ModelAttributeWriter.ShiftOffsets(meta,src.LodStart,delta,-1);
        using var output=new MemoryStream(checked(mdl.Length+dv+di+delta));
        output.Write(meta,0,valueTable);shapeValues.WriteTo(output);
        int tail=valueTable+4*values;
        output.Write(meta,tail,ve-tail);vertices.WriteTo(output);
        output.Write(meta,ve,ie-ve);indices.WriteTo(output);
        output.Write(meta,ie,meta.Length-ie);
        return output.ToArray();
    }
}
