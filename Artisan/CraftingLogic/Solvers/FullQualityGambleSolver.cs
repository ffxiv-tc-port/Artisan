using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation.Character;
using System;

namespace Artisan.CraftingLogic.Solvers;

/// <summary>
/// 高難+ 按品質計分的成品：Raphael 的確定解（照順序、通常狀態）做不到滿品質時，改由專家解算器接手整場
/// （奇蹟之材＋倉促／冒進／急速製作等會失敗的技能）衝滿品質。這種成品每件滿品質才拿 1000、金星要每件都滿。
/// 第一步才決定：開工時有奇蹟之材可用才換（沒有的話專家解算器在這類配方上明顯較差），否則整場照 Raphael 全鏈。
/// 契約：只讀遊戲已解析的狀態、不碰 log（說明由呼叫端讀走再記，同 MaterialMiracleSolver）；可被複製去做模擬。
/// </summary>
public class FullQualityGambleSolver : Solver, ICraftValidator
{
    private enum Mode { Undecided, Chain, Gamble }

    private Solver _chain;
    private readonly int _planQuality;
    private readonly int _cap;
    private readonly int _items;
    private Mode _mode;
    private int _budget;
    private int _used;
    private bool _wasActive;
    private CraftState? _craftIn, _craftOut;
    private string _note = "";

    public FullQualityGambleSolver(Solver chain, int planQuality, int cap, int items)
    {
        _chain = chain;
        _planQuality = planQuality;
        _cap = cap;
        _items = items;
    }

    /// <summary>照 Raphael 全鏈走（還沒決定或決定不換）時的那條鏈；換成專家解算器後回 null。</summary>
    public Solver? Chain => _mode == Mode.Gamble ? null : _chain;

    public override Solver Clone()
    {
        var res = (FullQualityGambleSolver)MemberwiseClone();
        res._chain = _chain.Clone();
        return res;
    }

    public bool Validate(CraftState craft) => _chain is not ICraftValidator v || v.Validate(craft);

    /// <summary>上次讀走之後的切換說明；沒有回空字串。讀走即清。</summary>
    public string ConsumeNote()
    {
        var note = _note;
        _note = "";
        return note;
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (_mode == Mode.Undecided)
            Decide(step);
        if (_mode == Mode.Chain)
            return _chain.Solve(craft, step);

        if (step.MaterialMiracleActive && !_wasActive)
            ++_used;
        _wasActive = step.MaterialMiracleActive;
        var seen = _used >= _budget && step.MaterialMiracleCharges > 0 ? step with { MaterialMiracleCharges = 0 } : step;
        var rec = ExpertSolver.SolveNextStep(Config(), SeenCraft(craft, step), seen);
        // 專家解算器只替收尾留一次模範製作的 CP，這類配方進展多，品質衝完常常耐久不夠收尾。
        // 這一步照「會失敗的技能失敗」算（成功率 100% 的照常成功）：失敗了還收得了尾才放。
        if (rec.Action != Skills.None && Simulator.CanUseAction(craft, step, rec.Action) && CanFinish(craft, step)
            && !CanFinish(craft, Simulator.Execute(craft, step, rec.Action, PessimisticRoll, 1).Item2))
            return new(Progress.Solve(craft, step).Action, "衝滿品質：再放這一步就收不了尾，先做進展");
        return new(rec.Action, $"衝滿品質：{rec.Comment}");
    }

    private static readonly ProgressOnlySolver Progress = new();
    private const int FinishCheckStepLimit = 60;
    private const float PessimisticRoll = 0.999f; // 成功判定是 roll < 成功率：1 會連必成功的技能都判失敗

    /// <summary>從這裡只做進展（必成功、狀態不變好）做得完。</summary>
    private static bool CanFinish(CraftState craft, StepState step)
    {
        var cur = step;
        for (var guard = 0; Simulator.Status(craft, cur) == Simulator.CraftStatus.InProgress; ++guard)
        {
            var action = guard < FinishCheckStepLimit ? Progress.Solve(craft, cur).Action : Skills.None;
            if (action == Skills.None)
                return false;
            var (res, next) = Simulator.Execute(craft, cur, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return false;
            cur = next;
        }
        return cur.Progress >= craft.CraftProgress;
    }

    private void Decide(StepState step)
    {
        var charges = (int)step.MaterialMiracleCharges;
        if (charges <= 0 || step.MaterialMiracleActive)
        {
            _mode = Mode.Chain;
            _note = $"Raphael 做不到滿品質（品質 {_planQuality}／上限 {_cap}），但這場沒有奇蹟之材可用，照 Raphael 的解做（專家解算器沒有奇蹟之材時在這類配方上比較差）。";
            return;
        }
        _mode = Mode.Gamble;
        // 兩件型任務的次數整個任務共用：一件用光，下一件就只能照 Raphael。
        _budget = Math.Max(1, charges / Math.Max(1, _items));
        _note = $"Raphael 做不到滿品質（品質 {_planQuality}／上限 {_cap}），改用專家解算器（奇蹟之材、倉促／冒進、急速製作）衝滿品質；"
            + $"奇蹟之材剩 {charges} 次、這件最多用 {_budget} 次（任務要 {_items} 件）。";
    }

    // 金星只看滿品質：專家解算器照使用者設定可能只做到第三門檻、或不用奇蹟之材，這裡固定追滿並使用。
    private static ExpertSolverSettings Config()
    {
        var cfg = P.Config.ExpertSolverConfig;
        if (cfg.MaxCosmicRecipes && cfg.UseMaterialMiracle)
            return cfg;
        var res = cfg.ShallowCopy();
        res.MaxCosmicRecipes = true;
        res.UseMaterialMiracle = true;
        return res;
    }

    // 非專家配方的等級表常帶著專家配方的狀態旗標（例如高效），但奇蹟之材以外只會出現通常／高品質／最高品質／低品質；
    // 照表上的旗標，專家解算器會一直觀察等高效、把 CP 耗光。
    private CraftState SeenCraft(CraftState craft, StepState step)
    {
        if (step.MaterialMiracleActive)
            return craft;
        if (!ReferenceEquals(craft, _craftIn))
        {
            _craftIn = craft;
            _craftOut = craft with { ConditionFlags = ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent | ConditionFlags.Poor };
        }
        return _craftOut!;
    }
}
