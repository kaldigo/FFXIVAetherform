using System.Numerics;

namespace Aetherform.Geometry;

internal static class VisibleClearance
{
    internal sealed record Evidence(int[] Samples,Vector3[] Directions,HashSet<int> SkinFaces);

    // Input positions and depth are in millimetres. Count only newly exposed
    // replacement skin where the morphed garment skin used to clear the cloth.
    internal static Evidence Inspect(Vector3[] cloth, Vector3[] cut, Vector3[] carrier, bool[] tracked,bool captureSkin=false)
    {
        if(cloth.Length!=3*tracked.Length || cut.Length%3!=0 || carrier.Length%3!=0)
            throw new ArgumentException("Expected triangle corner arrays.");
        var result=new Evidence(new int[tracked.Length],new Vector3[tracked.Length],[]);
        if(cloth.Length==0 || cut.Length==0 || carrier.Length==0 || !tracked.Contains(true)) return result;
        Vector3[][] sets=[cloth,cut,carrier];
        var projected=sets.Select(s=>new Vector3[s.Length]).ToArray();
        for(int elevation=-1;elevation<=1;elevation++)
        for(int azimuth=0;azimuth<8;azimuth++)
        {
            float angle=(float)(azimuth*Math.PI/4);
            var view=Vector3.Normalize(new(MathF.Cos(angle),elevation*.55f,MathF.Sin(angle)));
            var right=Vector3.Normalize(Vector3.Cross(Vector3.UnitY,view));
            var up=Vector3.Cross(view,right);
            for(int s=0;s<sets.Length;s++)
                for(int i=0;i<sets[s].Length;i++)
                {
                    var p=sets[s][i];
                    projected[s][i]=new(2*Vector3.Dot(p,right),2*Vector3.Dot(p,up),Vector3.Dot(p,view));
                }
            int left=checked((int)MathF.Floor(projected[0].Min(p=>p.X))-1);
            int bottom=checked((int)MathF.Floor(projected[0].Min(p=>p.Y))-1);
            int width=checked((int)MathF.Ceiling(projected[0].Max(p=>p.X))-left+2);
            int height=checked((int)MathF.Ceiling(projected[0].Max(p=>p.Y))-bottom+2);
            // Limit working memory while retaining the offline half-mm sample grid.
            const int band=128;
            int capacity=checked(width*Math.Min(height,band));
            var owner=new int[capacity];
            var skinOwner=captureSkin?new int[capacity]:null;
            var depth=sets.Select(_=>new float[capacity]).ToArray();
            for(int y=bottom;y<bottom+height;y+=band)
            {
                int rows=Math.Min(band,bottom+height-y);
                for(int s=0;s<sets.Length;s++)
                {
                    Array.Fill(depth[s],float.NegativeInfinity,0,width*rows);
                    Raster(projected[s],left,y,width,rows,depth[s],s==0?owner:s==1?skinOwner:null);
                }
                for(int i=0;i<width*rows;i++)
                {
                    int face=owner[i];
                    if(!float.IsFinite(depth[0][i]) || !tracked[face] || !float.IsFinite(depth[2][i])) continue;
                    float threshold=depth[0][i]+.01f;
                    if(depth[1][i]>threshold && depth[2][i]<=threshold)
                    {
                        result.Samples[face]++;
                        if(skinOwner!=null) result.SkinFaces.Add(skinOwner[i]);
                        result.Directions[face]+=view;
                    }
                }
            }
        }
        return result;
    }

    private static void Raster(Vector3[] corners,int left,int bottom,int width,int height,float[] depth,int[]? owner)
    {
        for(int i=0;i<corners.Length;i+=3)
        {
            var p=corners[i]; var q=corners[i+1]; var r=corners[i+2];
            var u=q-p; var v=r-p;float determinant=u.X*v.Y-v.X*u.Y;
            if(MathF.Abs(determinant)<1e-8f) continue;
            int y0=Math.Max(bottom,(int)MathF.Floor(MathF.Min(p.Y,MathF.Min(q.Y,r.Y))));
            int y1=Math.Min(bottom+height-1,(int)MathF.Ceiling(MathF.Max(p.Y,MathF.Max(q.Y,r.Y))));
            if(y1<y0) continue;
            int x0=Math.Max(left,(int)MathF.Floor(MathF.Min(p.X,MathF.Min(q.X,r.X))));
            int x1=Math.Min(left+width-1,(int)MathF.Ceiling(MathF.Max(p.X,MathF.Max(q.X,r.X))));
            for(int y=y0;y<=y1;y++)
            for(int x=x0;x<=x1;x++)
            {
                float dx=x+.5f-p.X,dy=y+.5f-p.Y;
                float b=(dx*v.Y-dy*v.X)/determinant,c=(u.X*dy-u.Y*dx)/determinant;
                if(b<0 || c<0 || b+c>1) continue;
                float z=p.Z+b*u.Z+c*v.Z;
                int pixel=(y-bottom)*width+x-left;
                if(z<=depth[pixel]) continue;
                depth[pixel]=z;
                if(owner!=null) owner[pixel]=i/3;
            }
        }
    }
}
