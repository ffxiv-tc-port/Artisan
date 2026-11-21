using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic.Solvers
{
    // 本檔契約：背景求解不進 Tasks（InProgressAny 擋開工），一次一個；前景 Build 登記進 Tasks 後 PreemptBackground，
    // 「看 Tasks 是否為空再開跑」與「讓路」同在 BgLock 內 ⇒ 兩邊不同時跑。P.Config 只在框架執行緒讀寫；
    // 卸載時 Shutdown 連帶取消 linked 的 bgCts、砍掉子行程。結果都記在舊解的 RaphaelBackgroundTriedKey（同鍵只試一次）；
    // 被讓路不算試過，同一次載入讓路超過 BackgroundPreemptLimit 次就這次載入放棄。
    internal static partial class RaphaelCache
    {
        internal static readonly TimeSpan BackgroundTimeLimit = TimeSpan.FromMinutes(4);
        internal static readonly int BackgroundThreadCap = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        private const int BackgroundPreemptLimit = 3;

        private sealed class BackgroundJob(string key, CraftState craft, MacroSolverSettings.Macro old, string arguments, int threads)
        {
            public readonly string Key = key;
            public readonly CraftState Craft = craft;
            public readonly MacroSolverSettings.Macro Old = old;
            public readonly string Arguments = arguments;
            public readonly int Threads = threads;
            public int Preempted;
            public bool PreemptedNow;
        }

        private static readonly object BgLock = new();
        private static readonly LinkedList<BackgroundJob> BgQueue = new();
        private static readonly HashSet<string> BgSeen = new(StringComparer.Ordinal);
        private static BackgroundJob? bgRunning;
        private static CancellationTokenSource? bgCts;
        private static Task? bgWorker;
        private static readonly SemaphoreSlim BgSignal = new(0);

        /// <summary>
        /// 這份解值得在背景求一份不後置進展的解：是後置進展下求的（或不知道）、這個鍵還沒試過、這次求解本來就會帶 --backload-progress。
        /// <paramref name="key"/>＝<see cref="GetKey"/>(craft)：能力值變了鍵就變，會再試一次。
        /// </summary>
        internal static bool NeedsBackgroundSolve(CraftState craft, MacroSolverSettings.Macro solution, string key)
            => BackgroundAllowed(craft)
            && !solution.Options.RaphaelNoBackload
            && solution.Options.RaphaelBackgroundTriedKey != key
            && (TempConfigs.TryGetValue(key, out var c) ? c.BackloadProgress : P.Config.RaphaelSolverConfig.AllowBackloadProgress)
            && !ReflectFillsQuality(craft);

        // 專家配方兩種旗標都常求超過 3 分鐘（背景只會白跑到上限）；自動產生關著＝使用者不要自動求解。
        private static bool BackgroundAllowed(CraftState craft)
            => P.Config.RaphaelSolverConfig.BackgroundNoBackload && P.Config.RaphaelSolverConfig.AutoGenerate && !craft.CraftExpert;

        /// <summary>開工時呼叫（框架執行緒）：這場要用的 Raphael 解符合 <see cref="NeedsBackgroundSolve"/> 就排一次背景求解。</summary>
        internal static void TryQueueBackgroundSolve(CraftState craft)
        {
            if (!BackgroundAllowed(craft) || !HasSolution(craft, out var solution))
                return;
            var key = GetKey(craft);
            if (!NeedsBackgroundSolve(craft, solution!, key))
                return;
            var cfg = P.Config.RaphaelSolverConfig;
            var solveConfig = TempConfigs.GetValueOrDefault(key) ?? new()
            {
                EnsureReliability = cfg.AllowEnsureReliability,
                HeartAndSoul = cfg.ShowSpecialistSettings && craft.Specialist,
                QuickInno = cfg.ShowSpecialistSettings && craft.Specialist,
            };
            QueueBackgroundSolve(craft, key, solution!, solveConfig);
        }

        /// <summary>排一次背景求解（框架執行緒）：參數照 <paramref name="solveConfig"/>，只拿掉後置進展。同一個鍵每次載入只排一次。</summary>
        private static void QueueBackgroundSolve(CraftState craft, string key, MacroSolverSettings.Macro old, RaphaelSolutionConfig solveConfig)
        {
            if (!BackgroundAllowed(craft) || Shutdown.IsCancellationRequested || !CLIExists())
                return;
            var config = new RaphaelSolutionConfig
            {
                EnsureReliability = solveConfig.EnsureReliability,
                BackloadProgress = false,
                HeartAndSoul = solveConfig.HeartAndSoul,
                QuickInno = solveConfig.QuickInno,
            };
            var max = P.Config.RaphaelSolverConfig.MaximumThreads;
            var threads = max > 0 ? Math.Min(max, BackgroundThreadCap) : BackgroundThreadCap;
            // 參數在這裡（框架執行緒）組好：會讀設定與資料表。
            var arguments = SolveArguments(craft, config, true, threads, out _);
            lock (BgLock)
            {
                if (!BgSeen.Add(key))
                    return;
                BgQueue.AddLast(new BackgroundJob(key, craft, old, arguments, threads));
                bgWorker ??= Task.Run(BackgroundLoop);
            }
            BgSignal.Release();
        }

        /// <summary>前景求解要開始了：背景那一個先停（砍掉 raphael-cli），排回佇列最前面，等前景都做完再重來。</summary>
        private static void PreemptBackground()
        {
            lock (BgLock)
            {
                if (bgRunning is not { } job || bgCts is not { } cts || cts.IsCancellationRequested)
                    return;
                job.PreemptedNow = true;
                if (++job.Preempted <= BackgroundPreemptLimit)
                    BgQueue.AddFirst(job);
                cts.Cancel();
            }
        }

        private static void StopBackground()
        {
            lock (BgLock)
            {
                BgQueue.Clear();
                bgCts?.Cancel();
            }
        }

        private static async Task BackgroundLoop()
        {
            var token = Shutdown.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    BackgroundJob? job = null;
                    CancellationTokenSource? cts = null;
                    bool pending;
                    lock (BgLock)
                    {
                        pending = BgQueue.Count > 0;
                        if (pending && Tasks.IsEmpty)
                        {
                            job = BgQueue.First!.Value;
                            BgQueue.RemoveFirst();
                            job.PreemptedNow = false;
                            cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            bgRunning = job;
                            bgCts = cts;
                        }
                    }
                    if (job == null)
                    {
                        // 有排隊但前景在求解：輪詢等它做完（前景結束時沒有通知點）。
                        if (pending)
                            await Task.Delay(1000, token);
                        else
                            await BgSignal.WaitAsync(token);
                        continue;
                    }
                    try
                    {
                        await RunBackgroundJob(job, cts!);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex, $"Raphael 背景求解（配方 {job.Craft.RecipeId}）擲出例外，這一筆放棄");
                    }
                    finally
                    {
                        lock (BgLock)
                        {
                            bgRunning = null;
                            bgCts = null;
                        }
                        cts!.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (BgLock)
                    bgWorker = null;
            }
        }

        private static async Task RunBackgroundJob(BackgroundJob job, CancellationTokenSource cts)
        {
            var craft = job.Craft;
            cts.CancelAfter(BackgroundTimeLimit);
            using var process = new Process { StartInfo = CliStartInfo(job.Arguments) };
            if (cts.IsCancellationRequested)
                return; // 還沒開跑就被讓路（已排回佇列）或卸載
            process.Start();
            Svc.Log.Information($"Raphael 背景求解（配方 {craft.RecipeId}）：目前用的解是後置進展下求的，背景再求一份不後置進展的解"
                + $"（{job.Threads} 個執行緒、低優先權、最多 {BackgroundTimeLimit.TotalMinutes:0} 分鐘；正在做的這場照用原本的解）。");
            // 取消回呼在呼叫 Cancel 的執行緒上跑（計時器、框架執行緒）：不可擲例外。
            using (cts.Token.Register(() =>
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
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 已經結束了：沒有要降的。
                }
                var errorTask = process.StandardError.ReadToEndAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = (await errorTask).Trim();
                await process.WaitForExitAsync();

                if (Shutdown.IsCancellationRequested)
                    return;
                bool preempted;
                lock (BgLock)
                    preempted = job.PreemptedNow;
                if (preempted)
                {
                    Svc.Log.Information(job.Preempted <= BackgroundPreemptLimit
                        ? $"Raphael 背景求解（配方 {craft.RecipeId}）：有要等的求解開始了，背景先停下，之後重來。"
                        : $"Raphael 背景求解（配方 {craft.RecipeId}）：已經讓路 {job.Preempted} 次，這次載入不再試。");
                    return;
                }
                if (cts.IsCancellationRequested)
                {
                    MarkBackgroundTried(job, null, $"{BackgroundTimeLimit.TotalMinutes:0} 分鐘內沒算完，放棄，保留原本的解");
                    return;
                }
                if (process.ExitCode != 0)
                {
                    MarkBackgroundTried(job, null, $"求解失敗（{DescribeCliFailure(error, process.ExitCode)}），保留原本的解");
                    return;
                }
                var neu = NewSolution(job.Key, craft, output, false);
                if (neu.Steps.Count == 0)
                {
                    MarkBackgroundTried(job, null, "沒有產生可用的解，保留原本的解");
                    return;
                }
                var before = EvaluatePlan(craft, job.Old.Steps);
                var after = EvaluatePlan(craft, neu.Steps);
                var detail = $"步數 {before.Steps}→{after.Steps}、時間 {before.Seconds:0.#}→{after.Seconds:0.#} 秒、"
                    + $"品質 {Describe(before)}→{Describe(after)}（上限 {Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable)}）";
                if (PreferBackgroundSolution(before, after))
                    MarkBackgroundTried(job, neu, detail);
                else
                    MarkBackgroundTried(job, null, $"不後置進展的解沒有比較好，保留原本的解：{detail}");
            }
        }

        private static string Describe(PlanOutcome o) => o.Done ? o.Quality.ToString() : "做不完";

        /// <summary>在框架執行緒把結果寫回：<paramref name="replacement"/> 非 null 就換掉舊解（舊解仍在快取、這個鍵沒有別人寫過才換），並記「試過」、存檔。</summary>
        private static void MarkBackgroundTried(BackgroundJob job, MacroSolverSettings.Macro? replacement, string detail)
        {
            var replaced = false;
            try
            {
                ApplyOnFramework(() =>
                {
                    var cache = P.Config.RaphaelSolverCacheV3;
                    if (replacement != null && P.Config.RaphaelSolverConfig.BackgroundNoBackload
                        && cache.Values.Any(v => ReferenceEquals(v, job.Old))
                        && (!cache.TryGetValue(job.Key, out var current) || ReferenceEquals(current, job.Old)))
                    {
                        // 舊解在別的鍵（較低的能力值）下照留：那組能力值還用得到它。
                        StoreSolution(job.Key, replacement, null);
                        replaced = true;
                    }
                    job.Old.Options.RaphaelBackgroundTriedKey = job.Key;
                    P.Config.Save();
                });
            }
            catch (OperationCanceledException)
            {
                return; // 卸載中
            }
            if (replacement == null)
                Svc.Log.Information($"Raphael 背景求解（配方 {job.Craft.RecipeId}）：{detail}。");
            else if (replaced)
                Svc.Log.Information($"Raphael 背景求解（配方 {job.Craft.RecipeId}）：換成不後置進展的解，下一場起使用：{detail}。");
            else
                Svc.Log.Information($"Raphael 背景求解（配方 {job.Craft.RecipeId}）：新解比較好，但原本的解已被清掉或換掉（或設定已關），不換：{detail}。");
        }

        /// <summary>一份解從起始局面照順序走完（每步都是通常狀態、機率技能都成功）的結果；品質夾在送給 raphael-cli 的上限。</summary>
        internal readonly record struct PlanOutcome(bool Done, int Quality, int Steps, double Seconds);

        /// <summary>
        /// 用 Simulator 走一遍：舊快取解沒有 raphael-cli 回報的數字可比，兩份解一律用同一套模型量，也就是實機會跑的那套。
        /// 時間按動畫長短記 3／2 秒（同 raphael-cli 的 Duration），只用來在步數相同時分勝負。
        /// </summary>
        internal static PlanOutcome EvaluatePlan(CraftState craft, IReadOnlyList<MacroSolverSettings.MacroStep> steps)
        {
            var cap = Calculations.RecipeMaxQuality(craft.Recipe, craft.LevelTable);
            var step = Simulator.CreateInitial(craft, craft.InitialQuality);
            int n = 0;
            double seconds = 0;
            foreach (var s in steps)
            {
                if (Simulator.Status(craft, step) != Simulator.CraftStatus.InProgress)
                    break;
                var (res, next) = Simulator.Execute(craft, step, s.Action, 0f, 0f);
                if (res == Simulator.ExecuteResult.CantUse)
                    break;
                ++n;
                seconds += s.Action.ActionIsLengthyAnimation() ? 3 : 2;
                step = next with { Condition = CraftData.Condition.Normal };
            }
            var done = step.Progress >= craft.CraftProgress;
            return new(done, done ? Math.Min(step.Quality, cap) : 0, n, seconds);
        }

        /// <summary>
        /// 換解規則（品質優先）：新解要做得完，且品質（夾上限）嚴格較高，或品質相同而步數更少、或步數相同但時間更短。
        /// 舊解做不完就只要新解做得完。宇宙高難+ 每 1% 品質都算分，多一步換更高品質值得。
        /// </summary>
        internal static bool PreferBackgroundSolution(PlanOutcome old, PlanOutcome neu)
            => neu.Done && (!old.Done
                || neu.Quality > old.Quality
                || neu.Quality == old.Quality && (neu.Steps < old.Steps || neu.Steps == old.Steps && neu.Seconds < old.Seconds));
    }
}
