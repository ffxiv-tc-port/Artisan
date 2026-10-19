namespace Artisan.GameInterop;

/// <summary>換職業的時間預算：從第一次送出換套裝起算，職業遲遲沒換過去多久才放棄。不讀遊戲狀態。</summary>
/// <remarks>
/// 只累計任務真的在重試的時間：兩次呼叫隔超過 <see cref="IdleGapMs"/>（任務被別的條件擋住沒在跑）
/// 那一段不算，等使用者回答確認框的時間由 <see cref="Hold"/> 排除。只在框架執行緒使用。
/// </remarks>
internal sealed class ClassChangeBudget
{
    internal const long BudgetMs = 5_000;

    internal const long IdleGapMs = 2_000;

    private bool active;
    private uint job;
    private long spent;
    private long lastAt;

    /// <summary>這一輪送出過幾次換套裝。</summary>
    internal int Attempts { get; private set; }

    /// <summary>遊戲最後一次的回傳值（0＝送出了，負值＝被拒絕）。</summary>
    internal int LastResult { get; private set; }

    /// <summary>換成功、中止、或任務重排：下一次送出重新起算。</summary>
    internal void Reset() => active = false;

    /// <summary>正在等使用者回答確認框：這段時間不算。</summary>
    internal void Hold(long now)
    {
        if (active)
            lastAt = now;
    }

    /// <summary>再送一次之前問：預算用完了沒。還沒送出過、或換了職業一律 false。</summary>
    internal bool Expired(long now, uint jobId)
    {
        if (!active || jobId != job)
        {
            active = false;
            return false;
        }

        var gap = now - lastAt;
        if (gap <= IdleGapMs)
            spent += gap;
        lastAt = now;
        return spent > BudgetMs;
    }

    /// <summary>記一次送出；這一輪的第一次同時是預算的起點。</summary>
    internal void NoteAttempt(long now, uint jobId, int result)
    {
        if (!active || jobId != job)
        {
            active = true;
            job = jobId;
            spent = 0;
            lastAt = now;
            Attempts = 0;
        }

        Attempts++;
        LastResult = result;
    }
}
