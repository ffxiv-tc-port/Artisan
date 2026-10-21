using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Artisan.CraftingLists
{
    /// <summary>
    /// 製作清單碰到「素材不夠」時，先把缺的、自己做得出來的中間素材插進清單目前位置再繼續。
    /// </summary>
    /// <remarks>
    /// 契約：全部只在遊戲主執行緒上呼叫（<see cref="CraftingListFunctions.ProcessList"/> 由視窗繪製每幀呼叫）。
    /// 插進來的項目走跟清單項目完全相同的流程（換職業、消耗品、解算器、簡易製作）；它們不在清單的
    /// Recipes 裡，消耗品與解算器因此用全域配方設定。插入的永遠是目前索引開始的一段連續區間。
    /// </remarks>
    internal static class ListPrecraft
    {
        /// <summary>同一個配方在一輪清單裡最多補幾次；第一次做壞（失敗沒產出）還有一次機會，之後照原本跳過。</summary>
        private const int MaxRoundsPerRecipe = 2;

        private static int regionEnd;
        private static uint regionParent;
        private static readonly Dictionary<uint, int> rounds = new();
        // 為了「只收 NQ 的簡易製作」而插入、本身不在清單裡的配方：照樣用簡易製作做。
        private static readonly HashSet<uint> quickRecipes = new();
        private static readonly ListItemOptions QuickOptions = new() { NQOnly = true };

        internal static void Reset()
        {
            regionEnd = 0;
            regionParent = 0;
            rounds.Clear();
            quickRecipes.Clear();
        }

        internal static bool IsPrecraftEntry(int index) => index < regionEnd;

        /// <summary>目前這段插入區間是為了哪個配方（最外層）；不在插入區間時回 0。</summary>
        internal static uint ParentOf(int index) => IsPrecraftEntry(index) ? regionParent : 0;

        /// <summary>清單項目自己的選項優先；插入、不在清單裡的配方回 null（＝一般製作）或簡易製作選項。</summary>
        internal static ListItemOptions? OptionsFor(NewCraftingList list, uint recipeId)
        {
            var item = list.Recipes.FirstOrDefault(x => x.ID == recipeId);
            if (item != null)
                return item.ListItemOptions;
            return quickRecipes.Contains(recipeId) ? QuickOptions : null;
        }

        /// <summary>從 <paramref name="index"/> 起、同一個配方連續幾項。</summary>
        internal static int ContiguousRun(NewCraftingList list, int index)
        {
            var l = list.ExpandedList;
            if (index < 0 || index >= l.Count)
                return 0;
            var id = l[index];
            var n = 0;
            while (index + n < l.Count && l[index + n] == id)
                n++;
            return n;
        }

        /// <summary>插入區間內、從 <paramref name="index"/> 起同一個配方還要做幾項（不越過區間尾）。</summary>
        internal static int PrecraftRun(NewCraftingList list, int index)
            => Math.Min(ContiguousRun(list, index), Math.Max(0, regionEnd - index));

        /// <summary>
        /// 試著為 <paramref name="parent"/> 補素材：成功＝已插入、回 true（呼叫端本幀直接 return，下一幀開始做第一項）；
        /// 回 false＝補不了，呼叫端照原本的「素材不足、跳過」處理。
        /// </summary>
        internal static bool TryInsert(NewCraftingList list, Recipe parent, bool parentQuick, int index)
        {
            try
            {
                return TryInsertCore(list, parent, parentQuick, index);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[Artisan][先做素材] 規劃 {parent.RowId} 的素材時出錯，照原本跳過");
                return false;
            }
        }

        private static bool TryInsertCore(NewCraftingList list, Recipe parent, bool parentQuick, int index)
        {
            var parentName = parent.RowId.NameOfRecipe();
            var used = rounds.GetValueOrDefault(parent.RowId);
            if (used >= MaxRoundsPerRecipe)
            {
                Svc.Log.Information($"[Artisan][先做素材] {parentName} 這一輪已經補過 {used} 次素材仍然不夠，照原本跳過。");
                return false;
            }

            var run = ContiguousRun(list, index);
            if (run < 1)
                return false;

            var parentBlocked = WhyParentBlocked(parent, CharacterInfo.JobID);
            if (parentBlocked != null)
            {
                Svc.Log.Information($"[Artisan][先做素材] {parentName} 缺素材，但素材補齊了也做不了（{parentBlocked}），不先做素材；照原本跳過。");
                return false;
            }

            var reasons = new Dictionary<uint, string>();
            var parentModel = ToModel(parent, parentQuick);
            var plan = PrecraftPlanner.Plan(parentModel, run, Have, (item, nq) => FindRecipe(list, item, nq, reasons), out var blocking);
            rounds[parent.RowId] = used + 1;

            if (plan == null || plan.Steps.Count == 0)
            {
                var why = blocking == 0 ? "" : $"{blocking.NameOfItem()}（{reasons.GetValueOrDefault(blocking, "背包裡的不夠，也沒有自己能做的配方")}）";
                Svc.Log.Information($"[Artisan][先做素材] {parentName} 缺素材，但沒辦法自己補齊：{why}；照原本跳過。");
                return false;
            }

            var entries = new List<uint>();
            foreach (var step in plan.Steps)
            {
                entries.AddRange(Enumerable.Repeat(step.RecipeId, step.Crafts));
                if (step.Quick && !list.Recipes.Any(x => x.ID == step.RecipeId))
                    quickRecipes.Add(step.RecipeId);
            }

            list.ExpandedList.InsertRange(index, entries);
            if (index < regionEnd)
            {
                regionEnd += entries.Count;
            }
            else
            {
                regionEnd = index + entries.Count;
                regionParent = parent.RowId;
            }

            var summary = string.Join("、", plan.Steps.Select(s => $"{s.RecipeId.NameOfRecipe()}×{s.Crafts}{(s.Quick ? "（簡易製作）" : "")}"));
            Svc.Log.Information($"[Artisan][先做素材] {parentName} 缺素材，先做：{summary}；補完可做 {plan.ParentCrafts}/{run} 份（第 {used + 1} 次）。");
            return true;
        }

        private static PrecraftRecipe ToModel(Recipe r, bool quick)
            => new(r.RowId, r.ItemResult.RowId, r.AmountResult,
                r.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId != 0)
                    .Select(x => new PrecraftIngredient(x.Item.RowId, x.Amount)).ToList(),
                quick);

        // 與 CheckForIngredients 同一種數法（含裝備中與裝備箱），只是 NQ／HQ 分開。
        private static unsafe (int Nq, int Hq) Have(uint itemId)
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
                return (0, 0);
            return (inv->GetInventoryItemCount(itemId), inv->GetInventoryItemCount(itemId, true));
        }

        private static PrecraftRecipe? FindRecipe(NewCraftingList list, uint itemId, bool nqOnly, Dictionary<uint, string> reasons)
        {
            var candidates = RecipesFor(itemId);
            if (candidates.Count == 0)
            {
                reasons[itemId] = "不是製作得出來的道具";
                return null;
            }

            var current = CharacterInfo.JobID;
            string? lastReason = null;
            // 清單裡已有的配方優先（沿用使用者對它的設定），再來是目前職業（少換一次套裝）。
            foreach (var r in candidates
                         .OrderByDescending(x => list.Recipes.Any(y => y.ID == x.RowId))
                         .ThenByDescending(x => (Job)(x.CraftType.RowId + 8) == current))
            {
                var why = WhyNotCraftable(r, current);
                if (why != null)
                {
                    lastReason = why;
                    continue;
                }

                var inList = list.Recipes.FirstOrDefault(x => x.ID == r.RowId);
                var canQuick = r.CanQuickSynth && P.ri.HasRecipeCrafted(r.RowId);
                bool quick;
                if (inList != null)
                    quick = (inList.ListItemOptions?.NQOnly ?? false) && canQuick;
                else
                    quick = nqOnly && canQuick;

                if (nqOnly && !quick)
                {
                    lastReason = inList != null ? "清單裡這個配方設定成一般製作，做出來不保證是 NQ，簡易製作用不了" : "這個配方還不能簡易製作，做出來不保證是 NQ";
                    continue;
                }

                return ToModel(r, quick);
            }

            reasons[itemId] = lastReason ?? "沒有能用的配方";
            return null;
        }

        private static unsafe string? WhyNotCraftable(Recipe r, Job current)
        {
            if (r.Number == 0)
                return "宇宙探索配方";
            if (r.IsSpecializationRequired)
                return "要專家才能做";

            var job = (Job)(r.CraftType.RowId + 8);
            // 比清單本身寬限 5 級更嚴：自動插進來的製作要能穩定做成，不然失敗會白白吃掉素材。
            var need = r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? byte.MaxValue;
            var lv = CharacterInfo.JobLevel(job);
            if (lv < need)
                return $"{job} 等級 {lv} 未達配方等級 {need}";

            if (r.SecretRecipeBook.RowId != 0)
            {
                var ps = PlayerState.Instance();
                if (ps == null || !ps->IsSecretRecipeBookUnlocked(r.SecretRecipeBook.RowId))
                    return "還沒讀過需要的秘笈";
            }

            return WhyCannotEquipOrSwitch(r, job, current);
        }

        /// <summary>
        /// 清單之後輪到父配方本身時會擋下的條件（換職業、裝備必要道具、等級寬限 5 級），提前判；
        /// 秘笈在呼叫 TryInsert 之前就判過了。
        /// </summary>
        private static string? WhyParentBlocked(Recipe parent, Job current)
        {
            var job = (Job)(parent.CraftType.RowId + 8);
            var need = parent.RecipeLevelTable.ValueNullable?.ClassJobLevel;
            if (need != null)
            {
                var lv = CharacterInfo.JobLevel(job);
                if (lv < need.Value - 5)
                    return $"{job} 等級 {lv} 比配方等級 {need.Value} 低超過 5 級";
            }
            return WhyCannotEquipOrSwitch(parent, job, current);
        }

        private static string? WhyCannotEquipOrSwitch(Recipe r, Job job, Job current)
        {
            if (r.ItemRequired.RowId != 0 && !PreCrafting.CanEquipItem(r.ItemRequired.RowId))
                return $"需要裝備 {r.ItemRequired.RowId.NameOfItem()}，但身上、背包、兵裝庫的主手與手部欄都沒有";

            if (job != current && !HasGearset(job))
                return $"沒有 {job} 的套裝";

            return null;
        }

        private static unsafe bool HasGearset(Job job)
        {
            var gearsets = RaptureGearsetModule.Instance();
            if (gearsets == null)
                return false;
            foreach (ref var gs in gearsets->Entries)
            {
                if ((Job)gs.ClassJob == job && gearsets->IsValidGearset(gs.Id))
                    return true;
            }
            return false;
        }

        private sealed class ByResult(Dictionary<uint, Recipe>? source, Dictionary<uint, List<Recipe>> map)
        {
            public readonly Dictionary<uint, Recipe>? Source = source;
            public readonly Dictionary<uint, List<Recipe>> Map = map;
        }

        private static ByResult? byResult;

        private static IReadOnlyList<Recipe> RecipesFor(uint itemId)
        {
            var sheet = LuminaSheets.RecipeSheet;
            if (sheet == null)
                return Array.Empty<Recipe>();
            var index = Volatile.Read(ref byResult);
            if (index == null || !ReferenceEquals(index.Source, sheet))
            {
                var map = new Dictionary<uint, List<Recipe>>();
                foreach (var r in sheet.Values)
                {
                    if (!map.TryGetValue(r.ItemResult.RowId, out var l))
                        map[r.ItemResult.RowId] = l = new List<Recipe>();
                    l.Add(r);
                }
                Volatile.Write(ref byResult, index = new ByResult(sheet, map));
            }
            return index.Map.TryGetValue(itemId, out var list) ? list : Array.Empty<Recipe>();
        }
    }
}
