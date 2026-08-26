using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Dalamud.Interface.Windowing;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using System;

namespace Artisan.UI
{
    internal class ProcessingWindow : Window
    {
        public ProcessingWindow() : base("Processing List".Loc() + "###ProcessingList", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
        {
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            SizeCondition = ImGuiCond.Appearing;
        }

        public override bool DrawConditions()
        {
            if (CraftingListUI.Processing)
                return true;

            return false;
        }

        public override void PreDraw()
        {
            if (!P.Config.DisableTheme)
            {
                P.Style.Push();
                P.StylePushed = true;
            }

            // base.PreDraw() 推入 Dalamud 的每視窗不透明度(ImGuiStyleVar.Alpha)。
            // 必須推在主題「之後」:StyleModelV1.Push() 自己也推了 Alpha
            // (本外掛主題 Broken Mountain 的 Alpha = 1.0),先 base 再主題會被主題蓋掉,
            // 不透明度就靜默失效。
            base.PreDraw();
        }

        public override void PostDraw()
        {
            // 與 PreDraw 相反順序彈出:ImGui 樣式堆疊是 LIFO,
            // 且 StyleModel.Pop() 依「計數」彈出而非依名稱,順序錯會還原到錯的值。
            base.PostDraw();

            if (P.StylePushed)
            {
                P.Style.Pop();
                P.StylePushed = false;
            }
        }

        public unsafe override void Draw()
        {
            if (CraftingListUI.Processing)
            {
                CraftingListFunctions.ProcessList(CraftingListUI.selectedList);

                //if (ImGuiEx.AddHeaderIcon("OpenConfig", FontAwesomeIcon.Cog, new ImGuiEx.HeaderIconOptions() { Tooltip = "Open Config" }))
                //{
                //    P.PluginUi.IsOpen = true;
                //}

                ImGui.Text("Now Processing: ??".Loc(CraftingListUI.selectedList.Name));
                ImGui.Separator();
                ImGui.Spacing();
                if (CraftingListUI.CurrentProcessedItem != 0)
                {
                    ImGuiEx.TextV("Crafting: ??".Loc(LuminaSheets.RecipeSheet[CraftingListUI.CurrentProcessedItem].ItemResult.Value.Name.ToDalamudString().ToString()));
                    ImGuiEx.TextV("Current Item Progress: ?? / ??".Loc(CraftingListUI.CurrentProcessedItemCount, CraftingListUI.CurrentProcessedItemListCount));
                    ImGuiEx.TextV("Overall List Progress: ?? / ??".Loc(CraftingListFunctions.CurrentIndex + 1, CraftingListUI.selectedList.ExpandedList.Count));

                    string duration = CraftingListFunctions.ListEndTime == TimeSpan.Zero ? "Unknown".Loc() : string.Format("{0:D2}d {1:D2}h {2:D2}m {3:D2}s", CraftingListFunctions.ListEndTime.Days, CraftingListFunctions.ListEndTime.Hours, CraftingListFunctions.ListEndTime.Minutes, CraftingListFunctions.ListEndTime.Seconds);
                    ImGuiEx.TextV("Approximate Remaining Duration: ??".Loc(duration));

                }

                if (!CraftingListFunctions.Paused)
                {
                    if (ImGui.Button("Pause".Loc()))
                    {
                        CraftingListFunctions.Paused = true;
                        P.TM.Abort();
                        CraftingListFunctions.CLTM.Abort();
                        PreCrafting.Tasks.Clear();
                    }
                }
                else
                {
                    if (ImGui.Button("Resume".Loc()))
                    {
                        if (Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
                        {
                            var recipe = LuminaSheets.RecipeSheet[CraftingListUI.CurrentProcessedItem];
                            PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), default));
                        }

                        CraftingListFunctions.Paused = false;
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel".Loc()))
                {
                    CraftingListUI.Processing = false;
                    CraftingListFunctions.Paused = false;
                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                    PreCrafting.Tasks.Clear();
                    Crafting.CraftFinished -= CraftingListUI.UpdateListTimer;
                }
            }
        }
    }
}
