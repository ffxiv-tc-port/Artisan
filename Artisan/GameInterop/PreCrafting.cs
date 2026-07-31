using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.GameInterop.CSExt;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;

namespace Artisan.GameInterop;

// manages 'outer loop' of crafting (equipping correct items, using consumables, etc, and finally initiating crafting)
public unsafe static class PreCrafting
{
    public enum CraftType { Normal, Quick, Trial }

    public static int equipAttemptLoops = 0;
    public static int equipGearsetLoops = 0;
    public static int timeWasteLoops = 0;
    private static long NextTaskAt = 0;

    private delegate void ClickSynthesisButton(void* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData);
    private static Hook<ClickSynthesisButton> _clickButton;

    private delegate void* FireCallbackDelegate(AtkUnitBase* atkUnitBase, int valueCount, AtkValue* atkValues, byte updateVisibility);
    private static Hook<FireCallbackDelegate> _gearsetCallback;

    delegate nint AddonWKSRecipeNote_ReceiveEventDelegate(nint a1, ushort a2, uint a3, nint a4, nint a5);
    private static Hook<AddonWKSRecipeNote_ReceiveEventDelegate> _cosmicCallback;

    public enum TaskResult { Done, Retry, Abort }
    public static List<(Func<TaskResult> task, TimeSpan retryDelay)> Tasks = new();
    private static DateTime _nextRetry;

    static PreCrafting()
    {
        _clickButton = Svc.Hook.HookFromSignature<ClickSynthesisButton>("40 55 53 56 57 41 56 48 8D 6C 24 D1 48 81 EC C0 00 00 00", ClickSynthButtons);
        _clickButton?.Enable();

        _gearsetCallback = Svc.Hook.HookFromSignature<FireCallbackDelegate>("E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20", CallbackDetour);

        _cosmicCallback = Svc.Hook.HookFromSignature<AddonWKSRecipeNote_ReceiveEventDelegate>("4C 8B DC 49 89 6B 20 41 56 48 83 EC 60", ClickCosmicButton);
        _cosmicCallback?.Enable();
    }

    private static nint ClickCosmicButton(nint a1, ushort a2, uint a3, nint a4, nint a5)
    {
        try
        {
            if (a2 == 25 && a3 == 0)
            {
                StartCraftingFromSynth(14);
                return 0;
            }
        }
        catch( Exception ex)
        {
            ex.Log();
        }
        return _cosmicCallback.Original(a1, a2, a3, a4, a5);
    }

    private static void* CallbackDetour(AtkUnitBase* atkUnitBase, int valueCount, AtkValue* atkValues, byte updateVisibility)
    {
        var name = atkUnitBase->NameString.TrimEnd();
        if (name.Length >= 11 && name.Substring(0, 11) == "SelectYesno")
        {
            var result = atkValues[0];
            if (result.Int == 1)
            {
                Svc.Log.Debug($"Select no, clearing tasks");
                Endurance.ToggleEndurance(false);
                if (CraftingListUI.Processing)
                {
                    CraftingListFunctions.Paused = true;
                }
                Tasks.Clear();
            }

            _gearsetCallback.Disable();

        }
        return _gearsetCallback.Original(atkUnitBase, valueCount, atkValues, updateVisibility);
    }

    public static void Dispose()
    {
        _clickButton?.Dispose();
        _gearsetCallback?.Dispose();
        _cosmicCallback?.Dispose();
    }

    public static void Update()
    {
        if (DateTime.Now < _nextRetry)
            return;

        while (Tasks.Count > 0)
        {
            switch (Tasks[0].task())
            {
                case TaskResult.Done:
                    Tasks.RemoveAt(0);
                    break;
                case TaskResult.Retry:
                    _nextRetry = DateTime.Now.Add(Tasks[0].retryDelay);
                    return;
                case TaskResult.Abort:
                    Tasks.Clear();
                    return;
            }
        }
    }

