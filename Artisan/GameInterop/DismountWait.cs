namespace Artisan.GameInterop;

/// <summary>
/// 「開工前先下坐騎」的等待狀態機：呼叫端每次輪詢給現在時間、是否騎乘中、跳下現在能不能用，照回傳去做。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒呼叫；自己不碰遊戲，送動作與記 log 都由呼叫端做。
/// 同一輪最多等 <see cref="TimeoutMs"/>，期間每 <see cref="PressIntervalMs"/> 最多送一次 ⇒ 下坐騎沒生效也不會卡死或連按。
/// 兩次輪詢間隔超過 newRoundGapMs、或呼叫 <see cref="Reset"/>，就當成新的一輪重新計時。
/// 本檔刻意不引用 Dalamud／遊戲型別：離線探針直接連結這個檔編譯。
/// </remarks>
internal sealed class DismountWait
{
    internal enum Step
    {
        /// <summary>沒在坐騎上：照常開工。</summary>
        Proceed,
        /// <summary>還在坐騎上：這一次要送「跳下」，然後等。</summary>
        Press,
        /// <summary>還在坐騎上：等（剛送過，或跳下現在不能用）。</summary>
        Wait,
        /// <summary>這一輪剛等滿上限仍在坐騎上（每輪只回一次）。</summary>
        GiveUp,
        /// <summary>這一輪已經放棄過、仍在坐騎上。</summary>
        AfterGiveUp,
    }

    internal const long TimeoutMs = 10_000;
    internal const long PressIntervalMs = 1_500;

    private readonly long newRoundGapMs;
    private bool active;
    private bool gaveUp;
    private long startedAt;
    private long lastPollAt;
    private long lastPressAt;

    internal DismountWait(long newRoundGapMs) => this.newRoundGapMs = newRoundGapMs;

    /// <summary>這一輪送過幾次「跳下」。</summary>
    internal int Presses { get; private set; }

    /// <summary>這一輪從開始到這次輪詢經過的毫秒。</summary>
    internal long ElapsedMs { get; private set; }

    /// <summary>這一輪已經等滿上限放手、而且還沒開始新的一輪。</summary>
    internal bool GaveUp => active && gaveUp;

    /// <summary>這次輪詢開始了新的一輪（呼叫端可印一次說明）。</summary>
    internal bool StartedRound { get; private set; }

    /// <summary>這次回 <see cref="Step.Proceed"/> 是因為剛等到下坐騎（呼叫端可印一次結果）。</summary>
    internal bool FinishedWaiting { get; private set; }

    /// <summary>下一次輪詢一律當成新的一輪。<see cref="Presses"/> 留到新的一輪開始才歸零，給呼叫端印結果。</summary>
    internal void Reset()
    {
        active = false;
        gaveUp = false;
    }

    internal Step Poll(long now, bool mounted, bool canPress)
    {
        StartedRound = false;
        FinishedWaiting = false;
        var newRound = !active || now - lastPollAt > newRoundGapMs;
        lastPollAt = now;

        if (!mounted)
        {
            FinishedWaiting = active && !newRound && !gaveUp;
            ElapsedMs = active ? now - startedAt : 0;
            Reset();
            return Step.Proceed;
        }

        if (newRound)
        {
            active = true;
            gaveUp = false;
            startedAt = now;
            lastPressAt = 0;
            Presses = 0;
            StartedRound = true;
        }

        ElapsedMs = now - startedAt;
        if (gaveUp)
            return Step.AfterGiveUp;
        if (ElapsedMs >= TimeoutMs)
        {
            gaveUp = true;
            return Step.GiveUp;
        }
        if (canPress && (Presses == 0 || now - lastPressAt >= PressIntervalMs))
        {
            lastPressAt = now;
            Presses++;
            return Step.Press;
        }
        return Step.Wait;
    }
}
