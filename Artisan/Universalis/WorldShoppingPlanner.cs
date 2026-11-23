using System;
using System.Collections.Generic;

namespace Artisan.Universalis
{
    internal enum ShoppingPlanMode
    {
        /// <summary>盡量在越少世界買齊越好（使用者偏好：方便優先，小價差不在意）。</summary>
        FewestWorlds = 0,
        /// <summary>每件材料各自去最便宜的世界。</summary>
        CheapestEach = 1,
    }

    internal enum ShoppingAssignKind
    {
        /// <summary>沒有價格資料（還沒查、查失敗）。</summary>
        NoPrice = 0,
        World = 1,
        Npc = 2,
        /// <summary>有查到，但沒有任何一筆掛售。</summary>
        NoListing = 3,
    }

    internal sealed class ShoppingNeed
    {
        public uint ItemId;
        /// <summary>還要買的件數（&gt; 0）。</summary>
        public double Quantity;
        /// <summary>null＝沒有價格資料。</summary>
        public IReadOnlyList<Listing>? Listings;
        /// <summary>完整掛售清單；false＝降級端點，只知道一件的最低價與世界。</summary>
        public bool Exact;
        /// <summary>NPC 商店單價；0＝NPC 不賣。</summary>
        public uint NpcUnitPrice;
        /// <summary>呼叫端的 DP 表快取；規劃時可能換新，呼叫端自己寫回。</summary>
        public CheapestTable? Table;
    }

    /// <param name="CheapestWorld">各自最便宜時會去的世界（比價用；非市場時是空字串）。</param>
    /// <param name="CheapestCost">在 <paramref name="CheapestWorld"/> 的付款；非市場或不知道時等於 <paramref name="Cost"/>。</param>
    internal readonly record struct ShoppingAssignment(
        uint ItemId, ShoppingAssignKind Kind, string World, double Qty, double Cost, bool Full, bool Approximate,
        string CheapestWorld, double CheapestCost);

    /// <summary>
    /// 把要買的材料分到世界（純計算：不碰遊戲、Dalamud、設定；離線測試直接編這個檔）。
    /// 契約：只顯示用，結果與輸入順序無關；單一世界的買法一律用 <see cref="CheapestWorldSolver"/>。
    /// NPC 判準與材料表價格欄相同：完整清單、NPC 買齊比市場最佳世界便宜才算 NPC；市場沒有掛售而 NPC 有賣也算 NPC。
    /// </summary>
    internal static class WorldShoppingPlanner
    {
        /// <summary>「越少世界」模式下，某世界買這件的價格超過最便宜世界的這個倍數，就不算這個世界「買得齊」。</summary>
        /// <remarks>擋的是某個世界剛好只剩離譜高價的掛售；小價差照使用者偏好忽略。</remarks>
        internal const double MaxPremiumRatio = 2.0;

        public static ShoppingAssignment[] Plan(IReadOnlyList<ShoppingNeed> needs, ShoppingPlanMode mode)
        {
            var result = new ShoppingAssignment[needs.Count];
            var best = new CheapestWorldCost[needs.Count];
            var pending = new List<int>();
            var perWorld = new CheapestWorldCost[needs.Count][];

            for (var i = 0; i < needs.Count; i++)
            {
                var n = needs[i];
                if (n.Listings == null)
                {
                    result[i] = new(n.ItemId, ShoppingAssignKind.NoPrice, string.Empty, 0, 0, false, false, string.Empty, 0);
                    continue;
                }

                var b = n.Listings.Count > 0 ? CheapestWorldSolver.Solve(n.Listings, n.Quantity, ref n.Table, out _) : default;
                best[i] = b;
                var npcTotal = (double)n.NpcUnitPrice * n.Quantity;
                var hasMarket = b.Qty > 0;

                if (n.NpcUnitPrice > 0 && (!hasMarket || (n.Exact && npcTotal < b.Cost)))
                    result[i] = new(n.ItemId, ShoppingAssignKind.Npc, string.Empty, n.Quantity, npcTotal, true, false, string.Empty, npcTotal);
                else if (!hasMarket)
                    result[i] = new(n.ItemId, ShoppingAssignKind.NoListing, string.Empty, 0, 0, false, false, string.Empty, 0);
                else if (!n.Exact)
                {
                    // 降級資料只有「一件的最低價」：小計用單價×件數，標成約略。
                    var approx = b.Cost / b.Qty * n.Quantity;
                    result[i] = new(n.ItemId, ShoppingAssignKind.World, b.World, n.Quantity, approx, true, true, b.World, approx);
                }
                else if (mode == ShoppingPlanMode.CheapestEach)
                    result[i] = new(n.ItemId, ShoppingAssignKind.World, b.World, b.Qty, b.Cost, b.Qty >= n.Quantity, false, b.World, b.Cost);
                else
                {
                    perWorld[i] = CheapestWorldSolver.SolveEachWorld(n.Listings, n.Quantity, ref n.Table);
                    pending.Add(i);
                }
            }

            if (pending.Count > 0)
                AssignFewestWorlds(needs, pending, perWorld, best, result);
            return result;
        }

        // 貪婪集合覆蓋：每輪挑「買得齊的品項最多」的世界（同數取總價低、再同取名字序數小），直到沒有世界再買得齊任何一件。
        private static void AssignFewestWorlds(IReadOnlyList<ShoppingNeed> needs, List<int> pending, CheapestWorldCost[][] perWorld,
            CheapestWorldCost[] best, ShoppingAssignment[] result)
        {
            var assigned = new bool[needs.Count];
            var score = new Dictionary<string, (int Count, double Cost)>(StringComparer.Ordinal);
            while (true)
            {
                score.Clear();
                foreach (var i in pending)
                {
                    if (assigned[i])
                        continue;
                    var limit = best[i].Qty >= needs[i].Quantity ? best[i].Cost * MaxPremiumRatio : double.NaN;
                    foreach (var w in perWorld[i])
                    {
                        if (w.Qty >= needs[i].Quantity && w.Cost <= limit)
                        {
                            var s = score.GetValueOrDefault(w.World);
                            score[w.World] = (s.Count + 1, s.Cost + w.Cost);
                        }
                    }
                }
                if (score.Count == 0)
                    break;

                string? pick = null;
                (int Count, double Cost) top = default;
                foreach (var (world, s) in score)
                {
                    if (pick == null || s.Count > top.Count
                        || (s.Count == top.Count && (s.Cost < top.Cost || (s.Cost == top.Cost && string.CompareOrdinal(world, pick) < 0))))
                    {
                        pick = world;
                        top = s;
                    }
                }

                foreach (var i in pending)
                {
                    if (assigned[i])
                        continue;
                    var limit = best[i].Qty >= needs[i].Quantity ? best[i].Cost * MaxPremiumRatio : double.NaN;
                    foreach (var w in perWorld[i])
                    {
                        if (w.World == pick && w.Qty >= needs[i].Quantity && w.Cost <= limit)
                        {
                            result[i] = new(needs[i].ItemId, ShoppingAssignKind.World, w.World, w.Qty, w.Cost, true, false, best[i].World, best[i].Cost);
                            assigned[i] = true;
                            break;
                        }
                    }
                }
            }

            // 哪個世界都買不齊：照最便宜的買法（件數最多的那個世界）。
            foreach (var i in pending)
            {
                if (!assigned[i])
                    result[i] = new(needs[i].ItemId, ShoppingAssignKind.World, best[i].World, best[i].Qty, best[i].Cost, false, false, best[i].World, best[i].Cost);
            }
        }
    }
}
