using Artisan.Autocraft;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Quality = Artisan.CraftingLists.InventorySlotRules.Quality;

namespace Artisan.CraftingLists;

/// <summary>製作清單的背包格位：開始時與清單視窗上的預估（只顯示），以及「背包剩 N 格就暫停清單」。</summary>
/// <remarks>
/// - 全部只在框架執行緒呼叫（清單視窗繪製、<see cref="CraftingListFunctions.ProcessList"/>）；背包每次重讀，不保留原生指標。
/// - 讀不到背包＝「不知道」：預估顯示「?」，暫停判斷照舊不暫停（不把讀不到當成背包滿）。
/// - 不佔背包的成品不算：水晶欄（道具 1～19）、宇宙探索配方（Number 0，清單存檔時本來就會被移除）。
/// - HQ 與 NQ 分開疊。簡易製作、不能 HQ 的成品一律 NQ；其餘事前不知道品質：預估把兩種品質當同一種、
///   既有的空間合併算；暫停判斷要求兩種品質的現有疊都放得下，否則照「要新格子」處理。
/// - 收藏品每件各佔一格。預估是上限：中間素材用完空出來的格子不扣。
/// </remarks>
internal static class ListInventorySlots
{
    internal sealed class Estimate
    {
        public bool Known;
        public int Needed;
        public int Free;
        /// <summary>佔格最多的成品，由多到少，最多 3 種。</summary>
        public readonly List<(string Name, int Slots)> Top = new();
        public int Short => Known ? Math.Max(0, Needed - Free) : 0;
    }

    /// <param name="crafts">要做的（配方、是否簡易製作、做幾次）。</param>
    internal static Estimate Compute(IEnumerable<(uint RecipeId, bool Quick, int Crafts)> crafts)
    {
        var est = new Estimate();
        var recipes = LuminaSheets.RecipeSheet;
        var items = LuminaSheets.ItemSheet;
        if (recipes == null || items == null || !CharacterOther.TryGetInventoryFreeSlotCount(out var free))
            return est;

        var demand = new Dictionary<(uint Item, Quality Q), int>();
        foreach (var (recipeId, quick, n) in crafts)
        {
            if (n <= 0 || !recipes.TryGetValue(recipeId, out var r) || r.Number == 0)
                continue;
            var itemId = r.ItemResult.RowId;
            if (itemId == 0 || InventorySlotRules.SkipsBag(itemId) || !items.TryGetValue(itemId, out var item))
                continue;
            var key = (itemId, InventorySlotRules.ExpectedQuality(quick, item.CanBeHq, r.CanHq));
            demand[key] = demand.GetValueOrDefault(key) + n * Math.Max(1, (int)r.AmountResult);
        }

        var perKind = new List<(string Name, int Slots)>();
        var needed = 0;
        foreach (var ((itemId, q), amount) in demand)
        {
            var item = items[itemId];
            var stackSize = (int)item.StackSize;
            var stackable = InventorySlotRules.IsStackable(stackSize, item.AlwaysCollectable);
            var room = 0;
            if (stackable)
            {
                if (q != Quality.Hq)
                {
                    if (!CharacterOther.TryGetStackRoom(itemId, false, stackSize, out var t, out _))
                        return est;
                    room += t;
                }
                if (q != Quality.Nq)
                {
                    if (!CharacterOther.TryGetStackRoom(itemId, true, stackSize, out var t, out _))
                        return est;
                    room += t;
                }
            }

            var slots = InventorySlotRules.NewSlotsNeeded(amount, stackable, stackSize, room);
            if (slots <= 0)
                continue;
            needed += slots;
            perKind.Add((item.Name.ExtractText(), slots));
        }

        est.Known = true;
        est.Needed = needed;
        est.Free = free;
        est.Top.AddRange(perKind.OrderByDescending(x => x.Slots).Take(3));
        return est;
    }

    private static bool IsQuick(uint recipeId, bool nqOnly)
        => nqOnly
           && LuminaSheets.RecipeSheet?.TryGetValue(recipeId, out var r) == true
           && r.CanQuickSynth
           && P.ri.HasRecipeCrafted(recipeId);

