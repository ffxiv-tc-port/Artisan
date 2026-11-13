using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
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
                // 擲例外會讓 Dalamud 把整個視窗換成錯誤面板，下面的暫停／取消鈕跟著消失 ⇒ 在這裡接住並暫停清單。
                // ProcessList 不推彈任何 ImGui 樣式，try 裡沒有要對稱收尾的堆疊。
                try
                {
                    CraftingListFunctions.ProcessList(CraftingListUI.selectedList);
                }
                catch (Exception ex)
                {
                    OnProcessListFault(ex);
                }

                try
                {
                    DrawProgress();
                }
                catch (Exception ex)
                {
                    FaultOnce.Report("ProcessingWindow 進度文字", ex);
                }

                if (!CraftingListFunctions.Paused)
                {
                    if (ImGui.Button("Pause".Loc()))
                        PauseList();
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
                    CancelList();
            }
        }

        /// <summary>「暫停」鈕：暫停清單並清掉已排的任務（進行中那一件照常做完）。只在框架執行緒呼叫。</summary>
        internal static void PauseList()
        {
            CraftingListFunctions.Paused = true;
            P.TM.Abort();
            CraftingListFunctions.CLTM.Abort();
            PreCrafting.Tasks.Clear();
        }

        /// <summary>「取消」鈕：結束清單並清掉已排的任務（進行中那一件照常做完）。只在框架執行緒呼叫。</summary>
        internal static void CancelList()
        {
            CraftingListUI.Processing = false;
            CraftingListFunctions.Paused = false;
            P.TM.Abort();
            CraftingListFunctions.CLTM.Abort();
            PreCrafting.Tasks.Clear();
            Crafting.CraftFinished -= CraftingListUI.UpdateListTimer;
        }

        // 與「暫停」鈕做同樣的事；只在清單跑著且沒暫停時才停並說明，暫停中重擲（例如素材表建不起來）只進 log。
        private static void OnProcessListFault(Exception ex)
        {
            FaultOnce.Report("製作清單處理", ex);
            if (!CraftingListUI.Processing || CraftingListFunctions.Paused)
                return;

            Endurance.NotifyForcedStop("製作清單處理時發生錯誤");
            CraftingListFunctions.Paused = true;
            P.TM.Abort();
            CraftingListFunctions.CLTM.Abort();
            PreCrafting.Tasks.Clear();
            Svc.Chat.PrintError("[Artisan] " + "The crafting list hit an error and has been paused. Press Resume to try again, or Cancel to stop. Details are in the log.".Loc());
        }

        private static void DrawProgress()
        {
            //if (ImGuiEx.AddHeaderIcon("OpenConfig", FontAwesomeIcon.Cog, new ImGuiEx.HeaderIconOptions() { Tooltip = "Open Config" }))
            //{
            //    P.PluginUi.IsOpen = true;
            //}

            ImGui.Text("Now Processing: ??".Loc(CraftingListUI.selectedList.Name));
            ImGui.Separator();
            ImGui.Spacing();
            if (CraftingListUI.CurrentProcessedItem != 0)
            {
                ImGuiEx.TextV("Crafting: ??".Loc(CraftingListUI.CurrentProcessedItem.NameOfRecipeOrThrow()));
                if (ListPrecraft.ParentOf(CraftingListFunctions.CurrentIndex) is var precraftFor and not 0)
                    ImGuiEx.TextV("Making missing materials for: ??".Loc(precraftFor.NameOfRecipe()));
                ImGuiEx.TextV("Current Item Progress: ?? / ??".Loc(CraftingListUI.CurrentProcessedItemCount, CraftingListUI.CurrentProcessedItemListCount));
                ImGuiEx.TextV("Overall List Progress: ?? / ??".Loc(CraftingListFunctions.CurrentIndex + 1, CraftingListUI.selectedList.ExpandedList.Count));

                string duration = CraftingListFunctions.ListEndTime == TimeSpan.Zero ? "Unknown".Loc() : string.Format("{0:D2}d {1:D2}h {2:D2}m {3:D2}s", CraftingListFunctions.ListEndTime.Days, CraftingListFunctions.ListEndTime.Hours, CraftingListFunctions.ListEndTime.Minutes, CraftingListFunctions.ListEndTime.Seconds);
                ImGuiEx.TextV("Approximate Remaining Duration: ??".Loc(duration));

            }
        }
    }
}
