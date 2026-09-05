using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;

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

            // 每配方的「臨時」工藝設定:只活在記憶體裡,不寫進設定檔,Artisan 卸載時清空。
            Svc.PluginInterface.GetIpcProvider<uint, string, bool>("Artisan.SetTemporarySolver").RegisterFunc(SetTemporarySolver);
            Svc.PluginInterface.GetIpcProvider<uint, uint, bool, bool>("Artisan.SetTemporaryFood").RegisterFunc(SetTemporaryFood);
            Svc.PluginInterface.GetIpcProvider<uint, uint, bool, bool>("Artisan.SetTemporaryPotion").RegisterFunc(SetTemporaryPotion);
            Svc.PluginInterface.GetIpcProvider<uint, object>("Artisan.ClearTemporaryRecipeSettings").RegisterAction(ClearTemporaryRecipeSettings);
            Svc.PluginInterface.GetIpcProvider<object>("Artisan.ClearAllTemporarySettings").RegisterAction(ClearAllTemporarySettings);
            Svc.PluginInterface.GetIpcProvider<uint, string[]>("Artisan.GetAvailableSolvers").RegisterFunc(GetAvailableSolvers);
            Svc.PluginInterface.GetIpcProvider<bool, uint[]>("Artisan.GetAvailableFood").RegisterFunc(GetAvailableFood);
            Svc.PluginInterface.GetIpcProvider<bool, uint[]>("Artisan.GetAvailablePots").RegisterFunc(GetAvailablePots);

            Svc.PluginInterface.GetIpcProvider<List<(string, int)>>("Artisan.ReturnMacroInfo").RegisterFunc(ReturnMacroInfo);
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
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").UnregisterFunc();

            Svc.PluginInterface.GetIpcProvider<uint, string, bool>("Artisan.SetTemporarySolver").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, uint, bool, bool>("Artisan.SetTemporaryFood").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, uint, bool, bool>("Artisan.SetTemporaryPotion").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, object>("Artisan.ClearTemporaryRecipeSettings").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<object>("Artisan.ClearAllTemporarySettings").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<uint, string[]>("Artisan.GetAvailableSolvers").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, uint[]>("Artisan.GetAvailableFood").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, uint[]>("Artisan.GetAvailablePots").UnregisterFunc();

            Svc.PluginInterface.GetIpcProvider<List<(string, int)>>("Artisan.ReturnMacroInfo").UnregisterFunc();

            // 卸載時把臨時覆寫清乾淨。它們不進設定檔,但 P.Config 的 RecipeConfig 物件
            // 在同一個 session 內是活的,留著會讓下次載入沿用上次的臨時設定。
            ClearAllTemporarySettings();
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

        private static bool SetTemporarySolver(uint recipeId, string solverName)
        {
            if (!TryBuildCraft(recipeId, out var craft))
                return false;

            var selectedSolver = CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .FirstOrDefault(x => string.Equals(x.Name, solverName, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(selectedSolver.Name))
            {
                DuoLog.Error($"配方 {recipeId} 不支援求解器「{solverName}」。");
                return false;
            }

            var config = GetOrCreateRecipeConfig(recipeId);
            config.TempSolverType = selectedSolver.Def.GetType().FullName!;
            config.TempSolverFlavour = selectedSolver.Flavour;
            return true;
        }

        private static bool SetTemporaryFood(uint recipeId, uint itemId, bool hq)
        {
            if (!RecipeExists(recipeId))
                return false;
            if (itemId is not (RecipeConfig.Default or RecipeConfig.Disabled)
                && !ConsumableChecker.GetFood(true, hq).Any(x => x.Id == itemId))
            {
                DuoLog.Error($"物品 {itemId} 不是 Artisan 可用的{(hq ? " HQ" : " NQ")}製作食物。");
                return false;
            }

            var config = GetOrCreateRecipeConfig(recipeId);
            config.TempRequiredFood = itemId == RecipeConfig.Default ? null : itemId;
            config.TempRequiredFoodHQ = hq;
            return true;
        }

        private static bool SetTemporaryPotion(uint recipeId, uint itemId, bool hq)
        {
            if (!RecipeExists(recipeId))
                return false;
            if (itemId is not (RecipeConfig.Default or RecipeConfig.Disabled)
                && !ConsumableChecker.GetPots(true, hq).Any(x => x.Id == itemId))
            {
                DuoLog.Error($"物品 {itemId} 不是 Artisan 可用的{(hq ? " HQ" : " NQ")}製作藥水。");
                return false;
            }

            var config = GetOrCreateRecipeConfig(recipeId);
            config.TempRequiredPotion = itemId == RecipeConfig.Default ? null : itemId;
            config.TempRequiredPotionHQ = hq;
            return true;
        }

        private static void ClearTemporaryRecipeSettings(uint recipeId)
        {
            if (P.Config.RecipeConfigs.TryGetValue(recipeId, out var config))
                config.ClearTemporaryOverrides();
        }

        private static void ClearAllTemporarySettings()
        {
            foreach (var config in P.Config.RecipeConfigs.Values.ToArray())
                config.ClearTemporaryOverrides();
        }

        private static string[] GetAvailableSolvers(uint recipeId)
            => TryBuildCraft(recipeId, out var craft)
                ? CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                    .Where(x => !string.IsNullOrEmpty(x.Name))
                    .Select(x => x.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];

        private static uint[] GetAvailableFood(bool hq)
            => ConsumableChecker.GetFood(true, hq).Select(x => x.Id).Distinct().ToArray();

        private static uint[] GetAvailablePots(bool hq)
            => ConsumableChecker.GetPots(true, hq).Select(x => x.Id).Distinct().ToArray();

        private static RecipeConfig GetOrCreateRecipeConfig(uint recipeId)
        {
            if (!P.Config.RecipeConfigs.TryGetValue(recipeId, out var config) || config == null)
            {
                config = new RecipeConfig();
                P.Config.RecipeConfigs[recipeId] = config;
            }
            return config;
        }

        private static bool RecipeExists(uint recipeId)
        {
            if (LuminaSheets.RecipeSheet!.ContainsKey(recipeId))
                return true;
            DuoLog.Error($"找不到配方 {recipeId}。");
            return false;
        }

        private static bool TryBuildCraft(uint recipeId, out CraftState craft)
        {
            craft = null!;
            if (!LuminaSheets.RecipeSheet!.TryGetValue(recipeId, out var recipe))
            {
                DuoLog.Error($"找不到配方 {recipeId}。");
                return false;
            }

            var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
            craft = Crafting.BuildCraftStateForRecipe(CharacterStats.GetBaseStatsForClassHeuristic(job), job, recipe);
            return craft != null;
        }

        /// <summary>
        /// 回傳目前 Artisan 裡所有巨集的（名稱, ID）。
        /// 使用者可以在巨集編輯器裡自訂名稱，
        /// 呼叫端（例如宇宙探索）靠這個把名稱對回 ID。
        /// </summary>
        public static List<(string, int)> ReturnMacroInfo()
        {
            List<(string, int)> macros = new();

            var macroList = P.Config.MacroSolverConfig.Macros;
            if (macroList.Count > 0)
            {
                foreach (var macro in macroList)
                    macros.Add(new(macro.Name, macro.ID));
            }

            return macros;
        }

        public enum ArtisanMode
        {
            None = 0,
            Endurance = 1,
            Lists = 2,
        }
    }
}
