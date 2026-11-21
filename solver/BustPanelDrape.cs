using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

// Interpolate a broad panel's change of shape across the original facets. Averaging
// displacement (rather than positions) preserves details already present in vanilla.
internal static class BustPanelDrape
{
    internal static void Apply(SecondSkinWriter.Source src,int mesh,GarmentClearance.Mesh source,GarmentClearance.Mesh fit,
        ClothSubdivision.Origins lineage,int[] nodes,Vector3[] rest,Vector3[] moved,bool[] close,
        float[] peaks,Dictionary<int,Vector3> moves,Lazy<(Vector3,Vector3)[]> skinRims)
    {
        var wt=src.Decls[mesh].FirstOrDefault(e=>e.Usage==1);var bi=src.Decls[mesh].FirstOrDefault(e=>e.Usage==2);
        if(wt.Usage!=1 || bi.Usage!=2)return;
        var palette=src.BoneTables[BitConverter.ToUInt16(src.S,source.Header+14)];
        var bust=palette.Select(i=>src.BoneNames[i] is "j_mune_l" or "j_mune_r").ToArray();
        if(!bust.Contains(true))return;
        var mass=new float[rest.Length];var near=new bool[rest.Length];var active=new HashSet<int>();
        int At(SecondSkinWriter.VElem e,int v)=>src.Vb+BitConverter.ToInt32(src.S,source.Header+20+4*e.Stream)+src.S[source.Header+32+e.Stream]*v+e.Offset;
        for(int f=0;f<close.Length;f++)
        {
            int t=3*f;
            for(int k=0;k<3;k++)
            {
                int v=source.Indices[t+k],n=nodes[v];near[n]|=close[f];
                if(!active.Add(v))continue;
                int a=At(wt,v),b=At(bi,v);float w=0;
                for(int j=0;j<(wt.Type==17?8:4);j++)if(src.S[a+j]!=0 && bust[src.S[b+j]])w+=src.S[a+j]/255f;
                mass[n]=Math.Max(mass[n],w);
            }
        }
        // The apex correction already owns concentrated peaks. This pass handles
        // broad, sparsely sampled cups with long planar spans in the native mesh.
        if(Enumerable.Range(0,mass.Length).Any(n=>mass[n]>.05f && peaks[n]>=.55f))return;
        var seeds=Enumerable.Range(0,mass.Length).Where(n=>mass[n]>.03f && near[n]).ToArray();
        if(seeds.Length<3)return;
        var area=new float[rest.Length];
        for(int f=0;f<source.Indices.Length;f+=3)
        {
            int i=nodes[source.Indices[f]],j=nodes[source.Indices[f+1]],k=nodes[source.Indices[f+2]];
            float w=Vector3.Cross(rest[j]-rest[i],rest[k]-rest[i]).Length()/6;
            area[i]+=w;area[j]+=w;area[k]+=w;
        }
        var sample=Enumerable.Range(0,rest.Length).Where(i=>area[i]>0 && seeds.Any(j=>Vector3.DistanceSquared(rest[i],rest[j])<120*120)).ToArray();
        var seen=new HashSet<int>();
        for(int f=0;f<lineage.Parents.Length;f++)
        {
            foreach(int v in fit.Indices.AsSpan(3*f,3))
            {
                if(!seen.Add(v) || fit.AmbiguousShapeBases.Contains(v))continue;
                var p=1000*lineage.Positions[v];
                float nearest=seeds.Min(j=>Vector3.DistanceSquared(p,rest[j]));
                float blend=Math.Clamp((85-MathF.Sqrt(nearest))/40,0,1);
                blend*=blend*(3-2*blend);
                if(blend<=0)continue;
                float rimDistance=20;
                foreach(var (a,b) in skinRims.Value)rimDistance=Math.Min(rimDistance,SkinSeamCoverage.Distance(p,a,b));
                float seam=Math.Clamp((rimDistance-.12f)/12,0,1);
                blend*=seam*seam*(3-2*seam);
                if(blend<=0)continue;
                float total=0;var displacement=Vector3.Zero;
                foreach(int j in sample)
                {
                    float r=Vector3.DistanceSquared(p,rest[j]);float weight=area[j]*MathF.Exp(-r/(2*30*30));
                    displacement+=weight*(moved[j]-rest[j]);total+=weight;
                }
                if(total<=0)continue;
                var correction=displacement/total-(fit.P[v]-p);float len=correction.Length();
                if(len>1e-6f)correction*=12*MathF.Tanh(len/12)/len;
                moves[v]=moves.GetValueOrDefault(v)+blend*correction;
            }
        }
    }
}
