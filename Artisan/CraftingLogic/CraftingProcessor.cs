using Artisan.Autocraft;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.CraftingLogic;

// monitors crafting state changes and provides recommendation based on assigned solver algorithm
// TODO: toasts etc should be moved outside - this should provide events instead
public static class CraftingProcessor
{
    public static Solver.Recommendation NextRec => _nextRec;
    public static SolverRef ActiveSolver = new("");

    public delegate void SolverStartedDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState initialStep);
    public static event SolverStartedDelegate? SolverStarted;

    public delegate void SolverFailedDelegate(Lumina.Excel.Sheets.Recipe recipe, string reason);
    public static event SolverFailedDelegate? SolverFailed; // craft started, but solver couldn't

    public delegate void SolverFinishedDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState finalStep);
    public static event SolverFinishedDelegate? SolverFinished;

    public delegate void RecommendationReadyDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState step, Solver.Recommendation recommendation);
    public static event RecommendationReadyDelegate? RecommendationReady;

    public static List<ISolverDefinition> SolverDefinitions = new();
    private static Solver? _activeSolver; // solver for current or expected crafting session
    private static uint? _expectedRecipe; // non-null and equal to recipe id if we've requested start of a specific craft (with a specific solver) and are waiting for it to start
    private static Solver.Recommendation _nextRec;

    public static void Setup()
    {
        SolverDefinitions.Add(new StandardSolverDefinition());
        SolverDefinitions.Add(new ProgressOnlySolverDefinition());
        SolverDefinitions.Add(new ExpertSolverDefinition());
        SolverDefinitions.Add(new MacroSolverDefinition());
        SolverDefinitions.Add(new ScriptSolverDefinition());
        SolverDefinitions.Add(new RaphaelSolverDefintion());

        Crafting.CraftStarted += OnCraftStarted;
        Crafting.CraftAdvanced += OnCraftAdvanced;
        Crafting.CraftFinished += OnCraftFinished;
    }

    public static void Dispose()
    {
        Crafting.CraftStarted -= OnCraftStarted;
        Crafting.CraftAdvanced -= OnCraftAdvanced;
        Crafting.CraftFinished -= OnCraftFinished;
    }

    public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForRecipe(CraftState craft, bool returnUnsupported, Type? skipSolver = null)
    {
        foreach (var solver in SolverDefinitions)
        {
            if (solver.GetType() == skipSolver)
                continue;

            foreach (var f in solver.Flavours(craft))
            {
                if (returnUnsupported || f.UnsupportedReason.Length == 0)
                {
                    yield return f;
                }
            }
            yield return default;
        }
    }

    public static ISolverDefinition.Desc? FindSolver(CraftState craft, string type, int flavour)
    {
        var solver = type.Length > 0 ? SolverDefinitions.Find(s => s.GetType().FullName == type) : null;
        if (solver == null)
            return null;

        foreach (var f in solver.Flavours(craft).Where(f => f.Flavour == flavour))
            return f;
        return null;
    }

    public static ISolverDefinition.Desc GetSolverForRecipe(RecipeConfig? recipeConfig, CraftState craft)
    {
        var s = FindSolver(craft, recipeConfig?.CurrentSolverType ?? "", recipeConfig?.CurrentSolverFlavour ?? 0);
        if (s != null)
            return s.Value;

        // The recipe has a solver explicitly assigned, but that definition currently offers no matching
        // flavour at all (its Flavours() yielded nothing). Falling straight through to MaxBy(Priority)
        // swaps in the standard solver without telling anyone. Gate that behind the setting, keeping the
        // Def pointing at the real definition so CreateSolver() on this Desc still cannot null-deref.
        // ⚠️ FallbackToStandardAllowed 不只看使用者那個開關:由 IPC 驅動的製作(ICE)一律不准降級。
        // 🔴 這裡要看**生效中**的解算器,不是設定檔裡的那個。臨時解算器正是 IPC 設進來的
        //    (ICE),而這段擋的就是「IPC 驅動時不准無聲降級」——用 SolverType 會讓臨時
        //    解算器不可用時直接掉進下面的 MaxBy(Priority),正好繞過這個閘門。
        var configuredType = recipeConfig?.CurrentSolverType ?? "";
        if (configuredType.Length > 0 && !RaphaelCache.FallbackToStandardAllowed)
        {
            var configuredDef = SolverDefinitions.Find(x => x.GetType().FullName == configuredType);
            if (configuredDef != null)
                return new ISolverDefinition.Desc(configuredDef, recipeConfig?.CurrentSolverFlavour ?? 0, 0,
                    "(assigned solver unavailable)".Loc(),
                    "The solver assigned to this recipe is not available right now.".Loc());
        }

        var s2 = GetAvailableSolversForRecipe(craft, false);
        if (s2.Count() > 0)
            return s2.MaxBy(x => x.Priority);

        return default;
    }

    private static void OnCraftStarted(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState initialStep, bool trial)
    {
        Svc.Log.Debug($"[CProc] OnCraftStarted #{recipe.RowId} '{recipe.ItemResult.Value.Name.ToDalamudString()}' (trial={trial}) (cosmic={craft.IsCosmic}) (IQ={craft.InitialQuality}) (PQ={craft.CraftProgress}/{craft.CraftQualityMax})");
        if (_expectedRecipe != null && _expectedRecipe.Value != recipe.RowId)
        {
            Svc.Log.Error($"Unexpected recipe started: expected {_expectedRecipe}, got {recipe.RowId}");
            _activeSolver = null; // something wrong has happened
            ActiveSolver = new("");
        }
        _expectedRecipe = null;
        // we don't want any solvers running with broken gear
        if (RepairManager.GetMinEquippedPercent() == 0)
        {
            SolverFailed?.Invoke(recipe, "You have broken gear");
            _activeSolver = null;
            ActiveSolver = new("");
            return;
        }

        if (_activeSolver == null)
        {
            // if we didn't provide an explicit solver, create one - but make sure if we have manually assigned one, it is actually supported
            var autoSolver = GetSolverForRecipe(P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId), craft);
            if (autoSolver.UnsupportedReason.Length > 0)
            {
                SolverFailed?.Invoke(recipe, autoSolver.UnsupportedReason);
                return;
            }
            _activeSolver = autoSolver.CreateSolver(craft);
            if (_activeSolver == null)
            {
                // GetSolverForRecipe returns default when nothing at all is available, and CreateSolver on a
                // default Desc returns null. Everything below dereferences _activeSolver unconditionally, so
                // without this the craft starts and then throws NRE out of the framework update.
                SolverFailed?.Invoke(recipe, "No solver is available for this craft.".Loc());
                ActiveSolver = new("");
                return;
            }
            ActiveSolver = new(autoSolver.Name, _activeSolver);
        }

        if (_activeSolver is ICraftValidator validator)
        {
            Svc.Log.Information("Validation");
            var validation = validator.Validate(craft);
            if (!validation)
            {
                SolverFailed?.Invoke(recipe, "You have mismatched stats");
                _activeSolver = null;
                ActiveSolver = new("");
                return;
            }
        }

        SolverStarted?.Invoke(recipe, ActiveSolver, craft, initialStep);

        _nextRec = _activeSolver.Solve(craft, initialStep);
        // 奇蹟之材的閘門判定在上面那一行就跑完了。**在這裡**記 log 而不是在解算器內部,是因為
        // 同一個解算器也被配方視窗的提示取樣器拿去跑上百次模擬(見 SolverHintSampler),
        // 在裡面記會把 log 洗掉;而這裡保證是實機的那一場製作。
        // Information 級是刻意的:使用者跑 LogLevel 2,Debug/Verbose 收不到。
        if (_activeSolver is Solvers.MaterialMiracleSolver mmSolver && mmSolver.LastGateExplanation is { Length: > 0 } mmWhy)
            Svc.Log.Information($"[MaterialMiracle] {mmWhy}");
        if (Simulator.CannotUseAction(craft, initialStep, _nextRec.Action, out string reason))
            DuoLog.Error($"Unable to use {_nextRec.Action.NameOfAction()}: {reason}");
        if (_nextRec.Action != Skills.None)
            RecommendationReady?.Invoke(recipe, ActiveSolver, craft, initialStep, _nextRec);
    }

    private static void OnCraftAdvanced(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState step)
    {
        Svc.Log.Debug($"[CProc] OnCraftAdvanced #{recipe.RowId} (solver={ActiveSolver.Name}): {step}");
        if (_activeSolver == null)
            return;
        if (_nextRec.Action != Skills.None && _nextRec.Action != step.PrevComboAction)
            Svc.Log.Warning($"Previous action was different from recommendation: recommended {_nextRec.Action}, used {step.PrevComboAction}");

        _nextRec = _activeSolver.Solve(craft, step);
        // 標準解算器的「奇蹟之材期間要不要交給專家解算器代打」閘門是在製作**中途**(buff 生效那一步)
        // 才跑的,所以不能像 MaterialMiracleSolver 那樣只在 OnCraftStarted 讀一次。
        // 讀走即清 ⇒ 一場製作只會印一行。Information 級是刻意的:使用者跑 LogLevel 2。
        if (_activeSolver is Solvers.StandardSolver stdSolver && stdSolver.ConsumeGateExplanation() is { Length: > 0 } stdWhy)
            Svc.Log.Information($"[MaterialMiracle] {stdWhy}");
        Svc.Log.Debug($"Next rec is: {_nextRec.Action}");
        if (Simulator.CannotUseAction(craft, step, _nextRec.Action, out string reason))
            DuoLog.Error($"Unable to use {_nextRec.Action.NameOfAction()}: {reason}");
        if (_nextRec.Action != Skills.None)
            RecommendationReady?.Invoke(recipe, ActiveSolver, craft, step, _nextRec);
    }

    private static void OnCraftFinished(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState finalStep, bool cancelled)
    {
        Svc.Log.Debug($"[CProc] OnCraftFinished #{recipe.RowId} (cancel={cancelled}, solver={ActiveSolver.Name}): {finalStep}");
        if (_activeSolver == null)
            return;
        if (!cancelled && _nextRec.Action != Skills.None && _nextRec.Action != finalStep.PrevComboAction)
            Svc.Log.Warning($"Previous action was different from recommendation: recommended {_nextRec.Action}, used {finalStep.PrevComboAction}");

        SolverFinished?.Invoke(recipe, ActiveSolver, craft, finalStep);
        _activeSolver = null;
        ActiveSolver = new("");
        _nextRec = new();
    }
}
