using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Interface.Components;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using System;
using static Artisan.RawInformation.AddonExtensions;

namespace Artisan.CraftingLogic.Solvers;

public class ExpertSolverSettings
{
    public bool MaxIshgardRecipes;
    public bool UseReflectOpener;
    public bool MuMeIntensiveGood = true; // if true, we allow spending mume on intensive (400p) rather than rapid (500p) if good condition procs
    public bool MuMeIntensiveMalleable = false; // if true and we have malleable during mume, use intensive rather than hoping for rapid
    public bool MuMeIntensiveLastResort = true; // if true and we're on last step of mume, use intensive (forcing via H&S if needed) rather than hoping for rapid (unless we have centered)
    public bool MuMePrimedManip = false; // if true, allow using primed manipulation after veneration is up on mume
    public bool MuMeAllowObserve = false; // if true, observe rather than use actions during unfavourable conditions to conserve durability
    public int MuMeMinStepsForManip = 2; // if this or less rounds are remaining on mume, don't use manipulation under favourable conditions
    public int MuMeMinStepsForVene = 1; // if this or less rounds are remaining on mume, don't use veneration
    public int MidMinIQForHSPrecise = 10; // min iq stacks where we use h&s+precise; 10 to disable
    public bool MidBaitPliantWithObservePreQuality = true; // if true, when very low on durability and without manip active during pre-quality phase, we use observe rather than normal manip
    public bool MidBaitPliantWithObserveAfterIQ = true; // if true, when very low on durability and without manip active after iq has 10 stacks, we use observe rather than normal manip or inno+finnesse
    public bool MidPrimedManipPreQuality = true; // if true, allow using primed manipulation during pre-quality phase
    public bool MidPrimedManipAfterIQ = true; // if true, allow using primed manipulation during after iq has 10 stacks
    public bool MidKeepHighDuraUnbuffed = true; // if true, observe rather than use actions during unfavourable conditions to conserve durability when no buffs are active
    public bool MidKeepHighDuraVeneration = false; // if true, observe rather than use actions during unfavourable conditions to conserve durability when veneration is active
    public bool MidAllowVenerationGoodOmen = true; // if true, we allow using veneration during iq phase if we lack a lot of progress on good omen
    public bool MidAllowVenerationAfterIQ = true; // if true, we allow using veneration after iq is fully stacked if we still lack a lot of progress
    public bool MidAllowIntensiveUnbuffed = false; // if true, we allow spending good condition on intensive if we still need progress when no buffs are active
    public bool MidAllowIntensiveVeneration = false; // if true, we allow spending good condition on intensive if we still need progress when veneration is active
    public bool MidAllowPrecise = true; // if true, we allow spending good condition on precise touch if we still need iq
    public bool MidAllowSturdyPreсise = false; // if true,we consider sturdy+h&s+precise touch a good move for building iq
    public bool MidAllowCenteredHasty = true; // if true, we consider centered hasty touch a good move for building iq (85% reliability)
    public bool MidAllowSturdyHasty = true; // if true, we consider sturdy hasty touch a good move for building iq (50% reliability), otherwise we use combo
    public bool MidAllowGoodPrep = true; // if true, we consider prep touch a good move for finisher under good+inno+gs
    public bool MidAllowSturdyPrep = true; // if true, we consider prep touch a good move for finisher under sturdy+inno
    public bool MidGSBeforeInno = true; // if true, we start quality combos with gs+inno rather than just inno
    public bool MidFinishProgressBeforeQuality = false; // if true, at 10 iq we first finish progress before starting on quality
    public bool MidObserveGoodOmenForTricks = false; // if true, we'll observe on good omen where otherwise we'd use tricks on good
    public bool FinisherBaitGoodByregot = true; // if true, use careful observations to try baiting good byregot
    public bool EmergencyCPBaitGood = false; // if true, we allow spending careful observations to try baiting good for tricks when we really lack cp
    public bool UseMaterialMiracle = false;

    public ExpertSolverSettings()
    {
        // 移除构造函数中的图标加载
    }

