using System;
using System.Buffers;
using System.Collections.Generic;

namespace Artisan.Universalis
{
    /// <summary>在 <see cref="World"/> 買 <see cref="Qty"/> 件、共付 <see cref="Cost"/>。</summary>
    /// <remarks>掛售只能整筆買 ⇒ <see cref="Qty"/> 可能大於要求的數量；小於＝這個世界買不齊。</remarks>
    public readonly record struct CheapestWorldCost(string World, double Qty, double Cost);

    /// <summary>
    /// 一份掛售清單分好世界、算好 DP 的結果，讓同一份清單以不同數量反覆查詢時不必重算
    /// （成品庫存頁每幀以各成品的需求量問同一個材料）。契約：建好後不再改動，容量不夠時整份換新。
    /// </summary>
    internal sealed class CheapestTable
    {
        internal readonly IReadOnlyList<Listing> Source;
        internal readonly int SourceCount;
        /// <summary>依世界名序數排序。</summary>
        internal readonly WorldGroup[] Worlds;
        /// <summary>數量為正的掛售總筆數。</summary>
        internal readonly int ValidCount;
        /// <summary>整數數量的世界，需求件數 ≤ Cap 時可直接查表。</summary>
        internal readonly int Cap;

        internal CheapestTable(IReadOnlyList<Listing> source, int sourceCount, WorldGroup[] worlds, int validCount, int cap)
        {
            Source = source;
            SourceCount = sourceCount;
            Worlds = worlds;
            ValidCount = validCount;
            Cap = cap;
        }
    }

    internal sealed class WorldGroup
    {
        internal readonly string Name;
        internal readonly List<Listing> Items;
        internal readonly double Total, TotalCost;
        internal readonly bool Integral;
        /// <summary>Cost[q]／Qty[q]＝湊到 ≥ q 件的最少付款與那組的件數（同價取件數多）；null＝沒建表。</summary>
        internal readonly double[]? Cost;
        internal readonly int[]? Qty;

        internal WorldGroup(string name, List<Listing> items, double total, double totalCost, bool integral, double[]? cost, int[]? qty)
        {
            Name = name;
            Items = items;
            Total = total;
            TotalCost = totalCost;
            Integral = integral;
            Cost = cost;
            Qty = qty;
        }
    }

    /// <summary>
    /// 「去哪一個世界買最便宜」的純計算（不碰遊戲、Dalamud、設定；離線測試直接編這個檔）。
    /// 契約：結果只由掛售的內容決定，與清單順序、查詢順序無關；不改動輸入。
    /// </summary>
    internal static class CheapestWorldSolver
    {
        /// <summary>單次呼叫的 DP 格數上限（建表或單次計算擇一，不會兩者都做）＝最壞耗時的上界。</summary>
        internal const long CellBudget = 2_000_000;

        /// <summary>單一世界走 DP 的需求件數上限（DP 陣列長度上界）。</summary>
        internal const int MaxDpUnits = 65_536;

        public static CheapestWorldCost Solve(IReadOnlyList<Listing> listings, double quantity)
            => Solve(listings, quantity, out _);

        public static CheapestWorldCost Solve(IReadOnlyList<Listing> listings, double quantity, out int approximatedWorlds)
        {
            CheapestTable? table = null;
            return Solve(listings, quantity, ref table, out approximatedWorlds);
        }

        /// <param name="table">上次的表；不是這份清單的、或容量不夠時換成新的。</param>
        /// <param name="approximatedWorlds">超過預算、改用近似解的世界數（給測試用）。</param>
        public static CheapestWorldCost Solve(IReadOnlyList<Listing> listings, double quantity, ref CheapestTable? table, out int approximatedWorlds)
        {
            approximatedWorlds = 0;
            table = Prepare(listings, quantity, table, out var need);
            if (table.Worlds.Length == 0)
                return new(string.Empty, 0, 0);

            var budget = CellBudget;
            var found = false;
            CheapestWorldCost best = default;
            var bestFull = false;
            foreach (var w in table.Worlds)
            {
                var (qty, cost) = BestInWorld(w, quantity, need, ref budget, ref approximatedWorlds);
                var full = qty >= quantity;
                // 買得齊的優先、其中付最少（同價取件數多）；都買不齊時取件數最多（同量取付最少）。同分留名字排前面的。
                var better = !found
                    || (full != bestFull ? full
                        : full ? cost < best.Cost || (cost == best.Cost && qty > best.Qty)
                        : qty > best.Qty || (qty == best.Qty && cost < best.Cost));
                if (better)
                {
                    found = true;
                    bestFull = full;
                    best = new(w.Name, qty, cost);
                }
            }
            return best;
        }

