using System.Text;
using Proteus.Services;

namespace Aetherform.Geometry;

// Extend the existing palettes in place logically: all old local and global
// bone indices remain valid, including those referenced by unedited LODs.
internal static class SkinDrawBindings
{
    internal static byte[] Add(byte[] model,byte[] body,string material,IReadOnlyDictionary<int,string[]> perMesh,IReadOnlySet<int>? skinMeshes=null)
    {
        var src=SecondSkinWriter.Parse(model);
        var native=SecondSkinWriter.Parse(body);
        int U(int at)=>BitConverter.ToUInt16(model,at);
        int I(int at)=>BitConverter.ToInt32(model,at);
        int mat=src.MatNames.IndexOf(material);
        var bones=src.BoneNames.ToList();
        var palettes=src.BoneTables.Select(t=>t.ToList()).ToList();
        foreach(var (mesh,names) in perMesh)
        {
            var palette=palettes[U(src.MeshStart+36*mesh+14)];
            foreach(string name in names.Distinct(StringComparer.Ordinal))
            {
                int global=bones.IndexOf(name);
                if(global<0){global=bones.Count;bones.Add(name);}
                if(!palette.Contains(checked((ushort)global))) palette.Add((ushort)global);
            }
            if(palette.Count>256) throw new InvalidDataException("Skin bone palette exceeds byte addressing.");
        }
        var newBones=bones.Skip(src.BoneCount).ToArray();
        int boneTable=src.MatOffStart+4*src.MatCount;
        int boneEnd=boneTable+4*src.BoneCount;
        int EndNames(int table,int count,IReadOnlyList<string> names)=>
            Enumerable.Range(0,count).Max(i=>I(table+4*i)+Encoding.UTF8.GetByteCount(names[i])+1);
        int boneNamesEnd=EndNames(boneTable,src.BoneCount,src.BoneNames);
        int matNamesEnd=EndNames(src.MatOffStart,src.MatCount,src.MatNames);
        using var bNames=new MemoryStream();
        var bOffsets=new List<int>();
        using var boxes=new MemoryStream();
        foreach(string name in newBones)
        {
            bOffsets.Add(boneNamesEnd+checked((int)bNames.Length));
            bNames.Write(Encoding.UTF8.GetBytes(name));bNames.WriteByte(0);
            int i=Array.IndexOf(native.BoneNames,name);
            if(i<0) throw new InvalidDataException("Missing AF bone bounds for "+name);
            boxes.Write(native.BoneBBoxes,32*i,32);
        }
        byte[] mName=mat<0?Encoding.UTF8.GetBytes(material+"\0"):[];
        int growth=checked((int)bNames.Length+mName.Length),pad=(4-growth%4)%4;
        int Rel(int offset)=>offset+(offset>=boneNamesEnd?(int)bNames.Length:0)+(offset>=matNamesEnd?mName.Length:0);
        var edits=new List<(int At,int Removed,byte[] Bytes)>();
        void Insert(int at,byte[] bytes){if(bytes.Length!=0) edits.Add((at,0,bytes));}
        Insert(src.StrBlock+boneNamesEnd,bNames.ToArray());
        Insert(src.StrBlock+matNamesEnd,mName);
        Insert(src.Mh,new byte[pad]);
        if(mat<0) Insert(boneTable,BitConverter.GetBytes(matNamesEnd+(boneNamesEnd<=matNamesEnd?(int)bNames.Length:0)));
        Insert(boneEnd,bOffsets.SelectMany(o=>BitConverter.GetBytes(o+(matNamesEnd<boneNamesEnd?mName.Length:0))).ToArray());
        using var packed=new MemoryStream();
        bool v6=(model[0]==6);
        if(v6) SecondSkinWriter.WriteBoneTablesV6(packed,palettes.Select(t=>t.ToArray()).ToList());
        else
        {
            using var bw=new BinaryWriter(packed,Encoding.UTF8,true);
            foreach(var palette in palettes)
            {
                if(palette.Count>64) throw new InvalidDataException("Version 5 bone palette limit exceeded.");
                for(int i=0;i<64;i++) bw.Write(i<palette.Count?palette[i]:(ushort)0);
                bw.Write(palette.Count);
            }
        }
        edits.Add((boneEnd,src.ShapeBlock-boneEnd,packed.ToArray()));
        Insert(src.BoneBBoxAt+src.BoneCount*32,boxes.ToArray());
        var header=(byte[])model.Clone();
        void Short(int at,int v)=>BitConverter.TryWriteBytes(header.AsSpan(at),checked((ushort)v));
        void Int(int at,int v)=>BitConverter.TryWriteBytes(header.AsSpan(at),v);
        foreach(var (start,n) in new[]{(src.AttrStart,src.AttrNames.Length),(src.MatOffStart,src.MatCount),(boneTable,(int)src.BoneCount)})
            for(int i=0;i<n;i++) Int(start+4*i,Rel(I(start+4*i)));
        for(int i=0;i<U(src.Mh+16);i++) Int(src.ShapeBlock+16*i,Rel(I(src.ShapeBlock+16*i)));
        Short(src.DeclEnd,U(src.DeclEnd)+newBones.Length+(mat<0?1:0));
        Int(src.DeclEnd+4,checked((int)src.StrSize+growth+pad));
        Short(src.Mh+12,bones.Count);
        if(mat<0)
        {
            mat=src.MatCount;
            Short(src.Mh+10,mat+1);
            Short(14,mat+1); // The loader allocates material handles from the file header.
        }
        foreach(int m in perMesh.Keys.Where(m=>skinMeshes==null || skinMeshes.Contains(m))) Short(src.MeshStart+36*m+8,mat);
        if(v6) Short(src.Mh+44,((int)packed.Length-4*palettes.Count)/2);
        int delta=edits.Sum(e=>e.Bytes.Length-e.Removed);
        Int(8,I(8)+delta);
        ModelAttributeWriter.ShiftOffsets(header,src.LodStart,delta,-1);
        using var output=new MemoryStream(header.Length+delta);
        int cursor=0;
        foreach(var e in edits.OrderBy(e=>e.At))
        {
            output.Write(header,cursor,e.At-cursor);output.Write(e.Bytes);cursor=e.At+e.Removed;
        }
        output.Write(header,cursor,header.Length-cursor);
        return output.ToArray();
    }
}
