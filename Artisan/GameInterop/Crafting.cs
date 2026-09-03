using Artisan.Autocraft;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.CraftData;
using Artisan.GameInterop.CSExt;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Linq;
using Condition = Artisan.CraftingLogic.CraftData.Condition;

namespace Artisan.GameInterop;

// state of the crafting process
// manages the 'inner loop' (executing actions to complete a single craft)
public static unsafe class Crafting
{
    public enum State
    {
        IdleNormal, // we're not crafting - the default state of the game
        Exiting, // standing up from IdleBetween to IdleNormal
        IdleBetween, // we've finished a craft and have not yet started another, sitting in the menu
        WaitStart, // we're waiting for a new (quick) craft to start
        InProgress, // crafting is in progress, waiting for next action
        WaitAction, // we've executed an action and are waiting for results
        WaitFinish, // we're waiting for a craft to end (success / failure / cancel)
        QuickCraft, // we're inside quick craft loop
        InvalidState, // we're in a state we probably shouldn't be, such as reloading the plugin mid-craft
    }

    public static State CurState { get; private set; } = State.InvalidState;
    public static event Action<State>? StateChanged;

    public static Recipe? CurRecipe { get; private set; }
    public static CraftState? CurCraft { get; private set; }
    public static StepState? CurStep { get; private set; }
    public static bool IsTrial { get; private set; }

    public static int InitialQuality;

    public static bool CanCancelQS = false;

    public static (int Cur, int Max) QuickSynthState { get; private set; }
    public static bool QuickSynthCompleted => QuickSynthState.Cur == QuickSynthState.Max && QuickSynthState.Max > 0;

    public delegate void CraftStartedDelegate(Recipe recipe, CraftState craft, StepState initialStep, bool trial);
    public static event CraftStartedDelegate? CraftStarted;

    // note: step index increases for most actions (except final appraisal / careful observation / heart&soul)
    public delegate void CraftAdvancedDelegate(Recipe recipe, CraftState craft, StepState step);
    public static event CraftAdvancedDelegate? CraftAdvanced;

    // note: final action that completes/fails a craft does not advance step index
    public delegate void CraftFinishedDelegate(Recipe recipe, CraftState craft, StepState finalStep, bool cancelled);
    public static event CraftFinishedDelegate? CraftFinished;

    public delegate void QuickSynthProgressDelegate(int cur, int max);
    public static event QuickSynthProgressDelegate? QuickSynthProgress;

    private static StepState? _predictedNextStep; // set when receiving Advance*Action messages
    private static DateTime _predictionDeadline;

    // 奇蹟之材是 45 秒的**實時** buff,不是回合制的。模擬器沒有時鐘,只能用
    // Simulator.MaterialMiracleDurationSteps 估;實機有時鐘,所以實機一律用時鐘,不用估的。
    // Environment.TickCount64 的絕對值,0 = 目前沒有在跑。
    private static long _materialMiracleExpiryTick;

    private delegate void CraftingEventHandlerUpdateDelegate(CraftingEventHandler* self, nint a2, nint a3, CraftingEventHandler.OperationId* payload);
    private static Hook<CraftingEventHandlerUpdateDelegate> _craftingEventHandlerUpdateHook;

    static Crafting()
    {
        _craftingEventHandlerUpdateHook = Svc.Hook.HookFromSignature<CraftingEventHandlerUpdateDelegate>("48 89 5C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 80 A1", CraftingEventHandlerUpdateDetour);
        _craftingEventHandlerUpdateHook.Enable();
    }

    public static void Dispose()
    {
        _craftingEventHandlerUpdateHook.Dispose();
    }

