using Artisan.CraftingLists;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using System.Collections.Generic;
using System.Linq;
using Condition = Artisan.CraftingLogic.CraftData.Condition;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers;

public class MacroSolverDefinition : ISolverDefinition
{
    public string MouseoverDescription { get; set; } = "This is the equivalent of an in-game macro, with less restrictions.".Loc();

    public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
    {
        foreach (var m in P.Config.MacroSolverConfig.Macros)
        {
            if (m.Steps.Count == 0) continue;

            var statsOk = m.Options.MinCraftsmanship <= craft.StatCraftsmanship && m.Options.MinControl <= craft.StatControl && m.Options.MinCP <= craft.StatCP;
            yield return new(this, m.ID, 0, "Macro: ??".Loc(m.Name), statsOk ? "" : "You do not meet the minimum stats for this macro".Loc());
        }
    }

    public Solver Create(CraftState craft, int flavour) => new MacroSolver(P.Config.MacroSolverConfig.FindMacro(flavour) ?? new(), craft);
}

public class MacroSolver : Solver, ICraftValidator
{
    private MacroSolverSettings.Macro _macro;
    private Solver? _fallback;
    private int _nextStep;

    /// 品質到這個值以上就跳過剩下的純品質步驟(0＝不跳)。只由程式設定、不進巨集設定檔 ⇒ 快取裡的舊解也吃得到。
    /// 與 <see cref="MacroSolverSettings.MacroOptions.SkipQualityIfMet"/>(門檻是第三突破點)是兩回事。
    public int SkipQualityAtOrAbove { get; init; }
    private bool _skipUnchecked; // 驗證跳過用的分身:不再巢狀驗證
    private const int SkipCheckStepLimit = 100;

    public MacroSolver(MacroSolverSettings.Macro m, CraftState craft)
    {
        _macro = m;
        _fallback = CraftingProcessor.GetAvailableSolversForRecipe(craft, false, typeof(MacroSolverDefinition)).MaxBy(f => f.Priority).CreateSolver(craft);
    }

    public override Solver Clone()
    {
        var res = (MacroSolver)MemberwiseClone();
        res._fallback = _fallback?.Clone();
        return res;
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        var fallback = _fallback?.Solve(craft, step); // note: we need to call it, even if we provide rec from macro, to ensure fallback solver's state is updated

        while (_nextStep < _macro.Steps.Count)
        {
            var s = _macro.Steps[_nextStep++];
            var action = s.Action;
            if ((s.ExcludeNormal && step.Condition == Condition.Normal) ||
                (s.ExcludeGood && step.Condition == Condition.Good) ||
                (s.ExcludePoor && step.Condition == Condition.Poor) ||
                (s.ExcludeExcellent && step.Condition == Condition.Excellent) ||
                (s.ExcludeCentered && step.Condition == Condition.Centered) ||
                (s.ExcludeSturdy && step.Condition == Condition.Sturdy) ||
                (s.ExcludePliant && step.Condition == Condition.Pliant) ||
                (s.ExcludeMalleable && step.Condition == Condition.Malleable) ||
                (s.ExcludePrimed && step.Condition == Condition.Primed) ||
                (s.ExcludeGoodOmen && step.Condition == Condition.GoodOmen))
            {
                if (s.ReplaceOnExclude)
                {
                    action = s.ReplacementAction;
                }
                else
                {
                    continue;
                }
            }

            if (_macro.Options.SkipQualityIfMet && step.Quality >= craft.CraftQualityMin3 && ActionIsQuality(action))
            {
                continue;
            }

            if (SkipQualityAtOrAbove > 0 && step.Quality >= SkipQualityAtOrAbove && IsSpentOnlyOnQuality(action) && (_skipUnchecked || SkipStillFinishes(craft, step)))
            {
                continue;
            }

            if (_macro.Options.SkipObservesIfNotPoor && step.Condition != Condition.Poor && action is Skills.Observe or Skills.CarefulObservation)
            {
                continue;
            }

            if (P.Config.SkipMacroStepIfUnable && !Simulator.CanUseAction(craft, step, action))
            {
                continue;
            }


            if (action == Skills.TouchCombo)
            {
                action = Simulator.NextTouchCombo(step, craft);
            }

            if (action == Skills.TouchComboRefined)
            {
                action = Simulator.NextTouchComboRefined(step, craft);
            }

            if (action == Skills.None)
            {
                action = fallback?.Action ?? Skills.None;
            }

            if (!s.ExcludeFromUpgrade && step.Condition is Condition.Good or Condition.Excellent)
            {
                if (_macro.Options.UpgradeQualityActions && ActionIsUpgradeableQuality(action) && Simulator.CanUseAction(craft, step, Skills.PreciseTouch))
                {
                    action = Skills.PreciseTouch;
                }
                if (_macro.Options.UpgradeProgressActions && ActionIsUpgradeableProgress(action) && Simulator.CanUseAction(craft, step, Skills.IntensiveSynthesis))
                {
                    action = Skills.IntensiveSynthesis;
                }
            }

            return new(action, $"{_nextStep}/{_macro.Steps.Count}");
        }

        // we've run out of macro steps, see if we can use solver to continue
        // TODO: this is not a very good condition, it depends on external state...
        if (!P.Config.DisableMacroArtisanRecommendation || CraftingListUI.Processing)
            return new(fallback?.Action ?? Skills.None, fallback?.Action is null ? $"Macro has completed, the fallback solver is not working{(craft.UnlockedManipulation ? " " : " (You need to unlock Manipulation) ")}so you will have to manually finish this" : "Macro has completed. Now continuing with solver.");
        return new(Skills.None, "Macro has completed. Please continue to manually craft.");
    }

