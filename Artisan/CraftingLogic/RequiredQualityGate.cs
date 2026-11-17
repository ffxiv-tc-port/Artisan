using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace Artisan.CraftingLogic;

/// <summary>
/// 有必要品質（Recipe.RequiredQuality＞0）的一般配方：品質不足＝製作失敗、素材損失。
/// 契約：自動挑選只挑「決定性模擬做得到必要品質」的解算器；明確指定的（設定檔或臨時解算器）照用，只提示一次。
/// 專家配方不套用：決定性模擬把狀態固定成「通常」，對專家配方太悲觀，而它的自動選項（專家解算器）本來就追品質。
/// </summary>
public static class RequiredQualityGate
{
    public enum Kind { NotApplicable, Ok, AssignedFallsShort, WaitForRaphael, Blocked }

    public readonly record struct Verdict(Kind Kind, ISolverDefinition.Desc Solver, string Reason);

    public static bool Applies(CraftState craft) => craft.CraftRequiredQuality > 0 && !craft.CraftExpert;

    /// <summary>純判斷：不讀原生、不通知、不改狀態（離線探針直接呼叫）。</summary>
    public static Verdict Evaluate(RecipeConfig? config, CraftState craft, bool raphaelInProgress)
    {
        var chosen = CraftingProcessor.GetSolverForRecipe(config, craft);
        if (!Applies(craft))
            return new(Kind.NotApplicable, chosen, "");

        var required = craft.CraftRequiredQuality;
        var name = RecipeName(craft);

        // 指定的解算器現在不能用、又不准退回標準解算器：照舊不開工，只是改成在按開始之前就擋。
        if (chosen != default && chosen.UnsupportedReason.Length > 0)
            return raphaelInProgress
                ? new(Kind.WaitForRaphael, chosen, $"「{name}」有必要品質 {required}，Raphael 正在求解，算完再開始製作。")
                : new(Kind.Blocked, chosen, $"「{name}」不開始製作：{chosen.UnsupportedReason}");

        if (chosen != default && IsAssigned(config, chosen))
        {
            var q = SimulatedQuality(chosen, craft);
            return q >= required
                ? new(Kind.Ok, chosen, "")
                : new(Kind.AssignedFallsShort, chosen,
                    $"「{name}」有必要品質 {required}（品質不足會製作失敗並損失素材）：指定的「{chosen.Name}」模擬{DescribeQuality(q)}，達不到必要品質。照指定開始製作。");
        }

        var tried = new List<string>();
        foreach (var cand in AutoCandidates(chosen, craft))
        {
            var q = SimulatedQuality(cand, craft);
            if (q >= required)
                return new(Kind.Ok, cand, tried.Count == 0 ? "" : $"「{name}」改用「{cand.Name}」（{string.Join("、", tried)}）。");
            tried.Add($"「{cand.Name}」模擬{DescribeQuality(q)}");
        }

        if (raphaelInProgress)
            return new(Kind.WaitForRaphael, chosen, $"「{name}」有必要品質 {required}，Raphael 正在求解，算完再開始製作。");

        var why = tried.Count > 0 ? string.Join("、", tried) : "沒有可用的解算器";
        var advice = RaphaelCache.HasSolution(craft, out _)
            ? "目前的能力值連 Raphael 的解也做不到，請用食物／藥水或換裝提高能力值，再重新產生 Raphael 解。"
            : "也沒有可用的 Raphael 解。請在配方設定按「Build Raphael Solution」產生解（或開啟自動產生），或提高能力值後再開始。";
        return new(Kind.Blocked, chosen,
            $"「{name}」有必要品質 {required}（品質不足會製作失敗並損失素材），不開始製作：{why}，都達不到必要品質；{advice}");
    }

    // 開著「解出來就自動切到 Raphael」時，設定檔裡的 Raphael 是 Artisan 自己寫的，不當成使用者指定。
    private static bool IsAssigned(RecipeConfig? config, ISolverDefinition.Desc desc)
        => config != null && config.CurrentSolverType.Length > 0
           && desc.Def.GetType().FullName == config.CurrentSolverType && desc.Flavour == config.CurrentSolverFlavour
           && !(desc.Def is RaphaelSolverDefintion && P.Config.RaphaelSolverConfig.AutoSwitch);

    // 先試照優先度自動選到的那個（既有行為），再試 Raphael 的解、標準解算器；使用者的巨集不自動挑。
    private static IEnumerable<ISolverDefinition.Desc> AutoCandidates(ISolverDefinition.Desc chosen, CraftState craft)
    {
        var seen = new HashSet<(Type, int)>();
        if (chosen != default && seen.Add((chosen.Def.GetType(), chosen.Flavour)))
            yield return chosen;
        foreach (var type in new[] { typeof(RaphaelSolverDefintion), typeof(StandardSolverDefinition) })
        {
            foreach (var d in CraftingProcessor.GetAvailableSolversForRecipe(craft, false))
            {
                if (d == default || d.Def.GetType() != type || !seen.Add((type, d.Flavour)))
                    continue;
                yield return d;
            }
        }
    }

