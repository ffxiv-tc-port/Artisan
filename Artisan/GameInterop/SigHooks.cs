using Dalamud.Hooking;
using ECommons.DalamudServices;
using System;

namespace Artisan.GameInterop;

internal static class SigHooks
{
    /// <summary>
    /// 與 <c>Svc.Hook.HookFromSignature</c> 是同一個呼叫，但建不起來時回 null 並寫一行 Warning，不擲例外。
    /// 給靜態建構子用：那裡擲了，之後每次碰到該型別都是 TypeInitializationException。
    /// </summary>
    /// <param name="lostFeature">寫給看 log 的人：少了這個 hook 會少掉什麼功能。</param>
    internal static Hook<T>? TryHookFromSignature<T>(string name, string signature, T detour, string lostFeature) where T : Delegate
    {
        try
        {
            return Svc.Hook.HookFromSignature(signature, detour);
        }
        catch (Exception ex)
        {
            Svc.Log?.Warning($"[Artisan] 建立 hook 失敗（多半是遊戲改版後特徵碼對不上）：{name}（{signature}）。{lostFeature}〔{ex.GetType().Name}: {ex.Message}〕");
            return null;
        }
    }
}
