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

        if (RaphaelCache.InProgressAny())
            return BlockedBy("RaphaelCache.InProgressAny()");

        // 🔴 宇宙製作(WKSRecipeNotebook)絕對不能套用底下那段「素材指派完成了嗎」的閘門。
        //
        // 那段讀的是 GetSelectedRecipeEntry() → RecipeNote.Instance()->RecipeList 加上
        // SelectedIndex，也就是**一般製作手帳**的資料。只有 OpenRecipeByRecipeId 會填它；
        // 宇宙筆記的選取是 Callback.Fire 做的，完全不會更新那份資料
        // （同一件事 PreCrafting.TaskSelectRecipe 與 DumpCosmicStateOnce 的註解已經各記過一次，
        //  那時就是實機驗證出來的）。把它拿來當宇宙製作的閘門，等於拿另一個配方的陳舊狀態
        // 去決定這個配方能不能開始。
        //
        // 2026-08-03 實機證據（dalamud.log 21:24:03～21:25:55）：
        //   宇宙筆記明明選著 recipe 36521，Artisan 自己也連續 56 次記了「配方 36521 已選中」，
        //   這個閘門卻始終回報 ingredient[0] item=48233 assignedNQ=0 assignedHQ=0 total=1。
        //   查台服 7.20 的 Recipe 表就知道那是**另一個配方**：
        //     36520 → ItemResult 48267(宇宙探索用的布料)、Ingredient[0] 48233(宇宙貨箱) x1
        //     36521 → ItemResult 48521(宇宙探索用的狩獵帽)、Ingredient[0] 48267 x1
        //   36521 的素材是 48267，不是 48233。閘門讀到的是 36520 的陳舊項目，而那時宇宙貨箱
        //   剛好用完，NumAssigned 永遠是 0 → 閘門恆假 → Artisan 永遠開不了工 →
        //   ICE 看到「Artisan 不忙了」就重下指令 → 每 1.35 秒一輪、近 30 次不收斂。
        //
        // ⚠️ 這不是「素材指派壞掉」：同一份 log 裡 36520 成功製作 7 次、36521 成功 5 次，
        //    SetIngredients 點的 NQ/HQ 按鈕是對的（見 CraftingList.SetIngredients 的 ULD 註解）。
        //    素材真的沒指派好時，遊戲端會回 LogMessage 1144/1145/1146，
        //    由 Endurance.CheckCraftBlockingError 當成終局條件處理 —— 那才是正確的判據來源。
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
            // 一般製作手帳：這份資料就是它自己的，閘門在這裡是有效的。
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
