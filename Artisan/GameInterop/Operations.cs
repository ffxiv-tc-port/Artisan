using Artisan.Autocraft;
using Artisan.CraftingLists;
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
                // 試作不會關製作手帳(它只是隱藏再回來),每一輪試作都會再送 ⇒ 多次互動窗的 15 幀逃生口;
                // 要擋的是 TaskExitCraft 對這一扇送過 Fire(-1) 之後、它還在關閉中的那幾幀。守衛放在節流之後。
                if (GenericHelpers.TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var recipenote)
                    && AddonPressGuard.TryBeginPress("RecipeNote", &recipenote->AtkUnitBase, "T|10", AddonPressGuard.RoutineRePressEscapeFrames))
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

            // 開簡易製作對話框不會關製作手帳(15 幀逃生口);被擋就這次不開,呼叫端的 WaitStart 逾時後照既有路徑重排。
            if (!AddonPressGuard.TryBeginPress("RecipeNote", &addon->AtkUnitBase, "T|9", AddonPressGuard.RoutineRePressEscapeFrames))
                return;

            Svc.Log.Debug($"Starting quick craft");
            Callback.Fire(&addon->AtkUnitBase, true, 9);

            var quickSynthWindow = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SynthesisSimpleDialog", 1).Address;

            // 對話框送出數量就關 ⇒ 單答終結窗,同一扇只送一次。
            if (quickSynthWindow != null && AddonPressGuard.TryBeginPress("SynthesisSimpleDialog", quickSynthWindow))
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

    private const long QuickSynthDialogWaitMs = 1000;

    /// <summary>
    /// 簡易製作分兩步：先按製作手帳的「簡易製作」，等數量框就緒再填數量。
    /// 回傳的委派每幀呼叫一次：true＝數量已送出或製作已開始；false＝還沒，下一幀再來；
    /// null＝耐力模式／清單已停止或暫停（與 <see cref="RepeatActualCraft"/> 同契約，中止整條佇列）。
    /// </summary>
    public static Func<bool?> QuickSynthTask(int crafts)
    {
        long pressedAt = 0;
        var waitLogged = false;
        return () => QuickSynthStep(crafts, ref pressedAt, ref waitLogged);
    }

    private unsafe static bool? QuickSynthStep(int crafts, ref long pressedAt, ref bool waitLogged)
    {
        if (Crafting.CurState is Crafting.State.WaitStart or Crafting.State.QuickCraft or Crafting.State.InProgress)
            return true;

        if (!Endurance.Enable && !(CraftingListUI.Processing && !CraftingListFunctions.Paused))
        {
            Svc.Log.Information("Artisan: 耐力模式已關閉、清單已停止或暫停，不開始這一輪簡易製作。");
            return null;
        }

        if (Crafting.CurState is not Crafting.State.IdleBetween and not Crafting.State.IdleNormal)
            return false;

        try
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SynthesisSimpleDialog", out var dialog) && dialog->IsVisible)
            {
                if (!GenericHelpers.IsAddonReady(dialog) || !TryReadCraftable(out var craftable))
                    return false;

                // 單答終結窗：被擋＝這一扇已經送過數量，交給後面的「等開始」。
                if (AddonPressGuard.TryBeginPress("SynthesisSimpleDialog", dialog))
                    FireQuickSynthCount(dialog, Math.Min(craftable, Math.Min(crafts, 99)));
                return true;
            }

            if (pressedAt != 0)
            {
                if (Environment.TickCount64 - pressedAt < QuickSynthDialogWaitMs)
                    return false;
                Svc.Log.Information($"Artisan: 按下簡易製作後 {QuickSynthDialogWaitMs} ms 內沒看到數量框，重按一次。");
            }

            if (!TryPressQuickSynth(out var why))
            {
                if (!waitLogged)
                {
                    waitLogged = true;
                    Svc.Log.Debug($"Artisan: 簡易製作還不能按（{why}），下一幀再試。");
                }
                return false;
            }

            pressedAt = Environment.TickCount64;
            return false;
        }
        catch (Exception ex)
        {
            ex.Log();
            return false;
        }
    }

    private unsafe static bool TryReadCraftable(out int craftable)
    {
        craftable = 0;
        if (!GenericHelpers.TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon))
            return false;
        var node = addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory;
        return node != null && int.TryParse(node->NodeText.ToString(), out craftable) && craftable > 0;
    }

    private unsafe static bool TryPressQuickSynth(out string why)
    {
        if (!GenericHelpers.TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
        {
            why = "RecipeNote not ready";
            return false;
        }
        if (!TryReadCraftable(out _))
        {
            why = "craftable count unreadable or 0";
            return false;
        }
        // 開簡易製作對話框不會關製作手帳(15 幀逃生口)。
        if (!AddonPressGuard.TryBeginPress("RecipeNote", &addon->AtkUnitBase, "T|9", AddonPressGuard.RoutineRePressEscapeFrames))
        {
            why = "AddonPressGuard RecipeNote";
            return false;
        }
        Svc.Log.Debug($"Starting quick craft");
        Callback.Fire(&addon->AtkUnitBase, true, 9);
        why = "";
        return true;
    }

    private unsafe static void FireQuickSynthCount(AtkUnitBase* dialog, int count)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new()
        {
            Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
            Int = count,
        };
        values[1] = new()
        {
            Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool,
            Byte = 1,
        };
        Callback.Fire(dialog, true, values[0], values[1]);
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

                // CanCancelQS 只在 WaitStart→QuickCraft 那一次轉換設真、Fire 完立刻設假,結構上每扇窗只會送一次關閉;
                // 守衛只是把它納入同一把鎖,被擋時 CanCancelQS 維持原值、下一幀再來。
                if (!AddonPressGuard.TryBeginPress("SynthesisSimple", quickSynthWindow, AddonPressGuard.ClosePressKey))
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

    // Diagnostic for "crafting list keeps reopening the recipe window": every early return below
    // means "not ready, retry"; when one of them is permanently true, task ListCraft burns its
    // timeout, the list loop restarts, and the window is opened again.
    // Logged at Information (a diagnostic users are asked to report), throttled to once a second,
    // only while something is actually blocking.
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

    /// <returns>true＝已送出開始製作；false＝這一幀還不能開始；null＝已經停止，中止呼叫端整條佇列。</returns>
    public unsafe static bool? RepeatActualCraft()
    {
        // 排進佇列之後才按停止（或清單被暫停）時，不能照樣開始下一場；也不能走到下面的 PreCrafting.Tasks.Clear()，
        // 那會把停止時排的 TaskExitCraft 一起清掉。
        if (!Endurance.Enable && !(CraftingListUI.Processing && !CraftingListFunctions.Paused))
        {
            Svc.Log.Information("Artisan: 耐力模式已關閉、清單已停止或暫停，不開始這一場製作。");
            return null;
        }

        if (Crafting.CurState is not Crafting.State.IdleBetween and not Crafting.State.IdleNormal)
            return BlockedBy($"CurState={Crafting.CurState}");

        if (PreCrafting.Occupied())
            return BlockedBy("PreCrafting.Occupied()");

        if (RaphaelCache.InProgressAny())
            return BlockedBy("RaphaelCache.InProgressAny()");

        // 🔴 宇宙製作(WKSRecipeNotebook)絕對不能套用底下那段「素材指派完成了嗎」的閘門:
        // 那段讀的是一般製作手帳的資料(GetSelectedRecipeEntry()),只有 OpenRecipeByRecipeId 會填;
        // 宇宙筆記的選取是 Callback.Fire 做的,不會更新它 ⇒ 等於拿另一個配方的陳舊狀態當閘門。
        // 素材真的沒指派好時遊戲會回 LogMessage 1144/1145/1146,由 Endurance.CheckCraftBlockingError 當終局條件處理。
        if (TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmicAddon))
        {
            if (cosmicAddon == null)
                return BlockedBy("WKSRecipeNotebook addon null");

            // 開始製作不會關筆記(每一件都會再送 ⇒ 15 幀逃生口);要擋的是 TaskExitCraft 對這一扇送過 Fire(-1) 之後、
            // 它還在關閉中的那幾幀。回 false 與其他 BlockedBy 走同一條輪詢路徑。
            if (!AddonPressGuard.TryBeginPress("WKSRecipeNotebook", cosmicAddon, "T|6", AddonPressGuard.RoutineRePressEscapeFrames))
                return BlockedBy("AddonPressGuard WKSRecipeNotebook");

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

            if (!AddonPressGuard.TryBeginPress("RecipeNote", &addon->AtkUnitBase, "T|8", AddonPressGuard.RoutineRePressEscapeFrames))
                return BlockedBy("AddonPressGuard RecipeNote");

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
