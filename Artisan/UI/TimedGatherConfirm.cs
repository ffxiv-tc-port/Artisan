using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.Universalis;
using Dalamud.Bindings.ImGui;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Artisan.UI;

/// <summary>
/// 「缺料全部加進 GatherBuddy」送出前的確認：列出限時採集點素材、市價與 GatherBuddy 預估的採集時間，
/// 讓使用者取消勾選想改用別的方式取得的素材。只顯示，不做任何購買或市場動作。
/// </summary>
/// <remarks>只從繪製執行緒呼叫。市價與預估都在面板打開時拍一次；市價晚到的會在下一幀補上。</remarks>
internal sealed class TimedGatherConfirm
{
    private const string PopupId = "###ArtisanTimedGatherConfirm";

    private sealed class Row
    {
        public uint       ItemId;
        public Ingredient Item         = null!;
        public int        Remaining;
        public string     Name         = string.Empty;
        public string     NodeLabel    = string.Empty;
        public string     TimeText     = "?";
        public string     TimeTooltip  = string.Empty;
        public bool       TimeKnown;
        public bool       Send         = true;
        public bool       PriceDone;
        public object?    PriceFrom;
        public bool       PriceFailed;
        public string     PriceText    = "?";
        public string     PriceTooltip = string.Empty;
        public bool       PriceKnown;
    }

    private readonly List<Row> _rows = new();
    private int  _otherCount;
    private bool _openRequested;
    private bool _dontAskAgain;

    /// <summary>
    /// <paramref name="missing"/> 裡有限時採集點素材就準備面板並回 <see langword="true"/>（呼叫端這次先不送）；
    /// 沒有就回 <see langword="false"/>，呼叫端照原本流程送。
    /// </summary>
    public bool TryOffer(IReadOnlyList<Ingredient> missing)
    {
        _rows.Clear();
        var log = new StringBuilder();
        foreach (var item in missing)
        {
            // 只看採集品：GatherBuddy 不估魚，魚照原本流程直接送。
            if (!item.Sources.Contains(2))
                continue;

            var remaining = item.Remaining;
            var estimate  = GatherBuddyIPC.GetTimedGatherEstimate(item.Data.RowId, remaining);
            if (!TimedGatherConfirmRules.IsTimedCandidate(estimate?.Status, item.TimedNode))
                continue;

            _rows.Add(BuildRow(item, remaining, estimate));
            log.Append($" {item.Data.RowId}:{remaining}/{estimate?.Status.ToString() ?? "x"}/{estimate?.Rounds ?? 0}/{estimate?.Seconds ?? 0:0}");
        }

        if (_rows.Count == 0)
            return false;

        _otherCount    = missing.Count - _rows.Count;
        _dontAskAgain  = false;
        _openRequested = true;
        PluginLog.Information($"[Artisan] 缺料送 GatherBuddy 前列出限時採集點素材 {_rows.Count} 種、其他 {_otherCount} 種照常送；"
          + $"逐項 道具:還缺/GatherBuddy 狀態（x＝問不到）/輪/秒:{log}");
        return true;
    }

    /// <summary>每幀在「缺料全部加進 GatherBuddy」按鈕旁呼叫。</summary>
    /// <param name="excluded">使用者按下送出時，取消勾選的道具 id。</param>
    /// <returns>使用者這一幀按了送出。</returns>
    public bool Draw(out HashSet<uint> excluded)
    {
        excluded = null!;
        if (_rows.Count == 0 && !_openRequested)
            return false;

        var title = "Timed gathering points - send to GatherBuddy?".Loc() + PopupId;
        if (_openRequested)
        {
            ImGui.OpenPopup(title);
            _openRequested = false;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            _rows.Clear();
            return false;
        }

        var sent = false;
        try
        {
            sent = DrawBody(out excluded);
        }
        finally
        {
            ImGui.EndPopup();
        }

        if (sent || !open)
            _rows.Clear();
        return sent;
    }

    private bool DrawBody(out HashSet<uint> excluded)
    {
        excluded = null!;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36);
        ImGui.TextUnformatted("These missing ingredients come from timed gathering points, which GatherBuddy can only gather while they are up - often only a couple of times an hour. Untick the ones you would rather get another way; unticked ones are not sent to GatherBuddy.".Loc());
        if (_otherCount > 0)
            ImGui.TextUnformatted("The other ?? missing ingredient(s) are sent as usual.".Loc(_otherCount));
        ImGui.PopTextWrapPos();

