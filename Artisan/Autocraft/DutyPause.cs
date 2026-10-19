using Artisan.CraftingLists;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using System;

namespace Artisan.Autocraft
{
    /// <summary>
    /// 「排到任務就停止／離開任務再恢復」的待恢復狀態。只有 <see cref="Arm"/>（排到任務時停下正在跑的自動化）
    /// 會開始一次待恢復；外部插件的停止／恢復請求、登入登出、使用者自己動過耐力模式或清單都會作廢它，
    /// 而且只在對應的那次離開任務時恢復一次。
    /// </summary>
    /// <remarks>全部只在框架執行緒上呼叫（Condition 事件、IPC 閘門內、登入登出事件、RunOnTick）。</remarks>
    internal static class DutyPause
    {
        private enum Stage { None, Queued, InDuty, Resuming }

        // 任務確認視窗關掉到進場讀取完的上限；超過才進任務＝不是排到的那一場（例如取消排隊後找 NPC 進的任務）。
        private const long EnterWindowMs = 120_000;

        private static Stage stage;
        private static int token;
        private static long popClosedAt = long.MinValue / 2;
        private static IPC.IPC.ArtisanMode mode;
        private static int enduranceStarts;
        private static int pausedChanges;

        internal static bool AutomationRunning
            => Endurance.Enable || (CraftingListUI.Processing && !CraftingListFunctions.Paused);

        /// <summary>在「排到任務就停止」剛停下自動化之後呼叫，記下要恢復的模式。</summary>
        internal static void Arm()
        {
            token++;
            mode = IPC.IPC.CurrentMode;
            if (mode == IPC.IPC.ArtisanMode.None)
            {
                stage = Stage.None;
                return;
            }
            stage = Stage.Queued;
            enduranceStarts = Endurance.StartRequests;
            pausedChanges = CraftingListFunctions.PausedChanges;
            Svc.Log.Information($"[DutyPause] 排到任務，已停下 {mode}；離開這場任務時才可能恢復");
        }

        internal static void Cancel(string reason)
        {
            token++;
            if (stage == Stage.None)
                return;
            stage = Stage.None;
            Svc.Log.Information($"[DutyPause] 作廢待恢復的 {mode}：{reason}");
        }

        internal static void OnPopClosed() => popClosedAt = Environment.TickCount64;

        internal static void OnEnterDuty()
        {
            if (stage != Stage.Queued)
                return;

            if (Svc.Condition[ConditionFlag.WaitingForDutyFinder] || Environment.TickCount64 - popClosedAt <= EnterWindowMs)
                stage = Stage.InDuty;
            else
                Cancel("進的不是排到的那場任務");
        }

        /// <summary>離開任務時呼叫；回傳要不要排一次恢復，回 true 時 <paramref name="resumeToken"/> 給延遲的恢復用。</summary>
        internal static bool OnLeaveDuty(bool resumeEnabled, out int resumeToken)
        {
            resumeToken = 0;
            if (stage != Stage.InDuty)
                return false;

            if (!resumeEnabled)
            {
                Cancel("沒開「離開任務後恢復」");
                return false;
            }

            stage = Stage.Resuming;
            resumeToken = ++token;
            return true;
        }

        /// <summary>延遲到期時呼叫；回 true＝狀態沒被動過、可以恢復（同時結束這次待恢復）。</summary>
        internal static bool TryConsumeResume(int resumeToken)
        {
            if (stage != Stage.Resuming || resumeToken != token)
                return false;

            string? changed = null;
            if (IPC.IPC.CurrentMode != mode)
                changed = "停止時記下的模式被換掉了";
            else if (Endurance.StartRequests != enduranceStarts)
                changed = "期間有人開過耐力模式";
            else if (CraftingListFunctions.PausedChanges != pausedChanges)
                changed = "期間清單被繼續或取消過";
            else if (mode == IPC.IPC.ArtisanMode.Lists && !CraftingListUI.Processing)
                changed = "清單已經不在執行";

            if (changed != null)
            {
                Cancel(changed);
                return false;
            }

            stage = Stage.None;
            token++;
            Svc.Log.Information($"[DutyPause] 離開任務，恢復 {mode}");
            return true;
        }
    }
}
