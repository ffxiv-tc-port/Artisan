using Artisan.CraftingLists;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.Sounds;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.CircularBuffers;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static ECommons.GenericHelpers;
using Artisan.IPC;

namespace Artisan.Autocraft
{
    public class EnduranceIngredients
    {
        public int HQSet { get; set; }
        public int IngredientSlot { get; set; }
        public int NQSet { get; set; }
    }

    internal static unsafe class Endurance
    {
        internal static bool IPCOverride = false;
        internal static bool SkipBuffs = false;
        internal static CircularBuffer<long> Errors = new(5);
        static CircularBuffer<long> FailedStarts = new(5);

        internal static List<int>? HQData = null;

        internal static ushort RecipeID
        {
            get;
            set
            {
                if (field != value)
                {
                    P.Config.CraftingX = false;
                    P.Config.CraftX = 0;
                }
                field = value;
            }
        }

        internal static EnduranceIngredients[] SetIngredients = new EnduranceIngredients[6];

        internal static readonly List<uint> UnableToCraftErrors = new List<uint>()
        {
            1134,1135,1136,1137,1138,1139,1140,1141,1142,1143,1144,1145,1146,1148,1149,1198,1199,1222,1223,1224,
        };

        internal static bool Enable
        {
            get => enable;
            set
            {
                enable = value;
            }
        }

        internal static string RecipeName
        {
            get => RecipeID == 0 ? "No Recipe Selected".Loc() : LuminaSheets.RecipeSheet[RecipeID].ItemResult.Value.Name.ToDalamudString().ToString().Trim();
        }

        /// <summary>呼叫 <c>ToggleEndurance(true)</c> 的次數（不論成不成功）；<see cref="DutyPause"/> 用它判斷待恢復期間有沒有人動過。</summary>
        internal static int StartRequests { get; private set; }

        internal static void ToggleEndurance(bool enable)
        {
            if (enable)
                StartRequests++;

            // 收不到製作事件時不開（IPC 代為製作也走這裡）；當成關閉走下面既有分支，勾選框先設真的欄位會被設回去。
            if (enable && Crafting.RefuseAutoCraftIfNoEventHook("耐力模式"))
                enable = false;

            if (RecipeID > 0 && enable)
            {
                Enable = enable;
                // 新的一輪：缺消耗品的提示重新開始算，不然使用者補了貨再開還是看不到下一次提示。
                PreCrafting.ResetMissingConsumablesNotice();
            }
            else if (Enable)
            {
                Svc.Log.Debug("Endurance toggled off");
                Enable = false;
                IPCOverride = false;
                PreCrafting.Tasks.Clear();
            }
        }

        internal static void Dispose()
        {
            Svc.Toasts.ErrorToast -= Toasts_ErrorToast;
            Svc.Toasts.ErrorToast -= CheckNonMaxQuantityModeFinished;
            Svc.Toasts.ErrorToast -= CheckCraftBlockingError;
        }

        internal static void Draw()
        {
            if (CraftingListUI.Processing)
            {
                ImGui.TextWrapped("Processing list...".Loc());
                return;
            }

            ImGui.TextWrapped("Endurance mode is Artisan's way to repeat the same craft over and over, either so many times or until you run out of materials. It has full capabilities to automatically repair your gear once a piece is under a certain percentage, use food/potions/exp manuals and extract materia from spiritbonding. Please note these settings are independent of crafting list settings, and only intended to be used to craft the one item repeatedly.".Loc());
            ImGui.Separator();
            ImGui.Spacing();

            if (RecipeID == 0)
            {
                ImGuiEx.TextV(ImGuiColors.DalamudRed, "No recipe selected".Loc());
            }
            else
            {
                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                    ImGui.BeginDisabled();

                if (ImGui.Checkbox("Enable Endurance Mode".Loc(), ref enable))
                {
                    ToggleEndurance(enable);
                }

                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                {
                    ImGui.EndDisabled();

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("You cannot start Endurance as you do not possess ingredients to craft this recipe.".Loc());
                        ImGui.EndTooltip();
                    }
                }

                ImGuiComponents.HelpMarker("In order to begin Endurance Mode crafting you should first select the recipe in the crafting menu.\nEndurance Mode will automatically repeat the selected recipe similar to Auto-Craft but will factor in food/medicine buffs before doing so.".Loc());

                ImGuiEx.Text("Recipe: ?? ??".Loc(RecipeName, RecipeID != 0 ? $"({LuminaSheets.ClassJobSheet[LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8].Abbreviation})" : ""));
            }

