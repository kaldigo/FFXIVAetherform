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
                    new Destination("body", "Body installer", "B", DrawBody),
                    new Destination("physics", "Physics preset builder", "P", DrawPhysics)
                ], (p, _) => ImGui.GetWindowDrawList().AddText(p, 0xffced462, "AF"));
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report(ex); }
        finally { WindowShell.PopHostStyle(); }
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
        for (int i = 0; i < bank.Index.Areas.Length; i++)
        {
            var area = bank.Index.Areas[i];
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
