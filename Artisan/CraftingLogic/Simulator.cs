using Artisan.CraftingLogic.CraftData;
using Artisan.GameInterop;
using Artisan.GameInterop.CSExt;
using Artisan.RawInformation.Character;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using Lumina.Excel.Sheets;
using System;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Text;
using Condition = Artisan.CraftingLogic.CraftData.Condition;

namespace Artisan.CraftingLogic;

public static class Simulator
{
    /// <summary>
    /// 奇蹟之材(Action #41269)的持續時間,單位秒。
    ///
    /// ✅ **有台服官方資料背書,不是抄來的猜測**(2026-08-07 更正):
    /// `D:/ffxiv-tc-port/exd-tc/7.20/ActionTransient.csv` 第 41269 列的 `Description` 欄逐字寫著
    ///   「每次作業會發生變化的製作狀態固定變為『高品質』『結實』『安定』『高效』『長持續』『大進展』
    ///     狀態的其中一個 / **持續時間:**45秒 / **該技能僅限在探索任務的製作中使用**」
    /// (舊註解說「Action 表裡查不到時長欄位所以無法離線驗證」——**前半對、結論錯**。)
    ///
    /// 🔑 可複用的教訓:**技能時長不在 `Action` 表的欄位裡,在 `ActionTransient.Description` 的文字裡。**
    /// 全外掛只有這一處硬編,底下兩個值都由它推導。
    /// </summary>
    public const float MaterialMiracleDurationSeconds = 45f;

    /// <summary>
    /// 一個製作步驟平均花幾秒。實機量到的平均值約 2.19 秒
    /// (Artisan 自己的耗時模型是「長動畫 2.5 秒、短動畫 1.25 秒」,再加使用者設定的
    /// AutoDelay + RecommendationDelay)。
    /// ⚠️ 這個值會隨機器效能、網路延遲與使用者的延遲設定漂移 —— 所以**實機不用它**:
    /// 實機在 <see cref="GameInterop.Crafting"/> 記 <c>Environment.TickCount64</c> 走真時鐘,
    /// 這裡的換算只給「沒有時鐘」的前瞻模擬用。
    /// </summary>
    public const float MaterialMiracleSecondsPerStep = 2.19f;

    /// <summary>
    /// 奇蹟之材換算成「幾個動作」——**只給前瞻模擬用**,實機走真時鐘。
    ///
    /// = ceil(45 秒 / 2.19 秒) = 21 步。
    /// 舊值是 15(當時假設一步 2~3 秒、取中間值)。實機 log 量出來的形狀是
    /// 「走完 15 步遊戲 buff 還在、再約 6 步才真的失效」,也就是估得太短。
    /// 估太長 = buff 到期那一步會對不上狀態;估太短 = 提早以為 buff 沒了。
    /// 兩種都只造成「一次」狀態不合(之後 BuildStepState 會重新對齊),不會累積。
    /// 🔴 這個常數只在使用者主動開啟奇蹟之材時才有作用(兩個相關設定預設都是關的)。
    /// </summary>
    public static readonly int MaterialMiracleDurationSteps =
        (int)Math.Ceiling(MaterialMiracleDurationSeconds / MaterialMiracleSecondsPerStep);

    /// <summary>
    /// 奇蹟之材(Action #41269)生效期間的製作狀態池 —— **全外掛唯一的一份**。
    ///
    /// ✅ **池的成員是查證過的,不是推測**,兩個互相獨立的來源:
    ///  ① 台服官方文字:`exd-tc/7.20/ActionTransient.csv` 第 41269 列 `Description` 逐字寫著
    ///     「每次作業會發生變化的製作狀態**固定變為**『高品質』『結實』『安定』『高效』『長持續』『大進展』
    ///       狀態的其中一個」。這裡的順序就照那句話的順序排,方便逐字核對。
    ///  ② 使用者實機 log(2026-08-15 重新收割全部 `dalamud*.log`,不是舊註解那 55 步):
    ///     `MaterialMiracleActive:True` 的步驟共 **812 筆(去重後 801 筆)**,
    ///     **落在池外的 0 筆** —— 沒有通常、沒有最高品質/低品質、沒有予兆。
    ///     同一批 log 裡 buff 沒生效的 14087 步則是 通常 73.3%、予兆 1.3%、最高品質 1.1%、低品質 1.1%。
    ///     ⇒ 奇蹟之材是把狀態池**整個換掉**,不是在配方原本的池子上做過濾
    ///       (連配方 `ConditionsFlag` 根本沒有「安定」的非專家配方也照樣擲得出來)。
    ///
    /// ⚠️ **池內的機率是假設,不是查證** —— 台服 EXD **沒有任何一張表**存製作狀態的機率
    ///    (`RecipeLevelTable.ConditionsFlag` 只說「哪些狀態會出現」;連一般製作的權重
    ///     `CraftState.NormalCraftConditionProbabilities` 也是社群逆向出來的硬編值)。
    ///    這裡採**池內均勻分布(各 1/6)**。實機 801 筆的分布是
    ///    結實 19.7% / 安定 19.6% / 大進展 15.5% / 高效 15.4% / 長持續 15.0% / 高品質 14.9%,
    ///    對「完全均勻」的卡方 13.1(df=5,5% 臨界 11.07)—— **略微偏離但沒有權威真值可用**,
    ///    而且樣本高度集中在少數幾場製作(570/812 來自同一個 log 檔),不足以拿來擬合權重。
    ///    🔑 要推翻這個假設,需要的是**更多不同場次的實機樣本**,不是再讀一次資料表。
    /// </summary>
    public static readonly Condition[] MaterialMiracleConditionPool =
    [
        Condition.Good,     // 高品質:品質 ×1.5(宇宙配方 ×1.75)
        Condition.Sturdy,   // 結實:耐久消耗減半
        Condition.Centered, // 安定:成功率 +25%
        Condition.Pliant,   // 高效:CP 消耗減半
        Condition.Primed,   // 長持續:新掛上的回合制 buff +2 回合
        Condition.Malleable,// 大進展:進度 ×1.5
    ];

