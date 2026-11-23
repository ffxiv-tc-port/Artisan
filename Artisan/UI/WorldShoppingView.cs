using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.UI.Tables;
using Artisan.Universalis;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Artisan.UI;

/// <summary>清單編輯器「依世界採購」頁：把還缺的素材照 <see cref="WorldShoppingPlanner"/> 分到世界，列需要／持有／還要／小計。</summary>
/// <remarks>
/// - 🔴 市場紅線：只顯示；唯一的動作是使用者手按的「查全部價格」（既有）與「前往市場」（Lifestream，既有），不買、不接手。
/// - 價格只用材料表已經查到的資料（<see cref="Ingredient.MarketboardData"/>），不另外查；DP 表快取自己留，不寫回共用資料。
/// - 只在繪製執行緒用；最多每秒重算一次，畫的時候只用快取好的列與字串。
/// - 買齊（還要＝0）的列留在上次分到的那一組、標「已備齊」，所以邊買邊看時列不會跳走。
/// </remarks>
internal sealed class WorldShoppingView
{
    private sealed class Row
    {
        public required string Name;
        public int Required, Have, Remaining;
        public ShoppingAssignment Plan;
        public string BuyText = string.Empty, CostText = string.Empty, CostTooltip = string.Empty;
        public bool Short;
    }

    private sealed class Group
    {
        public required string Key;
        public required ShoppingAssignKind Kind;
        public string World = string.Empty;
        public readonly List<Row> Rows = new();
        public double Subtotal;
        public int ToBuy;
        public bool Approximate;
        public string Header = string.Empty, TravelLabel = string.Empty, TableId = string.Empty;
        public int Order;
    }

    private const string KeyNpc = "\u0001npc", KeyNoListing = "\u0001none", KeyNoPrice = "\u0001noprice", KeyReady = "\u0001ready";

    private ShoppingPlanMode mode = ShoppingPlanMode.FewestWorlds;
    private long nextRefreshAt;
    private IngredientTable? lastTable;
    private readonly List<Group> groups = new();
    private readonly Dictionary<uint, string> lastGroup = new();
    private readonly Dictionary<uint, CheapestTable?> dpCache = new();
    private string summary = string.Empty, summaryTooltip = string.Empty;
    private bool anyNoPrice;

