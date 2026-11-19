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
}
