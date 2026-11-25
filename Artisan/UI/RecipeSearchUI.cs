using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using Lumina.Excel.Sheets;
using OtterGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Artisan.UI;

// 本檔假設與契約：
// - 除了建索引的背景工作，全部在繪製（框架）執行緒呼叫。
// - 唯一碰原生的是「做過沒」（P.ri.CraftedState）：只在 Player.Available 時、對畫到的列或「做過」篩選重算時呼叫，依配方快取；
//   分頁重新打開、換角色時清快取。委派沒對上或查不到＝列上顯示「?」。
// - 篩選改了不在當幀重掃：文字停 500 ms、其餘下一幀才重算一次，並在那時存設定。
internal static class RecipeSearchUI
{
    private sealed record Index(
        List<RecipeSearchRow> Rows,
        Dictionary<uint, RecipeSearchRow> ById,
        List<(uint Id, string Name)> Categories,
        int MaxStars,
        int MaxLevel,
        string[] JobAbbr,
        string[] JobName);

    private const int TypingDelayMs = 500;

    private static volatile Index? index;
    private static Task? indexTask;
    private static volatile bool indexFailed;
    private static bool fallbackTried;

    private static List<RecipeSearchRow> results = new();
    private static bool recomputePending = true;
    private static long recomputeAt;
    private static bool settingsDirty;
    private static int unknownCrafted;
    private static string lastAddedText = string.Empty;

    private static readonly HashSet<uint> selected = new();
    private static readonly Dictionary<uint, int> wantedItems = new();
    private static readonly Dictionary<uint, bool?> craftedCache = new();
    private static readonly Dictionary<uint, string> jobLabels = new();
    private static ulong craftedCid;
    private static bool lastAvailable;
    private static bool lastCacheBuilt;
    private static int lastFrame = -10;

    private static RecipeSearchSettings S => P.Config.RecipeSearch ??= new();

    private static float Px(float x) => x * ImGui.GetFontSize() / 16f;

    /// <summary>清單編輯器的［用搜尋加入…］：主視窗切到本分頁，加入目標設成這份清單。</summary>
    internal static void OpenFor(NewCraftingList list)
    {
        S.TargetListId = list.ID;
        settingsDirty = true;
        MarkDirty();
        P.PluginUi.OpenWindow = OpenWindow.RecipeSearch;
        P.PluginUi.IsOpen = true;
        P.PluginUi.BringToFront();
    }

    internal static void Draw()
    {
        var idx = EnsureIndex();
        if (idx == null)
        {
            ImGui.TextDisabled(indexFailed && fallbackTried ? "The recipe index could not be built; see the log.".Loc() : "Building recipe index...".Loc());
            return;
        }

        WatchState();
        DrawFilters(idx);
        if (recomputePending && Environment.TickCount64 >= recomputeAt)
            Recompute(idx);

        ImGui.Separator();
        var target = TargetList();
        DrawActions(idx, target);
        DrawResults(idx, target);
    }

    private static void MarkDirty(int delayMs = 0)
    {
        recomputePending = true;
        recomputeAt = Environment.TickCount64 + delayMs;
    }

    private static void SettingChanged(int delayMs = 0)
    {
        settingsDirty = true;
        MarkDirty(delayMs);
    }

    private static Index? EnsureIndex()
    {
        if (index is { } ready)
            return ready;
        if (indexTask == null)
        {
            indexTask = Task.Run(() =>
            {
                try
                {
                    index = BuildIndex();
                }
                catch (Exception e)
                {
                    Svc.Log.Warning($"[Artisan][搜尋配方] 背景建立配方索引失敗，改在繪製執行緒重建：{e.Message}");
                    indexFailed = true;
                }
            });
        }
        else if (indexTask.IsCompleted && index == null && !fallbackTried)
        {
            fallbackTried = true;
            try
            {
                index = BuildIndex();
            }
            catch (Exception e)
            {
                Svc.Log.Warning($"[Artisan][搜尋配方] 建立配方索引失敗，搜尋配方分頁無法使用：{e}");
                indexFailed = true;
            }
        }
        return index;
    }

