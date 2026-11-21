using System.Numerics;
using Proteus.Services;
namespace Aetherform.Geometry;

internal static class ModelPanelEdits
{
    internal static void Write(byte[] data,SecondSkinWriter.Source layout,int mesh,GarmentClearance.Mesh draw,Dictionary<int,Vector3> moves,
        Dictionary<int,(Vector3 Normal,float Blend)>? shading=null)
    {
        if(moves.Count==0) return;
        var before=draw.P;var after=(Vector3[])before.Clone();
        foreach(var (i,delta) in moves)
        {
            after[i]+=delta;
            draw.Write(data,i,after[i]);
            if(draw.ShapeTargets.TryGetValue(i,out var variants)) foreach(int v in variants) after[v]=before[v]+delta;
        }
        var strides=Enumerable.Range(0,3).Select(s=>(int)data[draw.Header+32+s]).ToArray();
        var starts=Enumerable.Range(0,3).Select(s=>layout.Vb+BitConverter.ToInt32(data,draw.Header+20+4*s)).ToArray();
        var streams=Enumerable.Range(0,3).Select(s=>strides[s]==0?[]:data.AsSpan(starts[s],strides[s]*before.Length).ToArray()).ToArray();
        PanelStreams.UpdateShading(streams,strides,layout.Decls[mesh],before,after,draw.Indices,shading);
        for(int s=0;s<3;s++) if(strides[s]>0) streams[s].CopyTo(data,starts[s]);
    }
}
