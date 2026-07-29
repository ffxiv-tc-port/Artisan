using Artisan.Autocraft;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop.CSExt;
using Artisan.UI;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using static ECommons.GenericHelpers;

namespace Artisan.GameInterop;

public static unsafe class Operations
{
    public unsafe static void RepeatTrialCraft()
    {
        try
        {
            if (Throttler.Throttle(500))
            {
                if (GenericHelpers.TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var recipenote))
                {
                    Callback.Fire(&recipenote->AtkUnitBase, true, 10);
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "RepeatTrialCraft");
        }
    }

    public unsafe static void QuickSynthItem(int crafts)
    {
        if (Crafting.CurState is not Crafting.State.IdleBetween and not Crafting.State.IdleNormal)
            return;

        try
        {
            var recipeWindow = Svc.GameGui.GetAddonByName("RecipeNote", 1);
            if (recipeWindow == nint.Zero)
                return;

            GenericHelpers.TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon);

            if (addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory == null || !int.TryParse(addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory->NodeText.ToString(), out int trueNumberCraftable) || trueNumberCraftable == 0)
            {
                return;
            }

            var addonPtr = (AddonRecipeNote*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            Svc.Log.Debug($"Starting quick craft");
            Callback.Fire(&addon->AtkUnitBase, true, 9);

            var quickSynthWindow = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SynthesisSimpleDialog", 1).Address;

            if (quickSynthWindow != null)
            {
                var values = stackalloc AtkValue[2];
                values[0] = new()
                {
                    Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                    Int = Math.Min(trueNumberCraftable, Math.Min(crafts, 99)),
                };
                values[1] = new()
                {
                    Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool,
                    Byte = 1,
                };
                Callback.Fire(quickSynthWindow, true, values[0], values[1]);
            }

        }
        catch (Exception ex)
        {
            ex.Log();
        }
    }

    public unsafe static void CloseQuickSynthWindow()
    {
        try
        {
            if (Crafting.CanCancelQS)
            {
                var quickSynthPTR = Svc.GameGui.GetAddonByName("SynthesisSimple", 1);
                if (quickSynthPTR == nint.Zero)
                    return;

                var quickSynthWindow = (AtkUnitBase*)quickSynthPTR.Address;
                if (quickSynthWindow == null)
                    return;

                Callback.Fire(quickSynthWindow, true, -1);
                Crafting.CanCancelQS = false;
            }
        }
        catch (Exception e)
        {
            e.Log();
        }
    }

    // Diagnostic for the TC "crafting list keeps reopening the recipe window" report
    // (2026-07-29). Every early return below means "not ready, retry"; when one of
    // them is permanently true, task ListCraft burns its 10s timeout, the list loop
    // restarts, and the window is opened again - which is what the user sees.
    //
    // Logged at INFORMATION, not Debug: the reporting user runs at LogLevel 2, which
    // is exactly why the previous round produced no usable log. Throttled to once a
    // second and only emitted while something is actually blocking.
    private static DateTime _lastBlockLog = DateTime.MinValue;

    private static bool BlockedBy(string reason)
    {
        if ((DateTime.Now - _lastBlockLog).TotalMilliseconds >= 1000)
        {
            _lastBlockLog = DateTime.Now;
            Svc.Log.Information($"Artisan: RepeatActualCraft blocked by [{reason}]");
        }
        return false;
    }

    public unsafe static bool RepeatActualCraft()
    {
        if (Crafting.CurState is not Crafting.State.IdleBetween and not Crafting.State.IdleNormal)
            return BlockedBy($"CurState={Crafting.CurState}");

        if (PreCrafting.Occupied())
            return BlockedBy("PreCrafting.Occupied()");

        var recipe = GetSelectedRecipeEntry();
        if (recipe == null)
            return BlockedBy("GetSelectedRecipeEntry()==null");

        var ingIndex = 0;
        foreach (var ing in recipe->IngredientsSpan)
        {
            if (ing.NumAssignedNQ + ing.NumAssignedHQ != ing.NumTotal)
                return BlockedBy($"ingredient[{ingIndex}] item={ing.ItemId} "
                                 + $"assignedNQ={ing.NumAssignedNQ} assignedHQ={ing.NumAssignedHQ} "
                                 + $"total={ing.NumTotal}");
            ingIndex++;
        }

        if (RaphaelCache.InProgressAny())
            return BlockedBy("RaphaelCache.InProgressAny()");

        if (TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmicAddon))
        {
            if (cosmicAddon == null)
                return BlockedBy("WKSRecipeNotebook addon null");

            Svc.Log.Debug($"Starting actual cosmic craft");
            Callback.Fire(cosmicAddon, true, 6);
            PreCrafting.Tasks.Clear();
            return true;

        }
        else
        {
            var addon = (AddonRecipeNote*)Svc.GameGui.GetAddonByName("RecipeNote").Address;
            if (addon == null)
                return BlockedBy("RecipeNote addon null");

            Svc.Log.Information($"Artisan: starting actual craft");
            Callback.Fire(&addon->AtkUnitBase, true, 8);
            PreCrafting.Tasks.Clear();
            return true;
        }
    }

    // get recipe currently selected in recipenote, with all the necessary safety checks
    // returns null if data is not fully ready
    public unsafe static RecipeNoteRecipeEntry* GetSelectedRecipeEntry()
    {
        var rd = RecipeNoteRecipeData.Ptr();
        return rd != null && rd->Recipes != null && rd->SelectedIndex < rd->RecipesCount ? rd->Recipes + rd->SelectedIndex : null;
    }
}
