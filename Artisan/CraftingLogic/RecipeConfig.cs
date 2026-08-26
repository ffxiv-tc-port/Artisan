using Artisan.Autocraft;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Artisan.CraftingLogic;

public class RecipeConfig
{
    public const uint Default = 0;
    public const uint Disabled = 1;

    // 臨時覆寫:只活在記憶體裡,不進設定檔(NonSerialized 擋欄位序列化、JsonIgnore 擋
    // Newtonsoft)。由 Artisan.SetTemporary* 這組 IPC 設定,Artisan.ClearTemporary*
    // 或 Artisan 卸載時清掉。
    // 🔴 未設定時一律沿用既有欄位 —— 既有使用者的行為與設定檔內容都不變。
    [NonSerialized, JsonIgnore]
    public string TempSolverType = "";
    [NonSerialized, JsonIgnore]
    public int TempSolverFlavour = -1;
    [NonSerialized, JsonIgnore]
    public uint? TempRequiredFood;
    [NonSerialized, JsonIgnore]
    public bool TempRequiredFoodHQ;
    [NonSerialized, JsonIgnore]
    public uint? TempRequiredPotion;
    [NonSerialized, JsonIgnore]
    public bool TempRequiredPotionHQ;

    public string CurrentSolverType => TempSolverType.Length > 0 ? TempSolverType : SolverType;
    public int CurrentSolverFlavour => TempSolverFlavour >= 0 ? TempSolverFlavour : SolverFlavour;
    public uint CurrentRequiredFood => TempRequiredFood ?? requiredFood;
    public bool CurrentRequiredFoodHQ => TempRequiredFood.HasValue ? TempRequiredFoodHQ : requiredFoodHQ;
    public uint CurrentRequiredPotion => TempRequiredPotion ?? requiredPotion;
    public bool CurrentRequiredPotionHQ => TempRequiredPotion.HasValue ? TempRequiredPotionHQ : requiredPotionHQ;

    public void ClearTemporaryOverrides()
    {
        TempSolverType = "";
        TempSolverFlavour = -1;
        TempRequiredFood = null;
        TempRequiredFoodHQ = false;
        TempRequiredPotion = null;
        TempRequiredPotionHQ = false;
    }


    public string SolverType = ""; // TODO: ideally it should be a Type?, but that causes problems for serialization
    public int SolverFlavour;
    public uint requiredFood = Default;
    public uint requiredPotion = Default;
    public uint requiredManual = Default;
    public uint requiredSquadronManual = Default;
    public bool requiredFoodHQ = true;
    public bool requiredPotionHQ = true;


    public bool FoodEnabled => RequiredFood != Disabled;
    public bool PotionEnabled => RequiredPotion != Disabled;
    public bool ManualEnabled => RequiredManual != Disabled;
    public bool SquadronManualEnabled => RequiredSquadronManual != Disabled;


    public uint RequiredFood => CurrentRequiredFood == Default ? P.Config.DefaultConsumables.requiredFood : CurrentRequiredFood;
    public uint RequiredPotion => CurrentRequiredPotion == Default ? P.Config.DefaultConsumables.requiredPotion : CurrentRequiredPotion;
    public uint RequiredManual => requiredManual == Default ? P.Config.DefaultConsumables.requiredManual : requiredManual;
    public uint RequiredSquadronManual => requiredSquadronManual == Default ? P.Config.DefaultConsumables.requiredSquadronManual : requiredSquadronManual;
    public bool RequiredFoodHQ => CurrentRequiredFood == Default ? P.Config.DefaultConsumables.requiredFoodHQ : CurrentRequiredFoodHQ;
    public bool RequiredPotionHQ => CurrentRequiredPotion == Default ? P.Config.DefaultConsumables.requiredPotionHQ : CurrentRequiredPotionHQ;