    // note: this uses current character stats & equipped gear
    public static CraftState BuildCraftStateForRecipe(CharacterStats stats, Job job, Recipe recipe)
    {
        stats.Level = stats.Level == default ? CharacterInfo.JobLevel(job) : stats.Level;
        var lt = recipe.Number == 0 && stats.Level < 100 ? Svc.Data.GetExcelSheet<RecipeLevelTable>().First(x => x.ClassJobLevel == stats.Level) : recipe.RecipeLevelTable.Value;
        // 一次查完「有沒有奇蹟之材」與「給幾次」—— 兩者走的是同一串任務資料表,
        // 分兩次呼叫等於把整段掃描做兩遍(AllValidCrafts 會對整個職業的配方逐一建 CraftState)。
        var miracle = recipe.MissionMaterialMiracle();
        var res = new CraftState()
        {
            ItemId = recipe.ItemResult.RowId,
            RecipeId = recipe.RowId, // for future cli update
            Recipe = recipe,
            StatCraftsmanship = stats.Craftsmanship,
            StatControl = stats.Control,
            StatCP = stats.CP,
            StatLevel = stats.Level,
            UnlockedManipulation = stats.Manipulation,
            Specialist = stats.Specialist,
            SplendorCosmic = stats.SplendorCosmic,
            CraftCollectible = recipe.ItemResult.Value.AlwaysCollectable,
            CraftExpert = recipe.IsExpert,
            CraftLevel = lt.ClassJobLevel,
            CraftDurability = Calculations.RecipeDurability(recipe),
            CraftProgress = recipe.Number == 0 ? Calculations.RecipeDifficulty(recipe, lt) : Calculations.RecipeDifficulty(recipe),
            CraftProgressDivider = lt.ProgressDivider,
            CraftProgressModifier = lt.ProgressModifier,
            CraftQualityDivider = lt.QualityDivider,
            CraftQualityModifier = lt.QualityModifier,
            CraftQualityMax = recipe.Number == 0 ? Calculations.RecipeMaxQuality(recipe, lt) : Calculations.RecipeMaxQuality(recipe),
            CraftRequiredQuality = (int)recipe.RequiredQuality,
            CraftRecommendedCraftsmanship = lt.SuggestedCraftsmanship,
            CraftHQ = recipe.CanHq,
            CollectableMetadataKey = recipe.CollectableMetadataKey,
            IsCosmic = recipe.Number == 0,
            ConditionFlags = (ConditionFlags)lt.ConditionsFlag,
            MissionHasMaterialMiracle = miracle.Has,
            MissionMaterialMiracleCharges = miracle.Charges,
            LevelTable = lt
        };

        if (res.CraftCollectible)
        {
            switch (res.CollectableMetadataKey)
            {
                /*
                    1 => CollectablesShopRefine
                    2 => HWDCrafterSupply
                    3 => SatisfactionSupply
                    4 => SharlayanCraftWorksSupply
                    6 => CollectablesRefined
                    7 => Cosmic, but it scales so not a sheet
                     _ => Untyped
                 */
                // HWD Recipes
                case 2:
                    var hwdRow = ECommons.GenericHelpers.FindRow<HWDCrafterSupply>(x => x.HWDCrafterSupplyParams.Any(y => y.ItemTradeIn.RowId == recipe.ItemResult.RowId));
                    if (hwdRow != null)
                    {
                        var index = hwdRow.Value.HWDCrafterSupplyParams.IndexOf(x => x.ItemTradeIn.RowId == recipe.ItemResult.RowId);
                        res.CraftQualityMin1 = hwdRow.Value.HWDCrafterSupplyParams[index].BaseCollectableRating * 10;
                        res.CraftQualityMin2 = hwdRow.Value.HWDCrafterSupplyParams[index].MidCollectableRating * 10;
                        res.CraftQualityMin3 = hwdRow.Value.HWDCrafterSupplyParams[index].HighCollectableRating * 10;
                        res.IshgardExpert = res.CraftExpert;
                    }
                    break;

                // Satisfaction Supply Recipes
                case 3:
                    var satisfactionRow = ECommons.GenericHelpers.FindRow<SatisfactionSupply>(x => x.Item.Value.RowId == recipe.ItemResult.RowId);
                    if (satisfactionRow.HasValue)
                    {
                        res.CraftQualityMin1 = satisfactionRow.Value.CollectabilityLow * 10;
                        res.CraftQualityMin2 = satisfactionRow.Value.CollectabilityMid * 10;
                        res.CraftQualityMin3 = satisfactionRow.Value.CollectabilityHigh * 10;
                    }
                    break;
                // Sharlayan
                case 4:
                    var sharlayanRow = ECommons.GenericHelpers.FindRow<SharlayanCraftWorksSupply>(x => x.Item.Any(y => y.ItemId.RowId == recipe.ItemResult.RowId));
                    if (sharlayanRow != null)
                    {
                        var it = sharlayanRow.Value.Item.First(y => y.ItemId.RowId == recipe.ItemResult.RowId);
                        res.CraftQualityMin1 = it.CollectabilityMid * 10;
                        res.CraftQualityMin2 = it.CollectabilityHigh * 10;
                    }
                    break;
                // Wachumeqimeqi
                case 6:
                    var bankaRow = ECommons.GenericHelpers.FindRow<BankaCraftWorksSupply>(x => x.Item.Any(y => y.ItemId.RowId == recipe.ItemResult.RowId));
                    if (bankaRow != null)
                    {
                        var it = bankaRow.Value.Item.First(y => y.ItemId.RowId == recipe.ItemResult.RowId);
                        res.CraftQualityMin1 = it.Collectability.Value.CollectabilityLow * 10;
                        res.CraftQualityMin2 = it.Collectability.Value.CollectabilityMid * 10;
                        res.CraftQualityMin3 = it.Collectability.Value.CollectabilityHigh * 10;
                    }
                    break;
                case 7:
                    var wksRow = ECommons.GenericHelpers.FindRow<WKSMissionToDoEvalutionRefin>(x => x.RowId == recipe.CollectableMetadata.RowId);
                    if (wksRow != null)
                    {
                        var scale = res.LevelTable.Quality * ((double)res.Recipe.QualityFactor / 100) / 1000;
                        res.CraftQualityMin1 = (int)Math.Floor(wksRow.Value.Unknown0 * scale) * 10;
                        res.CraftQualityMin2 = (int)Math.Floor(wksRow.Value.Unknown1 * scale) * 10;
                        res.CraftQualityMin3 = (int)Math.Floor(wksRow.Value.Unknown2 * scale) * 10;
                    }
                    break;
                // Check for any other Generic Collectable
                default:
                    var genericRow = ECommons.GenericHelpers.FindRow<CollectablesShopItem>(x => x.Item.Value.RowId == recipe.ItemResult.RowId);
                    if (genericRow is { CollectablesShopRefine: { } breakpoints })
                    {
                        res.CraftQualityMin1 = breakpoints.Value.LowCollectability * 10;
                        res.CraftQualityMin2 = breakpoints.Value.MidCollectability * 10;
                        res.CraftQualityMin3 = breakpoints.Value.HighCollectability * 10;
                    }
                    break;
            }

            if (res.CraftQualityMin3 == 0)
            {
                res.CraftQualityMin3 = res.CraftQualityMin2;
                res.CraftQualityMin2 = res.CraftQualityMin1;
            }
        }
        else if (recipe.RequiredQuality > 0)
        {
            res.CraftQualityMin1 = res.CraftQualityMin2 = res.CraftQualityMin3 = res.CraftQualityMax = (int)recipe.RequiredQuality;
        }
        else if (recipe.CanHq)
        {
            res.CraftQualityMin3 = res.CraftQualityMax;
        }

        return res;
    }

