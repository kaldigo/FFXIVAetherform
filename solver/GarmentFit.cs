using System.Buffers.Binary;
using Dalamud.Plugin.Services;
using Proteus.Services;
using XivLiveMesh;

namespace Aetherform.Geometry;

public static class GarmentFit
{
    public static byte[] Generate(byte[] garment, string slot, ushort modelRace,
        IReadOnlyDictionary<string,string> originals, IReadOnlyDictionary<string,string> targets,
        string[] legOptions, byte[]? deformer, byte[]? skeleton, string bindingDirectory, GarmentClassification? classification, IPluginLog log, string chestOption, string legOption)
    {
        if (classification != null && !classification.Matches(garment)) classification = null;
        var uv = new UVRemapService(log, AppContext.BaseDirectory);
        var pairs = new Dictionary<string,BodyRetarget.SlotPair>();
        var bodies = new Dictionary<string,byte[]>();
        foreach (var (part, path) in targets)
        {
            var target = File.ReadAllBytes(path);
            if (part == "_dwn") ApplyLegShapes(target, legOptions);
            var vanilla = File.ReadAllBytes(originals[part]);
            bodies[part] = target;
            if (slot is "_glv" or "_sho")
            {
                var refusal = BodyRetarget.BuildPair(part, vanilla, target, part, false, null, null, uv, out var pair);
                if (refusal != null) throw new InvalidDataException(refusal);
                pairs[part] = pair;
            }
        }

        RacialModelBake.Wearer? wearer = null;
        bool sharedMale = modelRace == 101;
        var route = new RacialModelBake.Route(modelRace, 201, !sharedMale);
        if (modelRace != 201)
        {
            var rig = skeleton == null ? null : HavokSkeleton.Read(skeleton);
            if (rig == null || deformer == null) throw new InvalidDataException("Missing race skeleton or deformer.");
            wearer = new(modelRace, rig.ParentMap(), new PbdFile(deformer));
            garment = route.Into(garment, wearer, out var refusal) ?? throw new InvalidDataException(refusal);
        }
        var model = ModelPartReader.Read(garment) ?? throw new InvalidDataException("Unreadable garment.");
        byte[] result;
        var origins = new Dictionary<int,ClothSubdivision.Origins>();
        if (slot is "_glv" or "_sho")
        {
            var ends = KeepEnds.Read(slot, model, garment) ?? throw new InvalidDataException("Unreadable limb weights.");
            var plan = KeepEnds.Plan(model, garment, ends, pairs[ends.Limb], pairs[slot], true);
            result = RefitCore.MatchSkinToBody(plan.Model, originals[slot], targets.Values.ToArray(), uv, false, log);
        }
        else
        {
            var morph = new NativeMorph(bodies,chestOption,legOption);
            var morphed = morph.Apply(garment);
            MorphContinuity.Apply(garment,morphed);
            morphed=GarmentSupport.Apply(garment,morphed,classification);
            morphed = BodyMotionTransfer.Apply(garment,morphed,morph,originals,bodies);
            var overlays=SkinOverlays.Find(garment);
            result = DensitySubdivision.Apply(garment,morphed,new BodyDensity(bodies.Where(b=>b.Key is "_top" or "_dwn").Select(b=>b.Value)),origins,overlays);
            // Seam retessellation also needs the rest-space lineage for draws
            // that already met the density target and were not subdivided.
            var restLayout=SecondSkinWriter.Parse(garment);
            for(int mesh=restLayout.Lod0MeshIndex;mesh<restLayout.Lod0MeshIndex+restLayout.Lod0MeshCount;mesh++)
            {
                if(origins.ContainsKey(mesh) || SecondSkinWriter.IsBodySkinMaterial(restLayout.MatNames[BitConverter.ToUInt16(garment,restLayout.MeshStart+36*mesh+8)])) continue;
                var rest=new GarmentClearance.Mesh(restLayout,mesh);
                origins.Add(mesh,new(rest.P.Select(p=>p/1000).ToArray(),Enumerable.Range(0,rest.Indices.Length/3).ToArray()));
            }
            PanelPeaks.Apply(garment,morphed,result,classification,NativeMorph.VanillaSurface,origins);
            var joins=ClothSubdivision.IdentifyJoins(SecondSkinWriter.Parse(garment),out bool unmatchedCut);
            bool sewnTop=slot=="_top" && (unmatchedCut || joins.Count>0) && SkinRoutes.For(garment)==null;
            if(overlays.Count>0 || sewnTop)
            {
                // Keep the original shared boundary and its skinning while converting to the AF material and UV layout.
                ClothAttachments.Apply(garment,result,origins,classification);
                result=MatchRetainedSkin(garment,result,originals[slot],targets.Values.ToArray(),uv,log);
            }
            else try
            {
                result = GarmentSkinWriter.Replace(garment, morphed, result, bodies[slot],File.ReadAllBytes(originals[slot]),origins,classification,slot=="_dwn");
            }
            catch (SkinRegistrationException ex) when (SkinRoutes.For(garment)==null)
            {
                log.Warning("[Aetherform] Preserved morphed garment skin for {Slot}. AF registration needs review: {Reason}",slot,ex.Message);
                // Skin registration failed before clearance or attachment placement. The
                // subdivided cloth still has its source triangle lineage on this path.
                ClothAttachments.Apply(garment,result,origins,classification);
                result=MatchRetainedSkin(garment,result,originals[slot],targets.Values.ToArray(),uv,log);
            }
        }
        result=SkinSurfaceContact.Apply(garment,result,origins);
        if(slot is "_top" or "_dwn") result=SkirtMotionClearance.Apply(garment,result,bodies,
            new[]{"_top","_dwn"}.Select(part=>File.ReadAllBytes(originals[part])),log,origins);
        if (wearer != null && !sharedMale)
            result = route.Back(result, wearer, out var refusal) ?? throw new InvalidDataException(refusal);
        if (ModelPartReader.Read(result) == null) throw new InvalidDataException("Generated model failed read-back.");
        var layout = SecondSkinWriter.Parse(result);
        for (int mesh = 0; mesh < BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(layout.Mh + 4)); mesh++)
        {
            int material = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(layout.MeshStart + 36 * mesh + 8));
            if (material >= layout.MatCount)
                throw new InvalidDataException($"Mesh {mesh} references material {material} outside the material table.");
        }
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), checked((ushort)layout.MatCount));
        return result;
    }

    private static byte[] MatchRetainedSkin(byte[] source,byte[] fitted,string vanilla,string[] targets,UVRemapService uv,IPluginLog log)
    {
        // The mirrored skin layout assigns left and right from authored X and triangle
        // connectivity. A fitted centerline can move across X=0 without changing UV islands.
        var layout=SecondSkinWriter.Parse(source);
        var skin=Enumerable.Range(layout.Lod0MeshIndex,layout.Lod0MeshCount)
            .Where(m=>SecondSkinWriter.IsBodySkinMaterial(layout.MatNames[BitConverter.ToUInt16(source,layout.MeshStart+36*m+8)])).ToArray();
        if(skin.Length==0) return fitted;
        var input=(byte[])fitted.Clone();
        CopySkinX(source,input,skin);
        var converted=RefitCore.MatchSkinToBody(input,vanilla,targets,uv,false,log);
        CopySkinX(fitted,converted,skin);
        return converted;
    }

    private static void CopySkinX(byte[] from,byte[] to,int[] meshes)
    {
        var a=SecondSkinWriter.Parse(from);var b=SecondSkinWriter.Parse(to);
        foreach(int m in meshes)
        {
            var src=a.Decls[m].First(f=>f.Usage==0);var dst=b.Decls[m].First(f=>f.Usage==0);
            int ha=a.MeshStart+36*m,hb=b.MeshStart+36*m,count=BitConverter.ToUInt16(from,ha);
            if(count!=BitConverter.ToUInt16(to,hb) || src.Type!=dst.Type)
                throw new InvalidDataException("Retained skin vertex layout changed during UV conversion.");
            int size=src.Type switch { 2 or 3=>4,13 or 14=>2,_=>throw new InvalidDataException("Unsupported skin position format.") };
            int read=a.Vb+BitConverter.ToInt32(from,ha+20+4*src.Stream)+src.Offset;
            int write=b.Vb+BitConverter.ToInt32(to,hb+20+4*dst.Stream)+dst.Offset;
            for(int i=0;i<count;i++,read+=from[ha+32+src.Stream],write+=to[hb+32+dst.Stream])
                from.AsSpan(read,size).CopyTo(to.AsSpan(write,size));
        }
    }

    public static object? ModelAvailability(string path) => RacialModelBake.Switch(path, 201);

    public static Func<int,int,string?> ItemLookup(IDataManager data, IPluginLog log) => ItemNames.Lookup(data, log);

    // Save once per option. A later equip must never replace a model modified in Studio.
    public static bool SaveEditable(string folder, string output, string source, string slot, string body,
        string option, string shape, Dictionary<string,string> targets, string model)
    {
        var record = BodyRetargetWriter.ReadRecord(folder);
        var existing = record?.Options.FirstOrDefault(e => e.Group == "Fit" && e.Name == option && e.GamePath == output);
        if (existing != null && File.Exists(Path.Combine(folder,existing.File))) return false;
        object? eqdp = output == source ? null : ModelAvailability(output);
        var result = BodyRetargetWriter.Save(folder, "Fit", output, body, "Vanilla body",
            [new(option, File.ReadAllBytes(model), shape)], manipulations: eqdp == null ? null : [eqdp],
            auto: true, fromGame: true, provenance: new("::vanilla", "c0201e0000" + slot + ".mdl", targets));
        if (!result.Ok) throw new IOException(result.Message);
        return true;
    }

    internal static void ApplyLegShapes(byte[] body, string[] options)
    {
        bool hip = options.Contains("Hip Dips"), soft = options.Contains("Soft Butt");
        if (!hip && !soft) return;
        var src = SecondSkinWriter.Parse(body);
        var names = new List<string>();
        if (hip) names.Add("shpx_yab_hip");
        if (soft) names.AddRange(["shpx_yam_softbutt", "shpx_yam_c_softwaist", "shpx_yab_c_softwaist"]);
        if (hip && soft) names.Add("shpx_yab_c_hipsoft");
        foreach (var name in names)
            if (src.Shapes.TryGetValue(name, out var entries))
                foreach (var entry in entries)
                    foreach (var (at, replacement) in entry.Values)
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(checked(src.Ib + 2 * ((int)entry.MeshIndexOffset + at))), replacement);
    }
}