    /// <summary>清單編輯器裡的整份清單（與按「開始」時展開的內容相同）。</summary>
    internal static IEnumerable<(uint, bool, int)> EditorCrafts(NewCraftingList list)
    {
        foreach (var r in list.Recipes.ToArray())
        {
            if (r.ListItemOptions?.Skipping == true || r.Quantity <= 0)
                continue;
            yield return (r.ID, IsQuick(r.ID, r.ListItemOptions?.NQOnly ?? false), r.Quantity);
        }
    }

    /// <summary>執行中清單從 <paramref name="from"/> 起還沒做的（含先做素材插入的項目）。</summary>
    internal static IEnumerable<(uint, bool, int)> RemainingCrafts(NewCraftingList list, int from)
    {
        var quick = new Dictionary<(uint, bool), bool>();
        for (var i = Math.Max(0, from); i < list.ExpandedList.Count; i++)
        {
            var id = list.ExpandedList[i];
            var key = (id, ListPrecraft.OptionsFor(list, id, i)?.NQOnly ?? false);
            if (!quick.TryGetValue(key, out var q))
                quick[key] = q = IsQuick(id, key.Item2);
            yield return (id, q, 1);
        }
    }

    private static string DescribeTop(Estimate e)
        => string.Join("、", e.Top.Select(x => $"{x.Name}×{x.Slots}"));

    /// <summary>按「開始」時寫一行預估（設定關著就不寫）。不影響開始。</summary>
    internal static void LogAtStart(NewCraftingList list)
    {
        if (!P.Config.ShowListInventoryEstimate)
            return;
        try
        {
            var e = Compute(RemainingCrafts(list, 0));
            if (!e.Known)
                Svc.Log.Information($"[Artisan][背包格位] 清單 {list.Name} 開始：讀不到背包，算不出要幾格。");
            else if (e.Short > 0)
                Svc.Log.Information($"[Artisan][背包格位] 清單 {list.Name} 預估要 {e.Needed} 格、目前空 {e.Free} 格，約差 {e.Short} 格；佔格最多：{DescribeTop(e)}");
            else
                Svc.Log.Information($"[Artisan][背包格位] 清單 {list.Name} 預估要 {e.Needed} 格、目前空 {e.Free} 格。");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[Artisan][背包格位] 清單 {list.Name} 開始時預估失敗（不影響製作）：{ex.Message}");
        }
    }

    // 同一次「背包不夠」只響聲／請塔塔露一次：AutoRetainer 收完僱員恢復清單後會立刻再停，那次只寫聊天與記錄。
    // 背包空出超過保留格數、或重新開始清單，才算新的一次。
    private static bool reserveNoticeSent;

    internal static void ResetReserveNotice() => reserveNoticeSent = false;

    /// <summary>
    /// 「背包剩 N 格就暫停清單」：下一件做出來放不進現有的疊、而且空格 ≤ N 就照「暫停」鈕暫停並回 <see langword="true"/>。
    /// 每幀呼叫；不暫停的路徑不配置記憶體。
    /// </summary>
    internal static bool PauseIfReserveReached(NewCraftingList list, Recipe recipe, bool quick)
    {
        var reserve = P.Config.ListPauseAtFreeSlots;
        if (reserve <= 0)
            return false;
        var itemId = recipe.ItemResult.RowId;
        if (recipe.Number == 0 || itemId == 0 || InventorySlotRules.SkipsBag(itemId))
            return false;
        var items = LuminaSheets.ItemSheet;
        if (items == null || !items.TryGetValue(itemId, out var item))
            return false;
        if (!CharacterOther.TryGetInventoryFreeSlotCount(out var free))
            return false;
        if (free > reserve)
        {
            reserveNoticeSent = false;
            return false;
        }

        var stackSize = (int)item.StackSize;
        var stackable = InventorySlotRules.IsStackable(stackSize, item.AlwaysCollectable);
        var q = InventorySlotRules.ExpectedQuality(quick, item.CanBeHq, recipe.CanHq);
        int nqMax = 0, hqMax = 0;
        if (stackable)
        {
            if (q != Quality.Hq && !CharacterOther.TryGetStackRoom(itemId, false, stackSize, out _, out nqMax))
                return false;
            if (q != Quality.Nq && !CharacterOther.TryGetStackRoom(itemId, true, stackSize, out _, out hqMax))
                return false;
        }
        var amount = Math.Max(1, (int)recipe.AmountResult);
        if (!InventorySlotRules.ShouldPause(reserve, free, InventorySlotRules.FitsExistingStack(q, stackable, amount, nqMax, hqMax)))
            return false;

        var notify = !reserveNoticeSent;
        reserveNoticeSent = true;
        if (notify)
            Endurance.NotifyForcedStop($"背包只剩 {free} 格");
        ProcessingWindow.PauseList();

        var name = item.Name.ExtractText();
        Svc.Log.Information($"[Artisan][背包格位] 背包剩 {free} 格，已達設定的 {reserve} 格；{name}（{itemId}，{q}，可疊={stackable}）放不進現有的格子，"
                            + $"暫停清單 {list.Name}（第 {CraftingListFunctions.CurrentIndex + 1}/{list.ExpandedList.Count} 件）；"
                            + (notify ? "已發出提示音／TataruPraise 通知。" : "這次背包不足已經通知過，不再響聲。"));
        Svc.Chat.PrintError("[Artisan] " + "Your bag has only ?? free slot(s) left (you set the list to pause at ??), and ?? would need a new slot, so the crafting list has been paused. Make room in your bag, then press Resume.".Loc(free, reserve, name));
        return true;
    }

