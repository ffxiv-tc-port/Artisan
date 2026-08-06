using Artisan.Autocraft;
using Artisan.GameInterop;
using Artisan.GameInterop.CSExt;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.GenericHelpers;

namespace Artisan.CraftingLists
{
    public class CraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<uint> Items { get; set; } = new();

        public Dictionary<uint, ListItemOptions> ListItemOptions { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class NewCraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<ListItem> Recipes { get; set; } = new();

        public List<uint> ExpandedList { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class ListItem
    {
        public uint ID { get; set; }

        public int Quantity { get; set; }

        public ListItemOptions? ListItemOptions { get; set; } = new();

    }

    public class ListItemOptions
    {
        public bool NQOnly { get; set; }
        // TODO: custom RecipeConfig?

        public bool Skipping { get; set; }
    }

    public static class CraftingListFunctions
    {
        public static int CurrentIndex;

        public static bool Paused { get; set; } = false;

        public static Dictionary<uint, int>? Materials;

        public static TaskManager CLTM = new();

        public static TimeSpan ListEndTime = default(TimeSpan);

        public static void SetID(this NewCraftingList list)
        {
            var rng = new Random();
            var proposedRNG = rng.Next(1, 50000);
            while (P.Config.NewCraftingLists.Where(x => x.ID == proposedRNG).Any())
            {
                proposedRNG = rng.Next(1, 50000);
            }

            list.ID = proposedRNG;
        }

        public static Dictionary<uint, int> ListMaterials(this NewCraftingList list)
        {
            var output = new Dictionary<uint, int>();
            // This runs on a background thread (called from IngredientHelpers.GenerateList
            // while the list editor keeps drawing), and the list editor can add/remove/skip
            // recipes on list.Recipes concurrently. Iterate a snapshot so a structural change
            // to the live List<T> can't throw "Collection was modified" mid-enumeration here,
            // and don't mutate config objects or call P.Config.Save() from this thread
            // (ListItemOptions already defaults to a non-null instance; ?. covers the rare
            // legacy-null case without writing back).
            foreach (var item in list.Recipes.ToArray())
            {
                if (item.ListItemOptions?.Skipping == true || item.Quantity == 0) continue;
                Recipe r = LuminaSheets.RecipeSheet[item.ID];
                CraftingListHelpers.AddRecipeIngredientsToList(r, ref output, false, list);
            }

            return output;
        }

        public static bool Save(this NewCraftingList list, bool isNew = false)
        {
            if (list.Recipes.Count == 0 && !isNew) return false;

            list.Recipes.RemoveAll(x => LuminaSheets.RecipeSheet?.First(y => y.Value.RowId == x.ID).Value.Number == 0);

            list.SkipIfEnough = P.Config.DefaultListSkip;
            list.Materia = P.Config.DefaultListMateria;
            list.Repair = P.Config.DefaultListRepair;
            list.RepairPercent = P.Config.DefaultListRepairPercent;
            list.AddAsQuickSynth = P.Config.DefaultListQuickSynth;

            if (list.AddAsQuickSynth)
            {
                foreach (var item in list.Recipes)
                {
                    if (item.ListItemOptions == null)
                    {
                        item.ListItemOptions = new ListItemOptions();
                    }
                    item.ListItemOptions.NQOnly = true;
                }
            }

            P.Config.NewCraftingLists.Add(list);
            P.Config.Save();
            return true;
        }

        public static unsafe bool RecipeWindowOpen()
        {
            return TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) && addon->AtkUnitBase.IsVisible && Operations.GetSelectedRecipeEntry() != null;
        }

        public static unsafe bool CosmicLogOpen()
        {
            return TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmicaddon) && cosmicaddon->IsVisible;
        }

        public static unsafe void OpenRecipeByID(uint recipeID, bool skipThrottle = false)
        {
            PreCrafting.TaskSelectRecipe(LuminaSheets.RecipeSheet[recipeID]);
            //if (Crafting.CurState != Crafting.State.IdleNormal) return;

            //var re = Operations.GetSelectedRecipeEntry();

            //if (!TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) || (re != null && re->RecipeId != recipeID))
            //{
            //    AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID);
            //}
        }

        public static bool HasItemsForRecipe(uint currentProcessedItem)
        {
            if (currentProcessedItem == 0) return false;
            var recipe = LuminaSheets.RecipeSheet[currentProcessedItem];
            if (recipe.RowId == 0) return false;

            return CraftingListUI.CheckForIngredients(recipe, false);
        }

        private static DateTime _lastConsumableLog = DateTime.MinValue;
        private static DateTime _lastBranchLog = DateTime.MinValue;
        private static string _lastBranch = "";

        // Names the guard branch that stopped ProcessList before it could reach
        // recipe selection. Info level (the reporting user runs at LogLevel 2) and
        // throttled, but always logs immediately when the branch CHANGES so a loop
        // between two branches is visible rather than averaged away.
        private static void ReportBranch(string branch)
        {
            if (branch != _lastBranch || (DateTime.Now - _lastBranchLog).TotalSeconds >= 3)
            {
                _lastBranch = branch;
                _lastBranchLog = DateTime.Now;
                Svc.Log.Information($"Artisan: ProcessList stopped early at [{branch}] "
                                    + $"(CurState={Crafting.CurState}, "
                                    + $"PreCrafting.Tasks={PreCrafting.Tasks.Count}, CLTM.IsBusy={CLTM.IsBusy})");
            }
        }


        internal static unsafe void ProcessList(NewCraftingList selectedList)
        {
            var isCrafting = Svc.Condition[ConditionFlag.Crafting];
            var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
            Materials ??= selectedList.ListMaterials();

            if (Paused)
            {
                return;
            }

            if (CurrentIndex < selectedList.ExpandedList.Count)
            {
                if (CraftingListUI.CurrentProcessedItem != selectedList.ExpandedList[CurrentIndex])
                {
                    CraftingListUI.CurrentProcessedItem = selectedList.ExpandedList[CurrentIndex];
                    CraftingListUI.CurrentProcessedItemCount = 1;
                    CraftingListUI.CurrentProcessedItemIndex = CurrentIndex;
                    CraftingListUI.CurrentProcessedItemListCount = selectedList.ExpandedList.Count(v => v == CraftingListUI.CurrentProcessedItem);

                }
                else if (CraftingListUI.CurrentProcessedItemIndex != CurrentIndex)
                {
                    CraftingListUI.CurrentProcessedItemIndex = CurrentIndex;
                    CraftingListUI.CurrentProcessedItemCount++;
                }
            }
            else
            {
                Svc.Log.Verbose("End of Index");
                CurrentIndex = 0;
                CraftingListUI.Processing = false;
                Operations.CloseQuickSynthWindow();
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromSeconds(5)));

                if (P.Config.PlaySoundFinishList)
                    Sounds.SoundPlayer.PlaySound();
                return;
            }