        if (ImGui.BeginTable("###ArtisanTimedGatherRows", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("Send".Loc());
                ImGui.TableSetupColumn("Ingredient".Loc());
                ImGui.TableSetupColumn("Gathering point".Loc());
                ImGui.TableSetupColumn("Still missing".Loc());
                ImGui.TableSetupColumn("Market price".Loc());
                ImGui.TableSetupColumn("Estimated gathering time".Loc());
                ImGui.TableHeadersRow();
                foreach (var row in _rows)
                    DrawRow(row);
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        ImGui.Checkbox("Don't ask again".Loc(), ref _dontAskAgain);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Takes effect when you press Send. Can be turned back on under Settings > Ingredient Table Settings.".Loc());

        var ticked = 0;
        foreach (var row in _rows)
        {
            if (row.Send)
                ticked++;
        }

        var nothingToSend = ticked == 0 && _otherCount == 0;
        if (nothingToSend)
            ImGui.BeginDisabled();
        var pressed = false;
        try
        {
            pressed = ImGui.Button("Send to GatherBuddy (??)".Loc(ticked + _otherCount));
        }
        finally
        {
            if (nothingToSend)
                ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(nothingToSend
                ? "Everything is unticked, so there is nothing to send.".Loc()
                : "Sends the ticked timed-node ingredients and every other missing ingredient, the same way the button always does. It does not start gathering by itself.".Loc());

        ImGui.SameLine();
        if (ImGui.Button("Cancel".Loc()))
        {
            PluginLog.Information($"[Artisan] 限時採集點確認：使用者取消，這次沒有送任何素材給 GatherBuddy。");
            ImGui.CloseCurrentPopup();
            _rows.Clear();
            return false;
        }

        if (!pressed)
            return false;

        excluded = new HashSet<uint>();
        foreach (var row in _rows)
        {
            if (!row.Send)
                excluded.Add(row.ItemId);
        }

        if (_dontAskAgain)
        {
            P.Config.SkipTimedGatherConfirm = true;
            P.Config.Save();
        }

        PluginLog.Information($"[Artisan] 限時採集點確認：使用者按下送出，列出 {_rows.Count} 種、取消勾選 {excluded.Count} 種（{string.Join(",", excluded)}）；之後不再詢問={_dontAskAgain}。");
        ImGui.CloseCurrentPopup();
        return true;
    }

    private static void DrawRow(Row row)
    {
        ImGui.TableNextRow();
        ImGui.PushID((int)row.ItemId);
        try
        {
            ImGui.TableNextColumn();
            ImGui.Checkbox("###send", ref row.Send);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.NodeLabel);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Remaining.ToString("N0", CultureInfo.CurrentCulture));

            ImGui.TableNextColumn();
            RefreshPrice(row);
            DrawCell(row.PriceText, row.PriceKnown, row.PriceTooltip);

            ImGui.TableNextColumn();
            DrawCell(row.TimeText, row.TimeKnown, row.TimeTooltip);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    // 「不知道」灰字畫在列上，為什麼放 tooltip。
    private static void DrawCell(string text, bool known, string tooltip)
    {
        if (known)
            ImGui.TextUnformatted(text);
        else
            ImGui.TextDisabled(text);
        if (tooltip.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static Row BuildRow(Ingredient item, int remaining, GatherBuddyIPC.TimedEstimate? estimate)
    {
        var row = new Row
        {
            ItemId    = item.Data.RowId,
            Item      = item,
            Remaining = remaining,
            Name      = item.Data.Name.ToString(),
            NodeLabel = estimate?.NodeType switch
            {
                TimedGatherConfirmRules.NodeLegendary => "Legendary node".Loc(),
                TimedGatherConfirmRules.NodeUnspoiled => "Unspoiled node".Loc(),
                TimedGatherConfirmRules.NodeEphemeral => "Ephemeral node".Loc(),
                _                                     => "Timed Node".Loc(),
            },
        };

        if (estimate is not { } e)
        {
            row.TimeTooltip = "GatherBuddy Reborn is not loaded, or is too old to estimate gathering time.".Loc();
            return row;
        }

        if (!TimedGatherConfirmRules.HasEstimate(e.Status))
        {
            row.TimeTooltip = e.Reason.Length > 0 ? e.Reason : "GatherBuddy could not estimate the gathering time for this item.".Loc();
            return row;
        }

        var hour     = " " + "hours".Loc();
        var minute   = " " + "minutes".Loc();
        var duration = TimedGatherConfirmRules.FormatDuration(e.Seconds, hour, minute);
        row.TimeKnown = true;
        row.TimeText = TimedGatherConfirmRules.IsRough(e.Status)
            ? "About ?? rounds / ?? (rough)".Loc(e.Rounds, duration)
            : "About ?? rounds / ??".Loc(e.Rounds, duration);

        var source = e.Status switch
        {
            TimedGatherConfirmRules.EstimateThisItem   => "GatherBuddy's rotation solver estimate for this item".Loc(),
            TimedGatherConfirmRules.EstimateOtherItems => "average of other timed items GatherBuddy's rotation solver handled".Loc(),
            _                                          => "GatherBuddy's default guess - its rotation solver has not run on a timed node since it was loaded".Loc(),
        };
        var perWindow = e.WindowsPerEorzeaDay > 0
            ? TimedGatherConfirmRules.FormatDuration(70 * 60.0 / e.WindowsPerEorzeaDay, hour, minute)
            : "?";
        row.TimeTooltip = "Up ?? time(s) per Eorzean day, about once every ??.".Loc(e.WindowsPerEorzeaDay, perWindow)
          + "\n" + "About ?? per round (??).".Loc(e.YieldPerWindow.ToString("0.#", CultureInfo.CurrentCulture), source)
          + "\n" + "Still missing ?? -> about ?? round(s), ??.".Loc(remaining, e.Rounds, duration)
          + "\n" + "Flying and waiting for the first window are not included. Display only.".Loc();
        return row;
    }

    // 市價只讀 Artisan 已經查到的資料，不替使用者發查詢；晚到的在下一幀補上。
    private static void RefreshPrice(Row row)
    {
        var data   = row.Item.MarketboardData;
        var failed = row.Item.MarketboardFetchFailed;
        if (row.PriceDone && ReferenceEquals(row.PriceFrom, data) && row.PriceFailed == failed)
            return;

        row.PriceDone    = true;
        row.PriceFrom    = data;
        row.PriceFailed  = failed;
        row.PriceKnown   = false;
        row.PriceText    = "?";

        if (data == null)
        {
            row.PriceTooltip = failed
                ? "Could not fetch the price from Universalis.".Loc()
                : P.Config.UseUniversalis
                    ? "No price fetched yet. Use Fetch Prices in the ingredient table.".Loc()
                    : "Universalis prices are turned off in Artisan's settings.".Loc();
            return;
        }

        if (data.AllListings.Count == 0)
        {
            row.PriceTooltip = "Universalis has no listings for this item.".Loc();
            return;
        }

        var cheapest = MarketboardPricing.GetCheapestWorldCost(data, row.Remaining);
        row.PriceKnown = true;
        if (data.Source == MarketboardSource.Aggregated)
        {
            row.PriceText    = "about ?? gil each".Loc(cheapest.Cost.ToString("N0", CultureInfo.CurrentCulture));
            row.PriceTooltip = "Universalis timed out on the full listings, so this is the lowest unit price from its summary endpoint (??).".Loc(cheapest.World)
              + "\n" + "Display only - nothing is bought.".Loc();
            return;
        }

        row.PriceText = cheapest.Qty < row.Remaining
            ? "?? gil (only ?? listed)".Loc(cheapest.Cost.ToString("N0", CultureInfo.CurrentCulture), cheapest.Qty)
            : "?? gil".Loc(cheapest.Cost.ToString("N0", CultureInfo.CurrentCulture));
        row.PriceTooltip = "Cheapest single world: ??, ?? gil for ?? unit(s).".Loc(cheapest.World, cheapest.Cost.ToString("N0", CultureInfo.CurrentCulture), cheapest.Qty)
          + "\n" + "Display only - nothing is bought.".Loc();
    }
}