    /// <summary>清單視窗上的一行預估。最多每秒重算一次；畫的時候只用快取好的字串。</summary>
    internal sealed class Line(Func<IEnumerable<(uint, bool, int)>> source)
    {
        private long nextRefreshAt;
        private bool show;
        private bool known;
        private string text = string.Empty;
        private string tooltip = string.Empty;
        private string okText = string.Empty;

        /// <param name="sameLine">接在同一列前一個元件後面；這一幀沒有東西要畫時不會留下懸空的 SameLine。</param>
        internal void Draw(bool sameLine)
        {
            if (!P.Config.ShowListInventoryEstimate)
                return;

            RefreshIfDue();
            if (!show)
                return;
            if (sameLine)
                ImGui.SameLine();
            if (known)
                ImGui.TextColored(ImGuiColors.DalamudOrange, text);
            else
                ImGui.TextDisabled(text);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }

        /// <summary>「開工前」卡片上的一段：背包夠也畫（綠字），所以這一段在卡片上永遠看得到結果或「?」。</summary>
        internal void DrawCardSegment(bool sameLine)
        {
            if (!P.Config.ShowListInventoryEstimate)
                return;

            RefreshIfDue();
            if (sameLine)
                ImGui.SameLine();
            if (!known)
                ImGui.TextDisabled(text);
            else if (show)
                ImGui.TextColored(ImGuiColors.DalamudOrange, text);
            else
                ImGui.TextColored(ImGuiColors.HealerGreen, okText);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }

        private void RefreshIfDue()
        {
            var now = Environment.TickCount64;
            if (now >= nextRefreshAt)
            {
                nextRefreshAt = now + 1000;
                Refresh();
            }
        }

        private void Refresh()
        {
            Estimate e;
            try
            {
                e = Compute(source());
            }
            catch (Exception ex)
            {
                FaultOnce.Report("背包格位預估", ex);
                e = new Estimate();
            }

            known = e.Known;
            if (!e.Known)
            {
                show = true;
                text = "Bag slots: ?".Loc();
                tooltip = "Your bag can't be read right now (for example while changing areas), so the number of slots this list needs is unknown.".Loc();
                return;
            }

            show = e.Short > 0;
            if (!show)
            {
                okText = "Bag: enough".Loc();
                tooltip = "Estimated ?? slot(s) needed, ?? free now. This is an upper bound: slots freed as intermediate materials get used up are not subtracted.".Loc(e.Needed, e.Free);
                return;
            }
            text = "Bag about ?? slot(s) short".Loc(e.Short);
            var sb = new StringBuilder();
            sb.Append("Estimated ?? slot(s) needed, ?? free now. This is an upper bound: slots freed as intermediate materials get used up are not subtracted.".Loc(e.Needed, e.Free));
            sb.Append('\n').Append("Takes the most slots:".Loc());
            foreach (var (name, slots) in e.Top)
                sb.Append('\n').Append("??: ?? slot(s)".Loc(name, slots));
            tooltip = sb.ToString();
        }
    }
}
