using System.Numerics;
using Proteus.Services;
using static Aetherform.Geometry.SkinCoverage;

namespace Aetherform.Geometry;

internal sealed class CutSkinAssembly
{
    internal sealed record Knot(Vector3 PositionMm, NativeSkinChart.Influence[] Weights);
    internal sealed record Corner(NativeSkinChart.Payload Skin,GarmentSkinField.Sample Carrier);
    internal sealed record Assembly(SkinCutJoins.Result Cut, Dictionary<int,Knot> Knots, Corner[] Corners,IReadOnlyList<SkinRoutes.ClothKnot> Schedule);

    internal static Assembly Prepare(byte[] original, byte[] morphed, NativeSkinChart target)
        => PrepareForFit(original,morphed,target,null);

    internal static Assembly PrepareForFit(byte[] original,byte[] morphed,NativeSkinChart target,byte[]? bare)
    {
        var routes = SkinRoutes.For(original);
        var registered = GarmentSkin.Register(morphed,target,routes);
        var coverage = Compile(target.Cutting, registered.Select(f => new Region(f.UV,f.Domain)));
        var scheduled = routes?.Knots ?? GarmentBoundaryRoutes.Trace(original,target,registered,coverage);
        var seams=new SkinSeamCoverage();
        if(bare!=null && routes==null) seams.Complete(coverage,registered,target,original,bare);
        var samples = scheduled.Select(k => new SkinCutJoins.Sample(k.Face,k.Target,k.Variable)).ToArray();
        var surface = SkinCutJoins.Conform(coverage,target.PhysicalCorners,samples,target.PositionsMm);
        var model = SecondSkinWriter.Parse(morphed);
        var knots = new Dictionary<int,Knot>();
        var clothCorners = new Dictionary<(int Mesh,int First),Vector3[]>();
        foreach (var k in scheduled)
        {
            var key = (k.Mesh,k.First);
            if (!clothCorners.TryGetValue(key,out var corners))
            {
                int header = model.MeshStart+36*k.Mesh;
                var pos = model.Decls[k.Mesh].First(e => e.Usage == 0);
                int first = BitConverter.ToInt32(morphed,header+16)+k.First;
                int stream = model.Vb+BitConverter.ToInt32(morphed,header+20+4*pos.Stream);
                corners = new Vector3[3];
                var values = new float[4];
                for (int i=0;i<3;i++)
                {
                    int vertex = BitConverter.ToUInt16(morphed,model.Ib+2*(first+i));
                    int address = stream+vertex*morphed[header+32+pos.Stream]+pos.Offset;
                    SecondSkinWriter.ReadTyped(morphed,address,pos.Type,values);
                    corners[i] = 1000*new Vector3(values[0],values[1],values[2]);
                }
                clothCorners.Add(key,corners);
            }
            var p = corners[0]+(float)k.Source.X*(corners[1]-corners[0])+(float)k.Source.Y*(corners[2]-corners[0]);
            if (knots.TryGetValue(k.Variable,out var previous))
            {
                if (Vector3.Distance(previous.PositionMm,p)>.001f)
                    throw new InvalidDataException($"Source cloth evaluations disagree at shared knot {k.Variable}.");
            }
            else knots.Add(k.Variable,new(p,target.Evaluate(k.Face,k.Target).Weights));
        }
        var field=new GarmentSkinField(target,registered);
        var cornersOut=new Corner[3*surface.Triangles.Count];
        for(int i=0;i<surface.Triangles.Count;i++)
        {
            var cut=surface.Triangles[i];
            var coordinates=cut.Barycentric.Polygon;
            for(int j=0;j<3;j++)
            {
                var p=coordinates[j];
                var skin=target.Evaluate(cut.Face,p);
                var carrier=field.At(cut,seams.Carrier(target,cut,p));
                cornersOut[3*i+j]=new(skin with { PositionMm=skin.PositionMm+carrier.OffsetMm },carrier);
            }
        }
        foreach(var (variable,corners) in surface.Joins)
        {
            var knot=knots[variable];
            foreach(var corner in corners)
            {
                int index=3*corner.Triangle+corner.Index;
                var existing=cornersOut[index];
                cornersOut[index]=existing with { Skin=existing.Skin with { PositionMm=knot.PositionMm,Weights=knot.Weights } };
            }
        }
        return new(surface,knots,cornersOut,scheduled);
    }
}
