namespace Artisan.GameInterop;

/// <summary>換套裝確認框（套裝缺主手）叫出來之後的旁觀判斷；不讀遊戲狀態，由呼叫端把看到的值傳進來。</summary>
/// <remarks>
/// 確認框出現過、消失後職業超過寬限仍沒換過去＝使用者沒答應（按「否」、取消，或換裝本身沒成功）。
/// 兩次輪詢隔超過 <see cref="RoundGapMs"/>＝上一輪已被打斷，狀態作廢。只在框架執行緒使用。
/// </remarks>
internal sealed class GearsetPromptWatch
{
    internal enum Verdict
    {
        /// <summary>沒在等（或等的不是這個職業、確認框還沒出現過）：呼叫端照原本的流程。</summary>
        NotWatching = 0,

        /// <summary>確認框還在畫面上，或剛消失、還在寬限內等職業換過去。</summary>
        Waiting,

        /// <summary>確認框消失超過寬限，職業沒換。回報一次後不再旁觀。</summary>
        Declined,
    }

    internal const long GraceMs = 3_000;

    internal const long RoundGapMs = 2_000;

    private bool armed;
    private bool shown;
    private uint job;
    private long lastShownAt;
    private long lastPollAt;

    /// <summary>呼叫端剛叫出確認框。</summary>
    internal void Arm(long now, uint jobId)
    {
        armed = true;
        shown = false;
        job = jobId;
        lastPollAt = now;
    }

    internal void Reset() => armed = false;

    /// <summary>這一輪換職業還在輪詢、而且正在等確認框的結果。</summary>
    internal bool IsAwaiting(long now) => armed && now - lastPollAt <= RoundGapMs;

    internal Verdict Poll(long now, uint jobId, bool promptVisible)
    {
        if (!armed)
            return Verdict.NotWatching;

        if (jobId != job || now - lastPollAt > RoundGapMs)
        {
            armed = false;
            return Verdict.NotWatching;
        }

        lastPollAt = now;
        if (promptVisible)
        {
            shown = true;
            lastShownAt = now;
            return Verdict.Waiting;
        }

        if (!shown)
            return Verdict.NotWatching;

        if (now - lastShownAt <= GraceMs)
            return Verdict.Waiting;

        armed = false;
        return Verdict.Declined;
    }
}