    private static bool ActionIsQuality(Skills skill) => skill is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch or Skills.PreparatoryTouch
        or Skills.PreciseTouch or Skills.PrudentTouch or Skills.TrainedFinesse or Skills.ByregotsBlessing or Skills.GreatStrides or Skills.Innovation or Skills.TouchCombo or Skills.TouchComboRefined
        or Skills.DaringTouch or Skills.RefinedTouch or Skills.QuickInnovation;

    // 觀察／專心致志只在「下一步就是會被跳過的加工」時跟著跳:前者是為了上級加工,後者是為了集中加工且會白耗能工巧匠圖紙。
    // 工匠的絕技、儉約、掌握等照留:跳過的加工不吃它們,效果留給之後的進展步驟。
    private bool IsSpentOnlyOnQuality(Skills action) => action switch
    {
        Skills.Observe => NextPlannedAction() is Skills.AdvancedTouch,
        Skills.HeartAndSoul => NextPlannedAction() is Skills.PreciseTouch,
        _ => ActionIsQuality(action),
    };

    // 跳掉的闊步／改革等不耗耐久的步驟原本是掌握回耐久的回合,跳掉後下一個進展可能把耐久打穿。
    // ⇒ 先用模擬器把「從這裡開始照跳」走完,做不完就照原計畫放這一步。
    private bool SkipStillFinishes(CraftState craft, StepState step)
    {
        var probe = (MacroSolver)Clone();
        probe._skipUnchecked = true;
        var cur = step;
        for (var guard = 0; Simulator.Status(craft, cur) == Simulator.CraftStatus.InProgress; ++guard)
        {
            var action = guard < SkipCheckStepLimit ? probe.Solve(craft, cur).Action : Skills.None;
            if (action == Skills.None)
                return false;
            var (res, next) = Simulator.Execute(craft, cur, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return false;
            cur = next;
        }
        return cur.Progress >= craft.CraftProgress;
    }

    private Skills NextPlannedAction() => _nextStep < _macro.Steps.Count ? _macro.Steps[_nextStep].Action : Skills.None;

    private static bool ActionIsUpgradeableQuality(Skills skill) => skill is Skills.HastyTouch or Skills.PreparatoryTouch or Skills.AdvancedTouch or Skills.StandardTouch or Skills.BasicTouch;
    private static bool ActionIsUpgradeableProgress(Skills skill) => skill is Skills.Groundwork or Skills.PrudentSynthesis or Skills.CarefulSynthesis or Skills.BasicSynthesis;

    public bool Validate(CraftState craft)
    {
        if(_macro.Options.ExactCraftsmanship != 0)
        {
            return _macro.Options.ExactCraftsmanship == craft.StatCraftsmanship && _macro.Options.MinControl <= craft.StatControl && _macro.Options.MinCP <= craft.StatCP;
        }


        return _macro.Options.MinCraftsmanship <= craft.StatCraftsmanship && _macro.Options.MinControl <= craft.StatControl && _macro.Options.MinCP <= craft.StatCP;
    }
}
