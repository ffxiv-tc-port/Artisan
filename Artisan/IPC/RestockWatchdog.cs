namespace Artisan.IPC;

/// <summary>從僱員補貨那條任務鏈的中止判斷；不讀遊戲狀態，由呼叫端每幀把讀到的值傳進來。</summary>
/// <remarks>
/// 步驟逾時會清掉整條鏈；這裡管的是步驟還在時限內、人卻已經離開傳喚鈴的情況。
/// 只在框架執行緒使用（開始是任務、檢查在 Framework.Update）。
/// </remarks>
internal sealed class RestockWatchdog
{
    internal enum Stop
    {
        None = 0,

        /// <summary>傳送、換區，或規劃時的區域在鏈開始前就換掉（同一個佇列裡排在後面的第二輪）。</summary>
        LeftArea,

        /// <summary>開始後一直沒能打開傳喚鈴。</summary>
        BellNotReached,

        /// <summary>打開過傳喚鈴，之後關掉超過寬限時間。</summary>
        BellClosed,
    }

    internal const long BellReachTimeoutMs = 20_000;

    // 換僱員時傳喚鈴旗標若短暫掉一下也不算關閉。
    internal const long BellLostGraceMs = 3_000;

    private long startedAt;
    private ushort territory;
    private bool bellSeen;
    private bool bellLost;
    private long bellLostAt;
    private bool ending;

    internal void Begin(long now, ushort plannedTerritory)
    {
        startedAt = now;
        territory = plannedTerritory;
        bellSeen = false;
        bellLost = false;
        ending = false;
    }

    /// <summary>收尾關掉僱員清單之後，傳喚鈴本來就會關，不再檢查。</summary>
    internal void MarkEnding() => ending = true;

    /// <summary>已經走到收尾那一步（之後鏈結束不算中途中止）。</summary>
    internal bool IsEnding => ending;

    internal Stop Check(long now, bool betweenAreas, ushort currentTerritory, bool atBell)
    {
        if (ending)
            return Stop.None;

        if (betweenAreas || currentTerritory != territory)
            return Stop.LeftArea;

        if (atBell)
        {
            bellSeen = true;
            bellLost = false;
            return Stop.None;
        }

        if (!bellSeen)
            return now - startedAt > BellReachTimeoutMs ? Stop.BellNotReached : Stop.None;

        if (!bellLost)
        {
            bellLost = true;
            bellLostAt = now;
            return Stop.None;
        }

        return now - bellLostAt > BellLostGraceMs ? Stop.BellClosed : Stop.None;
    }

    internal static string Describe(Stop stop) => stop switch
    {
        Stop.LeftArea => "角色離開了開始補貨時的區域（傳送或換區）",
        Stop.BellNotReached => $"{BellReachTimeoutMs / 1000} 秒內沒能打開傳喚鈴（附近沒有可用的傳喚鈴，或角色正在忙）",
        Stop.BellClosed => "傳喚鈴／僱員視窗已經關閉",
        _ => string.Empty,
    };
}
