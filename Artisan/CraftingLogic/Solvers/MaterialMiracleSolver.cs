using ECommons.LanguageHelpers;
using System;
using Condition = Artisan.CraftingLogic.CraftData.Condition;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers;

// 把「奇蹟之材」包在一份固定計畫(Raphael 產生的巨集)外面的那一層。
// 為什麼固定計畫需要這一層:Raphael 只認得 通常/高品質/最高品質/低品質 四種狀態,
// 解算時全程餵 Normal,產出的是一份不會回頭看狀態的動作清單 —— 所以它永遠不會提議奇蹟之材。
// 而奇蹟之材對這種固定計畫其實**特別安全**,原因是它根本不動計畫。
// 📌 這份狀態池現在是 Simulator.MaterialMiracleConditionPool,模擬器本身就會擲它;
//    詳細出處與「機率是假設」的說明寫在那裡,不在這裡重複。
// 🔴 **唯一的真風險是提早完工**:大進展讓進度動作變 1.5 倍,計畫中段就可能把進度打滿,
//    把還沒跑完的品質階段整段截掉。所以這裡不是無條件按下去,而是先過一道閘門(見 EvaluateGate)。
public class MaterialMiracleSolver : Solver, ICraftValidator
{
    private const int RolloutStepLimit = 200;

    /// <summary>
    /// 奇蹟之材生效期間可能出現的狀態。
    /// 🔑 已經下沉到 <see cref="Simulator.MaterialMiracleConditionPool"/>,
    /// 由模擬器、提示取樣、ExpertSolver 自適應、StandardSolver 逐場閘門與這裡共用同一份。
    /// </summary>
    public static Condition[] ConditionPool => Simulator.MaterialMiracleConditionPool;

    private Solver _plan;
    private bool _usedThisCraft;
    private bool? _gate;

    public Solver Inner => _plan;

    /// <summary>
    /// 閘門最近一次判定的理由,給**呼叫端**決定要不要記 log 用。
    /// 🔴 這裡刻意**不自己呼叫 Svc.Log**,有兩個各自獨立的理由:
    ///  ① 解算器這條路是配方視窗的提示取樣器在跑的,一次提示會驅動解算器上百次模擬
    ///     (見 <see cref="SolverHintSampler"/>),在裡面記 log 會把 log 洗掉。
    ///  ② 想用「這場是不是實機製作」當閘門的話,最直覺的寫法是去讀
    ///     <c>Crafting.CurCraft</c> —— 但那會觸發 <c>Crafting</c> 的靜態建構式(它會掛遊戲 hook)。
    /// </summary>
    public string LastGateExplanation { get; private set; } = "";

    public MaterialMiracleSolver(Solver plan) => _plan = plan;

    public override Solver Clone()
    {
        var res = (MaterialMiracleSolver)MemberwiseClone();
        res._plan = _plan.Clone();
        return res;
    }

    public bool Validate(CraftState craft) => _plan is not ICraftValidator v || v.Validate(craft);

    /// <summary>
    /// 這個解算器**會不會**在有奇蹟之材的宇宙任務上用到它。給配方視窗畫提示用 ——
    /// 使用者現在完全看不出自己被靜默降級成「整場不用奇蹟之材」。
    /// 🔑 用型別判斷不用在地化過的名稱:名稱比對會被翻譯改動靜默弄壞。
    /// </summary>
    public static bool SolverUsesMaterialMiracle(ISolverDefinition.Desc desc) => desc.Def switch
    {
        StandardSolverDefinition => P.Config.UseMaterialMiracle,
        RaphaelSolverDefintion => P.Config.UseMaterialMiracle,
        ExpertSolverDefinition => P.Config.ExpertSolverConfig.UseMaterialMiracle,
        _ => false,
    };

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (step.PrevComboAction == Skills.MaterialMiracle)
        {
            // 與 StandardSolver 同一條規則:沒開「一場製作可用多次」就只用一次。
            _usedThisCraft |= !P.Config.MaterialMiracleMulti;
            // 開了多次的話,下一次要重新過閘門 —— 那時的剩餘計畫已經不一樣了。
            _gate = null;
        }

        if (ShouldOpen(craft, step))
            return new(Skills.MaterialMiracle, "Material Miracle (does not consume a step)".Loc());

