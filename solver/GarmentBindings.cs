using System.Numerics;
using System.Security.Cryptography;

namespace Aetherform.Geometry;

// A binding belongs to the source garment in its normalized race space and
// the baked reference. It can be used with every compatible AF target shape.
internal sealed class GarmentBindings
{
    private readonly Dictionary<(Vector3,bool),VanillaAfWrap.Binding> values=[];
    private readonly string path;
    private bool dirty;

    internal GarmentBindings(string directory,string slot,byte[] source)
    {
        string name=Convert.ToHexString(SHA256.HashData(source));
        path=Path.Combine(directory,VanillaAfWrap.MapId,slot,name+".bind");
        if(!File.Exists(path)) return;
        var bytes=File.ReadAllBytes(path);
        const int recordBytes=41;
        if(bytes.Length<32 || (bytes.Length-32)%recordBytes!=0 ||
            !SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0,32))) return;
        using var reader=new BinaryReader(new MemoryStream(bytes,32,bytes.Length-32));
        Vector3 ReadVector()=>new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
        while(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            var p=ReadVector();
            bool legsOnly=reader.ReadBoolean();
            int t=reader.ReadInt32();
            if(t<0 || t%3!=0 || t>=VanillaAfWrap.CornerCount) { values.Clear(); return; }
            values[(p,legsOnly)]=new(t,ReadVector(),ReadVector());
        }
    }

    internal VanillaAfWrap.Binding Get(Vector3 p,bool legsOnly)
    {
        if(values.TryGetValue((p,legsOnly),out var b)) return b;
        b=VanillaAfWrap.Bind(p,legsOnly);
        values.Add((p,legsOnly),b);
        dirty=true;
        return b;
    }

    internal void Save()
    {
        if(!dirty) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var payload=new MemoryStream();
        using var writer=new BinaryWriter(payload);
        void WriteVector(Vector3 p) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        foreach(var ((point,legsOnly),binding) in values)
        {
            WriteVector(point); writer.Write(legsOnly); writer.Write(binding.Corner);
            WriteVector(binding.Weights); WriteVector(binding.Offset);
        }
        var data=payload.ToArray();
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=File.Create(temp))
            {
                file.Write(SHA256.HashData(data));
                file.Write(data);
            }
            File.Move(temp,path,true);
        }
        finally { File.Delete(temp); }
    }
}
