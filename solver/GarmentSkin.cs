using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal sealed class SkinRegistrationException(string message) : Exception(message);
internal static class GarmentSkin
{
    internal sealed record Face(int Mesh,int First,int Domain,int[] Vertices,Vector3[] Position,Triangle UV,
        SkinRegistration.Address[] Address,Triangle SourceBarycentric);
    internal static List<Face> Register(byte[] garment,NativeSkinChart target,SkinRoutes? retained)
    {
        if(retained!=null) return retained.Register(garment,target);
        var src=SecondSkinWriter.Parse(garment);
        var registration=new SkinRegistration(target);
        var faces=new List<Face>();
        for(int mid=src.Lod0MeshIndex;mid<src.Lod0MeshIndex+src.Lod0MeshCount;mid++)
        {
            int h=src.MeshStart+36*mid;
            if(!SecondSkinWriter.IsBodySkinMaterial(src.MatNames[BitConverter.ToUInt16(garment,h+8)])) continue;
            int count=BitConverter.ToUInt16(garment,h),first=BitConverter.ToInt32(garment,h+16);
            var fields=src.Decls[mid];
            var uv=fields.First(e=>e.Usage==4);var xyz=fields.First(e=>e.Usage==0);
            int At(SecondSkinWriter.VElem f,int i)=>src.Vb+BitConverter.ToInt32(garment,h+20+4*f.Stream)+i*garment[h+32+f.Stream]+f.Offset;
            var positions=new Vector3[count];var lookupUV=new Point[count];
            var values=new float[4];
            for(int i=0;i<count;i++)
            {
                SecondSkinWriter.ReadTyped(garment,At(xyz,i),xyz.Type,values);
                positions[i]=1000*new Vector3(values[0],values[1],values[2]);
                SecondSkinWriter.ReadTyped(garment,At(uv,i),uv.Type,values);lookupUV[i]=new(values[0],values[1]);
            }
            var ix=Enumerable.Range(0,BitConverter.ToInt32(garment,h+4))
                .Select(i=>(int)BitConverter.ToUInt16(garment,src.Ib+2*(first+i))).ToArray();
            var parents=Enumerable.Range(0,count).ToArray();
            int Root(int i)
            {
                while(i!=parents[i]){parents[i]=parents[parents[i]];i=parents[i];}return i;
            }
            var active=new List<int>();
            for(int i=0;i<ix.Length;i+=3)
            {
                var a=positions[ix[i]];var b=positions[ix[i+1]];var c=positions[ix[i+2]];
                if(Vector3.Cross(b-a,c-a).LengthSquared()<=4e-16f) continue;
                parents[Root(ix[i+1])]=Root(ix[i]); parents[Root(ix[i+2])]=Root(ix[i]);active.Add(i);
            }
            foreach(var group in active.SelectMany(i=>ix[i..(i+3)]).Distinct().GroupBy(Root))
            {
                int shift=(int)Math.Floor(group.Min(i=>lookupUV[i].X)+1e-7)-1;
                foreach(int i in group)
                {
                    lookupUV[i]=new(lookupUV[i].X-shift,lookupUV[i].Y);
                    if(lookupUV[i].X>2+1e-7) throw new SkinRegistrationException("Unqualified repeating skin tile.");
                }
            }
            int Side(int i)=>Math.Sign(positions[ix[i]].X+positions[ix[i+1]].X+positions[ix[i+2]].X);
            var keys=active.SelectMany(i=>ix[i..(i+3)].Select(v=>(Vertex:v,Side:Side(i)))).Distinct().ToArray();
            var charts=keys.Select(k=>new Point(.5+.5*(lookupUV[k.Vertex].X-1)*k.Side,lookupUV[k.Vertex].Y)).ToArray();
            var seams=new bool[keys.Length];
            for(int i=0;i<keys.Length;i++) for(int j=0;j<i;j++)
            {
                if(Vector3.DistanceSquared(positions[keys[i].Vertex],positions[keys[j].Vertex])>4e-6f) continue;
                var d=charts[i]-charts[j];if(d.X*d.X+d.Y*d.Y>.01) seams[i]=seams[j]=true;
            }
            var addresses=new Dictionary<(int,int),SkinRegistration.Address>();
            for(int i=0;i<keys.Length;i++)
                addresses.Add(keys[i],registration.Locate(positions[keys[i].Vertex],charts[i],seams[i]));
            // Repair only the chart addresses incident to a folded source face.
            // Keep the same connected AF island and preserve seam membership.
            var keyIds=keys.Select((key,i)=>(key,i)).ToDictionary(t=>t.key,t=>t.i);
            var visited=new HashSet<(int,int)>();
            while(true)
            {
                var affected=new HashSet<(int,int)>();
                foreach(int i in active)
                {
                    int side=Side(i);
                    var a=ix[i];var b=ix[i+1];var c=ix[i+2];
                    var mapped=new Triangle(addresses[(a,side)].Chart,addresses[(b,side)].Chart,addresses[(c,side)].Chart);
                    var authored=new Triangle(lookupUV[a],lookupUV[b],lookupUV[c]);
                    if(mapped.SignedArea*authored.SignedArea*side>0) continue;
                    affected.Add((a,side));affected.Add((b,side));affected.Add((c,side));
                }
                affected.ExceptWith(visited);
                if(affected.Count==0) break;
                foreach(var key in affected)
                {
                    int index=keyIds[key];var old=addresses[key];
                    addresses[key]=registration.NearestChart(old.Face,charts[index],seams[index]);
                    visited.Add(key);
                }
            }            int subStart=BitConverter.ToUInt16(garment,h+10),subCount=BitConverter.ToUInt16(garment,h+12);
            foreach(int i in active)
            {
                int side=Side(i);if(side==0) throw new SkinRegistrationException("Undefined skin chart side.");
                var v=ix[i..(i+3)];var aa=v.Select(id=>addresses[(id,side)]).ToArray();
                var mapped=new Triangle(aa[0].Chart,aa[1].Chart,aa[2].Chart);
                var original=new Triangle(lookupUV[v[0]],lookupUV[v[1]],lookupUV[v[2]]);
                if(mapped.SignedArea*original.SignedArea*side<=0)
                    throw new SkinRegistrationException($"Skin registration folded mesh {mid}, index {i}.");
                int domain=-1;
                for(int sub=subStart;sub<subStart+subCount;sub++)
                {
                    int sh=src.SubmeshStart+16*sub,start=BitConverter.ToInt32(garment,sh);
                    if(first+i>=start && first+i<start+BitConverter.ToInt32(garment,sh+4)) {domain=sub;break;}
                }
                faces.Add(new(mid,i,domain,v,v.Select(id=>positions[id]).ToArray(),mapped,aa,new(new(0,0),new(1,0),new(0,1))));
            }
        }
        return faces;
    }
}
