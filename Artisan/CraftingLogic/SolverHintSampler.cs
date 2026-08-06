using Artisan.RawInformation.Character;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Artisan.CraftingLogic;

/// <summary>
/// 配方頁「這個配方做得起來嗎」提示背後的隨機模擬取樣器。
///
/// 為什麼需要它:原本那句提示只跑**一次決定性模擬**
/// (<see cref="SolverUtils.SimulateSolverExecution"/>,擲骰寫死成「動作必成功、之後永遠是通常狀態」),
/// 於是同時吃到一個樂觀假設與一個悲觀假設,兩邊都不對:離線量測 12 個專家配方 × 3 檔能力值
/// (~/.claude/tools/artisan-sim 的 b4hint)顯示品質平均低估 48.8 個百分點,36 格裡有 16 格
/// 提示說「做不完」但實測完成率超過 50%,最極端的一格提示說品質 6.7%、實測完成率 89.8%。
/// 真實的製作是隨機的,所以提示也要用分布來講。
///
/// 🔴 為什麼要快取與分幀:呼叫端(配方設定頁 / 清單編輯器)是**每一幀**都在呼叫的 ImGui 繪製碼。
/// 一次完整模擬要跑幾十次解算器決策,而 ExpertSolver 開了自適應前瞻時單次決策實測可以到 300 微秒
/// —— 一口氣把幾百次模擬跑完等於直接卡住遊戲執行緒。所以:
///   * 結果按「配方＋能力值＋解算器＋起始品質」快取,鍵沒變就不重算;
///   * 還沒取樣完的時候每幀只花固定的時間預算往前推進,分幾幀補完。
/// ⚠️ 刻意**不丟背景執行緒**:解算器那條路上會碰到讀遊戲記憶體的東西
///   (例如 <see cref="Simulator.CannotUseAction"/> 會去問身上的能工巧匠圖紙數量),
///   在非遊戲執行緒上碰那些是崩潰等級的風險,而這裡省下來的只是幾幀。
/// </summary>
public static class SolverHintSampler
{
    /// <summary>每一幀最多花在取樣上的毫秒數。</summary>
    private const double FrameBudgetMs = 1.5;

    /// <summary>單次模擬的步數上限 —— 解算器不收斂時當成失敗,不要把遊戲執行緒吊死。</summary>
    private const int StepCap = 250;

    /// <summary>同時最多記幾組結果(配方視窗與清單編輯器可能同時開著,不能互相把對方擠掉)。</summary>
    private const int MaxEntries = 8;

    public sealed class Result
    {
        public string Key = "";
        public int Target;
        public int Samples;
        public int Completed;
        public long LastUsed;

        /// <summary>做完的那幾次的品質達成率(0~100),已排序。</summary>
        public readonly List<double> CompletedQuality = new();
        /// <summary>收藏品/專家配方打到第幾個突破點的次數;index 0 = 一個都沒到。</summary>
        public readonly int[] TierCount = new int[4];

        public Random Rng = new(0);
        public bool Sorted;

        public bool Done => Samples >= Target;
        public double CompletionPct => Samples > 0 ? 100.0 * Completed / Samples : 0;

        /// <summary>做完的那幾次裡的品質分位數(0~1);一次都沒做完時回 -1。</summary>
        public double QualityQuantile(double q)
        {
            if (CompletedQuality.Count == 0)
                return -1;
            if (!Sorted)
            {
                CompletedQuality.Sort();
                Sorted = true;
            }
            var idx = (int)Math.Round(q * (CompletedQuality.Count - 1));
            return CompletedQuality[Math.Clamp(idx, 0, CompletedQuality.Count - 1)];
        }

        public double TierPct(int tier) => Samples > 0 ? 100.0 * TierCount[tier] / Samples : 0;
    }

    private static readonly Dictionary<string, Result> _entries = new();
    private static long _clock;