    internal void Draw(IngredientTable? table, bool building)
    {
        if (!P.Config.UseUniversalis)
        {
            ImGui.TextWrapped("Market prices are turned off. Turn on Universalis price lookups in Artisan's settings to group purchases by world.".Loc());
            return;
        }
        if (table == null)
        {
            ImGui.TextUnformatted(building ? "Ingredient table is still populating. Please wait.".Loc() : "Ingredient table is not ready yet.".Loc());
            return;
        }

        if (ImGui.RadioButton("Fewest worlds (convenient)".Loc(), mode == ShoppingPlanMode.FewestWorlds))
        {
            mode = ShoppingPlanMode.FewestWorlds;
            nextRefreshAt = 0;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Buy as much as possible on as few worlds as possible. A world only takes an item if it costs at most twice the cheapest world's price for it.".Loc());
        ImGui.SameLine();
        if (ImGui.RadioButton("Cheapest per item".Loc(), mode == ShoppingPlanMode.CheapestEach))
        {
            mode = ShoppingPlanMode.CheapestEach;
            nextRefreshAt = 0;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each item goes to the world where buying it costs the least (same as the Optimal World For Buying column).".Loc());

        if (P.Config.UniversalisOnDemand)
        {
            ImGui.SameLine();
            if (ImGui.Button("Fetch All Prices".Loc()))
                IngredientTable.CheapestServerColumn.RequestAllPrices(table.ListItems);
        }

        var now = Environment.TickCount64;
        if (now >= nextRefreshAt || !ReferenceEquals(table, lastTable))
        {
            nextRefreshAt = now + 1000;
            lastTable = table;
            try
            {
                Refresh(table);
            }
            catch (Exception ex)
            {
                FaultOnce.Report("依世界採購", ex);
            }
        }

        ImGui.TextUnformatted(summary);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(summaryTooltip);

        if (ImGui.BeginChild("###ArtisanWorldShopping", ImGui.GetContentRegionAvail()))
        {
            foreach (var g in groups)
                DrawGroup(g);
        }
        ImGui.EndChild();
    }

    private void DrawGroup(Group g)
    {
        if (!ImGui.CollapsingHeader(g.Header, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (g.Kind == ShoppingAssignKind.World && g.ToBuy > 0 && ImGui.Button(g.TravelLabel))
        {
            var result = LifestreamIPC.TravelToMarketboard(g.World);
            if (result == LifestreamIPC.TravelResult.NotLoaded)
                Notify.Error("Lifestream is not loaded, so Artisan cannot travel to the market board.".Loc());
            else if (result == LifestreamIPC.TravelResult.Failed)
                Notify.Error("Could not ask Lifestream to travel. See the log for details.".Loc());
        }

        if (!ImGui.BeginTable(g.TableId, 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;
        try
        {
            ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Required".Loc());
            ImGui.TableSetupColumn("Have".Loc());
            ImGui.TableSetupColumn("Still needed".Loc());
            ImGui.TableSetupColumn("Buy here".Loc());
            ImGui.TableSetupColumn("Subtotal".Loc());
            ImGui.TableHeadersRow();
            foreach (var r in g.Rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Required.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Have.ToString("N0"));
                ImGui.TableNextColumn();
                if (r.Remaining > 0)
                    ImGui.TextUnformatted(r.Remaining.ToString("N0"));
                else
                    ImGui.TextColored(ImGuiColors.HealerGreen, "Got it".Loc());
                ImGui.TableNextColumn();
                if (r.Short)
                    ImGui.TextColored(ImGuiColors.DalamudOrange, r.BuyText);
                else
                    ImGui.TextUnformatted(r.BuyText);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.CostText);
                if (r.CostTooltip.Length > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(r.CostTooltip);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void Refresh(IngredientTable table)
    {
        var needs = new List<ShoppingNeed>();
        var needRows = new List<Row>();
        var doneRows = new List<(uint Id, Row Row)>();
        foreach (var ing in table.ListItems)
        {
            if (ing.RecipeOnList || ing.Required <= 0)
                continue;
            var id = ing.Data.RowId;
            var remaining = ing.Remaining;
            var row = new Row
            {
                Name = ing.ItemName,
                Required = ing.Required,
                Have = Math.Max(0, ing.Required - remaining),
                Remaining = remaining,
            };
            if (remaining <= 0)
            {
                doneRows.Add((id, row));
                continue;
            }

            var data = ing.MarketboardData;
            var need = new ShoppingNeed
            {
                ItemId = id,
                Quantity = remaining,
                Listings = data?.AllListings,
                Exact = data?.Source == MarketboardSource.Listings,
                NpcUnitPrice = MarketboardPricing.TryGetNpcPrice(ing.Data, out var npc) ? npc : 0,
                Table = dpCache.GetValueOrDefault(id),
            };
            needs.Add(need);
            needRows.Add(row);
        }

        var plan = WorldShoppingPlanner.Plan(needs, mode);
        var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Length; i++)
        {
            dpCache[needs[i].ItemId] = needs[i].Table;
            var a = plan[i];
            var row = needRows[i];
            row.Plan = a;
            FillRowText(row, a);
            var key = a.Kind switch
            {
                ShoppingAssignKind.World => "w:" + a.World,
                ShoppingAssignKind.Npc => KeyNpc,
                ShoppingAssignKind.NoListing => KeyNoListing,
                _ => KeyNoPrice,
            };
            lastGroup[a.ItemId] = key;
            var g = GetGroup(byKey, key, a.Kind, a.World);
            g.Rows.Add(row);
            g.ToBuy++;
            g.Subtotal += a.Cost;
            g.Approximate |= a.Approximate;
        }

        foreach (var (id, row) in doneRows)
        {
            var key = lastGroup.GetValueOrDefault(id) ?? KeyReady;
            var kind = key == KeyReady ? ShoppingAssignKind.NoPrice : key.StartsWith("w:", StringComparison.Ordinal) ? ShoppingAssignKind.World
                : key == KeyNpc ? ShoppingAssignKind.Npc : key == KeyNoListing ? ShoppingAssignKind.NoListing : ShoppingAssignKind.NoPrice;
            GetGroup(byKey, key, kind, key.StartsWith("w:", StringComparison.Ordinal) ? key[2..] : string.Empty).Rows.Add(row);
        }

        groups.Clear();
        groups.AddRange(byKey.Values);
        int worlds = 0;
        double total = 0, cheapestTotal = 0;
        var totalApprox = false;
        anyNoPrice = false;
        foreach (var g in groups)
        {
            g.Rows.Sort(static (a, b) =>
            {
                var c = (b.Remaining > 0).CompareTo(a.Remaining > 0);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            g.Header = HeaderFor(g) + "###grp" + g.Key;
            g.TableId = "###tbl" + g.Key;
            g.Order = g.Key == KeyReady ? 5 : g.Kind switch
            {
                ShoppingAssignKind.World => 0,
                ShoppingAssignKind.Npc => 1,
                ShoppingAssignKind.NoListing => 2,
                _ => 3,
            };
            if (g.Kind == ShoppingAssignKind.World)
            {
                g.TravelLabel = "Travel to ??'s market board (Lifestream)".Loc(g.World) + "###travel" + g.Key;
                if (g.ToBuy > 0)
                    worlds++;
            }
            if (g.Kind is ShoppingAssignKind.World or ShoppingAssignKind.Npc)
            {
                total += g.Subtotal;
                totalApprox |= g.Approximate;
                foreach (var r in g.Rows)
                {
                    if (r.Remaining > 0)
                        cheapestTotal += r.Plan.CheapestCost;
                }
            }
            if (g.Key == KeyNoPrice && g.ToBuy > 0)
                anyNoPrice = true;
        }
        groups.Sort(static (a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            if (c == 0) c = b.ToBuy.CompareTo(a.ToBuy);
            if (c == 0) c = a.Subtotal.CompareTo(b.Subtotal);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });

        var totalText = (totalApprox ? "about ?? gil".Loc(total.ToString("N0")) : "?? gil".Loc(total.ToString("N0")));
        summary = "?? world(s) to visit, total ??".Loc(worlds, totalText);
        if (mode == ShoppingPlanMode.FewestWorlds && cheapestTotal < total)
            summary += " " + "(cheapest per item: ?? gil)".Loc(cheapestTotal.ToString("N0"));
        if (anyNoPrice)
            summary += " " + "(some items have no price yet)".Loc();

        var sb = new StringBuilder("Only items this list does not craft itself. Prices come from the ingredient table's Universalis data; market listings are bought whole, so you may get more than you need.".Loc());
        sb.Append('\n').Append("\"Have\" counts your bag plus retainers (retainers need Allagan Tools), the same as the Remaining Needed column. Rows you have finished buying stay in their group, marked \"Got it\".".Loc());
        summaryTooltip = sb.ToString();
    }

    private static Group GetGroup(Dictionary<string, Group> byKey, string key, ShoppingAssignKind kind, string world)
    {
        if (!byKey.TryGetValue(key, out var g))
            byKey[key] = g = new Group { Key = key, Kind = kind, World = world };
        return g;
    }

    private static void FillRowText(Row row, ShoppingAssignment a)
    {
        switch (a.Kind)
        {
            case ShoppingAssignKind.World when a.Approximate:
                row.BuyText = "?";
                row.CostText = "about ?? gil".Loc(a.Cost.ToString("N0"));
                row.CostTooltip = "Universalis only returned the lowest single-unit price for this item, so this is that price times the amount you still need.".Loc();
                break;
            case ShoppingAssignKind.World:
                row.Short = !a.Full;
                row.BuyText = a.Full ? a.Qty.ToString("N0") : "only ??".Loc(a.Qty.ToString("N0"));
                row.CostText = "?? gil".Loc(a.Cost.ToString("N0"));
                if (!a.Full)
                    row.CostTooltip = "No world has enough of this item listed; this world has the most.".Loc();
                else if (a.CheapestCost < a.Cost && a.CheapestWorld != a.World)
                    row.CostTooltip = "Cheapest on ?? for ?? gil (?? gil more here).".Loc(a.CheapestWorld, a.CheapestCost.ToString("N0"), (a.Cost - a.CheapestCost).ToString("N0"));
                break;
            case ShoppingAssignKind.Npc:
                row.BuyText = "NPC";
                row.CostText = "?? gil".Loc(a.Cost.ToString("N0"));
                break;
            default:
                row.BuyText = "?";
                row.CostText = "?";
                break;
        }
    }

    private static string HeaderFor(Group g)
    {
        if (g.Key == KeyReady)
            return "Already on hand: ?? item(s)".Loc(g.Rows.Count);
        switch (g.Kind)
        {
            case ShoppingAssignKind.World:
                if (g.ToBuy == 0)
                    return "??: all bought".Loc(g.World);
                return g.Approximate
                    ? "??: ?? item(s) to buy, subtotal about ?? gil".Loc(g.World, g.ToBuy, g.Subtotal.ToString("N0"))
                    : "??: ?? item(s) to buy, subtotal ?? gil".Loc(g.World, g.ToBuy, g.Subtotal.ToString("N0"));
            case ShoppingAssignKind.Npc:
                return "NPC shops: ?? item(s), subtotal ?? gil".Loc(g.ToBuy, g.Subtotal.ToString("N0"));
            case ShoppingAssignKind.NoListing:
                return "Nothing listed on the market board: ?? item(s)".Loc(g.ToBuy);
            default:
                return "No price yet: ?? item(s)".Loc(g.ToBuy);
        }
    }
}