    private static Index BuildIndex()
    {
        var sheet = LuminaSheets.RecipeSheet ?? throw new InvalidOperationException("RecipeSheet is not loaded");
        var rows = new List<RecipeSearchRow>(sheet.Count);
        var cats = new Dictionary<uint, (string Name, int Major, int Minor)>();
        foreach (var r in sheet.Values)
        {
            if (!RecipeSearchFilter.IsListable(r.ItemResult.RowId, r.Number))
                continue;
            if (r.ItemResult.ValueNullable is not { } item)
                continue;
            var lvl = r.RecipeLevelTable.ValueNullable;
            var cat = item.ItemUICategory.RowId;
            rows.Add(new RecipeSearchRow(r.RowId, r.ItemResult.RowId, item.Name.ExtractText(), r.CraftType.RowId,
                lvl?.ClassJobLevel ?? 0, lvl?.Stars ?? 0, cat, item.IsCollectable, r.IsExpert,
                r.SecretRecipeBook.RowId > 0, r.AmountResult));
            if (cat != 0 && !cats.ContainsKey(cat) && item.ItemUICategory.ValueNullable is { } c)
                cats[cat] = (c.Name.ExtractText(), c.OrderMajor, c.OrderMinor);
        }

        var abbr = new string[8];
        var names = new string[8];
        for (var ct = 0; ct < 8; ct++)
        {
            var job = LuminaSheets.ClassJobSheet?.GetValueOrDefault((uint)ct + 8);
            abbr[ct] = job?.Abbreviation.ExtractText() is { Length: > 0 } a ? a : $"#{ct}";
            names[ct] = job?.Name.ExtractText() ?? abbr[ct];
        }

        return new Index(
            rows,
            rows.ToDictionary(x => x.RecipeId),
            cats.OrderBy(x => x.Value.Major).ThenBy(x => x.Value.Minor).ThenBy(x => x.Key).Select(x => (x.Key, x.Value.Name)).ToList(),
            rows.Count == 0 ? 0 : rows.Max(x => x.Stars),
            rows.Count == 0 ? 100 : rows.Max(x => x.Level),
            abbr,
            names);
    }

    private static void WatchState()
    {
        // 分頁重新打開（上一幀沒畫）＝期間可能做出新東西：「做過」重查。
        var frame = ImGui.GetFrameCount();
        if (frame != lastFrame + 1)
            ResetCrafted();
        lastFrame = frame;

        var available = Player.Available;
        if (available)
        {
            var cid = Svc.PlayerState.ContentId;
            if (cid != craftedCid)
            {
                craftedCid = cid;
                ResetCrafted();
            }
        }
        if (available != lastAvailable)
        {
            lastAvailable = available;
            if (S.Crafted != RecipeSearchTri.Any || P.Config.ShowOnlyCraftable)
                MarkDirty();
        }

        if (P.Config.ShowOnlyCraftable)
        {
            // 與清單編輯器的配方下拉同一條：換角後在用得到時才重建，不走 RetainerInfo.TM（它也跑補貨）。
            if (CraftingListUI.CraftableItemsStale && available)
                _ = RetainerInfo.LoadCacheLogged();
            if (RetainerInfo.CacheBuilt != lastCacheBuilt)
            {
                lastCacheBuilt = RetainerInfo.CacheBuilt;
                MarkDirty();
            }
        }
    }

    private static void ResetCrafted()
    {
        craftedCache.Clear();
        if (S.Crafted != RecipeSearchTri.Any)
            MarkDirty();
    }

    private static bool? CraftedFor(uint recipeId)
    {
        if (craftedCache.TryGetValue(recipeId, out var v))
            return v;
        // 未登入不查也不快取：登入後再畫到這列時才查。
        if (!Player.Available)
            return null;
        v = P.ri.CraftedState(recipeId);
        craftedCache[recipeId] = v;
        return v;
    }

    private static bool? HasMaterials(uint recipeId)
    {
        if (!RetainerInfo.CacheBuilt || LuminaSheets.RecipeSheet?.TryGetValue(recipeId, out var r) != true)
            return null;
        return CraftingListUI.CraftableItems.TryGetValue(r, out var can) ? can : null;
    }

