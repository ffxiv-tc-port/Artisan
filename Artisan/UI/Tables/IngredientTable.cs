using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.Universalis;
using Dalamud.Interface.Colors;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using OtterGui.Raii;
using OtterGui.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Artisan.UI.Tables
{
    internal class IngredientTable : Table<Ingredient>, IDisposable
    {
        private static float _nameColumnWidth = 0;
        private static float _requiredColumnWidth = 80;
        private static float _idColumnWidth = 80;
        private static float _inventoryColumnWidth = 80;
        private static float _retainerColumnWidth = 80;
        private static float _remainingColumnWidth = 100;
        private static float _canCraftColumnWidth = 100;
        private static float _craftableCountColumnWidth = 100;
        private static float _craftItemsColumnWidth = 0;
        private static float _itemCategoryColumnWidth = 0;
        private static float _gatherItemLocationColumWidth = 0;
        private static float _cheapestColumnWidth = 100;
        private static float _numberForSaleWidth = 100;

        public readonly IdColumn _idColumn;
        public readonly NameColumn _nameColumn;
        public readonly RequiredColumn _requiredColumn;
        public readonly InventoryCountColumn _inventoryColumn;
        public readonly RetainerCountColumn _retainerColumn;
        public readonly RemaingCountColumn _remainingColumn;
        public readonly CraftableColumn _craftableColumn;
        public readonly CraftableCountColumn _craftableCountColumn;
        public readonly CraftItemsColumn _craftItemsColumn;
        public readonly ItemCategoryColumn _itemCategoryColumn;
        public readonly GatherItemLocationColumn _gatherItemLocationColumn;
        public readonly CheapestServerColumn _cheapestServerColumn;
        public readonly NumberForSaleColumn _numberForSaleColumn;

        private static bool GatherBuddy =>
            DalamudReflector.TryGetDalamudPlugin("GatherbuddyReborn", out var _, false, true);

        private static bool ItemVendor =>
            DalamudReflector.TryGetDalamudPlugin("ItemVendorLocation", out var _, false, true);

        private static bool MonsterLookup =>
            DalamudReflector.TryGetDalamudPlugin("MonsterLootHunter", out var _, false, true);

        private static bool Marketboard =>
            DalamudReflector.TryGetDalamudPlugin("MarketBoardPlugin", out var _, false, true);

        // 價格欄裡查到市價的每一列每幀都讀它，而反射走訪插件清單一次約配置 1 KB ⇒ 最多每秒重查一次。
        // 只在繪製執行緒讀寫，不加鎖。
        private static long lifestreamNextCheck;
        private static bool lifestreamLoaded;
        private static bool Lifestream
        {
            get
            {
                var now = Environment.TickCount64;
                if (now >= lifestreamNextCheck)
                {
                    lifestreamNextCheck = now + 1000;
                    lifestreamLoaded = DalamudReflector.TryGetDalamudPlugin("Lifestream", out var _, false, true);
                }
                return lifestreamLoaded;
            }
        }

        // ItemFinderModule.Instance() 是 UIModule 的轉手,手寫成「uiModule == null ? null : ...」,
        // 沒登入時合法回 null。沒判就 ->SearchForItem 是解參考 null = AccessViolationException,
        // 攔不到。取不到就不搜尋(這是使用者按下去才會跑的路徑)。
        private static unsafe void SearchItem(uint item)
        {
            var itemFinder = ItemFinderModule.Instance();
            if (itemFinder != null)
                itemFinder->SearchForItem(item);
        }

        public List<Ingredient> ListItems;

        private bool CraftFiltered = false;
        private bool? isOnList = null;

        // OtterGui's Table<T>.FilteredItems is now `internal` (cross-assembly
        // inaccessible), so "show only ingredients used in this craft" can no
        // longer splice the base class' filtered list directly; instead drive
        // it through NameColumn's FilterFunc via this set (see ExtraFilter).
        private HashSet<Ingredient>? _craftFilterSet;

        // OtterGui's Table<T>.Headers is now assigned solely through the base
        // constructor (was a mutable field before, settable from the derived
        // constructor body); build every column up front in a static helper so
        // it can be handed to base(...) before `this` exists, then recover the
        // individual column instances via out-params for the fields below.
        private static Column<Ingredient>[] BuildHeaders(
            out IdColumn idColumn, out NameColumn nameColumn, out RequiredColumn requiredColumn,
            out InventoryCountColumn inventoryColumn, out RetainerCountColumn retainerColumn,
            out RemaingCountColumn remainingColumn, out CraftableColumn craftableColumn,
            out CraftableCountColumn craftableCountColumn, out CraftItemsColumn craftItemsColumn,
            out ItemCategoryColumn itemCategoryColumn, out GatherItemLocationColumn gatherItemLocationColumn,
            out CheapestServerColumn cheapestServerColumn, out NumberForSaleColumn numberForSaleColumn)
        {
            idColumn = new() { Label = "ID" };
            nameColumn = new() { Label = "Item Name".Loc() };
            requiredColumn = new() { Label = "Required".Loc() };
            inventoryColumn = new() { Label = "Inventory".Loc() };
            retainerColumn = new() { Label = "Retainers".Loc() };
            remainingColumn = new() { Label = "Remaining Needed".Loc() };
            craftableColumn = new() { Label = "Sources".Loc() };
            craftableCountColumn = new() { Label = "Number Craftable".Loc() };
            craftItemsColumn = new() { Label = "Used to Craft".Loc() };
            itemCategoryColumn = new() { Label = "Category".Loc() };
            gatherItemLocationColumn = new() { Label = "Gathered Zone".Loc() };
            cheapestServerColumn = new() { Label = "Optimal World For Buying".Loc() };
            numberForSaleColumn = new() { Label = "Quantity For Sale (All Worlds)".Loc() };

            if (P.Config.DefaultHideInventoryColumn) inventoryColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideRetainerColumn) retainerColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideRemainingColumn) remainingColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideCraftableColumn) craftableColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideCraftableCountColumn) craftableCountColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideCraftItemsColumn) craftItemsColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideCategoryColumn) itemCategoryColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideGatherLocationColumn) gatherItemLocationColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;
            if (P.Config.DefaultHideIdColumn) idColumn.Flags |= ImGuiTableColumnFlags.DefaultHide;

            List<Column<Ingredient>> headers = new() { nameColumn, requiredColumn, inventoryColumn, remainingColumn, craftableColumn, craftableCountColumn, craftItemsColumn, itemCategoryColumn, gatherItemLocationColumn, idColumn };
            if (RetainerInfo.ATools) headers.Insert(3, retainerColumn);
            if (P.Config.UseUniversalis)
            {
                headers.Insert(headers.Count - 1, cheapestServerColumn);
                headers.Insert(headers.Count - 1, numberForSaleColumn);
            }
            return headers.ToArray();
        }

        public IngredientTable(List<Ingredient> ingredientList)
            : base("IngredientTable", ingredientList,
                BuildHeaders(out var idColumn, out var nameColumn, out var requiredColumn, out var inventoryColumn,
                    out var retainerColumn, out var remainingColumn, out var craftableColumn, out var craftableCountColumn,
                    out var craftItemsColumn, out var itemCategoryColumn, out var gatherItemLocationColumn,
                    out var cheapestServerColumn, out var numberForSaleColumn))
        {
            _idColumn = idColumn;
            _nameColumn = nameColumn;
            _requiredColumn = requiredColumn;
            _inventoryColumn = inventoryColumn;
            _retainerColumn = retainerColumn;
            _remainingColumn = remainingColumn;
            _craftableColumn = craftableColumn;
            _craftableCountColumn = craftableCountColumn;
            _craftItemsColumn = craftItemsColumn;
            _itemCategoryColumn = itemCategoryColumn;
            _gatherItemLocationColumn = gatherItemLocationColumn;
            _cheapestServerColumn = cheapestServerColumn;
            _numberForSaleColumn = numberForSaleColumn;

            Sortable = true;
            ListItems = ingredientList;
            Flags |= ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable;

            _nameColumn.OnContextMenuRequest += OpenContextMenu;
            _nameColumn.ExtraFilter = item => _craftFilterSet is null || _craftFilterSet.Contains(item);
            _remainingColumn.SourceList = ListItems;

            foreach (var item in Items)
            {
                item.OnRemainingChange += SetFilterDirty;
            }
        }

        private void SetFilterDirty(object? sender, bool e)
        {
            foreach (var item in Items)
            {
                item.AmountUsedForSubcrafts = item.GetSubCraftCount();
            }
            this.FilterDirty = true;
        }

        public void Dispose()
        {
            _nameColumn.OnContextMenuRequest -= OpenContextMenu;

            foreach (var item in Items)
            {
                item.OnRemainingChange -= SetFilterDirty;
            }
        }

        public sealed class NameColumn : ColumnString<Ingredient>
        {
            // OtterGui's ColumnString<T> dropped the built-in OnContextMenuRequest
            // event / InvokeContextMenu helper when it migrated to
            // Dalamud.Bindings.ImGui; re-implement them locally.
            public event EventHandler<Ingredient>? OnContextMenuRequest;

            public void InvokeContextMenu(Ingredient e)
                => OnContextMenuRequest?.Invoke(this, e);

            // See _craftFilterSet on the owning IngredientTable: additional
            // predicate combined with the normal text-search filter below.
            public Func<Ingredient, bool>? ExtraFilter;

            public NameColumn()
               => Flags |= ImGuiTableColumnFlags.NoHide;

            public override string ToName(Ingredient item)
            {
                return item.ItemName;
            }

            public override bool FilterFunc(Ingredient item)
                => base.FilterFunc(item) && (ExtraFilter?.Invoke(item) ?? true);

            public bool ShowColour = false;
            public bool ShowHQOnly = false;

            public override float Width => _nameColumnWidth * ImGuiHelpers.GlobalScale;

            public override void DrawColumn(Ingredient item, int _)
            {
                if (ShowColour)
                {
                    int invAmount = ShowHQOnly && item.CanBeCrafted ? item.InventoryHQ : item.Inventory;
                    int retainerAmount = ShowHQOnly && item.CanBeCrafted ? item.ReainterCountHQ : item.RetainerCount;

                    if (item.CanBeCrafted && retainerAmount + invAmount + item.TotalCraftable >= item.Required)
                    {
                        var color = ImGuiColors.TankBlue;
                        color.W -= 0.6f;
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                    }

                    if (retainerAmount + invAmount >= item.Required)
                    {
                        var color = ImGuiColors.DalamudOrange;
                        color.W -= 0.6f;
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                    }

                    if (invAmount >= item.Required - (item.OriginList.SkipIfEnough && item.OriginList.SkipLiteral ? 0 : item.GetSubCraftCount()))
                    {
                        var color = ImGuiColors.HealerGreen;
                        color.W -= 0.3f;
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(color));
                    }
                }

                // Fetch the wrap fresh every frame: shared immediate texture wraps must not
                // be cached across frames - a cached wrap gets disposed once Dalamud evicts
                // the texture, and drawing it then throws ObjectDisposedException (fatal).
                if (P.Icons.TryLoadIcon(item.Data.Icon, out var icon))
                    ImGuiUtil.HoverIcon(icon, Interface.LineIconSize);
                ImGui.SameLine();

                var selected = ImGui.Selectable(item.ItemName);
                InvokeContextMenu(item);

                if (selected)
                {
                    ImGui.SetClipboardText(item.ItemName);
                    Notify.Success("Name copied to clipboard".Loc());
                }

                if (ImGui.IsItemHovered())
                {
                    StringBuilder sb = new();
                    foreach (var usedin in item.UsedInCrafts)
                    {
                        var recipe = LuminaSheets.RecipeSheet[usedin];
                        var amountUsed = recipe.Ingredients().FirstOrDefault(x => x.Item.RowId == item.Data.RowId).Amount * item.OriginList.Recipes.First(x => x.ID == recipe.RowId).Quantity;

                        sb.Append($"{usedin.NameOfRecipe()} - {amountUsed}\r\n");
                    }
                    ImGui.BeginTooltip();
                    ImGui.Text("Used in:".Loc() + $"\n{sb}");
                    ImGui.EndTooltip();
                }
            }
        }

        public sealed class RequiredColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _requiredColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.Required.CompareTo(rhs.Required);

            public override void DrawColumn(Ingredient item, int _)
            {
                ImGuiUtil.Center($"{ToName(item)}");
            }

            public override string ToName(Ingredient item)
            {
                return item.Required.ToString();
            }
        }

        public sealed class IdColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _idColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.Data.RowId.CompareTo(rhs.Data.RowId);

            public override string ToName(Ingredient item)
            {
                return item.Data.RowId.ToString();
            }
        }

        public sealed class InventoryCountColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _inventoryColumnWidth;

            public bool HQOnlyCrafts = false;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.Inventory.CompareTo(rhs.Inventory);

            public override void DrawColumn(Ingredient item, int _)
            {
                ImGuiUtil.Center($"{ToName(item)}");
            }

            public unsafe override string ToName(Ingredient item)
            {
                if (!HQOnlyCrafts || !item.CanBeCrafted)
                    return item.Inventory.ToString();

                int HQ = InventoryManager.Instance()->GetInventoryItemCount(item.Data.RowId, true, false, false);
                return HQ.ToString();
            }
        }

        public sealed class RetainerCountColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _retainerColumnWidth;

            public bool HQOnlyCrafts = false;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.RetainerCount.CompareTo(rhs.RetainerCount);

            public override void DrawColumn(Ingredient item, int _)
                => ImGuiUtil.Center($"{ToName(item)}");

            public override string ToName(Ingredient item)
            {
                if (!HQOnlyCrafts || !item.CanBeCrafted)
                    return item.RetainerCount.ToString();

                int retainerHQ = item.ReainterCountHQ;
                return retainerHQ.ToString();
            }
        }

        public sealed class CraftableCountColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _craftableCountColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.TotalCraftable.CompareTo(rhs.TotalCraftable);

            public override void DrawColumn(Ingredient item, int _)
                => ImGuiUtil.Center(ToName(item));


            public override string ToName(Ingredient item)
            {
                return item.Sources.Contains(1) ? item.TotalCraftable.ToString() : "N/A";
            }
        }

        public sealed class CraftItemsColumn : ColumnString<Ingredient>
        {
            public override float Width
                => _craftItemsColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.UsedInCrafts.First().CompareTo(rhs.UsedInCrafts.First());

            public override string ToName(Ingredient item)
            {
                return string.Join(", ", item.UsedInCrafts.Select(x => x.NameOfRecipe()));
            }

            public override void DrawColumn(Ingredient item, int _)
            {
                ImGui.Text(ToName(item));
            }

        }

        public sealed class CheapestServerColumn : ColumnString<Ingredient>
        {
            public override float Width => _cheapestColumnWidth;
            public Dictionary<uint, (string World, double Qty, double Cost)> CheapestListings = new();

            public static void RequestPrice(Ingredient item)
            {
                MarketboardFetch.Fetch(item.Data.RowId,
                    onFailed: () => item.MarketboardFetchFailed = true,
                    onComplete: data => item.MarketboardData = data);
            }

            // Fires off a price fetch for every row that still needs one - used by the
            // "一键全搜索" bulk button so the user doesn't have to click each row individually.
            public static void RequestAllPrices(IEnumerable<Ingredient> items)
            {
                foreach (var item in items.Where(x => x.MarketboardData == null && !x.MarketboardFetchFailed && x.Remaining > 0))
                {
                    RequestPrice(item);
                }
            }

            public override int Compare(Ingredient lhs, Ingredient rhs)
            {
                var lh = lhs.MarketboardData?.LowestWorld;
                var rh = rhs.MarketboardData?.LowestWorld;

                if (lh == null || rh == null)
                    return 0;

                return lh.CompareTo(rh);
            }

            public override string ToName(Ingredient item)
            {
                if (item.Remaining == 0) return "No need to buy".Loc();
                if (item.MarketboardData != null && !CheapestListings.ContainsKey(item.Data.RowId))
                {
                    var cheapest = MarketboardPricing.GetCheapestWorldCost(item.MarketboardData, item.Remaining);
                    CheapestListings.TryAdd(item.Data.RowId, (cheapest.World, cheapest.Qty, cheapest.Cost));
                    item.MarketboardData.LowestWorld = cheapest.World;
                }

                if (CheapestListings.ContainsKey(item.Data.RowId))
                {
                    var listing = CheapestListings[item.Data.RowId];

                    // 契約:市價不可比時勝負不落在這一格 —— 不比 NPC、只畫約略價。
                    //    NPC 那個精確價改由 DrawColumn 的 tooltip 講。
                    if ((item.MarketboardData?.IsUsableForComparison ?? false)
                        && MarketboardPricing.TryGetNpcPrice(item.Data, out var npcUnitPrice)
                        && (double)npcUnitPrice * item.Remaining < listing.Cost)
                        return "NPC Shop - Cost ??, Qty unlimited".Loc(npcUnitPrice.ToString("N0"));

                    var text = "?? - Cost ??, Qty ??".Loc(listing.World, listing.Cost.ToString("N0"), listing.Qty);

                    // 🔴 降級來源（aggregated 端點）只知道「最低單價」與「在哪個世界」，
                    //    算不出「買 N 件要多少錢」⇒ 必須在列上就看得出這是約略值。
                    //    把它畫成一個普通的價格會被當成精確報價。
                    if (item.MarketboardData?.Source == MarketboardSource.Aggregated)
                        text = "?? (approx.)".Loc(text);

                    return text;

                }

                return "ERROR - No Listings (Possible Universalis Connection Issue)".Loc();
            }

            public override void DrawColumn(Ingredient item, int _)
            {
                if (item.MarketboardData != null)
                {
                    ImGui.Text($"{ToName(item)}");

                    var degraded = item.MarketboardData.Source == MarketboardSource.Aggregated;
                    var canTravel = Lifestream && CheapestListings.ContainsKey(item.Data.RowId) && item.Remaining > 0;

                    // 「為什麼是約略」是起疑才查的資訊，所以放 tooltip；
                    // 「這是約略值」本身已經在列上（見 ToName）。
                    if ((degraded || canTravel) && ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        if (degraded)
                            ImGui.Text("Universalis timed out on the full listings, so this is the lowest unit price from its summary endpoint - the quantity shown is 1 unit, not the amount you need.".Loc());
                        if (degraded && MarketboardPricing.TryGetNpcPrice(item.Data, out var npcUnitPrice))
                            ImGui.Text("An NPC sells it for ?? gil each, and that price is exact.".Loc(npcUnitPrice.ToString("N0")));
                        if (canTravel)
                            ImGui.Text("Click to travel to ??.".Loc(CheapestListings[item.Data.RowId].World));
                        ImGui.EndTooltip();
                    }

                    if (canTravel && ImGui.IsItemClicked())
                    {
                        Chat.Instance.SendMessage($"/li {CheapestListings[item.Data.RowId].World} mb");
                    }
                }
                else if (P.Config.UniversalisOnDemand && P.Config.UseUniversalis)
                {
                    if (item.Remaining == 0)
                    {
                        ImGui.Text("No need to buy".Loc());
                        return;
                    }

                    if (item.MarketboardFetchFailed)
                    {
                        ImGui.Text("Unable to fetch prices".Loc());
                        return;
                    }

                    using var smallBtnStyle = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0));
                    if (ImGui.Button("Fetch Prices".Loc()))
                    {
                        CheapestServerColumn.RequestPrice(item);
                    }
                }
            }
        }

        public sealed class NumberForSaleColumn : ColumnString<Ingredient>
        {
            public override float Width => _numberForSaleWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
            {
                var lh = lhs.MarketboardData?.TotalQuantityOfUnits;
                var rh = rhs.MarketboardData?.TotalQuantityOfUnits;

                if (lh == null || rh == null)
                    return 0;

                return lh.Value.CompareTo(rh.Value);
            }

            public override string ToName(Ingredient item)
            {
                if (item.MarketboardData != null)
                {
                    var qty = item.MarketboardData.TotalQuantityOfUnits;
                    var listings = item.MarketboardData.TotalNumberOfListings;

                    // 🔴 「不知道」不可以畫成 0 —— 降級來源（aggregated 端點）根本沒有這兩個數字，
                    //    而 $"{null:N0}" 會產生空字串，看起來像「查到了，結果是空的」。
                    if (qty == null || listings == null)
                        return "?? listings - ?? total items".Loc("?", "?");

                    // 🔴 被 listings=N 截斷時，Universalis 回的那兩個數字算的是「回傳的那幾筆」，
                    //    不是市場真實總數 ⇒ 只能當下界講。
                    if (item.MarketboardData.ListingsTruncated)
                        return "?? listings - ?? total items".Loc($"{listings:N0}+", $"{qty:N0}+");

                    return "?? listings - ?? total items".Loc($"{listings:N0}", $"{qty:N0}");
                }
                return "";
            }

            public override void DrawColumn(Ingredient item, int _)
            {
                ImGui.Text($"{ToName(item)}");
            }
        }


        public sealed class GatherItemLocationColumn : ItemFilterColumn
        {
            public GatherItemLocationColumn()
            {
                Flags -= ImGuiTableColumnFlags.NoResize;
                SetFlags(ItemFilter.GatherZone, ItemFilter.NoGatherZone, ItemFilter.TimedNode, ItemFilter.NonTimedNode);
                SetNames("Gather Zone".Loc(), "No Gather Zone".Loc(), "Timed Node".Loc(), "Non-Timed Node".Loc());

            }
            public override float Width
                => _gatherItemLocationColumWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.GatherZoneName.CompareTo(rhs.GatherZoneName);

            public override void DrawColumn(Ingredient item, int idx)
            {
                ImGui.Text(item.GatherZoneName);
            }

            public override bool FilterFunc(Ingredient item)
            {
                bool zone = item.GatherZone.RowId switch
                {
                    1 => FilterValue.HasFlag(ItemFilter.NoGatherZone),
                    _ => FilterValue.HasFlag(ItemFilter.GatherZone)
                };

                bool timed = item.TimedNode switch
                {
                    true => FilterValue.HasFlag(ItemFilter.TimedNode),
                    false => FilterValue.HasFlag(ItemFilter.NonTimedNode)
                };

                return zone & timed;
            }
        }

        public sealed class ItemCategoryColumn : ItemFilterColumn
        {
            public ItemCategoryColumn()
            {
                Flags -= ImGuiTableColumnFlags.NoResize;
                SetFlags(ItemFilter.NonCrystals, ItemFilter.Crystals);
                SetNames("Non-Crystals".Loc(), "Crystals".Loc());
            }


            public override float Width
                => _itemCategoryColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.Category.CompareTo(rhs.Category);

            public override void DrawColumn(Ingredient item, int idx)
            {
                ImGui.Text(item.CategoryName);
            }

            public override bool FilterFunc(Ingredient item)
            {
                return item.Category switch
                {
                    58 => FilterValue.HasFlag(ItemFilter.Crystals),
                    _ => FilterValue.HasFlag(ItemFilter.NonCrystals)
                };
            }
        }

        public class ItemFilterColumn : ColumnFlags<ItemFilter, Ingredient>
        {
            private ItemFilter[] FlagValues = Array.Empty<ItemFilter>();
            private string[] FlagNames = Array.Empty<string>();

            protected void SetFlags(params ItemFilter[] flags)
            {
                FlagValues = flags;
                AllFlags = FlagValues.Aggregate((f, g) => f | g);
            }

            protected void SetFlagsAndNames(params ItemFilter[] flags)
            {
                SetFlags(flags);
                SetNames(flags.Select(f => f.ToString()).ToArray());
            }

            protected void SetNames(params string[] names)
                => FlagNames = names;

            protected sealed override IReadOnlyList<ItemFilter> Values
                => FlagValues;

            protected sealed override string[] Names
                => FlagNames;

            public sealed override ItemFilter FilterValue
                => P.Config.ShowItemsV1;

            protected sealed override void SetValue(ItemFilter f, bool v)
            {
                var tmp = v ? FilterValue | f : FilterValue & ~f;
                if (tmp == FilterValue)
                    return;

                P.Config.ShowItemsV1 = tmp;
                P.Config.Save();
            }
        }

        public sealed class RemaingCountColumn : ItemFilterColumn
        {
            public RemaingCountColumn()
            {
                Flags -= ImGuiTableColumnFlags.NoResize;
                SetFlags(ItemFilter.MissingItems, ItemFilter.NoMissingItems);
                SetNames("Missing Items".Loc(), "No Missing Items".Loc());
            }

            public override float Width
                => _remainingColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => lhs.Remaining.CompareTo(rhs.Remaining);

            public List<Ingredient> SourceList = new();

            public override void DrawColumn(Ingredient item, int idx)
            {
                ImGuiUtil.Center($"{item.Remaining}");

                if (!(item.OriginList.SkipIfEnough && item.OriginList.SkipLiteral) && ImGui.IsItemHovered())
                {
                    StringBuilder sb = new StringBuilder();
                    if (item.UsedInMaterialsListCount.Count > 0)
                    {
                        foreach (var i in item.UsedInMaterialsListCount.Where(x => x.Value > 0))
                        {
                            var owned = RetainerInfo.GetRetainerItemCount(LuminaSheets.RecipeSheet[i.Key].ItemResult.RowId) + CraftingListUI.NumberOfIngredient(LuminaSheets.RecipeSheet[i.Key].ItemResult.RowId);
                            if (SourceList.FindFirst(x => x.CraftedRecipe.RowId == i.Key, out var ingredient))
                            {
                                sb.AppendLine("?? less is required due to having ????x ??".Loc(i.Value, owned > ingredient.Required ? "at least ".Loc() : "", Math.Min(ingredient.Required, owned), i.Key.NameOfRecipe()));
                            }
                        }
                    }

                    if (item.SubSubMaterials.Count > 0)
                    {
                        foreach (var i in item.SubSubMaterials)
                        {
                            if (item.UsedInMaterialsListCount.ContainsKey(i.Key))
                                continue;

                            sb.AppendLine($"{i.Value.Sum(x => x.Item2)} less is required for {i.Key.NameOfRecipe()}");
                            foreach (var m in i.Value)
                            {
                                var owned = RetainerInfo.GetRetainerItemCount(LuminaSheets.RecipeSheet[m.Item1].ItemResult.RowId) + CraftingListUI.NumberOfIngredient(LuminaSheets.RecipeSheet[m.Item1].ItemResult.RowId);
                                if (SourceList.FindFirst(x => x.CraftedRecipe.RowId == m.Item1, out var ingredient))
                                {
                                    sb.AppendLine($"└ {m.Item1.NameOfRecipe()} uses {i.Key.NameOfRecipe()}, you have {(owned > ingredient.Required ? "at least " : "")}{Math.Min(ingredient.Required, owned)} {m.Item1.NameOfRecipe()} so {m.Item2}x {item.Data.Name} less is required as a result.");
                                }
                            }
                        }
                    }

                    ImGuiUtil.HoverTooltip(sb.ToString().Trim());
                }

            }

            public override bool FilterFunc(Ingredient item)
            {
                return item.Remaining switch
                {
                    0 => FilterValue.HasFlag(ItemFilter.NoMissingItems),
                    _ => FilterValue.HasFlag(ItemFilter.MissingItems)
                };
            }
        }

        public sealed class CraftableColumn : ItemFilterColumn
        {
            public CraftableColumn()
            {
                Flags -= ImGuiTableColumnFlags.NoResize;
                SetFlags(ItemFilter.Crafted, ItemFilter.Gathered, ItemFilter.Fishing, ItemFilter.Vendor, ItemFilter.MonsterDrop, ItemFilter.Unknown);
                SetNames("Crafted".Loc(), "Gathered".Loc(), "Fishing".Loc(), "Vendor".Loc(), "Monster Drop".Loc(), "Unknown".Loc());
            }


            public override float Width
                => _canCraftColumnWidth;

            public override int Compare(Ingredient lhs, Ingredient rhs)
                => string.Join(", ", lhs.Sources).CompareTo(string.Join(", ", rhs.Sources));

            public override void DrawColumn(Ingredient item, int idx)
            {
                List<string> outputs = new();

                if (item.Sources.Contains(1)) outputs.Add("Crafted".Loc());
                if (item.Sources.Contains(2)) outputs.Add("Gathered".Loc());
                if (item.Sources.Contains(3)) outputs.Add("Fishing".Loc());
                if (item.Sources.Contains(4)) outputs.Add("Vendor".Loc());
                if (item.Sources.Contains(5)) outputs.Add("Monster Drop".Loc());
                if (item.Sources.Contains(-1)) outputs.Add("Unknown".Loc());

                ImGui.Text($"{string.Join(", ", outputs)}");
            }

            public override bool FilterFunc(Ingredient item)
            {
                if (item.Sources.Contains(1) && FilterValue.HasFlag(ItemFilter.Crafted)) return true;
                if (item.Sources.Contains(2) && FilterValue.HasFlag(ItemFilter.Gathered)) return true;
                if (item.Sources.Contains(3) && FilterValue.HasFlag(ItemFilter.Fishing)) return true;
                if (item.Sources.Contains(4) && FilterValue.HasFlag(ItemFilter.Vendor)) return true;
                if (item.Sources.Contains(5) && FilterValue.HasFlag(ItemFilter.MonsterDrop)) return true;
                if (item.Sources.Contains(-1) && FilterValue.HasFlag(ItemFilter.Unknown)) return true;


                return false;
            }
        }

        private void OpenContextMenu(object? sender, Ingredient item)
        {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup(item.Data.RowId.ToString());

            using var popup = ImRaii.Popup(item.Data.RowId.ToString());
            if (!popup)
                return;

            DrawGatherItem(item);
            DrawAddToAutoGather(item);
            DrawSearchItem(item.Data.RowId, item.Data.Name.ToString());
            DrawItemVendorLookup(item.Data.RowId, item.Data.Name.ToString());
            DrawMonsterLootLookup(item.Data.RowId, item.Data.Name.ToString());
            DrawMarketBoardLookup(item.Data.RowId, item.Data.Name.ToDalamudString().ToString());
            DrawFilterOnCrafts(item);
            DrawRestockFromRetainer(item);
            //DrawCraftThisItem(item);
        }

        internal static void DrawMarketBoardLookup(uint itemId, string itemName)
        {

            if (itemId == 0)
                return;

            if (Marketboard)
            {
                if (ImGui.Selectable("Market Board Lookup".Loc()))
                {
                    Chat.Instance.SendMessage($"/pmb {itemName}");
                }
            }
        }

        private void DrawRestockFromRetainer(Ingredient item)
        {
            if (item.Data.RowId == 0 || item.RetainerCount == 0 || item.Required <= item.Inventory)
                return;

            if (RetainerInfo.GetReachableRetainerBell() == null)
            {
                ImGui.TextDisabled("Fetch From Retainer (please stand by a bell)".Loc());
            }
            else
            {
                if (RetainerInfo.TM.IsBusy)
                {
                    ImGui.TextDisabled("Currently fetching. Please wait.".Loc());
                    return;
                }

                if (!ImGui.Selectable("Fetch From Retainer".Loc()))
                    return;

                var howManyToGet = item.Required - item.Inventory;
                if (howManyToGet > 0)
                {
                    RetainerInfo.RestockFromRetainers(item.Data.RowId, howManyToGet);
                }
            }
        }

        private void DrawFilterOnCrafts(Ingredient item)
        {
            if (item.Data.RowId == 0)
                return;

            if (!CraftFiltered || Headers.Any(x => x.FilterFunc(item)))
            {
                if (isOnList == null)
                {
                    isOnList = item.OriginList.Recipes.Any(x => LuminaSheets.RecipeSheet.Values.Any(y => y.ItemResult.RowId == item.Data.RowId && y.RowId == x.ID));
                }

                if (item.Sources.Contains(1) && isOnList.Value)
                {
                    if (ImGui.Selectable("Show ingredients used for this".Loc()))
                    {
                        var craftFilterSet = new HashSet<Ingredient> { item };
                        foreach (var ingredient in CraftingListHelpers.GetIngredientRecipe(item.Data.RowId).Value.Ingredients().Where(x => x.Amount > 0))
                        {
                            if (Items.FindFirst(x => x.Data.RowId == ingredient.Item.RowId, out var result))
                                craftFilterSet.Add(result);
                        }

                        _craftFilterSet = craftFilterSet;
                        CraftFiltered = true;
                        FilterDirty = true;
                    }
                }
            }

            if (CraftFiltered)
            {
                if (!ImGui.Selectable("Clear Filters".Loc()))
                    return;

                CraftFiltered = false;
                _craftFilterSet = null;
                FilterDirty = true;

            }
        }

        internal static void DrawMonsterLootLookup(uint itemId, string itemName)
        {
            if (itemId == 0)
                return;

            if (MonsterLookup)
            {
                if (!ImGui.Selectable("Monster Loot Lookup".Loc()))
                    return;

                try
                {
                    Chat.Instance.SendMessage($"/mloot {itemName}");
                }
                catch (Exception e)
                {
                    e.Log();
                }
            }
            else
            {
                ImGui.TextDisabled("Monster Loot Lookup (Please install Monster Loot Hunter)".Loc());
            }
        }

        internal static void DrawItemVendorLookup(uint itemId, string itemName)
        {
            if (itemId == 0)
                return;

            if (ItemVendor)
            {
                if (ItemVendorLocation.ItemHasVendor(itemId))
                {
                    if (!ImGui.Selectable("Item Vendor Lookup".Loc()))
                        return;

                    try
                    {
                        ItemVendorLocation.OpenContextMenu(itemId);
                    }
                    catch (Exception e)
                    {
                        e.Log();
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("Item Vendor Lookup (Please install Item Vendor Location)".Loc());
            }
        }

        internal static void DrawSearchItem(uint itemId, string itemName)
        {
            if (itemId == 0)
                return;

            if (!ImGui.Selectable("Search for Item".Loc()))
                return;

            try
            {
                SearchItem(itemId);
            }
            catch (Exception e)
            {
                e.Log();
            }

        }

        private static void DrawGatherItem(Ingredient item)
        {
            if (item.Data.RowId == 0 || item.Sources.Contains(1))
                return;

            if (GatherBuddy)
            {
                if (!ImGui.Selectable("Gather Item".Loc()))
                    return;

                try
                {
                    if (LuminaSheets.GatheringItemSheet!.Any(x => x.Value.Item.RowId == item.Data.RowId))
                        Chat.Instance.SendMessage($"/gather {item.Data.Name.ToString()}");
                    else
                        Chat.Instance.SendMessage($"/gatherfish {item.Data.Name.ToString()}");
                }
                catch (Exception e)
                {
                    e.Log();
                }
            }
            else
            {
                ImGui.TextDisabled("Gather Item (Please install Gatherbuddy)".Loc());
            }
        }

        /// <summary>
        /// 缺料時把這項的所需數量加進 GatherBuddy 的自動採集清單，不下 /gather、加完也不自動開始；
        /// 開始採集是緊接在下面、要使用者自己按的另一個選單項。
        /// 缺料為 0 時刻意留一行灰字而不是隱藏：使用者要看得出「不是壞了，是不缺」。
        /// </summary>
        private static void DrawAddToAutoGather(Ingredient item)
        {
            if (item.Data.RowId == 0 || item.Sources.Contains(1))
                return;
            if (!item.Sources.Contains(2) && !item.Sources.Contains(3))
                return;

            if (!GatherBuddyIPC.Supported)
            {
                ImGui.TextDisabled("Add to Auto-Gather List (needs a newer Gatherbuddy Reborn)".Loc());
                return;
            }

            // 選單照舊在點選後關閉：開始鍵同時會出現在工具列上，下次打開選單也在這一項下面。
            var missing = item.Remaining;
            if (missing <= 0)
                ImGui.TextDisabled("Add to Auto-Gather List (none missing)".Loc());
            else if (GatherBuddyIPC.SupportsSetQuantity)
                DrawRaiseAutoGatherTarget(item, missing);
            else
                DrawAddRequiredToAutoGather(item, missing);

            GatherBuddyStartButton.Draw(inContextMenu: true, sameLine: false);
        }

        // 設定語意：目標拉到所需數量、原本不低於它就不動，重按或重新載入都不會疊加，所以這條路徑沒有「再加入一次」。
        private static void DrawRaiseAutoGatherTarget(Ingredient item, int missing)
        {
            var itemId   = item.Data.RowId;
            var name     = item.Data.Name.ToString();
            var required = item.Required;
            var target   = GatherBuddyIPC.GetAutoGatherListQuantityForDisplay(itemId);

            if (target >= required)
            {
                ImGui.TextDisabled("Gatherbuddy auto-gather target: ??x (covers the Required amount)".Loc(target));
                if (ImGui.IsItemHovered())
                    ImGuiEx.SetTooltip("GatherBuddy counts what you already have and keeps gathering until it reaches its target, so nothing needs to change. A higher target (for example from another list) is never lowered.".Loc()
                                       + "\n" + "With GatherBuddy's 'Check Retainer Inventories' off, it only counts your own inventory.".Loc());
                return;
            }

            var clicked = ImGui.Selectable("Set Gatherbuddy auto-gather target to ??x (the Required amount)".Loc(required));
            if (ImGui.IsItemHovered())
            {
                var help = "GatherBuddy counts what you already have and keeps gathering until you have this many, so Artisan gives it the Required amount rather than only what is missing (??).".Loc(missing);
                if (target > 0)
                    help += "\n" + "Its current target is ??x.".Loc(target);
                ImGuiEx.SetTooltip(help + "\n" + "With GatherBuddy's 'Check Retainer Inventories' off, it only counts your own inventory.".Loc());
            }

            if (!clicked)
                return;

            var outcome = GatherBuddyIPC.RaiseAutoGatherTarget(itemId, required, out var before);
            Svc.Log.Information($"[Artisan] 右鍵設定 GatherBuddy 目標：道具 {itemId}，所需數量 {required}、剩餘需求 {missing}、對方原本目標 {before}（-1＝問不到），結果 {outcome}。");
            switch (outcome)
            {
                case GatherBuddyIPC.TargetOutcome.Raised:
                    GatherBuddyStartButton.OnAdded();
                    Notify.Success("Gatherbuddy's auto-gather target for ?? is now ??x.".Loc(name, required));
                    break;
                case GatherBuddyIPC.TargetOutcome.AlreadyCovered:
                    GatherBuddyStartButton.OnAdded();
                    Notify.Info("Gatherbuddy's target for ?? is already ??x; left unchanged.".Loc(name, before));
                    break;
                default:
                    Notify.Error(before < 0
                        ? "Could not read Gatherbuddy's current target for ??; nothing was changed.".Loc(name)
                        : "Gatherbuddy could not take ?? (no gathering node for it).".Loc(name));
                    break;
            }
        }

        // 舊版對方（沒有設定端點）的累加路徑。對方是累加數量：這份清單本次送過的就不再自動送，
        // 要再送只能走明寫「再加入一次」的那一項（例如使用者已經從對方清單拿掉了）。
        private static void DrawAddRequiredToAutoGather(Ingredient item, int missing)
        {
            var listId   = item.OriginList.ID;
            var itemId   = item.Data.RowId;
            var name     = item.Data.Name.ToString();
            var required = item.Required;
            var sent     = GatherBuddyIPC.SentFromList(listId, itemId);
            var again    = sent >= required;

            var clicked = ImGui.Selectable(again
                ? "Add ??x to Gatherbuddy Auto-Gather List again".Loc(required)
                : "Add ??x (the Required amount) to Gatherbuddy Auto-Gather List".Loc(required));

            if (ImGui.IsItemHovered())
            {
                var help = again
                    ? "This list already sent ??x of it to GatherBuddy since Artisan was loaded, and GatherBuddy adds on top of what its list already has. Only send it again if you removed it from GatherBuddy's list.".Loc(sent)
                    : "GatherBuddy counts what you already have and keeps gathering until you have this many, so Artisan gives it the Required amount rather than only what is missing (??).".Loc(missing);
                if (!again && sent > 0)
                    help += "\n" + "This list already sent ??x since Artisan was loaded, so only the other ??x is added.".Loc(sent, required - sent);
                ImGuiEx.SetTooltip(help + "\n" + "With GatherBuddy's 'Check Retainer Inventories' off, it only counts your own inventory.".Loc());
            }

            if (!clicked)
                return;

            var added = GatherBuddyIPC.AddRequiredToAutoGatherList(listId, itemId, required, again);
            Svc.Log.Information($"[Artisan] 右鍵把道具 {itemId} 加進 GatherBuddy：清單 {listId}，所需數量 {required}、剩餘需求 {missing}、本次已送 {sent}、再加入一次={again}，這次送 {added?.ToString() ?? "失敗"}。");
            if (added is > 0)
            {
                GatherBuddyStartButton.OnAdded();
                Notify.Success("Added ??x ?? to Gatherbuddy's auto-gather list.".Loc(added.Value, name));
            }
            else if (added == null)
            {
                Notify.Error("Gatherbuddy could not take ?? (no gathering node for it).".Loc(name));
            }
        }
    }

    [Flags]
    public enum ItemFilter
    {
        NoItems = 0,
        MissingItems = 1,
        NoMissingItems = 2,

        Crafted = 4,
        Gathered = 8,
        Fishing = 16,
        Vendor = 32,
        MonsterDrop = 64,
        Unknown = 128,

        NonCrystals = 256,
        Crystals = 512,

        GatherZone = 4096,
        NoGatherZone = 8192,
        TimedNode = 16384,
        NonTimedNode = 32768,

        All = MissingItems + NoMissingItems +
                Crafted + Gathered + Fishing + Vendor + MonsterDrop + Unknown +
                NonCrystals + Crystals +
                GatherZone + NoGatherZone + TimedNode + NonTimedNode,
    }
}