    public bool Draw()
    {
        ImGui.TextWrapped("The expert recipe solver is not an alternative to the standard solver. This is used exclusively with expert recipes.".Loc());
        ImGui.TextWrapped("This solver only applies to recipes marked as expert recipes in the crafting log.".Loc());
        bool changed = false;
        ImGui.Indent();
        if (ImGui.CollapsingHeader("Opener Settings".Loc()))
        {
            changed |= ImGui.Checkbox("Use ?? instead of ?? for the opener".Loc(Skills.Reflect.NameOfAction(), Skills.MuscleMemory.NameOfAction()), ref UseReflectOpener);
            changed |= ImGui.Checkbox("Allow spending ?? on ?? (400%) rather than ?? (500%) if ?? ??".Loc(Skills.MuscleMemory.NameOfAction(), Skills.IntensiveSynthesis.NameOfAction(), Skills.RapidSynthesis.NameOfAction(), Condition.Good.ToLocalizedString(), ConditionString), ref MuMeIntensiveGood);
            changed |= ImGui.Checkbox("If ?? ?? during ??, use ?? + ??".Loc(Condition.Malleable.ToLocalizedString(), ConditionString, Skills.MuscleMemory.NameOfAction(), Skills.HeartAndSoul.NameOfAction(), Skills.IntensiveSynthesis.NameOfAction()), ref MuMeIntensiveMalleable);
            changed |= ImGui.Checkbox("If at last step of ?? and not ?? ??, use ?? (forcing via ?? if necessary)".Loc(Skills.MuscleMemory.NameOfAction(), Condition.Centered.ToLocalizedString(), ConditionString, Skills.IntensiveSynthesis.NameOfAction(), Skills.HeartAndSoul.NameOfAction()), ref MuMeIntensiveLastResort);
            changed |= ImGui.Checkbox("Use ?? on ?? ??, if ?? is already active".Loc(Skills.Manipulation.NameOfAction(), Condition.Primed.ToLocalizedString(), ConditionString, Skills.Veneration.NameOfAction()), ref MuMePrimedManip);
            changed |= ImGui.Checkbox("?? during unfavourable ?? instead of spending ?? on ??".Loc(Skills.Observe.NameOfAction(), ConditionString, DurabilityString, Skills.RapidSynthesis.NameOfAction()), ref MuMeAllowObserve);
            ImGui.Text("Allow ?? only if more than this amount of steps remain on ??".Loc(Skills.Manipulation.NameOfAction(), Skills.MuscleMemory.NameOfAction()));
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt("###MumeMinStepsForManip", ref MuMeMinStepsForManip, 0, 5);
            ImGui.Text("Allow ?? only if more than this amount of steps remain on ??".Loc(Skills.Veneration.NameOfAction(), Skills.MuscleMemory.NameOfAction()));
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt("###MuMeMinStepsForVene", ref MuMeMinStepsForVene, 0, 5);
        }
        if (ImGui.CollapsingHeader("Main Rotation Settings".Loc()))
        {
            ImGui.Text("Minimum ?? stacks to spend ?? on ?? (10 to disable)".Loc(Buffs.InnerQuiet.NameOfBuff(), Skills.HeartAndSoul.NameOfAction(), Skills.PreciseTouch.NameOfAction()));
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt($"###MidMinIQForHSPrecise", ref MidMinIQForHSPrecise, 0, 10);
            changed |= ImGui.Checkbox("On low ??, prefer ?? over non-?? ?? before ?? has 10 stacks".Loc(DurabilityString, Skills.Observe.NameOfAction(), Condition.Pliant.ToLocalizedString(), Skills.Manipulation.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()), ref MidBaitPliantWithObservePreQuality);
            changed |= ImGui.Checkbox("On low ??, prefer ?? over non-?? ?? / ??+?? after ?? has 10 stacks".Loc(DurabilityString, Skills.Observe.NameOfAction(), Condition.Pliant.ToLocalizedString(), Skills.Manipulation.NameOfAction(), Skills.Innovation.NameOfAction(), Skills.TrainedFinesse.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()), ref MidBaitPliantWithObserveAfterIQ);
            changed |= ImGui.Checkbox("Use ?? on ?? ?? before ?? has 10 stacks".Loc(Skills.Manipulation.NameOfAction(), Condition.Primed.ToLocalizedString(), ConditionString, Buffs.InnerQuiet.NameOfBuff()), ref MidPrimedManipPreQuality);
            changed |= ImGui.Checkbox("Use ?? on ?? ?? after ?? has 10 stacks, if enough CP is available to utilize ?? well".Loc(Skills.Manipulation.NameOfAction(), Condition.Primed.ToLocalizedString(), ConditionString, Buffs.InnerQuiet.NameOfBuff(), DurabilityString), ref MidPrimedManipAfterIQ);
            changed |= ImGui.Checkbox("Allow ?? during unfavourable ?? without buffs".Loc(Skills.Observe.NameOfAction(), ConditionString), ref MidKeepHighDuraUnbuffed);
            changed |= ImGui.Checkbox("Allow ?? during unfavourable ?? under ??".Loc(Skills.Observe.NameOfAction(), ConditionString, Buffs.Veneration.NameOfBuff()), ref MidKeepHighDuraVeneration);
            changed |= ImGui.Checkbox("Allow ?? if we still have large ?? deficit (more than ?? can complete) on ??".Loc(Skills.Veneration.NameOfAction(), ProgressString, Skills.IntensiveSynthesis.NameOfAction(), Condition.GoodOmen.ToLocalizedString()), ref MidAllowVenerationGoodOmen);
            changed |= ImGui.Checkbox("Allow ?? if we still have large ?? deficit (more than ?? can complete) after ?? has 10 stacks".Loc(Skills.Veneration.NameOfAction(), ProgressString, Skills.RapidSynthesis.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()), ref MidAllowVenerationAfterIQ);
            changed |= ImGui.Checkbox("Spend ?? ?? on ?? if we need more ?? without buffs".Loc(Condition.Good.ToLocalizedString(), ConditionString, Skills.IntensiveSynthesis.NameOfAction(), ProgressString), ref MidAllowIntensiveUnbuffed);
            changed |= ImGui.Checkbox("Spend ?? ?? on ?? if we need more ?? under ??".Loc(Condition.Good.ToLocalizedString(), ConditionString, Skills.IntensiveSynthesis.NameOfAction(), ProgressString, Skills.Veneration.NameOfAction()), ref MidAllowIntensiveVeneration);
            changed |= ImGui.Checkbox("Spend ?? ?? on ?? if we need more ?? stacks".Loc(Condition.Good.ToLocalizedString(), ConditionString, Skills.PreciseTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()), ref MidAllowPrecise);
            changed |= ImGui.Checkbox("Consider ?? ?? ?? + ?? a good move for building ?? stacks".Loc(Condition.Sturdy.ToLocalizedString(), ConditionString, Skills.HeartAndSoul.NameOfAction(), Skills.PreciseTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()), ref MidAllowSturdyPreсise);
            changed |= ImGui.Checkbox("Consider ?? ?? ?? a good move for building ?? stacks (85% success, 10 ??)".Loc(Condition.Centered.ToLocalizedString(), ConditionString, Skills.HastyTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff(), DurabilityString), ref MidAllowCenteredHasty);
            changed |= ImGui.Checkbox("Consider ?? ?? ?? a good move for building ?? stacks (50% success, 5 ??)".Loc(Condition.Sturdy.ToLocalizedString(), ConditionString, Skills.HastyTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff(), DurabilityString), ref MidAllowSturdyHasty);
            changed |= ImGui.Checkbox("Consider ?? a good move under ?? ?? + ?? + ??, assuming we have enough ??".Loc(Skills.PreparatoryTouch.NameOfAction(), Condition.Good.ToLocalizedString(), ConditionString, Buffs.Innovation.NameOfBuff(), Buffs.GreatStrides.NameOfBuff(), DurabilityString), ref MidAllowGoodPrep);
            changed |= ImGui.Checkbox("Consider ?? a good move under ?? ?? + ??, assuming we have enough ??".Loc(Skills.PreparatoryTouch.NameOfAction(), Condition.Sturdy.ToLocalizedString(), ConditionString, Buffs.Innovation.NameOfBuff(), DurabilityString), ref MidAllowSturdyPrep);
            changed |= ImGui.Checkbox("Use ?? before ?? + ?? combos".Loc(Skills.GreatStrides.NameOfAction(), Skills.Innovation.NameOfAction(), QualityString), ref MidGSBeforeInno);
            changed |= ImGui.Checkbox("Finish ?? before starting ?? phase".Loc(ProgressString, QualityString), ref MidFinishProgressBeforeQuality);
            changed |= ImGui.Checkbox("?? on ?? ?? if we would otherwise use ?? on ?? ??".Loc(Skills.Observe.NameOfAction(), Condition.GoodOmen.ToLocalizedString(), ConditionString, Skills.TricksOfTrade.NameOfAction(), Condition.Good.ToLocalizedString(), ConditionString), ref MidObserveGoodOmenForTricks);
        }
        ImGui.Unindent();
        changed |= ImGui.Checkbox("Max out Ishgard Restoration recipes instead of just hitting max breakpoint".Loc(), ref MaxIshgardRecipes);
        ImGuiComponents.HelpMarker("This will try to maximise quality to earn more Skyward points.".Loc());
        changed |= ImGui.Checkbox("Finisher: use ?? to try baiting ?? ?? for ??".Loc(Skills.CarefulObservation.NameOfAction(), Condition.Good.ToLocalizedString(), ConditionString, Skills.ByregotsBlessing.NameOfAction()), ref FinisherBaitGoodByregot);
        changed |= ImGui.Checkbox("Emergency: use ?? to try baiting ?? ?? for ?? if really low on CP".Loc(Skills.CarefulObservation.NameOfAction(), Condition.Good.ToLocalizedString(), ConditionString, Skills.TricksOfTrade.NameOfAction()), ref EmergencyCPBaitGood);
        changed |= ImGui.Checkbox("Use Material Miracle in Cosmic Exploration".Loc(), ref UseMaterialMiracle);
        if (ImGuiEx.ButtonCtrl("Reset Expert Solver Settings To Default".Loc()))
        {
            P.Config.ExpertSolverConfig = new();
            changed |= true;
        }
        return changed;
    }
}