    public string FoodName => requiredFood == Default ? "?? (Default)".Loc(P.Config.DefaultConsumables.FoodName) : RequiredFood == Disabled ? "Disabled".Loc() :$"{(RequiredFoodHQ ? " " : "")}{ConsumableChecker.Food.FirstOrDefault(x => x.Id == RequiredFood).Name}";
    public string PotionName => requiredPotion == Default ? "?? (Default)".Loc(P.Config.DefaultConsumables.PotionName) : RequiredPotion == Disabled ? "Disabled".Loc() :$"{(RequiredPotionHQ ? " " : "")}{ConsumableChecker.Pots.FirstOrDefault(x => x.Id == RequiredPotion).Name}";
    public string ManualName => requiredManual == Default ? "?? (Default)".Loc(P.Config.DefaultConsumables.ManualName) : RequiredManual == Disabled ? "Disabled".Loc() :$"{ConsumableChecker.Manuals.FirstOrDefault(x => x.Id == RequiredManual).Name}";
    public string SquadronManualName => requiredSquadronManual == Default ? "?? (Default)".Loc(P.Config.DefaultConsumables.SquadronManualName) : RequiredSquadronManual == Disabled ? "Disabled".Loc() :$"{ConsumableChecker.SquadronManuals.FirstOrDefault(x => x.Id == RequiredSquadronManual).Name}";



    public bool Draw(uint recipeId)
    {
        var recipe = LuminaSheets.RecipeSheet[recipeId];
        ImGuiEx.LineCentered($"###RecipeName{recipeId}", () => { ImGuiEx.TextUnderlined($"{recipe.ItemResult.Value.Name.ToDalamudString().ToString()}"); });
        var config = this;
        var stats = CharacterStats.GetBaseStatsForClassHeuristic((Job)((uint)Job.CRP + recipe.CraftType.RowId));
        stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ), new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
        var craft = Crafting.BuildCraftStateForRecipe(stats, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
        craft.InitialQuality = Simulator.GetStartingQuality(recipe, false, craft.StatLevel);
        bool changed = false;
        changed |= DrawFood();
        changed |= DrawPotion();
        changed |= DrawManual();
        changed |= DrawSquadronManual();
        changed |= DrawSolver(craft);
        DrawSimulator(craft);
        return changed;
    }