    public static void Update()
    {
        // typical craft loop looks like this:
        // 1. starting from IdleNormal state (no condition flags) or IdleBetween state (Crafting + PreparingToCraft condition flags)
        // 2. user presses 'craft' button
        // 2a. craft-start animation starts - this is signified by ExecutingCraftingAction condition flag, we transition to WaitStart state
        // 2b. quickly after that Crafting flag is set (if it was not already set before)
        // 2c. some time later, animation ends - at this point synth addon is updated and ExecutingCraftingAction condition flag is cleared - at this point we transition to InProgress state
        // 3. user executes an action that doesn't complete a craft
        // 3a. client sets ExecutingCraftingAction condition flag - we transition to WaitAction state
        // 3b. a bit later client receives a bunch of packets: ActorControl (to start animation), StatusEffectList (containing previous statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3c. a few seconds later client receives another bunch of packets: some misc ones, EventPlay64 (contains new crafting state - progress/quality/condition/etc), StatusEffectList (contains new statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3d. on the next frame after receiving EventPlay64, ExecutingCraftingAction flag is cleared and player is unblocked
        // 3e. sometimes EventPlay64 and final StatusEffectList might end up in a different packet bundle and can get delayed for arbitrary time (and it won't be sent at all if there are no status updates) - we transition back to InProgress state only once statuses are updated
        // 4. user executes an action that completes a craft in any way (success or failure)
        // 4a-c - same as 3a-c
        // 4d. same as 3d, however ExecutingCraftingAction flag remains set and crafting finish animation starts playing
        // 4e. as soon as we've got fully updated state, we transition to WaitFinish state
        // 4f. some time later, finish animation ends - ExecutingCraftingAction condition flag is cleared, PreparingToCraft flag is set - at this point we transition to IdleBetween state
        // 5. user exits crafting mode - condition flags are cleared, we transition to IdleNormal state
        // 6. if at some point during craft user cancels it
        // 6a. client sets ExecutingCraftingAction condition flag - we transition to WaitAction state
        // 6b. soon after, addon disappears - we detect that and transition to WaitFinish state
        // 6c. next EventPlay64 contains abort message - we ignore it for now
        // since an action can complete a craft only if it increases progress or reduces durability, we can use that to determine when to transition from WaitAction to WaitFinish
        var newState = CurState switch
        {
            State.IdleNormal => TransitionFromIdleNormal(),
            State.Exiting => TransitionFromExiting(),
            State.IdleBetween => TransitionFromIdleBetween(),
            State.WaitStart => TransitionFromWaitStart(),
            State.InProgress => TransitionFromInProgress(),
            State.WaitAction => TransitionFromWaitAction(),
            State.WaitFinish => TransitionFromWaitFinish(),
            State.QuickCraft => TransitionFromQuickCraft(),
            _ => TransitionFromInvalid()
        };
        if (newState != CurState)
        {
            Svc.Log.Debug($"Transition: {CurState} -> {newState}");
            CurState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private static State TransitionFromExiting()
    {
        if (Svc.Condition[ConditionFlag.NormalConditions])
            return State.IdleNormal;

        if (CurCraft != null && CurRecipe != null && CurStep != null)
        {
            CraftFinished?.Invoke(CurRecipe.Value, CurCraft, CurStep, true);
        }

        if (CurCraft != null)
        {
            _predictedNextStep = null;
            _predictionDeadline = default;
            _materialMiracleExpiryTick = 0;
            CurRecipe = null;
            CurCraft = null;
            CurStep = null;
            IsTrial = false;
        }

        return State.Exiting;
    }

    private static State TransitionFromInvalid()
    {
        if (Svc.Condition[ConditionFlag.Crafting] && Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.IdleBetween;

        if (!Svc.Condition[ConditionFlag.ExecutingCraftingAction] && !Svc.Condition[ConditionFlag.Crafting] && !Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.IdleNormal;

        // wrap up
        if (CurRecipe != null && CurCraft != null && CurStep != null)
        {
            CraftFinished?.Invoke(CurRecipe.Value, CurCraft, CurStep, true); // emulate cancel (TODO reconsider)
            return State.WaitFinish;
        }

        return State.InvalidState; // stay in this state until we get to one of the idle states
    }

    private static State TransitionFromIdleNormal()
    {
        if (Svc.Condition[ConditionFlag.NormalConditions])
            return State.IdleNormal;

        if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
            return State.WaitStart; // craft started, but we don't yet know details

        if (Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            Svc.Log.Error("Unexpected crafting state transition: from idle to preparing");
            return State.IdleBetween;
        }

        // stay in default state or exit crafting menu
        return State.IdleNormal;
    }

    private static State TransitionFromIdleBetween()
    {
        // note that ExecutingCraftingAction remains set after exiting from quick-synth mode
        if (Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.IdleBetween; // still in idle state

        if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
            return State.WaitStart; // craft started, but we don't yet know details

        // exit crafting menu
        return State.Exiting;
    }

    private static State TransitionFromWaitStart()
    {
        var quickSynth = GetQuickSynthAddon(); // TODO: consider updating quicksynth state to 0/max in CEH update hook and checking that here instead
        if (quickSynth != null)
        {
            CanCancelQS = true;
            return State.QuickCraft; // we've actually started quick synth
        }

        if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
            return State.WaitStart; // still waiting

        // note: addon is normally available on the same frame transition ends
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            if (Svc.Condition[ConditionFlag.NormalConditions])
                return State.IdleNormal;

            if (Svc.Condition[ConditionFlag.PreparingToCraft])
                return State.IdleBetween;

            Svc.Log.Error($"Unexpected addon state when craft should've been started");
            return State.WaitStart; // try again next frame
        }

        if (CurRecipe == null)
            return State.InvalidState; // failed to find recipe, bail out...

        var canHQ = CurRecipe.Value.CanHq;
        CurCraft = BuildCraftStateForRecipe(CharacterStats.GetCurrentStats(), CharacterInfo.JobID, CurRecipe!.Value);
        if (CurCraft != null)
            CurCraft.InitialQuality = InitialQuality;
        CurStep = BuildStepState(synthWindow, null, CurCraft);
        if (CurStep.Index != 1 || CurStep.Condition != Condition.Normal || CurStep.PrevComboAction != Skills.None)
            Svc.Log.Error($"Unexpected initial state: {CurStep}");

        IsTrial = synthWindow->AtkUnitBase.AtkValues[1] is { Type: FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte: 1 };
        CraftStarted?.Invoke(CurRecipe.Value, CurCraft, CurStep, IsTrial);
        return State.InProgress;
    }

    private static State TransitionFromInProgress()
    {
        if (!Svc.Condition[ConditionFlag.ExecutingCraftingAction])
            return State.InProgress; // when either action is executed or craft is cancelled, this condition flag will be set
        _predictedNextStep = null; // just in case, ensure it's cleared
        _predictionDeadline = default;
        return State.WaitAction;
    }

    private static State TransitionFromWaitAction()
    {
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            // craft was aborted
            CraftFinished?.Invoke(CurRecipe.Value, CurCraft!, CurStep!, true);
            return State.WaitFinish;
        }

        if (_predictedNextStep == null)
            return State.WaitAction; // continue waiting for transition

        if (_predictedNextStep.Progress >= CurCraft!.CraftProgress || _predictedNextStep.Durability <= 0)
        {
            // craft was finished, we won't get any status updates, so just wrap up
            CurStep = BuildStepState(synthWindow, _predictedNextStep, CurCraft);
            _predictedNextStep = null;
            _predictionDeadline = default;
            CraftFinished?.Invoke(CurRecipe.Value!, CurCraft, CurStep, false);
            return State.WaitFinish;
        }
        else
        {
            // action was executed, but we might not have correct statuses yet
            var step = BuildStepState(synthWindow, _predictedNextStep, CurCraft);
            if (step != _predictedNextStep)
            {
                if (DateTime.Now <= _predictionDeadline)
                {
                    Svc.Log.Debug("Waiting for status update...");
                    return State.WaitAction; // wait for a bit...
                }
                // ok, we've been waiting too long - complain and consider current state to be correct
                Svc.Log.Error($"Unexpected status update - probably a simulator bug:\n" +
                    $"     had {CurStep}\n" +
                    $"expected {_predictedNextStep}\n" +
                    $"     got {step}\n" +
                    $"   stats Craft:{CharacterInfo.Craftsmanship}, Control:{CharacterInfo.Control}, CP:{CharacterInfo.CurrentCP}/{CharacterInfo.MaxCP}, crafting #{CurRecipe?.RowId} '{CurRecipe?.ItemResult.Value.Name.ToDalamudString()}'");
            }
            CurStep = step;
            _predictedNextStep = null;
            _predictionDeadline = default;
            CraftAdvanced?.Invoke(CurRecipe.Value, CurCraft, CurStep);
            return State.InProgress;
        }
    }

    private static State TransitionFromWaitFinish()
    {
        if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
            return State.WaitFinish; // transition still in progress

        Svc.Log.Debug($"Resetting");
        _predictedNextStep = null;
        _predictionDeadline = default;
        _materialMiracleExpiryTick = 0;
        CurRecipe = null;
        P.TM.DelayNext(200);
        P.TM.Enqueue(() => CurCraft = null);
        CurStep = null;
        IsTrial = false;
        return Svc.Condition[ConditionFlag.PreparingToCraft] ? State.IdleBetween : State.IdleNormal;
    }

    private static State TransitionFromQuickCraft()
    {
        if (Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            CanCancelQS = false;
            UpdateQuickSynthState((0, 0));
            CurRecipe = null;
            return State.IdleBetween; // exit quick-craft menu
        }
        else
        {
            var quickSynth = GetQuickSynthAddon();
            UpdateQuickSynthState(quickSynth != null ? GetQuickSynthState(quickSynth) : (0, 0));
            return State.QuickCraft;
        }
    }

    private static AddonSynthesis* GetAddon()
    {
        var synthWindow = (AddonSynthesis*)Svc.GameGui.GetAddonByName("Synthesis").Address;
        if (synthWindow == null)
            return null; // not ready

        if (synthWindow->AtkUnitBase.AtkValuesCount < 26)
        {
            Svc.Log.Error($"Unexpected addon state: 0x{(nint)synthWindow:X} {synthWindow->AtkUnitBase.AtkValuesCount} {synthWindow->AtkUnitBase.UldManager.NodeListCount})");
            return null;
        }

        return synthWindow;
    }

    public static AtkUnitBase* GetCosmicAddon()
    {
        var cosmicAddon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("WKSRecipeNotebook").Address;
        if (cosmicAddon == null || !cosmicAddon->IsVisible || !cosmicAddon->IsReady)
            return null; // not ready

        return cosmicAddon;
    }

    private static AtkUnitBase* GetQuickSynthAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SynthesisSimple").Address;
        if (addon == null)
            return null;

        if (addon->AtkValuesCount < 9)
        {
            Svc.Log.Error($"Unexpected quicksynth addon state: 0x{(nint)addon:X} {addon->AtkValuesCount} {addon->UldManager.NodeListCount})");
            return null;
        }

        return addon;
    }

    private static (int cur, int max) GetQuickSynthState(AtkUnitBase* quickSynthWindow)
    {
        var cur = quickSynthWindow->AtkValues[3].Int;
        var max = quickSynthWindow->AtkValues[4].Int;
        //var succeededNQ = quickSynthWindow->AtkValues[5].Int;
        //var succeededHQ = quickSynthWindow->AtkValues[8].Int;
        //var failed = quickSynthWindow->AtkValues[6].Int;
        //var ItemId = quickSynthWindow->AtkValues[7].UInt;
        return (cur, max);
    }

    private static void UpdateQuickSynthState((int cur, int max) state)
    {
        if (QuickSynthState == state)
            return;

        QuickSynthState = state;
        Svc.Log.Debug($"Quick-synth progress update: {QuickSynthState} {Environment.TickCount64}");
        QuickSynthProgress?.Invoke(QuickSynthState.Cur, QuickSynthState.Max);
    }

    private static int GetStepIndex(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[15].Int;
    private static int GetStepProgress(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[5].Int;
    private static int GetStepQuality(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[9].Int;
    private static int GetStepDurability(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[7].Int;
    private static Condition GetStepCondition(AddonSynthesis* synthWindow) => (Condition)synthWindow->AtkUnitBase.AtkValues[12].Int;
    public static int DelineationCount() => InventoryManager.Instance()->GetInventoryItemCount(28724);

    private unsafe static uint MaterialMiracleCharges()
    {
        try
        {
            if (DutyActionManager.GetInstanceIfReady() != null)
                return (uint)(DutyActionManager.GetInstanceIfReady()->CurCharges[1] + DutyActionManager.GetInstanceIfReady()->CurCharges[0]);

            return 0;
        }
        catch (Exception e)
        {
            ECommons.GenericHelpers.Log(e);
            return 0;
        }
    }

    private unsafe static int CarefulObservationCharges()
    {
        try
        {
            return (int)ActionManager.Instance()->GetCurrentCharges(100395);
        }
        catch
        {
            return 0;
        }
    }

    private static StepState BuildStepState(AddonSynthesis* synthWindow, StepState? predictedStep, CraftState craft)
    {
        var ret = new StepState();
        ret.Index = GetStepIndex(synthWindow);
        ret.Progress = GetStepProgress(synthWindow);
        ret.Quality = GetStepQuality(synthWindow);
        ret.Durability = GetStepDurability(synthWindow);
        ret.RemainingCP = (int)CharacterInfo.CurrentCP;
        ret.Condition = GetStepCondition(synthWindow);
        ret.IQStacks = GetStatus(Buffs.InnerQuiet)?.Param ?? 0;
        ret.WasteNotLeft = GetStatus(Buffs.WasteNot2)?.Param ?? GetStatus(Buffs.WasteNot)?.Param ?? 0;
        ret.ManipulationLeft = GetStatus(Buffs.Manipulation)?.Param ?? 0;
        ret.GreatStridesLeft = GetStatus(Buffs.GreatStrides)?.Param ?? 0;
        ret.InnovationLeft = GetStatus(Buffs.Innovation)?.Param ?? 0;
        ret.VenerationLeft = GetStatus(Buffs.Veneration)?.Param ?? 0;
        ret.MuscleMemoryLeft = GetStatus(Buffs.MuscleMemory)?.Param ?? 0;
        ret.FinalAppraisalLeft = GetStatus(Buffs.FinalAppraisal)?.Param ?? 0;
        ret.CarefulObservationLeft = predictedStep is null ? craft.Specialist && craft.StatLevel >= Skills.CarefulObservation.Level() ? Math.Min(3, DelineationCount()) : 0 : Math.Min(predictedStep.CarefulObservationLeft, DelineationCount()); //Charges based on delineations, best to just use the predicted state until a proper check can be discovered
        ret.HeartAndSoulActive = GetStatus(Buffs.HeartAndSoul) != null;
        ret.HeartAndSoulAvailable = ActionManagerEx.CanUseSkill(Skills.HeartAndSoul);
        ret.TrainedPerfectionActive = GetStatus(Buffs.TrainedPerfection) != null;
        ret.TrainedPerfectionAvailable = ActionManagerEx.CanUseSkill(Skills.TrainedPerfection);
        ret.QuickInnoAvailable = ActionManagerEx.CanUseSkill(Skills.QuickInnovation);
        ret.QuickInnoLeft = !craft.Specialist ? 0 : ActionManagerEx.CanUseSkill(Skills.QuickInnovation) ? 1 : predictedStep?.QuickInnoLeft ?? 0;
        // Expedience is a one-use proc; the only consumer treats ExpedienceLeft as a
        // >0 / ==0 binary check. On the TC client the status's Param can read back 0
        // while the buff is still present, so presence of the status (not its Param)
        // is the reliable signal here.
        ret.ExpedienceLeft = GetStatus(Buffs.Expedience) != null ? 1 : 0;
        ret.PrevActionFailed = predictedStep?.PrevActionFailed ?? false;
        ret.PrevComboAction = predictedStep?.PrevComboAction ?? Skills.None;
        ret.MaterialMiracleCharges = MaterialMiracleCharges();
        // 🔑 buff 在不在,一律以遊戲的狀態列為準,時鐘只負責「還剩多少」。
        ret.MaterialMiracleActive = GetStatus(Buffs.MaterialMiracle) != null;
        // 遊戲只告訴我們 buff 在不在,不告訴我們模擬器的「還剩幾步」。所以沿用預測值
        // (預測那邊已經是真時鐘算出來的),只在沒有預測可沿用時才自己換算一次。
        // 🔑 這樣寫的重點是 StepState 是 record,`step != _predictedNextStep` 會逐欄比對 ——
        //    若這裡自己算一份跟預測不同的數字,反而會製造出新的「狀態不合」誤報。
        //    時鐘是連續的,而這裡與 detour 的呼叫時間差了幾百毫秒,兩邊各算一次必定偶爾差一格。
        // ⚠️ 這裡**刻意不去清 _materialMiracleExpiryTick** —— 剛用掉奇蹟之材的那幾幀狀態列
        //    還沒更新(狀態要等下一個 StatusEffectList),此時 Active 是暫時的 false,
        //    清掉就等於把剛設好的 45 秒丟了,之後整段 buff 都只會被估成「還剩 1 步」。
        //    時鐘活過頭不會造成誤判:算剩餘步數的兩處都先過 Active 這一關。
        ret.MaterialMiracleStepsLeft = predictedStep is { MaterialMiracleStepsLeft: > 0 } p && ret.MaterialMiracleActive
            ? p.MaterialMiracleStepsLeft
            : MaterialMiracleStepsLeftFromClock(ret.MaterialMiracleActive);
        ret.ObserveCounter = predictedStep?.ObserveCounter ?? 0;

        return ret;
    }

    /// <summary>
    /// 奇蹟之材還剩幾個「模擬步數」—— 實機用 <c>Environment.TickCount64</c> 的真時鐘換算,
    /// 而不是 <see cref="Simulator.MaterialMiracleDurationSteps"/> 那個步數估計值。
    /// </summary>
    /// <param name="buffActive">
    /// 遊戲狀態列現在有沒有這個 buff。**它決定「有沒有」,時鐘只決定「還剩多少」**:
    /// 遊戲說沒了就回 0;遊戲說還在就至少回 1(即使我們的時鐘已經跑完,或是根本沒有時鐘 ——
    /// 例如製作到一半才載入外掛,buff 已經在身上了)。
    /// </param>
    private static int MaterialMiracleStepsLeftFromClock(bool buffActive)
    {
        if (!buffActive)
            return 0;
        if (_materialMiracleExpiryTick == 0)
            return 1; // 沒有起算點:只知道還在,不知道剩多久 —— 給最保守的 1 步
        var remainingMs = _materialMiracleExpiryTick - Environment.TickCount64;
        if (remainingMs <= 0)
            return 1; // 時鐘跑完了但遊戲說還在 —— 以遊戲為準
        return Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0 / Simulator.MaterialMiracleSecondsPerStep));
    }

    private static Dalamud.Game.ClientState.Statuses.Status? GetStatus(uint statusID) => Svc.Objects.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == statusID);

    // 這支 detour 內部例外的節流計時器(Environment.TickCount64 的絕對值)。
    // ⚠️ 刻意**不用** Artisan.Autocraft.Throttler —— 那是全外掛共用的單一節流窗,
    //    拿它記 log 會把製作動作的節流窗一起吃掉。
    private static long _nextDetourErrorLogTick;

    /// <summary>
    /// detour 內部出受管理例外時的節流記錄。**Information 級是刻意的** —— 使用者跑 LogLevel 1,
    /// 盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒,而這正是我們要使用者回報的東西。
    /// </summary>
    private static void LogDetourException(string what, Exception ex)
    {
        if (Environment.TickCount64 < _nextDetourErrorLogTick)
            return;
        _nextDetourErrorLogTick = Environment.TickCount64 + 10000;
        Svc.Log.Information(ex, $"[Artisan] {what} detour 內部發生例外,已略過這一次的狀態解讀,遊戲流程照常轉交(10 秒內不重複記錄)");
    }

    private static void CraftingEventHandlerUpdateDetour(CraftingEventHandler* self, nint a2, nint a3, CraftingEventHandler.OperationId* payload)
    {
        // fail-closed:我們自己的解讀邏輯全部包進 try。無論成功或擲例外,
        // **一定照樣呼叫 Original 並讓遊戲流程繼續** —— 吞掉 Original 等於把製作流程整個弄斷。
        // ⚠️ 這裡攔得到的是**受管理例外**(Lumina 查表失敗擲的 ArgumentOutOfRangeException、
        //    NullReferenceException、特徵碼未解析的 InvalidOperationException 等)。
        //    懸空指標造成的 AccessViolationException 是 corrupted-state exception,**攔不到**。
        try
        {
            HandleCraftingEvent(payload);
        }
        catch (Exception ex)
        {
            LogDetourException("CraftingEventHandlerUpdate", ex);
        }

        _craftingEventHandlerUpdateHook.OriginalDisposeSafe(self, a2, a3, payload);
        Svc.Log.Verbose("CEH hook exit");
    }

    /// <summary>
    /// CEH 訊息的解讀邏輯。**這裡不呼叫 Original** —— 呼叫者負責,所以本函式的任何提早 return
    /// 或擲出的例外都不會讓遊戲少收到一次原函式。
    /// </summary>
    private static void HandleCraftingEvent(CraftingEventHandler.OperationId* payload)
    {
        if (payload == null)
            return;

        Svc.Log.Verbose($"CEH hook: {*payload}");
        switch (*payload)
        {
            case CraftingEventHandler.OperationId.StartPrepare:
                // this is sent immediately upon starting (quick) synth and does nothing interesting other than resetting the state
                // transition (ExecutingCraftingAction) is set slightly earlier by client when initiating the craft
                // the actual crafting states (setting Crafting and clearing PreparingToCraft) set in response to this message
                if (CurState is not State.WaitStart and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.StartInfo:
                // this is sent few 100s of ms after StartPrepare for normal synth and contains details of the recipe
                // client stores the information in payload in event handler, but we continue waiting
                var startPayload = (CraftingEventHandler.StartInfo*)payload;
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                Svc.Log.Debug($"Starting craft: recipe #{startPayload->RecipeId}, initial quality {startPayload->StartingQuality}, u8={startPayload->u8}");
                if (CurRecipe != null)
                    Svc.Log.Error($"Unexpected non-null recipe when receiving {*payload} message");
                // 🔴 一定要用 GetRowOrDefault:Lumina 的 GetRow(uint) 對不存在的列**擲
                //    ArgumentOutOfRangeException,永遠不回 null**(`?.` 只擋 sheet 為 null)。
                //    用 GetRow 的話下面那個 null 檢查對「列不存在」是死碼。
                CurRecipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>()?.GetRowOrDefault(startPayload->RecipeId);
                if (CurRecipe == null)
                    Svc.Log.Error($"Failed to find recipe #{startPayload->RecipeId}");

                InitialQuality = startPayload->StartingQuality;
                // note: we could build CurCraft and CurStep here
                break;
            case CraftingEventHandler.OperationId.StartReady:
                // this is sent few 100s of ms after StartInfo for normal synth and instructs client to start synth session - set up addon, etc
                // transition (ExecutingCraftingAction) will be cleared in a few frames
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.Finish:
                // this is sent few seconds after last action that completed the craft or quick synth and instructs client to exit the finish transition
                // transition (ExecutingCraftingAction) is cleared in response to this message
                if (CurState is not State.WaitFinish and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.Abort:
                // this is sent immediately upon aborting synth
                // transition (ExecutingCraftingAction) is set slightly earlier by client when aborting the craft
                // actual craft state (Crafting) is cleared several seconds later
                // currently we rely on addon disappearing to detect aborts (for robustness), it can happen either before or after Abort message
                if (CurState is not State.WaitAction and not State.WaitFinish and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                if (_predictedNextStep != null)
                    Svc.Log.Error($"Unexpected non-null predicted-next when receiving {*payload} message");
                if (CurCraft is not null && CurCraft.IsCosmic && Endurance.Enable)
                    Endurance.ToggleEndurance(false);

                CurState = State.Exiting;
                break;
            case CraftingEventHandler.OperationId.AdvanceCraftAction:
            case CraftingEventHandler.OperationId.AdvanceNormalAction:
                // this is sent a few seconds after using an action and contains action result
                // in response to this action, client updates the addon data, prints log message and clears consumed statuses (mume, gs, etc)
                // transition (ExecutingCraftingAction) will be cleared in a few frames, if this action did not complete the craft
                // if there are any status changes (e.g. remaining step updates) and if craft is not complete, these will be updated by the next StatusEffectList packet, which might arrive with a delay
                // because of that, we wait until statuses match prediction (or too much time passes) before transitioning to InProgress
                // 🔴 原本寫成 `is not State.WaitAction or State.InProgress`,C# 會解析成
                //    `(not WaitAction) or InProgress` —— 而 InProgress 本來就屬於 not WaitAction,
                //    整條等價於 `!= WaitAction`,上游 2024-01-11「Fix errors」想加進白名單的
                //    InProgress **從來沒生效過**,反而每次都落進下面的錯誤路徑並跳過預測邏輯。
                if (CurState is not State.WaitAction and not State.InProgress)
                {
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message"); //Probably an invalid state, so most data will not be set causing CTD
                    // 只跳過我們的解讀;Original 由呼叫端統一呼叫,行為與改寫前相同。
                    return;
                }
                if (_predictedNextStep != null)
                {
                    Svc.Log.Error($"Unexpected non-null predicted-next when receiving {*payload} message");
                    _predictedNextStep = null;
                }
                var advancePayload = (CraftingEventHandler.AdvanceStep*)payload;
                bool complete = advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.CompleteSuccess) || advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.CompleteFail);
                Svc.Log.Debug($"AdvanceActionComplete: {complete}");
                var usedMaterialMiracle = advancePayload->LastActionId == (uint)Skills.MaterialMiracle;
                _predictedNextStep = Simulator.Execute(CurCraft!, CurStep!, usedMaterialMiracle ? Skills.MaterialMiracle : SkillActionMap.ActionToSkill(advancePayload->LastActionId), advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.LastActionSucceeded) ? 0 : 1, 1).Item2;
                _predictedNextStep.Condition = (Condition)(advancePayload->ConditionPlus1 - 1);
                // fix up predicted state to match what game sends
                if (complete)
                    _predictedNextStep.Index = CurStep.Index; // step is not advanced for final actions
                // 奇蹟之材:模擬器剛剛用「步數估計」算了一份,這裡用真時鐘覆蓋掉。
                // ⚠️ 起算點是 Advance 訊息抵達的瞬間,比伺服器真正套用 buff 晚了一個延遲,
                //    所以我們的時鐘一定「比遊戲晚收」—— 因此 mmActive 一律先問狀態列,
                //    遊戲說沒了就是沒了,時鐘活過頭也翻不了案。
                // ⚠️ 唯一的例外是剛用掉的那一步:狀態列還沒更新(狀態要等下一個 StatusEffectList
                //    才會到),所以「這一步就是奇蹟之材」本身要當成 active,不能只看狀態列。
                if (usedMaterialMiracle)
                    _materialMiracleExpiryTick = Environment.TickCount64 + (long)(Simulator.MaterialMiracleDurationSeconds * 1000);
                var mmActive = usedMaterialMiracle || GetStatus(Buffs.MaterialMiracle) != null;
                _predictedNextStep.MaterialMiracleStepsLeft = MaterialMiracleStepsLeftFromClock(mmActive);
                _predictedNextStep.MaterialMiracleActive = _predictedNextStep.MaterialMiracleStepsLeft > 0;
                _predictedNextStep.Progress = Math.Min(_predictedNextStep.Progress, CurCraft.CraftProgress);
                _predictedNextStep.Quality = Math.Min(_predictedNextStep.Quality, CurCraft.CraftQualityMax);
                _predictedNextStep.Durability = Math.Max(_predictedNextStep.Durability, 0);
                // validate sim predictions
                if (_predictedNextStep.Index != advancePayload->StepIndex)
                    Svc.Log.Error($"Prediction error: expected step #{advancePayload->StepIndex}, got {_predictedNextStep.Index}");
                if (_predictedNextStep.Progress != advancePayload->CurProgress)
                    Svc.Log.Error($"Prediction error: expected progress {advancePayload->CurProgress}, got {_predictedNextStep.Progress}");
                if (_predictedNextStep.Quality != advancePayload->CurQuality)
                    Svc.Log.Error($"Prediction error: expected quality {advancePayload->CurQuality}, got {_predictedNextStep.Quality}");
                if (_predictedNextStep.Durability != advancePayload->CurDurability)
                    Svc.Log.Error($"Prediction error: expected durability {advancePayload->CurDurability}, got {_predictedNextStep.Durability}");
                var predictedDeltaProgress = _predictedNextStep.PrevActionFailed ? 0 : Simulator.CalculateProgress(CurCraft!, CurStep!, _predictedNextStep.PrevComboAction);
                var predictedDeltaQuality = _predictedNextStep.PrevActionFailed ? 0 : Simulator.CalculateQuality(CurCraft!, CurStep!, _predictedNextStep.PrevComboAction);
                var predictedDeltaDurability = _predictedNextStep.PrevComboAction == Skills.MastersMend ? 30 : _predictedNextStep.PrevComboAction == Skills.ImmaculateMend ? 100 : -Simulator.GetDurabilityCost(CurStep!, _predictedNextStep.PrevComboAction);
                if (predictedDeltaProgress != advancePayload->DeltaProgress)
                    Svc.Log.Error($"Prediction error: expected progress delta {advancePayload->DeltaProgress}, got {predictedDeltaProgress}");
                if (predictedDeltaQuality != advancePayload->DeltaQuality)
                    Svc.Log.Error($"Prediction error: expected quality delta {advancePayload->DeltaQuality}, got {predictedDeltaQuality}");
                if (predictedDeltaDurability != advancePayload->DeltaDurability)
                    Svc.Log.Error($"Prediction error: expected durability delta {advancePayload->DeltaDurability}, got {predictedDeltaDurability}");
                if ((_predictedNextStep.Progress >= CurCraft!.CraftProgress || _predictedNextStep.Durability <= 0) != complete)
                    Svc.Log.Error($"Prediction error: unexpected completion state diff (got {complete})");
                _predictionDeadline = DateTime.Now.AddSeconds(0.5f); // if we don't get status effect list quickly enough, bail out...
                break;
            case CraftingEventHandler.OperationId.QuickSynthStart:
                // this is sent a few seconds after StartPrepare for quick synth and contains details of the recipe
                // client stores the information in payload in event handler and opens the addon
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                var quickSynthPayload = (CraftingEventHandler.QuickSynthStart*)payload;
                Svc.Log.Debug($"Starting quicksynth: recipe #{quickSynthPayload->RecipeId}, count {quickSynthPayload->MaxCount}");
                if (CurRecipe != null)
                    Svc.Log.Error($"Unexpected non-null recipe when receiving {*payload} message");
                // 🔴 同上:GetRow 對不存在的列擲例外,GetRowOrDefault 才回 null。
                CurRecipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>()?.GetRowOrDefault(quickSynthPayload->RecipeId);
                if (CurRecipe == null)
                    Svc.Log.Error($"Failed to find recipe #{quickSynthPayload->RecipeId}");
                break;
            case CraftingEventHandler.OperationId.QuickSynthProgress:
                // this is sent a ~second after ActorControl that contains the actual new counts
                if (CurState != State.QuickCraft)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
        }
    }
}
