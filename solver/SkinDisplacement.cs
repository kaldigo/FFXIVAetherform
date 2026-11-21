using System.Numerics;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal static class SkinDisplacement
{
    private readonly record struct Address(int A,int B,int C,long WeightA,long WeightB);
    private static Address Key(int face,Point p,int[] indices)
    {
        Span<(int Id,double W)> v=stackalloc (int,double)[]{(indices[3*face],1-p.X-p.Y),(indices[3*face+1],p.X),(indices[3*face+2],p.Y)};
        for(int i=0;i<3;i++) if(Math.Abs(v[i].W)<=1e-8) v[i]=(int.MaxValue,0);
        if(v[0].Id>v[1].Id) (v[0],v[1])=(v[1],v[0]);
        if(v[1].Id>v[2].Id) (v[1],v[2])=(v[2],v[1]);
        if(v[0].Id>v[1].Id) (v[0],v[1])=(v[1],v[0]);
        return new(v[0].Id,v[1].Id,v[2].Id,(long)Math.Round(v[0].W*1e10),(long)Math.Round(v[1].W*1e10));
    }

    internal static void Apply(NativeSkinChart target,CutSkinAssembly.Assembly assembly,byte[] morphed,GarmentClassification? classification)
    {
        var corners=assembly.Corners;
        if(corners.Length==0) return;
        var ids=new int[corners.Length];var registry=new Dictionary<Address,int>();
        var native=new List<Vector3>();var raw=new List<Vector3>();var count=new List<int>();
        for(int j=0;j<ids.Length;j++)
        {
            var cut=assembly.Cut.Triangles[j/3];
            var bary=cut.Barycentric.Polygon[j%3];var key=Key(cut.Face,bary,target.PhysicalCorners);
            if(!registry.TryGetValue(key,out int id))
            {
                id=registry.Count;registry.Add(key,id);native.Add(default);raw.Add(default);count.Add(0);
            }
            ids[j]=id;var q=target.PositionsMm;
            var basePoint=q[3*cut.Face]+(float)bary.X*(q[3*cut.Face+1]-q[3*cut.Face])+(float)bary.Y*(q[3*cut.Face+2]-q[3*cut.Face]);
            native[id]+=basePoint;raw[id]+=corners[j].Skin.PositionMm-basePoint;count[id]++;
        }
        int n=registry.Count;var fixedNode=new bool[n];
        for(int i=0;i<n;i++){native[i]/=count[i];raw[i]/=count[i];}
        var edges=new Dictionary<(int,int),int>();
        for(int f=0;f<target.PhysicalCorners.Length;f+=3)
        for(int j=0;j<3;j++)
        {
            int a=target.PhysicalCorners[f+j],b=target.PhysicalCorners[f+(j+1)%3];
            if(a>b) (a,b)=(b,a);edges[(a,b)]=edges.GetValueOrDefault((a,b))+1;
        }
        var boundary=edges.Where(e=>e.Value==1).Select(e=>e.Key).ToHashSet();
        var endpoints=boundary.SelectMany(e=>new[]{e.Item1,e.Item2}).ToHashSet();
        foreach(var (key,id) in registry)
        {
            if(key.C!=int.MaxValue || !(key.B==int.MaxValue?endpoints.Contains(key.A):boundary.Contains((key.A,key.B)))) continue;
            fixedNode[id]=true;raw[id]=default;
        }
        foreach(var (k,refs) in assembly.Cut.Joins)
            foreach(var c in refs)
            {
                int id=ids[3*c.Triangle+c.Index];
                fixedNode[id]=true;raw[id]=assembly.Knots[k].PositionMm-native[id];
            }

        // Screen the displacement field on the physical cut surface. The AF
        // base and its authored UV splits stay exact. Units are millimetres.
        var mass=new double[n];var stiffness=new Dictionary<(int,int),double>();
        var sourceNormals=new Vector3[n];var outputNormals=new Vector3[n];
        for(int j=0;j<ids.Length;j+=3)
        {
            int a=ids[j],b=ids[j+1],c=ids[j+2];var p=native[a];var q=native[b];var r=native[c];
            var cross=Vector3.Cross(q-p,r-p);double twice=cross.Length();
            if(twice<=2e-8) continue;
            sourceNormals[a]+=cross;sourceNormals[b]+=cross;sourceNormals[c]+=cross;
            mass[a]+=twice/6;mass[b]+=twice/6;mass[c]+=twice/6;
            Add(b,c,Vector3.Dot(q-p,r-p));Add(a,c,Vector3.Dot(p-q,r-q));Add(a,b,Vector3.Dot(p-r,q-r));
            void Add(int x,int y,double dot)
            {
                if(x>y) (x,y)=(y,x);
                stiffness[(x,y)]=stiffness.GetValueOrDefault((x,y))+72*dot/twice;
            }
        }
        var entries=stiffness.Select(p=>(p.Key.Item1,p.Key.Item2,p.Value)).ToArray();
        var diag=mass.Select(m=>Math.Max(m,1e-9)).ToArray();
        foreach(var (a,b,w) in entries){diag[a]+=w;diag[b]+=w;}
        var solved=raw.ToArray();
        // Symmetric sparse elimination with an updating minimum-degree queue.
        // Factor once for all three coordinates. Very thin cut faces have a
        // large stiffness ratio and make diagonal-preconditioned CG unreliable.
        var rows=Enumerable.Range(0,n).Select(_=>new Dictionary<int,double>()).ToArray();
        var rhs=new double[n,3];
        for(int i=0;i<n;i++)
            for(int axis=0;axis<3;axis++) rhs[i,axis]=Math.Max(1e-9,mass[i])*raw[i][axis];
        foreach(var (i,j,w) in entries)
        {
            if(i==j) continue;
            if(!fixedNode[i] && !fixedNode[j]){rows[i][j]=-w;rows[j][i]=-w;}
            else if(fixedNode[i]!=fixedNode[j])
            {
                int f=fixedNode[i]?i:j,k=fixedNode[i]?j:i;
                for(int axis=0;axis<3;axis++) rhs[k,axis]+=w*raw[f][axis];
            }
        }
        var queue=new PriorityQueue<int,(int Degree,int Id)>();var eliminated=new bool[n];var order=new List<int>();
        for(int i=0;i<n;i++) if(!fixedNode[i]) queue.Enqueue(i,(rows[i].Count,i));
        while(queue.TryDequeue(out int i,out var rank))
        {
            if(eliminated[i] || rank.Degree!=rows[i].Count) continue;
            double pivot=diag[i];
            if(!(pivot>0)) throw new InvalidDataException($"Nonpositive skin stiffness pivot at {i}.");
            eliminated[i]=true;order.Add(i);
            var neighbours=rows[i].ToArray();
            for(int a=0;a<neighbours.Length;a++)
            {
                var (j,v)=neighbours[a];rows[j].Remove(i);double scale=v/pivot;
                diag[j]-=scale*v;
                for(int axis=0;axis<3;axis++) rhs[j,axis]-=scale*rhs[i,axis];
                for(int b=0;b<a;b++)
                {
                    var (k,u)=neighbours[b];double adjusted=rows[j].GetValueOrDefault(k)-scale*u;
                    rows[j][k]=adjusted;rows[k][j]=adjusted;
                }
            }
            foreach(var (j,_) in neighbours) queue.Enqueue(j,(rows[j].Count,j));
        }
        for(int r=order.Count-1;r>=0;r--)
        {
            int i=order[r];
            for(int axis=0;axis<3;axis++)
            {
                double v=rhs[i,axis];
                foreach(var (j,c) in rows[i]) v-=c*rhs[j,axis];
                rhs[i,axis]=v/diag[i]; solved[i][axis]=(float)rhs[i,axis];
            }
        }
        // A garment's embedded skin is the reference for the clearance it
        // actually had under that garment, including layered cloth and armor.
        var source=Proteus.Services.SecondSkinWriter.Parse(morphed);var shells=new List<Vector3>();
        for(int m=source.Lod0MeshIndex;m<source.Lod0MeshIndex+source.Lod0MeshCount;m++)
        {
            int h=source.MeshStart+36*m;
            if(Proteus.Services.SecondSkinWriter.IsBodySkinMaterial(source.MatNames[BitConverter.ToUInt16(morphed,h+8)])) continue;
            var mesh=new GarmentClearance.Mesh(source,m);
            for(int i=0;i<mesh.Indices.Length;i+=3)
            {
                if(classification?.Closure(m-source.Lod0MeshIndex,i/3)==true) continue;
                int a=mesh.Indices[i],b=mesh.Indices[i+1],c=mesh.Indices[i+2];
                var cross=Vector3.Cross(mesh.P[b]-mesh.P[a],mesh.P[c]-mesh.P[a]);
                if(cross.LengthSquared()<1e-16f) continue;
                if(Vector3.Dot(cross,mesh.N[a]+mesh.N[b]+mesh.N[c])<0) (b,c)=(c,b);
                shells.Add(mesh.P[a]);shells.Add(mesh.P[b]);shells.Add(mesh.P[c]);
            }
        }
        if(shells.Count>0)
        {
            var body=new ClearanceSurface(shells.ToArray());
            var carrier=new Vector3[n];var normals=new Vector3[n];
            for(int j=0;j<ids.Length;j++)
            {
                int i=ids[j];var sample=corners[j].Carrier;var v=sample.Source.Position;var bc=sample.Barycentric;
                carrier[i]+=v[0]+(float)bc.X*(v[1]-v[0])+(float)bc.Y*(v[2]-v[0]);normals[i]+=corners[j].Skin.Normal;
            }
            for(int i=0;i<n;i++)
            {
                carrier[i]/=count[i];
                if(fixedNode[i] || normals[i].LengthSquared()<1e-10f) continue;
                var p=carrier[i];
                if(!body.Ray(p,Vector3.Normalize(normals[i]),out var normal)) continue;
                float lost=Vector3.Dot(normal,native[i]+solved[i]-p);
                if(lost>0) solved[i]-=lost*normal;
            }
            FiniteClearance.Solve(body,ids,native,solved,carrier,fixedNode,normals);
        }
        for(int j=0;j<ids.Length;j+=3)
        {
            int a=ids[j],b=ids[j+1],c=ids[j+2];var p=native[a]+solved[a];var q=native[b]+solved[b];var r=native[c]+solved[c];
            var cross=Vector3.Cross(q-p,r-p);outputNormals[a]+=cross;outputNormals[b]+=cross;outputNormals[c]+=cross;
        }
        for(int j=0;j<ids.Length;j++)
        {
            int i=ids[j];var skin=corners[j].Skin;var normal=skin.Normal;
            if(sourceNormals[i].LengthSquared()>1e-12f && outputNormals[i].LengthSquared()>1e-12f)
            {
                var a=Vector3.Normalize(sourceNormals[i]);var b=Vector3.Normalize(outputNormals[i]);float dot=Vector3.Dot(a,b);
                if(dot>-.9999f && dot<.999999f)
                    normal=Vector3.Normalize(Vector3.Transform(normal,Quaternion.Normalize(new(Vector3.Cross(a,b),1+dot))));
            }
            corners[j]=corners[j] with {Skin=skin with {PositionMm=native[i]+solved[i],Normal=normal}};
        }
    }
}
