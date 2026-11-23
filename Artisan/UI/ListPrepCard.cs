using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.UI.Tables;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Verdict = Artisan.CraftingLists.ListPrepRules.StatVerdict;

namespace Artisan.UI;

/// <summary>清單編輯器頂端的「開工前」卡片：屬性是否足夠、還缺哪些素材、背包格位（沿用 <see cref="ListInventorySlots.Line"/>）。</summary>
/// <remarks>
/// - 只顯示，不改清單、不改設定、不排任何查詢。只在繪製（框架）執行緒呼叫；不保留原生指標，能力值走 <see cref="CrafterStatsSnapshot.Current"/>。
/// - 最多每秒重算一次；畫的時候只用快取好的字串與列。
/// - 素材數字直接讀材料表（<see cref="Ingredient.Remaining"/>，與「剩餘需求」欄同一個值），表還沒建就是「?」。
/// </remarks>
internal sealed class ListPrepCard(NewCraftingList list, ListInventorySlots.Line slotLine)
{
    private sealed record StatRow(string Recipe, string Job, int ReqCraft, int ReqControl,
        int GearCraft, int GearControl, int BoostCraft, int BoostControl, Verdict Verdict);

    private sealed record MissingRow(string Name, int Required, int Have, int Missing);

    private long nextRefreshAt;
    private IngredientTable? lastTable;
    private readonly List<StatRow> statRows = new();
    private readonly List<MissingRow> missingRows = new();
    private string statText = string.Empty, statTooltip = string.Empty;
    private Vector4 statColor;
    private bool statGrey;
    private bool materialsKnown;
    private string materialText = string.Empty, materialTooltip = string.Empty;

    /// <returns>true＝使用者按了「計算素材」，呼叫端要開始建材料表。</returns>
    internal bool Draw(IngredientTable? table, bool tableBuilding)
    {
        var now = Environment.TickCount64;
        if (now >= nextRefreshAt || !ReferenceEquals(table, lastTable))
        {
            nextRefreshAt = now + 1000;
            lastTable = table;
            try
            {
                RefreshStats();
                RefreshMaterials(table);
            }
            catch (Exception ex)
            {
                FaultOnce.Report("開工前檢查", ex);
            }
        }

        var open = ImGui.TreeNodeEx("Before you start###ArtisanListPrepCard".Loc());
        var requested = false;

        ImGui.SameLine();
        if (statGrey)
            ImGui.TextDisabled(statText);
        else
            ImGui.TextColored(statColor, statText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(statTooltip);

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if (table == null)
        {
            ImGui.TextDisabled(tableBuilding ? "Materials: calculating...".Loc() : "Materials: ?".Loc());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Missing materials are unknown until the ingredient table is built. Press the button next to this, or open the Ingredients tab.".Loc());
            if (!tableBuilding)
            {
                ImGui.SameLine();
                requested = ImGui.SmallButton("Check materials###ArtisanPrepCheckMaterials".Loc());
            }
        }
        else
        {
            if (!materialsKnown)
                ImGui.TextDisabled(materialText);
            else if (missingRows.Count > 0)
                ImGui.TextColored(ImGuiColors.DalamudOrange, materialText);
            else
                ImGui.TextColored(ImGuiColors.HealerGreen, materialText);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(materialTooltip);
        }

        if (P.Config.ShowListInventoryEstimate)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            slotLine.DrawCardSegment(sameLine: true);
        }

        if (open)
        {
            try
            {
                DrawDetails(table != null);
            }
            finally
            {
                ImGui.TreePop();
            }
        }
        return requested;
    }

