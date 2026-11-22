using System;
using System.Globalization;

namespace Artisan.UI;

// 匯出到 GatherBuddy 前的限時採集點確認：純判斷與排版，不碰 Dalamud（離線探針直接編譯這個檔）。
internal static class TimedGatherConfirmRules
{
    // 與 GatherBuddyReborn.GetTimedGatherEstimate 的 Status 一致。0＝問不到，不可讀成有估計。
    public const int EstimateUnavailable  = 0;
    public const int EstimateNotTimed     = 1;
    public const int EstimateNotPossible  = 2;
    public const int EstimateThisItem     = 3;
    public const int EstimateOtherItems   = 4;
    public const int EstimateDefaultGuess = 5;

    // GatherBuddy 的 NodeType 值。
    public const int NodeUnspoiled = 1;
    public const int NodeEphemeral = 2;
    public const int NodeLegendary = 3;

    /// <summary>這項素材要不要列進確認面板。</summary>
    /// <param name="gbrStatus">對方的 Status；<see langword="null"/>＝沒問到（沒載入、舊版、呼叫失敗）。</param>
    /// <param name="artisanTimedFlag">本插件自己從遊戲資料判的「限時採集點」旗標。</param>
    /// <remarks>對方明確說是一般點或限時點就照它（它知道同一道具有沒有一般點可採）；其餘退回本插件的旗標。</remarks>
    public static bool IsTimedCandidate(int? gbrStatus, bool artisanTimedFlag)
        => gbrStatus switch
        {
            EstimateNotTimed                                                                       => false,
            EstimateNotPossible or EstimateThisItem or EstimateOtherItems or EstimateDefaultGuess => true,
            _                                                                                      => artisanTimedFlag,
        };

    public static bool HasEstimate(int? gbrStatus)
        => gbrStatus is EstimateThisItem or EstimateOtherItems or EstimateDefaultGuess;

    public static bool IsRough(int? gbrStatus)
        => gbrStatus is EstimateOtherItems or EstimateDefaultGuess;

    // 與 GatherBuddy 自動採集清單列尾同一種寫法：滿 1 小時寫小數一位的小時，否則寫分鐘（至少 1）。
    public static string FormatDuration(double seconds, string hourUnit, string minuteUnit)
        => seconds >= 3600
            ? (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture) + hourUnit
            : Math.Max(1, (int)Math.Round(seconds / 60)).ToString(CultureInfo.InvariantCulture) + minuteUnit;

    /// <summary>預估時間那段文字；<see langword="null"/>＝沒有估計（畫面上寫「?」）。</summary>
    /// <param name="exactTemplate">兩個 <c>??</c> 依序填輪數與時間。</param>
    /// <param name="roughTemplate">同上，給平均值或預設值的估計（帶「估」字樣）。</param>
    public static string? EstimateText(int? gbrStatus, int rounds, double seconds, string exactTemplate, string roughTemplate,
        string hourUnit, string minuteUnit)
        => HasEstimate(gbrStatus)
            ? Fill(IsRough(gbrStatus) ? roughTemplate : exactTemplate, rounds.ToString(CultureInfo.InvariantCulture),
                FormatDuration(seconds, hourUnit, minuteUnit))
            : null;

    /// <summary>素材表右鍵提示多的那一行；兩個 <c>??</c> 依序填時間與市價，不知道的填「?」。</summary>
    public static string HintLine(string template, string? timeText, string? priceText)
        => Fill(template, string.IsNullOrEmpty(timeText) ? "?" : timeText, string.IsNullOrEmpty(priceText) ? "?" : priceText);

    // 與 ECommons Loc(參數…) 同語意：每個值依序換掉第一個「??」，沒有就不換。
    public static string Fill(string template, params string[] values)
    {
        foreach (var v in values)
        {
            var pos = template.IndexOf("??", StringComparison.Ordinal);
            if (pos < 0)
                return template;
            template = string.Concat(template.AsSpan(0, pos), v, template.AsSpan(pos + 2));
        }

        return template;
    }

    /// <summary>右鍵提示的估計值留多久（毫秒）。</summary>
    public const long HintCacheMs = 5000;
}

/// <summary>
/// 只記一筆的過期快取，鍵是 (道具, 數量)：右鍵提示每幀都會畫，對方的估計卻要跨插件呼叫。
/// 「問不到」也照樣記，對方沒裝時不會每幀重問。只在繪製執行緒用。
/// </summary>
internal sealed class TimedHintCache<T>
{
    private uint _itemId;
    private int  _quantity;
    private long _until;
    private bool _has;
    private T    _value = default!;

    public bool TryGet(uint itemId, int quantity, long nowMs, out T value)
    {
        var hit = _has && _itemId == itemId && _quantity == quantity && nowMs < _until;
        value = hit ? _value : default!;
        return hit;
    }

    public void Put(uint itemId, int quantity, long nowMs, T value, long ttlMs = TimedGatherConfirmRules.HintCacheMs)
    {
        _itemId   = itemId;
        _quantity = quantity;
        _until    = nowMs + ttlMs;
        _value    = value;
        _has      = true;
    }
}
