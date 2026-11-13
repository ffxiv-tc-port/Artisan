using ECommons.DalamudServices;
using System;
using System.Collections.Generic;

namespace Artisan;

/// <summary>
/// 每幀路徑上「同一處、同一種例外」只記一次 Error；之後同樣的重擲靜默吞掉。
/// </summary>
/// <remarks>
/// 不用 EzThrottler：它的 key 全插件共用且永久保留。簽章＝位置＋例外型別＋擲出點，
/// 不含訊息（訊息常帶變動數字，會讓去重失效）。記過的簽章有上限，滿了之後新簽章照樣靜默。
/// </remarks>
internal static class FaultOnce
{
    private const int MaxSignatures = 64;
    private static readonly HashSet<string> Seen = new();
    private static readonly object Gate = new();

    /// <returns>這個簽章第一次出現（已寫進 log）時回 true。</returns>
    internal static bool Report(string where, Exception ex)
    {
        var signature = $"{where}|{ex.GetType().FullName}|{ex.TargetSite?.DeclaringType?.FullName}.{ex.TargetSite?.Name}";
        lock (Gate)
        {
            if (Seen.Count >= MaxSignatures || !Seen.Add(signature))
                return false;
        }
        Svc.Log.Error(ex, $"[Artisan] {where} 擲出例外（同一處同一種例外之後不再重複記錄）");
        return true;
    }
}
