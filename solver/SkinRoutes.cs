using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

// Routes contain only source and target triangle addresses. Evaluate both
// bodies from the active preset, including resolved leg shape replacements.
internal sealed class SkinRoutes
{
    internal sealed record ClothKnot(int Mesh,int First,int Variable,Point Source,int Face,Point Target);
    private readonly record struct Pair(Point Source,int Face,Point Target);
    private sealed record Row(int Mesh,int First,Pair[] Corners);
    private readonly List<Row> rows=[];
    internal readonly List<ClothKnot> Knots=[];
    private static readonly Lazy<Dictionary<string,SkinRoutes>> Catalog=new(Load);

    internal static SkinRoutes? For(byte[] source)=>Catalog.Value.GetValueOrDefault(Convert.ToHexString(SHA256.HashData(source)));
    private static Dictionary<string,SkinRoutes> Load()
    {
        var result=new Dictionary<string,SkinRoutes>();
        var assembly=typeof(SkinRoutes).Assembly;
        foreach(string name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("Aetherform.skinroutes.",StringComparison.Ordinal)))
        {
            using var stream=assembly.GetManifestResourceStream(name)!;
            using var gzip=new GZipStream(stream,CompressionMode.Decompress);
            using var reader=new BinaryReader(gzip);
            if(!reader.ReadBytes(4).AsSpan().SequenceEqual("AFR1"u8)) throw new InvalidDataException("Unsupported AF skin routes.");
            string key=Convert.ToHexString(reader.ReadBytes(32));var route=new SkinRoutes();
            Point Point()=>new(reader.ReadDouble(),reader.ReadDouble());
            int count=reader.ReadInt32();
            for(int j=0;j<count;j++)
                route.rows.Add(new(reader.ReadInt32(),reader.ReadInt32(),
                    Enumerable.Range(0,3).Select(_=>new Pair(Point(),reader.ReadInt32(),Point())).ToArray()));
            int knots=reader.ReadInt32();
            for(int j=0;j<knots;j++)
                route.Knots.Add(new(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32(),Point(),reader.ReadInt32(),Point()));
            if(gzip.ReadByte()!=-1) throw new InvalidDataException("Trailing AF skin route data.");
            result.Add(key,route);
        }
        return result;
    }

    internal List<GarmentSkin.Face> Register(byte[] morphed,NativeSkinChart chart)
    {
        var src=SecondSkinWriter.Parse(morphed);
        var results=new List<GarmentSkin.Face>(rows.Count);
        var positions=new Dictionary<(int,int),Vector3>();
        var tmp=new float[4];
        foreach(var row in rows)
        {
            int m=row.Mesh,h=src.MeshStart+36*m,first=BitConverter.ToInt32(morphed,h+16);
            var v=new int[3];var tri=new Vector3[3];var coords=new Point[3];var mapped=new Vector3[3];
            var addresses=new SkinRegistration.Address[3];
            for(int i=0;i<3;i++)
            {
                int vertex=BitConverter.ToUInt16(morphed,src.Ib+2*(first+row.First+i));v[i]=vertex;
                if(!positions.TryGetValue((m,vertex),out tri[i]))
                {
                    var pos=src.Decls[m].First(e=>e.Usage==0);
                    int offset=src.Vb+BitConverter.ToInt32(morphed,h+20+4*pos.Stream)+pos.Offset+vertex*morphed[h+32+pos.Stream];
                    SecondSkinWriter.ReadTyped(morphed,offset,pos.Type,tmp);
                    positions[(m,vertex)]=tri[i]=1000*new Vector3(tmp[0],tmp[1],tmp[2]);
                }
            }
            for(int i=0;i<3;i++)
            {
                var p=row.Corners[i];var uv=chart.Cutting[p.Face];var bc=p.Source;
                mapped[i]=tri[0]+(float)bc.X*(tri[1]-tri[0])+(float)bc.Y*(tri[2]-tri[0]);
                coords[i]=uv.A+p.Target.X*(uv.B-uv.A)+p.Target.Y*(uv.C-uv.A);
                addresses[i]=new(p.Face,p.Target,coords[i]);
            }
            int sub=BitConverter.ToUInt16(morphed,h+10),count=BitConverter.ToUInt16(morphed,h+12),domain=-1;
            for(int i=sub;i<sub+count;i++)
            {
                int sh=src.SubmeshStart+16*i,start=BitConverter.ToInt32(morphed,sh);
                if(first+row.First>=start && first+row.First<start+BitConverter.ToInt32(morphed,sh+4)) {domain=i;break;}
            }
            results.Add(new(m,row.First,domain,v,mapped,new(coords[0],coords[1],coords[2]),addresses,new(row.Corners[0].Source,row.Corners[1].Source,row.Corners[2].Source)));
        }
        return results;
    }
}
