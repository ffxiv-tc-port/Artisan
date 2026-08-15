using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using System;
using System.Collections.Generic;
using Condition = Artisan.CraftingLogic.CraftData.Condition;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers
{
    public class StandardSolverDefinition : ISolverDefinition
    {
        public string MouseoverDescription { get; set; } = "This is the normal recipe solver.".Loc();

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            if (!craft.CraftExpert && craft.CraftHQ)
                yield return new(this, 0, 2, "Standard Recipe Solver".Loc());
        }

        public Solver Create(CraftState craft, int flavour) => new StandardSolver(flavour != 0);
    }

    public class StandardSolver : Solver
    {
        private bool _expert;

        // for normal crafts, we don't ever want to use manip/wn more than once
        private bool _manipulationUsed;
        private bool _wasteNotUsed;
        private bool _qualityStarted;
        private bool _venereationUsed;
        private bool _trainedEyeUsed;
        private bool _materialMiracleUsed;

        private Solver? _fallback; //For Material Miracle

        // 這一場製作要不要在奇蹟之材期間交給 ExpertSolver 代打(只算一次,見 ShouldDelegateDuringMiracle)。
        private bool? _delegateDuringMiracle;

        /// <summary>
        /// 代打閘門最近一次判定的理由,給**呼叫端**記 log 用(讀走就清掉,一場製作只會印一次)。
        /// 🔴 這裡刻意不自己呼叫 Svc.Log:同一個解算器也被配方視窗的提示取樣器拿去跑上百次模擬
        ///    (見 <see cref="SolverHintSampler"/>),在裡面記會把 log 洗掉 —— 與
        ///    <see cref="MaterialMiracleSolver.LastGateExplanation"/> 同一個理由與同一套接線。
        /// </summary>
        public string ConsumeGateExplanation()
        {
            var res = _gateExplanation;
            _gateExplanation = "";
            return res;
        }

        private string _gateExplanation = "";

        public StandardSolver(bool expert)
        {
            _expert = expert;
            _fallback = new ExpertSolver();
        }

        public override Recommendation Solve(CraftState craft, StepState step)
        {
            var rec = GetRecommendation(craft, step);

            if (Simulator.GetDurabilityCost(step, rec.Action) == 0)
            {
                if (rec.Action != Skills.MaterialMiracle)
                {
                    if (step.Durability <= 10 && Simulator.CanUseAction(craft, step, Skills.MastersMend)) rec.Action = Skills.MastersMend;
                    if (step.Durability <= 10 && Simulator.CanUseAction(craft, step, Skills.ImmaculateMend) && craft.CraftDurability >= 70) rec.Action = Skills.ImmaculateMend;
                }
            }
            else
            {
                var stepClone = rec.Action;
                if (WillActFail(craft, step, stepClone) && Simulator.CanUseAction(craft, step, Skills.MastersMend)) rec.Action = Skills.MastersMend;
                if (WillActFail(craft, step, stepClone) && Simulator.CanUseAction(craft, step, Skills.ImmaculateMend) && craft.CraftDurability >= 70) rec.Action = Skills.ImmaculateMend;

            }

            // 🔴 原本寫成 `is not Skills.MastersMend or Skills.ImmaculateMend`,C# 解析成
            //    `(not MastersMend) or (ImmaculateMend)` —— 除了 MastersMend 恆真,
            //    比爾格祝福因此可以蓋掉剛選定的精修。意圖是「兩個修理技都不是」(對照 72 行的正面寫法)。
            if ((rec.Action is not Skills.MastersMend and not Skills.ImmaculateMend) &&
                step.Quality < craft.CraftQualityMax &&
                Simulator.CanUseAction(craft, step, Skills.ByregotsBlessing) &&
                step.RemainingCP - Simulator.GetCPCost(step, rec.Action) < Simulator.GetCPCost(step, Skills.ByregotsBlessing) &&
                !WillActFail(craft, step, Skills.ByregotsBlessing))
            {
                rec.Action = Skills.ByregotsBlessing;
            }

            if ((rec.Action is Skills.MastersMend or Skills.ImmaculateMend) &&
                step.Condition is Condition.Good or Condition.Excellent && Simulator.CanUseAction(craft, step, Skills.TricksOfTrade))
                rec.Action = Skills.TricksOfTrade;

            if (Simulator.GetDurabilityCost(step, rec.Action) == 20 && !_trainedEyeUsed && step.TrainedPerfectionAvailable && step.VenerationLeft == 0)
                rec.Action = Skills.TrainedPerfection;

            if (WillActFail(craft, step, rec.Action))
                rec.Action = Skills.BasicSynthesis;

            return rec;
        }

        private bool DelegateNow(CraftState craft, StepState step)
        {
            if (!step.MaterialMiracleActive)
                return false;
            _delegateDuringMiracle ??= ShouldDelegateDuringMiracle(craft, step);
            return _delegateDuringMiracle.Value;
        }

        // 12 次是量出來的:離線量測(artisan-sim mmdiag,每個配方 500~800 次製作)
        // 2 次時閘門在部分配方上判錯(整批平均做出來率 83.6%),8 次 94.9%,12 次 97.2%,16 次沒有再更好。
        // 成本是每場製作一次、約 0.7~1.5 ms(非專家製作,ExpertSolver 的自適應前瞻不會被觸發)。
        private const int GateRollouts = 12;
        private const int GateRolloutCap = 150;

        /// <summary>
        /// 「奇蹟之材生效期間交給專家解算器代打」對**這一場**製作是賺是賠,只在 buff 剛生效時算一次。
        ///
        /// 為什麼需要:代打不是換個解算器,是把一場製作**切成兩段**由兩個策略各打一半。
        /// 專家解算器的打法是「先把品質堆完,進度留到最後收」,而 buff 只有 21 步 ——
        /// 交還的那一刻常常是「品質很漂亮、進度還差一大截、耐久與 CP 都花光了」,
        /// 標準解算器接手後收不回來。離線量測(2026-08-15,artisan-sim mmdiag):
        /// 這件事在不同配方形狀上**兩個方向都有** —— 有的形狀代打後品質 91.3→100.0 且完成率不變,
        /// 有的形狀完成率 100%→9.5%。所以正解不是「一律代打」也不是「一律不代打」,是逐場判斷。
        ///
        /// 評分函式與 <see cref="ExpertSolver"/> 的前瞻模擬刻意用同一個:做得出來 ? 品質達成率 : 0。
        /// **做不出來給 0 分**是關鍵 —— 素材做爛的代價遠大於少幾個品質點。
        /// 兩條路用同一組亂數(common random numbers)⇒ 比的是策略差異不是運氣差異;
        /// 種子只由局面決定 ⇒ 同一個局面永遠得到同一個答案,不會每幀跳來跳去。
        /// 平手時維持代打(＝改動前的行為)。
        /// </summary>
        private bool ShouldDelegateDuringMiracle(CraftState craft, StepState step)
        {
            double withDelegate = 0, without = 0;
            for (var k = 0; k < GateRollouts; ++k)
            {
                var seed = unchecked(step.Index * 7919 + k * 104729 + step.Durability * 31 + step.RemainingCP);
                withDelegate += PolicyValue(craft, step, true, new Random(seed));
                without += PolicyValue(craft, step, false, new Random(seed));
            }
            var ok = withDelegate >= without;
            _gateExplanation = ok
                ? $"第 {step.Index} 步起的奇蹟之材期間交給專家解算器代打:前瞻模擬 {GateRollouts} 次的期望收穫 " +
                  $"{withDelegate / GateRollouts:F3} ≥ 自己打的 {without / GateRollouts:F3}。"
                : $"第 {step.Index} 步起的奇蹟之材期間**不**交給專家解算器代打:前瞻模擬 {GateRollouts} 次的期望收穫 " +
                  $"{withDelegate / GateRollouts:F3} < 自己打的 {without / GateRollouts:F3}(代打會在 buff 結束交還時留下收不回來的進度缺口)。";
            return ok;
        }

        /// 用指定的代打政策把剩下的製作跑完,回傳「做得出來 ? 品質達成率(0~1) : 0」。
        private double PolicyValue(CraftState craft, StepState step, bool delegateDuringMiracle, Random rng)
        {
            // 🔴 一定要先把政策釘進複製品,否則 Solve 又會走進這道閘門 → 無窮遞迴。
            var probe = (StandardSolver)Clone();
            probe._delegateDuringMiracle = delegateDuringMiracle;
            var cur = step;
            for (var guard = 0; Simulator.Status(craft, cur) == Simulator.CraftStatus.InProgress; ++guard)
            {
                if (guard >= GateRolloutCap)
                    return 0; // 沒收斂:當成做不出來,不要把遊戲執行緒吊死
                var action = probe.Solve(craft, cur).Action;
                if (action == Skills.None)
                    return 0;
                var (res, next) = Simulator.Execute(craft, cur, action, rng.NextSingle(), rng.NextSingle());
                if (res == Simulator.ExecuteResult.CantUse)
                    return 0;
                cur = next;
            }
            if (cur.Progress < craft.CraftProgress || craft.CraftQualityMax <= 0)
                return 0;
            return Math.Min(1.0, (double)cur.Quality / craft.CraftQualityMax);
        }

        private static bool InTouchRotation(CraftState craft, StepState step)
            => step.PrevComboAction == Skills.BasicTouch && craft.StatLevel >= Simulator.MinLevel(Skills.StandardTouch) || step.PrevComboAction == Skills.StandardTouch && craft.StatLevel >= Simulator.MinLevel(Skills.AdvancedTouch);

        public Skills BestSynthesis(CraftState craft, StepState step, bool progOnly = false)
        {
            // Need to take into account MP
            // Rapid(500/50, 0)?
            // Intensive(400, 6) > Groundwork(300, 18) > Focused(200, 5) > Prudent(180, 18) > Careful(150, 7) > Groundwork(150, 18) > Basic(120, 0)

            var remainingProgress = craft.CraftProgress - step.Progress;
            if (CalculateNewProgress(craft, step, Skills.BasicSynthesis) >= craft.CraftProgress)
            {
                return Skills.BasicSynthesis;
            }

            if (Simulator.CanUseAction(craft, step, Skills.IntensiveSynthesis))
            {
                return Skills.IntensiveSynthesis;
            }

            if (!_qualityStarted && !progOnly)
            {
                if (CalculateNewProgress(craft, step, Skills.BasicSynthesis) >= craft.CraftProgress - Simulator.BaseProgress(craft))
                    return Skills.BasicSynthesis;
            }

            if (Simulator.CanUseAction(craft, step, Skills.Groundwork) && step.Durability > Simulator.GetDurabilityCost(step, Skills.Groundwork))
            {
                return Skills.Groundwork;
            }

            if (Simulator.CanUseAction(craft, step, Skills.PrudentSynthesis))
            {
                return Skills.PrudentSynthesis;
            }

            if (Simulator.CanUseAction(craft, step, Skills.CarefulSynthesis))
            {
                return Skills.CarefulSynthesis;
            }

            if (CanSpamBasicToComplete(craft, step))
            {
                return Skills.BasicSynthesis;
            }

            return Skills.BasicSynthesis;
        }

        private static bool CanSpamBasicToComplete(CraftState craft, StepState step)
        {
            while (true)
            {
                var (res, next) = Simulator.Execute(craft, step, Skills.BasicSynthesis, 0, 1);
                if (res == Simulator.ExecuteResult.CantUse)
                    return step.Progress >= craft.CraftProgress;
                step = next;
            }
        }

        public Recommendation GetRecommendation(CraftState craft, StepState step)
        {
            var fallbackRec = _fallback.Solve(craft, step);

            _manipulationUsed |= step.PrevComboAction == Skills.Manipulation;
            _trainedEyeUsed |= step.PrevComboAction == Skills.TrainedEye;
            _wasteNotUsed |= step.PrevComboAction is Skills.WasteNot or Skills.WasteNot2;
            _qualityStarted |= step.PrevComboAction is Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch or Skills.ByregotsBlessing or Skills.PrudentTouch
                or Skills.PreciseTouch or Skills.TrainedEye or Skills.PreparatoryTouch or Skills.TrainedFinesse or Skills.Innovation;
            _venereationUsed |= step.PrevComboAction == Skills.Veneration;
            _materialMiracleUsed |= step.PrevComboAction == Skills.MaterialMiracle && !P.Config.MaterialMiracleMulti;

            if (DelegateNow(craft, step))
                return fallbackRec;

            if (P.Config.UseMaterialMiracle && !_materialMiracleUsed && Simulator.CanUseAction(craft, step, Skills.MaterialMiracle))
                return new(Skills.MaterialMiracle);

            bool inCombo = (step.PrevComboAction == Skills.BasicTouch && Simulator.CanUseAction(craft, step, Skills.StandardTouch)) || (step.PrevComboAction == Skills.StandardTouch && Simulator.CanUseAction(craft, step, Skills.AdvancedTouch));
            var act = BestSynthesis(craft, step);
            var goingForQuality = GoingForQuality(craft, step, out var maxQuality);

            if (step.Index == 1 && CanFinishCraft(craft, step, Skills.DelicateSynthesis) && CalculateNewQuality(craft, step, Skills.DelicateSynthesis) >= maxQuality && Simulator.CanUseAction(craft, step, Skills.DelicateSynthesis)) return new(Skills.DelicateSynthesis);
            if (!goingForQuality && CanFinishCraft(craft, step, act)) return new(act);

            if (Simulator.CanUseAction(craft, step, Skills.TrainedEye) && goingForQuality) return new(Skills.TrainedEye);
            if (Simulator.CanUseAction(craft, step, Skills.TricksOfTrade))
            {
                if (step.Index > 2 && (step.Condition == Condition.Good && P.Config.UseTricksGood || step.Condition == Condition.Excellent && P.Config.UseTricksExcellent))
                    return new(Skills.TricksOfTrade);

                if (step.RemainingCP < 7 ||
                    craft.StatLevel < Simulator.MinLevel(Skills.PreciseTouch) && step.Condition == Condition.Good && step.InnovationLeft == 0 && step.WasteNotLeft == 0 && !InTouchRotation(craft, step))
                    return new(Skills.TricksOfTrade);
            }

            if ((maxQuality == 0 || P.Config.MaxPercentage == 0) && !craft.CraftCollectible)
            {
                if (step.Index == 1 && Simulator.CanUseAction(craft, step, Skills.MuscleMemory)) return new(Skills.MuscleMemory);
                if (CanFinishCraft(craft, step, act)) return new(act);
                return new(act);
            }

            if (goingForQuality)
            {
                if (!P.Config.UseQualityStarter && craft.StatLevel >= Simulator.MinLevel(Skills.MuscleMemory))
                {
                    if (Simulator.CanUseAction(craft, step, Skills.MuscleMemory) && !CanFinishCraft(craft, step, Skills.MuscleMemory)) return new(Skills.MuscleMemory);

                    if (step.MuscleMemoryLeft > 0 && !CanFinishCraft(craft, step, Skills.BasicSynthesis))
                    {
                        if (craft.StatLevel < Simulator.MinLevel(Skills.IntensiveSynthesis) && step.Condition is Condition.Good or Condition.Excellent && Simulator.CanUseAction(craft, step, Skills.PreciseTouch)) return new(Skills.PreciseTouch);
                        if (Simulator.CanUseAction(craft, step, Skills.FinalAppraisal) && step.FinalAppraisalLeft == 0 && CanFinishCraft(craft, step, act)) return new(Skills.FinalAppraisal);
                        return new(act);
                    }

                    //if (!CanFinishCraft(craft, step, act) && step.VenerationLeft > 0 && step.Durability > 10)
                    //    return new(act);
                }

                if (P.Config.UseQualityStarter)
                {
                    if (Simulator.CanUseAction(craft, step, Skills.Reflect)) return new(Skills.Reflect);
                }

                if (Simulator.CanUseAction(craft, step, Skills.BasicTouch) && CalculateNewQuality(craft, step, Skills.BasicTouch) >= craft.CraftQualityMax && step.Index == 1)
                    return new(Skills.BasicTouch);

                if (Simulator.CanUseAction(craft, step, Skills.Manipulation) && step.ManipulationLeft == 0 && !_manipulationUsed) return new(Skills.Manipulation);

                if (step.Progress < craft.CraftProgress - 1 && (!_qualityStarted || !Simulator.CanUseAction(craft, step, Skills.FinalAppraisal)))
                {
                    bool canUseAct = step.Progress + Simulator.BaseProgress(craft) < craft.CraftProgress;
                    if (canUseAct)
                    {
                        bool shouldUseVeneration = CheckIfVenerationIsWorth(craft, step, act);

                        if (Simulator.CanUseAction(craft, step, Skills.Veneration) && step.VenerationLeft == 0 && shouldUseVeneration) return new(Skills.Veneration);
                        if (Simulator.CanUseAction(craft, step, Skills.WasteNot2) && step.WasteNotLeft == 0 && !_wasteNotUsed) return new(Skills.WasteNot2);
                        if (Simulator.CanUseAction(craft, step, Skills.WasteNot) && step.WasteNotLeft == 0 && !_wasteNotUsed) return new(Skills.WasteNot);
                        if (Simulator.CanUseAction(craft, step, Skills.FinalAppraisal) && step.FinalAppraisalLeft == 0 && CanFinishCraft(craft, step, act)) return new(Skills.FinalAppraisal, $"Synth is {act}");
                        if (!CanFinishCraft(craft, step, act))
                        return new(act);
                    }
                }

                if (Simulator.CanUseAction(craft, step, Skills.ByregotsBlessing) && !WillActFail(craft, step, Skills.ByregotsBlessing))
                {
                    var newQuality = CalculateNewQuality(craft, step, Skills.ByregotsBlessing);
                    var newHQPercent = maxQuality > 0 ? Calculations.GetHQChance(newQuality * 100.0 / maxQuality) : 100;
                    var newDone = craft.CraftQualityMin1 == 0 ? newHQPercent >= P.Config.MaxPercentage : newQuality >= maxQuality;
                    if (newDone) return new(Skills.ByregotsBlessing);
                }

                if (_wasteNotUsed && Simulator.CanUseAction(craft, step, Skills.PreciseTouch) && step.GreatStridesLeft == 0 && step.Condition is Condition.Good or Condition.Excellent && !WillActFail(craft, step, Skills.PreciseTouch)) return new(Skills.PreciseTouch);
                if (craft.StatLevel < Simulator.MinLevel(Skills.PreciseTouch) && step.GreatStridesLeft == 0 && step.Condition is Condition.Excellent)
                {
                    if (step.PrevComboAction == Skills.BasicTouch && Simulator.CanUseAction(craft, step, Skills.StandardTouch) && step.Durability - Simulator.GetDurabilityCost(step, Skills.StandardTouch) > 0) return new(Skills.StandardTouch);
                    if (Simulator.CanUseAction(craft, step, Skills.BasicTouch) && step.Durability - Simulator.GetDurabilityCost(step, Skills.BasicTouch) > 0) return new(Skills.BasicTouch);
                    if (Simulator.CanUseAction(craft, step, Skills.TricksOfTrade)) return new(Skills.TricksOfTrade);
                }
                if (step.InnovationLeft == 0 && Simulator.CanUseAction(craft, step, Skills.Innovation) && !inCombo && step.RemainingCP >= 36) return new(Skills.Innovation);
                if (!_wasteNotUsed && step.WasteNotLeft == 0 && Simulator.CanUseAction(craft, step, Skills.WasteNot2)) return new(Skills.WasteNot2);
                if (!_wasteNotUsed && step.WasteNotLeft == 0 && Simulator.CanUseAction(craft, step, Skills.WasteNot) && craft.StatLevel < Simulator.MinLevel(Skills.WasteNot2)) return new(Skills.WasteNot);
                if (Simulator.CanUseAction(craft, step, Skills.PrudentTouch) && step.Durability == 10) return new(Skills.PrudentTouch);
                if (step.GreatStridesLeft == 0 && Simulator.CanUseAction(craft, step, Skills.GreatStrides) && step.Condition != Condition.Excellent && step.RemainingCP >= Simulator.GetCPCost(step, Skills.GreatStrides) + Simulator.GetCPCost(step, Skills.ByregotsBlessing) && !WillActFail(craft, step, Skills.ByregotsBlessing))
                {
                    var newQuality = GreatStridesByregotCombo(craft, step);
                    var newHQPercent = maxQuality > 0 ? Calculations.GetHQChance(newQuality * 100.0 / maxQuality) : 100;
                    var newDone = craft.CraftQualityMin1 == 0 ? newHQPercent >= P.Config.MaxPercentage : newQuality >= maxQuality;
                    if (newDone) return new(Skills.GreatStrides, "GS Combo");
                }

                if (step.Condition == Condition.Poor && Simulator.CanUseAction(craft, step, Skills.CarefulObservation) && P.Config.UseSpecialist) return new(Skills.CarefulObservation);
                if (step.Condition == Condition.Poor && Simulator.CanUseAction(craft, step, Skills.Observe))
                {
                    if (step.InnovationLeft >= 2 && craft.StatLevel >= Simulator.MinLevel(Skills.AdvancedTouch))
                        return new(Skills.Observe);

                    if (!CanFinishCraft(craft, step, act))
                        return new(act);

                    return new(Skills.Observe);
                }
                if (step.GreatStridesLeft != 0 && Simulator.CanUseAction(craft, step, Skills.ByregotsBlessing) && !WillActFail(craft, step, Skills.ByregotsBlessing)) return new(Skills.ByregotsBlessing);
                if (step.HeartAndSoulAvailable && Simulator.CanUseAction(craft, step, Skills.HeartAndSoul) && P.Config.UseSpecialist) return new(Skills.HeartAndSoul);
                if (HighestLevelTouch(craft, step) is var touch && touch != Skills.None) return new(touch);
            }

            if (CanFinishCraft(craft, step, act))
                return new(act);

            if (Simulator.CanUseAction(craft, step, Skills.Veneration) && step.VenerationLeft == 0 && step.Condition != Condition.Excellent) return new(Skills.Veneration);
            return new(act);
        }

        private bool CheckIfVenerationIsWorth(CraftState craft, StepState step, Skills act)
        {
            if (step.Condition is Condition.Good or Condition.Excellent) return false;
            if (_venereationUsed) return false;
            if (step.FinalAppraisalLeft > 0) return false;  

            var (result, next) = Simulator.Execute(craft, step with { Durability = 40 }, act, 0, 1);
            if (next.Progress >= craft.CraftProgress) return false;
            var (result2, next2) = Simulator.Execute(craft, next with { Durability = 40 }, act, 0, 1);
            if (next2.Progress >= craft.CraftProgress) return false;
            //var (result3, next3) = Simulator.Execute(craft, next2 with { Durability = 40 }, act, 0, 1);
            //if (next3.Progress >= craft.CraftProgress) return false;

            return true;
        }

        private static bool WillActFail(CraftState craft, StepState step, Skills act)
        {
            bool result = step.Durability - Simulator.GetDurabilityCost(step, act) <= 0 && CalculateNewProgress(craft, step, act) < craft.CraftProgress;
            return result;
        }

        private static bool GoingForQuality(CraftState craft, StepState step, out int maxQuality)
        {
            bool wantMoreQuality;
            if (craft.CraftQualityMin1 == 0)
            {
                // normal craft
                maxQuality = craft.CraftQualityMax;
                wantMoreQuality = maxQuality > 0 && Calculations.GetHQChance(step.Quality * 100.0 / maxQuality) < P.Config.MaxPercentage;
            }
            else
            {
                // collectible
                maxQuality = P.Config.SolverCollectibleMode switch
                {
                    1 => craft.CraftQualityMin1,
                    2 => craft.CraftQualityMin2,
                    _ => craft.CraftQualityMin3,
                };
                wantMoreQuality = step.Quality < maxQuality;
            }

            return wantMoreQuality;
        }

        private bool ShouldMend(CraftState craft, StepState step,bool goingForQuality)
        {
            var synthOption = BestSynthesis(craft, step);
            var touchOption = HighestLevelTouch(craft, step);

            if (goingForQuality && _qualityStarted)
            {
                if (WillActFail(craft, step, touchOption)) return true;
            }
            else
            {
                if (WillActFail(craft, step, synthOption)) return true;
            }

            return false;
        }

        private static int GetComboDurability(CraftState craft, StepState step, params Skills[] comboskills)
        {
            int output = step.Durability;
            foreach (var skill in comboskills)
            {
                var (result, next) = Simulator.Execute(craft, step, skill, 1, 0);
                if (result == Simulator.ExecuteResult.CantUse)
                    continue;

                output = next.Durability;
                step = next;
            }

            return output;
        }
        private static bool CanCompleteTouchCombo(CraftState craft, StepState step)
        {
            int wasteStacks = step.WasteNotLeft;
            var innoStacks = step.InnovationLeft;

            if (craft.StatLevel < Simulator.MinLevel(Skills.StandardTouch))
            {
                return step.Durability > Simulator.GetDurabilityCost(step, Skills.BasicTouch);
            }
            else if (craft.StatLevel < Simulator.MinLevel(Skills.AdvancedTouch))
            {
                if (step.PrevComboAction == Skills.BasicTouch) return true; //Assume started
                if (step.RemainingCP < 36 || innoStacks < 2) return false;

                var copyofDura = step.Durability;
                for (int i = 1; i == 2; i++)
                {
                    copyofDura = wasteStacks > 0 ? copyofDura - 5 : copyofDura - 10;
                    wasteStacks--;
                }
                return copyofDura > 0;
            }
            else
            {
                if (step.PrevComboAction is Skills.BasicTouch or Skills.StandardTouch) return true; //Assume started
                if (step.RemainingCP < 54 || innoStacks < 3) return false;

                var copyofDura = step.Durability;
                for (int i = 1; i == 3; i++)
                {
                    copyofDura = wasteStacks > 0 ? copyofDura - 5 : copyofDura - 10;
                    wasteStacks--;
                }
                return copyofDura > 0;
            }
        }

        public static int CalculateNewProgress(CraftState craft, StepState step, Skills action) => step.FinalAppraisalLeft > 0 ? Math.Min(step.Progress + Simulator.CalculateProgress(craft, step, action), craft.CraftProgress -1) : step.Progress + Simulator.CalculateProgress(craft, step, action);
        public static int CalculateNewQuality(CraftState craft, StepState step, Skills action) => step.Quality + Simulator.CalculateQuality(craft, step, action);
        public static bool CanFinishCraft(CraftState craft, StepState step, Skills act) => CalculateNewProgress(craft, step, act) >= craft.CraftProgress;

        public static int GreatStridesByregotCombo(CraftState craft, StepState step)
        {
            if (!Simulator.CanUseAction(craft, step, Skills.ByregotsBlessing) || step.RemainingCP < 56)
                return 0;

            var (res, next) = Simulator.Execute(craft, step, Skills.GreatStrides, 0, 1);
            if (res != Simulator.ExecuteResult.Succeeded)
                return 0;

            return CalculateNewQuality(craft, next, Skills.ByregotsBlessing);
        }

        public static Skills HighestLevelTouch(CraftState craft, StepState step)
        {
            bool wasteNots = step.WasteNotLeft > 0;

            if (Simulator.CanUseAction(craft, step, Skills.AdvancedTouch) && step.PrevComboAction == Skills.Observe) return Skills.AdvancedTouch;
            if (Simulator.CanUseAction(craft, step, Skills.PreciseTouch) && Simulator.CanUseAction(craft, step, Skills.PreciseTouch)) return Skills.PreciseTouch;
            if (Simulator.CanUseAction(craft, step, Skills.PreparatoryTouch) && step.IQStacks < P.Config.MaxIQPrepTouch && step.InnovationLeft > 0) return Skills.PreparatoryTouch;
            if (Simulator.CanUseAction(craft, step, Skills.AdvancedTouch) && step.PrevComboAction == Skills.StandardTouch) return Skills.AdvancedTouch;
            if (Simulator.CanUseAction(craft, step, Skills.StandardTouch) && step.PrevComboAction == Skills.BasicTouch) return Skills.StandardTouch;
            if (Simulator.CanUseAction(craft, step, Skills.PrudentTouch) && GetComboDurability(craft, step, Skills.BasicTouch, Skills.StandardTouch, Skills.AdvancedTouch) <= 0) return Skills.PrudentTouch;
            if (Simulator.CanUseAction(craft, step, Skills.TrainedFinesse) && step.Durability <= 10) return Skills.TrainedFinesse;
            if (Simulator.CanUseAction(craft, step, Skills.BasicTouch)) return Skills.BasicTouch;
            if (Simulator.CanUseAction(craft, step, Skills.DaringTouch)) return Skills.DaringTouch;
            if (Simulator.CanUseAction(craft, step, Skills.HastyTouch)) return Skills.HastyTouch;

            return Skills.None;
        }

        public static Skills HighestLevelSynth(CraftState craft, StepState step)
        {
            if (Simulator.CanUseAction(craft, step, Skills.IntensiveSynthesis)) return Skills.IntensiveSynthesis;
            if (Simulator.CanUseAction(craft, step, Skills.Groundwork) && step.Durability > 20) return Skills.Groundwork;
            if (Simulator.CanUseAction(craft, step, Skills.PrudentSynthesis)) return Skills.PrudentSynthesis;
            if (Simulator.CanUseAction(craft, step, Skills.CarefulSynthesis)) return Skills.CarefulSynthesis;
            if (Simulator.CanUseAction(craft, step, Skills.BasicSynthesis)) return Skills.BasicSynthesis;

            return Skills.None;
        }
    }
}
