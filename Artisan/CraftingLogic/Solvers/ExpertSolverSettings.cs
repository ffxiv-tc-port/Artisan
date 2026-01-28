using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using System;
using System.Numerics;
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
    public enum MidKeepHighDuraSetting  // what to do in pre-quality when dura is starting to run low
    {
        MidKeepHighDuraUnbuffed,        // fish for procs with observe to conserve dura, as long as veneration isn't up
        MidKeepHighDuraVeneration,      // fish for procs with observe to conserve dura, no matter what
        MidUseDura                      // don't fish for procs, keep using durability
    }
    public string GetMidKeepHighDuraSettingName(MidKeepHighDuraSetting value)
        => value switch
        {
            MidKeepHighDuraSetting.MidKeepHighDuraUnbuffed => $"Use {Skills.Observe.NameOfAction()} for a better {ConditionString.ToLower()}, as long as {Buffs.Veneration.NameOfBuff()} isn't on",
            MidKeepHighDuraSetting.MidKeepHighDuraVeneration => $"Use {Skills.Observe.NameOfAction()} for a better {ConditionString.ToLower()}, even during {Buffs.Veneration.NameOfBuff()}",
            MidKeepHighDuraSetting.MidUseDura or _ => $"Don't use {Skills.Observe.NameOfAction()}, just keep going",
        };
    public MidKeepHighDuraSetting MidKeepHighDura = MidKeepHighDuraSetting.MidKeepHighDuraUnbuffed;
    public enum MidAllowIntensiveSetting  // how to handle good procs before finishable progress
    {
        MidAllowIntensiveUnbuffed,        // use intensive synthesis no matter what
        MidAllowIntensiveVeneration,      // use intensive synthesis as long as veneration is up
        MidNoIntensive                    // don't use intensive synthesis (good will be used for tricks or precise)
    }
    public string GetMidAllowIntensiveSettingName(MidAllowIntensiveSetting value)
        => value switch
        {
            MidAllowIntensiveSetting.MidNoIntensive => $"Don't use {Skills.IntensiveSynthesis.NameOfAction()}",
            MidAllowIntensiveSetting.MidAllowIntensiveVeneration => $"Use {Skills.IntensiveSynthesis.NameOfAction()} as long as {Buffs.Veneration.NameOfBuff()} is on",
            MidAllowIntensiveSetting.MidAllowIntensiveUnbuffed or _ => $"Use {Skills.IntensiveSynthesis.NameOfAction()} regardless of buffs"
        };
    public MidAllowIntensiveSetting MidAllowIntensive = MidAllowIntensiveSetting.MidNoIntensive;
    public bool MidAllowVenerationGoodOmen = true; // if true, we allow using veneration during iq phase if we lack a lot of progress on good omen
    public bool MidAllowVenerationAfterIQ = true; // if true, we allow using veneration after iq is fully stacked if we still lack a lot of progress
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
	public bool RapidSynthYoloAllowed = true; // if false, expert crafting may lock up midway, so not good for AFK crafting. This yolo however is likely to fail the craft, so disabling gives opportunity for intervention
    public bool UseMaterialMiracle = false;
	public int MinimumStepsBeforeMiracle = 10;

    [NonSerialized]
    public IDalamudTextureWrap? expertIcon;

    public ExpertSolverSettings()
    {
        var tex = Svc.PluginInterface.UiBuilder.LoadUld("ui/uld/RecipeNoteBook.uld");
        expertIcon = tex?.LoadTexturePart("ui/uld/RecipeNoteBook_hr1.tex", 14);
    }

    public bool Draw()
    {
            bool changed = false;
        try
        {
            ImGui.TextWrapped($"专家配方求解器并不是标准求解器的替代品。它仅用于专家配方。");
            if (expertIcon != null)
            {
                ImGui.TextWrapped($"专家配方求解器仅适用于专家配方。");
                ImGui.SameLine();
                ImGui.Image(expertIcon.Handle, expertIcon.Size, new Vector2(0, 0), new Vector2(1, 1), new Vector4(0.94f, 0.57f, 0f, 1f));
                ImGui.SameLine();
                ImGui.TextWrapped($"在制作日志中显示图标。");
            }

            ImGui.Indent();
ImGui.Dummy(new Vector2(0, 5f));
            if (ImGui.CollapsingHeader("起手设置"))
            {
                changed |= ImGui.Checkbox($"使用 [{Skills.Reflect.NameOfAction()}] 代替 [{Skills.MuscleMemory.NameOfAction()}]", ref UseReflectOpener);
                if (!UseReflectOpener)
                {
                    ImGui.Dummy(new Vector2(0, 5f));
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, $"这些设置仅在制作开始且 [{Skills.MuscleMemory.NameOfAction()}] 激活时生效。");
                    ImGui.Dummy(new Vector2(0, 5f));
                    
                    changed |= ImGui.Checkbox($"当处于 [{Condition.Good.ToLocalizedString()}] 时，优先使用 [{Skills.IntensiveSynthesis.NameOfAction()}] (400%) 而不是 [{Skills.RapidSynthesis.NameOfAction()}] (500%)", ref MuMeIntensiveGood);
                    changed |= ImGui.Checkbox($"当处于 [{Condition.Malleable.ToLocalizedString()}] 时，使用 [{Skills.HeartAndSoul.NameOfAction()}] + [{Skills.IntensiveSynthesis.NameOfAction()}] (如果可用)", ref MuMeIntensiveMalleable);
                    changed |= ImGui.Checkbox($"当处于 [{Condition.Primed.ToLocalizedString()}] 且 [{Skills.Veneration.NameOfAction()}] 已激活时，使用 [{Skills.Manipulation.NameOfAction()}]", ref MuMePrimedManip);
                    ImGuiComponents.HelpMarker($"如果禁用此项，在 [{Skills.MuscleMemory.NameOfAction()}] 期间，[{Skills.Manipulation.NameOfAction()}] 仅会在 [{Condition.Pliant.ToLocalizedString()}] 时使用。");
                    
                    changed |= ImGui.Checkbox($"当处于 [{Condition.Normal.ToLocalizedString()}] 或其他无关状态时，使用 [{Skills.Observe.NameOfAction()}] 而不是 [{Skills.RapidSynthesis.NameOfAction()}]", ref MuMeAllowObserve);
                    ImGuiComponents.HelpMarker($"这会以消耗 [{Skills.MuscleMemory.NameOfAction()}] 步数为代价来节省 {DurabilityString}。");
                    
                    changed |= ImGui.Checkbox($"当 [{Skills.MuscleMemory.NameOfAction()}] 仅剩 1 步且不是 [{Condition.Centered.ToLocalizedString()}] 时，使用 [{Skills.IntensiveSynthesis.NameOfAction()}] (必要时强制使用 [{Skills.HeartAndSoul.NameOfAction()}])", ref MuMeIntensiveLastResort);
                    ImGuiComponents.HelpMarker($"如果最后一步是 [{Condition.Centered.ToLocalizedString()}]，仍会使用 [{Skills.RapidSynthesis.NameOfAction()}]。");
                    
                    ImGui.Text($"仅当 [{Skills.MuscleMemory.NameOfAction()}] 剩余步数不小于此数值时，才允许使用以下技能：");
                    ImGuiComponents.HelpMarker($"求解器仍只会在合适的状态（{ConditionString}）下使用这些技能。");
                    
                    ImGui.PushItemWidth(250);
                    changed |= ImGui.SliderInt($"{Skills.Manipulation.NameOfAction()}###MumeMinStepsForManip", ref MuMeMinStepsForManip, 1, 5);
                    ImGui.PushItemWidth(250);
                    changed |= ImGui.SliderInt($"{Skills.Veneration.NameOfAction()}###MuMeMinStepsForVene", ref MuMeMinStepsForVene, 1, 5);
                    ImGui.Dummy(new Vector2(0, 5f));
                }
            }

            if (ImGui.CollapsingHeader($"主循环 - 品质准备阶段"))
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, $"这些设置适用于起手之后，但 [{Buffs.InnerQuiet.NameOfBuff()}] 叠满之前的阶段。");

                // Pre-quality dura/CP settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"常规设置");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"{DurabilityString} 较低时，使用 [{Skills.Observe.NameOfAction()}] 以尝试触发有利于 [{Skills.Manipulation.NameOfAction()}] 的状态", ref MidBaitPliantWithObservePreQuality);
                ImGuiComponents.HelpMarker($"尝试触发 [{Condition.Pliant.ToLocalizedString()}] (如果启用了相关选项，也包括 [{Condition.Primed.ToLocalizedString()}] )。如果禁用，将不计状态立即使用 [{Skills.Manipulation.NameOfAction()}]。");
                changed |= ImGui.Checkbox($"在 [{Condition.Primed.ToLocalizedString()}] 状态下使用 [{Skills.Manipulation.NameOfAction()}]", ref MidPrimedManipPreQuality);
                ImGuiComponents.HelpMarker($"如果禁用，在此阶段 [{Condition.Primed.ToLocalizedString()}] 将被视为 [{Condition.Normal.ToLocalizedString()}]。");
                ImGui.Unindent();

                // Pre-quality progress settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"{ProgressString} 设置");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"优先完成 {ProgressString}，而非积累 [{Buffs.InnerQuiet.NameOfBuff()}] 或 {QualityString}", ref MidFinishProgressBeforeQuality);
                ImGuiComponents.HelpMarker($"该设置将尽快使用 [{Buffs.Veneration.NameOfBuff()}] 和 [{Skills.RapidSynthesis.NameOfAction()}] 来推满进展，而不考虑层数或状态（灵活性较低，但尽量保证完成制作）。如果禁用，求解器在叠满层数前不会强制推进展。");
                
                ImGui.TextWrapped($"当 {DurabilityString} 开始不足且需要使用 [{Skills.RapidSynthesis.NameOfAction()}] 时：");
                ImGui.PushItemWidth(400);
                if (ImGui.BeginCombo("##midKeepHighDuraSetting", GetMidKeepHighDuraSettingName(MidKeepHighDura)))
                {
                    foreach (MidKeepHighDuraSetting x in Enum.GetValues<MidKeepHighDuraSetting>())
                    {
                        if (ImGui.Selectable(GetMidKeepHighDuraSettingName(x)))
                        {
                            MidKeepHighDura = x;
                            changed = true;
                        }
                    }
                    ImGui.EndCombo();
                }
                
                ImGui.TextWrapped($"当处于 [{Condition.Good.ToLocalizedString()}] 且仍在推进展时：");
                ImGuiComponents.HelpMarker($"如果禁用，[{Condition.Good.ToLocalizedString()}] 将用于 [{Skills.PreciseTouch.NameOfAction()}] 或 [{Skills.TricksOfTrade.NameOfAction()}]。");
                if (ImGui.BeginCombo("##midAllowIntensiveSetting", GetMidAllowIntensiveSettingName(MidAllowIntensive)))
                {
                    foreach (MidAllowIntensiveSetting x in Enum.GetValues<MidAllowIntensiveSetting>())
                    {
                        if (ImGui.Selectable(GetMidAllowIntensiveSettingName(x)))
                        {
                            MidAllowIntensive = x;
                            changed = true;
                        }
                    }
                    ImGui.EndCombo();
                }
                changed |= ImGui.Checkbox($"当 {ProgressString} 缺口较大时，在 [{Condition.GoodOmen.ToLocalizedString()}] 状态下使用 [{Skills.Veneration.NameOfAction()}]", ref MidAllowVenerationGoodOmen);
                ImGuiComponents.HelpMarker($"特指接下来的 [{Condition.Good.ToLocalizedString()}] 步如果不配合 [{Skills.Veneration.NameOfAction()}]，其 [{Skills.IntensiveSynthesis.NameOfAction()}] 无法推满进展的情况。");
                ImGui.Unindent();

                // Pre-quality Inner Quiet settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"{Buffs.InnerQuiet.NameOfBuff()} 积累");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"当处于 [{Condition.Good.ToLocalizedString()}] 时，使用 [{Skills.PreciseTouch.NameOfAction()}]", ref MidAllowPrecise);
                ImGuiComponents.HelpMarker($"如果进展未完成，[{Skills.IntensiveSynthesis.NameOfAction()}] 优先度更高。如果两者都禁用，[{Condition.Good.ToLocalizedString()}] 将用于 [{Skills.TricksOfTrade.NameOfAction()}]。");
                
                ImGui.TextWrapped($"使用 [{Skills.HeartAndSoul.NameOfAction()}] 强制触发 [{Skills.PreciseTouch.NameOfAction()}]：");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"当处于 [{Condition.Sturdy.ToLocalizedString()}] 时", ref MidAllowSturdyPreсise);
                ImGui.PushItemWidth(250);
                changed |= ImGui.SliderInt($"当达到此层数时 (10为禁用)###MidMinIQForHSPrecise", ref MidMinIQForHSPrecise, 0, 10);
                ImGui.Unindent();
                
                ImGui.TextWrapped($"使用 [{Skills.HastyTouch.NameOfAction()}] 或 [{Skills.DaringTouch.NameOfAction()}]：");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"当处于 [{Condition.Centered.ToLocalizedString()}] 时 (85%成功率, 10 {DurabilityString})", ref MidAllowCenteredHasty);
                changed |= ImGui.Checkbox($"当处于 [{Condition.Sturdy.ToLocalizedString()}] 时 (60%成功率, 5 {DurabilityString})", ref MidAllowSturdyHasty);
                ImGui.Unindent();
                ImGui.Unindent();
                ImGui.Dummy(new Vector2(0, 5f));
            }

            if (ImGui.CollapsingHeader($"主循环 - {QualityString} 阶段"))
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, $"这些设置适用于 [{Buffs.InnerQuiet.NameOfBuff()}] 叠满之后的阶段。");

                // Mid-quality dura/CP settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"常规设置");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"{DurabilityString} 极低时，使用 [{Skills.Observe.NameOfAction()}] 以尝试触发有利于回复 {DurabilityString} 的状态", ref MidBaitPliantWithObserveAfterIQ);
                ImGuiComponents.HelpMarker($"尝试触发 [{Condition.Pliant.ToLocalizedString()}]。如果禁用，将不计状态立即使用回复技能或 0 耐久消耗技能。");
                changed |= ImGui.Checkbox($"当有足够 CP 且处于 [{Condition.Primed.ToLocalizedString()}] 时，使用 [{Skills.Manipulation.NameOfAction()}]", ref MidPrimedManipAfterIQ);
                changed |= ImGui.Checkbox($"处于 [{Condition.GoodOmen.ToLocalizedString()}] 且无增益时，优先使用 [{Skills.Observe.NameOfAction()}] → [{Skills.TricksOfTrade.NameOfAction()}]", ref MidObserveGoodOmenForTricks);
                ImGuiComponents.HelpMarker($"如果禁用，求解器将优先开启增益技能，并将后续的 [{Condition.Good.ToLocalizedString()}] 用于进展或品质。开启此项通常效率更高。");
                ImGui.Unindent();

                // Mid-quality progress settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"{ProgressString} 设置");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"当 {ProgressString} 缺口较大时使用 [{Skills.Veneration.NameOfAction()}]", ref MidAllowVenerationAfterIQ);
                ImGuiComponents.HelpMarker($"即使在制作后期，如果单次 [{Skills.IntensiveSynthesis.NameOfAction()}] 无法完成制作则会使用。若开启了“优先推进展”则会被覆盖。");
                ImGui.Unindent();

                // Mid-quality action settings
                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"{QualityString} 设置");
                ImGui.Indent();
                ImGui.TextWrapped($"使用 [{Skills.PreparatoryTouch.NameOfAction()}]：");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"在 [{Condition.Good.ToLocalizedString()}] + [{Buffs.Innovation.NameOfBuff()}] + [{Buffs.GreatStrides.NameOfBuff()}] 状态下", ref MidAllowGoodPrep);
                changed |= ImGui.Checkbox($"在 [{Condition.Sturdy.ToLocalizedString()}] + [{Buffs.Innovation.NameOfBuff()}] 状态下", ref MidAllowSturdyPrep);
                ImGui.Unindent();
                changed |= ImGui.Checkbox($"在非终结品质连招前使用 [{Skills.GreatStrides.NameOfAction()}]", ref MidGSBeforeInno);
                ImGuiComponents.HelpMarker($"例如：[{Buffs.Innovation.NameOfBuff()}] → [{Skills.Observe.NameOfAction()}] → [{Skills.AdvancedTouch.NameOfAction()}]。开启此项消耗更多 CP 但节省耐久。");
                ImGui.Unindent();
                ImGui.Dummy(new Vector2(0, 5f));
            }

            if (ImGui.CollapsingHeader($"终结技设置"))
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, $"这些设置适用于品质接近满值或资源即将耗尽的阶段。");

                ImGui.Dummy(new Vector2(0, 5f));
                ImGui.TextWrapped($"使用 [{Skills.CarefulObservation.NameOfAction()}] 尝试触发 [{Condition.Good.ToLocalizedString()}]：");
                ImGui.Indent();
                changed |= ImGui.Checkbox($"为 [{Skills.ByregotsBlessing.NameOfAction()}] 争取高品质 (作为阔步的备选)", ref FinisherBaitGoodByregot);
                ImGuiComponents.HelpMarker($"当“阔步+比尔格”能推满品质，但 CP 不足以开启阔步时触发。");
                changed |= ImGui.Checkbox($"当 CP 极低时争取 [{Skills.TricksOfTrade.NameOfAction()}]", ref EmergencyCPBaitGood);
                ImGuiComponents.HelpMarker($"当别无选择且甚至比尔格也无法达到目标品质时触发。");
                ImGui.Unindent();
                
                changed |= ImGui.Checkbox($"资源耗尽时允许使用 [{Skills.RapidSynthesis.NameOfAction()}] 强行收尾", ref RapidSynthYoloAllowed);
                ImGuiComponents.HelpMarker($"如果禁用，求解器在无路可走时将停手。通常建议开启，因为它仅在无 CP 或耐久时作为最后尝试。");
                ImGui.Dummy(new Vector2(0, 5f));
            }
            ImGui.Unindent();

            // Misc. settings
            ImGui.Dummy(new Vector2(0, 5f));
            changed |= ImGui.Checkbox("充分利用伊修加德重建配方，而不是仅仅达到最大品质断点", ref MaxIshgardRecipes);
            ImGuiComponents.HelpMarker("这将尝试最大化品质以获得更多的技巧点（Skyward points）。");
            changed |= ImGui.Checkbox($"在宇宙探索中使用 [{Skills.MaterialMiracle.NameOfAction()}]", ref UseMaterialMiracle);
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt($"在尝试 [{Skills.MaterialMiracle.NameOfAction()}] 前最少执行步数###MinimumStepsBeforeMiracle", ref MinimumStepsBeforeMiracle, 0, 20);
            if (ImGuiEx.ButtonCtrl("重置专家求解器设置到默认状态"))
            {
                P.Config.ExpertSolverConfig = new();
                changed |= true;
            }
            return changed;
        }
        catch { }
        return changed;
    }
}
