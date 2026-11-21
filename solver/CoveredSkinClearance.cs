using System.Numerics;
namespace Aetherform.Geometry;

// A remaining exposed replacement face can cross a panel between its seam
// constraints. Restore the inherited carrier position locally on the covered
// side; accept only a verified improvement from the same cameras.
internal static class CoveredSkinClearance
{
    internal static void Restore(CutSkinAssembly.Assembly assembly,SkinDisplacement.Topology topology,
        Vector3[] cloth,Vector3[] skin,Vector3[] old,bool[] tracked)
    {
        var evidence=VisibleClearance.Inspect(cloth,skin,old,tracked,true);
        if(evidence.SkinFaces.Count==0) return;
        var ids=topology.Nodes;int n=topology.Anchors.Length;
        var goals=new Vector3[n];var count=new int[n];var neighbours=Enumerable.Range(0,n).Select(_=>new HashSet<int>()).ToArray();
        for(int j=0;j<ids.Length;j++)
        {
            var s=assembly.Corners[j].Carrier;var b=s.Barycentric;var v=s.Source.Position;
            goals[ids[j]]+=v[0]+(float)b.X*(v[1]-v[0])+(float)b.Y*(v[2]-v[0]);
            count[ids[j]]++;int next=ids[j-j%3+(j+1)%3];
            neighbours[ids[j]].Add(next);neighbours[next].Add(ids[j]);
        }
        for(int i=0;i<n;i++) goals[i]/=count[i];
        var trial=new Vector3[skin.Length];
        for(int iteration=0;iteration<8;iteration++)
        {
            var strength=new float[n];var ring=new HashSet<int>();
            foreach(int f in evidence.SkinFaces)
                for(int k=0;k<3;k++) ring.Add(ids[3*f+k]);
            for(int depth=0;depth<3;depth++)
            {
                var adjacent=new HashSet<int>();
                foreach(int id in ring)
                {
                    if(strength[id]>0 || topology.Anchors[id]) continue;
                    strength[id]=MathF.Pow(.5f,depth);
                    adjacent.UnionWith(neighbours[id]);
                }
                ring=adjacent;
            }
            bool accepted=false;
            foreach(float step in new[]{1f,.5f,.25f,.125f,.0625f})
            {
                var frozen=new bool[n];
                bool stable=false;
                for(int pass=0;pass<12;pass++)
                {
                    for(int j=0;j<skin.Length;j++) trial[j]=frozen[ids[j]]?skin[j]:Vector3.Lerp(skin[j],goals[ids[j]],step*strength[ids[j]]);
                    stable=true;
                    for(int j=0;j<skin.Length;j+=3)
                    {
                        if(strength[ids[j]]+strength[ids[j+1]]+strength[ids[j+2]]==0) continue;
                        var before=Vector3.Cross(skin[j+1]-skin[j],skin[j+2]-skin[j]);
                        var after=Vector3.Cross(trial[j+1]-trial[j],trial[j+2]-trial[j]);
                        if(before.LengthSquared()<=4e-12f || Vector3.Dot(before,after)>0) continue;
                        frozen[ids[j]]=frozen[ids[j+1]]=frozen[ids[j+2]]=true;
                        stable=false;
                    }
                    if(stable) break;
                }
                if(!stable) continue;
                var check=VisibleClearance.Inspect(cloth,trial,old,tracked,true);
                if(check.Samples.Sum()>=evidence.Samples.Sum() || check.Samples.Where((v,i)=>v>0 && evidence.Samples[i]==0).Any()) continue;
                (skin,trial)=(trial,skin);evidence=check;accepted=true;break;
            }
            if(!accepted || evidence.SkinFaces.Count==0) break;
        }
        for(int j=0;j<skin.Length;j++)
        {
            var c=assembly.Corners[j];
            assembly.Corners[j]=c with { Skin=c.Skin with { PositionMm=skin[j] } };
        }
    }
}
