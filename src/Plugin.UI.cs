using System.Numerics;
using AetherglassUI;
using Dalamud.Bindings.ImGui;

namespace Aetherform;

public sealed partial class Plugin
{
    private readonly WindowShell shell = new();
    private bool visible;
    private string presetName = "";

    private void Draw()
    {
        if (!visible) return;
        ImGui.SetNextWindowSize(new Vector2(640, 700), ImGuiCond.FirstUseEver);
        WindowShell.PushHostStyle();
        try
        {
            bool open = ImGui.Begin("Aetherform###AetherformMain", ref visible, WindowShell.HostFlags);
            try
            {
                if (open) shell.Draw("Aetherform", "", [
                    new Destination("body", "Body installer", "", DrawBody) { DrawIcon = DrawBodyIcon },
                    new Destination("physics", "Physics preset builder", "", DrawPhysics) { DrawIcon = DrawPhysicsIcon }
                ], DrawHeaderMark);
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report(ex); }
        finally { WindowShell.PopHostStyle(); }
    }

    private void DrawHeaderMark(Vector2 position, float size)
    {
        var texture = textures.GetFromManifestResource(typeof(Plugin).Assembly, "Aetherform.mark.png").GetWrapOrDefault();
        if (texture != null)
            ImGui.GetWindowDrawList().AddImage(texture.Handle, position, position + new Vector2(size),
                new Vector2(.10f), new Vector2(.90f));
    }

    private static void DrawBodyIcon(Vector2 position, float size, uint color)
    {
        var draw = ImGui.GetWindowDrawList();
        Vector2 Point(float x, float y) => position + size * new Vector2(x, y);
        draw.AddCircleFilled(Point(.5f, .24f), size * .2f, color);
        draw.AddRectFilled(Point(.08f, .5f), Point(.92f, .93f), color, size * .18f);
    }

    private static void DrawPhysicsIcon(Vector2 position, float size, uint color)
    {
        var draw = ImGui.GetWindowDrawList();
        float[] stops = [.35f, .72f, .45f];
        for (int row = 0; row < stops.Length; row++)
        {
            var start = position + size * new Vector2(.05f, .17f + row * .33f);
            draw.AddLine(start, start + new Vector2(.90f * size, 0), color, size * .09f);
            draw.AddCircleFilled(start + new Vector2(stops[row] * size, 0), size * .12f, color);
        }
    }

    private bool Collection()
    {
        if (bank == null)
        {
            ImGui.TextWrapped("Copy your Aetherform.pmp and physics.zip into this folder to use the installer and physics builder.");
            ImGui.TextWrapped(payloadDirectory);
            if (Controls.ActionButton("Copy folder path")) ImGui.SetClipboardText(payloadDirectory);
            if (status.Length > 0) ImGui.TextWrapped(status);
            return false;
        }
        if (collections.Count == 0)
        {
            ImGui.TextWrapped("Load Penumbra to select a collection.");
            return false;
        }
        Controls.Row("target", "Penumbra collection", "", () =>
        {
            if (!ImGui.BeginCombo("##collection", collections.GetValueOrDefault(selected) ?? "Choose collection")) return;
            try
            {
                foreach (var (id, name) in collections.OrderBy(p => p.Value))
                    if (id != Guid.Empty && ImGui.Selectable(name + "###" + id, selected == id)) Select(id);
            }
            finally { ImGui.EndCombo(); }
        });
        return selected != Guid.Empty && collections.ContainsKey(selected);
    }

    private void DrawBody()
    {
        if (!Collection()) return;
        Controls.Row("refit", "Refit vanilla gear on equip", "Uses the chest and legs selected under Smallclothes in this collection.", () =>
        {
            bool enabled = !profiles.RefitDisabled.Contains(selected);
            if (Controls.Switch("enabled", enabled) == enabled) return;
            if (enabled) profiles.RefitDisabled.Add(selected);
            else profiles.RefitDisabled.Remove(selected);
            Save();
            nextPoll = 0;
        });
        int priority=profiles.RefitPriority;
        if(ImGui.InputInt("Generated refit priority",ref priority))
        {
            profiles.RefitPriority=priority;
            Save();
            nextPoll=0;
        }
        if (fits.Status.Length > 0) ImGui.TextWrapped(fits.Status);
        ImGui.TextWrapped("Install the body in Penumbra and enable it in the selected collection. Choose your body shape, hands, feet and hair in the mod’s Penumbra settings.");
        ImGui.BeginDisabled(installing != null);
        try
        {
            if (Controls.ActionButton(body == null ? "Install body" : "Enable installed body")) Install();
        }
        finally { ImGui.EndDisabled(); }
        if (status.Length > 0) ImGui.TextWrapped(status);
    }

    private void DrawPhysics()
    {
        if (!Collection() || bank == null) return;
        if (!bank.HasMotion)
            ImGui.TextWrapped($"For more motion choices, place tiramisu2110.zip in {payloadDirectory} and reload Aetherform.");
        for (int i = 0; i < bank.Index.Areas.Length; i++)
        {
            var area = bank.Index.Areas[i];
            if (i == 0 && bank.HasMotion)
            {
                var style = bank.ChestStyles.Single(s => s.Indices.Contains(draft[0]));
                Controls.Row(area.Name, area.Name, "", () =>
                {
                    var name = Controls.Choice("##style", style.Name, bank.ChestStyles.Select(s => s.Name).ToArray());
                    if (name == style.Name) return;
                    style = bank.ChestStyles.Single(s => s.Name == name);
                    int standard = Array.IndexOf(style.Variations, "Standard");
                    draft[0] = style.Indices[Math.Max(0, standard)];
                });
                if (style.Indices.Length > 1)
                    Controls.Row("chestVariation", "Chest variation", "", () =>
                    {
                        int j = Array.IndexOf(style.Indices, draft[0]);
                        var value = Controls.Choice("##variation", style.Variations[j], style.Variations);
                        draft[0] = style.Indices[Array.IndexOf(style.Variations, value)];
                    });
                continue;
            }
            Controls.Row(area.Name, area.Name, "", () =>
            {
                var value = Controls.Choice("##choice", area.Values[draft[i]], area.Values);
                draft[i] = Array.IndexOf(area.Values, value);
            });
        }
        if (Controls.ActionButton("Apply to collection")) Apply();
        ImGui.SameLine();
        if (Controls.ActionButton("Load body defaults")) draft = bank.Defaults;
        if (status.Length > 0) ImGui.TextWrapped(status);
        Controls.Row("name", "Preset name", "", () => Controls.Text(ref presetName, 64));
        if (Controls.ActionButton("Save named preset") && !string.IsNullOrWhiteSpace(presetName))
        {
            profiles.Named[presetName.Trim()] = [..draft];
            Save();
            status = "Saved " + presetName.Trim() + ".";
        }
        if (profiles.Named.Count > 0)
            Controls.Row("saved", "Load a saved preset", "", () =>
            {
                if (!ImGui.BeginCombo("##saved", "Choose preset")) return;
                try
                {
                    foreach (var (name, picks) in profiles.Named.OrderBy(p => p.Key))
                        if (bank.Valid(picks) && ImGui.Selectable(name))
                        {
                            draft = [..picks];
                            presetName = name;
                        }
                }
                finally { ImGui.EndCombo(); }
            });
    }
}
