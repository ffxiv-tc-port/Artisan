using ECommons.DalamudServices;
using System.Collections.Generic;
using Condition = Artisan.CraftingLogic.CraftData.Condition;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers;

// Wraps a fixed plan (a Raphael-generated macro) with opportunistic condition awareness.
//
// A macro plays back a fixed action list, so on its own it cannot react to Good/Excellent/Poor showing up.
// Raphael's plan is optimal against a deterministic model where CP, durability and progress are all budgeted
// exactly, so deviating from it is dangerous: an inserted step burns one turn off *every* turn-based buff
// (innovation, veneration, manipulation, waste not, great strides, muscle memory...) and can easily cost more
// than the condition multiplier gains.
//
// The rule here is therefore "simulate before deviating": every candidate is played out against the simulator,
// with the *remainder of the original plan* replayed on top of it, and it is only adopted when the craft still
// completes and the final quality is strictly higher than sticking to the plan. If the plan itself does not
// simulate to a completed craft we never deviate at all - deviating on a model we already know is off is worse
// than useless.
//
// Note: actions that consume 能工巧匠圖紙 (careful observation / heart and soul / quick innovation) are
// deliberately never proposed - they cost the player a real consumable, which is not ours to spend.
public class OpportunisticSolver : Solver, ICraftValidator
{
    private const int RolloutStepLimit = 200;

    private Solver _plan;

    public Solver Inner => _plan;

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
        if (!P.Config.RaphaelSolverConfig.OpportunisticDeviation || !IsInterestingCondition(step.Condition))
            return _plan.Solve(craft, step);

        // work out what the plan wants to do without disturbing it yet
        var planClone = _plan.Clone();
        var planned = planClone.Solve(craft, step);
        if (planned.Action == Skills.None)
            return _plan.Solve(craft, step);

        // baseline: follow the plan from here to the end of the craft
        var baseline = Rollout(planClone, craft, step, planned.Action);
        if (baseline == null || baseline.Progress < craft.CraftProgress)
            return _plan.Solve(craft, step); // plan does not simulate cleanly - do not gamble on top of that

        var bestAction = planned.Action;
        var bestConsumesPlanStep = true;
        var best = baseline;

        foreach (var (candidate, consumesPlanStep) in Candidates(step, planned.Action))
        {
            if (candidate == planned.Action && consumesPlanStep)
                continue;
            if (!Simulator.CanUseAction(craft, step, candidate))
                continue;

            var solver = _plan.Clone();
            if (consumesPlanStep)
                solver.Solve(craft, step); // the candidate replaces this plan step, so burn it

            var outcome = Rollout(solver, craft, step, candidate);
            if (outcome == null || outcome.Progress < craft.CraftProgress)
                continue; // deviation breaks the craft
            if (outcome.Quality <= best.Quality)
                continue; // no better than what we already have

            best = outcome;
            bestAction = candidate;
            bestConsumesPlanStep = consumesPlanStep;
        }

        if (bestConsumesPlanStep)
            _plan.Solve(craft, step); // commit: the plan step was used (either as-is or substituted)

        if (bestAction == planned.Action)
            return planned;

        Svc.Log.Information($"[Opportunistic] {step.Condition} 狀態偏離原計畫：{planned.Action} -> {bestAction} " +
            $"({(bestConsumesPlanStep ? "取代該步" : "額外插入一步")})，模擬最終品質 {baseline.Quality} -> {best.Quality}");
        return new(bestAction, $"{planned.Comment} (偏離：{step.Condition} 預估品質 +{best.Quality - baseline.Quality})");
    }

    private static bool IsInterestingCondition(Condition c) => c is Condition.Good or Condition.Excellent or Condition.Poor;

    // (action, whether taking it consumes the current plan step)
    private static IEnumerable<(Skills, bool)> Candidates(StepState step, Skills planned)
    {
        if (step.Condition is Condition.Good or Condition.Excellent)
        {
            // substitute in the condition-gated upgrades - costs no extra turn, so no buff is burned
            if (IsUpgradeableQuality(planned))
                yield return (Skills.PreciseTouch, true);
            if (IsUpgradeableProgress(planned))
                yield return (Skills.IntensiveSynthesis, true);

            // or spend an extra turn to bank the condition multiplier / the free CP, then resume the plan
            yield return (Skills.PreciseTouch, false);
            yield return (Skills.TricksOfTrade, false);
        }
        else if (step.Condition is Condition.Poor)
        {
            // burn the halved turn on something that does not care about it, then resume the plan
            if (IsQuality(planned))
                yield return (Skills.Observe, false);
        }
    }

    private static bool IsQuality(Skills s) => s is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch
        or Skills.DaringTouch or Skills.PreparatoryTouch or Skills.PreciseTouch or Skills.PrudentTouch or Skills.TrainedFinesse
        or Skills.RefinedTouch or Skills.ByregotsBlessing or Skills.DelicateSynthesis;

    private static bool IsUpgradeableQuality(Skills s) => s is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch
        or Skills.HastyTouch or Skills.PreparatoryTouch or Skills.PrudentTouch or Skills.TrainedFinesse or Skills.RefinedTouch;

    private static bool IsUpgradeableProgress(Skills s) => s is Skills.BasicSynthesis or Skills.CarefulSynthesis
        or Skills.PrudentSynthesis or Skills.Groundwork or Skills.RapidSynthesis;

    // take 'first', then let 'solver' play out whatever it has left until the craft ends
    private static StepState? Rollout(Solver solver, CraftState craft, StepState step, Skills first)
    {
        var (res, cur) = Simulator.Execute(craft, step, first, 0, 1);
        if (res == Simulator.ExecuteResult.CantUse)
            return null;

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
        }
        return cur;
    }
}
