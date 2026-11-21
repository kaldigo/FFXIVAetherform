using System.Numerics;
using Proteus.Services;
using E=Proteus.Services.SecondSkinWriter.VElem;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal sealed class SkinVertexFormat
{
    private readonly byte[] body;
    private readonly int header,buffer;
    private readonly E[] fields;
    internal readonly byte[] Declaration,Strides;
    private readonly int[] bases=new int[3],offsets;
    private readonly NativeSkinChart chart;

    internal SkinVertexFormat(byte[] body,NativeSkinChart chart)
    {
        this.body=body;this.chart=chart;
        var model=SecondSkinWriter.Parse(body);
        header=model.MeshStart+36*chart.Mesh;buffer=model.Vb;
        Declaration=body.AsSpan(0x44+136*chart.Mesh,136).ToArray();
        Strides=body.AsSpan(header+32,4).ToArray();
        fields=model.Decls[chart.Mesh];
        offsets=new int[fields.Length];
        // AF cut samples can interpolate influences from all three parent corners.
        for(int s=0;s<3;s++)
        {
            int extra=0;
            foreach(int j in Enumerable.Range(0,fields.Length).Where(i=>fields[i].Stream==s).OrderBy(i=>fields[i].Offset))
            {
                var f=fields[j];
                if(!PanelStreams.Supported(f)) throw new InvalidDataException($"Unsupported AF vertex type {f.Type}, usage {f.Usage}.");
                offsets[j]=f.Offset+extra;Declaration[8*j+1]=checked((byte)offsets[j]);
                if(f.Usage is 1 or 2 && f.Type!=17){Declaration[8*j+2]=17;extra+=4;}
            }
            Strides[s]=checked((byte)(Strides[s]+extra));
            if(s>0) bases[s]=bases[s-1]+Strides[s-1];
        }
    }

    internal byte[] Encode(int face,Point bary,NativeSkinChart.Payload skin,Dictionary<string,byte> palette)
    {
        var row=new byte[bases[2]+Strides[2]];
        Span<float> val=stackalloc float[4],acc=stackalloc float[4];
        Span<double> w=stackalloc double[]{1-bary.X-bary.Y,bary.X,bary.Y};
        for(int j=0;j<fields.Length;j++)
        {
            var e=fields[j];int at=bases[e.Stream]+offsets[j];
            if(e.Usage is 1 or 2)
            {
                if(skin.Weights.Length>8) throw new InvalidDataException("AF skin requires more than eight influences.");
                for(int k=0;k<skin.Weights.Length;k++)
                    row[at+k]=e.Usage==1?skin.Weights[k].Weight:palette[skin.Weights[k].Bone];
                continue;
            }
            int stream=buffer+BitConverter.ToInt32(body,header+20+4*e.Stream);
            acc.Clear();double largest=-1;float sign=1;
            for(int k=0;k<3;k++)
            {
                int v=chart.RenderCorners[3*face+k];
                SecondSkinWriter.ReadTyped(body,stream+v*body[header+32+e.Stream]+e.Offset,e.Type,val);
                for(int c=0;c<4;c++) acc[c]+=(float)w[k]*val[c];
                if(w[k]>largest){sign=val[3];largest=w[k];}
            }
            if(e.Usage==0){acc[0]=skin.PositionMm.X/1000;acc[1]=skin.PositionMm.Y/1000;acc[2]=skin.PositionMm.Z/1000;}
            if(e.Usage is 3 or 5 or 6)
            {
                var n=new Vector3(acc[0],acc[1],acc[2]);
                if(e.Type==8) n=2*n-Vector3.One;
                if(e.Usage==3) n=skin.Normal;
                else if(n.LengthSquared()>1e-12f) n=Vector3.Normalize(n);
                if(e.Type==8) n=(n+Vector3.One)*.5f;
                acc[0]=n.X;acc[1]=n.Y;acc[2]=n.Z;
                if(e.Usage!=3) acc[3]=sign;
            }
            if(e.Usage==4 && e.UsageIndex==0){acc[0]=(float)skin.RenderUV.X;acc[1]=(float)skin.RenderUV.Y;}
            int nc=e.Type switch {0=>1,1 or 13=>2,2=>3,_=>4};
            for(int c=0;c<nc;c++)
            {
                if(e.Type is 5 or 8) row[at+c]=(byte)Math.Clamp(MathF.Round(acc[c]*(e.Type==8?255:1)),0,255);
                else if(e.Type is 13 or 14) BitConverter.TryWriteBytes(row.AsSpan(at+2*c),(Half)acc[c]);
                else BitConverter.TryWriteBytes(row.AsSpan(at+4*c),acc[c]);
            }
        }
        return row;
    }
    internal byte[][] Streams(IReadOnlyList<byte[]> rows)
    {
        var result=new byte[3][];
        for(int s=0;s<3;s++)
        {
            int stride=Strides[s];result[s]=new byte[rows.Count*stride];
            for(int i=0;i<rows.Count;i++) Buffer.BlockCopy(rows[i],bases[s],result[s],i*stride,stride);
        }
        return result;
    }
}