    /// <summary>
    /// 「不擲骰」的前瞻模擬(慣例是 <c>Execute(craft, step, action, 0, 1)</c>:動作必成功、
    /// 狀態擲骰餵 1)在奇蹟之材生效期間要落在哪個狀態。
    ///
    /// 🔑 為什麼是「安定」而不是別的:那條慣例的 roll=1 原本會讓
    /// <see cref="GetTransitionByRoll"/> 全部扣完仍不小於 0,退回「通常」——
    /// 也就是「**沒有任何狀態加成**」的中性世界。但奇蹟之材期間**沒有通常可選**,
    /// 六個成員裡只有「安定」的效果是**成功率**,而成功率在這條慣例下已經被 roll=0 釘成必成功,
    /// 所以「安定」是唯一一個**對進度/品質/耐久/CP/buff 時長全都是恆等變換**的成員。
    /// ⇒ 拿它當代表,既不再宣稱一個實機不可能出現的狀態,又讓所有既有的決定性前瞻
    ///   (<c>SolverUtils.SimulateSolverExecution</c>、<c>OpportunisticSolver</c> 的 rollout、
    ///    <c>StandardSolver.CanSpamBasicToComplete</c>)在數值上維持原樣。
    /// ⚠️ 唯一還是會變的是「**依狀態名稱分支**」的解算器邏輯(例如 ExpertSolver 把
    ///    通常/高品質/予兆/長持續 視為「可以觀察」的那一組;安定不在裡面)。這是刻意的:
    ///    那些分支本來就該看到實機真的會出現的狀態。
    /// </summary>
    public const Condition MaterialMiracleDeterministicCondition = Condition.Centered;

    /// <summary>
    /// 奇蹟之材生效期間的狀態轉移:忽略配方自己的狀態表,改從
    /// <see cref="MaterialMiracleConditionPool"/> 均勻抽一個(機率是假設,見該欄位註解)。
    /// </summary>
    public static Condition MaterialMiracleTransition(float roll)
    {
        var idx = (int)(roll * MaterialMiracleConditionPool.Length);
        // (uint) 轉型同時擋住 roll >= 1(決定性慣例)與理論上的負值,不用兩個比較。
        return (uint)idx < (uint)MaterialMiracleConditionPool.Length
            ? MaterialMiracleConditionPool[idx]
            : MaterialMiracleDeterministicCondition;
    }

    public enum CraftStatus
    {
        [Description("製作進行中")]
        InProgress,
        [Description("因耐久不足導致製作失敗")]
        FailedDurability,
        [Description("因未達到最低品質導致製作失敗")]
        FailedMinQuality,
        [Description("已完成第一個品質突破點")]
        SucceededQ1,
        [Description("已完成第二個品質突破點")]
        SucceededQ2,
        [Description("已完成第三個品質突破點")]
        SucceededQ3,
        [Description("已完成最高品質")]
        SucceededMaxQuality,
        [Description("已完成，但未達到最高品質")]
        SucceededSomeQuality,
        [Description("已完成，不需要品質")]
        SucceededNoQualityReq,

        Count
    }

    public static string ToOutputString(this CraftStatus status)
    {
        return status.GetAttribute<DescriptionAttribute>().Description;
    }

    public enum ExecuteResult
    {
        CantUse,
        Failed,
        Succeeded
    }

    public static StepState CreateInitial(CraftState craft, int startingQuality)
        => new()
        {
            Index = 1,
            Durability = craft.CraftDurability,
            Quality = startingQuality,
            RemainingCP = craft.StatCP,
            CarefulObservationLeft = craft.Specialist ? 3 : 0,
            HeartAndSoulAvailable = craft.Specialist,
            QuickInnoLeft = craft.Specialist ? 1 : 0,
            TrainedPerfectionAvailable = craft.StatLevel >= MinLevel(Skills.TrainedPerfection),
            Condition = Condition.Normal,
            // 以前這裡寫死 1。任務其實可以給到 3 次(實機 log 直接觀測到配方 36214 是 3),
            // 寫死 1 會讓「一場製作可用多次」那個設定在模擬器裡**永遠無效而且靜默**:
            // 勾了沒反應,因為 CanUseAction 卡在 Charges > 0。
            // 🔴 MissionMaterialMiracleCharges 自帶 Max(1,…) 的 fail-safe,所以這裡的下界仍是舊行為。
            MaterialMiracleCharges = craft.MissionHasMaterialMiracle ? Math.Max(1u, craft.MissionMaterialMiracleCharges) : 0,
        };

    public static CraftStatus Status(CraftState craft, StepState step)
    {
        if (step.Progress < craft.CraftProgress)
        {
            if (step.Durability > 0)
                return CraftStatus.InProgress;
            else
                return CraftStatus.FailedDurability;
        }

        if (craft.CraftCollectible || craft.CraftExpert)
        {
            if (step.Quality >= craft.CraftQualityMin3)
                return CraftStatus.SucceededQ3;

            if (step.Quality >= craft.CraftQualityMin2)
                return CraftStatus.SucceededQ2;

            if (step.Quality >= craft.CraftQualityMin1)
                return CraftStatus.SucceededQ1;

            if (step.Quality < craft.CraftRequiredQuality || step.Quality < craft.CraftQualityMin1)
                return CraftStatus.FailedMinQuality;

        }

        if (craft.CraftHQ && !craft.CraftCollectible)
        {
            if (step.Quality >= craft.CraftQualityMax)
                return CraftStatus.SucceededMaxQuality;
            else
                return CraftStatus.SucceededSomeQuality;

        }
        else
        {
            return CraftStatus.SucceededNoQualityReq;
        }
    }

