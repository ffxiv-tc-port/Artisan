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
            // 🔴 用「型別全名」而不是顯示名指定解算器。舊的 Artisan.SetTemporarySolver 比對的是
            //    ISolverDefinition.Desc.Name,而那是**在地化後**的字串(RaphaelSolver.cs 的
            //    "Raphael Recipe Solver".Loc()) —— 繁中介面下呼叫端傳英文名一定零命中,
            //    而且失敗形式是「回 false」不是例外。型別全名不隨介面語言變,也正好與
            //    RecipeConfig.SolverType 存的是同一種字串(CraftingProcessor.FindSolver 就是拿它比對)。
            //    🔑 舊端點刻意保留不動:換形狀時「換新名字」比「同名改語意」安全。
            Svc.PluginInterface.GetIpcProvider<uint, string, bool>("Artisan.SetTemporarySolverByType").RegisterFunc(SetTemporarySolverByType);
            Svc.PluginInterface.GetIpcProvider<uint, string[]>("Artisan.GetAvailableSolverTypes").RegisterFunc(GetAvailableSolverTypes);
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
            Svc.PluginInterface.GetIpcProvider<uint, string, bool>("Artisan.SetTemporarySolverByType").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, string[]>("Artisan.GetAvailableSolverTypes").UnregisterFunc();
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
            ClearAllTemporarySettingsCore();
        }

        static bool GetEnduranceStatus()
        {
            return Endurance.Enable;
        }

        // ToggleEndurance 會 PreCrafting.Tasks.Clear()（裸 List，framework 獨佔）。
        static void SetEnduranceStatus(bool s)
            => IpcFrameworkGate.Run("Artisan.SetEnduranceStatus", () => Endurance.ToggleEndurance(s));

        static bool IsListRunning()
        {
            return CraftingListUI.Processing;
        }

        static bool IsListPaused()
        {
            return CraftingListUI.Processing && CraftingListFunctions.Paused;
        }

        static void SetListPause(bool s)
            => IpcFrameworkGate.Run("Artisan.SetListPause", () =>
            {
                if (IsListPaused())
                    CraftingListFunctions.Paused = s;
            });

        static bool GetStopRequest()
        {
            return StopCraftingRequest;
        }

        // StopCraftingRequest 的 setter 會讀原生的 Svc.Condition、走 StopCrafting()
        //（Operations.CloseQuickSynthWindow 解 addon 指標、PreCrafting.Tasks 增刪），
        // 而 DuoLog 每一級都無條件寫使用者的聊天視窗（裸 Queue）。整支交回主執行緒。
        static void SetStopRequest(bool s)
            => IpcFrameworkGate.Run("Artisan.SetStopRequest", () =>
            {
                if (s)
                    DuoLog.Information("Artisan has been requested to stop by an external plugin.");
                else
                    DuoLog.Information("Artisan has been requested to restart by an external plugin.");

                StopCraftingRequest = s;
            });

        /// <summary>
        /// <c>Artisan.CraftItem</c> 端點。整支交回主執行緒：它會動 <c>PreCrafting.Tasks</c>
        /// 與 <c>P.TM</c>（兩者都是 framework 獨佔的裸 List）、寫 <c>P.Config</c>、印 DuoLog。
        /// <para/>
        /// 📌 找不到配方時擲的 <c>Exception</c> 照樣原型別傳回呼叫端：閘門用
        /// <c>GetAwaiter().GetResult()</c> 重擲，不會包成 <c>AggregateException</c>。
        /// </summary>
        public static void CraftX(ushort recipeId, int amount)
            => IpcFrameworkGate.Run("Artisan.CraftItem", () => CraftXCore(recipeId, amount));

        private unsafe static void CraftXCore(ushort recipeId, int amount)
        {
            if (LuminaSheets.RecipeSheet!.FindFirst(x => x.Value.RowId == recipeId, out var recipe))
            {
                // 🔴 選取階段失敗時「照樣啟動製作」是這條路徑原本的行為,而且完全無聲。
                // PreCrafting.Update() 對 TaskResult.Abort 的處理是 Tasks.Clear()(見
                // PreCrafting.cs 的 switch),所以「宇宙筆記裡找不到這個配方、等到逾時中止」
                // 與「選取成功」在下面這個 Tasks.Count == 0 的判斷裡**完全分不出來**。
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

        /// <summary>
        /// 框架執行緒每幀發佈的「Artisan 現在忙不忙」快照。<see cref="IsBusy"/> 從別的執行緒
        /// 被呼叫時讀的就是這一份。
        /// </summary>
        /// <remarks>
        /// 🔴 這個端點刻意<b>不</b>走 <see cref="IpcFrameworkGate"/>：它是別的外掛<b>高頻輪詢</b>的，
        /// 而閘門的逾時是 <see cref="IpcFrameworkGate.TimeoutMs"/> 毫秒 —— 遊戲一讀取畫面，
        /// 每一次輪詢都會把呼叫端整整卡住五秒。
        /// <para/>
        /// 🔑 輪詢型端點的正解是「框架執行緒推快照、端點只讀快照」：非阻塞、最多差一幀（約 16ms）。
        /// <para/>
        /// 📌 還沒發佈過任何一幀時是 <see langword="false"/>＝「不忙」，而那一刻 Artisan 確實
        /// 什麼都還沒做（快照的發佈點在 <c>Artisan.OnFrameworkUpdate</c> 最前面，
        /// 登出那一幀也會發佈，所以不會停在登出前的「忙」）。
        /// </remarks>
        private static volatile bool busySnapshot;

        /// <summary>由 <c>Artisan.OnFrameworkUpdate</c> 每幀無條件呼叫一次。</summary>
        internal static void PublishBusySnapshot() => busySnapshot = IsBusyCore();

        /// <summary>現算版本。<b>只在框架執行緒上呼叫</b>（讀 TaskManager 的裸 List）。</summary>
        private static bool IsBusyCore()
        {
            return Endurance.Enable || CraftingListUI.Processing || P.TM.NumQueuedTasks > 0 || P.CTM.NumQueuedTasks > 0 || !(Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal);
        }

        public static bool IsBusy()
        {
            return Svc.Framework.IsInFrameworkUpdateThread ? IsBusyCore() : busySnapshot;
        }

        // TryBuildCraft 走 CharacterStats.GetBaseStatsForClassHeuristic ——
        // 它解 RaptureGearsetModule.Instance()->Entries（原生指標），而寫入端是
        // P.Config.RecipeConfigs（裸 Dictionary）。整支交回主執行緒。
        private static bool SetTemporarySolver(uint recipeId, string solverName)
            => IpcFrameworkGate.Get<bool>("Artisan.SetTemporarySolver", () => SetTemporarySolverCore(recipeId, solverName), false);

        private static bool SetTemporarySolverCore(uint recipeId, string solverName)
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

        /// <summary>
        /// 用解算器定義的<b>型別全名</b>指定臨時解算器,例如
        /// <c>Artisan.CraftingLogic.Solvers.RaphaelSolverDefintion</c>(注意上游把 Definition 拼成
        /// Defintion,這裡逐字照抄實際型別名)或 <c>Artisan.CraftingLogic.Solvers.ExpertSolverDefinition</c>。
        /// 可用的名單向 <c>Artisan.GetAvailableSolverTypes</c> 拿,不要寫死。
        /// </summary>
        /// <remarks>
        /// 🔴 與 <c>Artisan.SetTemporarySolver</c> 的差別在於比對的東西:那一支比的是
        /// <c>Desc.Name</c>,而 <c>Name</c> 是<b>在地化後</b>的顯示字串,繁中介面下傳英文名恆為 false。
        /// ⚠️ 一個定義可以提供多個 flavour(巨集解算器是每個巨集一個),型別全名只認得到定義,
        /// 所以這一支取的是<b>該定義目前第一個可用的 flavour</b>。要指定到特定巨集請用舊端點。
        /// 📌 設定的是 <c>TempSolverType</c>／<c>TempSolverFlavour</c>,兩者都掛著
        /// <c>[NonSerialized, JsonIgnore]</c> ⇒ <b>絕不會被寫進設定檔</b>。
        /// </remarks>
        private static bool SetTemporarySolverByType(uint recipeId, string solverTypeFullName)
            => IpcFrameworkGate.Get<bool>("Artisan.SetTemporarySolverByType", () => SetTemporarySolverByTypeCore(recipeId, solverTypeFullName), false);

        private static bool SetTemporarySolverByTypeCore(uint recipeId, string solverTypeFullName)
        {
            if (string.IsNullOrEmpty(solverTypeFullName))
            {
                PluginLog.Information($"[Artisan IPC] SetTemporarySolverByType:配方 {recipeId} 收到空的解算器型別名,已拒絕。");
                return false;
            }

            if (!TryBuildCraft(recipeId, out var craft, quiet: true))
                return false;

            // ⚠️ GetAvailableSolversForRecipe 在每個定義之後會 yield 一個 default 當分隔,
            //    那一筆的 Def 是 null —— 先濾掉再取 FullName,否則會 NRE。
            var selectedSolver = CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .FirstOrDefault(x => x.Def != null && string.Equals(x.Def.GetType().FullName, solverTypeFullName, StringComparison.Ordinal));
            if (selectedSolver.Def == null)
            {
                // 失敗寫 Information 不寫 DuoLog:DuoLog 每一級都無條件印到使用者的聊天視窗,
                // 而這條路徑可能被別的外掛在無人看畫面時反覆呼叫。
                PluginLog.Information($"[Artisan IPC] SetTemporarySolverByType:配方 {recipeId} 目前沒有可用的解算器「{solverTypeFullName}」。目前可用:{string.Join(", ", AvailableSolverTypes(craft))}");
                return false;
            }

            var config = GetOrCreateRecipeConfig(recipeId);
            config.TempSolverType = selectedSolver.Def.GetType().FullName!;
            config.TempSolverFlavour = selectedSolver.Flavour;
            return true;
        }

        /// <summary>
        /// 這個配方目前可用的解算器<b>型別全名</b>清單,可直接餵給
        /// <c>Artisan.SetTemporarySolverByType</c>。與 <c>Artisan.GetAvailableSolvers</c> 的差別是
        /// 後者回的是在地化後的顯示名(給人看的),這一支回的是不隨語言變的識別字串(給程式用的)。
        /// </summary>
        private static string[] GetAvailableSolverTypes(uint recipeId)
            => IpcFrameworkGate.Get<string[]>("Artisan.GetAvailableSolverTypes",
                () => TryBuildCraft(recipeId, out var craft, quiet: true) ? AvailableSolverTypes(craft) : [],
                []);

        private static string[] AvailableSolverTypes(CraftState craft)
            => CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .Where(x => x.Def != null)
                .Select(x => x.Def.GetType().FullName!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        // ConsumableChecker.GetFood(true, ...) 解 InventoryManager.Instance()->（原生指標）。
        private static bool SetTemporaryFood(uint recipeId, uint itemId, bool hq)
            => IpcFrameworkGate.Get<bool>("Artisan.SetTemporaryFood", () => SetTemporaryFoodCore(recipeId, itemId, hq), false);

        private static bool SetTemporaryFoodCore(uint recipeId, uint itemId, bool hq)
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

        // ConsumableChecker.GetPots(true, ...) 解 InventoryManager.Instance()->（原生指標）。
        private static bool SetTemporaryPotion(uint recipeId, uint itemId, bool hq)
            => IpcFrameworkGate.Get<bool>("Artisan.SetTemporaryPotion", () => SetTemporaryPotionCore(recipeId, itemId, hq), false);

        private static bool SetTemporaryPotionCore(uint recipeId, uint itemId, bool hq)
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
            => IpcFrameworkGate.Run("Artisan.ClearTemporaryRecipeSettings", () =>
            {
                if (P.Config.RecipeConfigs.TryGetValue(recipeId, out var config))
                    config.ClearTemporaryOverrides();
            });

        private static void ClearAllTemporarySettings()
            => IpcFrameworkGate.Run("Artisan.ClearAllTemporarySettings", ClearAllTemporarySettingsCore);

        /// <summary>
        /// ⚠️ <c>Dispose()</c> 直接走這一支、不經閘門：外掛卸載時 framework 可能已經在收攤，
        /// 讓卸載路徑去等主執行緒最多 5 秒沒有好處，而卸載本來就不與 IPC 呼叫並行。
        /// 這一支的走訪已經先 <c>ToArray()</c> 拍快照，行為與改動前逐字相同。
        /// </summary>
        private static void ClearAllTemporarySettingsCore()
        {
            foreach (var config in P.Config.RecipeConfigs.Values.ToArray())
                config.ClearTemporaryOverrides();
        }

        private static string[] GetAvailableSolvers(uint recipeId)
            => IpcFrameworkGate.Get<string[]>("Artisan.GetAvailableSolvers", () => GetAvailableSolversCore(recipeId), []);

        private static string[] GetAvailableSolversCore(uint recipeId)
            => TryBuildCraft(recipeId, out var craft)
                ? CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                    .Where(x => !string.IsNullOrEmpty(x.Name))
                    .Select(x => x.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];

        private static uint[] GetAvailableFood(bool hq)
            => IpcFrameworkGate.Get<uint[]>("Artisan.GetAvailableFood", () => ConsumableChecker.GetFood(true, hq).Select(x => x.Id).Distinct().ToArray(), []);

        private static uint[] GetAvailablePots(bool hq)
            => IpcFrameworkGate.Get<uint[]>("Artisan.GetAvailablePots", () => ConsumableChecker.GetPots(true, hq).Select(x => x.Id).Distinct().ToArray(), []);

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

        /// <param name="quiet">
        /// true 時把「找不到配方」寫 <c>Information</c> 而不是 <c>DuoLog.Error</c>。
        /// DuoLog 每一級都無條件印到使用者的聊天視窗,而新的查詢型端點可能被反覆呼叫。
        /// 預設 false ＝ 既有呼叫端行為逐字不變。
        /// </param>
        private static bool TryBuildCraft(uint recipeId, out CraftState craft, bool quiet = false)
        {
            craft = null!;
            if (!LuminaSheets.RecipeSheet!.TryGetValue(recipeId, out var recipe))
            {
                if (quiet)
                    PluginLog.Information($"[Artisan IPC] 找不到配方 {recipeId}。");
                else
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
            => IpcFrameworkGate.Get<List<(string, int)>>("Artisan.ReturnMacroInfo", ReturnMacroInfoCore, []);

        // 走訪 P.Config.MacroSolverConfig.Macros（裸 List，巨集編輯器會增刪）。
        private static List<(string, int)> ReturnMacroInfoCore()
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