    private void RefreshStats()
    {
        statRows.Clear();
        var recipes = LuminaSheets.RecipeSheet;
        var jobs = LuminaSheets.ClassJobSheet;
        var snapshot = CrafterStatsSnapshot.Current();
        int shortCount = 0, foodCount = 0, unknownCount = 0, checkedCount = 0;
        var unknownJobs = new SortedSet<string>(StringComparer.Ordinal);

        if (recipes != null)
        {
            foreach (var li in list.Recipes.ToArray())
            {
                if (li.ListItemOptions?.Skipping == true || li.Quantity <= 0 || !recipes.TryGetValue(li.ID, out var r))
                    continue;
                int reqCraft = r.RequiredCraftsmanship, reqControl = r.RequiredControl;
                if (reqCraft <= 0 && reqControl <= 0)
                    continue;
                checkedCount++;

                var craftType = r.CraftType.RowId;
                var job = jobs != null && jobs.TryGetValue(craftType + 8, out var cj) ? cj.Abbreviation.ExtractText() : "?";
                var known = snapshot?.IsKnown(craftType) == true;
                var gear = known ? snapshot!.StatsForCraftType(craftType) : default;
                var boosted = gear;
                if (known)
                {
                    var config = P.Config.RecipeConfigs.GetValueOrDefault(r.RowId) ?? new();
                    var food = new ConsumableStats(config.FoodEnabled ? config.RequiredFood : 0, config.RequiredFoodHQ);
                    var pot = new ConsumableStats(config.PotionEnabled ? config.RequiredPotion : 0, config.RequiredPotionHQ);
                    boosted.AddConsumables(food, pot, snapshot!.FcCraftsmanshipParam);
                }

                var verdict = ListPrepRules.Classify(known, reqCraft, reqControl, gear.Craftsmanship, gear.Control, boosted.Craftsmanship, boosted.Control);
                switch (verdict)
                {
                    case Verdict.Ok:
                        continue;
                    case Verdict.Short: shortCount++; break;
                    case Verdict.NeedsConsumables: foodCount++; break;
                    default: unknownCount++; unknownJobs.Add(job); break;
                }
                statRows.Add(new(r.ItemResult.Value.Name.ExtractText(), job, reqCraft, reqControl,
                    gear.Craftsmanship, gear.Control, boosted.Craftsmanship, boosted.Control, verdict));
            }
        }
        statRows.Sort(static (a, b) => b.Verdict.CompareTo(a.Verdict));

        statGrey = false;
        if (recipes == null)
        {
            statGrey = true;
            statText = "Stats: ?".Loc();
        }
        else if (shortCount > 0)
        {
            statColor = ImGuiColors.DalamudRed;
            statText = "Stats: ?? recipe(s) short with current gear".Loc(shortCount);
        }
        else if (unknownCount > 0)
        {
            statGrey = true;
            statText = "Stats: ?".Loc();
        }
        else if (foodCount > 0)
        {
            statColor = ImGuiColors.DalamudYellow;
            statText = "Stats: ?? recipe(s) need food/medicine".Loc(foodCount);
        }
        else
        {
            statColor = ImGuiColors.HealerGreen;
            statText = checkedCount > 0 ? "Stats: enough".Loc() : "Stats: no requirement".Loc();
        }
        if (shortCount > 0 && unknownCount > 0)
            statText += " " + "(?? unknown)".Loc(unknownCount);

        var sb = new StringBuilder("Compared with each recipe's required Craftsmanship and Control, using your current gear if you are on that job, otherwise the first gear set of that job. The food and medicine set for the recipe, and the free company buff you have now, are added on top. Values that can't be read show \"?\" and are never treated as enough.".Loc());
        foreach (var job in unknownJobs)
            sb.Append('\n').Append("No gear set for ?? and you are not on that job right now.".Loc(job));
        if (statRows.Count > 0)
            sb.Append('\n').Append("Expand this card to see each recipe.".Loc());
        statTooltip = sb.ToString();
    }