    /// <summary>
    /// 配方頁的「這個配方做得起來嗎」提示。
    ///
    /// 🔑 結論(完成率／品質中位數)寫在列上,明細(樣本數、分位數、突破點分布、
    /// 舊的單次決定性模擬)進 <paramref name="tooltip"/>。取樣還沒跑完時列上寫 <c>?</c> ——
    /// 「不知道」本身要看得見,不能畫成 0。
    /// </summary>
    public unsafe static string SimulatorResult(Recipe recipe, RecipeConfig config, CraftState craft, out Vector4 hintColor, out string tooltip, bool assumeMaxStartingQuality = false)
    {
        hintColor = ImGuiColors.DalamudWhite;
        tooltip = "";
        var solverDesc = CraftingProcessor.GetSolverForRecipe(config, craft);
        var solver = solverDesc.CreateSolver(craft);
        if (solver == null) return "沒有找到有效的解算器";
        var startingQuality = GetStartingQuality(recipe, assumeMaxStartingQuality, craft.StatLevel);
        var time = SolverUtils.EstimateCraftTime(solver, craft, startingQuality);
        var result = SolverUtils.SimulateSolverExecution(solver, craft, startingQuality);
        var status = result != null ? Status(craft, result) : CraftStatus.InProgress;
        var hq = result != null ? Calculations.GetHQChance((float)result.Quality / craft.CraftQualityMax * 100) : 0;

        string deterministicHint = status switch
        {
            CraftStatus.InProgress => "製作未完成（解算器在完成之前未返回任何步驟）。",
            CraftStatus.FailedDurability => $"因耐久度不足導致製作失敗。(進展：{(float)result.Progress / craft.CraftProgress * 100:f0}%，品質：{(float)result.Quality / craft.CraftQualityMax * 100:f0}%）",
            CraftStatus.FailedMinQuality => $"製作完成並達到滿品質，耗時（進展：{(float)result.Progress / craft.CraftProgress * 100:f0}%，品質：{(float)result.Quality / craft.CraftQualityMax * 100:f0}%）",
            CraftStatus.SucceededQ1 => $"製作完成並達到第一個品質門檻，耗時 {time.TotalSeconds:f0} 秒。",
            CraftStatus.SucceededQ2 => $"製作完成並達到第二個品質門檻，耗時 {time.TotalSeconds:f0} 秒。",
            CraftStatus.SucceededQ3 => $"製作完成並達到第三個品質門檻，耗時 {time.TotalSeconds:f0} 秒！",
            CraftStatus.SucceededMaxQuality => $"製作完成並達到滿品質，耗時 {time.TotalSeconds:f0} 秒！",
            CraftStatus.SucceededSomeQuality => $"製作完成但未達到最大品質（{hq}%），耗時 {time.TotalSeconds:f0} 秒。",
            CraftStatus.SucceededNoQualityReq => $"製作完成，無需品質，耗時 {time.TotalSeconds:f0} 秒！",
            CraftStatus.Count => "你不應該看到這個，請報告問題。",
            _ => "你不應該看到這個，請報告問題。",
        };

        // 🔴 上面那句是**單次決定性模擬**的結論:擲骰寫死成 (0, 1) = 動作必成功、
        //    狀態永遠回到「通常」,同時吃到一個樂觀假設與一個悲觀假設。
        //    離線量測(~/.claude/tools/artisan-sim 的 b4hint)顯示專家配方的品質平均低估
        //    48.8 個百分點,36 格裡有 16 格說「做不完」但實測完成率超過 50%。
        //    所以它降級成 tooltip 裡的參考值,列上改用真的跑 N 次的分布。
        // 📌 把這句話塞進快取鍵是刻意的:它每一幀都重算,而且內含解算器實際跑出來的
        //    進度/品質/耗時 —— 使用者改了巨集之類「不在鍵裡的東西」時,它會跟著變,
        //    等於免費得到一個失效偵測。
        var qualityMatters = craft.CraftQualityMax > 0 && (craft.CraftHQ || craft.CraftCollectible || craft.CraftExpert);
        // 專家/收藏品的變異大,樣本要多一點;一般配方少一點就夠,免得白花時間。
        var target = craft.CraftExpert || craft.CraftCollectible ? 200 : 60;
        var key = $"{recipe.RowId}|{solverDesc.Def?.GetType().FullName}|{solverDesc.Flavour}|{solverDesc.Name}|" +
                  $"{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}/{craft.StatLevel}|" +
                  $"{craft.Specialist}{craft.UnlockedManipulation}|{startingQuality}|{deterministicHint}";
        var dist = SolverHintSampler.Sample(key, solver, craft, startingQuality, target);

        var medianQuality = dist.QualityQuantile(0.5);
        string solverHint;
        if (!dist.Done)
        {
            // ⚠️ 取樣還沒完的時候不要把未知畫成 0 —— 用 ? 標明「還不知道」。
            solverHint = $"完成率 ?　品質中位 ?　（取樣中 {dist.Samples}/{dist.Target}，預估耗時 {time.TotalSeconds:f0} 秒）";
        }
        else if (dist.Completed == 0)
        {
            solverHint = $"完成率 0%　做不完（{dist.Samples} 次模擬全部失敗）";
        }
        else if (qualityMatters)
        {
            solverHint = $"完成率 {dist.CompletionPct:f0}%　品質中位 {medianQuality:f0}%　預估耗時 {time.TotalSeconds:f0} 秒";
        }
        else
        {
            solverHint = $"完成率 {dist.CompletionPct:f0}%　（此配方不看品質）　預估耗時 {time.TotalSeconds:f0} 秒";
        }

        var medianHQ = medianQuality >= 0 ? Calculations.GetHQChance((float)medianQuality) : 0;
        hintColor = !dist.Done ? ImGuiColors.DalamudGrey
            : dist.CompletionPct < 50 ? ImGuiColors.DalamudRed
            : dist.CompletionPct < 95 ? ImGuiColors.DalamudOrange
            : !qualityMatters || medianQuality >= 99.5 ? ImGuiColors.ParsedGreen
            : new Vector4(1 - (medianHQ / 100f), 0 + (medianHQ / 100f), 1 - (medianHQ / 100f), 1f);

        tooltip = BuildHintTooltip(craft, dist, deterministicHint, qualityMatters);
        return solverHint;
    }