        /// <summary>
        /// 每個世界各自買 <paramref name="quantity"/> 件的結果（與 <see cref="Solve"/> 同一套單一世界算法），依世界名序數排序。
        /// <see cref="Solve"/> 的答案就是從這一列裡照它的規則挑出來的那一個。
        /// </summary>
        public static CheapestWorldCost[] SolveEachWorld(IReadOnlyList<Listing> listings, double quantity, ref CheapestTable? table)
        {
            table = Prepare(listings, quantity, table, out var need);
            var budget = CellBudget;
            var approximated = 0;
            var result = new CheapestWorldCost[table.Worlds.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var w = table.Worlds[i];
                var (qty, cost) = BestInWorld(w, quantity, need, ref budget, ref approximated);
                result[i] = new(w.Name, qty, cost);
            }
            return result;
        }

        private static CheapestTable Prepare(IReadOnlyList<Listing> listings, double quantity, CheapestTable? table, out double need)
        {
            if (table == null || !ReferenceEquals(table.Source, listings) || table.SourceCount != listings.Count)
                table = Group(listings);
            need = quantity > 0 ? Math.Ceiling(quantity) : 0;
            if (table.Worlds.Length == 0)
                return table;
            var capMax = (int)Math.Min(MaxDpUnits, CellBudget / Math.Max(1, table.ValidCount) - 1);
            if (need > table.Cap && need <= capMax && NeedsTable(table, quantity, need))
                table = WithCap(table, (int)Math.Min(capMax, Math.Max(need, Math.Max(2L * table.Cap, 64))));
            return table;
        }

        // 每個世界都已能直接回答（買不齊或已有表）就不擴表：需求超過各世界總件數時建表是白做。
        private static bool NeedsTable(CheapestTable table, double quantity, double need)
        {
            foreach (var w in table.Worlds)
            {
                if (w.Integral && quantity < w.Total && (w.Cost == null || need >= w.Cost.Length))
                    return true;
            }
            return false;
        }

        private static CheapestTable Group(IReadOnlyList<Listing> listings)
        {
            var byWorld = new Dictionary<string, List<Listing>>(StringComparer.Ordinal);
            var valid = 0;
            foreach (var l in listings)
            {
                if (!byWorld.TryGetValue(l.World, out var list))
                    byWorld[l.World] = list = new List<Listing>();
                // 數量不是正數的掛售買了也不會多任何東西。
                if (l.Quantity > 0)
                {
                    list.Add(l);
                    valid++;
                }
            }

            var names = new List<string>(byWorld.Keys);
            names.Sort(StringComparer.Ordinal);
            var worlds = new WorldGroup[names.Count];
            for (var i = 0; i < worlds.Length; i++)
            {
                var items = byWorld[names[i]];
                double total = 0, totalCost = 0;
                var integral = true;
                foreach (var l in items)
                {
                    total += l.Quantity;
                    totalCost += l.TotalPrice;
                    integral &= l.Quantity == Math.Floor(l.Quantity);
                }
                worlds[i] = new WorldGroup(names[i], items, total, totalCost, integral, null, null);
            }
            return new CheapestTable(listings, listings.Count, worlds, valid, 0);
        }

        // 建表格數 ≤ ValidCount×(cap+1) ≤ CellBudget（cap ≤ capMax）。舊表已涵蓋的世界沿用。
        private static CheapestTable WithCap(CheapestTable old, int cap)
        {
            var worlds = new WorldGroup[old.Worlds.Length];
            for (var i = 0; i < worlds.Length; i++)
            {
                var w = old.Worlds[i];
                var c = (int)Math.Min(cap, w.Total);
                if (!w.Integral || (w.Cost != null && w.Cost.Length - 1 >= c))
                {
                    worlds[i] = w;
                    continue;
                }

                var cost = new double[c + 1];
                var qty = new int[c + 1];
                qty[c] = (int)RunDp(w.Items, c, cost);
                for (var q = c - 1; q >= 0; q--)
                {
                    if (cost[q] < cost[q + 1])
                        qty[q] = q;
                    else
                    {
                        cost[q] = cost[q + 1];
                        qty[q] = qty[q + 1];
                    }
                }
                worlds[i] = new WorldGroup(w.Name, w.Items, w.Total, w.TotalCost, w.Integral, cost, qty);
            }
            return new CheapestTable(old.Source, old.SourceCount, worlds, old.ValidCount, cap);
        }

        // 一個世界裡湊到 ≥ quantity 件、付款最少的掛售組合；買不齊時＝全部掛售。
        private static (double Qty, double Cost) BestInWorld(WorldGroup w, double quantity, double need, ref long budget, ref int approximated)
        {
            if (quantity <= 0)
                return (0, 0);
            if (w.Total <= quantity)
                return (w.Total, w.TotalCost);
            if (w.Cost != null && need < w.Cost.Length)
                return (w.Qty![(int)need], w.Cost[(int)need]);

            if (w.Integral && need <= MaxDpUnits)
            {
                var cells = (long)w.Items.Count * ((long)need + 1);
                if (cells <= budget)
                {
                    budget -= cells;
                    var dp = ArrayPool<double>.Shared.Rent((int)need + 1);
                    try
                    {
                        var q = RunDp(w.Items, (int)need, dp);
                        return (q, dp[(int)need]);
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(dp);
                    }
                }
            }

            approximated++;
            return CoverApprox(w.Items, quantity);
        }

