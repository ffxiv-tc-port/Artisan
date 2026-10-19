using Artisan.CraftingLists;
using Artisan.RawInformation;
using Artisan.UI;
using Artisan.Universalis;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.IPC
{
    // 本檔契約：
    // - 兩支端點都是唯讀：不排 Universalis 查詢、不送封包、不改設定。價格只取已經有的（開著的清單編輯器材料表、或 TTL 內的快取）。
    // - 回傳型別的成員名是跨插件契約（消費端用同名鏡像型別經 JSON 接）；要改形狀就換新端點名。
    // - 永不回 null：找不到清單時回 Found=false 的空計畫。
    // - 整支在框架執行緒上跑（讀 P.Config 的裸 List、視窗清單、背包與僱員數量）。

    public sealed class CraftingListSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int RecipeCount { get; set; }
    }

    public sealed class ListShoppingPlan
    {
        public bool Found { get; set; }
        public int ListId { get; set; }
        public string ListName { get; set; } = string.Empty;

        /// <summary>Artisan 設定裡有沒有開 Universalis 查價；沒開時每一列都不會有價格。</summary>
        public bool UniversalisEnabled { get; set; }

        /// <summary>true＝查價範圍是單一世界（「限制在目前資料中心」那個選項），最佳世界只會是查價當時所在的世界。</summary>
        public bool PriceScopeSingleWorld { get; set; }

        public List<ShoppingPlanItem> Items { get; set; } = new();
    }

    public sealed class ShoppingPlanItem
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public int Required { get; set; }

        /// <summary>背包＋僱員（僱員數量要 AllaganTools）。</summary>
        public int Owned { get; set; }

        /// <summary>還缺多少；與清單編輯器「還需要」欄同一算式。只回 &gt; 0 的列。</summary>
        public int Remaining { get; set; }

        /// <summary>這件道具本身就由同一份清單上的配方製作。</summary>
        public bool CraftedOnList { get; set; }

        /// <summary>0＝沒有價格資料、1＝開著的清單編輯器、2＝Universalis 快取。</summary>
        public int PriceSource { get; set; }

        /// <summary>有價格資料而且至少一筆掛售。</summary>
        public bool HasListings { get; set; }

        /// <summary>買齊（或盡量買）<see cref="Remaining"/> 件最便宜的世界名稱；不知道時是空字串。</summary>
        public string OptimalWorld { get; set; } = string.Empty;

        /// <summary><see cref="OptimalWorld"/> 在 World 表的列號；名稱對不回列號時是 0。</summary>
        public uint OptimalWorldId { get; set; }

        /// <summary>在最佳世界買得到的件數（可能少於 <see cref="Remaining"/>）。</summary>
        public double OptimalQuantity { get; set; }

        /// <summary>在最佳世界買 <see cref="OptimalQuantity"/> 件的總價。</summary>
        public double OptimalCost { get; set; }

        /// <summary>價格來自 Universalis 的降級端點：只知道一件的最低價，總價是約略值。</summary>
        public bool Approximate { get; set; }

        /// <summary>NPC 商店單價；0＝沒有 NPC 用金幣賣。</summary>
        public uint NpcUnitPrice { get; set; }

        /// <summary>NPC 買齊比市場最佳世界便宜（與清單編輯器價格欄同一判準）。</summary>
        public bool NpcCheaper { get; set; }
    }

    internal static class ShoppingPlanIPC
    {
        internal const string SummariesEndpoint = "Artisan.GetCraftingListSummaries";
        internal const string PlanEndpoint = "Artisan.GetListShoppingPlan";

        internal const int PriceSourceNone = 0;
        internal const int PriceSourceListEditor = 1;
        internal const int PriceSourceCache = 2;

        private static Dictionary<string, List<(uint Id, uint Dc)>>? worldIdsByName;

        internal static void Init()
        {
            Svc.PluginInterface.GetIpcProvider<CraftingListSummary[]>(SummariesEndpoint).RegisterFunc(GetCraftingListSummaries);
            Svc.PluginInterface.GetIpcProvider<int, ListShoppingPlan>(PlanEndpoint).RegisterFunc(GetListShoppingPlan);
        }

        internal static void Dispose()
        {
            Svc.PluginInterface.GetIpcProvider<CraftingListSummary[]>(SummariesEndpoint).UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<int, ListShoppingPlan>(PlanEndpoint).UnregisterFunc();
        }

        private static CraftingListSummary[] GetCraftingListSummaries()
            => IpcFrameworkGate.Get(SummariesEndpoint, GetCraftingListSummariesCore, Array.Empty<CraftingListSummary>());

        private static CraftingListSummary[] GetCraftingListSummariesCore()
            => P.Config.NewCraftingLists
                .Select(x => new CraftingListSummary { Id = x.ID, Name = x.Name ?? string.Empty, RecipeCount = x.Recipes.Count })
                .ToArray();

        private static ListShoppingPlan GetListShoppingPlan(int listId)
            => IpcFrameworkGate.Get(PlanEndpoint, listId, static id => GetListShoppingPlanCore(id), new ListShoppingPlan { ListId = listId });

        private static ListShoppingPlan GetListShoppingPlanCore(int listId)
        {
            var plan = new ListShoppingPlan
            {
                ListId = listId,
                UniversalisEnabled = P.Config.UseUniversalis,
                PriceScopeSingleWorld = P.Config.LimitUnversalisToDC,
            };

            var list = P.Config.NewCraftingLists.FirstOrDefault(x => x.ID == listId);
            if (list == null)
                return plan;

            plan.Found = true;
            plan.ListName = list.Name ?? string.Empty;

            var craftedOnList = new HashSet<uint>();
            foreach (var recipe in list.Recipes)
            {
                if (LuminaSheets.RecipeSheet!.TryGetValue(recipe.ID, out var r))
                    craftedOnList.Add(r.ItemResult.RowId);
            }

            // 清單編輯器開著而且材料表建好了，就用畫面上那一份價格；否則只看快取。
            var editorItems = P.ws.Windows.OfType<ListEditor>()
                .FirstOrDefault(x => x.SelectedList.ID == listId && x.Table != null)?.Table?.ListItems;

            var currentWorldId = Svc.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0;
            foreach (var (itemId, required) in DirectMaterials(list).OrderBy(x => x.Key))
            {
                var owned = CraftingListUI.NumberOfIngredient(itemId) + RetainerInfo.GetRetainerItemCount(itemId);
                var remaining = Math.Max(0, required - owned);
                if (remaining == 0)
                    continue;

                var row = new ShoppingPlanItem
                {
                    ItemId = itemId,
                    ItemName = LuminaSheets.ItemSheet!.TryGetValue(itemId, out var item) ? item.Name.ToString() : string.Empty,
                    Required = required,
                    Owned = owned,
                    Remaining = remaining,
                    CraftedOnList = craftedOnList.Contains(itemId),
                };

                FillPrice(row, item, itemId, editorItems, currentWorldId);
                plan.Items.Add(row);
            }

            return plan;
        }

        // 與 ListMaterials() 同一份材料與數量；不呼叫它，因為它會寫 CraftingListHelpers 的共用靜態字典。
        private static Dictionary<uint, int> DirectMaterials(NewCraftingList list)
        {
            var output = new Dictionary<uint, int>();
            foreach (var li in list.Recipes)
            {
                if (li.ListItemOptions?.Skipping == true || li.Quantity == 0)
                    continue;
                if (!LuminaSheets.RecipeSheet!.TryGetValue(li.ID, out var recipe))
                    continue;

                var quantity = CraftingListHelpers.GetEffectiveCraftQuantity(recipe, list);
                foreach (var ing in recipe.Ingredients())
                {
                    if (ing.Amount <= 0 || ing.Item.RowId == 0)
                        continue;
                    output[ing.Item.RowId] = output.GetValueOrDefault(ing.Item.RowId) + ing.Amount * quantity;
                }
            }
            return output;
        }

        private static void FillPrice(ShoppingPlanItem row, Item item, uint itemId, List<Ingredient>? editorItems, uint currentWorldId)
        {
            MarketboardData? data = editorItems?.FirstOrDefault(x => x.Data.RowId == itemId)?.MarketboardData;
            if (data != null)
                row.PriceSource = PriceSourceListEditor;
            else if (P.UniversalsisClient.TryPeekCached(itemId, out data) && data != null)
                row.PriceSource = PriceSourceCache;
            else
                return;

            row.Approximate = data.Source == MarketboardSource.Aggregated;
            if (data.AllListings.Count > 0)
            {
                var cheapest = MarketboardPricing.GetCheapestWorldCost(data, row.Remaining);
                row.HasListings = true;
                row.OptimalWorld = cheapest.World ?? string.Empty;
                row.OptimalWorldId = ResolveWorldId(row.OptimalWorld, currentWorldId);
                row.OptimalQuantity = cheapest.Qty;
                row.OptimalCost = cheapest.Cost;
            }

            if (item.RowId != 0 && MarketboardPricing.TryGetNpcPrice(item, out var npc))
            {
                row.NpcUnitPrice = npc;
                row.NpcCheaper = data.IsUsableForComparison && (double)npc * row.Remaining < row.OptimalCost;
            }
        }

        // 世界名會撞名（台服的巴哈姆特與另一列同名）：唯一就用它；撞名時只認與目前所在世界同資料中心的那一列，認不出回 0。
        private static uint ResolveWorldId(string name, uint currentWorldId)
        {
            if (string.IsNullOrEmpty(name))
                return 0;

            worldIdsByName ??= BuildWorldIndex();
            if (!worldIdsByName.TryGetValue(name, out var candidates))
                return 0;
            if (candidates.Count == 1)
                return candidates[0].Id;

            var myDc = Svc.Data.GetExcelSheet<World>().TryGetRow(currentWorldId, out var me) ? me.DataCenter.RowId : 0;
            if (myDc == 0)
                return 0;
            var match = candidates.Where(x => x.Dc == myDc).ToList();
            return match.Count == 1 ? match[0].Id : 0;
        }

        private static Dictionary<string, List<(uint Id, uint Dc)>> BuildWorldIndex()
        {
            var map = new Dictionary<string, List<(uint Id, uint Dc)>>(StringComparer.Ordinal);
            foreach (var world in Svc.Data.GetExcelSheet<World>())
            {
                var name = world.Name.ToString();
                if (world.RowId == 0 || string.IsNullOrEmpty(name))
                    continue;
                if (!map.TryGetValue(name, out var list))
                    map[name] = list = new();
                list.Add((world.RowId, world.DataCenter.RowId));
            }
            return map;
        }
    }
}
