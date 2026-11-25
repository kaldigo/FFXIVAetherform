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
        var own=new HashSet<string>{"skirt"};
        var sourceSkin=new Dictionary<string,float>{{"hip",1}};
        var afSkin=new Dictionary<string,float>{{"afHip",1}};
        Check(SkirtMotionWeights.Fit(authored,authored,sourceSkin,sourceSkin,own),authored,"Unchanged skin changed body binding");
        Check(SkirtMotionWeights.Fit(sourceSkin,sourceSkin,sourceSkin,afSkin,own),afSkin,"Late body follower did not convert");
        var rigged=new Dictionary<string,float>{{"hip",153/255f},{"skirt",102/255f}};
        var rebound=SkirtMotionWeights.Fit(rigged,rigged,sourceSkin,afSkin,own);
        Check(rebound,new Dictionary<string,float>{{"afHip",153/255f},{"skirt",102/255f}},"Late binding changed skirt animation mass");
        Check(SkirtMotionWeights.Fit(rigged,rigged,sourceSkin,afSkin,rigged.Keys.ToHashSet()),rigged,"Fully protected skinning changed");
        var dense=Enumerable.Range(0,12).ToDictionary(i=>"body"+i,i=>1/12f);
        var limited=SkirtMotionWeights.Fit(rigged,rigged,sourceSkin,dense,own);
        if(limited.Count>8 || limited["skirt"]!=rigged["skirt"] || Math.Abs(limited.Values.Sum()-1)>1e-6)
            throw new Exception("Late binding exceeded its packed free budget");
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
        var random=new Random(419);
        for(int trial=0;trial<2000;trial++)
        {
            Vector3 RandomVector()=>new((float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1);
            var n=Vector3.Normalize(RandomVector());var pivot=200*RandomVector();
            var host=new ThighMotionEnvelope.Group((float)random.NextDouble(),150*RandomVector());
            var cloth=new ThighMotionEnvelope.Group((float)random.NextDouble(),150*RandomVector());
            var moment=host.Moment-cloth.Moment-(host.Mass-cloth.Mass)*pivot;
            var rotation=Quaternion.CreateFromAxisAngle(Vector3.Normalize(RandomVector()),(float)random.NextDouble()*ThighMotionEnvelope.RotationRadians);
            var translation=ThighMotionEnvelope.TranslationMm*Vector3.Normalize(RandomVector())*(float)random.NextDouble();
            float closing=Vector3.Dot(n,Vector3.Transform(moment,rotation)-moment+(host.Mass-cloth.Mass)*translation);
            if(closing>ThighMotionEnvelope.Closure(n,pivot,host,cloth)+.001f)
                throw new Exception("Thigh clearance envelope underestimated an independently sampled rigid motion");
            if(ThighMotionEnvelope.Closure(n,pivot,host,host)!=0)
                throw new Exception("Common thigh motion did not cancel");
        }
        if(ThighMotionEnvelope.Side("j_sk_s_a_l")!=-1 || ThighMotionEnvelope.Side("j_asi_a_l")!=0 ||
            ThighMotionEnvelope.Side("ya_daitai_phys_r")!=1 || !ThighMotionEnvelope.IsSkirt("j_sk_s_a_l"))
            throw new Exception("Thigh and garment rig roles were confused");
        if(!ThighMotionEnvelope.Exterior(Vector3.UnitX,new(100,900,0),Vector3.Zero) ||
            ThighMotionEnvelope.Exterior(-Vector3.UnitX,new(30,900,0),Vector3.Zero) ||
            !ThighMotionEnvelope.Exterior(Vector3.UnitZ,new(0,900,80),Vector3.Zero))
            throw new Exception("Inner leg planes were confused with the skirt's exterior envelope");
        Console.WriteLine("Thigh envelope bounds 2,000 independently sampled rotations/translations and cancels common motion.");
        Console.WriteLine("Motion coefficient identity, exact transfer, garment bones, position moments and packed constraints passed.");
    }

    private static void Check(Dictionary<string,float> actual,Dictionary<string,float> expected,string message)
    {
        if(actual.Keys.Union(expected.Keys).Any(n=>Math.Abs(actual.GetValueOrDefault(n)-expected.GetValueOrDefault(n))>1e-6))
            throw new Exception(message);
    }
}