    /// <summary>
    /// 取得(必要時繼續推進)某一組設定的模擬分布。同一個 key 反覆呼叫不會重算。
    /// </summary>
    /// <param name="template">拿來 Clone 的解算器樣板 —— 每一次模擬都用自己的複本,不會動到它。</param>
    public static Result Sample(string key, Solver template, CraftState craft, int startingQuality, int target)
    {
        if (!_entries.TryGetValue(key, out var res))
        {
            if (_entries.Count >= MaxEntries)
            {
                // 丟掉最久沒被用到的那一組。呼叫端是 UI,同時活著的組數本來就個位數。
                var oldest = _entries.MinBy(x => x.Value.LastUsed).Key;
                _entries.Remove(oldest);
            }
            // 種子由 key 決定 → 同一個配方看到的數字固定,不會每次打開都跳一點。
            // ⚠️ 這裡不能用 string.GetHashCode():.NET Core 的字串雜湊**每個行程都不一樣**,
            //    那樣就只有「同一次遊戲期間」穩定,重開遊戲數字又變了。
            res = new Result { Key = key, Target = target, Rng = new Random(StableHash(key)) };
            _entries[key] = res;
        }
        res.LastUsed = ++_clock;

        if (!res.Done)
        {
            var sw = Stopwatch.StartNew();
            var ran = 0;
            do
            {
                RunOne(res, template, craft, startingQuality);
                ++ran;
                // 🔑 用「目前為止的平均」預測下一次要花多久,預測會超出預算就收手 ——
                //    跑完才發現超了等於預算沒有作用(單次模擬在最貴的設定下可以到數毫秒)。
                var elapsed = sw.Elapsed.TotalMilliseconds;
                if (elapsed + elapsed / ran > FrameBudgetMs)
                    break;
            }
            while (!res.Done);
        }

        return res;
    }

    /// <summary>跨行程穩定的字串雜湊(FNV-1a 32 位元)。</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)h;
        }
    }

    private static void RunOne(Result res, Solver template, CraftState craft, int startingQuality)
    {
        var solver = template.Clone();
        var step = Simulator.CreateInitial(craft, startingQuality);
        var guard = 0;
        while (Simulator.Status(craft, step) == Simulator.CraftStatus.InProgress)
        {
            if (++guard > StepCap)
                break; // 解算器不收斂:當成沒做完
            var action = solver.Solve(craft, step).Action;
            if (action == Skills.None)
                break;

            // 🔑 這裡跟舊的單次模擬唯一的差別:兩個擲骰都真的擲。
            //    舊的寫死 (0, 1) = 動作必成功、狀態永遠回到「通常」。
            var (exec, next) = Simulator.Execute(craft, step, action, res.Rng.NextSingle(), res.Rng.NextSingle());
            if (exec == Simulator.ExecuteResult.CantUse)
                break;
            step = next;
        }

        ++res.Samples;
        var completed = step.Progress >= craft.CraftProgress;
        if (completed)
        {
            ++res.Completed;
            res.CompletedQuality.Add(craft.CraftQualityMax > 0
                ? Math.Min(100.0, 100.0 * step.Quality / craft.CraftQualityMax)
                : 100.0);
            res.Sorted = false;
        }
        ++res.TierCount[completed ? Tier(craft, step) : 0];
    }

    /// <summary>
    /// 這一次做出來的東西打到第幾個突破點。
    /// ⚠️ 由高往低比,而且**門檻為 0 的那一段不算數** —— 可 HQ 的一般配方 Min1/Min2 就是 0,
    ///    先比低的會讓每一次完成都被記成滿分。
    /// </summary>
    private static int Tier(CraftState craft, StepState step)
    {
        if (craft.CraftQualityMin3 > 0 && step.Quality >= craft.CraftQualityMin3) return 3;
        if (craft.CraftQualityMin2 > 0 && step.Quality >= craft.CraftQualityMin2) return 2;
        if (craft.CraftQualityMin1 > 0 && step.Quality >= craft.CraftQualityMin1) return 1;
        return 0;
    }
}