        return _plan.Solve(craft, step);
    }

    private bool ShouldOpen(CraftState craft, StepState step)
    {
        // 🔴 預設關:UseMaterialMiracle 的預設值是 false,沒主動開的人行為與改動前逐字相同。
        if (!P.Config.UseMaterialMiracle || !craft.MissionHasMaterialMiracle)
            return false;
        if (_usedThisCraft || step.MaterialMiracleActive)
            return false;
        // 次數與等級由 CanUseAction 把關(實機的次數來自 DutyActionManager,模擬器來自任務資料)。
        if (!Simulator.CanUseAction(craft, step, Skills.MaterialMiracle))
            return false;

        // 🔴 閘門只算一次。呼叫端(配方視窗提示的取樣器)是**每一幀**在跑解算器的,
        //    每一步都重算兩次完整 rollout 會讓那條路慢上一個數量級。
        //    而且「這一步不划算」在後面的步數只會更不划算(剩餘視窗更短),重算沒有意義。
        _gate ??= EvaluateGate(craft, step);
        return _gate.Value;
    }

    /// <summary>
    /// 「現在按下去會不會賠掉品質」的**決定性**閘門。
    /// 做法是把剩下的計畫整段跑兩次,兩次**只差一個變數 —— 狀態**。
    /// 🔑 **模擬器在 補上狀態池之後,這裡仍然刻意釘死狀態,不改成擲骰** ——
    ///    這不是遺漏,是這道閘門的定義。
    /// </summary>
    private bool EvaluateGate(CraftState craft, StepState step)
    {
        var baseline = Rollout(_plan.Clone(), craft, step, Condition.Normal);
        var worst = Rollout(_plan.Clone(), craft, step, Condition.Malleable);

        var ok = baseline != null && worst != null
              && baseline.Progress >= craft.CraftProgress
              && worst.Progress >= craft.CraftProgress
              && EffectiveQuality(craft, worst) >= EffectiveQuality(craft, baseline);

        LastGateExplanation = ok
            ? $"第 {step.Index} 步採用奇蹟之材:剩餘計畫在「全程大進展」的最壞情況下仍然完成," +
              $"最終品質 {EffectiveQuality(craft, baseline!)} -> {EffectiveQuality(craft, worst!)}(不低於基準線)。"
            : "第 " + step.Index + " 步不採用奇蹟之材:" +
              (baseline == null || baseline.Progress < craft.CraftProgress
                  ? "剩餘計畫本身就模擬不出完整製作,不在這個基礎上加碼。"
                  : worst == null || worst.Progress < craft.CraftProgress
                      ? "「全程大進展」的最壞情況下剩餘計畫跑不完。"
                      : $"「全程大進展」會讓製作提早結束、截掉品質階段(最終品質 {EffectiveQuality(craft, baseline)} -> {EffectiveQuality(craft, worst!)})。");

        return ok;
    }

    /// <summary>
    /// 讓 <paramref name="solver"/> 把剩下的計畫跑到製作結束,過程中把狀態釘死在
    /// <paramref name="pinned"/>。釘狀態是為了讓兩次 rollout **只差一個變數**,
    /// 否則擲骰慣例(roll=1)自己會挑狀態,比出來的差異就分不清是誰造成的。
    /// </summary>
    private static StepState? Rollout(Solver solver, CraftState craft, StepState step, Condition pinned)
    {
        var cur = step with { Condition = pinned };
        for (var guard = 0; Simulator.Status(craft, cur) == Simulator.CraftStatus.InProgress; ++guard)
        {
            if (guard >= RolloutStepLimit)
                return null; // 沒收斂:當成失敗,不要把遊戲執行緒吊死
            var action = solver.Solve(craft, cur).Action;
            if (action == Skills.None)
                return null;
            var (res, next) = Simulator.Execute(craft, cur, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return null;
            cur = next with { Condition = pinned };
        }
        return cur;
    }

    // 與 OpportunisticSolver 同一個理由:Simulator 的品質**從不封頂**,
    // 兩條都已經滿品質的路會因為溢出值誰大而被判成「更好」。比較前一律夾回遊戲真正記錄的上限。
    private static int EffectiveQuality(CraftState craft, StepState s)
        => craft.CraftQualityMax > 0 ? Math.Min(s.Quality, craft.CraftQualityMax) : s.Quality;
}
