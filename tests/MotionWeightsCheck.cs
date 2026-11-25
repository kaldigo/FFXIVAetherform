using System.Numerics;
using Aetherform.Geometry;

internal static class MotionWeightsCheck
{
    internal static void Run()
    {
        var position=new Vector3(10,20,30);
        var authored=new Dictionary<string,float>{{"hip",153/255f},{"leg",102/255f}};
        var body=new Dictionary<string,MotionWeights.Coefficient>{{"hip",new(1,position)}};
        var bodyBones=new HashSet<string>{"hip","leg","afHip"};
        var identity=MotionWeights.Fit(position,position,authored,body,body,bodyBones,4,25);
        Check(identity,authored,"Identity body changed garment weights");
        var targetBody=new Dictionary<string,MotionWeights.Coefficient>{{"afHip",new(1,position)}};
        var converted=MotionWeights.Fit(position,position,new Dictionary<string,float>{{"hip",1}},body,targetBody,bodyBones,4,25);
        Check(converted,new Dictionary<string,float>{{"afHip",1}},"Exact body follower was not converted");
        var moved=position+new Vector3(12,-4,7);
        var movedBody=new Dictionary<string,MotionWeights.Coefficient>{{"afHip",new(1,moved)}};
        var movedFit=MotionWeights.Fit(position,moved,new Dictionary<string,float>{{"hip",1}},body,movedBody,bodyBones,4,25);
        Check(movedFit,new Dictionary<string,float>{{"afHip",1}},"Body shape change broke exact transfer");
        var skirt=new Dictionary<string,float>{{"hip",153/255f},{"skirt",102/255f}};
        var adapted=MotionWeights.Fit(position,position,skirt,body,targetBody,bodyBones,4,25);
        if(adapted["skirt"]!=skirt["skirt"] || Math.Abs(adapted.Values.Sum()-1)>1e-6 || adapted.Count>4)
            throw new Exception("Garment bone mass or output constraints changed");
        // Identical masses with different per-bone position moments must give
        // different optima. Weight subtraction alone cannot pass this control.
        var shifted=position+new Vector3(20,0,0);
        var near=new Dictionary<string,MotionWeights.Coefficient>
        {
            ["hip"]=new(.5,position*.5f),["leg"]=new(.5,position*.5f)
        };
        var far=new Dictionary<string,MotionWeights.Coefficient>
        {
            ["hip"]=new(.5,(position+new Vector3(40,0,0))*.5f),["leg"]=new(.5,position*.5f)
        };
        var first=MotionWeights.Fit(position,shifted,authored,near,near,bodyBones,4,25);
        var second=MotionWeights.Fit(position,shifted,authored,near,far,bodyBones,4,25);
        if(first.Keys.Union(second.Keys).All(n=>Math.Abs(first.GetValueOrDefault(n)-second.GetValueOrDefault(n))<1e-5))
            throw new Exception("Bone position moments were ignored");
        foreach(var weights in new[]{identity,converted,adapted,first,second})
            if(weights.Values.Any(w=>!float.IsFinite(w)||w<0) || Math.Abs(weights.Values.Sum()-1)>1e-6)
                throw new Exception("Invalid fitted weights");
        Console.WriteLine("Motion coefficient identity, exact transfer, garment bones, position moments and packed constraints passed.");
    }

    private static void Check(Dictionary<string,float> actual,Dictionary<string,float> expected,string message)
    {
        if(actual.Keys.Union(expected.Keys).Any(n=>Math.Abs(actual.GetValueOrDefault(n)-expected.GetValueOrDefault(n))>1e-6))
            throw new Exception(message);
    }
}