            bool repairs = P.Config.Repair;
            if (ImGui.Checkbox("Automatic Repairs".Loc(), ref repairs))
            {
                P.Config.Repair = repairs;
                P.Config.Save();
            }
            ImGuiComponents.HelpMarker(SharedText.AutoRepairHelp.Loc(RepairManager.GetMinEquippedPercent(), RepairManager.GetNPCRepairPrice()));
            if (P.Config.Repair)
            {
                //ImGui.SameLine();
                ImGui.PushItemWidth(200);
                int percent = P.Config.RepairPercent;
                if (ImGui.SliderInt("##repairp", ref percent, 10, 100, $"%d%%"))
                {
                    P.Config.RepairPercent = percent;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    P.Config.Save();
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
                ImGui.BeginDisabled();

            bool materia = P.Config.Materia;
            if (ImGui.Checkbox("Automatically Extract Materia".Loc(), ref materia))
            {
                P.Config.Materia = materia;
                P.Config.Save();
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
            {
                ImGui.EndDisabled();

                ImGuiComponents.HelpMarker(SharedText.MateriaExtractionNotUnlocked.Loc());
            }
            else
                ImGuiComponents.HelpMarker(SharedText.AutoMateriaExtractionHelp.Loc());

            ImGui.Checkbox("Craft only X times".Loc(), ref P.Config.CraftingX);
            if (P.Config.CraftingX)
            {
                ImGui.Text("Number of Times:".Loc());
                ImGui.SameLine();
                ImGui.PushItemWidth(200);
                if (ImGui.InputInt("###TimesRepeat", ref P.Config.CraftX))
                {
                    if (P.Config.CraftX < 0)
                        P.Config.CraftX = 0;
                }
            }

            if (ImGui.Checkbox("Use Quick Synthesis where possible".Loc(), ref P.Config.QuickSynthMode))
            {
                P.Config.Save();
            }

            bool stopIfFail = P.Config.EnduranceStopFail;
            if (ImGui.Checkbox("Disable Endurance Mode Upon Failed Craft".Loc(), ref stopIfFail))
            {
                P.Config.EnduranceStopFail = stopIfFail;
                P.Config.Save();
            }

            bool stopIfNQ = P.Config.EnduranceStopNQ;
            if (ImGui.Checkbox("Disable Endurance Mode Upon Crafting an NQ item".Loc(), ref stopIfNQ))
            {
                P.Config.EnduranceStopNQ = stopIfNQ;
                P.Config.Save();
            }

            if (ImGui.Checkbox("Max Quantity Mode".Loc(), ref P.Config.MaxQuantityMode))
            {
                P.Config.Save();
            }

            ImGuiComponents.HelpMarker("Will set ingredients for you, to maximise the amount of crafts possible.".Loc());
        }

        internal static void DrawRecipeData()
        {
            var curRec = Operations.GetSelectedRecipeEntry();
            if (curRec is null || curRec->RecipeId == 0)
                return;

            RecipeID = curRec->RecipeId;
            try
            {
                for (int i = 0; i < curRec->IngredientsSpan.Length; i++)
                {
                    var ing = curRec->IngredientsSpan[i];
                    if (ing.ItemId == 0)
                        break;
                    var nq = ing.NumAssignedNQ;
                    var hq = ing.NumAssignedHQ;

                    // 每幀路徑:這一格的內容本來就等於要寫的值時沿用原物件;要換值時整顆換新、不就地改。
                    if (SetIngredients[i] is { IngredientSlot: 0 } cur && cur.NQSet == nq && cur.HQSet == hq)
                        continue;

                    SetIngredients[i] = new EnduranceIngredients()
                    {
                        NQSet = nq,
                        HQSet = hq,
                    };

                    //Svc.Log.Debug($"Assigned {nq}NQ, {hq}HQ {ing.ItemId.NameOfItem()}");
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Setting Recipe ID");
                RecipeID = 0;
            }


        }

        internal static void Init()
        {
            Svc.Toasts.ErrorToast += Toasts_ErrorToast;
            Svc.Toasts.ErrorToast += CheckNonMaxQuantityModeFinished;
            Svc.Toasts.ErrorToast += CheckCraftBlockingError;
        }

        // 終局條件；刻意不含 1147（指派途中會短暫出現）、
        // 1148／1149（時間／位置性，會自己好）、1135~1142（Artisan 自己會切職業）。
        private static readonly uint[] CraftBlockingLogMessages =
            [1134, 1143, 1144, 1145, 1146, 1198, 1199, 1222, 1223, 1224];

        // 通用斷路器要「10 秒內 5 次」，而重試間隔約 3 秒 ⇒ 條件永遠不成立，故在此收到就停。
        private static void CheckCraftBlockingError(ref SeString message, ref bool isHandled)
        {
            if (!Enable && !CraftingListUI.Processing)
                return;

            // 🔴 另一端是 Lumina 的 row.Text.ExtractText() ⇒ 這一端也必須走 Lumina 解析器。
            // 不要用 message.ExtractText():它綁到 ECommons 已標 [Obsolete] 的那支,會把
            // 連字符 payload(02 1F 01 03)整個丟掉 ⇒ 含破折號的訊息永遠比不中,
            // 表現是「遊戲說這個製作不可能成功,但斷路器沒動」。
            var text = LuminaText.Extract(message);
            var sheet = Svc.Data?.GetExcelSheet<LogMessage>();
            if (sheet is null || string.IsNullOrEmpty(text))
                return;

            uint matched = 0;
            foreach (var id in CraftBlockingLogMessages)
            {
                // 1134~1143 的表文字前面有 ObjStr 參數（角色名），ExtractText 把巨集攤成空字串
                // ⇒ 表文字是實際訊息的**後綴**而不是全等；用 == 比這幾則永遠比不中。
                if (sheet.TryGetRow(id, out var row))
                {
                    var pattern = row.Text.ExtractText();
                    if (pattern.Length > 0 && text.EndsWith(pattern, StringComparison.Ordinal))
                    {
                        matched = id;
                        break;
                    }
                }
            }

            if (matched == 0)
                return;

            var itemName = "?";
            try
            {
                if (LuminaSheets.RecipeSheet is not null
                    && LuminaSheets.RecipeSheet.TryGetValue(RecipeID, out var r))
                    itemName = r.ItemResult.ValueNullable?.Name.ExtractText() ?? "?";
            }
            catch
            {
                // 名稱只是說明文字，查不到不該讓斷路器本身失敗。
            }

            Svc.Log.Information(
                $"Artisan: game reported \"{text}\" (LogMessage {matched}) for recipe {RecipeID} ({itemName}). "
                + "This is terminal - retrying cannot change the outcome, so crafting is being stopped.");
            DuoLog.Error($"Artisan: {text} [recipe {RecipeID} - {itemName}]");
            NotifyForcedStopIfRunning("遊戲回報這個製作不可能成功");

            Errors.Clear();
            if (enable)
                ToggleEndurance(false);
            if (CraftingListUI.Processing)
                CraftingListFunctions.Paused = true;

            PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));
            P.TM.Abort();
            CraftingListFunctions.CLTM.Abort();
        }

        private static bool enable = false;
        private static void CheckNonMaxQuantityModeFinished(ref SeString message, ref bool isHandled)
        {
            // 🔴 理由同 CheckCraftBlockingError:右邊是 Lumina,左邊也必須是 Lumina。
            // (刻意保留短路求值 —— 這是聊天鉤子,每一則訊息都會進來,不要在條件外先攤平。)
            if (!P.Config.MaxQuantityMode && Enable &&
                (LuminaText.Extract(message) == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1147).Text.ExtractText() ||
                 LuminaText.Extract(message) == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1146).Text.ExtractText() ||
                 LuminaText.Extract(message) == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1145).Text.ExtractText() ||
                 LuminaText.Extract(message) == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1144).Text.ExtractText()))
            {
                if (P.Config.PlaySoundFinishEndurance)
                    SoundPlayer.PlaySound();

                ToggleEndurance(false);
                TataruPraiseIPC.NotifyEnduranceFinished("素材用完了");
            }
        }

        public static void Update()
        {
            if (!Enable) return;
            var needToRepair = P.Config.Repair && RepairManager.GetMinEquippedPercent() < P.Config.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            if ((Crafting.CurState == Crafting.State.QuickCraft && Crafting.QuickSynthCompleted) || needToRepair ||
                (P.Config.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (!P.TM.IsBusy && Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
            {
                var isCrafting = Svc.Condition[ConditionFlag.Crafting];
                var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
                var recipe = LuminaSheets.RecipeSheet[RecipeID];
                if (PreCrafting.Tasks.Count > 0)
                {
                    return;
                }

                if (P.Config.CraftingX && P.Config.CraftX == 0 || PreCrafting.GetNumberCraftable(recipe) == 0)
                {
                    ToggleEndurance(false);
                    P.Config.CraftingX = false;
                    DuoLog.Information("Craft X has completed.".Loc());
                    if (P.Config.PlaySoundFinishEndurance)
                        SoundPlayer.PlaySound();

                    // 🔴 刻意在 if 外面：提示音是使用者另外一個開關，這一句不受它管。
                    TataruPraiseIPC.NotifyEnduranceFinished("指定的份數做完了");

                    return;
                }

                if (RecipeID == 0)
                {
                    Svc.Toasts.ShowError(SharedText.EnduranceNoRecipeSet.Loc());
                    DuoLog.Error(SharedText.EnduranceNoRecipeSet.Loc());
                    NotifyForcedStop("沒有選配方");
                    ToggleEndurance(false);
                    return;
                }

                if ((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8 != CharacterInfo.JobID)
                {
                    PreCrafting.equipGearsetLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                bool needEquipItem = recipe.ItemRequired.RowId > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.RowId);
                if (needEquipItem)
                {
                    PreCrafting.equipAttemptLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.RowId), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (!Spiritbond.ExtractMateriaTask(P.Config.Materia))
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (P.Config.Repair && !RepairManager.ProcessRepair())
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                // 清單項的專用覆寫優先；耐久模式本身不屬於任何清單，所以平常這裡就是全域設定。
                var config = CraftingListFunctions.ActiveListItemOverride(RecipeID)
                    ?? P.Config.RecipeConfigs.GetValueOrDefault(RecipeID) ?? new();
                PreCrafting.CraftType type = P.Config.QuickSynthMode && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;
                bool needConsumables = PreCrafting.NeedsConsumablesCheck(type, config);
                bool hasConsumables = PreCrafting.HasConsumablesCheck(config);

                if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
                {
                    PreCrafting.MissingConsumablesMessage(recipe, config);
                    NotifyForcedStop("缺少要求的食物或藥水");
                    ToggleEndurance(false);
                    return;
                }
                else if (needConsumables && !hasConsumables)
                {
                    PreCrafting.NotifyMissingConsumables(recipe, config);
                }

                bool needFood = config != default && ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config);
                bool needPot = config != default && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config);
                bool needManual = config != default && ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config);
                bool needSquadronManual = config != default && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config);

                if (needFood || needPot || needManual || needSquadronManual)
                {
                    if (!P.TM.IsBusy && !PreCrafting.Occupied())
                    {
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                        P.TM.DelayNext(100);
                    }
                    return;
                }

                if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
                {
                    if (!P.TM.IsBusy)
                    {
                        PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(500)));

                        if (!CraftingListFunctions.RecipeWindowShows(recipe) && !CraftingListFunctions.CosmicLogOpen()) return;

                        if (type == PreCrafting.CraftType.Quick)
                        {
                            P.TM.Enqueue(Operations.QuickSynthTask(P.Config.CraftingX ? P.Config.CraftX : 99), 5000, "EnduranceQSStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.WaitStart, 5000, "EnduranceQSWaitStart");
                        }
                        else if (type == PreCrafting.CraftType.Normal)
                        {
                            P.TM.DelayNext(200);

                            if (P.Config.MaxQuantityMode)
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(), "EnduranceSetIngredientsNonLayout");
                            else
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(SetIngredients), "EnduranceSetIngredientsLayout");

                            P.TM.Enqueue(() => Operations.RepeatActualCraft(), 500, "EnduranceNormalStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.WaitStart, 500, "EnduranceNormalWaitStart");
                            P.TM.Enqueue(() =>
                            {
                                if (!RaphaelCache.InProgressAny())
                                {
                                    if (FailedStarts.Count() >= 5 && FailedStarts.All(x => x > Environment.TickCount64 - (10 * 1000)))
                                    {
                                        FailedStarts.Clear();
                                        if (Crafting.CurState is not Crafting.State.QuickCraft and not Crafting.State.InProgress and not Crafting.State.WaitStart)
                                        {
                                            if (!IPCOverride)
                                            {
                                                DuoLog.Error("Unable to start crafting. Disabling Endurance. ??".Loc(!P.Config.MaxQuantityMode ? "Please enable Max Quantity mode or set your ingredients before starting.".Loc() : ""));
                                            }
                                            else
                                            {
                                                DuoLog.Error("Something has gone wrong whilst another plugin tried to control Artisan. Disabling Endurance.".Loc());
                                            }
                                            // 這是排進 TaskManager 的工作，輪到時耐力模式可能已經被關掉。
                                            if (Enable)
                                                NotifyForcedStop("連續五次開不了製作");
                                            ToggleEndurance(false);
                                        }
                                    }
                                    else
                                    {
                                        FailedStarts.PushBack(Environment.TickCount64);
                                    }
                                }
                            });

                        }
                    }

                }
            }
        }

        private static void Toasts_ErrorToast(ref SeString message, ref bool isHandled)
        {
            if (Enable || (CraftingListUI.Processing && !CraftingListFunctions.Paused))
            {
                //foreach (uint errorId in UnableToCraftErrors)
                //{
                //    if (message.ExtractText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == errorId).Text.ExtractText())
                //    {
                //        Svc.Toasts.ShowError($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                //        DuoLog.Error($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                //        if (enable)
                //            ToggleEndurance(false);
                //        if (CraftingListUI.Processing)
                //            CraftingListFunctions.Paused = true;
                //        PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                //        P.TM.Abort();
                //        CraftingListFunctions.CLTM.Abort();
                //    }
                //}

                // 修理／精製被「製作狀態」拒絕是時機問題，兩邊都會等旗標清掉再按，不該把清單停掉。
                var repairPressed = RepairManager.RecentlyPressedRepair;
                if (repairPressed || Spiritbond.RecentlyPressedMaterialize)
                {
                    var text = LuminaText.Extract(message);
                    if (RepairManager.IsCraftStateRejectText(text))
                    {
                        Svc.Log.Information($"[Artisan][{(repairPressed ? "修理" : "精製")}] 遊戲拒絕了操作（{text}）：製作狀態還沒完全結束，稍後再按；不計入「十秒內連續五次錯誤」。");
                        return;
                    }
                }

                Errors.PushBack(Environment.TickCount64);
                Svc.Log.Warning($"Error Warnings [{Errors.Count(x => x > Environment.TickCount64 - 10 * 1000)}]: {message}");
                if (Errors.Count() >= 5 && Errors.All(x => x > Environment.TickCount64 - 10 * 1000))
                {
                    Svc.Toasts.ShowError(SharedText.CraftingModeChangedTooManyErrors.Loc(Enable ? "disabled".Loc() : "paused".Loc()));
                    DuoLog.Error(SharedText.CraftingModeChangedTooManyErrors.Loc(Enable ? "disabled".Loc() : "paused".Loc()));
                    NotifyForcedStop("十秒內連續五次錯誤");
                    if (enable)
                        ToggleEndurance(false);
                    if (CraftingListUI.Processing)
                        CraftingListFunctions.Paused = true;
                    Errors.Clear();
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                }
            }
        }

        // 別的外掛（例如 ICE）會在 Artisan 停下後重下指令，同一個錯誤可以一再重演 ⇒ 間隔內只響一次。
        private const long ErrorSoundMinIntervalMs = 30_000;

        // 只用 Interlocked 讀寫：Raphael 求解失敗那條是從背景工作排回來的，框架卸載時會就地在背景執行緒跑。
        private static long nextErrorSoundAt;

        /// <summary>製作被迫停下、需要人處理時響一聲（設定開著、而且距離上一次超過三十秒）。</summary>
        /// <remarks>任何執行緒都可以呼叫。「停下之前是不是真的在跑」由呼叫端判斷，這裡不讀製作狀態。</remarks>
        private static void PlayErrorSound()
        {
            // 外掛卸載後才輪到的委派會看到 P 是 null。
            if (P?.Config?.PlaySoundError != true)
                return;

            var now = Environment.TickCount64;
            var next = Interlocked.Read(ref nextErrorSoundAt);
            if (now < next)
            {
                Svc.Log.Information($"[Artisan] 製作因錯誤停下，但距離上一次錯誤提示音不到 {ErrorSoundMinIntervalMs / 1000} 秒，這次不響。");
                return;
            }

            // 兩處同時觸發時只有搶到的那一個響。
            if (Interlocked.CompareExchange(ref nextErrorSoundAt, now + ErrorSoundMinIntervalMs, next) != next)
                return;

            Svc.Log.Information("[Artisan] 製作因錯誤停下，播放錯誤提示音。");
            SoundPlayer.PlaySound();
        }

        /// <summary>製作被迫停下、需要人處理：錯誤提示音＋請 TataruPraise 說一句「需要幫忙」（reason 只寫進記錄檔）。</summary>
        /// <remarks>🔴 呼叫端自己確認「停下前真的在跑」，並排在停下的動作之前。</remarks>
        internal static void NotifyForcedStop(string reason)
        {
            PlayErrorSound();
            // IPC 在呼叫端執行緒上跑：框架卸載時就地在背景跑的委派、外掛卸載後才輪到的委派都不送。
            if (P is not null && Svc.Framework?.IsInFrameworkUpdateThread == true)
                TataruPraiseIPC.NotifyCraftNeedsHelp(reason);
        }

        /// <summary>
        /// 耐力模式開著、或清單在跑且沒暫停時才 <see cref="NotifyForcedStop"/>；給會同時停兩種模式、
        /// 或也可能在使用者手動製作時走到的停止點用。
        /// </summary>
        /// <remarks>🔴 必須排在停下的動作（關耐力、暫停清單）之前：它讀的是停下前的狀態。</remarks>
        internal static void NotifyForcedStopIfRunning(string reason)
        {
            // 清單已經暫停（例如使用者自己手動製作又碰到同一則錯誤）不是新的停止，不響。
            if (Enable || (CraftingListUI.Processing && !CraftingListFunctions.Paused))
                NotifyForcedStop(reason);
        }
    }
}
