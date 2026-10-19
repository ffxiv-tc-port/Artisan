using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.IPC;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using OtterGui.Raii;
using System;

namespace Artisan.UI;

/// <summary>
/// 素材丟進 GatherBuddy Reborn 的自動採集清單之後，在原地給一顆「開始自動採集」。
/// 刻意純手動：只在使用者按下時送出，丟料本身絕不順便開始採集。
/// </summary>
/// <remarks>只從繪製路徑呼叫。對方狀態每秒最多問一次，其餘時間讀快取。</remarks>
internal static class GatherBuddyStartButton
{
    private const long RefreshIntervalMs = 1000;

    /// <summary>件數會變，ImGui 的 ID 不能跟著標籤變，否則按到一半換 ID 會吃掉那次點擊。</summary>
    private const string ImGuiId = "###ArtisanStartGatherBuddyAutoGather";

    /// <summary>這次載入有沒有成功丟過料；沒丟過就不出現。</summary>
    private static bool _offered;

    private static long   _nextRefresh;
    private static bool?  _enabled;
    private static int    _itemCount  = GatherBuddyIPC.UnknownCount;
    private static string _statusText = string.Empty;

    /// <summary>成功丟進對方清單之後呼叫：按鈕從此出現，狀態在下一次繪製時重問。</summary>
    internal static void OnAdded()
    {
        _offered     = true;
        _nextRefresh = 0;
    }

    /// <param name="inContextMenu">右鍵選單裡畫成選單項，工具列上畫成按鈕。</param>
    /// <param name="sameLine">真的有東西要畫時才接在前一個元件的同一行。</param>
    internal static void Draw(bool inContextMenu, bool sameLine)
    {
        if (!_offered || !GatherBuddyIPC.Supported)
            return;

        Refresh();
        // 問不到對方的開關（端點不見了、正在卸載）就整個不畫，不要猜。
        if (_enabled == null)
            return;

        if (sameLine)
            ImGui.SameLine();

        if (_enabled == true)
            DrawRunning(inContextMenu);
        else
            DrawStart(inContextMenu);
    }

    private static void Refresh()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshIntervalMs;
        _enabled     = GatherBuddyIPC.IsAutoGatherEnabled();
        _itemCount   = _enabled == false ? GatherBuddyIPC.GetActiveItemCount() : GatherBuddyIPC.UnknownCount;
        _statusText  = _enabled == true ? GatherBuddyIPC.GetAutoGatherStatusText() ?? string.Empty : string.Empty;
    }

    private static void DrawRunning(bool inContextMenu)
    {
        if (!inContextMenu)
            ImGui.AlignTextToFramePadding();

        ImGuiEx.Text(ImGuiColors.HealerGreen, "GatherBuddy is auto-gathering".Loc());
        if (ImGui.IsItemHovered())
            ImGuiEx.SetTooltip("GatherBuddy Reborn is gathering everything on its enabled auto-gather lists.\nGatherBuddy status: ??"
                .Loc(_statusText.Length > 0 ? _statusText : "?"));
    }

    private static void DrawStart(bool inContextMenu)
    {
        var busy  = Endurance.Enable || CraftingListUI.Processing;
        var empty = _itemCount == 0;
        var label = "Start Auto-Gather (?? item kind(s))".Loc(_itemCount < 0 ? "?" : _itemCount) + ImGuiId;

        bool clicked;
        using (ImRaii.Disabled(busy || empty))
            clicked = inContextMenu ? ImGui.Selectable(label) : ImGui.Button(label);

        if (clicked)
            Start();

        // ImGuiEx.Tooltip 自己再判一次沒帶 AllowWhenDisabled 的 hover，變灰時會永遠不顯示。
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        var help = "Turns on GatherBuddy Reborn's auto-gather, the same as its own toggle.\nIt gathers everything on ALL of your enabled auto-gather lists, not only what Artisan just added (Artisan's items are on the 'From Other Plugins' list). The number on the button counts item kinds across all enabled lists.".Loc();
        if (busy)
            help = "Artisan is crafting (Endurance or a crafting list is running). Stop it first - gathering would fight the crafting for your character.".Loc() + "\n\n" + help;
        else if (empty)
            help = "GatherBuddy's enabled auto-gather lists have nothing on them.".Loc() + "\n\n" + help;
        ImGuiEx.SetTooltip(help);
    }

    private static void Start()
    {
        var countAtClick = _itemCount;
        var result       = GatherBuddyIPC.StartAutoGather();
        _nextRefresh = 0;

        PluginLog.Information($"[Artisan] 使用者按下「開始自動採集」：已送出 GatherBuddyReborn.SetAutoGatherEnabled(true)，對方回報 enabled={result?.ToString() ?? "?"}（按下時已啟用清單共 {countAtClick} 種道具，-1＝不知道）。");
        switch (result)
        {
            case true:
                Notify.Success("GatherBuddy started auto-gathering.".Loc());
                break;
            case false:
                Notify.Warning("GatherBuddy did not take the request (it may be busy or unloading). Try again in a moment.".Loc());
                break;
            default:
                Notify.Error("Could not reach GatherBuddy Reborn; auto-gathering was not started.".Loc());
                break;
        }
    }
}