    private static void StartCrafting(Recipe recipe, CraftType type)
    {
        try
        {
            Svc.Log.Debug($"Starting {type} crafting: {recipe.RowId} '{recipe.ItemResult.Value.Name.ToDalamudString()}'");

            var requiredClass = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
            var config = P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId) ?? new();

            bool hasIngredients = GetNumberCraftable(recipe) > 0;
            bool needClassChange = requiredClass != CharacterInfo.JobID;
            bool needEquipItem = recipe.ItemRequired.RowId > 0 && (needClassChange || !IsItemEquipped(recipe.ItemRequired.RowId));
            bool needConsumables = NeedsConsumablesCheck(type, config);
            bool hasConsumables = HasConsumablesCheck(config);

            // handle errors when we're forbidden from rectifying them automatically
            if (P.Config.DontEquipItems && needClassChange)
            {
                DuoLog.Error($"Can't craft {recipe.ItemResult.Value.Name.ToDalamudString()}: wrong class, {requiredClass} needed");
                return;
            }
            if (P.Config.DontEquipItems && needEquipItem)
            {
                DuoLog.Error($"Can't craft {recipe.ItemResult.Value.Name.ToDalamudString()}: required item {recipe.ItemRequired.Value.Name} not equipped");
                return;
            }
            if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
            {
                MissingConsumablesMessage(recipe, config);
                return;
            }

            bool needExitCraft = Crafting.CurState == Crafting.State.IdleBetween && (needClassChange || needEquipItem || needConsumables);

            // TODO: pre-setup solver for incoming craft
            Tasks.Clear();
            _nextRetry = default;
            if (needExitCraft)
                Tasks.Add((TaskExitCraft, default));
            if (needClassChange)
            {
                equipGearsetLoops = 0;
                Tasks.Add((() => TaskClassChange(requiredClass), TimeSpan.FromMilliseconds(200))); // TODO: avoid delay and just wait until operation is done
            }

            if (!hasIngredients && type != CraftType.Trial)
            {
                List<string> missingIngredients = MissingIngredients(recipe);

                DuoLog.Error($"Not all ingredients for {recipe.ItemResult.Value.Name.ToDalamudString()} found.\r\nMissing: {string.Join(", ", missingIngredients)}");
                return;
            }

            if (needEquipItem)
            {
                equipAttemptLoops = 0;
                Tasks.Add((() => TaskEquipItem(recipe.ItemRequired.RowId), default));
            }

            bool needFood = config != default && ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config);
            bool needPot = config != default && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config);
            bool needManual = config != default && ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config);
            bool needSquadronManual = config != default && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config);

            if (needFood || needPot || needManual || needSquadronManual)
                Tasks.Add((() => TaskUseConsumables(config, type), default));
            Tasks.Add((() => TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(500)));
            timeWasteLoops = 1;
            Tasks.Add((() => TimeWasteLoop(), TimeSpan.FromMilliseconds(10))); //This is needed for controller players, else if they're near an NPC it will target them and exit the craft as the button is interpreted as target and not confirm.
            Tasks.Add((() => TaskStartCraft(type), default));

            Update();
        }
        catch (Exception ex)
        {
            ex.Log();
        }
    }

    internal static void MissingConsumablesMessage(Recipe recipe, RecipeConfig? config)
    {
        List<string> missingConsumables = MissingConsumables(config);

        DuoLog.Error($"Can't craft {recipe.ItemResult.Value.Name.ToDalamudString()}: required consumables not up and missing {string.Join(", ", missingConsumables)}");
    }

    internal static bool NeedsConsumablesCheck(CraftType type, RecipeConfig? config)
    {
        // TODO: repair & extract materia
        return (type == CraftType.Normal || (type == CraftType.Trial && P.Config.UseConsumablesTrial) || (type == CraftType.Quick && P.Config.UseConsumablesQuickSynth)) && (!ConsumableChecker.IsFooded(config) || !ConsumableChecker.IsPotted(config) || !ConsumableChecker.IsManualled(config) || !ConsumableChecker.IsSquadronManualled(config));
    }

    internal static bool HasConsumablesCheck(RecipeConfig? config)
    {
        return config != default ?
            (ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) || ConsumableChecker.IsFooded(config)) &&
            (ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) || ConsumableChecker.IsPotted(config)) &&
            (ConsumableChecker.HasItem(config.RequiredManual, false) || ConsumableChecker.IsManualled(config)) &&
            (ConsumableChecker.HasItem(config.RequiredSquadronManual, false) || ConsumableChecker.IsSquadronManualled(config)) : true;
    }

    public static List<string> MissingConsumables(RecipeConfig? config)
    {
        List<string> missingConsumables = new List<string>();
        if (!ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config))
            missingConsumables.Add(config.FoodName);

        if (!ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config))
            missingConsumables.Add(config.PotionName);

        if (!ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config))
            missingConsumables.Add(config.ManualName);

        if (!ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config))
            missingConsumables.Add(config.SquadronManualName);
        return missingConsumables;
    }

    public static List<string> MissingIngredients(Recipe recipe)
    {
        List<string> missingIngredients = new();
        foreach (var ing in recipe.Ingredients())
        {
            if (ing.Amount > 0)
            {
                if (CraftingListUI.NumberOfIngredient(ing.Item.RowId) < ing.Amount)
                {
                    missingIngredients.Add(ing.Item.RowId.NameOfItem());
                }
            }
        }

        return missingIngredients;
    }

    public static TaskResult TimeWasteLoop()
    {
        if (timeWasteLoops > 0)
        {
            timeWasteLoops--;
            return TaskResult.Retry;
        }

        return TaskResult.Done;
    }

    public static int GetNumberCraftable(Recipe recipe)
    {
        if (TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) && addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory != null)
        {
            if (int.TryParse(addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory->NodeText.ToString(), out int output))
                return output;
        }
        if (TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmic) && cosmic->UldManager.NodeList[24] != null)
        {
            if (int.TryParse(cosmic->UldManager.NodeList[24]->GetAsAtkTextNode()->NodeText.ToString(), out int output))
                return output;
        }
        return -1;
    }

    public static TaskResult TaskExitCraft()
    {
        switch (Crafting.CurState)
        {
            case Crafting.State.WaitFinish:
            case Crafting.State.QuickCraft:
            case Crafting.State.WaitAction:
            case Crafting.State.InProgress:
                return TaskResult.Retry;
            case Crafting.State.IdleNormal:
                return TaskResult.Done;
            case Crafting.State.IdleBetween:
                var addon = (AddonRecipeNote*)Svc.GameGui.GetAddonByName("RecipeNote").Address;
                if (addon != null && addon->AtkUnitBase.IsVisible)
                {
                    Svc.Log.Debug("Closing recipe menu to exit crafting state");
                    Callback.Fire(&addon->AtkUnitBase, true, -1);
                }
                var addon2 = (AtkUnitBase*)Svc.GameGui.GetAddonByName("WKSRecipeNotebook").Address;
                if (addon2 != null && addon2->IsVisible)
                {
                    Svc.Log.Debug("Closing recipe menu to exit crafting state");
                    Callback.Fire(addon2, true, -1);
                }
                return TaskResult.Retry;
        }

        return TaskResult.Retry;
    }

    public static TaskResult TaskClassChange(Job job)
    {
        if (job == CharacterInfo.JobID)
            return TaskResult.Done;

        if (Svc.Condition[ConditionFlag.PreparingToCraft])
            return TaskResult.Retry;

        if (equipGearsetLoops >= 5)
        {
            DuoLog.Error("Unable to switch gearsets.");
            return TaskResult.Abort;
        }

        var gearsets = RaptureGearsetModule.Instance();
        foreach (ref var gs in gearsets->Entries)
        {
            if (!RaptureGearsetModule.Instance()->IsValidGearset(gs.Id)) continue;
            if ((Job)gs.ClassJob == job)
            {
                if (gs.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                {
                    if (TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var selectyesno))
                    {
                        if (selectyesno->AtkUnitBase.IsVisible)
                            return TaskResult.Retry;
                    }
                    else
                    {
                        equipGearsetLoops++;
                        _gearsetCallback?.Enable();
                        var r = gearsets->EquipGearset(gs.Id);
                        return r < 0 ? TaskResult.Abort : TaskResult.Retry;
                    }
                }

                var result = gearsets->EquipGearset(gs.Id);
                equipGearsetLoops++;
                Svc.Log.Debug($"Tried to equip gearset {gs.Id} for {job}, result={result}, flags={gs.Flags}");
                return result < 0 ? TaskResult.Abort : TaskResult.Retry;
            }
        }

        DuoLog.Error($"Failed to find gearset for {job}");
        return TaskResult.Abort;
    }

    public static TaskResult TaskEquipItem(uint ItemId)
    {
        if (IsItemEquipped(ItemId))
            return TaskResult.Done;

        var pos = FindItemInInventory(ItemId, [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.ArmoryMainHand, InventoryType.ArmoryHands]);
        if (pos == null)
        {
            DuoLog.Error($"Failed to find item {LuminaSheets.ItemSheet[ItemId].Name} (ID: {ItemId}) in inventory");
            Endurance.ToggleEndurance(false);
            if (CraftingListUI.Processing)
                CraftingListFunctions.Paused = true;

            return TaskResult.Abort;
        }

        var agentId = pos.Value.inv is InventoryType.ArmoryMainHand or InventoryType.ArmoryHands ? AgentId.ArmouryBoard : AgentId.Inventory;
        var addonId = AgentModule.Instance()->GetAgentByInternalId(agentId)->GetAddonId();
        var ctx = AgentInventoryContext.Instance();
        ctx->OpenForItemSlot(pos.Value.inv, pos.Value.slot, 0, addonId);

        var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu").Address;
        if (contextMenu != null)
        {
            for (int i = 0; i < contextMenu->AtkValuesCount; i++)
            {
                var firstEntryIsEquip = ctx->EventIds[i] == 25; // i'th entry will fire eventid 7+i; eventid 25 is 'equip'
                if (firstEntryIsEquip)
                {
                    Svc.Log.Debug($"Equipping item #{ItemId} from {pos.Value.inv} @ {pos.Value.slot}, index {i}");
                    Callback.Fire(contextMenu, true, 0, i - 7, 0, 0, 0); // p2=-1 is close, p2=0 is exec first command
                }
            }
            Callback.Fire(contextMenu, true, 0, -1, 0, 0, 0);
            equipAttemptLoops++;

            if (equipAttemptLoops >= 5)
            {
                DuoLog.Error($"Equip option not found after 5 attempts. Aborting.");
                return TaskResult.Abort;
            }
        }
        return TaskResult.Retry;
    }

    public static TaskResult TaskUseConsumables(RecipeConfig? config, CraftType type)
    {
        if (ActionManagerEx.AnimationLock > 0)
            return TaskResult.Retry; // waiting for animation lock to end

        if ((!P.Config.UseConsumablesQuickSynth && type == CraftType.Quick) ||
            (!P.Config.UseConsumablesTrial && type == CraftType.Trial))
            return TaskResult.Done;

        if (Occupied())
            return TaskResult.Retry;

        if (!ConsumableChecker.IsSquadronManualled(config) && InventoryManager.Instance()->GetInventoryItemCount(config.RequiredSquadronManual) != 0)
        {
            if (ActionManagerEx.CanUseAction(ActionType.Item, config.RequiredSquadronManual))
            {
                Svc.Log.Debug($"Using squadron manual: {config.RequiredSquadronManual}");
                ActionManagerEx.UseItem(config.RequiredSquadronManual);
                return TaskResult.Retry;
            }
            else
            {
                return TaskResult.Retry;
            }
        }

        if (!ConsumableChecker.IsManualled(config) && InventoryManager.Instance()->GetInventoryItemCount(config.RequiredManual) != 0)
        {
            if (ActionManagerEx.CanUseAction(ActionType.Item, config.RequiredManual))
            {
                Svc.Log.Debug($"Using manual: {config.RequiredManual}");
                ActionManagerEx.UseItem(config.RequiredManual);
                return TaskResult.Retry;
            }
            else
            {
                return TaskResult.Retry;
            }
        }

        var foodId = config.RequiredFood + (config.RequiredFoodHQ ? 1000000u : 0);
        if (!ConsumableChecker.IsFooded(config) && InventoryManager.Instance()->GetInventoryItemCount(config.RequiredFood, config.RequiredFoodHQ) != 0)
        {
            if (ActionManagerEx.CanUseAction(ActionType.Item, foodId))
            {
                Svc.Log.Debug($"Using food: {foodId}");
                ActionManagerEx.UseItem(foodId);
                return TaskResult.Retry;
            }
            else
            {
                return TaskResult.Retry;
            }
        }

        var potId = config.RequiredPotion + (config.RequiredPotionHQ ? 1000000u : 0);
        if (!ConsumableChecker.IsPotted(config) && InventoryManager.Instance()->GetInventoryItemCount(config.RequiredPotion, config.RequiredPotionHQ) != 0)
        {
            if (ActionManagerEx.CanUseAction(ActionType.Item, potId))
            {
                Svc.Log.Debug($"Using pot: {potId}");
                ActionManagerEx.UseItem(potId);
                return TaskResult.Retry;
            }
            else
            {
                return TaskResult.Retry;
            }
        }

        return TaskResult.Done;
    }

    // Guards against the runaway re-open loop reported on TC 2026-07-29 (crafting
    // menu flickers and sticks when a list starts).
    //
    // The task ALWAYS ends in `return TaskResult.Retry` after calling
    // OpenRecipeByRecipeId, and PreCrafting.Update() re-runs a Retry task every
    // 500 ms. The only exit is the `Done` check below, which depends on
    // Operations.GetSelectedRecipeEntry() -> RecipeNoteRecipeData.Ptr(). If that
    // struct read does not resolve on this client, the check never passes and the
    // recipe window is re-opened twice a second forever. Deduplicating the queue
    // (the previous attempt) does not help, because a SINGLE task is enough.
    //
    // So: never issue the open more than once per second for the same recipe. If
    // detection works this changes nothing (the task completes on the next tick);
    // if it is broken the window stays put instead of strobing.
    private static uint _lastOpenedRecipe;
    private static DateTime _lastOpenAttempt = DateTime.MinValue;
    private static int _openAttempts;

    private static void ReportRecipeOpenAttempt(uint recipeId)
    {
        // Reporting only - it must NOT gate the open call. Throttling the open
        // was one of today's speculative changes and is reverted; upstream issues
        // it on every retry and that is the baseline to diagnose from.
        var now = DateTime.Now;
        if (_lastOpenedRecipe != recipeId)
        {
            _lastOpenedRecipe = recipeId;
            _openAttempts = 0;
        }
        _lastOpenAttempt = now;
        _openAttempts++;

        // Info level on purpose: the reporting user runs at LogLevel 2, where
        // Svc.Log.Debug is invisible - that is why the first round produced "no
        // log at all". Fires a few times, only when something is actually wrong.
        //
        // Ptr()==0 alone is ambiguous: it is equally consistent with "the addon is
        // closed so RecipeList was freed" and with "the addon is open but we cannot
        // read it". The user reports the window visibly opening AND CLOSING in a
        // loop with the ingredients correctly assigned, so these three facts are
        // what separate the cases. Every value below is either a pointer CS already
        // handed us or a Dalamud-managed addon lookup - nothing is probed.
        if (_openAttempts is 5 or 10 or 20)
        {
            var instance = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
            var rd = RecipeNoteRecipeData.Ptr();
            var addonPtr = Svc.GameGui.GetAddonByName("RecipeNote", 1).Address;
            var addonOpen = addonPtr != nint.Zero;
            var addonVisible = addonOpen && ((AtkUnitBase*)addonPtr)->IsVisible;
            var agent = AgentRecipeNote.Instance();

            Svc.Log.Information(
                $"Artisan: recipe {recipeId} still not selected after {_openAttempts} open attempts. "
                + $"addon RecipeNote={(addonOpen ? $"0x{addonPtr:X} visible={addonVisible}" : "NOT OPEN")}, "
                + $"agent active={(agent != null && agent->AgentInterface.IsAgentActive())}, "
                + $"RecipeNote.Instance()={(nint)instance:X}, "
                + $"RecipeList={(nint)rd:X}"
                + (rd == null ? "" : $" (Recipes={(nint)rd->Recipes:X} count={rd->RecipesCount} sel={rd->SelectedIndex})")
                + $", CurState={Crafting.CurState}.");
        }
    }

    private static bool _searchFallbackLogged;
    private static DateTime _lastMismatchAction = DateTime.MinValue;

    // Fallback for the primary open call doing nothing (see the log evidence in
    // the commit). SearchRecipeByItemId has its own unique signature in TC's
    // binary and opens the crafting log via the item-search route. Only fires
    // after repeated failures while the window is still closed, so a healthy
    // client never takes this path.
    private static void TryOpenViaSearchFallback(Recipe recipe)
    {
        if (_openAttempts < 3)
            return;
        if (Svc.GameGui.GetAddonByName("RecipeNote", 1).Address != nint.Zero)
            return;
        var itemId = recipe.ItemResult.RowId;
        if (itemId == 0)
            return;

        if (!_searchFallbackLogged)
        {
            _searchFallbackLogged = true;
            Svc.Log.Information(
                $"Artisan: OpenRecipeByRecipeId has had no effect after {_openAttempts} attempts - "
                + $"falling back to SearchRecipeByItemId({itemId}). If the crafting log opens now, "
                + "the primary open function resolves to the wrong code on this client.");
        }
        AgentRecipeNote.Instance()->SearchRecipeByItemId(itemId);
    }

    // 🔴 宇宙筆記關掉之後怎麼開回來（2026-07-31 實機 log 定案）
    //
    // 觸發條件是「宇宙製作連做、下一輪要補消耗品」：前一次製作結束後角色停在
    // IdleBetween（人還在宇宙筆記裡），StartCrafting 於是排入 TaskExitCraft，
    // 那個 task 會把 WKSRecipeNotebook 關掉，角色才吃得下食物
    //（log 裡 PreparingToCraft=False 之後才 Using food，所以關窗是必要的）。
    //
    // 壞掉的是重開。原本這裡呼叫 OpenRecipeByRecipeId / SearchRecipeByItemId ——
    // 那是「一般製作手帳」的入口，宇宙配方根本不在裡面：
    //   19:50:27~35  addon RecipeNote=NOT OPEN, agent active=False（開不起來）
    //   19:55:57     偶爾真的把一般手帳開起來 → 遊戲回「尚未習得所選配方，無法查看」
    //                連五次 → 觸發 Artisan 自己的錯誤上限，整個製作模式被關掉
    // 使用者的回報就是這個：關窗吃完東西後看不到視窗、只聽得到開窗音效。
    //
    // 對照組（19:20，同一版、同一個食物）沒有 "Closing recipe menu" 這行 ——
    // 因為那次是剛接新任務、不在 IdleBetween，沒關窗就沒事。
    //
    // 正解是走玩家自己會走的路：任務資訊面板上的「宇宙製作筆記」按鈕
    //（WKSMissionInfomation 的 button 27）。點的是真的 UI 按鈕。
    // ⚠️ 不要改成自己拼一個原生入口 —— CS 目前沒有 AgentWKSRecipeNotebook
    //（只有 WKSHud / WKSMission / WKSMissionInfomation / WKSAnnounce）。
    private static DateTime _cosmicWaitSince = DateTime.MinValue;
    private static DateTime _lastCosmicReopen = DateTime.MinValue;
    // 點按鈕之間留時間讓視窗真的開起來，不要用重試節奏猛點。
    private const int CosmicReopenIntervalSeconds = 2;
    // 等不到就中止，把控制權還給呼叫端（ICE 有自己的任務流程會重新處理），
    // 而不是留在這裡空轉直到錯誤上限把製作模式關掉。
    private const int CosmicReopenGiveUpSeconds = 30;

    private static TaskResult ReopenCosmicNotebook(Recipe recipe)
    {
        var now = DateTime.Now;
        if (_cosmicWaitSince == DateTime.MinValue)
            _cosmicWaitSince = now;

        if (now - _cosmicWaitSince > TimeSpan.FromSeconds(CosmicReopenGiveUpSeconds))
        {
            _cosmicWaitSince = DateTime.MinValue;
            DuoLog.Error(
                $"Artisan：宇宙筆記已關閉，等了 {CosmicReopenGiveUpSeconds} 秒仍開不回來，"
                + $"無法選取宇宙配方 {recipe.RowId}。已中止這次製作。");
            return TaskResult.Abort;
        }

        if (now - _lastCosmicReopen < TimeSpan.FromSeconds(CosmicReopenIntervalSeconds))
            return TaskResult.Retry;

        if (TryGetAddonMaster<AddonMaster.WKSMissionInfomation>("WKSMissionInfomation", out var mission)
            && mission.IsAddonReady)
        {
            _lastCosmicReopen = now;
            Svc.Log.Information(
                $"Artisan：宇宙筆記已關閉（多半是剛才為了使用消耗品而離開製作），"
                + $"點任務資訊面板的「宇宙製作筆記」按鈕重新開啟以選取配方 {recipe.RowId}。");
            mission.CosmoCraftingLog();
        }

        return TaskResult.Retry;
    }

    private static bool _cosmicStateDumped = false;
    // 宇宙筆記逐項選取的游標（一幀送一個 callback，見 TaskSelectRecipe）
    private static int _cosmicSelectIndex;
    // 宇宙任務的配方清單很短（實測 2～3 項）；純粹是繞回重試的上限，不是清單長度。
    private const int MaxCosmicRecipeEntries = 20;

    /// <summary>
    /// 把 WKSRecipeNotebook 的 AtkValues 與節點文字傾印一次。
    /// 實機證實 GetSelectedRecipeEntry()（讀 RecipeNote.RecipeList）反映不了宇宙筆記的
    /// 選取狀態，所以要另找可判定的來源。AtkValues 比節點索引可靠：它有 AtkValuesCount
    /// 可以做邊界、而且是型別化的，不必猜 UI 佈局。
    /// </summary>
    private static unsafe void DumpCosmicStateOnce(AtkUnitBase* addon, Recipe recipe)
    {
        if (_cosmicStateDumped || addon == null)
            return;
        _cosmicStateDumped = true;

        var itemId = recipe.ItemResult.RowId;
        var itemName = recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? "?";
        Svc.Log.Information(
            $"Artisan: 宇宙筆記狀態傾印 — 目標 recipe={recipe.RowId} item={itemId} \"{itemName}\"");

        var sb = new System.Text.StringBuilder();
        sb.Append($"Artisan: AtkValues (count={addon->AtkValuesCount}):");
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var v = &addon->AtkValues[i];
            switch (v->Type)
            {
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                    sb.Append($" [{i}]i={v->Int}"); break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                    sb.Append($" [{i}]u={v->UInt}"); break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                    sb.Append($" [{i}]b={v->Byte != 0}"); break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                    sb.Append($" [{i}]s=\"{v->String.ExtractText()}\""); break;
                default:
                    sb.Append($" [{i}]{v->Type}"); break;
            }
        }
        Svc.Log.Information(sb.ToString());

        var nb = new System.Text.StringBuilder();
        nb.Append($"Artisan: 節點文字 (NodeListCount={addon->UldManager.NodeListCount}):");
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var n = addon->UldManager.NodeList[i];
            if (n == null || n->Type != NodeType.Text)
                continue;
            var t = n->GetAsAtkTextNode();
            if (t == null)
                continue;
            var text = t->NodeText.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                nb.Append($" [{i}]=\"{text}\"");
        }
        Svc.Log.Information(nb.ToString());
    }

    public static TaskResult TaskSelectRecipe(Recipe recipe)
    {
        var re = Operations.GetSelectedRecipeEntry();
        if ((re != null && re->RecipeId == recipe.RowId) || (Crafting.CurState is not Crafting.State.IdleBetween and not Crafting.State.IdleNormal))
            return TaskResult.Done;

        if (recipe.Number == 0)
        {
            var addon = Crafting.GetCosmicAddon();

            if (addon == null)
                return ReopenCosmicNotebook(recipe);

            _cosmicWaitSince = DateTime.MinValue;

            // 宇宙筆記的選取判定：用 addon 自己的 AtkValues，不要用 GetSelectedRecipeEntry()。
            //
            // 走過的兩條死路（都是實機證實的，不要再試）：
            //  1. Callback.Fire 逐項選 + GetSelectedRecipeEntry() 比對 → 畫面會依序切換，
            //     但比對永遠不成立、無限在配方之間跳。原因是 GetSelectedRecipeEntry() 讀的是
            //     RecipeNote.Instance()->RecipeList，那份資料只有 OpenRecipeByRecipeId 會填，
            //     Callback.Fire 只動畫面上的選取。
            //  2. 視窗已開時改用 OpenRecipeByRecipeId → 台服會把視窗「關閉重建」，於是每
            //     500ms 閃一次、使用者根本看不到視窗，只聽得到開關音效。
            //
            // 正解：用 Callback.Fire 選（那個是有效的），改成讀 AtkValues 驗證。
            // 2026-07-31 兩次實機傾印比對得出的佈局：
            //   AtkValues[45] = 當前選取的 item id（收藏品會是 id + 500000，例如 548368）
            //   AtkValues[46] = 當前選取的品項名稱
            const int SelectedItemIdValue = 45;
            const int SelectedItemNameValue = 46;
            const uint CollectableItemIdOffset = 500000;

            if (addon->AtkValuesCount <= SelectedItemNameValue)
            {
                // 佈局與取樣時不同，不要瞎猜；留下紀錄讓下次能重新取樣。
                DumpCosmicStateOnce(addon, recipe);
                return TaskResult.Retry;
            }

            var targetItemId = recipe.ItemResult.RowId;
            var targetItemName = recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? string.Empty;

            var selectedId = addon->AtkValues[SelectedItemIdValue].UInt;
            var selectedName = addon->AtkValues[SelectedItemNameValue].Type
                is FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString
                ? addon->AtkValues[SelectedItemNameValue].String.ExtractText()
                : string.Empty;

            var idMatches = selectedId == targetItemId || selectedId == targetItemId + CollectableItemIdOffset;
            var nameMatches = targetItemName.Length > 0 && selectedName == targetItemName;

            if (idMatches || nameMatches)
            {
                Svc.Log.Information($"Artisan: 宇宙配方 {recipe.RowId}（{targetItemName}）已選中");
                _cosmicSelectIndex = 0;
                return TaskResult.Done;
            }

            // 還沒選中：一幀送一個選取 callback，讓遊戲有機會更新後再驗。
            if (_cosmicSelectIndex >= MaxCosmicRecipeEntries)
            {
                _cosmicSelectIndex = 0;
                DumpCosmicStateOnce(addon, recipe);
            }

            Callback.Fire(addon, false, 0, _cosmicSelectIndex);
            _cosmicSelectIndex++;
            return TaskResult.Retry;
        }
        else
        {
            // 🔴 TC: re-issuing OpenRecipeByRecipeId while the window is already
            // open CLOSES AND RECREATES it (watcher log 19:27: lifetimes of exactly
            // 500 ms - this task's retry delay - with a different addon pointer
            // every cycle). The window never lives long enough to populate
            // RecipeList, so the selection check above never passes. On global the
            // repeat call is an idempotent re-select; here it is lethal. So: call
            // it only while the addon is absent, and otherwise WAIT for the list
            // to populate. Re-issue only if it populated with the wrong recipe.
            var recipeNoteOpen = Svc.GameGui.GetAddonByName("RecipeNote", 1).Address != nint.Zero;
            if (!recipeNoteOpen)
            {
                ReportRecipeOpenAttempt(recipe.RowId);
                AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipe.RowId);
                TryOpenViaSearchFallback(recipe);
            }
            else if (re != null && re->RecipeId != recipe.RowId)
            {
                // Populated with the WRONG recipe. Do NOT call OpenRecipeByRecipeId
                // again here: on TC that closes and recreates the window, which is
                // the 500 ms open/close cycle seen in every log so far. Use the
                // item-search entry point instead - that is the call whose actual
                // job is selecting a recipe - and rate-limit it so a failure cannot
                // spin at the retry cadence.
                if ((DateTime.Now - _lastMismatchAction).TotalSeconds >= 2)
                {
                    _lastMismatchAction = DateTime.Now;
                    var itemId = recipe.ItemResult.RowId;
                    Svc.Log.Information(
                        $"Artisan: RecipeNote is open but has recipe {re->RecipeId} selected, "
                        + $"wanted {recipe.RowId}. OpenRecipeByRecipeId did not select it; "
                        + $"trying SearchRecipeByItemId({itemId}).");
                    if (itemId != 0)
                        AgentRecipeNote.Instance()->SearchRecipeByItemId(itemId);
                }
            }
            else if ((DateTime.Now - _lastOpenAttempt).TotalSeconds >= 5)
            {
                // Window open, list still populating - waiting is correct, but say
                // so occasionally: ReportRecipeOpenAttempt only fires on OPEN calls,
                // so a silent permanent wait would otherwise be invisible in logs.
                _lastOpenAttempt = DateTime.Now;
                Svc.Log.Information(
                    $"Artisan: RecipeNote is open, waiting for its recipe list to populate "
                    + $"(recipe {recipe.RowId}, RecipeList={(nint)RecipeNoteRecipeData.Ptr():X})");
            }
        }
        return TaskResult.Retry;
    }

    public static TaskResult TaskStartCraft(CraftType type)
    {
        if (TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmicAddon))
        {
            if (cosmicAddon == null)
                return TaskResult.Retry;

            Svc.Log.Debug($"Starting actual cosmic craft");
            Callback.Fire(cosmicAddon, true, 6);

            return TaskResult.Done;

        }

        var addon = (AddonRecipeNote*)Svc.GameGui.GetAddonByName("RecipeNote").Address;
        if (addon == null)
            return TaskResult.Retry;

        Svc.Log.Debug($"Starting {type} craft");
        Callback.Fire(&addon->AtkUnitBase, true, 8 + (int)type);
        return TaskResult.Done;
    }

    public static bool IsItemEquipped(uint ItemId) => InventoryManager.Instance()->GetItemCountInContainer(ItemId, InventoryType.EquippedItems) > 0;

    private static (InventoryType inv, int slot)? FindItemInInventory(uint ItemId, IEnumerable<InventoryType> inventories)
    {
        foreach (var inv in inventories)
        {
            var cont = InventoryManager.Instance()->GetInventoryContainer(inv);
            for (int i = 0; i < cont->Size; ++i)
            {
                if (cont->GetInventorySlot(i)->ItemId == ItemId)
                {
                    return (inv, i);
                }
            }
        }
        return null;
    }

    public static bool Occupied()
    {
        return Svc.Condition[ConditionFlag.Occupied]
           || Svc.Condition[ConditionFlag.Occupied30]
           || Svc.Condition[ConditionFlag.Occupied33]
           || Svc.Condition[ConditionFlag.Occupied38]
           || Svc.Condition[ConditionFlag.Occupied39]
           || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
           || Svc.Condition[ConditionFlag.OccupiedInEvent]
           || Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
           || Svc.Condition[ConditionFlag.OccupiedSummoningBell];
    }

    private static void ClickSynthButtons(void* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData)
    {
        if (eventType == AtkEventType.ButtonClick && eventParam is 14 or 15 or 16)
        {
            StartCraftingFromSynth(eventParam);
        }
        else
        {
            _clickButton?.OriginalDisposeSafe(thisPtr, eventType, eventParam, atkEvent, atkEventData);
        }

    }

    private static void StartCraftingFromSynth(int eventParam)
    {
        var re = Operations.GetSelectedRecipeEntry();
        var recipe = re != null ? Svc.Data.GetExcelSheet<Recipe>()?.GetRow(re->RecipeId) : null;
        if (recipe != null)
            StartCrafting(recipe.Value, eventParam is 14 ? CraftType.Normal : eventParam is 15 ? CraftType.Quick : CraftType.Trial);
        else
            DuoLog.Error($"Somehow recipe is null. Please report this on the Discord.");
    }
}