    /// <summary>決定性模擬（動作必成功、狀態維持通常）做完時的品質；做不完回 -1。</summary>
    public static int SimulatedQuality(ISolverDefinition.Desc desc, CraftState craft)
    {
        var solver = desc.CreateSolver(craft);
        if (solver == null)
            return -1;
        var end = SolverUtils.SimulateSolverExecution(solver, craft, craft.InitialQuality);
        return end != null && end.Progress >= craft.CraftProgress ? end.Quality : -1;
    }

    private static string DescribeQuality(int q) => q < 0 ? "做不完" : $"只做到品質 {q}";

    private static string RecipeName(CraftState craft)
    {
        try
        {
            return craft.Recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? $"配方 {craft.RecipeId}";
        }
        catch
        {
            return $"配方 {craft.RecipeId}";
        }
    }

    // ---- 以下只在框架執行緒呼叫 ----

    private static readonly HashSet<string> Noticed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AutoBuildTried = new(StringComparer.Ordinal);
    private static string _waitKey = "";
    private static long _waitSinceTick;

    /// <summary>
    /// 自動製作按下「開始製作」之前呼叫。true＝可以開始；false＝等 Raphael，稍後再問；
    /// null＝不開始（已在聊天視窗說明原因，並關掉耐力模式／暫停清單）。
    /// 自動模式關著（技能由使用者自己按）不擋；開工那一刻 <see cref="CraftingProcessor"/> 仍會挑解算器並說明。
    /// </summary>
    internal static bool? CheckBeforeStart(Recipe recipe)
    {
        if (!(P.Config.AutoMode || Endurance.IPCOverride) || recipe.RequiredQuality == 0 || recipe.IsExpert)
            return true;
        var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
        if (CharacterInfo.JobID != job)
            return true; // 職業還沒換好算不準；開工那一刻會再判一次

        var craft = Crafting.BuildCraftStateForRecipe(CharacterStats.GetCurrentStats(), job, recipe);
        craft.InitialQuality = Simulator.GetStartingQuality(recipe, false, craft.StatLevel);
        var verdict = Evaluate(P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId), craft, RaphaelCache.InProgress(craft));

        switch (verdict.Kind)
        {
            case Kind.WaitForRaphael:
                return Wait(craft, verdict.Reason);
            case Kind.Blocked:
                if (TryStartRaphael(craft))
                    return Wait(craft, $"「{RecipeName(craft)}」有必要品質 {craft.CraftRequiredQuality}，目前的解算器達不到，已送出 Raphael 求解，算完再開始製作。");
                _waitKey = "";
                var failed = RaphaelCache.DescribeFailure(craft);
                Refuse(failed.Length > 0 ? $"{verdict.Reason}（Raphael：{failed}）" : verdict.Reason);
                return null;
            case Kind.AssignedFallsShort:
            case Kind.Ok when verdict.Reason.Length > 0:
                NoticeOnce(craft, verdict);
                break;
        }
        _waitKey = "";
        return true;
    }

    /// <summary>「指定的解算器達不到」進聊天視窗（要使用者決定）；「自動改用別的解算器」只進 log。同一組條件各說一次。</summary>
    internal static void NoticeOnce(CraftState craft, Verdict verdict)
    {
        if (Noticed.Count >= 128)
            Noticed.Clear();
        if (!Noticed.Add($"{RaphaelCache.GetFailureKey(craft)}|{verdict.Kind}|{verdict.Solver.Def?.GetType().FullName}|{verdict.Solver.Flavour}"))
            return;
        if (verdict.Kind == Kind.AssignedFallsShort)
            DuoLog.Warning($"[Artisan] {verdict.Reason}");
        else
            Svc.Log.Information($"[Artisan][必要品質] {verdict.Reason}");
    }

    private static bool TryStartRaphael(CraftState craft)
    {
        if (AutoBuildTried.Count >= 128)
            AutoBuildTried.Clear();
        // 同一組條件只自動送一次：失敗或逾時之後改成不開工並說明，不反覆重送；已有解（但達不到）也不重送。
        return !RaphaelCache.HasSolution(craft, out _) && AutoBuildTried.Add(RaphaelCache.GetFailureKey(craft)) && RaphaelCache.TryAutoBuild(craft);
    }

    private static bool? Wait(CraftState craft, string reason)
    {
        var key = RaphaelCache.GetFailureKey(craft);
        var now = Environment.TickCount64;
        if (_waitKey != key)
        {
            _waitKey = key;
            _waitSinceTick = now;
            DuoLog.Information($"[Artisan] {reason}");
        }

        // Raphael 自己會在 TimeOutMins 取消；這裡多等 30 秒當保險，超過就不再等。
        var limitMs = Math.Max(1, P.Config.RaphaelSolverConfig.TimeOutMins) * 60_000L + 30_000L;
        if (now - _waitSinceTick <= limitMs)
            return false;

        _waitKey = "";
        Refuse($"「{RecipeName(craft)}」等 Raphael 求解超過 {limitMs / 60_000.0:0.#} 分鐘仍沒有可用的解，不開始製作。請在配方設定重新產生 Raphael 解，或手動指定解算器。");
        return null;
    }

    private static void Refuse(string reason)
    {
        DuoLog.Error($"[Artisan] {reason}");
        Endurance.NotifyForcedStopIfRunning("達不到必要品質");
        if (Endurance.Enable)
            Endurance.ToggleEndurance(false);
        if (CraftingListUI.Processing)
            CraftingListFunctions.Paused = true;
    }
}
