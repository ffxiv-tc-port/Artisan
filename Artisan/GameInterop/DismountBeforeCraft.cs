using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace Artisan.GameInterop;

/// <summary>
/// 耐久模式與製作清單開工前先下坐騎（別的插件經 IPC 要求製作也走耐久模式）：坐騎上開工遊戲會拒絕，十秒內連錯五次就停用。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒呼叫。等滿上限仍下不來就放手、照原本流程開工（遊戲拒絕照常計入連錯，由既有的連錯停止收尾）；
/// 下了坐騎、或開始新的一輪製作（<see cref="Reset"/>）才會再試。
/// 用一般動作 23（跳下）：沒騎乘時送它沒有作用，不像 9（隨機坐騎）旗標慢一拍時重送會再騎上去。
/// </remarks>
internal static unsafe class DismountBeforeCraft
{
    // 間隔放長：放手後照原流程跑的那段不會被當成新的一輪；新的一輪靠 Reset()。
    private static readonly DismountWait wait = new(30_000);

    /// <summary>開始新的一輪製作（耐久模式開啟、製作清單開始）時呼叫。</summary>
    internal static void Reset() => wait.Reset();

    /// <summary>騎乘中、而且這一輪還沒放手：遊戲的拒絕訊息是騎乘造成的，下一幀會先下坐騎，不該算進連錯。</summary>
    internal static bool StillTrying => Svc.Condition[ConditionFlag.Mounted] && !wait.GaveUp;

    /// <summary>true＝還在下坐騎，呼叫端這一幀不要往下開工。</summary>
    internal static bool Hold(string who)
    {
        var mounted = Svc.Condition[ConditionFlag.Mounted];
        var am = mounted ? ActionManager.Instance() : null;
        var canPress = am != null && am->GetActionStatus(ActionType.GeneralAction, 23) == 0;
        var step = wait.Poll(Environment.TickCount64, mounted, canPress);

        if (wait.StartedRound)
            Svc.Log.Information($"[Artisan] {who}：人在坐騎上，先下坐騎再開始製作（最多等 {DismountWait.TimeoutMs / 1000} 秒）。");

        switch (step)
        {
            case DismountWait.Step.Proceed:
                if (wait.FinishedWaiting)
                    Svc.Log.Information($"[Artisan] {who}：已下坐騎（送了 {wait.Presses} 次「跳下」、等了 {wait.ElapsedMs / 1000.0:0.0} 秒），開始製作。");
                return false;
            case DismountWait.Step.Press:
                var accepted = am->UseAction(ActionType.GeneralAction, 23);
                Svc.Log.Debug($"[Artisan] {who}：送出「跳下」第 {wait.Presses} 次（遊戲回傳 {accepted}）。");
                return true;
            case DismountWait.Step.Wait:
                return true;
            case DismountWait.Step.GiveUp:
                Svc.Log.Information($"[Artisan] {who}：等了 {wait.ElapsedMs / 1000.0:0.0} 秒仍在坐騎上（送了 {wait.Presses} 次「跳下」），"
                                    + "照原本流程開始製作；遊戲若拒絕會照常計入連續錯誤。");
                return false;
            default:
                return false;
        }
    }
}
