using Aetherform.Geometry;
using static Aetherform.Geometry.SkinCoverage;

internal static class SkinCoverageCheck
{
    internal static void Run(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path));
        Point ReadPoint()=>new(reader.ReadDouble(),reader.ReadDouble());
        Triangle[] ReadTriangles()=>Enumerable.Range(0,reader.ReadInt32())
            .Select(_=>new Triangle(ReadPoint(),ReadPoint(),ReadPoint())).ToArray();
        var native=ReadTriangles();
        var source=ReadTriangles();
        var cuts=SkinCoverage.Compile(native,source);
        var actual=new double[native.Length];
        foreach(var cut in cuts) actual[cut.Face]+=2*cut.Barycentric.SignedArea;
        var errors=actual.Select(a=>Math.Abs(a-reader.ReadDouble())).ToArray();
        var max=errors.Max();
        Console.WriteLine($"{Path.GetFileName(path)}: {cuts.Count} triangles, {errors.Count(e=>e>1e-6)} mismatched face areas, maximum fraction {max:G4}.");
        if(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            int[] indices=Enumerable.Range(0,native.Length*3).Select(_=>reader.ReadInt32()).ToArray();
            var samples=Enumerable.Range(0,reader.ReadInt32()).Select(_=>
                new SkinCutJoins.Sample(reader.ReadInt32(),ReadPoint(),reader.ReadInt32())).ToArray();
            var positions=Enumerable.Range(0,indices.Length).Select(_=>new System.Numerics.Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())).ToArray();
            var conformed=SkinCutJoins.Conform(cuts,indices,samples,positions);
            Console.WriteLine($"{samples.Length} samples matched to {conformed.Joins.Count} cloth vertices.");
        }
        if(max>1e-6) Environment.ExitCode=1;
    }
}
