using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using Condition = Artisan.CraftingLogic.CraftData.Condition;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers;

// Wraps a fixed plan (a Raphael-generated macro) with opportunistic condition awareness.
// The rule is "simulate before deviating": every candidate is played out against the simulator with the
// *remainder of the original plan* replayed on top of it, and is only adopted when the craft still completes
// with strictly higher final quality (or, with reordering on, equal quality in fewer steps). If the plan itself does not simulate to completion we never deviate.
// Actions that consume 能工巧匠圖紙 (careful observation / heart and soul / quick innovation) are never proposed: a real consumable is not ours to spend.
public class OpportunisticSolver : Solver, ICraftValidator
{
    private const int RolloutStepLimit = 200;

    private Solver _plan;

    public Solver Inner => _plan;

    // 保進展的說明由呼叫端讀走再記 log：同一條鏈也在奇蹟之材閘門的試算與配方視窗的提示取樣裡跑，那些不是實機。
    private string _rescueNote = "";

    /// <summary>上次讀走之後保進展跳過了哪些步驟；沒有回空字串。讀走即清。</summary>
    public string ConsumeRescueNote()
    {
        var note = _rescueNote;
        _rescueNote = "";
        return note;
    }

    public OpportunisticSolver(Solver plan) => _plan = plan;

    public override Solver Clone()
    {
        var res = (OpportunisticSolver)MemberwiseClone();
        res._plan = _plan.Clone();
        return res;
    }

    public bool Validate(CraftState craft) => _plan is not ICraftValidator v || v.Validate(craft);

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (P.Config.RaphaelSolverConfig.ProgressRescue && _plan is MacroSolver macro)
            SkipQualityToFinish(craft, step, macro);

        if (!P.Config.RaphaelSolverConfig.OpportunisticDeviation || !IsInterestingCondition(step.Condition))
            return _plan.Solve(craft, step);

        // work out what the plan wants to do without disturbing it yet
        var planClone = _plan.Clone();
        var planned = planClone.Solve(craft, step);
        if (planned.Action == Skills.None)
            return _plan.Solve(craft, step);

        // baseline: follow the plan from here to the end of the craft
        if (Rollout(planClone.Clone(), craft, step, planned.Action) is not { } baseRun || baseRun.Final.Progress < craft.CraftProgress)
            return _plan.Solve(craft, step); // plan does not simulate cleanly - do not gamble on top of that

        var bestAction = planned.Action;
        var bestConsumesPlanStep = true;
        var bestPull = -1;
        var bestQuality = EffectiveQuality(craft, baseRun.Final);
        var bestActions = baseRun.Actions;

        foreach (var (candidate, consumesPlanStep, pull) in Candidates(craft, step, planned.Action, planClone))
        {
            if (candidate == planned.Action && consumesPlanStep)
                continue;
            if (!Simulator.CanUseAction(craft, step, candidate))
                continue;

            var solver = _plan.Clone();
            if (pull >= 0)
                ((MacroSolver)solver).Pull(pull);
            else if (consumesPlanStep)
                solver.Solve(craft, step); // the candidate replaces this plan step, so burn it

            if (Rollout(solver, craft, step, candidate) is not { } run || run.Final.Progress < craft.CraftProgress)
                continue; // deviation breaks the craft
            // 最終品質(夾上限)嚴格較高才偏離;開了局部重排時,品質相同而剩餘步數較少的也勝(品質提前滿＝之後的品質步驟被跳過)。
            var outcomeQuality = EffectiveQuality(craft, run.Final);
            if (outcomeQuality < bestQuality || outcomeQuality == bestQuality && (!P.Config.RaphaelSolverConfig.OpportunisticReorder || run.Actions >= bestActions))
                continue;
            if (outcomeQuality == bestQuality && !FinishesWithoutQualitySkip(craft, step, candidate, consumesPlanStep, pull))
                continue;

            bestQuality = outcomeQuality;
            bestActions = run.Actions;
            bestAction = candidate;
            bestConsumesPlanStep = consumesPlanStep;
            bestPull = pull;
        }

        var bestDrop = -1;
        if (P.Config.RaphaelSolverConfig.OpportunisticTradeTouch && step.Condition is Condition.Good or Condition.Excellent && _plan is MacroSolver plan
            && bestQuality < craft.CraftQualityMax)
        {
            foreach (var (candidate, pull, drop) in TradeCandidates(craft, step, planned.Action, plan, planClone as MacroSolver))
            {
                var solver = (MacroSolver)plan.Clone();
                if (pull >= 0)
                    solver.Pull(pull);
                solver.Pull(drop);
                if (Rollout(solver, craft, step, candidate) is not { } run || run.Final.Progress < craft.CraftProgress)
                    continue;
                var outcomeQuality = EffectiveQuality(craft, run.Final);
                if (outcomeQuality <= bestQuality)
                    continue;
                bestQuality = outcomeQuality;
                bestActions = run.Actions;
                bestAction = candidate;
                bestConsumesPlanStep = false;
                bestPull = pull;
                bestDrop = drop;
            }
        }