        // 0/1 最少成本覆蓋：dp[j]＝剛好 j 件的最少付款，dp[cap]＝至少 cap 件；回傳 dp[cap] 那組的件數（同價取件數多）。
        private static double RunDp(List<Listing> ls, int cap, double[] dp)
        {
            dp[0] = 0;
            for (var j = 1; j <= cap; j++)
                dp[j] = double.PositiveInfinity;
            double capQty = 0;

            foreach (var l in ls)
            {
                var q = l.Quantity;
                var c = l.TotalPrice;
                var step = q >= cap ? cap : (int)q;

                // j 由大往小：讀到的 dp[j] 一定還是這筆掛售加入前的值（每筆只能買一次）。
                if (!double.IsPositiveInfinity(dp[cap]))
                    OfferCap(dp, cap, ref capQty, dp[cap] + c, capQty + q);
                for (var j = cap - 1; j >= 0; j--)
                {
                    var cur = dp[j];
                    if (double.IsPositiveInfinity(cur))
                        continue;
                    var t = j + step;
                    if (t >= cap)
                        OfferCap(dp, cap, ref capQty, cur + c, j + q);
                    else if (cur + c < dp[t])
                        dp[t] = cur + c;
                }
            }
            return capQty;
        }

        private static void OfferCap(double[] dp, int cap, ref double capQty, double cost, double qty)
        {
            if (cost < dp[cap] || (cost == dp[cap] && qty > capQty))
            {
                dp[cap] = cost;
                capQty = qty;
            }
        }

        // 超過預算時的近似（O(n log n)，不保證最少）：照單價排序，取「前 i 筆＋剩下的掛售裡單筆就補足缺額的最便宜那筆」
        // 對每個 i 的最佳者，再和「照單價買到夠、去掉拿掉也還夠的掛售」比。
        private static (double Qty, double Cost) CoverApprox(List<Listing> ls, double quantity)
        {
            var order = new List<Listing>(ls);
            order.Sort(static (a, b) =>
            {
                var r = (a.TotalPrice / a.Quantity).CompareTo(b.TotalPrice / b.Quantity);
                if (r == 0) r = a.TotalPrice.CompareTo(b.TotalPrice);
                if (r == 0) r = b.Quantity.CompareTo(a.Quantity);
                return r;
            });
            var n = order.Count;

            var pq = new double[n + 1];
            var pc = new double[n + 1];
            for (var i = 0; i < n; i++)
            {
                pq[i + 1] = pq[i] + order[i].Quantity;
                pc[i + 1] = pc[i] + order[i].TotalPrice;
            }
            var k = 1;
            while (k < n && pq[k] < quantity)
                k++;

            var chosen = order.GetRange(0, k);
            double qty = pq[k], cost = pc[k];
            chosen.Sort(static (a, b) =>
            {
                var r = b.TotalPrice.CompareTo(a.TotalPrice);
                return r != 0 ? r : a.Quantity.CompareTo(b.Quantity);
            });
            foreach (var l in chosen)
            {
                if (qty - l.Quantity >= quantity)
                {
                    qty -= l.Quantity;
                    cost -= l.TotalPrice;
                }
            }

            // 由後往前把掛售放進「依數量排名」的樹，查「數量 ≥ 缺額」裡最好的一筆（同價取件數多）。
            var sortedQty = new double[n];
            var rankOf = new int[n];
            var byQty = new int[n];
            for (var i = 0; i < n; i++)
                byQty[i] = i;
            Array.Sort(byQty, (a, b) => order[a].Quantity.CompareTo(order[b].Quantity));
            for (var r = 0; r < n; r++)
            {
                rankOf[byQty[r]] = r;
                sortedQty[r] = order[byQty[r]].Quantity;
            }
            var tree = new int[n + 1];
            Array.Fill(tree, -1);
            for (var i = n - 1; i >= 0; i--)
            {
                for (var p = n - rankOf[i]; p <= n; p += p & -p)
                    tree[p] = Better(order, i, tree[p]);
                if (i >= k)
                    continue;

                var lo = LowerBound(sortedQty, quantity - pq[i]);
                var best = -1;
                for (var p = n - lo; p > 0; p -= p & -p)
                    best = Better(order, tree[p], best);
                if (best < 0)
                    continue;
                var cq = pq[i] + order[best].Quantity;
                var cc = pc[i] + order[best].TotalPrice;
                if (cc < cost || (cc == cost && cq > qty))
                {
                    qty = cq;
                    cost = cc;
                }
            }
            return (qty, cost);
        }

        private static int Better(List<Listing> order, int a, int b)
        {
            if (a < 0) return b;
            if (b < 0) return a;
            var x = order[a];
            var y = order[b];
            return x.TotalPrice < y.TotalPrice || (x.TotalPrice == y.TotalPrice && x.Quantity > y.Quantity) ? a : b;
        }

        private static int LowerBound(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
