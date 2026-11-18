using Artisan.GameInterop;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using System.Linq;
using System.Numerics;

namespace Artisan.UI
{
    internal static class RaphaelCacheUI
    {
        private static string _search = string.Empty;
        private static bool _oldVersion = false;
        // 閉包放在這裡：寫在迴圈裡的話，捕捉迴圈變數 m 讓每一列都配置一份，即使沒有點。
        private static bool HasEditorWindow(int id)
            => P.ws.Windows.Any(x => x.WindowName.Contains(id.ToString()));

        internal static void Draw()
        {
            ImGui.TextWrapped("This tab will allow you to view macros in the Raphael integration cache.".Loc());
            ImGui.Separator();

            if (Svc.ClientState.IsLoggedIn && Crafting.CurState is not Crafting.State.IdleNormal and not Crafting.State.IdleBetween)
            {
                ImGui.Text(SharedText.CraftingInProgressSettingsLocked.Loc());
                return;
            }
            ImGui.Spacing();

            if (ImGui.RadioButton("Old Cache (not in use)".Loc(), _oldVersion))
                _oldVersion = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("New Cache".Loc(), !_oldVersion))
                _oldVersion = false;

            ImGui.InputText("Search".Loc(), ref _search, 300);

            if (!_oldVersion)
            {

                if (ImGui.BeginChild("##selector", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 32f.Scale()), true))
                {
                    ImGuiEx.TextUnderlined("Level/Progress/Quality/Durability-Craftsmanship/Control/CP-Type/Initial Quality".Loc());
                    // 與走訪 .Keys 再查表同順序、同內容（寫入都在框架執行緒，畫的時候不會變），只是不必每幀複製一份鍵陣列。
                    foreach (var entry in P.Config.RaphaelSolverCacheV3)
                    {
                        var m = entry.Value;
                        if (!m.Name.Contains(_search, System.StringComparison.CurrentCultureIgnoreCase)) continue;
                        var selected = ImGui.Selectable($"{m.Name}###{m.ID}");

                        if (selected && !HasEditorWindow(m.ID))
                        {
                            new MacroEditor(m, true);
                        }
                    }

                }
                ImGui.EndChild();

            }
            else
            {

                if (ImGui.BeginChild("##selector", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 32f.Scale()), true))
                {
                    ImGuiEx.TextUnderlined("Level/Progress/Quality/Durability-Craftsmanship/Control/CP-Type".Loc());
                    foreach (var entry in P.Config.RaphaelSolverCacheV2)
                    {
                        var m = entry.Value;
                        if (!m.Name.Contains(_search, System.StringComparison.CurrentCultureIgnoreCase)) continue;
                        var selected = ImGui.Selectable($"{m.Name}###{m.ID}");

                        if (selected && !HasEditorWindow(m.ID))
                        {
                            new MacroEditor(m, true);
                        }
                    }

                }
                ImGui.EndChild();

            }

            if (ImGui.Button("Clear This Raphael Cache (Hold Ctrl)".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y)) && ImGui.GetIO().KeyCtrl)
            {
                if (_oldVersion)
                    P.Config.RaphaelSolverCacheV2.Clear();
                else
                    P.Config.RaphaelSolverCacheV3.Clear();
                P.Config.Save();
            }
        }
    }
}
