using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using System;
using System.Numerics;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Artisan.IPC;

/// <summary>
/// 傳喚鈴互動距離診斷：<see cref="RetainerInfo.GetValidInteractionDistance"/> 的手寫常數與遊戲
/// <c>EventFramework.CheckInteractRange</c> 判定不同時記 Information（最多每 30 秒一行；同區域同結論 5 分鐘一行，
/// 因為製作清單視窗開著時每幀都會問）。
/// 只記錄，判斷照舊用常數。只在框架執行緒呼叫；物件與角色位址只在呼叫當幀使用。
/// </summary>
internal static unsafe class BellInteractRangeDiag
{
    private static DateTime nextSampleAt = DateTime.MinValue;
    private static DateTime lastLogAt = DateTime.MinValue;
    private static (uint Territory, ObjectKind Kind, bool ByConstant) lastLogged;
    private static int suppressed;
    private static bool unavailableLogged;

    /// <summary>每秒最多放行一次；呼叫端每幀都會跑，同一次掃描裡的各個鈴共用這個答案。</summary>
    internal static bool TrySample()
    {
        var now = DateTime.UtcNow;
        if (now < nextSampleAt) return false;
        nextSampleAt = now.AddSeconds(1);
        return true;
    }

    internal static void Compare(IGameObject bell, string site)
    {
        var local = Svc.Objects.LocalPlayer;
        if (local == null) return;

        var game = IsWithinGameInteractRange(local.Address, bell.Address);
        if (game == null)
        {
            if (!unavailableLogged)
            {
                unavailableLogged = true;
                Svc.Log.Information("[Artisan][互動距離] 問不到遊戲的互動距離判定，無法比對（之後同樣情況不再記；不影響判斷）。");
            }
            return;
        }

        var distance = Vector3.Distance(bell.Position, local.Position);
        var limit = RetainerInfo.GetValidInteractionDistance(bell);
        var byConstant = distance < limit;
        if (game.Value == byConstant) return;

        var signature = ((uint)Svc.ClientState.TerritoryType, bell.ObjectKind, byConstant);
        var now = DateTime.UtcNow;
        if (now - lastLogAt < TimeSpan.FromSeconds(signature == lastLogged ? 300 : 30))
        {
            suppressed++;
            return;
        }

        lastLogAt = now;
        lastLogged = signature;
        Svc.Log.Information($"[Artisan][互動距離] 傳喚鈴判定不同（只記錄，照常數判斷）：常數={Reach(byConstant)}（{distance:F2} 米，門檻 {limit:F2} 米，{bell.ObjectKind}），遊戲={Reach(game.Value)}；高低差 {bell.Position.Y - local.Position.Y:F2} 米，區域 {Svc.ClientState.TerritoryType}，位置 {site}，上一行之後另有 {suppressed} 次不同。");
        suppressed = 0;
    }

    private static string Reach(bool reachable) => reachable ? "搆得到" : "搆不到";

    /// <returns>遊戲的答案；<see langword="null"/>＝問不到（位址為零、函式沒解析到，或單例還沒建立）。</returns>
    private static bool? IsWithinGameInteractRange(nint source, nint target)
    {
        if (source == 0 || target == 0) return null;
        if (EventFramework.MemberFunctionPointers.CheckInteractRange is null) return null;

        try
        {
            var framework = EventFramework.Instance();
            if (framework == null) return null;

            return framework->CheckInteractRange((GameObject*)source, (GameObject*)target, 1, false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