    public bool DrawFood(bool hasButton = false)
    {
        bool changed = false;
        ImGuiEx.TextV("Food Usage:".Loc());
        ImGui.SameLine(130f.Scale());
        if (hasButton) ImGuiEx.SetNextItemFullWidth(-120);
        if (ImGui.BeginCombo("##foodBuff", FoodName))
        {
            if (this != P.Config.DefaultConsumables)
            {
                if (ImGui.Selectable("Default (??)".Loc(P.Config.DefaultConsumables.FoodName)))
                {
                    requiredFood = Default;
                    requiredFoodHQ = false;
                    changed = true;
                }
            }
            if (ImGui.Selectable("Disable".Loc()))
            {
                requiredFood = Disabled;
                requiredFoodHQ = false;
                changed = true;
            }
            foreach (var x in ConsumableChecker.GetFood(true))
            {
                if (ImGui.Selectable($"{x.Name}"))
                {
                    requiredFood = x.Id;
                    requiredFoodHQ = false;
                    changed = true;
                }
            }
            foreach (var x in ConsumableChecker.GetFood(true, true))
            {
                if (ImGui.Selectable($" {x.Name}"))
                {
                    requiredFood = x.Id;
                    requiredFoodHQ = true;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    public bool DrawPotion(bool hasButton = false)
    {
        bool changed = false;
        ImGuiEx.TextV("Medicine Usage:".Loc());
        ImGui.SameLine(130f.Scale());
        if (hasButton) ImGuiEx.SetNextItemFullWidth(-120);
        if (ImGui.BeginCombo("##potBuff", PotionName))
        {
            if (this != P.Config.DefaultConsumables)
            {
                if (ImGui.Selectable("Default (??)".Loc(P.Config.DefaultConsumables.PotionName)))
                {
                    requiredPotion = Default;
                    requiredPotionHQ = false;
                    changed = true;
                }
            }
            if (ImGui.Selectable("Disable".Loc()))
            {
                requiredPotion = Disabled;
                requiredPotionHQ = false;
                changed = true;
            }
            foreach (var x in ConsumableChecker.GetPots(true))
            {
                if (ImGui.Selectable($"{x.Name}"))
                {
                    requiredPotion = x.Id;
                    requiredPotionHQ = false;
                    changed = true;
                }
            }
            foreach (var x in ConsumableChecker.GetPots(true, true))
            {
                if (ImGui.Selectable($" {x.Name}"))
                {
                    requiredPotion = x.Id;
                    requiredPotionHQ = true;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    public bool DrawManual(bool hasButton = false)
    {
        bool changed = false;
        ImGuiEx.TextV("Manual Usage:".Loc());
        ImGui.SameLine(130f.Scale());
        if (hasButton) ImGuiEx.SetNextItemFullWidth(-120);
        if (ImGui.BeginCombo("##manualBuff", ManualName))
        {
            if (this != P.Config.DefaultConsumables)
            {
                if (ImGui.Selectable("Default (??)".Loc(P.Config.DefaultConsumables.ManualName)))
                {
                    requiredManual = Default;
                    changed = true;
                }
            }
            if (ImGui.Selectable("Disable".Loc()))
            {
                requiredManual = Disabled;
                changed = true;
            }
            foreach (var x in ConsumableChecker.GetManuals(true))
            {
                if (ImGui.Selectable($"{x.Name}"))
                {
                    requiredManual = x.Id;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }



    public bool DrawSquadronManual(bool hasButton = false)
    {
        bool changed = false;
        ImGuiEx.TextV("Squadron Manual:".Loc());
        ImGui.SameLine(130f.Scale());
        if (hasButton) ImGuiEx.SetNextItemFullWidth(-120);
        if (ImGui.BeginCombo("##squadronManualBuff", SquadronManualName))
        {
            if (this != P.Config.DefaultConsumables)
            {
                if (ImGui.Selectable("Default (??)".Loc(P.Config.DefaultConsumables.SquadronManualName)))
                {
                    requiredSquadronManual = Default;
                    changed = true;
                }
            }
            if (ImGui.Selectable("Disable".Loc()))
            {
                requiredSquadronManual = Disabled;
                changed = true;
            }
            foreach (var x in ConsumableChecker.GetSquadronManuals(true))
            {
                if (ImGui.Selectable($"{x.Name}"))
                {
                    requiredSquadronManual = x.Id;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    public bool DrawSolver(CraftState craft, bool hasButton = false, bool liveStats = true)
    {
        bool changed = false;
        ImGuiEx.TextV("Solver:".Loc());
        ImGui.SameLine(130f.Scale());
        if (hasButton) ImGuiEx.SetNextItemFullWidth(-120);
        var solver = CraftingProcessor.GetSolverForRecipe(this, craft);
        if (ImGui.BeginCombo("##solver", solver.Name))
        {
            foreach (var opt in CraftingProcessor.GetAvailableSolversForRecipe(craft, true))
            {
                if (opt == default) continue;
                if (opt.UnsupportedReason.Length > 0)
                {
                    ImGui.Text("?? is unsupported - ??".Loc(opt.Name, opt.UnsupportedReason));
                }
                else
                {
                    bool selected = opt.Def == solver.Def && opt.Flavour == solver.Flavour;
                    if (ImGui.Selectable(opt.Name, selected))
                    {
                        SolverType = opt.Def.GetType().FullName!;
                        SolverFlavour = opt.Flavour;
                        changed = true;
                    }
                }
            }

            ImGui.EndCombo();
        }

        changed |= RaphaelCache.DrawRaphaelDropdown(craft, liveStats);

        return changed;
    }

    public unsafe void DrawSimulator(CraftState craft)
    {
        if (!P.Config.HideRecipeWindowSimulator)
        {
            var recipe = craft.Recipe;
            var config = this;
            var solverHint = Simulator.SimulatorResult(recipe, config, craft, out var hintColor, out var solverTooltip);
            var solver = CraftingProcessor.GetSolverForRecipe(config, craft);

            // 🔑 「這個宇宙任務有奇蹟之材」以及「現在這個解算器到底會不會用它」必須在**列上**看得見。
            //    改動前只有標準解算器那一條分支會講一句話,配方一旦(自動)切到 Raphael 就完全沒有提示 ——
            //    使用者被靜默降級成「整場不用奇蹟之材」而無從得知。tooltip 藏的是「為什麼」,不是「有沒有問題」。
            if (craft.MissionHasMaterialMiracle)
            {
                if (!P.Config.UseMaterialMiracle)
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "This mission grants Material Miracle, but it will not be used.".Loc());
                    // 📌 這句原本無條件叫使用者「去把開關打開」;2026-08-07 因為標準解算器打開後
                    //    做出來率 100%→46.1% 而改成反向警告。**那個代價 2026-08-15 已經修掉**
                    //    (代打改成逐場過閘門,見 StandardSolver.ShouldDelegateDuringMiracle):
                    //    在標準解算器真正搆得到的那 88 個非專家宇宙配方上重新量測(19 個抽樣 × 每格 500 次),
                    //    做出來率 88.8%→94.5%、期望品質 81.4→91.8,且沒有任何一個配方比修改前差。
                    //    ⇒ 警告已經沒有事實基礎,改回單純的建議。
                    ImGuiEx.Tooltip("Turn on \"Use Material Miracle when available\" in the main settings to let solvers use it.".Loc());
                }
                else if (!Solvers.MaterialMiracleSolver.SolverUsesMaterialMiracle(solver))
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "This mission grants Material Miracle, but ?? will not use it.".Loc(solver.Name));
                    ImGuiEx.Tooltip("Only the standard, expert and Raphael solvers know about Material Miracle. Pick one of those for this recipe to make use of it.".Loc());
                }
                else
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudWhite, "?? will use Material Miracle on this mission.".Loc(solver.Name));
                    ImGuiEx.Tooltip("Material Miracle costs no step, no CP and no durability, and does not tick any buff down. While it is up every condition is a beneficial one.".Loc());
                }
            }

            var showedDistribution = false;
            if (solver.Name != "Expert Recipe Solver".Loc())
            {
                if (craft.MissionHasMaterialMiracle && solver.Name == "Standard Recipe Solver".Loc() && P.Config.UseMaterialMiracle)
                    ImGuiEx.TextWrapped("This would use Material Miracle, which is not compatible with the simulator.".Loc());
                else
                {
                    ImGuiEx.TextWrapped(hintColor, solverHint);
                    showedDistribution = true;
                }
            }
            else
                ImGuiEx.TextWrapped(SharedText.RunInSimulatorForResults.Loc());

            // ⚠️ 先把點擊狀態存下來再畫 tooltip —— ImGuiEx.Tooltip 會開一個新視窗,
            //    之後的 IsItemClicked 就不再指向上面那段文字了。
            var hintClicked = ImGui.IsItemClicked();
            if (showedDistribution && solverTooltip.Length > 0)
                ImGuiEx.Tooltip(solverTooltip);

            if (hintClicked)
            {
                P.PluginUi.OpenWindow = UI.OpenWindow.Simulator;
                P.PluginUi.IsOpen = true;
                SimulatorUI.SelectedRecipe = recipe;
                SimulatorUI.ResetSim();
                if (config.PotionEnabled)
                {
                    SimulatorUI.SimMedicine ??= new();
                    SimulatorUI.SimMedicine.Id = config.RequiredPotion;
                    SimulatorUI.SimMedicine.ConsumableHQ = config.RequiredPotionHQ;
                    SimulatorUI.SimMedicine.Stats = new ConsumableStats(config.RequiredPotion, config.RequiredPotionHQ);
                }
                if (config.FoodEnabled)
                {
                    SimulatorUI.SimFood ??= new();
                    SimulatorUI.SimFood.Id = config.RequiredFood;
                    SimulatorUI.SimFood.ConsumableHQ = config.RequiredFoodHQ;
                    SimulatorUI.SimFood.Stats = new ConsumableStats(config.RequiredFood, config.RequiredFoodHQ);
                }

                // Instance() 沒登入/切場景時合法回 null,直接 ->Entries 就是解參考 null
                // (AccessViolationException,try/catch 攔不到)。取不到就不挑裝備組,
                // SimGS 維持原值 —— 下面的使用點本來就處理 SimGS 為 null 的情況。
                var gearsetModule = RaptureGearsetModule.Instance();
                if (gearsetModule != null)
                {
                    foreach (ref var gs in gearsetModule->Entries)
                    {
                        if ((Job)gs.ClassJob == (Job)((uint)Job.CRP + recipe.CraftType.RowId))
                        {
                            if (SimulatorUI.SimGS is null || (Job)SimulatorUI.SimGS.Value.ClassJob != (Job)((uint)Job.CRP + recipe.CraftType.RowId))
                            {
                                SimulatorUI.SimGS = gs;
                            }

                            if (SimulatorUI.SimGS.Value.ItemLevel < gs.ItemLevel)
                                SimulatorUI.SimGS = gs;
                        }
                    }
                }

                var rawSolver = CraftingProcessor.GetSolverForRecipe(config, craft);
                SimulatorUI._selectedSolver = new(rawSolver.Name, rawSolver.Def.Create(craft, rawSolver.Flavour));
            }

            if (ImGui.IsItemHovered())
            {
                ImGuiEx.Tooltip("Click to open in simulator".Loc());
            }


        }
    }
}
