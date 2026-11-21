using Artisan.Autocraft;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic.Solvers
{
    public class RaphaelSolverDefintion : ISolverDefinition
    {
        public Solver Create(CraftState craft, int flavour)
        {
            var key = RaphaelCache.GetKey(craft);
            if (RaphaelCache.HasSolution(craft, out var output))
            {
                // a macro plays back a fixed action list and cannot react to conditions; wrap it so we can deviate
                // opportunistically, but only when the simulator says the deviation still finishes with strictly better quality.
                // 再包一層奇蹟之材:Raphael 的狀態模型只有 通常/高品質/最高品質/低品質,所以它永遠不會提議 41269。
                // ⚠️ 順序是刻意的:奇蹟之材在最外層,底下才是機會性偏離,
                //    這樣閘門的 rollout 驅動的是「真正會執行的那份計畫」。
                // 品質門檻與 RecipeArgs 送給 raphael-cli 的上限是同一個值;讀不到配方表(0)就不跳。
                var skipAt = P.Config.RaphaelSolverConfig.SkipQualityWhenMaxed ? Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable) : 0;
                return new MaterialMiracleSolver(new OpportunisticSolver(new MacroSolver(output!, craft) { SkipQualityAtOrAbove = skipAt }));
            }
            return craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
        }

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            if (RaphaelCache.HasSolution(craft, out var solution))
            {
                yield return new(this, 3, 0, "Raphael Recipe Solver".Loc());
            }
            else if (!RaphaelCache.FallbackToStandardAllowed)
            {
                // Yielding an *unsupported* flavour rather than nothing at all: GetAvailableSolversForRecipe
                // still filters this out of every picker (it passes returnUnsupported: false), but FindSolver
                // now returns it for a recipe explicitly assigned to Raphael, so CraftingProcessor raises
                // SolverFailed with this reason instead of silently starting on the standard solver.
                var ignored = RaphaelCache.DescribeIgnoredSolution(craft);
                var failed = RaphaelCache.DescribeFailure(craft);
                // 三種「沒有解」要分得開:①有解但能力值對不上 ②這個 session 已經求解失敗過
                // ③根本還沒產生過。
                var reason = ignored.Length > 0
                    ? "No Raphael solution matches your current stats: ??".Loc(ignored)
                    : failed.Length > 0
                        ? failed
                        : "No Raphael solution has been generated for this recipe.".Loc();
                yield return new(this, 3, 0, "Raphael Recipe Solver".Loc(), reason);
            }
        }
    }

    internal static partial class RaphaelCache
    {
        internal static readonly ConcurrentDictionary<string, Tuple<CancellationTokenSource, Task>> Tasks = [];
        private static readonly CancellationTokenSource Shutdown = new();

        /// <summary>卸載時呼叫：之後不再寫設定，並取消所有進行中的求解（砍掉 raphael-cli，含背景求解）。</summary>
        internal static void Dispose()
        {
            Shutdown.Cancel();
            ECommons.GenericHelpers.Safe(StopBackground);
            foreach (var t in Tasks.Values)
                ECommons.GenericHelpers.Safe(t.Item1.Cancel);
        }

        /// <summary>
        /// 在框架執行緒上執行 <paramref name="apply"/> 並等它做完；P.Config 不是執行緒安全的，背景工作只能從這裡寫。
        /// 卸載後不執行、等待改擲 <see cref="OperationCanceledException"/>（延遲工作不隨插件卸載取消，所以自己判）。
        /// </summary>
        private static void ApplyOnFramework(Action apply)
        {
            var token = Shutdown.Token;
            token.ThrowIfCancellationRequested();
            var run = Svc.Framework.Run(() =>
            {
                if (!token.IsCancellationRequested && !Svc.Framework.IsFrameworkUnloading)
                    apply();
            }, token);
            try
            {
                run.Wait(token);
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                throw new OperationCanceledException(token);
            }
        }

        [NonSerialized]
        public static Dictionary<string, RaphaelSolutionConfig> TempConfigs = new();

        /// <summary>
        /// 這個 session 裡已知求解不出來的組合(配方＋職業等級＋能力值)。
        /// 🔴 為什麼需要:唯一會「自動」呼叫 <see cref="Build"/> 的地方是
        /// <see cref="DrawRaphaelDropdown"/>,而它是 <c>RecipeConfig.Draw</c> 的一部分 ——
        /// 那是 ImGui 的繪製路徑,<b>每一幀都會跑一次</b>。
        /// 把失敗記下來之後,同一組條件在同一個 session 內只會試一次。
        /// 使用者按「Build Raphael Solution」等於明確要求重試 —— 那顆按鈕會先清掉這裡的紀錄。
        /// </summary>
        internal static readonly ConcurrentDictionary<string, string> FailedSolves = [];

        /// <summary>
        /// 失敗快取的鍵。與 <see cref="GetKey"/> 是不同的東西:那個是「解的形狀」
        /// (等級/進度/品質/耐久…),這個是「這次求解請求的身分」—— 配方、職業等級、
        /// 能力值或起始品質任一改變都應該重新試一次。
        /// </summary>
        internal static string GetFailureKey(CraftState craft)
            => $"{craft.RecipeId}/{craft.StatLevel}/{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}/{craft.InitialQuality}";

        /// <summary>這組條件上次求解失敗的原因;沒失敗過就回空字串。</summary>
        internal static string DescribeFailure(CraftState craft)
            => FailedSolves.TryGetValue(GetFailureKey(craft), out var reason) ? reason : "";

        /// <summary>
        /// 「職業等級根本不夠,送去 raphael-cli 一定是白跑」的判斷。
        /// 回傳非 null 就代表不該 spawn,字串是要給使用者看的原因。
        /// ⚠️ 用 <c>craft.CraftLevel</c> 當需求等級是刻意的:它就是
        /// <c>Recipe.RecipeLevelTable.ClassJobLevel</c>,而宇宙配方(Number == 0)那條路徑
        /// 在 <c>BuildCraftStateForRecipe</c> 已經改成依玩家等級選 RecipeLevelTable,
        /// 兩者相等 ⇒ 這個閘門對宇宙製作不會誤擋。
        /// </summary>
        internal static string? DescribeLevelGate(CraftState craft)
        {
            if (craft.StatLevel > 0 && craft.StatLevel >= craft.CraftLevel)
                return null;

            var jobName = LuminaSheets.ClassJobSheet != null
                && LuminaSheets.ClassJobSheet.TryGetValue(craft.Recipe.CraftType.RowId + 8, out var classJob)
                    ? classJob.Name.ToString()
                    : $"CraftType {craft.Recipe.CraftType.RowId}";
            var recipeName = craft.Recipe.ItemResult.Value.Name.ToString();

            return $"{jobName}等級 {craft.StatLevel} 低於配方需求 {craft.CraftLevel},無法求解「{recipeName}」(配方 {craft.RecipeId})。";
        }

        /// <summary>
        /// raphael-cli 的配方參數。能從台服資料表算齊就送明確數字(<c>--custom-recipe</c>＋<c>--override-base-increases</c>),
        /// 求解設定與 <c>--recipe-id</c> 相同,但不依賴 raphael-data 寫死的配方表(改版後新配方也解得出來)。
        /// 算不齊時回 <c>--recipe-id</c>,<paramref name="fallbackReason"/> 是原因;送明確數字時為 null。
        /// </summary>
        internal static string RecipeArgs(CraftState craft, out string? fallbackReason)
        {
            fallbackReason = ExplicitRecipeUnavailable(craft);
            if (fallbackReason != null)
                return $"--recipe-id {craft.RecipeId}";

            // 最大品質用配方表原值,不用 CraftQualityMax(有 RequiredQuality 的配方那欄被改成門檻)。
            var maxQuality = Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable);
            var expert = craft.CraftExpert ? 1 : 0;
            return $"--custom-recipe {craft.LevelTable.RowId} {craft.CraftProgress} {maxQuality} {craft.CraftDurability} {expert} "
                + $"--override-base-increases {craft.CraftLevel} {Simulator.BaseProgress(craft)} {Simulator.BaseQuality(craft)}";
        }

        private static string? ExplicitRecipeUnavailable(CraftState craft)
        {
            var lt = craft.LevelTable;
            var recipe = craft.Recipe;
            if (lt.RowId == 0 || craft.CraftProgressDivider <= 0 || craft.CraftQualityDivider <= 0)
                return "讀不到配方等級表";
            // raphael-cli 依等級查自己的等級對照表(只到 100),超出會直接崩潰。
            if (craft.CraftLevel is <= 0 or > 100)
                return $"配方等級 {craft.CraftLevel} 超出 raphael-cli 支援範圍";
            // raphael-cli 的換算:可調等級的配方用 GathererCrafterLvAdjustTable[min(上限, 職業等級)] 那列等級表、耐久一律以 80 計,
            // 其餘用配方自己的等級表。Artisan 對等級不足 100 的宇宙配方一律換表,兩邊不一致時保留舊參數。
            var maxAdjust = recipe.MaxAdjustableJobLevel.RowId;
            var sameTable = maxAdjust == 0
                ? lt.RowId == recipe.RecipeLevelTable.RowId
                : Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.GathererCrafterLvAdjustTable>().GetRowOrDefault(Math.Min(maxAdjust, (uint)craft.StatLevel)) is { } adjust
                    && adjust.RecipeLevel.RowId == lt.RowId && recipe.RecipeLevelTable.Value.Durability == 80;
            if (!sameTable)
                return "配方等級表與 raphael-cli 的換算方式不同";
            var maxQuality = Calculations.RecipeMaxQuality(recipe, lt);
            int[] values = [craft.CraftProgress, maxQuality, craft.CraftDurability, Simulator.BaseProgress(craft), Simulator.BaseQuality(craft)];
            if (craft.CraftProgress <= 0 || craft.CraftDurability <= 0 || values.Any(v => v is < 0 or > ushort.MaxValue))
                return "配方數字超出 raphael-cli 可接受的範圍";
            return null;
        }

        /// <summary>
        /// 品質需求很低：第 1 步閒靜（含起始品質）就達到送給 raphael-cli 的品質上限。
        /// 這時帶 --backload-progress 等於強迫閒靜起手；不帶，Raphael 自己挑（堅信比較好時選堅信）。
        /// </summary>
        internal static bool ReflectFillsQuality(CraftState craft)
            => P.Config.RaphaelSolverConfig.LowQualityFreeOpener
            && Simulator.ReflectReaches(craft, Simulator.CreateInitial(craft, craft.InitialQuality), Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable));

        // 每個鍵每次載入只判一次、最多重求一次（0＝判過不必、1＝送出重求）：配方視窗每幀呼叫，判準要配置模擬狀態。
        // 成功後解帶 RaphaelNoBackload 不會再判舊；失敗、逾時就沿用舊解。
        private static readonly ConcurrentDictionary<string, byte> FreeOpenerChecked = new();

        /// <summary>
        /// 快取的解用閒靜起手、可能是在後置進展下求的，而現在求解會改成不後置 ⇒ 值得重求一次。
        /// 重求後 Raphael 仍選閒靜，解帶 <see cref="MacroSolverSettings.MacroOptions.RaphaelNoBackload"/>，之後不再判舊。
        /// </summary>
        internal static bool NeedsFreeOpenerRefresh(CraftState craft, MacroSolverSettings.Macro solution, string? key = null)
            => !solution.Options.RaphaelNoBackload
            && solution.Steps is [{ Action: Skills.Reflect }, ..]
            && (TempConfigs.TryGetValue(key ?? GetKey(craft), out var c) ? c.BackloadProgress : P.Config.RaphaelSolverConfig.AllowBackloadProgress)
            && ReflectFillsQuality(craft);

        /// <summary>
        /// 符合 <see cref="NeedsFreeOpenerRefresh"/> 就照自動產生的條件重求（舊解留著，成功才換掉）。回傳這次有沒有送出。
        /// <paramref name="key"/> 是 <see cref="GetKey"/>(craft)，呼叫端有現成的就傳（配方視窗每幀呼叫）。只在框架執行緒呼叫。
        /// </summary>
        internal static bool TryRefreshFreeOpener(CraftState craft, MacroSolverSettings.Macro solution, string? key = null, TimeSpan? fallbackDeadline = null)
        {
            if (solution.Options.RaphaelNoBackload || solution.Steps is not [{ Action: Skills.Reflect }, ..])
                return false;
            key ??= GetKey(craft);
            if (Tasks.ContainsKey(key) || !FreeOpenerChecked.TryAdd(key, 0) || !NeedsFreeOpenerRefresh(craft, solution, key))
                return false;
            FreeOpenerChecked[key] = 1;
            if (!TryAutoBuild(craft, fallbackDeadline, solution))
                return false;
            Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 的快取解用閒靜起手、可能是在後置進展下求的，品質需求很低，重求一次（舊解留著，求出來才換掉）。");
            return true;
        }

        /// <summary>這組條件正在跑 <see cref="TryRefreshFreeOpener"/> 送出的重求。</summary>
        internal static bool FreeOpenerRefreshInProgress(CraftState craft)
        {
            var key = GetKey(craft);
            return Tasks.ContainsKey(key) && FreeOpenerChecked.TryGetValue(key, out var state) && state == 1;
        }

        /// <summary>退路只用在宇宙探索配方：量測只做過宇宙配方；其他配方（收藏品門檻等）照用使用者要的確保可靠度。</summary>
        internal static bool ReliabilityFallbackApplies(CraftState craft) => craft.IsCosmic;

        /// <summary>確保可靠度下的解（照順序、通常狀態走完）沒做到最高品質：值得再求一份不確保的解。</summary>
        internal static bool ReliabilityFallbackNeeded(PlanOutcome reliable, int cap)
            => cap > 0 && (!reliable.Done || reliable.Quality < cap);

        /// <summary>不確保可靠度的解要做得完，而且品質（夾上限）嚴格較高才改用；確保的解做不完時只要做得完。</summary>
        internal static bool PreferReliabilityFallback(PlanOutcome reliable, PlanOutcome fallback)
            => fallback.Done && (!reliable.Done || fallback.Quality > reliable.Quality);

        // 同 FreeOpenerChecked：每個鍵每次載入只判一次、最多重求一次（0＝判過不必、1＝送出重求）。
        private static readonly ConcurrentDictionary<string, byte> ReliabilityChecked = new();

        /// <summary>
        /// 快取的解是舊版求的（沒判過確保可靠度的退路）、這組條件求解會確保可靠度、而且這份解沒做到最高品質 ⇒ 值得重求一次。
        /// 重求後的解帶 <see cref="MacroSolverSettings.MacroOptions.RaphaelReliabilityChecked"/>，之後不再判舊。
        /// </summary>
        internal static bool NeedsReliabilityRefresh(CraftState craft, MacroSolverSettings.Macro solution, string? key = null)
            => P.Config.RaphaelSolverConfig.ReliabilityFallback
            && ReliabilityFallbackApplies(craft)
            && !solution.Options.RaphaelReliabilityChecked
            && (TempConfigs.TryGetValue(key ?? GetKey(craft), out var c) ? c.EnsureReliability : P.Config.RaphaelSolverConfig.AllowEnsureReliability)
            && ReliabilityFallbackNeeded(EvaluatePlan(craft, solution.Steps), Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable));

        /// <summary>同 <see cref="TryRefreshFreeOpener"/>，判準換成 <see cref="NeedsReliabilityRefresh"/>。只在框架執行緒呼叫。</summary>
        internal static bool TryRefreshReliability(CraftState craft, MacroSolverSettings.Macro solution, string? key = null, TimeSpan? fallbackDeadline = null)
        {
            if (solution.Options.RaphaelReliabilityChecked || !P.Config.RaphaelSolverConfig.ReliabilityFallback)
                return false;
            key ??= GetKey(craft);
            if (Tasks.ContainsKey(key) || !ReliabilityChecked.TryAdd(key, 0) || !NeedsReliabilityRefresh(craft, solution, key))
                return false;
            ReliabilityChecked[key] = 1;
            if (!TryAutoBuild(craft, fallbackDeadline, solution))
                return false;
            Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 的快取解是確保可靠度下求的、沒做到最高品質，照新規則重求一次（舊解留著，求出來才換掉）。");
            return true;
        }

        /// <summary>這組條件正在跑 <see cref="TryRefreshReliability"/> 送出的重求。</summary>
        internal static bool ReliabilityRefreshInProgress(CraftState craft)
        {
            var key = GetKey(craft);
            return Tasks.ContainsKey(key) && ReliabilityChecked.TryGetValue(key, out var state) && state == 1;
        }

        /// <summary>
        /// 確保可靠度下的解沒做到最高品質時，用 <paramref name="fallbackArguments"/>（不確保、不後置進展）再求一次，
        /// 符合 <see cref="PreferReliabilityFallback"/> 就改用。回傳要存的解與它有沒有後置進展。背景執行緒呼叫；被取消就照用確保可靠度的解。
        /// </summary>
        private static (MacroSolverSettings.Macro Macro, bool Backload) ApplyReliabilityFallback(string key, CraftState craft, MacroSolverSettings.Macro reliableMacro, bool backload,
            string fallbackArguments, CancellationToken token)
        {
            var cap = Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable);
            var reliable = EvaluatePlan(craft, reliableMacro.Steps);
            if (!ReliabilityFallbackApplies(craft) || !ReliabilityFallbackNeeded(reliable, cap) || token.IsCancellationRequested)
                return (reliableMacro, backload);
            using var process = new Process { StartInfo = CliStartInfo(fallbackArguments) };
            using (token.Register(() =>
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }
            }))
            {
                process.Start();
                var errorTask = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                var error = errorTask.Result.Trim();
                process.WaitForExit();
                if (token.IsCancellationRequested)
                {
                    Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 在確保可靠度下做不到最高品質（{Describe(reliable)}／上限 {cap}），不確保可靠度的解還沒求完就被停止，照用原本的解。");
                    return (reliableMacro, backload);
                }
                var alt = process.ExitCode == 0 ? NewSolution(key, craft, output, false) : null;
                var fallback = alt is { Steps.Count: > 0 } ? EvaluatePlan(craft, alt.Steps) : default;
                var detail = $"品質 {Describe(reliable)}→{(alt == null ? $"求解失敗（{DescribeCliFailure(error, process.ExitCode)}）" : Describe(fallback))}（上限 {cap}）、步數 {reliable.Steps}→{fallback.Steps}";
                if (alt != null && PreferReliabilityFallback(reliable, fallback))
                {
                    Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 在確保可靠度下做不到最高品質，改用不確保可靠度、不後置進展的解：{detail}。");
                    return (alt, false);
                }
                Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 在確保可靠度下做不到最高品質，但不確保可靠度的解沒有比較好，照用原本的解：{detail}。");
                return (reliableMacro, backload);
            }
        }

        /// <param name="fallbackDeadline">
        /// 非 null＝呼叫端在等這份解、求不出來就改用別的解算器（<see cref="CosmicRaphaelGate"/>）：逾時改用這個期限，
        /// 失敗、逾時、取消都只記進 <see cref="FailedSolves"/> 並寫 Information，不關耐力模式、不關自動產生。
        /// </param>
        /// <param name="replaces">非 null＝重求這份還在用的舊解：先不刪、求出來才換掉（連同它在別的鍵下的那筆）；失敗比照 fallbackDeadline 只記 Information。</param>
        public static void Build(CraftState craft, RaphaelSolutionConfig config, TimeSpan? fallbackDeadline = null, MacroSolverSettings.Macro? replaces = null)
        {
            var key = GetKey(craft);
            var refresh = replaces != null;

            if (CLIExists() && !Tasks.ContainsKey(key))
            {
                // 🔴 這兩道閘門是「每幀重試」的剎車 —— 呼叫端是每一幀都會重畫的 UI。
                var failKey = GetFailureKey(craft);
                if (FailedSolves.ContainsKey(failKey))
                    return;

                // 等級不夠時連 spawn 都不必:raphael-cli 收到 --level 0 只會回 NoSolution。
                var levelGate = DescribeLevelGate(craft);
                if (levelGate != null)
                {
                    // TryAdd 保證同一組條件只寫一行 log(不然這裡每幀都會印一次)。
                    if (FailedSolves.TryAdd(failKey, levelGate))
                        Svc.Log.Information($"Raphael:{levelGate}不送出求解。");
                    return;
                }

                if (!refresh)
                    P.Config.RaphaelSolverCacheV3.TryRemove(key, out _);

                Svc.Log.Information("Spawning Raphael process");

                var process = new Process()
                {
                    StartInfo = CliStartInfo(SolveArguments(craft, config, false, P.Config.RaphaelSolverConfig.MaximumThreads, out var backload)),
                };

                Svc.Log.Information(process.StartInfo.Arguments);
                // 參數在框架執行緒組好（會讀設定與資料表）；用不用得到，等確保可靠度的解出來才知道。
                var fallbackArguments = config.EnsureReliability && P.Config.RaphaelSolverConfig.ReliabilityFallback
                    ? SolveArguments(craft, new RaphaelSolutionConfig { HeartAndSoul = config.HeartAndSoul, QuickInno = config.QuickInno }, true, P.Config.RaphaelSolverConfig.MaximumThreads, out _)
                    : null;

                var cts = new CancellationTokenSource();
                // 🔴 取消回呼跑在 CancelAfter 的計時器執行緒(或任何呼叫 Cancel() 的執行緒)上,回呼丟出來的例外
                //    會變成執行緒集區的未處理例外(計時器回呼只攔 ObjectDisposedException)。
                //    process.Kill() 在「行程根本沒啟動成功」時會丟 InvalidOperationException,所以這個清理回呼必須自己攔下例外,
                //    而且 Tasks 的移除要放進 finally 才保證跑得到 —— key 沒被移掉的話 InProgressAny() 就永遠是 true。
                cts.Token.Register(() =>
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Information($"Raphael: 取消時終止 raphael-cli 失敗(多半代表它已經自己結束了):{ex.Message}");
                    }
                    finally
                    {
                        Tasks.TryRemove(key, out _);
                    }
                });
                var timeout = TimeSpan.FromMinutes(P.Config.RaphaelSolverConfig.TimeOutMins);
                var fallbackMode = fallbackDeadline != null || refresh;
                if (fallbackDeadline is { } deadline && deadline < timeout)
                    timeout = deadline;
                cts.CancelAfter(timeout);

                // 🔴 這個工作必須「先登記進 Tasks,再開始跑」。Task.Run(...) 之後才 TryAdd 的話:
                //    快速失敗的路徑(raphael-cli 一啟動就非零離開)可以在 TryAdd 之前就把 key 移掉,
                //    接著 TryAdd 又把它加回去,而 cts 已經取消不會再觸發清理 ⇒ key 永遠留在 Tasks 裡,
                //    InProgressAny() 永遠是 true,Operations.RepeatActualCraft() 從此被擋死。
                var task = new Task(() =>
                {
                    try
                    {
                        process.Start();
                        var output = process.StandardOutput.ReadToEnd();
                        var error = process.StandardError.ReadToEnd().Trim();
                        // 卸載時 Dispose 砍掉 raphael-cli 的那一下不是求解失敗：不記失敗、不洗聊天、不碰耐力模式。
                        if (Shutdown.IsCancellationRequested)
                            return;
                        // 被砍掉的 raphael-cli 離開碼也是非零；等候中的呼叫端要的是「沒解、改用別的」，不是故障。
                        if (fallbackMode && cts.IsCancellationRequested)
                        {
                            FailedSolves[failKey] = $"Raphael 在 {timeout.TotalSeconds:0} 秒內沒算完（或被手動取消），已停止求解";
                            Svc.Log.Information($"Raphael:{FailedSolves[failKey]}(配方 {craft.RecipeId},{key})。");
                            return;
                        }
                        if (process.ExitCode != 0)
                        {
                            var failure = DescribeCliFailure(error, process.ExitCode);
                            // 記下來:同一組條件在這個 session 內不再重複 spawn。
                            FailedSolves[failKey] = failure;
                            if (fallbackMode)
                                Svc.Log.Information($"Raphael 求解失敗(配方 {craft.RecipeId}):{failure}");
                            else
                                DuoLog.Error(failure);
                            cts.Cancel();
                            if (!fallbackMode)
                                AbortWaitingAutomation();
                            return;
                        }
                        var macro = NewSolution(key, craft, output, backload);
                        if (fallbackArguments != null && macro.Steps.Count > 0)
                            (macro, backload) = ApplyReliabilityFallback(key, craft, macro, backload, fallbackArguments, cts.Token);
                        macro.Options.RaphaelReliabilityChecked = true;

                        var solved = macro.Steps.Count > 0;
                        // 相容配方掃描讀的是資料表，留在背景執行緒做；P.Config 的讀寫與存檔一律排回框架執行緒。
                        List<CraftState>? crafts = null;
                        if (solved && P.Config.RaphaelSolverConfig.AutoSwitch && P.Config.RaphaelSolverConfig.AutoSwitchOnAll && !cts.IsCancellationRequested)
                        {
                            // 產生這份解的配方本身一定要套用,不靠下面的相容性掃描把它找回來。
                            crafts = AllValidCrafts(key, craft.Recipe.CraftType.RowId)
                                .Append(craft)
                                .DistinctBy(x => x.Recipe.RowId)
                                .ToList();
                        }

                        var autoGenerateWas = false;
                        ApplyOnFramework(() =>
                        {
                            if (refresh && !solved)
                                return; // 舊解照用
                            StoreSolution(key, macro, replaces);

                            if (cts.IsCancellationRequested)
                                return;
                            if (!solved)
                            {
                                // 等候中的呼叫端有 FailedSolves 擋重送；關掉自動產生會讓之後的開工前求解也靜默失效。
                                if (fallbackMode)
                                    return;
                                autoGenerateWas = P.Config.RaphaelSolverConfig.AutoGenerate;
                                P.Config.RaphaelSolverConfig.AutoGenerate = false;
                                return;
                            }

                            if (P.Config.RaphaelSolverConfig.AutoSwitch)
                            {
                                if (!P.Config.RaphaelSolverConfig.AutoSwitchOnAll)
                                {
                                    Svc.Log.Debug("Switching to Raphael solver");
                                    var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == "Raphael Recipe Solver".Loc());
                                    if (opt is not null)
                                    {
                                        var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                        config.SolverType = opt?.Def.GetType().FullName!;
                                        config.SolverFlavour = (int)(opt?.Flavour);
                                        P.Config.RecipeConfigs[craft.Recipe.RowId] = config;
                                    }
                                }
                                else if (crafts != null)
                                {
                                    Svc.Log.Debug($"Applying solver to {crafts.Count()} recipes.");
                                    var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == "Raphael Recipe Solver".Loc());
                                    if (opt is not null)
                                    {
                                        var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                        config.SolverType = opt?.Def.GetType().FullName!;
                                        config.SolverFlavour = (int)(opt?.Flavour);
                                        foreach (var c in crafts)
                                        {
                                            Svc.Log.Debug($"Switching {c.Recipe.RowId} ({c.Recipe.ItemResult.Value.Name}) to Raphael solver");
                                            P.Config.RecipeConfigs[c.Recipe.RowId] = config;
                                        }
                                    }
                                }
                            }
                            // 成功了就讓這組條件恢復成「可以再試」。
                            FailedSolves.TryRemove(failKey, out _);
                            P.Config.Save();
                            if (backload)
                                QueueBackgroundSolve(craft, key, macro, config);
                        });

                        cts.Token.ThrowIfCancellationRequested();
                        if (!solved)
                        {
                            Svc.Log.Error($"Raphael failed to generate a valid macro. This could be one of the following reasons:" +
                                $"\n- If you are not running Windows, Raphael may not be compatible with your OS." +
                                $"\n- You cancelled the generation." +
                                $"\n- Raphael just gave up after not finding a result.{(autoGenerateWas ? "\nAutomatic generation will be disabled as a result." : "")}");
                            FailedSolves[failKey] = "Raphael 沒有產生可用的巨集(求解器放棄了,或求解被取消)。";
                            cts.Cancel();
                            if (!fallbackMode)
                                AbortWaitingAutomation();
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 使用者按了取消、或逾時到了,不是故障 —— 清理照做,但不必再吵一次。
                        Svc.Log.Information($"Raphael 解算已取消:{key}");
                    }
                    catch (Exception ex)
                    {
                        // 🔴 例外從這裡逃出去 ＝ 後面的清理整段跳過,而且 Task 的例外沒有人觀察,
                        //    使用者唯一看得到的現象是「Artisan 從此不再開工」而且一句話都沒有。
                        Svc.Log.Error(ex, "Raphael solution generation threw");
                        DuoLog.Error("Raphael solution generation failed: ??".Loc(ex.Message));
                        if (fallbackMode)
                            FailedSolves[failKey] = ex.Message;
                        else
                            AbortWaitingAutomation();
                    }
                    finally
                    {
                        // 不論成功、失敗還是取消,這個 key 一定要離開 Tasks。
                        Tasks.TryRemove(key, out _);
                    }
                }, cts.Token, TaskCreationOptions.DenyChildAttach);

                Tasks.TryAdd(key, new(cts, task));
                // 先登記再讓路：背景工作在 BgLock 內看 Tasks 是否為空才開跑（見 RaphaelBackgroundSolve.cs）。
                PreemptBackground();
                try
                {
                    task.Start(TaskScheduler.Default);
                }
                catch (InvalidOperationException ex)
                {
                    // 登記與啟動之間 cts 就被取消掉的極端情況:工作已經是完成狀態,Start() 會丟例外。
                    Svc.Log.Information($"Raphael 解算工作在啟動前就被取消:{ex.Message}");
                    Tasks.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// 求解參數（<c>solve</c> 之後整串）。<paramref name="backload"/>＝實際有沒有帶 --backload-progress
        /// （品質需求很低時就算設定要後置也不帶）。<paramref name="threads"/> ≤ 0＝不限。<paramref name="quiet"/>＝背景求解，不寫說明行。
        /// </summary>
        private static string SolveArguments(CraftState craft, RaphaelSolutionConfig config, bool quiet, int threads, out bool backload)
        {
            var manipulation = craft.UnlockedManipulation ? "--manipulation" : "";
            var itemText = RecipeArgs(craft, out var explicitFallback);
            if (explicitFallback != null && !quiet)
                Svc.Log.Information($"Raphael:{explicitFallback}(配方 {craft.RecipeId}),改用 raphael-cli 內建配方表(--recipe-id)。");
            var extraArgsBuilder = new StringBuilder();

            extraArgsBuilder.Append($"--initial {craft.InitialQuality} "); // must always have a space after

            if (config.EnsureReliability)
            {
                if (!quiet)
                    Svc.Log.Error("Ensuring reliability is enabled, this may take a while. NO SUPPORT GIVEN IF ENABLED.");
                extraArgsBuilder.Append($"--adversarial "); // must always have a space after
            }

            backload = config.BackloadProgress;
            if (backload && ReflectFillsQuality(craft))
            {
                backload = false;
                if (!quiet)
                    Svc.Log.Information($"Raphael:配方 {craft.RecipeId} 第 1 步閒靜就能補滿品質（上限 {Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable)}），這次不後置進展，由 Raphael 自己選起手。");
            }
            if (backload)
            {
                extraArgsBuilder.Append($"--backload-progress "); // must always have a space after
            }

            if (config.HeartAndSoul)
            {
                extraArgsBuilder.Append($"--heart-and-soul "); // must always have a space after
            }

            if (config.QuickInno)
            {
                extraArgsBuilder.Append($"--quick-innovation "); // must always have a space after
            }

            if (threads > 0)
            {
                extraArgsBuilder.Append($"--threads {threads} "); // must always have a space after
            }

            return $"solve {itemText} {manipulation} --level {craft.StatLevel} --stats {craft.StatCraftsmanship} {craft.StatControl} {craft.StatCP} {extraArgsBuilder} --output-variables ids";
        }

        private static ProcessStartInfo CliStartInfo(string arguments) => new()
        {
            FileName = Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"),
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        /// <summary>raphael-cli 的 <c>--output-variables ids</c> 輸出 → 快取用的巨集（還沒配 ID）。</summary>
        private static MacroSolverSettings.Macro NewSolution(string key, CraftState craft, string output, bool backload)
        {
            var cleansedOutput = output.Replace("[", "").Replace("]", "").Replace("\"", "").Split(", ").Select(x => int.TryParse(x, out int n) ? n : 0);
            return new MacroSolverSettings.Macro()
            {
                Name = key,
                Steps = MacroUI.ParseMacro(cleansedOutput),
                Options = new()
                {
                    SkipQualityIfMet = false,
                    // 🔴 這兩個維持 false 是刻意的,
                    //    原因是 Raphael 的解把 CP 與耐久算得剛剛好,把某一步換成
                    //    集中加工/集中製作會改變消耗,整份計畫的預算就崩了。
                    //    要在好/高品質狀態撿便宜,正確做法是 OpportunisticSolver。
                    UpgradeProgressActions = false,
                    UpgradeQualityActions = false,
                    MinCP = craft.StatCP,
                    MinControl = craft.StatControl,
                    MinCraftsmanship = craft.StatCraftsmanship,
                    RaphaelNoBackload = !backload,
                }
            };
        }

        /// <summary>配 ID、寫進快取；<paramref name="replaces"/> 非 null 時連它在別的鍵下的那筆一起拿掉。只在框架執行緒呼叫。</summary>
        private static void StoreSolution(string key, MacroSolverSettings.Macro macro, MacroSolverSettings.Macro? replaces)
        {
            var rng = new Random();
            var ID = rng.Next(50001, 10000000);
            while (P.Config.RaphaelSolverCacheV3.Any(kv => kv.Value.ID == ID))
                ID = rng.Next(50001, 10000000);
            macro.ID = ID;
            P.Config.RaphaelSolverCacheV3[key] = macro;
            if (replaces != null)
                foreach (var old in P.Config.RaphaelSolverCacheV3)
                    if (old.Key != key && ReferenceEquals(old.Value, replaces))
                        P.Config.RaphaelSolverCacheV3.TryRemove(old.Key, out _);
        }

        /// <summary>
        /// raphael-cli 失敗時 stderr 不保證有兩行:它可能一個字都沒印,也可能只印一行。
        /// 固定取 Split(...)[1] 的話那兩種情況都會丟 IndexOutOfRangeException,而那個例外會從
        /// 解算工作裡逃出去、把後面的清理整段跳過,於是 Tasks 裡的 key 永遠移不掉、
        /// InProgressAny() 永遠是 true,製作就靜默凍結在那裡。
        /// </summary>
        private static string DescribeCliFailure(string error, int exitCode)
        {
            // 取第 2 行是刻意的:raphael-cli 的第 1 行通常是 "error: ..." 這種標題,真正的說明在下一行。
            // 有第 2 行就照舊,只有一行就用那一行,完全沒輸出至少把離開碼講出來。
            var lines = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 1)
                return lines[1];
            if (lines.Length == 1)
                return lines[0];
            return "raphael-cli exited with code ?? without printing an error message.".Loc(exitCode);
        }

        /// <summary>
        /// 解算失敗時的收尾。耐力模式在等 Raphael 的期間,Operations.RepeatActualCraft() 被
        /// InProgressAny() 擋著;而 Endurance 那個「連續五次開不了工就停機並說明原因」的退避,
        /// 本身也寫在 if (!RaphaelCache.InProgressAny()) 裡面 —— 兩邊被同一個條件同時關掉,
        /// 所以解算一旦失敗又沒有人把耐力模式關掉,呼叫端只會看到 Artisan 一直「忙」而永遠不動。
        /// 這裡主動把耐力模式關掉,讓呼叫端自己的守衛(例如 ICE 的 CraftProgressGuard,它是在
        /// 「Artisan 不忙了」時才會去檢查有沒有進展)看得到狀態改變而接手處理。
        /// </summary>
        private static void AbortWaitingAutomation()
        {
            if (Shutdown.IsCancellationRequested || !Endurance.Enable)
                return;

            // 這裡是解算工作的背景執行緒,而 ToggleEndurance 會動到 PreCrafting.Tasks 這種
            // 只有框架執行緒該碰的狀態,所以排回框架執行緒再做。
            Svc.Framework?.RunOnFrameworkThread(() =>
            {
                if (!Endurance.Enable)
                    return;

                DuoLog.Error("Raphael was unable to produce a solution - Endurance has been stopped.".Loc());
                Endurance.NotifyForcedStop("Raphael 求解失敗");
                Endurance.ToggleEndurance(false);
            });
        }

        public static string GetKey(CraftState craft)
        {
            return $"{craft.CraftLevel}/{craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}-{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}-{(craft.CraftExpert ? "Expert" : "Standard")}/{craft.InitialQuality}";
        }

        public static IEnumerable<CraftState> AllValidCrafts(string key, uint craftType)
        {
            var stats = KeyParts(key);
            // 宇宙配方(Number == 0)實際用哪列 RecipeLevelTable 由職業等級決定,不能拿表上那列的等級篩;
            // RecipeSheet 已排除 ItemResult 為 0 的佔位列,所以這裡 Number == 0 只會是宇宙配方。
            var recipes = LuminaSheets.RecipeSheet.Values.Where(x =>
                x.CraftType.RowId == craftType &&
                (x.Number == 0 || x.RecipeLevelTable.Value.ClassJobLevel == stats.Level));
            foreach (var recipe in recipes)
            {
                // 用 key 的等級重建:default 會在背景執行緒逐筆等主執行緒讀玩家等級,也未必是產生 key 時的等級。
                var characterStats = new CharacterStats { Level = stats.Level };
                var state = Crafting.BuildCraftStateForRecipe(characterStats, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
                if (stats.Prog == state.CraftProgress &&
                    stats.Qual == state.CraftQualityMax &&
                    stats.Dur == state.CraftDurability)
                    yield return state;
            }
        }

        // 解析結果是鍵字串的純函式，依鍵記住、不必作廢；解析擲例外的鍵不記，每次照舊擲。
        private static readonly ConcurrentDictionary<string, (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool Expert)> parsedKeys = new();
        private const int ParsedKeysLimit = 4096;

        public static (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool Expert) KeyParts(string key)
        {
            if (parsedKeys.TryGetValue(key, out var known))
                return known;

            var parsed = ParseKey(key);
            if (parsedKeys.Count >= ParsedKeysLimit)
                parsedKeys.Clear();
            return parsedKeys[key] = parsed;
        }

        private static (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool Expert) ParseKey(string key)
        {
            var parts = key.Split('/');

            int.TryParse(parts[0], out var lvl);
            int.TryParse(parts[1], out var prog);
            int.TryParse(parts[2], out var qual);
            int.TryParse(parts[3].Split('-')[0], out var dur);
            int.TryParse(parts[3].Split('-')[1], out var crafts);
            int.TryParse(parts[4], out var ctrl);
            int.TryParse(parts[5].Split('-')[0], out var cp);
            int.TryParse(parts[6], out var initial);
            // GetKey 產生的第 5 段是 "{CP}-{Expert|Standard}"。後半也要解析,
            // 否則「專家配方的解」跟「一般配方的解」在比對時完全無法區分。
            var expert = parts[5].Split('-').Length > 1 && parts[5].Split('-')[1] == "Expert";

            return (lvl, prog, qual, dur, initial, crafts, ctrl, cp, expert);
        }

        public static bool HasSolution(CraftState craft, out MacroSolverSettings.Macro? raphaelSolutionConfig)
        {
            // 每幀會被問好幾次，所以不排序、不另建陣列。答案與「依 Control 由高到低穩定排序後取第一筆相符」相同：
            // 相符的裡面 Control 最高者，同分取列舉順序在前的。每一筆都先解析鍵，壞鍵照舊讓整個呼叫擲例外。
            raphaelSolutionConfig = null;
            var bestControl = 0;
            foreach (var solution in P.Config.RaphaelSolverCacheV3)
            {
                var solKey = KeyParts(solution.Key);

                // 這裡每一筆都會走到（排序版找到就離開），所以空的值一併跳過，不讓它變成新的例外來源。
                if (solution.Value is not { Steps.Count: > 0 }) continue;

                // 耐久與 Expert 旗標也要比對:
                // 兩個配方的等級/進度/品質可以完全一樣而耐久不同(DurabilityFactor 不同就會這樣),
                // 那種情況下拿另一個耐久的解來跑,巨集會在中途把耐久用光,而且是靜默的。
                // Expert 同理:專家配方的狀態機完全不同,不能共用一般配方的解。
                if (solKey.Level == craft.CraftLevel &&
                    solKey.Prog == craft.CraftProgress &&
                    solKey.Qual == craft.CraftQualityMax &&
                    solKey.Dur == craft.CraftDurability &&
                    solKey.Expert == craft.CraftExpert &&
                    solKey.Crafts == craft.StatCraftsmanship &&
                    solKey.Control <= craft.StatControl &&
                    solKey.Initial == craft.InitialQuality &&
                    solKey.CP <= craft.StatCP &&
                    (craft.Specialist || !UsesSpecialistActions(solution.Value)) &&
                    (raphaelSolutionConfig == null || solKey.Control > bestControl))
                {
                    raphaelSolutionConfig = solution.Value;
                    bestControl = solKey.Control;
                }
            }
            return raphaelSolutionConfig != null;
        }

        /// <summary>
        /// 快取鍵不含「是不是專家」：別的職業求出的解可能用到專家專用技能，非專家拿去跑，序列會在那一步斷掉。
        /// </summary>
        internal static bool UsesSpecialistActions(MacroSolverSettings.Macro macro)
        {
            foreach (var step in macro.Steps)
                if (step is { Action: Skills.HeartAndSoul or Skills.QuickInnovation or Skills.CarefulObservation })
                    return true;
            return false;
        }

        /// <summary>
        /// HasSolution throws a cached solution away the moment a single stat dimension misses - craftsmanship
        /// has to be EXACTLY equal, while control/CP only have to be greater-or-equal - so swapping gear or
        /// letting food expire makes the Raphael option disappear and the craft quietly fall back to the standard solver.
        /// <para/>
        /// This finds the closest cache entry generated for this same recipe (level, progress, quality and
        /// durability all match) that was rejected on stats, and describes what disqualified it. Returns an empty
        /// string when nothing is cached for this recipe, so callers can tell "no solution exists" from "ignored".
        /// </summary>
        public static string DescribeIgnoredSolution(CraftState craft)
        {
            // 每幀路徑：先只用數字挑出距離最近的那一筆（同距離取列舉順序在前的），只替那一筆組說明文字。
            string? bestKey = null;
            var bestDistance = long.MaxValue;
            var bestSpecialistOnly = false;

            foreach (var solution in P.Config.RaphaelSolverCacheV3)
            {
                if (solution.Value.Steps.Count == 0) continue;

                var k = KeyParts(solution.Key);
                if (k.Level != craft.CraftLevel || k.Prog != craft.CraftProgress ||
                    k.Qual != craft.CraftQualityMax || k.Dur != craft.CraftDurability)
                    continue;

                var ignored = false;
                long distance = 0;

                if (k.Crafts != craft.StatCraftsmanship)
                {
                    ignored = true;
                    distance += Math.Abs(k.Crafts - craft.StatCraftsmanship);
                }
                if (k.Control > craft.StatControl)
                {
                    ignored = true;
                    distance += k.Control - craft.StatControl;
                }
                if (k.CP > craft.StatCP)
                {
                    ignored = true;
                    distance += k.CP - craft.StatCP;
                }
                if (k.Initial != craft.InitialQuality)
                {
                    ignored = true;
                    distance += Math.Abs(k.Initial - craft.InitialQuality);
                }
                if (k.Expert != craft.CraftExpert)
                {
                    // 沒有這條的話,「只有專家旗標不同」會被當成「其實沒有被忽略」,
                    // 使用者就會看到「有解但沒說為什麼」的空白提示。
                    ignored = true;
                    distance += 1;
                }
                var specialistOnly = !craft.Specialist && UsesSpecialistActions(solution.Value);
                if (specialistOnly)
                {
                    ignored = true;
                    distance += 1;
                }

                // Not ignored means HasSolution would have accepted it.
                if (!ignored) continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestKey = solution.Key;
                    bestSpecialistOnly = specialistOnly;
                }
            }

            if (bestKey == null)
                return "";
            var why = DescribeMismatch(KeyParts(bestKey), craft);
            if (!bestSpecialistOnly)
                return why;
            var specialist = "The cached solution uses specialist-only actions (Careful Observation, Heart and Soul, Quick Innovation), but this job is not a specialist.".Loc();
            return why.Length == 0 ? specialist : why + "\n" + specialist;
        }

        /// <summary><see cref="DescribeIgnoredSolution"/> 挑中的那一筆被拒的理由，一行一個；條件與上面逐項相同。</summary>
        private static string DescribeMismatch((int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP, bool Expert) k, CraftState craft)
        {
            var reasons = new List<string>();
            if (k.Crafts != craft.StatCraftsmanship)
                reasons.Add("Craftsmanship must match exactly: solution ??, you have ??".Loc(k.Crafts, craft.StatCraftsmanship));
            if (k.Control > craft.StatControl)
                reasons.Add("Control is ?? short: solution needs ??, you have ??".Loc(k.Control - craft.StatControl, k.Control, craft.StatControl));
            if (k.CP > craft.StatCP)
                reasons.Add("CP is ?? short: solution needs ??, you have ??".Loc(k.CP - craft.StatCP, k.CP, craft.StatCP));
            if (k.Initial != craft.InitialQuality)
                reasons.Add("Starting quality must match exactly: solution ??, this craft ??".Loc(k.Initial, craft.InitialQuality));
            if (k.Expert != craft.CraftExpert)
                reasons.Add(k.Expert
                    ? "The cached solution was generated for an expert recipe, but this one is not.".Loc()
                    : "The cached solution was generated for a standard recipe, but this one is expert.".Loc());
            return string.Join("\n", reasons);
        }

        /// <summary>
        /// 「指定的解算器現在不能用的時候,可不可以安靜地改用標準解算器」。
        /// 使用者的設定說可以,也還有一個問題要問:這場製作是不是別的外掛透過 IPC 叫起來的
        /// (ICE 的宇宙製作就是,見 IPC.CraftX 設 Endurance.IPCOverride)。那種情況沒有人在看
        /// 畫面,退回標準解算器會把金牌需要的品質默默做丟,而且從頭到尾一聲不響 —— 已知的
        /// 唯一徵兆是事後發現獎牌不對。IPC 驅動時一律當成不准降級,讓 CraftingProcessor 觸發
        /// SolverFailed 把「哪個解算器不能用、為什麼」講出來並停下,而不是硬做完。
        /// </summary>
        internal static bool FallbackToStandardAllowed
            => P.Config.RaphaelSolverConfig.AllowFallbackToStandard && !Endurance.IPCOverride;

        public static bool InProgress(CraftState craft) => Tasks.TryGetValue(GetKey(craft), out var _);

        public static bool InProgressAny() => Tasks.Any();

        /// <summary>
        /// 必要品質閘門用：條件與配方視窗的自動產生相同（設定開、沒有臨時解算器、專家配方看 GenerateOnExperts），
        /// 成立就送出求解。回傳之後是否有這一組在求解中。只在框架執行緒呼叫。
        /// </summary>
        internal static bool TryAutoBuild(CraftState craft, TimeSpan? fallbackDeadline = null, MacroSolverSettings.Macro? replaces = null)
        {
            var cfg = P.Config.RaphaelSolverConfig;
            var recipeCfg = P.Config.RecipeConfigs.GetValueOrDefault(craft.RecipeId);
            if (!cfg.AutoGenerate || (recipeCfg?.TempSolverType.Length ?? 0) > 0 || (craft.CraftExpert && !cfg.GenerateOnExperts) || !CLIExists())
                return false;

            var key = GetKey(craft);
            if (!TempConfigs.TryGetValue(key, out var solveCfg))
            {
                solveCfg = new()
                {
                    EnsureReliability = cfg.AllowEnsureReliability,
                    BackloadProgress = cfg.AllowBackloadProgress,
                    HeartAndSoul = cfg.ShowSpecialistSettings && craft.Specialist,
                    QuickInno = cfg.ShowSpecialistSettings && craft.Specialist,
                };
                TempConfigs[key] = solveCfg;
            }
            Build(craft, solveCfg, fallbackDeadline, replaces);
            return InProgress(craft);
        }

        // 組件位置在這次載入期間不變，只記路徑；檔案在不在照舊每次問（中途刪掉或補回都跟得上）。
        private static string? cliPath;

        internal static bool CLIExists()
        {
            return File.Exists(cliPath ??= Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"));
        }

        /// <summary>勾選框標籤（在地化文字＋<c>##鍵</c>）：文字與鍵都沒變就沿用上一次組好的字串。只在繪製執行緒用。</summary>
        private sealed class LabelCache(string suffix)
        {
            private string? loc, key, label;

            public string Get(string locText, string solutionKey)
            {
                if (!ReferenceEquals(locText, loc) || solutionKey != key)
                {
                    label = locText + $"##{solutionKey}{suffix}";
                    loc = locText;
                    key = solutionKey;
                }
                return label!;
            }
        }

        private static readonly LabelCache ReliabilityLabel = new("Reliability"), ProgressLabel = new("Progress"), HeartSoulLabel = new("HS"), QuickInnoLabel = new("QI");

        public static bool DrawRaphaelDropdown(CraftState craft, bool liveStats = true)
        {
            bool changed = false;
            var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.RecipeId) ?? new();
            if (CLIExists())
            {
                var hasSolution = HasSolution(craft, out var solution);
                var key = GetKey(craft);

                if (!TempConfigs.ContainsKey(key))
                {
                    TempConfigs.Add(key, new());
                    TempConfigs[key].EnsureReliability = P.Config.RaphaelSolverConfig.AllowEnsureReliability;
                    TempConfigs[key].BackloadProgress = P.Config.RaphaelSolverConfig.AllowBackloadProgress;
                    TempConfigs[key].HeartAndSoul = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                    TempConfigs[key].QuickInno = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                }

                if (hasSolution)
                {
                    // 與 FirstOrNull(名稱相符) 同一個答案（第一個相符的），只是不必每幀配置委派與閉包。
                    ISolverDefinition.Desc? opt = null;
                    var raphaelName = "Raphael Recipe Solver".Loc();
                    foreach (var d in CraftingProcessor.GetAvailableSolversForRecipe(craft, true))
                    {
                        if (d.Name == raphaelName)
                        {
                            opt = d;
                            break;
                        }
                    }
                    var solverIsRaph = config.SolverType == opt?.Def.GetType().FullName!;
                    // 重求成功會照自動切換改配方的解算器：指定了別的解算器的配方不碰。
                    if (liveStats && (solverIsRaph || config.CurrentSolverType.Length == 0) && !CosmicRaphaelGate.OwnsAutoBuild(config, craft))
                    {
                        if (!TryRefreshFreeOpener(craft, solution!, key))
                            TryRefreshReliability(craft, solution!, key);
                    }
                    var curStats = CharacterStats.GetCurrentStats();
                    //Svc.Log.Debug($"{curStats.Craftsmanship}/{craft.StatCraftsmanship} - {curStats.Control}/{craft.StatControl} - {curStats.CP}/{craft.StatCP}");
                    if (liveStats && craft.StatCraftsmanship != curStats.Craftsmanship && solverIsRaph)
                    {
                        var craftsmanshipError = curStats.Craftsmanship - craft.StatCraftsmanship > 0 ? "(Excess of ??) ".Loc(curStats.Craftsmanship - craft.StatCraftsmanship) : "";
                        ImGuiEx.Text(ImGuiColors.DalamudRed, "Your current Craftsmanship ??does not match the generated result.\nThis solver won't be used until they match due to possible early finishes.\n(You may just need to have the correct buffs applied)".Loc(craftsmanshipError));
                    }

                    if (!solverIsRaph)
                    {
                        if (liveStats)
                        {
                            ImGuiEx.TextCentered("Raphael Solution Has Been Generated. (Click to Switch)".Loc());
                            if (ImGui.IsItemClicked())
                            {
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                                changed = true;
                            }
                        }
                        else
                        {
                            ImGuiEx.TextCentered("Raphael Solution Has Been Generated.".Loc());
                        }
                    }
                }
                else
                {
                    // "A solution exists but is being ignored" has to be visible on the row itself - drawing
                    // nothing here is what made this look like "Raphael never solved this recipe". The row says
                    // THAT it is being ignored; the tooltip carries which stat is off and by how much.
                    var ignored = DescribeIgnoredSolution(craft);
                    if (ignored.Length > 0)
                    {
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, "A Raphael solution exists but does not match your current stats - not used.".Loc());
                        ImGuiEx.Tooltip(ignored + "\n\n" + "Rebuild the solution with your current gear, or restore the stats it was generated for.".Loc());
                    }

                    // 求解失敗過的話,那件事本身要在**列上**看得見 —— 否則使用者只會看到
                    // 「沒有 Raphael 解」,分不出「還沒解」與「解不出來而且已經不再重試」。
                    var failedReason = DescribeFailure(craft);
                    if (failedReason.Length > 0)
                    {
                        ImGuiEx.TextCentered(ImGuiColors.DalamudYellow, "Raphael 求解失敗,已停止自動重試。");
                        ImGuiEx.Tooltip(failedReason + "\n\n" + "按下方的按鈕會清掉這個結果並重新求解一次。");
                    }

                    // 臨時解算器生效中就不要自動產生 Raphael 解:那是別的外掛透過 IPC 指定的,
                    // 自動產生會在使用者沒看畫面時把設定檔的解算器解算起來、蓋掉臨時指定的意圖。
                    if (config.TempSolverType.Length == 0 && liveStats && P.Config.RaphaelSolverConfig.AutoGenerate && !CosmicRaphaelGate.OwnsAutoBuild(config, craft) && CraftingProcessor.GetAvailableSolversForRecipe(craft, true).Any())
                    {
                        if (!craft.CraftExpert || (craft.CraftExpert && P.Config.RaphaelSolverConfig.GenerateOnExperts))
                            Build(craft, TempConfigs[key]);
                    }
                }

                ImGui.Separator();
                // key 就是 GetKey(craft)：上面沒有任何一步會改 craft，不必再組一次。
                var inProgress = Tasks.ContainsKey(key);
                var raphChanges = false;

                if (inProgress)
                    ImGui.BeginDisabled();

                if (P.Config.RaphaelSolverConfig.AllowEnsureReliability)
                    raphChanges |= ImGui.Checkbox(ReliabilityLabel.Get("Ensure reliability".Loc(), key), ref TempConfigs[key].EnsureReliability);
                if (P.Config.RaphaelSolverConfig.AllowBackloadProgress)
                    raphChanges |= ImGui.Checkbox(ProgressLabel.Get("Backload progress".Loc(), key), ref TempConfigs[key].BackloadProgress);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox(HeartSoulLabel.Get("Allow heart and soul usage".Loc(), key), ref TempConfigs[key].HeartAndSoul);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox(QuickInnoLabel.Get("Allow quick innovation usage".Loc(), key), ref TempConfigs[key].QuickInno);

                changed |= raphChanges;

                if (inProgress)
                    ImGui.EndDisabled();

                if (!inProgress)
                {
                    if (ImGui.Button("Build Raphael Solution".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        // 使用者明確要求重新求解 —— 先清掉「已知失敗」,不然 Build 會直接短路。
                        FailedSolves.TryRemove(GetFailureKey(craft), out _);
                        Build(craft, TempConfigs[key]);
                    }
                }
                else
                {
                    if (ImGui.Button("Cancel Raphael Generation".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Tasks.TryRemove(key, out var task);
                        task.Item1.Cancel();
                    }
                }

                if (TempConfigs[key].EnsureReliability && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Ensuring quality is enabled, no support shall be provided when its enabled\nDue to problems that can be caused.".Loc());
                    ImGui.EndTooltip();
                }

                if (TempConfigs[key].HeartAndSoul || TempConfigs[key].QuickInno)
                {
                    ImGui.Text("Specialist actions are enabled, this can slow down the solver a lot.".Loc());
                }

                if (inProgress)
                {
                    ImGuiEx.TextCentered("Generating...".Loc());
                }
            }

            return changed;
        }
    }

    public class RaphaelSolverSettings
    {
        public bool AllowEnsureReliability = false;
        public bool ReliabilityFallback = true;
        public bool AllowBackloadProgress = false;
        public bool ProgressRescue = true;
        public bool LowQualityFreeOpener = true;
        public bool BackgroundNoBackload = true;
        public bool ShowSpecialistSettings = false;
        public bool ExactCraftsmanship = false;
        public bool AutoGenerate = false;
        public bool AutoSwitch = false;
        public bool AutoSwitchOnAll = false;
        public int MaximumThreads = 0;
        public bool GenerateOnExperts = false;
        public int TimeOutMins = 1;
        public bool OpportunisticDeviation = true;
        // Default true == the behaviour Artisan has always had: when the solver a recipe is assigned to
        // cannot produce a usable flavour (Raphael with no matching cached solution, a deleted macro, ...)
        // the craft silently runs on whatever solver has the highest priority, i.e. the standard solver.
        // Turning this off makes that case raise SolverFailed instead, so the craft stops and says why.
        public bool AllowFallbackToStandard = true;
        public bool CosmicUnassignedRaphael = true;
        public bool SkipQualityWhenMaxed = true;
        public bool OpportunisticReorder = true;

        public bool Draw()
        {
            bool changed = false;

            ImGui.Indent();
            ImGui.TextWrapped("Raphael settings can change the performance and system memory consumption. If you have low amounts of RAM try not to change settings, recommended minimum amount of RAM free is 2GB".Loc());

            ImGui.SliderInt("Maximum Threads".Loc(), ref MaximumThreads, 0, Environment.ProcessorCount);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                P.Config.Save();
            }
            ImGuiEx.TextWrapped("By default uses all it can, but on lower end machines you might need to use less cpu at the cost of speed. (0 = everything)".Loc());

            changed |= ImGui.Checkbox("Ensure 100% reliability in macro generation".Loc(), ref AllowEnsureReliability);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(new System.Numerics.Vector4(255, 0, 0, 1), "Ensuring reliability may not always work and is very CPU and RAM intensive, suggested RAM at least 16GB+ spare. NO SUPPORT SHALL BE GIVEN IF YOU HAVE THIS ON".Loc());
            ImGui.PopTextWrapPos();
            if (AllowEnsureReliability)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("On Cosmic Exploration recipes, when ensuring reliability can't reach max quality, use a solution without it if that gets higher quality".Loc(), ref ReliabilityFallback);
                ImGuiComponents.HelpMarker("Ensuring reliability plans for the worst case, so on recipes where max quality is out of reach it settles for clearly lower quality (on Cosmic Exploration Hard+ final items roughly 10% lower, and every 1% there is worth 30 points). When the reliable solution doesn't reach max quality, Raphael solves again without ensuring reliability and without backloading progress, and that solution is used if it reaches higher quality. Recipes where max quality is reachable, and all recipes outside Cosmic Exploration, keep the reliable solution. With automatic generation on, a cached solution that hasn't been checked this way is re-solved once (the old one stays in use until the new one is ready).".Loc());
                ImGui.Unindent();
            }
            changed |= ImGui.Checkbox("Allow backloading of progress in macro generation".Loc(), ref AllowBackloadProgress);
            if (AllowBackloadProgress)
            {
                ImGui.Indent();
                var reflect = Skills.Reflect.NameOfAction();
                changed |= ImGui.Checkbox("Don't backload progress when one ?? fills quality; let Raphael pick the opener (usually ??)".Loc(reflect, Skills.MuscleMemory.NameOfAction()), ref LowQualityFreeOpener);
                ImGuiComponents.HelpMarker("With backloading, every quality action comes before progress, so any craft that needs quality has to open with ??. When ?? on the first step already reaches max quality, that solve leaves backloading off and Raphael picks the opener itself. With automatic generation on, a cached solution that opens with ?? is re-solved once (the old one stays in use until the new one is ready); if Raphael still opens with ??, that solution is kept.".Loc(reflect, reflect, reflect, reflect));
                changed |= ImGui.Checkbox("Also solve without backloading in the background; use that solution if it gets higher quality, or the same quality faster".Loc(), ref BackgroundNoBackload);
                ImGuiComponents.HelpMarker("Crafting starts right away with the backloaded solution. Meanwhile Raphael solves the same recipe again without backloading in the background: one at a time, at most ?? threads, low priority, up to ?? minutes, and it steps aside (and retries later) whenever a solve you are waiting for starts. Later crafts of that recipe use the new solution if it reaches higher quality (even with more steps), or the same quality in fewer steps (or the same steps in less time); the craft in progress is not affected. Each solution is tried once per set of stats. Only runs with automatic generation on, and never on expert recipes.".Loc(RaphaelCache.BackgroundThreadCap, RaphaelCache.BackgroundTimeLimit.TotalMinutes));
                ImGui.Unindent();
            }
            changed |= ImGui.Checkbox("Show specialist options when available".Loc(), ref ShowSpecialistSettings);
            changed |= ImGui.Checkbox("Automatically generate a solution if a valid one hasn't been created.".Loc(), ref AutoGenerate);

            if (AutoGenerate)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("Generate on Expert Recipes".Loc(), ref GenerateOnExperts);
                ImGui.Unindent();
            }

            changed |= ImGui.Checkbox("Automatically switch to the Raphael Solver once a solution has been created.".Loc(), ref AutoSwitch);

            if (AutoSwitch)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("Apply to all valid crafts".Loc(), ref AutoSwitchOnAll);
                changed |= ImGui.Checkbox("Cosmic Exploration: use Raphael on recipes with no assigned solver".Loc(), ref CosmicUnassignedRaphael);
                ImGuiComponents.HelpMarker("Uses a Raphael solution that matches your current stats even if it was generated on another job. When another plugin (e.g. ICE) starts the craft and automatic generation is on, a solution is first generated with your current stats (waits up to ?? seconds). If that fails or times out, the craft starts on the usual solver and the log says why.".Loc(CosmicRaphaelGate.WaitLimit.TotalSeconds));
                ImGui.Unindent();
            }

            changed |= ImGui.Checkbox("依製作狀態機會性偏離解算結果", ref OpportunisticDeviation);
            ImGuiComponents.HelpMarker("Raphael 產生的是固定步驟,本身看不到「高品質／最高品質／低品質」。開啟後,遇到這些狀態時會先用模擬器把候選動作連同剩下的巨集整段跑完,只有在「仍然完成製作」且「最終品質確實更高」時才偏離,否則照原計畫走。不會動用能工巧匠圖紙。");

            ImGui.Indent();
            var good = CraftData.Condition.Good.ToLocalizedString();
            var excellent = CraftData.Condition.Excellent.ToLocalizedString();
            var poor = CraftData.Condition.Poor.ToLocalizedString();
            changed |= ImGui.Checkbox("Reorder plan steps around ?? / ?? / ?? conditions".Loc(good, excellent, poor), ref OpportunisticReorder);
            ImGuiComponents.HelpMarker("On ?? or ??, the next planned quality action is used first and the planned buff or repair moves one step later (?? is always followed by ??). On ??, the next planned non-quality action is used first and the touch moves to the next step. Only done when simulating the rest of the plan still finishes the craft with at least the same quality; on a tie the shorter craft wins, but only if it would still finish without skipping quality actions.".Loc(good, excellent, excellent, poor, poor));
            ImGui.Unindent();

            changed |= ImGui.Checkbox("If the rest of the plan can no longer finish progress, skip quality actions to finish the craft".Loc(), ref ProgressRescue);
            ImGuiComponents.HelpMarker("Before each quality action the rest of the plan is simulated in normal conditions (the tightest case for durability and CP). If it would run out of durability or CP before progress is done, but skipping the remaining quality actions would still finish, this quality action is skipped; it is re-checked every step, so only as many are skipped as needed. Trades some quality for not failing the craft. Nothing is skipped while the plan can still finish.".Loc());

            changed |= ImGui.Checkbox("Skip the remaining quality actions once quality is maxed".Loc(), ref SkipQualityWhenMaxed);
            ImGuiComponents.HelpMarker("When good conditions or deviations fill quality earlier than planned, the rest of the plan's touches, ??, ??, ?? and ?? are skipped and the craft goes straight on to progress. Repairs and progress buffs are kept. Saves steps and avoids spending Delineations on ??.".Loc(Skills.ByregotsBlessing.NameOfAction(), Skills.Innovation.NameOfAction(), Skills.GreatStrides.NameOfAction(), Skills.QuickInnovation.NameOfAction(), Skills.QuickInnovation.NameOfAction()));

            changed |= ImGui.Checkbox("Allow automatic fallback to the standard solver".Loc(), ref AllowFallbackToStandard);
            ImGuiComponents.HelpMarker("When the solver a recipe is assigned to cannot be used right now - Raphael with no cached solution matching your current stats, an assigned macro that was deleted - Artisan quietly starts the craft on the standard solver instead. Turn this off to make it stop and tell you which solver was unavailable rather than silently crafting with a different one.".Loc());

            changed |= ImGui.SliderInt("Timeout solution generation".Loc(), ref TimeOutMins, 1, 15);

            ImGuiComponents.HelpMarker("If a solution takes longer than this many minutes to generate, it will cancel the generation task.".Loc());

            if (ImGui.Button("Clear raphael macro cache (Currently ?? stored)".Loc(P.Config.RaphaelSolverCacheV3.Count)))
            {
                P.Config.RaphaelSolverCacheV3.Clear();
                // 連「這個 session 求解失敗過」的紀錄一起清掉,不然按了清除還是不會重新求解。
                RaphaelCache.FailedSolves.Clear();
                changed |= true;
            }

            ImGui.Unindent();
            return changed;
        }
    }

    public class RaphaelSolutionConfig
    {
        public bool EnsureReliability = false;
        public bool BackloadProgress = false;
        public bool HeartAndSoul = false;
        public bool QuickInno = false;
        public string Macro = string.Empty;

        public int MinCP = 0;
        public int MinControl = 0;
        public int ExactCraftsmanship = 0;

        [NonSerialized]
        public bool HasChanges = false;
    }
}
