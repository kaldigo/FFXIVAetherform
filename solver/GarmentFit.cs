using System.Buffers.Binary;
using Dalamud.Plugin.Services;
using Proteus.Services;
using XivLiveMesh;

namespace Aetherform.Geometry;

public static class GarmentFit
{
    public static byte[] Generate(byte[] garment, string slot, ushort modelRace,
        IReadOnlyDictionary<string,string> originals, IReadOnlyDictionary<string,string> targets,
        string[] legOptions, byte[]? deformer, byte[]? skeleton, string bindingDirectory, IPluginLog log)
    {
        var uv = new UVRemapService(log, AppContext.BaseDirectory);
        var pairs = new Dictionary<string,BodyRetarget.SlotPair>();
        var bodies = new Dictionary<string,byte[]>();
        var stockBody = new List<BodySurface>();
        foreach (var (part, path) in targets)
        {
            var target = File.ReadAllBytes(path);
            if (part == "_dwn") ApplyLegShapes(target, legOptions);
            var vanilla = File.ReadAllBytes(originals[part]);
            bodies[part] = target;
            if (slot is ("_top" or "_dwn") && ModelPartReader.Read(vanilla) is {} sourceBody)
                stockBody.Add(BodySurface.OfBodyWorn(sourceBody,.025f));
            var refusal = BodyRetarget.BuildPair(part, vanilla, target,
                part, false, null, null, uv, out var pair);
            if (refusal != null) throw new InvalidDataException(refusal);
            pairs[part] = pair;
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
        BodyRetarget.Planned plan;
        if (slot is "_glv" or "_sho")
        {
            var ends = KeepEnds.Read(slot, model, garment) ?? throw new InvalidDataException("Unreadable limb weights.");
            plan = KeepEnds.Plan(model, garment, ends, pairs[ends.Limb], pairs[slot], true);
        }
        else
        {
            var other = slot == "_top" ? "_dwn" : "_top";
            var shape = pairs[other] with { ShapeOnly = true };
            plan = RefitCore.PlanRefit(model, garment, [pairs[slot], shape], slot, true, true, out _,
                transfer: slot == "_top" ? YabTransfer.LongCoatEnvelope : YabTransfer.Envelope,
                strength: slot == "_top" ? .75f : 1f);
        }
        var result = RefitCore.MatchSkinToBody(plan.Model, originals[slot],
            targets.Values.ToArray(), uv, false, log);
        if (slot is "_top" or "_dwn")
        {
            var bindings = new GarmentBindings(bindingDirectory,slot,garment);
            var cleared = (byte[])result.Clone();
            var map = VanillaAfWrap.Get(bodies);
            WrapGarment.Apply(garment,result,map,bindings,slot);
            result = ClothSubdivision.Apply(garment,cleared,result,map,bindings,slot,stockBody);
            bindings.Save();
        }
        if (wearer != null && !sharedMale)
            result = route.Back(result, wearer, out var refusal) ?? throw new InvalidDataException(refusal);
        if (ModelPartReader.Read(result) == null) throw new InvalidDataException("Generated model failed read-back.");
        return result;
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

    private static void ApplyLegShapes(byte[] body, string[] options)
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