    private static void Recompute(Index idx)
    {
        recomputePending = false;
        var crafted = S.Crafted != RecipeSearchTri.Any ? CraftedFor : (Func<uint, bool?>?)null;
        var mats = P.Config.ShowOnlyCraftable ? HasMaterials : (Func<uint, bool?>?)null;
        results = RecipeSearchFilter.Apply(idx.Rows, S, crafted, mats);
        unknownCrafted = crafted == null ? 0 : results.Count(x => CraftedFor(x.RecipeId) == null);
        if (settingsDirty)
        {
            settingsDirty = false;
            P.Config.Save();
        }
    }

    private static NewCraftingList? TargetList()
    {
        var id = S.TargetListId;
        foreach (var l in P.Config.NewCraftingLists)
            if (l.ID == id)
                return l;
        return null;
    }

    /// <summary>不能加入的原因；null＝可以加入。</summary>
    private static string? AddBlockReason(NewCraftingList? target)
    {
        if (target == null)
            return "Choose a list to add to first.".Loc();
        if (CraftingListUI.Processing && CraftingListUI.selectedList.ID == target.ID)
            return "This list is running. Stop it before adding recipes.".Loc();
        return null;
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    private static void DrawFilters(Index idx)
    {
        var s = S;

        ImGui.SetNextItemWidth(Px(220));
        if (ImGui.InputTextWithHint("##RecipeSearchText", "Name or ID".Loc(), ref s.Text, 100))
            SettingChanged(TypingDelayMs);
        Tip("Matches item names as plain text (not case-sensitive). A number also matches the recipe ID or the item ID.".Loc());
        ImGui.SameLine();
        if (ImGui.Button("x##RecipeSearchClearText") && s.Text.Length > 0)
        {
            s.Text = string.Empty;
            SettingChanged();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Add to:".Loc());
        ImGui.SameLine();
        var target = TargetList();
        ImGui.SetNextItemWidth(Px(200));
        using (var combo = ImRaii.Combo("##RecipeSearchTarget", target?.Name ?? "(choose a list)".Loc()))
        {
            if (combo)
            {
                foreach (var l in P.Config.NewCraftingLists)
                {
                    using var id = ImRaii.PushId(l.ID);
                    if (ImGui.Selectable(l.Name ?? $"#{l.ID}", l.ID == s.TargetListId) && l.ID != s.TargetListId)
                    {
                        s.TargetListId = l.ID;
                        P.Config.Save();
                    }
                }
            }
        }

        ImGui.TextUnformatted("Job".Loc());
        Tip("No job ticked = all jobs.".Loc());
        for (var ct = 0u; ct < 8; ct++)
        {
            ImGui.SameLine();
            var on = s.CraftTypes.Contains(ct);
            if (ImGui.Checkbox($"{idx.JobAbbr[ct]}##RecipeSearchJob{ct}", ref on))
            {
                if (on)
                    s.CraftTypes.Add(ct);
                else
                    s.CraftTypes.RemoveAll(x => x == ct);
                SettingChanged();
            }
            Tip(idx.JobName[ct]);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("All jobs".Loc()) && s.CraftTypes.Count > 0)
        {
            s.CraftTypes.Clear();
            SettingChanged();
        }

        ImGui.TextUnformatted("Level".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(48));
        if (ImGui.InputInt("##RecipeSearchMinLevel", ref s.MinLevel, 0, 0))
        {
            s.MinLevel = Math.Clamp(s.MinLevel, 1, Math.Max(1, idx.MaxLevel));
            SettingChanged(TypingDelayMs);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("~");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(48));
        if (ImGui.InputInt("##RecipeSearchMaxLevel", ref s.MaxLevel, 0, 0))
        {
            s.MaxLevel = Math.Clamp(s.MaxLevel, 1, Math.Max(1, idx.MaxLevel));
            SettingChanged(TypingDelayMs);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Stars".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(80));
        using (var combo = ImRaii.Combo("##RecipeSearchStars", StarsLabel(s.Stars)))
        {
            if (combo)
            {
                for (var st = -1; st <= idx.MaxStars; st++)
                {
                    if (ImGui.Selectable(StarsLabel(st), st == s.Stars) && st != s.Stars)
                    {
                        s.Stars = st;
                        SettingChanged();
                    }
                }
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Category".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(160));
        var catName = s.UiCategory == 0 ? "Any".Loc() : idx.Categories.FirstOrDefault(x => x.Id == s.UiCategory).Name ?? $"#{s.UiCategory}";
        using (var combo = ImRaii.Combo("##RecipeSearchCategory", catName))
        {
            if (combo)
            {
                if (ImGui.Selectable("Any".Loc(), s.UiCategory == 0) && s.UiCategory != 0)
                {
                    s.UiCategory = 0;
                    SettingChanged();
                }
                foreach (var (id, name) in idx.Categories)
                {
                    using var pid = ImRaii.PushId((int)id);
                    if (ImGui.Selectable(name, id == s.UiCategory) && id != s.UiCategory)
                    {
                        s.UiCategory = id;
                        SettingChanged();
                    }
                }
            }
        }

        if (TriCombo("Collectable".Loc(), "##RecipeSearchCollectable", ref s.Collectable)) SettingChanged();
        ImGui.SameLine();
        if (TriCombo("Expert recipe".Loc(), "##RecipeSearchExpert", ref s.Expert)) SettingChanged();
        ImGui.SameLine();
        if (TriCombo("Secret recipe book".Loc(), "##RecipeSearchSecret", ref s.SecretBook)) SettingChanged();
        ImGui.SameLine();
        if (TriCombo("Crafted before".Loc(), "##RecipeSearchCrafted", ref s.Crafted)) SettingChanged();
        Tip("Recipes whose status cannot be read are still listed, marked with ?.".Loc());

        ImGui.TextUnformatted("Sort".Loc());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(120));
        using (var combo = ImRaii.Combo("##RecipeSearchSort", SortLabel(s.Sort)))
        {
            if (combo)
            {
                foreach (var so in Enum.GetValues<RecipeSearchSort>())
                {
                    if (ImGui.Selectable(SortLabel(so), so == s.Sort) && so != s.Sort)
                    {
                        s.Sort = so;
                        SettingChanged();
                    }
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Descending".Loc(), ref s.SortDescending))
            SettingChanged();

        ImGui.SameLine();
        var onlyCraftable = P.Config.ShowOnlyCraftable;
        if (ImGui.Checkbox("Only recipes you have materials for".Loc(), ref onlyCraftable))
        {
            // 與清單編輯器的「僅顯示你有素材的配方」同一個設定、同一套重建。
            P.Config.ShowOnlyCraftable = onlyCraftable;
            if (onlyCraftable)
            {
                RetainerInfo.AbortRestock("切換了「僅顯示你有素材的配方」");
                RetainerInfo.TM.Enqueue(() => { _ = RetainerInfo.LoadCacheLogged(); });
            }
            lastCacheBuilt = RetainerInfo.CacheBuilt;
            SettingChanged();
        }
        Tip("Same setting as the list editor's \"Show only recipes you have materials for\"; whether retainer stock counts follows its \"Include Retainers\".".Loc());
    }

    private static bool TriCombo(string label, string id, ref RecipeSearchTri value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Px(64));
        var changed = false;
        using var combo = ImRaii.Combo(id, TriLabel(value));
        if (combo)
        {
            foreach (var t in Enum.GetValues<RecipeSearchTri>())
            {
                if (ImGui.Selectable(TriLabel(t), t == value) && t != value)
                {
                    value = t;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static string TriLabel(RecipeSearchTri t) => t switch
    {
        RecipeSearchTri.Yes => "Yes".Loc(),
        RecipeSearchTri.No => "No".Loc(),
        _ => "Any".Loc(),
    };

    private static string StarsLabel(int stars) => stars switch
    {
        < 0 => "Any".Loc(),
        0 => "No stars".Loc(),
        _ => new string('★', stars),
    };

    private static string SortLabel(RecipeSearchSort s) => s switch
    {
        RecipeSearchSort.Level => "Level".Loc(),
        RecipeSearchSort.Name => "Name".Loc(),
        RecipeSearchSort.Id => "ID",
        _ => "Job, then level".Loc(),
    };

    private static void DrawActions(Index idx, NewCraftingList? target)
    {
        ImGui.TextUnformatted("Matches: ?? / Selected: ??".Loc(results.Count, selected.Count));
        if (P.Config.ShowOnlyCraftable && !RetainerInfo.CacheBuilt)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Building Craftable Items List: ??/??".Loc(CraftingListUI.CraftableItems.Count, LuminaSheets.RecipeSheet?.Count ?? 0));
        }
        if (unknownCrafted > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?? with unknown crafted status)".Loc(unknownCrafted));
            Tip(CraftedUnknownHelp());
        }
        if (lastAddedText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, lastAddedText);
        }

        if (ImGui.SmallButton("Select all matches".Loc()))
            foreach (var r in results)
                selected.Add(r.RecipeId);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear selection".Loc()))
            selected.Clear();

        var block = AddBlockReason(target) ?? (selected.Count == 0 ? "Nothing is selected.".Loc() : null);
        ImGui.SameLine();
        using (ImRaii.Disabled(block != null))
        {
            if (ImGui.SmallButton("Add selected".Loc()))
                AddSelected(idx, target!, false);
        }
        Tip(block ?? "Adds every ticked recipe, using each row's amount.".Loc());
        ImGui.SameLine();
        using (ImRaii.Disabled(block != null))
        {
            if (ImGui.SmallButton("Add selected (with all sub-crafts)".Loc()))
                AddSelected(idx, target!, true);
        }
        Tip(block ?? "Adds every ticked recipe, using each row's amount.".Loc());
        if (ImGui.Checkbox("Remove all unnecessary subcrafts after adding".Loc(), ref S.TidyAfterAdd))
            P.Config.Save();
    }

    private static string CraftedUnknownHelp()
        => "Shown as ? when it cannot be read: not logged in, the game function was not found (usually right after a game update), secret recipe book recipes, or special recipes.".Loc();

    private static void AddSelected(Index idx, NewCraftingList target, bool withSubcrafts)
    {
        var rows = new List<RecipeSearchRow>(selected.Count);
        foreach (var id in selected)
            if (idx.ById.TryGetValue(id, out var row))
                rows.Add(row);
        rows.Sort(RecipeSearchFilter.Comparer(S.Sort, S.SortDescending));
        if (AddRows(target, rows, withSubcrafts) > 0)
            selected.Clear();
    }

    private static int AddRows(NewCraftingList target, IReadOnlyList<RecipeSearchRow> rows, bool withSubcrafts)
    {
        if (AddBlockReason(target) != null || LuminaSheets.RecipeSheet is not { } sheet)
            return 0;
        var items = new List<(Recipe, int)>(rows.Count);
        foreach (var row in rows)
            if (sheet.TryGetValue(row.RecipeId, out var recipe))
                items.Add((recipe, RecipeSearchFilter.CraftsFor(Wanted(row.RecipeId), row.AmountResult)));

        CraftingListUI.listMaterialsNew.Clear();
        var added = CraftingListHelpers.AddRecipesToList(target, items, withSubcrafts, nqOnExisting: false, tidy: S.TidyAfterAdd, source: "搜尋配方");
        if (added > 0)
        {
            ListEditor.RefreshOpenEditors(target);
            P.Config.Save();
            lastAddedText = "Added ?? recipes to \"??\".".Loc(added, target.Name ?? string.Empty);
        }
        return added;
    }

    private static int Wanted(uint recipeId) => wantedItems.TryGetValue(recipeId, out var n) ? n : 1;

    private static void DrawResults(Index idx, NewCraftingList? target)
    {
        if (results.Count == 0)
        {
            ImGui.TextDisabled("No recipes match.".Loc());
            return;
        }

        var block = AddBlockReason(target);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##RecipeSearchResults", 8, flags, new Vector2(0, Math.Max(Px(120), ImGui.GetContentRegionAvail().Y)));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, Px(24));
        ImGui.TableSetupColumn("Item".Loc(), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Job".Loc(), ImGuiTableColumnFlags.WidthFixed, Px(90));
        ImGui.TableSetupColumn("Crafted before".Loc(), ImGuiTableColumnFlags.WidthFixed, Px(56));
        ImGui.TableSetupColumn("Yield".Loc(), ImGuiTableColumnFlags.WidthFixed, Px(40));
        ImGui.TableSetupColumn("Amount".Loc(), ImGuiTableColumnFlags.WidthFixed, Px(60));
        ImGui.TableSetupColumn("Crafts".Loc(), ImGuiTableColumnFlags.WidthFixed, Px(60));
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.WidthFixed, Px(56));
        ImGui.TableHeadersRow();

        // Begin 把這個區域變數的位址記進 ImGui；離開迴圈的每一條路都要 End。
        var clipper = default(ImGuiListClipper);
        clipper.Begin(results.Count);
        try
        {
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRow(idx, results[i], target, block);
            }
        }
        finally
        {
            clipper.End();
        }
    }

    private static void DrawRow(Index idx, RecipeSearchRow row, NewCraftingList? target, string? block)
    {
        ImGui.TableNextRow();
        using var id = ImRaii.PushId((int)row.RecipeId);

        ImGui.TableNextColumn();
        var sel = selected.Contains(row.RecipeId);
        if (ImGui.Checkbox("##sel", ref sel))
        {
            if (sel)
                selected.Add(row.RecipeId);
            else
                selected.Remove(row.RecipeId);
        }

        ImGui.TableNextColumn();
        Vector4? color = null;
        if (LuminaSheets.ItemSheet?.TryGetValue(row.ItemId, out var item) == true)
        {
            // 每幀重取：共用的圖示包裝跨幀保留會在 Dalamud 回收後被畫到（已釋放）。
            if (P.Icons.TryLoadIcon(item.Icon, out var icon))
            {
                ImGuiUtil.HoverIcon(icon, new Vector2(ImGui.GetTextLineHeight()));
                ImGui.SameLine();
            }
            color = RarityColor(item.Rarity);
        }
        var colored = color.HasValue;
        if (colored)
            ImGui.PushStyleColor(ImGuiCol.Text, color!.Value);
        ImGui.TextUnformatted(row.Name);
        if (colored)
            ImGui.PopStyleColor();
        Tip("Recipe ID: ?? / Item ID: ??".Loc(row.RecipeId, row.ItemId));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(JobLabel(idx, row));

        ImGui.TableNextColumn();
        switch (CraftedFor(row.RecipeId))
        {
            case true:
                ImGui.TextColored(ImGuiColors.HealerGreen, "Yes".Loc());
                break;
            case false:
                ImGui.TextUnformatted("No".Loc());
                break;
            default:
                ImGui.TextDisabled("?");
                Tip(CraftedUnknownHelp());
                break;
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"x{row.AmountResult}");

        ImGui.TableNextColumn();
        var want = Wanted(row.RecipeId);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##want", ref want, 0, 0))
            wantedItems[row.RecipeId] = Math.Max(1, want);
        Tip("How many of this item you want. The list stores crafts: amount / yield, rounded up.".Loc());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted("= ?? crafts".Loc(RecipeSearchFilter.CraftsFor(want < 1 ? 1 : want, row.AmountResult)));

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(block != null))
        {
            if (ImGui.SmallButton("+"))
                AddRows(target!, [row], false);
        }
        Tip(block ?? "Add to List".Loc());
        ImGui.SameLine();
        using (ImRaii.Disabled(block != null))
        {
            if (ImGui.SmallButton("++"))
                AddRows(target!, [row], true);
        }
        Tip(block ?? "Add to List (with all sub-crafts)".Loc());
    }

    private static string JobLabel(Index idx, in RecipeSearchRow row)
    {
        if (!jobLabels.TryGetValue(row.RecipeId, out var label))
        {
            var abbr = row.CraftType < idx.JobAbbr.Length ? idx.JobAbbr[row.CraftType] : "?";
            label = row.Stars > 0 ? $"{abbr} {row.Level} {new string('★', row.Stars)}" : $"{abbr} {row.Level}";
            jobLabels[row.RecipeId] = label;
        }
        return label;
    }

    private static Vector4? RarityColor(byte rarity) => rarity switch
    {
        2 => ImGuiColors.ParsedGreen,
        3 => ImGuiColors.ParsedBlue,
        4 => ImGuiColors.ParsedPurple,
        7 => ImGuiColors.ParsedPink,
        _ => null,
    };
}
