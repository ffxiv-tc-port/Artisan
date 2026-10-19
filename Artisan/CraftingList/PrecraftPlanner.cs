using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.CraftingLists
{
    // 本檔是純計算（不碰遊戲、Dalamud、Lumina），離線量測台直接編這一個檔。
    // 契約：持有量與配方都由呼叫端以委派提供；Plan 本身不讀任何全域狀態，同樣的輸入必得同樣的計畫。

    internal readonly record struct PrecraftIngredient(uint ItemId, int Amount);

    /// <summary>一個可以拿來補缺口的配方。<see cref="Quick"/>＝會用簡易製作做（成品必為 NQ、素材也只能用 NQ）。</summary>
    internal sealed record PrecraftRecipe(uint RecipeId, uint ResultItemId, int AmountResult, IReadOnlyList<PrecraftIngredient> Ingredients, bool Quick);

    internal readonly record struct PrecraftStep(uint RecipeId, uint ResultItemId, int Crafts, bool Quick);

    internal sealed class PrecraftPlan
    {
        /// <summary>依相依順序排好：每一步要用到的中間素材都在它前面。</summary>
        public List<PrecraftStep> Steps { get; } = new();

        /// <summary>這份計畫讓父配方能做的份數（1..要求的份數）。</summary>
        public int ParentCrafts { get; set; }
    }

    internal static class PrecraftPlanner
    {
        /// <summary>往下補的層數上限（父配方的直接素材＝第 1 層）。遊戲裡的中間素材鏈實際上不超過 4 層。</summary>
        public const int MaxDepth = 5;

        /// <param name="have">道具 → (NQ, HQ) 持有量。</param>
        /// <param name="recipeFor">(道具, 是否必須是 NQ) → 拿來做它的配方；做不了回 null。</param>
        /// <param name="blockingItem">回 null 時：做一份都補不起來的那個素材（0＝不明）。</param>
        /// <returns>null＝連一份都補不起來；否則是能做到最多份的計畫（份數＜要求時只補到那麼多）。</returns>
        public static PrecraftPlan? Plan(PrecraftRecipe parent, int wantedCrafts,
            Func<uint, (int Nq, int Hq)> have, Func<uint, bool, PrecraftRecipe?> recipeFor, out uint blockingItem)
        {
            blockingItem = 0;
            if (wantedCrafts < 1)
                return null;

            var haveCache = new Dictionary<uint, (int Nq, int Hq)>();
            (int Nq, int Hq) Have(uint id)
            {
                if (!haveCache.TryGetValue(id, out var v))
                    haveCache[id] = v = have(id);
                return v;
            }

            var recipeCache = new Dictionary<(uint, bool), PrecraftRecipe?>();
            PrecraftRecipe? RecipeFor(uint id, bool nq)
            {
                if (!recipeCache.TryGetValue((id, nq), out var r))
                    recipeCache[(id, nq)] = r = recipeFor(id, nq);
                return r;
            }

            // 份數越多需求越大（單調），所以二分找「最多能做幾份」。
            var first = TryPlan(parent, wantedCrafts, Have, RecipeFor, out var block);
            if (first != null)
                return first;
            var lastBlock = block;

            PrecraftPlan? best = null;
            int lo = 1, hi = wantedCrafts - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var p = TryPlan(parent, mid, Have, RecipeFor, out block);
                if (p != null)
                {
                    best = p;
                    lo = mid + 1;
                }
                else
                {
                    lastBlock = block;
                    hi = mid - 1;
                }
            }

            if (best == null)
                blockingItem = lastBlock;
            return best;
        }

        private static PrecraftPlan? TryPlan(PrecraftRecipe parent, int crafts,
            Func<uint, (int Nq, int Hq)> have, Func<uint, bool, PrecraftRecipe?> recipeFor, out uint blockingItem)
        {
            var state = new State(have, recipeFor);
            var path = new HashSet<uint> { parent.ResultItemId };
            foreach (var ing in parent.Ingredients)
            {
                if (ing.Amount <= 0 || ing.ItemId == 0)
                    continue;
                if (!state.Need(ing.ItemId, checked(ing.Amount * crafts), parent.Quick, 1, path))
                {
                    blockingItem = state.Blocking;
                    return null;
                }
            }

            blockingItem = 0;
            var ordered = state.Order();
            if (ordered == null)
            {
                blockingItem = parent.ResultItemId;
                return null;
            }

            var plan = new PrecraftPlan { ParentCrafts = crafts };
            plan.Steps.AddRange(ordered);
            return plan;
        }

        private sealed class State(Func<uint, (int Nq, int Hq)> have, Func<uint, bool, PrecraftRecipe?> recipeFor)
        {
            private readonly Dictionary<uint, (int Nq, int Hq)> avail = new();
            private readonly Dictionary<uint, PrecraftRecipe> recipes = new();
            private readonly Dictionary<uint, int> crafts = new();
            private readonly List<uint> firstSeen = new();
            public uint Blocking;

            private (int Nq, int Hq) Avail(uint id)
            {
                if (!avail.TryGetValue(id, out var v))
                    avail[id] = v = have(id);
                return v;
            }

            public bool Need(uint item, int qty, bool nqOnly, int depth, HashSet<uint> path)
            {
                var (nq, hq) = Avail(item);
                // 一般製作指派素材是先 NQ 後 HQ，這裡照同一順序扣。
                var fromNq = Math.Min(nq, qty);
                nq -= fromNq;
                var shortBy = qty - fromNq;
                if (!nqOnly)
                {
                    var fromHq = Math.Min(hq, shortBy);
                    hq -= fromHq;
                    shortBy -= fromHq;
                }
                avail[item] = (nq, hq);
                if (shortBy == 0)
                    return true;

                if (depth > MaxDepth || path.Contains(item))
                {
                    Blocking = item;
                    return false;
                }

                var r = recipeFor(item, nqOnly);
                if (r == null || r.AmountResult <= 0 || (nqOnly && !r.Quick))
                {
                    Blocking = item;
                    return false;
                }

                // 同一份計畫裡同一個配方只用一種做法：先決定的那種為準，後來要求不同做法就當做不到。
                if (recipes.TryGetValue(r.RecipeId, out var chosen) && chosen.Quick != r.Quick)
                {
                    Blocking = item;
                    return false;
                }
                recipes[r.RecipeId] = r;

                var n = (shortBy + r.AmountResult - 1) / r.AmountResult;
                path.Add(item);
                foreach (var ing in r.Ingredients)
                {
                    if (ing.Amount <= 0 || ing.ItemId == 0)
                        continue;
                    if (!Need(ing.ItemId, checked(ing.Amount * n), r.Quick, depth + 1, path))
                    {
                        path.Remove(item);
                        return false;
                    }
                }
                path.Remove(item);

                // 多做出來的留給後面的需求用；一般製作的成品品質不確定，只記進 HQ（不拿去滿足只收 NQ 的需求）。
                var surplus = n * r.AmountResult - shortBy;
                var (nq2, hq2) = Avail(item);
                avail[item] = r.Quick ? (nq2 + surplus, hq2) : (nq2, hq2 + surplus);

                if (!crafts.ContainsKey(r.RecipeId))
                {
                    crafts[r.RecipeId] = 0;
                    firstSeen.Add(r.RecipeId);
                }
                crafts[r.RecipeId] += n;
                return true;
            }

            /// <summary>依「產出 → 用到它的配方」排拓樸順序，同層照第一次出現的順序；有環回 null。</summary>
            public List<PrecraftStep>? Order()
            {
                var producers = new Dictionary<uint, uint>();
                foreach (var id in firstSeen)
                    producers[recipes[id].ResultItemId] = id;

                var indeg = firstSeen.ToDictionary(x => x, _ => 0);
                var edges = firstSeen.ToDictionary(x => x, _ => new List<uint>());
                foreach (var id in firstSeen)
                {
                    foreach (var ing in recipes[id].Ingredients.Select(x => x.ItemId).Distinct())
                    {
                        if (producers.TryGetValue(ing, out var p) && p != id)
                        {
                            edges[p].Add(id);
                            indeg[id]++;
                        }
                    }
                }

                var result = new List<PrecraftStep>();
                var done = new HashSet<uint>();
                while (result.Count < firstSeen.Count)
                {
                    var ready = firstSeen.Where(x => !done.Contains(x) && indeg[x] == 0).Take(1).ToList();
                    if (ready.Count == 0)
                        return null;
                    var next = ready[0];
                    done.Add(next);
                    var r = recipes[next];
                    result.Add(new PrecraftStep(next, r.ResultItemId, crafts[next], r.Quick));
                    foreach (var e in edges[next])
                        indeg[e]--;
                }
                return result;
            }
        }
    }
}