    private void RefreshMaterials(IngredientTable? table)
    {
        missingRows.Clear();
        materialsKnown = table != null;
        if (table == null)
            return;

        foreach (var ing in table.ListItems)
        {
            if (ing.RecipeOnList || ing.Required <= 0)
                continue;
            var missing = ing.Remaining;
            if (missing > 0)
                missingRows.Add(new(ing.ItemName, ing.Required, Math.Max(0, ing.Required - missing), missing));
        }
        missingRows.Sort(static (a, b) => b.Missing.CompareTo(a.Missing));

        materialText = missingRows.Count > 0 ? "Materials: ?? kind(s) missing".Loc(missingRows.Count) : "Materials: all on hand".Loc();
        var sb = new StringBuilder("Same numbers as the Remaining Needed column on the Ingredients tab (bag plus retainers; retainers need Allagan Tools). Items this list crafts itself are not counted.".Loc());
        const int shown = 15;
        foreach (var m in missingRows.Take(shown))
            sb.Append('\n').Append("??: ?? missing".Loc(m.Name, m.Missing));
        if (missingRows.Count > shown)
            sb.Append('\n').Append("...and ?? more".Loc(missingRows.Count - shown));
        materialTooltip = sb.ToString();
    }

    private void DrawDetails(bool tableBuilt)
    {
        if (statRows.Count > 0)
        {
            if (ImGui.BeginTable("###ArtisanPrepStats", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                try
                {
                    ImGui.TableSetupColumn("Recipe".Loc());
                    ImGui.TableSetupColumn("Job".Loc());
                    ImGui.TableSetupColumn("Craftsmanship: need / gear / with food".Loc());
                    ImGui.TableSetupColumn("Control: need / gear / with food".Loc());
                    ImGui.TableSetupColumn("Status".Loc());
                    ImGui.TableHeadersRow();
                    foreach (var row in statRows)
                    {
                        var unknown = row.Verdict == Verdict.Unknown;
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.Recipe);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.Job);
                        ImGui.TableNextColumn();
                        DrawStatCell(unknown, row.ReqCraft, row.GearCraft, row.BoostCraft);
                        ImGui.TableNextColumn();
                        DrawStatCell(unknown, row.ReqControl, row.GearControl, row.BoostControl);
                        ImGui.TableNextColumn();
                        switch (row.Verdict)
                        {
                            case Verdict.Short: ImGui.TextColored(ImGuiColors.DalamudRed, "Short with current gear".Loc()); break;
                            case Verdict.NeedsConsumables: ImGui.TextColored(ImGuiColors.DalamudYellow, "Needs food/medicine".Loc()); break;
                            default: ImGui.TextDisabled("?"); break;
                        }
                    }
                }
                finally
                {
                    ImGui.EndTable();
                }
            }
        }

        if (tableBuilt && missingRows.Count > 0)
        {
            var lines = Math.Min(missingRows.Count + 1, 10);
            var size = new Vector2(0, lines * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2 * lines);
            if (ImGui.BeginTable("###ArtisanPrepMissing", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY, size))
            {
                try
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Item".Loc());
                    ImGui.TableSetupColumn("Required".Loc());
                    ImGui.TableSetupColumn("Have".Loc());
                    ImGui.TableSetupColumn("Missing".Loc());
                    ImGui.TableHeadersRow();
                    foreach (var m in missingRows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(m.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(m.Required.ToString("N0"));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(m.Have.ToString("N0"));
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ImGuiColors.DalamudOrange, m.Missing.ToString("N0"));
                    }
                }
                finally
                {
                    ImGui.EndTable();
                }
            }
        }

        if (statRows.Count == 0 && (!tableBuilt || missingRows.Count == 0))
            ImGui.TextDisabled("Nothing to show here.".Loc());
    }

    private static void DrawStatCell(bool unknown, int need, int gear, int boosted)
    {
        if (need <= 0)
        {
            ImGui.TextDisabled("-");
            return;
        }
        if (unknown)
        {
            ImGui.TextUnformatted($"{need} / ? / ?");
            return;
        }
        var color = boosted < need ? ImGuiColors.DalamudRed : gear < need ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen;
        ImGui.TextColored(color, $"{need} / {gear} / {boosted}");
    }
}
