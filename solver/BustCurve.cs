using System.Numerics;
using Proteus.Services;

namespace Aetherform.Geometry;

// Fit a quadratic to the fabric surrounding a bust apex in its local tangent frame.
internal static class BustCurve
{
    internal static void Apply(SecondSkinWriter.Source source,SecondSkinWriter.Source mapped,int mesh,GarmentClearance.Mesh native,
        GarmentClearance.Mesh draw,int[] parents,int[] node,Vector3[] centres,Vector3[] normals,
        float[] strengths,bool[] eligible,HashSet<int>[] rings,HashSet<int> pinned,Dictionary<int,Vector3> moves)
    {
        var weights=source.Decls[mesh].FirstOrDefault(f=>f.Usage==1);
        var bones=source.Decls[mesh].FirstOrDefault(f=>f.Usage==2);
        if(weights.Usage!=1 || bones.Usage!=2) return;
        var palette=source.BoneTables[BitConverter.ToUInt16(source.S,native.Header+14)];
        var bust=palette.Select(i=>source.BoneNames[i] is "j_mune_l" or "j_mune_r").ToArray();
        if(!bust.Contains(true)) return;
        int At(SecondSkinWriter.VElem f,int v)=>source.Vb+BitConverter.ToInt32(source.S,native.Header+20+4*f.Stream)+source.S[native.Header+32+f.Stream]*v+f.Offset;
        var seeds=new HashSet<int>();
        foreach(int v in native.Indices)
        {
            int seed=node[v];
            if(strengths[seed]<.55f || seeds.Contains(seed)) continue;
            int w=At(weights,v),b=At(bones,v);
            for(int k=0;k<(weights.Type==17?8:4);k++)
                if(source.S[w+k]>0 && source.S[b+k]<bust.Length && bust[source.S[b+k]]) {seeds.Add(seed);break;}
        }
        if(seeds.Count==0) return;
        var skinTriangles=new List<Vector3>();
        for(int m=mapped.Lod0MeshIndex;m<mapped.Lod0MeshIndex+mapped.Lod0MeshCount;m++)
        {
            var material=mapped.MatNames[BitConverter.ToUInt16(mapped.S,mapped.MeshStart+36*m+8)];
            if(!SecondSkinWriter.IsBodySkinMaterial(material))continue;
            var skin=new GarmentClearance.Mesh(mapped,m);
            skinTriangles.AddRange(skin.Indices.Select(i=>skin.P[i]));
        }
        ClearanceSurface? exposedSkin=skinTriangles.Count>=3 && Enumerable.Range(0,skinTriangles.Count/3).Any(i=>
            Vector3.Cross(skinTriangles[3*i+1]-skinTriangles[3*i],skinTriangles[3*i+2]-skinTriangles[3*i]).LengthSquared()>1e-16f)
            ? new(skinTriangles.ToArray()) : null;
        var points=draw.P.Select((p,i)=>p+moves.GetValueOrDefault(i)).ToArray();
        var corrections=new Dictionary<int,(Vector3 Delta,float Weight)>();
        foreach(int seed in seeds)
        {
            var nearby=new HashSet<int>(rings[seed]){seed};
            foreach(int neighbour in rings[seed]) nearby.UnionWith(rings[neighbour]);
            var n=normals[seed];var ex=Vector3.Cross(Vector3.UnitY,n);
            if(ex.LengthSquared()<.01f)continue;
            ex=Vector3.Normalize(ex);var ey=Vector3.Cross(n,ex);
            var centre=centres[seed];var system=new double[6,7];var verts=new HashSet<int>();
            var octants=new bool[8];
            for(int f=0;f<parents.Length;f++)
            {
                int parent=parents[f];if(!eligible[parent])continue;
                var basis=native.Indices.AsSpan(3*parent,3);
                if(!nearby.Contains(node[basis[0]])||!nearby.Contains(node[basis[1]])||!nearby.Contains(node[basis[2]]))continue;
                var ids=draw.Indices.AsSpan(3*f,3);var a=points[ids[0]];var b=points[ids[1]];var c=points[ids[2]];
                var normal=Vector3.Cross(b-a,c-a);float area=Vector3.Dot(normal,n);if(area<=0)continue;
                foreach(int v in ids)verts.Add(v);
                var p=(a+b+c)/3-centre;double x=Vector3.Dot(p,ex)/20,y=Vector3.Dot(p,ey)/20,z=Vector3.Dot(p,n);
                double r=Math.Sqrt(x*x+y*y);
                if(r<.67 || r>1.35 || Math.Abs(z)>19)continue;
                octants[Math.Min(7,(int)((Math.Atan2(y,x)+Math.PI)*4/Math.PI))]=true;
                double[] row=[1,x,y,x*x,x*y,y*y];
                for(int i=0;i<6;i++){for(int j=0;j<6;j++)system[i,j]+=area*row[i]*row[j];system[i,6]+=area*row[i]*z;}
            }
            if(octants.Any(filled=>!filled)||!Solve(system))continue;
            foreach(int v in verts)
            {
                if(pinned.Contains(v))continue;
                var p=points[v]-centre;double x=Vector3.Dot(p,ex)/20,y=Vector3.Dot(p,ey)/20;
                double r=Math.Sqrt(x*x+y*y);if(r>=1 || Math.Abs(Vector3.Dot(p,n))>18)continue;
                double z=system[0,6]+system[1,6]*x+system[2,6]*y+system[3,6]*x*x+system[4,6]*x*y+system[5,6]*y*y;
                float t=Math.Clamp((float)((1-r)/.45),0,1);float blend=t*t*(3-2*t);
                float shift=(float)(z-Vector3.Dot(p,n));
                if(!float.IsFinite(shift)||Math.Abs(shift)>12)continue;
                var prior=corrections.GetValueOrDefault(v);
                corrections[v]=(prior.Delta+n*(shift*blend*blend),prior.Weight+blend);
            }
        }
        foreach(var (v,correction) in corrections)
            {
            if(correction.Weight<=0)continue;
            var displacement=correction.Delta/correction.Weight;
            if(exposedSkin!=null)
            {
                float distance=exposedSkin.Nearest(points[v]).Distance;
                float t=Math.Clamp((distance-8)/12,0,1);
                displacement*=t*t*(3-2*t);
            }
            moves[v]=moves.GetValueOrDefault(v)+displacement;
        }
    }
    private static bool Solve(double[,] matrix)
    {
        for(int i=0;i<6;i++)
        {
            int pivot=i;for(int j=i+1;j<6;j++)if(Math.Abs(matrix[j,i])>Math.Abs(matrix[pivot,i]))pivot=j;
            if(Math.Abs(matrix[pivot,i])<1e-6)return false;
            for(int k=i;k<=6;k++)(matrix[i,k],matrix[pivot,k])=(matrix[pivot,k],matrix[i,k]);
            double scale=matrix[i,i];for(int k=i;k<=6;k++)matrix[i,k]/=scale;
            for(int j=0;j<6;j++)if(i!=j){double amount=matrix[j,i];for(int k=i;k<=6;k++)matrix[j,k]-=amount*matrix[i,k];}
        }
        return true;
    }
}