            var recipe = LuminaSheets.RecipeSheet[CraftingListUI.CurrentProcessedItem];
            var options = selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions;
            var config = /* options?.CustomConfig ?? */ P.Config.RecipeConfigs.GetValueOrDefault(CraftingListUI.CurrentProcessedItem) ?? new();
            var needToRepair = selectedList.Repair && RepairManager.GetMinEquippedPercent() < selectedList.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            PreCrafting.CraftType type = (options?.NQOnly ?? false) && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;

            if (Crafting.QuickSynthState.Max > 0 && (needToRepair || Crafting.QuickSynthCompleted || selectedList.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (PreCrafting.Tasks.Count > 0 || Crafting.CurState is not Crafting.State.IdleNormal and not Crafting.State.IdleBetween and not Crafting.State.InvalidState)
            {
                return;
            }

            if (recipe.SecretRecipeBook.RowId != 0)
            {
                if (!PlayerState.Instance()->IsSecretRecipeBookUnlocked(recipe.SecretRecipeBook.RowId))
                {
                    SeString error = new SeString(
                        new TextPayload("You haven't unlocked the recipe book "),
                        new ItemPayload(recipe.SecretRecipeBook.Value.Item.RowId),
                        new UIForegroundPayload(1),
                        new TextPayload(recipe.SecretRecipeBook.Value.Name.ToString()),
                        RawPayload.LinkTerminator,
                        UIForegroundPayload.UIForegroundOff,
                        new TextPayload(" for this recipe. Moving on."));

                    Svc.Chat.Print(new Dalamud.Game.Text.XivChatEntry()
                    {
                        Message = error,
                        Type = Dalamud.Game.Text.XivChatType.ErrorMessage,
                    });

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }
                }
            }

            if (selectedList.SkipIfEnough && (preparing || !isCrafting))
            {
                var ItemId = recipe.ItemResult.RowId;
                int numMats = Materials.Any(x => x.Key == recipe.ItemResult.RowId) && !selectedList.SkipLiteral ? Materials.First(x => x.Key == recipe.ItemResult.RowId).Value : selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.RowId == ItemId) * recipe.AmountResult;
                if (numMats <= CraftingListUI.NumberOfIngredient(recipe.ItemResult.RowId))
                {
                    DuoLog.Information($"Skipping {recipe.ItemResult.Value.Name.ToDalamudString()} due to having enough in inventory [Skip Items you already have enough of]");

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }

                    return;
                }
            }

            if (!HasItemsForRecipe(CraftingListUI.CurrentProcessedItem) && (preparing || !isCrafting))
            {
                DuoLog.Error($"Insufficient materials for {recipe.ItemResult.Value.Name}. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (Svc.ClientState.LocalPlayer.ClassJob.RowId != recipe.CraftType.Value.RowId + 8)
            {
                PreCrafting.equipGearsetLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)recipe.CraftType.Value.RowId + 8), TimeSpan.FromMilliseconds(200)));

                ReportBranch("class-change");
                return;
            }

            bool needEquipItem = recipe.ItemRequired.RowId > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.RowId);
            if (needEquipItem)
            {
                PreCrafting.equipAttemptLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.RowId), TimeSpan.FromMilliseconds(200)));
                ReportBranch("equip-item");
                return;
            }

            if (Svc.ClientState.LocalPlayer.Level < recipe.RecipeLevelTable.Value.ClassJobLevel - 5 && Svc.ClientState.LocalPlayer.ClassJob.RowId == recipe.CraftType.Value.RowId + 8 && !isCrafting && !preparing)
            {
                DuoLog.Error("Insufficient level to craft this item. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (!Spiritbond.ExtractMateriaTask(selectedList.Materia))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                ReportBranch("materia-extraction");
                return;
            }

            if (selectedList.Repair && !RepairManager.ProcessRepair(selectedList))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                ReportBranch("repair");
                return;
            }

            if (selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions is null)
            {
                selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions = new ListItemOptions();
            }
            bool needConsumables = PreCrafting.NeedsConsumablesCheck(type, config);
            bool hasConsumables = PreCrafting.HasConsumablesCheck(config);

            if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
            {
                PreCrafting.MissingConsumablesMessage(recipe, config);
                Paused = false;
                return;
            }

            bool needFood = config != default && ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config);
            bool needPot = config != default && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config);
            bool needManual = config != default && ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config);
            bool needSquadronManual = config != default && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config);

            if (needFood || needPot || needManual || needSquadronManual)
            {
                // This block CLOSES the recipe window (TaskExitCraft) and then returns,
                // so recipe selection below never runs. If a flag never clears, that is
                // the open/close loop the user hears. Info level (their LogLevel is 2),
                // throttled, and only while actually stuck.
                if ((DateTime.Now - _lastConsumableLog).TotalSeconds >= 3)
                {
                    _lastConsumableLog = DateTime.Now;
                    Svc.Log.Information(
                        $"Artisan: blocked before recipe selection by consumables - "
                        + $"food={needFood} pot={needPot} manual={needManual} squadron={needSquadronManual}. "
                        + $"config: foodEnabled={config?.FoodEnabled} food={config?.RequiredFood} foodHQ={config?.RequiredFoodHQ}, "
                        + $"potEnabled={config?.PotionEnabled} pot={config?.RequiredPotion} potHQ={config?.RequiredPotionHQ}, "
                        + $"manual={config?.RequiredManual} squadron={config?.RequiredSquadronManual}. "
                        + $"CurState={Crafting.CurState}, PreCrafting.Tasks={PreCrafting.Tasks.Count}, CLTM.IsBusy={CLTM.IsBusy}. "
                        + "While this is true the recipe window is repeatedly closed by TaskExitCraft.");
                }

                // Same pile-up as the recipe-selection block below: the CLTM tasks only append to
                // PreCrafting.Tasks and complete immediately, so DelayNext(100) was the only thing keeping
                // this from re-queueing every frame. Wait for the queue to drain instead.
                if (!CLTM.IsBusy && !PreCrafting.Occupied())
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                    CLTM.DelayNext(100);
                }
                return;
            }

            if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
            {
                // ProcessList() runs once per frame (ProcessingWindow.Draw), and the CLTM task below only
                // appends to PreCrafting.Tasks, so it finishes on the very next tick and CLTM goes idle
                // again - !CLTM.IsBusy therefore provides no back-pressure whatsoever. PreCrafting.Occupied()
                // only reads ConditionFlags and never looks at the queue either, so while the crafting log is
                // still opening (TaskSelectRecipe calls OpenRecipeByRecipeId and returns Retry, which
                // PreCrafting.Update() only re-runs every 500ms) this piled up ~60 duplicate TaskSelectRecipe
                // entries per second. Each one that got its turn re-opened the recipe window, which is the
                // flickering / repeatedly reopening crafting menu that made a starting list look stuck.
                // Waiting for the queue to drain is the back-pressure that was missing; Occupied() itself must
                // NOT be made queue-aware, because tasks such as TaskUseConsumables call it from inside the
                // queue and would then deadlock by always seeing themselves as occupied.
                if (!CLTM.IsBusy)
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(500))));

                    if (!RecipeWindowOpen()) return;

                    if (type == PreCrafting.CraftType.Quick)
                    {
                        var lastIndex = selectedList.ExpandedList.LastIndexOf(CraftingListUI.CurrentProcessedItem);
                        var count = lastIndex - CurrentIndex + 1;
                        count = CheckWhatExpected(selectedList, recipe, count);
                        if (count >= 99)
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(99));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQS99WaitStart");
                            return;
                        }
                        else if (count > 0)
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(count));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQSCountWaitStart");
                            return;
                        }
                        else
                        {
                            DuoLog.Error($"For some reason tried to quick synth 0 of {recipe.ItemResult.Value.Name}. Skipping.");
                            var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                            while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                            {
                                ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                                CurrentIndex++;
                                if (CurrentIndex == selectedList.ExpandedList.Count)
                                    return;
                            }
                        }
                    }
                    else if (type == PreCrafting.CraftType.Normal)
                    {
                        CLTM.DelayNext((int)(Math.Min(P.Config.ListCraftThrottle2, 2) * 1000));
                        CLTM.Enqueue(() => SetIngredients(), "SettingIngredients");
                        CLTM.Enqueue(() => Operations.RepeatActualCraft(), "ListCraft");
                        CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListNormalWaitStart");
                        return;

                    }
                }

            }
        }

        private static int CheckWhatExpected(NewCraftingList selectedList, Recipe recipe, int count)
        {
            if (selectedList.SkipIfEnough)
            {
                var inventoryitems = CraftingListUI.NumberOfIngredient(recipe.ItemResult.Value.RowId);
                var expectedNumber = 0;
                var stillToCraft = 0;
                var totalToCraft = selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Value.Name.ToDalamudString().ToString() == recipe.ItemResult.Value.Name.ToDalamudString().ToString()) * recipe.AmountResult;
                if (Materials!.Count(x => x.Key == recipe.ItemResult.RowId) == 0 || selectedList.SkipLiteral)
                {
                    // var previousCrafted = selectedList.Items.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.Value.Name.ToDalamudString().ToString() == recipe.ItemResult.Value.Name.ToDalamudString().ToString() && selectedList.Items.IndexOf(x) < CurrentIndex) * recipe.AmountResult;
                    stillToCraft = selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.RowId == recipe.ItemResult.RowId && selectedList.ExpandedList.IndexOf(x) >= CurrentIndex) * recipe.AmountResult - inventoryitems;
                    expectedNumber = stillToCraft > 0 ? Math.Min(selectedList.ExpandedList.Count(x => x == CraftingListUI.CurrentProcessedItem) * recipe.AmountResult, stillToCraft) : selectedList.ExpandedList.Count(x => x == CraftingListUI.CurrentProcessedItem);
                }
                else
                {
                    expectedNumber = Materials!.First(x => x.Key == recipe.ItemResult.RowId).Value;
                }

                var difference = Math.Min(totalToCraft - inventoryitems, expectedNumber);
                Svc.Log.Debug($"{recipe.ItemResult.Value.Name.ToDalamudString()} {expectedNumber} {difference}");
                double numberToCraft = Math.Ceiling((double)difference / recipe.AmountResult);

                count = (int)numberToCraft;
            }

            return count;
        }

        // 只在第一次進入宇宙製作分支時傾印一次節點清單，避免每幀洗版。
        private static bool _cosmicNodesDumped = false;

        // WKSRecipeNotebook 的 NQ／HQ 全選按鈕 —— 用**節點 ID**指定，不是節點清單索引。
        //
        // 2026-08-03 由台服 7.20 的 ui/uld/WKSRecipeNotebook.uld 離線解出（Lumina UldFile），
        // 不是猜的。根 widget 共 54 個節點，與實機 NodeListCount=54 完全對得起來：
        //   Node 38  Res            (0,152) 192x20   ← 素材列標題那一排
        //   Node 39  Component 1004 (100,-8) 44x28   ← NQ 全選按鈕
        //   Node 40  Component 1004 (148,-8) 44x28   ← HQ 全選按鈕
        //   Node 41  Text           (0,2)   86x13    ← 「素材」
        //   Node 42  Res            (0,164) 368x154  ← 素材列容器
        //   Node 43/44/45 Component 1028 368x62 ×3   ← 三個素材列
        //   Node 50  Component 1005 (228,315) 140x32 ← 製作按鈕
        // ULD 元件表裡 1004 的 Type 是 **Button**（1028 是 Custom、1029 是 TreeList）。
        //
        // ⚠️ 實機節點傾印印出來的 type=1004／1028／1029 **不是** 1000 + ComponentType，
        //    而是**該 ULD 檔自己的元件編號**（遊戲載入 ULD 時把這個值原樣搬進 AtkResNode.Type）。
        //    兩者長得很像所以很容易誤讀 —— 決定性的反例是 1028/1029：我們的 CS
        //    ComponentType 只到 Portrait=25，而 ULD 說 1028 的 Type 是 Custom(0)、
        //    1029 是 TreeList(12)。若真是 1000+ComponentType，它們會是 1000 和 1012。
        //    （曾據此推論「17/18 是 RadioButton＝普通/優質分頁鈕」，那是錯的。）
        //
        // 上游寫死的 NodeList[17]/[18] 在台服其實**指對了**（就是節點 39/40），
        // 但索引會隨版本漂移而 ID 不會，而且查 ID 失敗是回 null（可偵測），
        // 索引錯掉則是安靜地點到別的東西。ECommons 的 AddonMaster.WKSRecipeNotebook
        // 也是用 GetComponentButtonById(39)/(40)，兩邊獨立指向同一組 ID。
        private const uint CosmicNQButtonNodeId = 39;
        private const uint CosmicHQButtonNodeId = 40;

        /// <summary>
        /// 在 addon 的節點清單裡用節點 ID 找節點。
        /// </summary>
        /// <remarks>
        /// 刻意不用 <c>AtkUnitBase.GetComponentButtonById</c>：那是 <c>[MemberFunction]</c>，
        /// 要靠特徵碼掃描解位址，台服對不上時的失敗形式是原生層的問題。
        /// 這裡只讀 <c>NodeList</c> 與 <c>NodeId</c> 兩個純欄位，邊界是 <c>NodeListCount</c>，
        /// 完全不呼叫遊戲函式 —— 假設不成立時最差就是回 null。
        /// </remarks>
        private static unsafe AtkResNode* FindNodeById(AtkUnitBase* addon, uint nodeId)
        {
            if (addon == null)
                return null;

            var count = addon->UldManager.NodeListCount;
            for (var i = 0; i < count; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n != null && n->NodeId == nodeId)
                    return n;
            }
            return null;
        }

        /// <summary>
        /// 把 WKSRecipeNotebook 的節點清單傾印一次（只在按 ID 找不到按鈕時才會用到）。
        /// </summary>
        /// <remarks>
        /// 與上一版傾印的差別：**這次會印 NodeId**。上一版只印索引與 type，結果就是
        /// 拿到 log 也沒辦法直接對到 ULD 的節點編號，還得再繞一圈。
        /// 只讀 NodeList／NodeId／Type 三個純欄位，邊界一律是 NodeListCount。
        /// </remarks>
        private static unsafe void DumpCosmicNodeList(AtkUnitBase* addon)
        {
            if (addon == null)
                return;

            var count = addon->UldManager.NodeListCount;
            var dump = new System.Text.StringBuilder();
            dump.Append($"Artisan: WKSRecipeNotebook 節點傾印 (NodeListCount={count}):");
            for (var i = 0; i < count; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n == null) { dump.Append($" [{i}]=null"); continue; }
                dump.Append($" [{i}]id={n->NodeId},type={n->Type}");
                if (n->Type == NodeType.Text)
                {
                    var t = n->GetAsAtkTextNode();
                    if (t != null) dump.Append($",text=\"{t->NodeText}\"");
                }
                else if ((ushort)n->Type >= 1000)
                {
                    var c = n->GetAsAtkComponentNode();
                    if (c != null && c->Component != null)
                        dump.Append($",comp={c->Component->UldManager.NodeListCount}nodes");
                }
            }
            Svc.Log.Information(dump.ToString());
        }

        public static unsafe bool SetIngredients(EnduranceIngredients[]? setIngredients = null)
        {
            var recipe = Operations.GetSelectedRecipeEntry();
            if (recipe == null)
                return false;

            if (TryGetAddonByName<AtkUnitBase>("WKSRecipeNotebook", out var cosmicAddon) &&
                cosmicAddon->IsVisible)
            {
                // 用節點 ID 取按鈕（見上方 ULD 註解）。上游是寫死 NodeList[17]/[18]，
                // 索引超出 NodeListCount 就是讀陣列後方的堆積垃圾再當 AtkResNode* 解參考
                // —— 那是攔不到的 AVE。用 ID 查則是「找不到就回 null」。
                var nqNode = FindNodeById(cosmicAddon, CosmicNQButtonNodeId);
                var hqNode = FindNodeById(cosmicAddon, CosmicHQButtonNodeId);
                if (nqNode == null || hqNode == null)
                {
                    // 只印一次，避免每幀洗版；含節點 ID 是為了下次 ULD 真的改版時能立刻定位。
                    if (!_cosmicNodesDumped)
                    {
                        _cosmicNodesDumped = true;
                        DumpCosmicNodeList(cosmicAddon);
                    }
                    Svc.Log.Information(
                        $"Artisan: WKSRecipeNotebook 找不到素材全選按鈕節點 "
                        + $"(ID {CosmicNQButtonNodeId}/{CosmicHQButtonNodeId}，"
                        + $"NodeListCount={cosmicAddon->UldManager.NodeListCount})，無法指派宇宙製作的素材");
                    return false;
                }

                var nqBtn = nqNode->GetAsAtkComponentButton();
                var hqBtn = hqNode->GetAsAtkComponentButton();
                if (nqBtn == null || hqBtn == null)
                {
                    if (!_cosmicNodesDumped)
                    {
                        _cosmicNodesDumped = true;
                        DumpCosmicNodeList(cosmicAddon);
                    }
                    Svc.Log.Information(
                        $"Artisan: WKSRecipeNotebook 節點 {CosmicNQButtonNodeId}/{CosmicHQButtonNodeId} "
                        + $"不是 AtkComponentButton (typeNQ={nqNode->Type}, typeHQ={hqNode->Type})，"
                        + "無法指派宇宙製作的素材");
                    return false;
                }

                Svc.Log.Information("Artisan: 指派宇宙製作素材(點擊 NQ/HQ 全選按鈕)");
                nqBtn->ClickAddonButton(cosmicAddon);
                hqBtn->ClickAddonButton(cosmicAddon);

                return true;
            }

            if (TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) &&
                addon->AtkUnitBase.IsVisible &&
                AgentRecipeNote.Instance() != null &&
                RaptureAtkModule.Instance()->AtkModule.IsAddonReady(AgentRecipeNote.Instance()->AgentInterface.AddonId))
            {
                if (setIngredients == null || Endurance.IPCOverride)
                {
                    var diagHandled = 0;
                    var diag = new System.Text.StringBuilder();
                    diag.Append($"Artisan: SetIngredients walking RecipeNote nodes "
                                + $"(NodeCount={addon->AtkUnitBase.UldManager.NodeListCount}):");

                    // 🔴 上界必須在進迴圈前就驗，而且**只補判空是半套、完全擋不住**：
                    // UldManager.NodeList 的長度恰為 NodeListCount，索引越界讀到的是陣列後方的
                    // 堆積垃圾 —— 那不是 null。再把垃圾交給 GetAsAtkComponentNode()
                    //（FFXIVClientStructs 裡是 [MemberFunction] 原生呼叫，不是受管理轉型）就是
                    // AccessViolationException；AVE 是 corrupted-state exception，
                    // 底下那個 try/catch 與任何例外隔離包裝**一律攔不到**。
                    // 這個迴圈用到外層 NodeList[18..23]（i = 0..5），所以要求 count >= 24。
                    var outerList = addon->AtkUnitBase.UldManager.NodeList;
                    var outerCount = addon->AtkUnitBase.UldManager.NodeListCount;
                    if (outerList == null || outerCount < 24)
                    {
                        Svc.Log.Information(diag.ToString()
                            + $" -> RecipeNote NodeList 不足（count={outerCount}，需要 24），"
                            + "這次不指派素材。失敗形式是「沒有指派」而不是崩潰。");
                        return false;
                    }

                    for (int i = 0; i <= 5; i++)
                    {
                        try
                        {
                            diag.Append($" [{i}]node{23 - i}=");

                            var rawNode = outerList[23 - i];
                            if (rawNode == null)
                            {
                                diag.Append("NULL");
                                continue;
                            }

                            // 轉型前先驗型別：AtkResNode 宣告 Size = 0xB0，而 AtkComponentNode.Component
                            // 在 FieldOffset 0xB0 ⇒ 對非 component 節點讀 Component 是讀出界 16 bytes。
                            // Type >= 1000 才是 component 家族。
                            if ((int)rawNode->Type < 1000)
                            {
                                diag.Append($"type{(int)rawNode->Type}");
                                continue;
                            }

                            var node = rawNode->GetAsAtkComponentNode();
                            if (node == null || node->Component == null)
                            {
                                diag.Append("NULL");
                                continue;
                            }

                            // node->AtkResNode 是 FieldOffset 0 的內嵌結構，位址等同 rawNode。
                            if (!rawNode->IsVisible())
                            {
                                diag.Append("hidden");
                                continue;
                            }

                            // 內層元件同樣要驗：本區塊用到元件的 NodeList[11] 與 [14] ⇒ 要求 count >= 15。
                            var innerList = node->Component->UldManager.NodeList;
                            var innerCount = node->Component->UldManager.NodeListCount;
                            if (innerList == null || innerCount < 15)
                            {
                                diag.Append($"inner-count{innerCount}");
                                continue;
                            }

                            var hqMenuNode = innerList[11];
                            if (hqMenuNode == null)
                            {
                                diag.Append("inner-null");
                                continue;
                            }

                            // 原本這個判斷被求值兩次（診斷一次、決策一次），是 TOCTOU；取一次共用。
                            var hqMenuVisible = hqMenuNode->IsVisible();
                            diag.Append(hqMenuVisible ? "visible/hq-menu" : "visible/material");

                            diagHandled++;

                            if (hqMenuVisible)
                            {
                                var ingredient = LuminaSheets.RecipeSheet.Values.Where(x => x.RowId == Endurance.RecipeID).FirstOrDefault().Ingredients().ElementAt(i).Item;

                                var buttonNode = innerList[14];
                                if (buttonNode == null)
                                {
                                    diag.Append("(btn-node-null)");
                                    continue;
                                }

                                var btn = buttonNode->GetAsAtkComponentButton();
                                if (btn == null)
                                {
                                    diag.Append("(btn-null)");
                                    continue;
                                }

                                try
                                {
                                    // ⚠️ ClickAddonButton 的第一個參數是 by-value `this AtkComponentButton`
                                    // ⇒ **傳參當下就解參考**，內部守衛救不了呼叫端的 null，
                                    // 而這個 try 只擋得住受管理例外。null 必須在上面就攔掉。
                                    btn->ClickAddonButton((AtkComponentBase*)addon, 4, EventType.CHANGE);
                                }
                                catch (Exception ex)
                                {
                                    ex.Log();
                                }
                                var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextIconMenu").Address;
                                if (contextMenu != null)
                                {
                                    Callback.Fire(contextMenu, true, 0, 0, 0, ingredient, 0);
                                }
                            }
                            else
                            {
                                for (int m = 0; m <= 100; m++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material((uint)i, false);
                                }

                                for (int m = 0; m <= 100; m++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material((uint)i, true);
                                }
                            }

                        }
                        catch
                        {
                            return false;
                        }
                    }

                    if (diagHandled == 0)
                        Svc.Log.Information(diag.ToString()
                            + " -> NO slot handled, nothing assigned. The hardcoded node "
                            + "indices do not match this client's RecipeNote layout.");
                    else
                        Svc.Log.Information(diag.ToString() + $" -> handled {diagHandled} slot(s).");
                }
                else
                {
                    if (setIngredients != null)
                    {
                        var curRec = Operations.GetSelectedRecipeEntry();
                        int i = 0;
                        foreach (ref var ingredient in curRec->IngredientsSpan)
                        {
                            try
                            {
                                if (ingredient.ItemId == 0)
                                    break;
                                var nq = setIngredients[i].NQSet;
                                var hq = setIngredients[i].HQSet;

                                ingredient.SetSpecific(nq, hq, false);
                                Svc.Log.Debug($"{nq} {hq} {ingredient.ItemId.NameOfItem()} {ingredient.NumAssignedNQ} {ingredient.NumAssignedHQ}");
                                i++;
                            }
                            catch (Exception e)
                            {
                                e.Log();
                                return false;
                            }
                        }
                    }
                }
            }
            else
            {
                return false;
            }

            return true;
        }
    }
}