        var droppedAction = bestDrop >= 0 ? ((MacroSolver)_plan).Pull(bestDrop) : Skills.None;
        if (bestPull >= 0)
            ((MacroSolver)_plan).Pull(bestPull);
        else if (bestConsumesPlanStep)
            _plan.Solve(craft, step); // commit: the plan step was used (either as-is or substituted)

        if (bestAction == planned.Action && bestPull < 0 && bestDrop < 0)
            return planned;

        var baseQuality = EffectiveQuality(craft, baseRun.Final);
        var how = bestPull >= 0 ? $"提前計畫第 {bestPull + 1} 步、{planned.Action} 順延" : bestConsumesPlanStep ? "取代該步" : "額外插入一步";
        if (bestDrop >= 0)
            how += $"，拿掉計畫第 {bestDrop + 1} 步的 {droppedAction} 換出 CP／耐久";
        Svc.Log.Information($"[Opportunistic] {step.Condition} 狀態偏離原計畫：{planned.Action} -> {bestAction} " +
            $"({how})，模擬最終品質 {baseQuality} -> {bestQuality}、剩餘步數 {baseRun.Actions} -> {bestActions}");
        return new(bestAction, $"{planned.Comment} (偏離：{step.Condition} 預估品質 +{bestQuality - baseQuality})");
    }

    // 🔴 Simulator 的品質**從不封頂**(Simulator.Execute 只是 step.Quality + CalculateQuality,
    //    只有實機那條路才在 Crafting.cs 用 Math.Min 夾回 CraftQualityMax)。直接拿原始 Quality 比大小的話,
    //    兩條都已經滿品質的路會因為溢出值誰比較大而被判成「更好」,白白花掉一回合。
    //    比較前一律夾到遊戲真正會記錄的上限。
    // ⚠️ CraftQualityMax 為 0 的配方(完全不看品質)不夾。
    private static int EffectiveQuality(CraftState craft, StepState s)
        => craft.CraftQualityMax > 0 ? Math.Min(s.Quality, craft.CraftQualityMax) : s.Quality;

    private static bool IsInterestingCondition(Condition c) => c is Condition.Good or Condition.Excellent or Condition.Poor;

    // (action, whether taking it consumes the current plan step, plan index pulled forward or -1)
    // 🔑 局部重排:高品質／最高品質把最近的品質步驟提前、原本這步順延(最高品質之後一定是低品質);
    //    低品質把最近的非品質步驟提前、加工延到下一步(低品質之後一定是通常)。可不可行一律交給 rollout。
    private static IEnumerable<(Skills, bool, int)> Candidates(CraftState craft, StepState step, Skills planned, Solver planAfterThisStep)
    {
        var plan = P.Config.RaphaelSolverConfig.OpportunisticReorder ? planAfterThisStep as MacroSolver : null;
        if (step.Condition is Condition.Good or Condition.Excellent)
        {
            // substitute in the condition-gated upgrades - costs no extra turn, so no buff is burned
            if (IsUpgradeableQuality(planned))
                yield return (Skills.PreciseTouch, true, -1);
            if (IsUpgradeableProgress(planned))
                yield return (Skills.IntensiveSynthesis, true, -1);

            // 原本這步是補耐久時,提前的加工本身要耐久夠(否則等於把補耐久延到耐久打穿之後)。
            if (plan != null && !GainsQuality(craft, step, planned)
                && plan.FindPlanned(plan.NextIndex, a => GainsQuality(craft, step, a)) is var touch and >= 0
                && !(IsRepair(planned) && step.Durability <= Simulator.GetDurabilityCost(step, plan.PlannedAt(touch))))
                yield return (plan.PlannedAt(touch), false, touch);

            // or spend an extra turn to bank the condition multiplier / the free CP, then resume the plan
            yield return (Skills.PreciseTouch, false, -1);
            yield return (Skills.TricksOfTrade, false, -1);
        }
        else if (step.Condition is Condition.Poor)
        {
            if (plan != null && GainsQuality(craft, step, planned)
                && plan.FindPlanned(plan.NextIndex, a => !GainsQuality(craft, step, a) && !Simulator.SkipUpdates(a)) is var other and >= 0)
                yield return (plan.PlannedAt(other), false, other);

            // burn the halved turn on something that does not care about it, then resume the plan
            if (IsQuality(planned))
                yield return (Skills.Observe, false, -1);
        }
    }

    /// <summary>
    /// 高品質／最高品質想放加工但計畫的 CP、耐久剛好用完（照放就收不了尾）時：放這一下，同時拿掉之後一個純品質步驟。
    /// (動作, 提前的計畫索引或 -1, 拿掉的計畫索引)。只在最終品質嚴格較高時採用，可不可行交給 rollout。
    /// </summary>
    private static IEnumerable<(Skills, int, int)> TradeCandidates(CraftState craft, StepState step, Skills planned, MacroSolver plan, MacroSolver? afterThisStep)
    {
        var from = afterThisStep?.NextIndex ?? plan.NextIndex;
        var touch = !GainsQuality(craft, step, planned) ? plan.FindPlanned(from, a => GainsQuality(craft, step, a)) : -1;
        var drops = 0;
        for (var j = plan.FindPlanned(from, plan.SpentOnlyOnQuality); j >= 0 && drops < MaxTradeDrops; j = plan.FindPlanned(j + 1, plan.SpentOnlyOnQuality))
        {
            if (j == touch)
                continue;
            ++drops;
            yield return (Skills.PreciseTouch, -1, j);
            if (touch >= 0 && !(IsRepair(planned) && step.Durability <= Simulator.GetDurabilityCost(step, plan.PlannedAt(touch))))
                yield return (plan.PlannedAt(touch), touch, j);
        }
    }

    // 每個高品質／最高品質最多 2×12 次 rollout（實機每場只有幾次這種狀態；配方視窗的提示取樣也走這裡）。
    private const int MaxTradeDrops = 12;

    // 平手時步數少常常是靠「品質提前滿、之後的品質步驟被跳過」省下的 CP;實機狀態不如預期、品質沒提前滿時
    // 那些步驟照放,CP 就不夠收尾。⇒ 平手勝出前,先確認不跳過品質步驟也做得完。
    private bool FinishesWithoutQualitySkip(CraftState craft, StepState step, Skills candidate, bool consumesPlanStep, int pull)
    {
        if (_plan is not MacroSolver macro)
            return true;
        var solver = macro.CloneWithoutQualitySkip();
        if (pull >= 0)
            solver.Pull(pull);
        else if (consumesPlanStep)
            solver.Solve(craft, step);
        return Rollout(solver, craft, step, candidate) is { } run && run.Final.Progress >= craft.CraftProgress;
    }

    private static bool GainsQuality(CraftState craft, StepState step, Skills s) => Simulator.CalculateQuality(craft, step, s) > 0;
    private static bool IsRepair(Skills s) => s is Skills.Manipulation or Skills.MastersMend or Skills.ImmaculateMend;

    private static bool IsQuality(Skills s) => s is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch
        or Skills.DaringTouch or Skills.PreparatoryTouch or Skills.PreciseTouch or Skills.PrudentTouch or Skills.TrainedFinesse
        or Skills.RefinedTouch or Skills.ByregotsBlessing or Skills.DelicateSynthesis;

    private static bool IsUpgradeableQuality(Skills s) => s is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch
        or Skills.HastyTouch or Skills.PreparatoryTouch or Skills.PrudentTouch or Skills.TrainedFinesse or Skills.RefinedTouch;

    private static bool IsUpgradeableProgress(Skills s) => s is Skills.BasicSynthesis or Skills.CarefulSynthesis
        or Skills.PrudentSynthesis or Skills.Groundwork or Skills.RapidSynthesis;

    /// <summary>
    /// 保進展：計畫下一步是純品質步驟，而剩餘計畫照通常狀態（資源最緊的情況）做不完進展、跳過剩下的純品質步驟卻做得完
    /// ⇒ 跳過這一步（一次一步，下一步重判；做得完就照原計畫）。跳過也做不完就不動：那時放棄品質救不回來。
    /// </summary>
    private void SkipQualityToFinish(CraftState craft, StepState step, MacroSolver macro)
    {
        for (var guard = 0; guard < RolloutStepLimit; ++guard)
        {
            var probe = (MacroSolver)macro.Clone();
            var next = probe.Solve(craft, step).Action;
            if (next == Skills.None || !probe.SpentOnlyOnQuality(next))
                return;
            if (Rollout(probe, craft, step, next) is { } run && run.Final.Progress >= craft.CraftProgress)
                return;
            if (Rollout(macro.CloneSkippingAllQuality(), craft, step, null) is not { } rescue || rescue.Final.Progress < craft.CraftProgress)
                return;
            macro.Solve(craft, step); // 跳過這一步
            _rescueNote += $"第 {step.Index} 步：照剩下的計畫走，進展會做不完（耐久或 CP 不夠），跳過 {next} 先保住進展"
                + $"（目前 進展 {step.Progress}/{craft.CraftProgress}、品質 {step.Quality}、耐久 {step.Durability}、CP {step.RemainingCP}）。";
        }
    }

    // take 'first' (null: none), then let 'solver' play out whatever it has left until the craft ends
    private static (StepState Final, int Actions)? Rollout(Solver solver, CraftState craft, StepState step, Skills? first)
    {
        var cur = step;
        var actions = 0;
        if (first is { } f)
        {
            (var res, cur) = Simulator.Execute(craft, step, f, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return null;
            actions = 1;
        }

        for (var guard = 0; Simulator.Status(craft, cur) == Simulator.CraftStatus.InProgress; ++guard)
        {
            if (guard >= RolloutStepLimit)
                return null; // solver is not converging - treat as a failure rather than hanging the game thread

            var action = solver.Solve(craft, cur).Action;
            if (action == Skills.None)
                return null;

            var (r, next) = Simulator.Execute(craft, cur, action, 0, 1);
            if (r == Simulator.ExecuteResult.CantUse)
                return null;
            cur = next;
            ++actions;
        }
        return (cur, actions);
    }
}
