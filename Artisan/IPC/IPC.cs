using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.Logging;
using OtterGui;
using OtterGui.Extensions;
using System;

namespace Artisan.IPC
{
    internal static class IPC
    {
        /// <summary>
        /// IPC 觸發的製作,等待「配方確實被選中」的上限。超過就放棄整個請求,
        /// 不要退回「照樣啟動製作」——那會做成當下選著的配方。
        /// </summary>
        private const int RecipeSelectionTimeoutMs = 15_000;

        private static bool stopCraftingRequest;

        public static bool StopCraftingRequest
        {
            get => stopCraftingRequest;
            set
            {
                if (value)
                {
                    StopCrafting();
                }
                else
                {
                    if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WaitingForDutyFinder] && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty])
                        ResumeCrafting();
                }
                stopCraftingRequest = value;
            }
        }

        public static ArtisanMode CurrentMode;
        internal static void Init()
        {
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").RegisterFunc(GetEnduranceStatus);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").RegisterAction(SetEnduranceStatus);

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").RegisterFunc(IsListRunning);
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").RegisterFunc(IsListPaused);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").RegisterAction(SetListPause);

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").RegisterFunc(GetStopRequest);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").RegisterAction(SetStopRequest);

            Svc.PluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").RegisterAction(CraftX);
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").RegisterFunc(IsBusy);
        }

        internal static void Dispose()
        {
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<ushort, int, object>("Artisan.IsBusy").UnregisterFunc();
        }

        static bool GetEnduranceStatus()
        {
            return Endurance.Enable;
        }

        static void SetEnduranceStatus(bool s)
        {
            Endurance.ToggleEndurance(s);
        }

        static bool IsListRunning()
        {
            return CraftingListUI.Processing;
        }

        static bool IsListPaused()
        {
            return CraftingListUI.Processing && CraftingListFunctions.Paused;
        }

        static void SetListPause(bool s)
        {
            if (IsListPaused())
                CraftingListFunctions.Paused = s;
        }

        static bool GetStopRequest()
        {
            return StopCraftingRequest;
        }

        static void SetStopRequest(bool s)
        {
            if (s)
                DuoLog.Information("Artisan has been requested to stop by an external plugin.");
            else
                DuoLog.Information("Artisan has been requested to restart by an external plugin.");

            StopCraftingRequest = s;
        }

        public unsafe static void CraftX(ushort recipeId, int amount)
        {
            if (LuminaSheets.RecipeSheet!.FindFirst(x => x.Value.RowId == recipeId, out var recipe))
            {
                // 🔴 選取階段失敗時「照樣啟動製作」是這條路徑原本的行為,而且完全無聲。
                //
                // PreCrafting.Update() 對 TaskResult.Abort 的處理是 Tasks.Clear()(見
                // PreCrafting.cs 的 switch),所以「宇宙筆記裡找不到這個配方、等到逾時中止」
                // 與「選取成功」在下面這個 Tasks.Count == 0 的判斷裡**完全分不出來**。
                // 兩者都讓下一段的 ToggleEndurance(true) 跑起來,於是外掛就去做**宇宙筆記
                // 當下剛好選著的那個配方**——正是 TaskSelectRecipe 開頭那段註解記錄的
                // 「任務目標 2/1、另一個 0/1」的表徵,只是換了一條進入點。
                //
                // 所以這裡直接記錄 TaskSelectRecipe 自己的回傳值當作成功與否的真值,
                // 而**不是**照上游用 Operations.GetSelectedRecipeEntry() 回頭驗證:
                // 那個讀的是一般製作手帳的 RecipeList,宇宙配方從來不會填它(同一段註解),
                // 拿它當閘門會讓所有宇宙 IPC 製作一律被誤判成失敗而拒絕啟動。
                var selectionSucceeded = false;
                var selectionDeadline = Environment.TickCount64 + RecipeSelectionTimeoutMs;

                PreCrafting.Tasks.Add((() =>
                {
                    var result = PreCrafting.TaskSelectRecipe(recipe.Value);
                    if (result == PreCrafting.TaskResult.Done)
                        selectionSucceeded = true;
                    return result;
                }, TimeSpan.FromMilliseconds(500)));

                // 上游原本是無上限地等 Tasks 排空。TaskSelectRecipe 的一般配方分支只會回
                // Retry(永遠不 Abort),所以視窗一直開不起來時這個等待可以無限期掛住整個
                // TaskManager。補一個上限,逾時就把佇列清掉並講出來。
                P.TM.Enqueue(() =>
                {
                    if (PreCrafting.Tasks.Count == 0)
                        return true;

                    if (Environment.TickCount64 < selectionDeadline)
                        return false;

                    PreCrafting.Tasks.Clear();
                    DuoLog.Error($"Artisan:等了 {RecipeSelectionTimeoutMs / 1000} 秒仍未選中配方 {recipeId},已放棄這次 IPC 製作請求。");
                    return true;
                }, RecipeSelectionTimeoutMs + 5_000, true, $"WaitingForRecipeSelection:{recipeId}");

                P.TM.DelayNext(100);
                P.TM.Enqueue(() =>
                {
                    if (!selectionSucceeded)
                    {
                        DuoLog.Error($"Artisan:配方 {recipeId} 沒有成功選取,已拒絕啟動製作(避免做成當下選著的那個配方)。");
                        return;
                    }

                    Endurance.IPCOverride = true;
                    Endurance.RecipeID = recipeId;
                    P.Config.CraftX = amount;
                    P.Config.CraftingX = true;
                    Endurance.ToggleEndurance(true);
                });
            }
            else
            {
                throw new Exception("RecipeID not found.");
            }
        }

        public static bool IsBusy()
        {
            return Endurance.Enable || CraftingListUI.Processing || P.TM.NumQueuedTasks > 0 || P.CTM.NumQueuedTasks > 0 || !(Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal);
        }

        public enum ArtisanMode
        {
            None = 0,
            Endurance = 1,
            Lists = 2,
        }
    }
}