    private static string BuildHintTooltip(CraftState craft, SolverHintSampler.Result dist, string deterministicHint, bool qualityMatters)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"隨機模擬 {dist.Samples} 次（動作成敗與製作狀態都照機率擲骰）");
        if (!dist.Done)
            sb.AppendLine($"目標 {dist.Target} 次，還在取樣中……");
        sb.AppendLine($"完成率：{dist.CompletionPct:f1}%（{dist.Samples} 次裡有 {dist.Completed} 次做完）");

        if (dist.Completed > 0 && qualityMatters)
        {
            sb.AppendLine();
            sb.AppendLine("品質達成率分布（只計做完的那幾次）：");
            sb.AppendLine($"　最差 10%：{dist.QualityQuantile(0.1):f0}%");
            sb.AppendLine($"　中位數：　{dist.QualityQuantile(0.5):f0}%");
            sb.AppendLine($"　最好 10%：{dist.QualityQuantile(0.9):f0}%");
        }

        if ((craft.CraftCollectible || craft.CraftExpert) && craft.CraftQualityMin1 > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"打到第幾個突破點：第一 {dist.TierPct(1):f0}%／第二 {dist.TierPct(2):f0}%／第三 {dist.TierPct(3):f0}%");
        }

        sb.AppendLine();
        sb.AppendLine("單次決定性模擬（舊版提示；假設動作必成功、狀態永遠是「通常」，兩邊都不準）：");
        sb.Append($"　{deterministicHint}");
        return sb.ToString();
    }

    public unsafe static int GetStartingQuality(Recipe recipe, bool assumeMaxStartingQuality, int characterLevel)
    {
        var rd = RecipeNoteRecipeData.Ptr();
        var re = rd != null ? rd->FindRecipeById(recipe.RowId) : null;
        var shqf = (float)recipe.MaterialQualityFactor / 100;
        var lt = recipe.Number == 0 && characterLevel < 100 ? Svc.Data.GetExcelSheet<RecipeLevelTable>().First(x => x.ClassJobLevel == characterLevel) : recipe.RecipeLevelTable.Value;
        var startingQuality = assumeMaxStartingQuality ? (int)(Calculations.RecipeMaxQuality(recipe, lt) * shqf) : re != null ? Calculations.GetStartingQuality(recipe, re->GetAssignedHQIngredients(), lt) : 0;
        return startingQuality;
    }

    public static (ExecuteResult, StepState) Execute(CraftState craft, StepState step, Skills action, float actionSuccessRoll, float nextStateRoll)
    {
        if (Status(craft, step) != CraftStatus.InProgress)
            return (ExecuteResult.CantUse, step); // can't execute action on craft that is not in progress

        var success = actionSuccessRoll < GetSuccessRate(step, action);

        if (!CanUseAction(craft, step, action))
            return (ExecuteResult.CantUse, step); // can't use action because of level, insufficient cp or special conditions

        var next = new StepState();
        next.Index = SkipUpdates(action) ? step.Index : step.Index + 1;
        next.Progress = step.Progress + (success ? CalculateProgress(craft, step, action) : 0);
        next.Quality = step.Quality + (success ? CalculateQuality(craft, step, action) : 0);
        next.IQStacks = step.IQStacks;
        if (success)
        {
            if (next.Quality != step.Quality)
                ++next.IQStacks;
            if (action is Skills.PreciseTouch or Skills.PreparatoryTouch or Skills.Reflect or Skills.RefinedTouch)
                ++next.IQStacks;
            if (next.IQStacks > 10)
                next.IQStacks = 10;
            if (action == Skills.ByregotsBlessing)
                next.IQStacks = 0;
            if (action == Skills.HastyTouch)
                next.ExpedienceLeft = 1;
            else
                next.ExpedienceLeft = 0;
        }

        next.WasteNotLeft = action switch
        {
            Skills.WasteNot => GetNewBuffDuration(step, 4),
            Skills.WasteNot2 => GetNewBuffDuration(step, 8),
            _ => GetOldBuffDuration(step.WasteNotLeft, action)
        };
        next.ManipulationLeft = action == Skills.Manipulation ? GetNewBuffDuration(step, 8) : GetOldBuffDuration(step.ManipulationLeft, action);
        next.GreatStridesLeft = action == Skills.GreatStrides ? GetNewBuffDuration(step, 3) : GetOldBuffDuration(step.GreatStridesLeft, action, next.Quality != step.Quality);
        next.InnovationLeft = action == Skills.Innovation ? GetNewBuffDuration(step, 4) : action == Skills.QuickInnovation ? GetNewBuffDuration(step, 1) : GetOldBuffDuration(step.InnovationLeft, action);
        next.VenerationLeft = action == Skills.Veneration ? GetNewBuffDuration(step, 4) : GetOldBuffDuration(step.VenerationLeft, action);
        next.MuscleMemoryLeft = action == Skills.MuscleMemory ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.MuscleMemoryLeft, action, next.Progress != step.Progress);
        next.FinalAppraisalLeft = action == Skills.FinalAppraisal ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.FinalAppraisalLeft, action, next.Progress >= craft.CraftProgress);
        next.CarefulObservationLeft = step.CarefulObservationLeft - (action == Skills.CarefulObservation ? 1 : 0);
        next.HeartAndSoulActive = action == Skills.HeartAndSoul || step.HeartAndSoulActive && (step.Condition is Condition.Good or Condition.Excellent || !ConsumeHeartAndSoul(action));
        next.HeartAndSoulAvailable = step.HeartAndSoulAvailable && action != Skills.HeartAndSoul;
        next.QuickInnoLeft = step.QuickInnoLeft - (action == Skills.QuickInnovation ? 1 : 0);
        next.QuickInnoAvailable = step.QuickInnoLeft > 0 && next.InnovationLeft == 0;
        next.PrevActionFailed = !success;
        next.PrevComboAction = action; // note: even stuff like final appraisal and h&s break combos
        next.TrainedPerfectionActive = action == Skills.TrainedPerfection || (step.TrainedPerfectionActive && !HasDurabilityCost(action));
        next.TrainedPerfectionAvailable = step.TrainedPerfectionAvailable && action != Skills.TrainedPerfection;
        next.MaterialMiracleCharges = action == Skills.MaterialMiracle ? step.MaterialMiracleCharges - 1 : step.MaterialMiracleCharges;
        // 奇蹟之材是實時 45 秒的 buff。以前這裡直接把舊值抄過來,結果**兩個方向都錯**:
        //   - 用掉的那一步預測仍是 false,但遊戲已經給了 buff → 每次使用都必定對不上狀態
        //     (Crafting.Update 會等到 _predictionDeadline 才認賠,所以除了 log 還會實際拖慢製作)
        //   - 前瞻模擬裡永遠是 false → CanUseAction 允許重複使用,ExpertSolver 那條
        //     「奇蹟之材期間改用工匠的絕技＋坯料加工」的分支則永遠不會被走到
        // 反過來直接設成 true 也不對(會永遠擋住再次使用)。真正的解法是計時,所以這裡計時。
        // 📌 這裡是**前瞻模擬**的路,沒有時鐘只能用步數估。實機那條路(Crafting.BuildStepState 與
        //    CraftingEventHandlerUpdateDetour)會用 Environment.TickCount64 的真時鐘覆蓋掉這個估計值。
        next.MaterialMiracleStepsLeft = action == Skills.MaterialMiracle
            ? MaterialMiracleDurationSteps
            : Math.Max(0, step.MaterialMiracleStepsLeft - 1); // 實時 buff:連不佔回合的自由動作也照樣耗掉時間
        next.MaterialMiracleActive = next.MaterialMiracleStepsLeft > 0;
        next.ObserveCounter = action == Skills.Observe ? step.ObserveCounter + 1 : 0;

        if (step.FinalAppraisalLeft > 0 && next.Progress >= craft.CraftProgress)
            next.Progress = craft.CraftProgress - 1;

        next.RemainingCP = step.RemainingCP - GetCPCost(step, action);
        if (action == Skills.TricksOfTrade) // can't fail
            next.RemainingCP = Math.Min(craft.StatCP, next.RemainingCP + 20);

        // assume these can't fail
        next.Durability = step.Durability - GetDurabilityCost(step, action);
        if (next.Durability > 0)
        {
            int repair = 0;
            if (action == Skills.MastersMend)
                repair += 30;
            if (action == Skills.ImmaculateMend)
                repair = craft.CraftDurability;
            if (step.ManipulationLeft > 0 && action != Skills.Manipulation && !SkipUpdates(action) && next.Progress < craft.CraftProgress)
                repair += 5;
            next.Durability = Math.Min(craft.CraftDurability, next.Durability + repair);
        }

        // free actions do not advance the turn, so the condition does not re-roll either
        // (careful observation is the exception - re-rolling the condition is its entire purpose)
        //
        // 🔑 狀態池要看**轉移後**的 next.MaterialMiracleActive,不是轉移前的 step:
        //  ① 按下奇蹟之材的那一步 step 還是 false、next 才是 true,而實機 log 直接觀測到
        //     「#6 Good … MaterialMiracleActive:True(21) … Prev=MaterialMiracle」——
        //     **按下去當下狀態就已經換成池內的了**(50 次 Prev=MaterialMiracle 的觀測全在池內)。
        //     用 step 的話這一次轉移會漏掉,而那正好是最關鍵的一次。
        //  ② buff 到期的那一步反過來:step 還是 true、next 已經 false,該回配方自己的狀態表。
        //  ⇒ 兩個方向都是「該步顯示的狀態,對應該步的 buff 狀態」,這是自洽的那一個選擇。
        next.Condition = action is Skills.FinalAppraisal or Skills.HeartAndSoul or Skills.QuickInnovation ? step.Condition : GetNextCondition(craft, step, nextStateRoll, next.MaterialMiracleActive);

        return (success ? ExecuteResult.Succeeded : ExecuteResult.Failed, next);
    }

    private static bool HasDurabilityCost(Skills action)
    {
        var cost = action switch
        {
            Skills.BasicSynthesis or Skills.CarefulSynthesis or Skills.RapidSynthesis or Skills.IntensiveSynthesis or Skills.MuscleMemory => 10,
            Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch or Skills.PreciseTouch or Skills.Reflect or Skills.RefinedTouch => 10,
            Skills.ByregotsBlessing or Skills.DelicateSynthesis => 10,
            Skills.Groundwork or Skills.PreparatoryTouch => 20,
            Skills.PrudentSynthesis or Skills.PrudentTouch => 5,
            _ => 0
        };

        return cost > 0;
    }

    public static int BaseProgress(CraftState craft)
    {
        float res = craft.StatCraftsmanship * 10.0f / craft.CraftProgressDivider + 2;
        if (craft.StatLevel <= craft.CraftLevel) // TODO: verify this condition, teamcraft uses 'rlvl' here
            res = res * craft.CraftProgressModifier / 100;
        return (int)res;
    }

    public static int BaseQuality(CraftState craft)
    {
        float res = craft.StatControl * 10.0f / craft.CraftQualityDivider + 35;
        if (craft.StatLevel <= craft.CraftLevel) // TODO: verify this condition, teamcraft uses 'rlvl' here
            res = res * craft.CraftQualityModifier / 100;
        return (int)res;
    }

    public static int MinLevel(Skills action) => action.Level();

    public static bool CanUseAction(CraftState craft, StepState step, Skills action) => action switch
    {
        Skills.IntensiveSynthesis or Skills.PreciseTouch or Skills.TricksOfTrade => step.Condition is Condition.Good or Condition.Excellent || step.HeartAndSoulActive,
        Skills.PrudentSynthesis or Skills.PrudentTouch => step.WasteNotLeft == 0,
        Skills.MuscleMemory or Skills.Reflect => step.Index == 1,
        Skills.TrainedFinesse => step.IQStacks == 10,
        Skills.ByregotsBlessing => step.IQStacks > 0,
        Skills.TrainedEye => !craft.CraftExpert && craft.StatLevel >= craft.CraftLevel + 10 && step.Index == 1,
        Skills.Manipulation => craft.UnlockedManipulation,
        Skills.CarefulObservation => step.CarefulObservationLeft > 0,
        Skills.HeartAndSoul => step.HeartAndSoulAvailable,
        Skills.TrainedPerfection => step.TrainedPerfectionAvailable,
        Skills.DaringTouch => step.ExpedienceLeft > 0,
        Skills.QuickInnovation => step.QuickInnoLeft > 0 && step.InnovationLeft == 0,
        Skills.MaterialMiracle => step.MaterialMiracleCharges > 0 && !step.MaterialMiracleActive,
        _ => true
    } && craft.StatLevel >= MinLevel(action) && step.RemainingCP >= GetCPCost(step, action);

    public static bool CannotUseAction(CraftState craft, StepState step, Skills action, out string reason)
    {
        if (!CanUseAction(craft, step, action))
        {
            // Externalised through the normal .Loc() path (LanguageChineseTraditional.ini) instead of the
            // hardcoded zh-TW literals that used to live here, so these read the same way as the rest of the UI.
            // Three of those literals also named the wrong action - see the .ini for the corrected wording:
            //   TrainedPerfection is 工匠的絕技 (CraftAction #100475); 工匠的神技 is TrainedFinesse (#100435).
            //   HastyTouch is 倉促 (#100355), not 倉促製作.
            //   MaterialMiracle is 奇蹟之材 (Action #41269), not 素材奇蹟.
            reason = action switch
            {
                Skills.IntensiveSynthesis or Skills.PreciseTouch or Skills.TricksOfTrade => "Condition is not Good/Excellent or Heart and Soul is not active".Loc(),
                Skills.PrudentSynthesis or Skills.PrudentTouch => "You have a Waste Not buff".Loc(),
                Skills.MuscleMemory or Skills.Reflect => "You are not on the first step of the craft".Loc(),
                Skills.TrainedFinesse => "You have less than 10 Inner Quiet stacks".Loc(),
                Skills.ByregotsBlessing => "You have 0 Inner Quiet stacks".Loc(),
                Skills.TrainedEye => craft.CraftExpert ? "Craft is expert".Loc() : step.Index != 1 ? "You are not on the first step of the craft".Loc() : "Craft is not 10 or more levels lower than your current level".Loc(),
                Skills.Manipulation => "You haven't unlocked Manipulation".Loc(),
                Skills.CarefulObservation => craft.Specialist ? Crafting.DelineationCount() == 0 ? "You have run out of Delineations.".Loc() : "You already used Careful Observation 3 times".Loc() : "You are not a specialist".Loc(),
                Skills.HeartAndSoul => craft.Specialist ? Crafting.DelineationCount() == 0 ? "You have run out of Delineations.".Loc() : "You don't have Heart & Soul available anymore for this craft".Loc() : "You are not a specialist".Loc(),
                Skills.TrainedPerfection => "You have already used Trained Perfection".Loc(),
                Skills.DaringTouch => "Hasty Touch did not succeed".Loc(),
                Skills.QuickInnovation => !craft.Specialist ? "You are not a specialist".Loc() : Crafting.DelineationCount() == 0 ? "You have run out of Delineations.".Loc() : step.QuickInnoLeft == 0 ? "You don't have Quick Innovation available anymore for this craft".Loc() : step.InnovationLeft > 0 ? "You have an Innovation buff".Loc() : "",
                Skills.MaterialMiracle => !craft.MissionHasMaterialMiracle ? "This craft cannot use Material Miracle".Loc() : step.MaterialMiracleActive ? "You already have Material Miracle active".Loc() : step.MaterialMiracleCharges == 0 ? "You have no more Material Miracle charges".Loc() : "",
                // CanUseAction also returns false for "level too low" and "not enough CP", which can happen for
                // ANY action, not just the ones enumerated above. Without this arm the switch expression throws
                // SwitchExpressionException out of the OnCraftStarted/OnCraftAdvanced event handlers.
                _ => step.RemainingCP < GetCPCost(step, action)
                        ? "You have not enough CP.".Loc()
                        : craft.StatLevel < MinLevel(action)
                            ? "Your level is too low for this action".Loc()
                            : "",
            };

            return true;
        }
        reason = "";
        return false;
    }

    // "free" actions: using them does not consume a craft step, so the step index does not advance,
    // no turn-based buff ticks down, and manipulation does not repair.
    // Verified against the game's own action descriptions (CraftAction sheet, "使用本技能不會消耗一次作業時間"):
    // the only in-craft actions carrying that clause are 設計變動 (CarefulObservation), 專心致志 (HeartAndSoul),
    // 快速改革 (QuickInnovation) and 最終確認 (FinalAppraisal, Action sheet #19012).
    public static bool SkipUpdates(Skills action) => action is Skills.CarefulObservation or Skills.FinalAppraisal or Skills.HeartAndSoul or Skills.MaterialMiracle or Skills.QuickInnovation;
    public static bool ConsumeHeartAndSoul(Skills action) => action is Skills.IntensiveSynthesis or Skills.PreciseTouch or Skills.TricksOfTrade;

    public static double GetSuccessRate(StepState step, Skills action)
    {
        var rate = action switch
        {
            Skills.RapidSynthesis => 0.5,
            Skills.HastyTouch or Skills.DaringTouch => 0.6,
            _ => 1.0
        };
        if (step.Condition == Condition.Centered)
            rate += 0.25;
        return rate;
    }

    public static int GetBaseCPCost(Skills action, Skills prevAction) => action switch
    {
        Skills.CarefulSynthesis => 7,
        Skills.Groundwork => 18,
        Skills.IntensiveSynthesis => 6,
        Skills.PrudentSynthesis => 18,
        Skills.MuscleMemory => 6,
        Skills.BasicTouch => 18,
        Skills.StandardTouch => prevAction == Skills.BasicTouch ? 18 : 32,
        Skills.AdvancedTouch => prevAction is Skills.StandardTouch or Skills.Observe ? 18 : 46,
        Skills.PreparatoryTouch => 40,
        Skills.PreciseTouch => 18,
        Skills.PrudentTouch => 25,
        Skills.TrainedFinesse => 32,
        Skills.Reflect => 6,
        Skills.ByregotsBlessing => 24,
        Skills.TrainedEye => 250,
        Skills.DelicateSynthesis => 32,
        Skills.Veneration => 18,
        Skills.Innovation => 18,
        Skills.GreatStrides => 32,
        Skills.MastersMend => 88,
        Skills.Manipulation => 96,
        Skills.WasteNot => 56,
        Skills.WasteNot2 => 98,
        Skills.Observe => 7,
        Skills.FinalAppraisal => 1,
        Skills.RefinedTouch => 24,
        Skills.ImmaculateMend => 112,
        _ => 0
    };

    public static int GetCPCost(StepState step, Skills action)
    {
        var cost = GetBaseCPCost(action, step.PrevComboAction);
        if (step.Condition == Condition.Pliant)
            cost -= cost / 2; // round up
        return cost;
    }

    public static int GetDurabilityCost(StepState step, Skills action)
    {
        if (step.TrainedPerfectionActive) return 0;
        var cost = action switch
        {
            Skills.BasicSynthesis or Skills.CarefulSynthesis or Skills.RapidSynthesis or Skills.IntensiveSynthesis or Skills.MuscleMemory => 10,
            Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch or Skills.DaringTouch or Skills.PreciseTouch or Skills.Reflect or Skills.RefinedTouch => 10,
            Skills.ByregotsBlessing or Skills.DelicateSynthesis => 10,
            Skills.Groundwork or Skills.PreparatoryTouch => 20,
            Skills.PrudentSynthesis or Skills.PrudentTouch => 5,
            _ => 0
        };
        if (step.WasteNotLeft > 0)
            cost -= cost / 2; // round up
        if (step.Condition == Condition.Sturdy)
            cost -= cost / 2; // round up
        return cost;
    }

    public static int GetNewBuffDuration(StepState step, int baseDuration) => baseDuration + (step.Condition == Condition.Primed ? 2 : 0);
    public static int GetOldBuffDuration(int prevDuration, Skills action, bool consume = false) => consume || prevDuration == 0 ? 0 : SkipUpdates(action) ? prevDuration : prevDuration - 1;

    public static int CalculateProgress(CraftState craft, StepState step, Skills action)
    {
        int potency = action switch
        {
            Skills.BasicSynthesis => craft.StatLevel >= 31 ? 120 : 100,
            Skills.CarefulSynthesis => craft.StatLevel >= 82 ? 180 : 150,
            Skills.RapidSynthesis => craft.StatLevel >= 63 ? 500 : 250,
            Skills.Groundwork => step.Durability >= GetDurabilityCost(step, action) ? craft.StatLevel >= 86 ? 360 : 300 : craft.StatLevel >= 86 ? 180 : 150,
            Skills.IntensiveSynthesis => 400,
            Skills.PrudentSynthesis => 180,
            Skills.MuscleMemory => 300,
            Skills.DelicateSynthesis => craft.StatLevel >= 94 ? 150 : 100,
            _ => 0
        };
        if (potency == 0)
            return 0;

        float buffMod = 1 + (step.MuscleMemoryLeft > 0 ? 1 : 0) + (step.VenerationLeft > 0 ? 0.5f : 0);
        float effPotency = potency * buffMod;

        float condMod = step.Condition == Condition.Malleable ? 1.5f : 1;
        return (int)(BaseProgress(craft) * condMod * effPotency / 100);
    }

    public static int CalculateQuality(CraftState craft, StepState step, Skills action)
    {
        if (action == Skills.TrainedEye)
            return craft.CraftQualityMax;

        int potency = action switch
        {
            Skills.BasicTouch => 100,
            Skills.StandardTouch => 125,
            Skills.AdvancedTouch => 150,
            Skills.HastyTouch => 100,
            Skills.DaringTouch => 150,
            Skills.PreparatoryTouch => 200,
            Skills.PreciseTouch => 150,
            Skills.PrudentTouch => 100,
            Skills.TrainedFinesse => 100,
            Skills.Reflect => 300,
            Skills.ByregotsBlessing => 100 + 20 * step.IQStacks,
            Skills.DelicateSynthesis => 100,
            Skills.RefinedTouch => 100,
            _ => 0
        };
        if (potency == 0)
            return 0;

        float buffMod = (1 + (step.GreatStridesLeft > 0 ? 1 : 0) + (step.InnovationLeft > 0 ? 0.5f : 0)) * (100 + 10 * step.IQStacks) / 100;

        float condMod = step.Condition switch
        {
            Condition.Good => craft.SplendorCosmic ? 1.75f : 1.5f,
            Condition.Excellent => 4,
            Condition.Poor => 0.5f,
            _ => 1
        };
        // note: the multiplication order matters. buffMod is not exactly representable as a float for most
        // inner quiet stacks (e.g. IQ=3 yields 1.29999995 rather than 1.3), and folding potency in first
        // (potency * buffMod) rounds that deficit away, producing an off-by-one against the game.
        // applying buffMod to the base quality first reproduces the game exactly - verified over 177 live
        // quality deltas from a crafting session (177/177 with this order, 174/177 with the old one).
        return (int)(BaseQuality(craft) * buffMod * condMod * potency / 100);
    }

    public static bool WillFinishCraft(CraftState craft, StepState step, Skills action) => step.FinalAppraisalLeft == 0 && step.Progress + CalculateProgress(craft, step, action) >= craft.CraftProgress;

    public static Skills NextTouchCombo(StepState step, CraftState craft)
    {
        if (step.PrevComboAction == Skills.BasicTouch && craft.StatLevel >= MinLevel(Skills.StandardTouch)) return Skills.StandardTouch;
        if (step.PrevComboAction == Skills.StandardTouch && craft.StatLevel >= MinLevel(Skills.AdvancedTouch)) return Skills.AdvancedTouch;
        return Skills.BasicTouch;
    }

    internal static Skills NextTouchComboRefined(StepState step, CraftState craft)
    {
        if (step.PrevComboAction == Skills.BasicTouch && craft.StatLevel >= MinLevel(Skills.RefinedTouch)) return Skills.RefinedTouch;
        return Skills.BasicTouch;
    }

    /// <param name="step">**轉移前**的那一步(讀的是它的 <see cref="StepState.Condition"/>)。</param>
    /// <remarks>
    /// 這個多載用 <paramref name="step"/> 自己的奇蹟之材狀態。
    /// <see cref="Execute"/> 走的是四參數版,傳的是**轉移後**那一步的狀態 —— 差別見該處註解。
    /// </remarks>
    public static Condition GetNextCondition(CraftState craft, StepState step, float roll)
        => GetNextCondition(craft, step, roll, step.MaterialMiracleActive);

    /// <summary>
    /// 下一步的製作狀態。
    ///
    /// 🔴 <paramref name="materialMiracleActive"/> 為真時,配方自己的狀態表**整個不算數** ——
    /// 奇蹟之材期間只會出現 <see cref="MaterialMiracleConditionPool"/> 那六種
    /// (實機 801 步、池外 0 筆;見該欄位註解)。
    ///
    /// ⚠️ 這個分支**壓過**下面那組強制轉移,包括「最高品質 → 低品質」與
    ///    「非專家配方的 高品質 → 通常」。理由:
    ///     ① 官方文字寫的是「固定變為…其中一個」,沒有例外條款;
    ///     ② 實機 801 步裡低品質/通常都是 0 筆;
    ///     ③ 88 個玩家搆得到的宇宙奇蹟配方**全部是非專家**,不壓過的話
    ///        「高品質 → 通常」會把池子從六種打回通常,等於補了跟沒補一樣。
    ///    ⚠️ **沒有實機樣本能證明「按下奇蹟之材的那一步剛好是最高品質」時遊戲怎麼處理**
    ///    (那要求 buff 生效與最高品質同時發生);這裡選擇一致地走池子。
    /// </summary>
    public static Condition GetNextCondition(CraftState craft, StepState step, float roll, bool materialMiracleActive)
    {
        if (materialMiracleActive)
            return MaterialMiracleTransition(roll);

        return step.Condition switch
        {
            Condition.Normal => GetTransitionByRoll(craft, step, roll),
            Condition.Good => craft.CraftExpert ? GetTransitionByRoll(craft, step, roll) : Condition.Normal,
            Condition.Excellent => Condition.Poor,
            Condition.Poor => Condition.Normal,
            Condition.GoodOmen => Condition.Good,
            _ => GetTransitionByRoll(craft, step, roll)
        };
    }

    public static Condition GetTransitionByRoll(CraftState craft, StepState step, float roll)
    {
        for (int i = 1; i < craft.CraftConditionProbabilities.Length; ++i)
        {
            roll -= craft.CraftConditionProbabilities[i];
            if (roll < 0)
                return (Condition)i;
        }
        return Condition.Normal;
    }

    public static ConditionFlags ConditionToFlag(this Condition condition)
    {
        return condition switch
        {
            Condition.Normal => ConditionFlags.Normal,
            Condition.Good => ConditionFlags.Good,
            Condition.Excellent => ConditionFlags.Excellent,
            Condition.Poor => ConditionFlags.Poor,
            Condition.Centered => ConditionFlags.Centered,
            Condition.Sturdy => ConditionFlags.Sturdy,
            Condition.Pliant => ConditionFlags.Pliant,
            Condition.Malleable => ConditionFlags.Malleable,
            Condition.Primed => ConditionFlags.Primed,
            Condition.GoodOmen => ConditionFlags.GoodOmen,
            Condition.Unknown => throw new NotImplementedException(),
        };
    }

}
