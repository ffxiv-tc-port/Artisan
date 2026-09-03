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

        private static bool Lifestream =>
    DalamudReflector.TryGetDalamudPlugin("Lifestream", out var _, false, true);

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
                return item.Data.Name.ToString();
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

                var selected = ImGui.Selectable($"{item.Data.Name.ToString()}");
                InvokeContextMenu(item);

                if (selected)
                {
                    ImGui.SetClipboardText(item.Data.Name.ToString());
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

                    if (MarketboardPricing.TryGetNpcPrice(item.Data, out var npcUnitPrice) && (double)npcUnitPrice * item.Remaining < listing.Cost)
                        return "NPC Shop - Cost ??, Qty unlimited".Loc(npcUnitPrice.ToString("N0"));

                    return "?? - Cost ??, Qty ??".Loc(listing.World, listing.Cost.ToString("N0"), listing.Qty);

                }

                return "ERROR - No Listings (Possible Universalis Connection Issue)".Loc();
            }

            public override void DrawColumn(Ingredient item, int _)
            {
                if (item.MarketboardData != null)
                {
                    ImGui.Text($"{ToName(item)}");
                    if (Lifestream && CheapestListings.ContainsKey(item.Data.RowId) && item.Remaining > 0)
                    {
                        var server = CheapestListings[item.Data.RowId].World;
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.Text("Click to travel to ??.".Loc(server));
                            ImGui.EndTooltip();
                        }

                        if (ImGui.IsItemClicked())
                        {
                            Chat.Instance.SendMessage($"/li {server} mb");
                        }
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
                => lhs.GatherZone.PlaceName.Value.Name.ToString().CompareTo(rhs.GatherZone.PlaceName.Value.Name.ToString());

            public override void DrawColumn(Ingredient item, int idx)
            {
                ImGui.Text(item.GatherZone.PlaceName.Value.Name.ToString());
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
                ImGui.Text(Svc.Data.Excel.GetSheet<ItemSearchCategory>().GetRow(item.Category).Name.ToString());
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
