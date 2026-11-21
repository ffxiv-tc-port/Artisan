using Artisan.Autocraft;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using System;
using System.Collections.Generic;

namespace Artisan.CraftingLogic;

/// <summary>
/// 宇宙探索配方「沒有指定解算器」時改用 Raphael（設定 <see cref="RaphaelSolverSettings.CosmicUnassignedRaphael"/>＋自動切換）。
/// 契約：
/// - 快取鍵不含職業、各職業能力值常相同，而自動切換只寫產生解的那個職業的配方 ⇒ 沒指定的配方要在挑解算器時自己認同條件的解。
/// - 由其他插件經 IPC 驅動的製作，開工前先用實機能力值與起始品質求解，最多等 <see cref="WaitLimit"/>；
///   求不出來、逾時都照舊開工（自動挑到的解算器），並寫一行 Information。永遠不因此擋到不開工。
/// - 有指定（設定檔或臨時解算器）的配方一律不碰。除 <see cref="PreferRaphael"/> 外只在框架執行緒呼叫。
/// </summary>
internal static class CosmicRaphaelGate
{
    // 實機求解約 1～16 秒（含 --adversarial）；限時任務不能被拖太久，同一組條件只等這一次。
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> Attempted = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Noticed = new(StringComparer.Ordinal);

    private static bool Enabled(CraftState craft)
    {
        var cfg = P.Config.RaphaelSolverConfig;
        return craft.IsCosmic && cfg.CosmicUnassignedRaphael && cfg.AutoSwitch && (!craft.CraftExpert || cfg.GenerateOnExperts);
    }

    /// <summary>沒有指定解算器的宇宙配方：有符合目前條件的 Raphael 解就回傳它，否則 null（照舊依優先度挑）。</summary>
    internal static ISolverDefinition.Desc? PreferRaphael(CraftState craft)
    {
        if (!Enabled(craft))
            return null;
        foreach (var def in CraftingProcessor.SolverDefinitions)
        {
            if (def is not RaphaelSolverDefintion)
                continue;
            foreach (var f in def.Flavours(craft))
                if (f.UnsupportedReason.Length == 0)
                    return f;
        }
        return null;
    }

    /// <summary>
    /// 配方視窗的自動產生要不要讓給 <see cref="CheckBeforeStart"/>：那邊用實機能力值、有等待上限、失敗不關耐力模式；
    /// 視窗那條路逾時是整整 TimeOutMins 分鐘，失敗還會關掉耐力模式。
    /// </summary>
    internal static bool OwnsAutoBuild(RecipeConfig config, CraftState craft)
        => Endurance.IPCOverride && config.CurrentSolverType.Length == 0 && Enabled(craft);

    /// <summary>IPC 驅動的宇宙製作按下開始之前呼叫。true＝可以開始；false＝Raphael 求解中，稍後再問。</summary>
    internal static bool CheckBeforeStart(uint recipeId)
    {
        if (!Endurance.IPCOverride || recipeId == 0 || LuminaSheets.RecipeSheet == null
            || !LuminaSheets.RecipeSheet.TryGetValue(recipeId, out var recipe) || recipe.Number != 0)
            return true;
        if (!P.Config.RaphaelSolverConfig.AutoGenerate || (P.Config.RecipeConfigs.GetValueOrDefault(recipeId)?.CurrentSolverType.Length ?? 0) > 0)
            return true;
        var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
        if (CharacterInfo.JobID != job)
            return true; // 職業還沒換好算不準；開工那一刻 NoticeAtStart 會說明

        var craft = Crafting.BuildCraftStateForRecipe(CharacterStats.GetCurrentStats(), job, recipe);
        craft.InitialQuality = Simulator.GetStartingQuality(recipe, false, craft.StatLevel);
        if (!Enabled(craft) || !QualityMatters(craft))
            return true;
        if (RaphaelCache.HasSolution(craft, out var cached))
        {
            // 舊解可能是後置進展下求的閒靜起手、或確保可靠度下沒做到最高品質：等一次重求（同樣最多 WaitLimit），求不出來就用舊解開工。
            if (RaphaelCache.FreeOpenerRefreshInProgress(craft) || RaphaelCache.ReliabilityRefreshInProgress(craft)
                || RaphaelCache.TryRefreshFreeOpener(craft, cached!, null, WaitLimit) || RaphaelCache.TryRefreshReliability(craft, cached!, null, WaitLimit))
                return false;
            return true;
        }
        if (RaphaelCache.InProgress(craft))
            return false;

        var key = RaphaelCache.GetFailureKey(craft);
        if (RaphaelCache.DescribeFailure(craft).Length == 0 && !Attempted.Contains(key))
        {
            if (Attempted.Count >= 128)
                Attempted.Clear();
            Attempted.Add(key);
            if (RaphaelCache.TryAutoBuild(craft, WaitLimit))
            {
                Svc.Log.Information($"[Artisan][宇宙 Raphael] {Describe(craft)}開工前先用目前的能力值 {craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}、"
                    + $"起始品質 {craft.InitialQuality} 求 Raphael 解，最多等 {WaitLimit.TotalSeconds:0} 秒。");
                return false;
            }
        }
        NoticeOnce(craft, $"開工前沒有可用的 Raphael 解，照常開工：{Reason(craft)}");
        return true;
    }

    /// <summary>開工那一刻：沒指定解算器、這個功能開著，卻不是用 Raphael 開工 ⇒ 說明原因（同一組條件一次）。</summary>
    internal static void NoticeAtStart(RecipeConfig? config, CraftState craft, ISolverDefinition.Desc chosen)
    {
        if (!Endurance.IPCOverride || !P.Config.RaphaelSolverConfig.AutoGenerate || !Enabled(craft) || !QualityMatters(craft)
            || (config?.CurrentSolverType.Length ?? 0) > 0 || chosen.Def is RaphaelSolverDefintion or null)
            return;
        var ignored = RaphaelCache.DescribeIgnoredSolution(craft);
        NoticeOnce(craft, $"用「{chosen.Name}」開工、沒有用 Raphael：{(ignored.Length > 0 ? ignored.Replace("\n", "；") : Reason(craft))}");
    }

    private static bool QualityMatters(CraftState craft) => craft.CraftQualityMax > 0 && (craft.CraftHQ || craft.CraftCollectible);

    private static string Reason(CraftState craft)
    {
        var failed = RaphaelCache.DescribeFailure(craft);
        if (failed.Length > 0)
            return failed;
        if (!RaphaelCache.CLIExists())
            return "找不到 raphael-cli.exe";
        return Attempted.Contains(RaphaelCache.GetFailureKey(craft))
            ? $"Raphael 在 {WaitLimit.TotalSeconds:0} 秒內沒有產生可用的解"
            : "這組能力值與起始品質還沒有 Raphael 解";
    }

    private static void NoticeOnce(CraftState craft, string message)
    {
        if (Noticed.Count >= 128)
            Noticed.Clear();
        if (Noticed.Add(RaphaelCache.GetFailureKey(craft)))
            Svc.Log.Information($"[Artisan][宇宙 Raphael] {Describe(craft)}{message}");
    }

    private static string Describe(CraftState craft)
    {
        string name;
        try
        {
            name = craft.Recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? "";
        }
        catch
        {
            name = "";
        }
        return $"「{name}」（配方 {craft.RecipeId}）";
    }
}
