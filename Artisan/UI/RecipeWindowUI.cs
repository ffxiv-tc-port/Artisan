using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.FCWorkshops;
using Artisan.GameInterop;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.UI;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using OtterGui;
using OtterGui.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static ECommons.GenericHelpers;

namespace Artisan
{
    internal class RecipeWindowUI : Window
    {
        private static string search = string.Empty;
        private static bool searched = false;
        internal static string Search
        {
            get => search;
            set
            {
                if (search != value)
                {
                    search = value;
                    searched = false;
                }
            }
        }
        public RecipeWindowUI() : base($"###RecipeWindow", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing)
        {
            this.Size = new Vector2(0, 0);
            this.Position = new Vector2(0, 0);
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            DisableWindowSounds = true;
            this.SizeConstraints = new WindowSizeConstraints()
            {
                MaximumSize = new Vector2(0, 0),
            };
        }

        public override void Draw()
        {
            if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]) return;

            if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting] || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft])
                DrawOptions();

            DrawSearchReplace();

            DrawEnduranceCounter();
            DrawCosmicEnduranceCounter();

            DrawWorkshopOverlay();

            DrawSupplyMissionOverlay();

            DrawMacroOptions();
            DrawCosmicWindowOptions();
        }

        private unsafe void DrawCosmicEnduranceCounter()
        {
            if (Endurance.RecipeID == 0)
                return;

            var recipeWindow = Svc.GameGui.GetAddonByName("WKSRecipeNotebook", 1);
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            // 守衛數字必須 >= 本區塊用到的最大索引 + 1(這裡用到 [6] 與 [24],所以是 25)。
            // 原本寫 >= 5:UldManager.NodeList 的長度恰為 NodeListCount,count 落在 5..24 時
            // NodeList[24] 讀到的是陣列尾端之外的堆積垃圾,再當成 AtkResNode* 解參考 →
            // 攔不到的 AccessViolation(corrupted-state exception,try/catch 無效)。
            // 另加 IsAddonReady:GetAddonByName 在 addon 還在建構時就會回傳指標。
            if (GenericHelpers.IsAddonReady(addonPtr) && addonPtr->UldManager.NodeListCount >= 25)
            {
                //var node = addonPtr->UldManager.NodeList[1]->GetAsAtkComponentNode()->Component->UldManager.NodeList[4];
                var node = addonPtr->UldManager.NodeList[6];
                var countNode = addonPtr->UldManager.NodeList[24];
                if (node == null || countNode == null)
                    return;

                var countTextNode = countNode->GetAsAtkTextNode();
                if (countTextNode == null)
                    return;

                var position = AtkResNodeFunctions.GetNodePosition(node);
                var scale = AtkResNodeFunctions.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);
                //position += ImGuiHelpers.MainViewport.Pos;
                var textHeight = ImGui.CalcTextSize("Craft X Times:");
                var countText = countTextNode->NodeText.ToString();
                var craftableCount = countText == "" ? 0 : Convert.ToInt32(countText.GetNumbers());

                if (craftableCount == 0) return;

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X - 300f.Scale(), position.Y + 10f.Scale()));

                //Svc.Log.Debug($"Length: {size.Length()}, Width: {node->Width}, Scale: {scale.Y}");

                DrawCounter(node, scale, craftableCount);
            }

        }

        private unsafe void DrawCosmicWindowOptions()
        {
            var recipeWindow = Svc.GameGui.GetAddonByName("WKSRecipeNotebook", 1);
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            var baseX = addonPtr->X;
            var baseY = addonPtr->Y;

            // 節點數只保證陣列長度，不保證元素非空：元素為 null 時解參考會直接 AVE（每影格路徑，安靜跳過）。
            if (addonPtr->UldManager.NodeListCount >= 2 && addonPtr->UldManager.NodeList != null
                && addonPtr->UldManager.NodeList[1] != null && addonPtr->UldManager.NodeList[1]->IsVisible())
            {
                var node = addonPtr->UldManager.NodeList[1];

                if (!node->IsVisible())
                    return;

                var position = AtkResNodeFunctions.GetNodePosition(node);
                var scale = AtkResNodeFunctions.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);

                ImGuiHelpers.ForceNextWindowMainViewport();
                if ((AtkResNodeFunctions.ResetPosition && position.X != 0) || P.Config.LockMiniMenuR)
                {
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.Always);
                    AtkResNodeFunctions.ResetPosition = false;
                }
                else
                {
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.FirstUseEver);
                }

                //Svc.Log.Debug($"{position.X + node->Width + 7}");
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(7f, 7f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                ImGui.Begin($"###CosmicOptions{node->NodeId}", ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.AlwaysUseWindowPadding);

                ImGui.Spacing();

                DrawCopyOfCraftMenu();
                if (SimpleTweaks.IsFocusTweakEnabled())
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, SharedText.AutoFocusRecipeSearchWarning.Loc());
                }
                if (Endurance.RecipeID != 0)
                {
                    var config = P.Config.RecipeConfigs.GetValueOrDefault(Endurance.RecipeID) ?? new();
                    if (config.Draw(Endurance.RecipeID))
                    {
                        Svc.Log.Debug($"Updating config for {Endurance.RecipeID}");
                        P.Config.RecipeConfigs[Endurance.RecipeID] = config;
                        P.Config.Save();
                    }
                }

                ImGui.End();
                ImGui.PopStyleVar(2);
            }
        }

        private unsafe void DrawSearchReplace()
        {
            if (TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon))
            {
                if (!addon->AtkUnitBase.IsVisible)
                {
                    Search = "";
                    return;
                }
                var searchNode = addon->AtkUnitBase.GetNodeById(26);
                var searchLabel = addon->AtkUnitBase.GetNodeById(25);
                if (searchNode == null || searchLabel == null) return;

                if (P.Config.ReplaceSearch)
                {
                    searchLabel->GetAsAtkTextNode()->SetText("Artisan Search".Loc());
                }
                else
                {
                    string searchText = Svc.Data.Excel.GetSheet<Addon>().GetRow(1412).Text.ExtractText();
                    searchLabel->GetAsAtkTextNode()->SetText(searchText);
                    return;
                }

                var textInput = (AtkComponentTextInput*)searchNode->GetComponent();
                Search = Marshal.PtrToStringAnsi(new IntPtr(textInput->AtkComponentInputBase.UnkText1.StringPtr)).Trim();
                var textSize = ImGui.CalcTextSize(Search);

                var position = AtkResNodeFunctions.GetNodePosition(searchNode);
                var scale = AtkResNodeFunctions.GetNodeScale(searchNode);
                var size = new Vector2(searchNode->Width, searchNode->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X, position.Y + size.Y));

                try
                {
                    var compNode = (AtkComponentNode*)searchNode;
                    if (compNode->Component->UldManager.SearchNodeById(18) == null) return;

                    searched = !compNode->Component->UldManager.SearchNodeById(18)->IsVisible();

                    if (Search.Length > 0 && !searched)
                    {
                        if (LuminaSheets.RecipeSheet.Values.Count(x => Regex.Match(x.ItemResult.Value.Name.ToDalamudString().ToString(), Search, RegexOptions.IgnoreCase).Success) > 0)
                        {
                            ImGui.Begin($"###Search{searchNode->NodeId}", ImGuiWindowFlags.NoScrollbar
                                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNavFocus
                                | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

                            ImGui.AlignTextToFramePadding();
                            ImGui.SetNextItemWidth(size.Length() - 12f);

                            int results = 0;
                            foreach (var recipe in LuminaSheets.RecipeSheet.Values.Where(x => Regex.Match(x.ItemResult.Value.Name.ToDalamudString().ToString(), Search, RegexOptions.IgnoreCase).Success))
                            {
                                if (results >= 24) continue;
                                var selected = ImGui.Selectable($"{recipe.ItemResult.Value.Name.ToDalamudString()} ({(Job)recipe.CraftType.RowId + 8})###{recipe.RowId}");
                                if (selected)
                                {
                                    var orid = Operations.GetSelectedRecipeEntry();
                                    if (orid == null || (orid != null && orid->RecipeId != recipe.RowId))
                                    {
                                        // 🔴 AgentRecipeNote.Instance() 合法回 null(產生器本體即
                                        //    agentModule == null ? null : ...);裸解參考 = AVE,
                                        //    corrupted-state,外面那圈 catch (Exception) 攔不到。
                                        // fail-closed:取不到就不開 —— 使用者再點一次即可。
                                        var recipeAgent = AgentRecipeNote.Instance();
                                        if (recipeAgent != null)
                                            recipeAgent->OpenRecipeByRecipeId(recipe.RowId);
                                    }

                                    searched = true;
                                }
                                results++;
                            }
                            ImGui.End();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is not RegexParseException)
                        ex.Log();
                }
            }
        }

        private unsafe void DrawSupplyMissionOverlay()
        {
            if (TryGetAddonByName<AddonGrandCompanySupplyList>("GrandCompanySupplyList", out var addon))
            {
                try
                {
                    var subcontext = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu").Address;
                    if (subcontext != null && subcontext->IsVisible)
                        return;

                    if (addon->SupplyRadioButton is null)
                        return;

                    // 🔴 只判空是半套：UldManager.NodeList 的長度恰為 NodeListCount，索引越界讀到的是
                    // 陣列後方的堆積垃圾（**不是 null**），再當 AtkResNode* 交給 IsVisible()
                    //（[MemberFunction] 原生呼叫）就是攔不到的 AVE —— 外面那個 try/catch 對
                    // corrupted-state exception 完全無效。原本這裡只驗了 != null，等於沒擋。
                    var radioUld = addon->SupplyRadioButton->UldManager;
                    if (radioUld.NodeList == null || radioUld.NodeListCount <= 1)
                        return;

                    var radioNode = radioUld.NodeList[1];
                    if (radioNode != null && radioNode->IsVisible())
                        return;

                    var timerWindow = Svc.GameGui.GetAddonByName("GrandCompanySupplyList");
                    if (timerWindow == IntPtr.Zero)
                        return;

                    var atkUnitBase = (AtkUnitBase*)timerWindow.Address;

                    // 同上，這裡用到 NodeList[19] ⇒ 要求 count >= 20；節點本身也要判空。
                    if (atkUnitBase == null || atkUnitBase->UldManager.NodeList == null
                        || atkUnitBase->UldManager.NodeListCount <= 19)
                        return;

                    var node = atkUnitBase->UldManager.NodeList[19];

                    if (node == null || !node->IsVisible())
                        return;

                    var position = AtkResNodeFunctions.GetNodePosition(node);
                    var scale = AtkResNodeFunctions.GetNodeScale(node);
                    var size = new Vector2(node->Width, node->Height) * scale;
                    var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);
                    var textSize = ImGui.CalcTextSize("Create Crafting List".Loc());

                    ImGuiHelpers.ForceNextWindowMainViewport();
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X, position.Y + (textSize.Y * scale.Y) + (14f * scale.Y)));

                    ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 2f * scale.Y));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3f * scale.X, 3f * scale.Y));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                    ImGui.Begin($"###SupplyTimerWindow", ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings
                        | ImGuiWindowFlags.AlwaysAutoResize);

                    if (ImGui.GetIO().KeyShift)
                    {
                        if (ImGui.Button("Create Crafting List (Star only)".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCListAgent(atkUnitBase, false, true);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button(SharedText.CreateListWithSubcraftsStarOnly.Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCListAgent(atkUnitBase, true, true);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                    }
                    else
                    {
                        if (ImGui.Button("Create Crafting List".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCListAgent(atkUnitBase, false, false);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Create Crafting List (with subcrafts)".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCListAgent(atkUnitBase, true, false);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                    }
                    ImGui.End();
                    ImGui.PopStyleVar(5);
                    ImGui.PopStyleColor();


                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }
            else
            {
                try
                {
                    var subcontext = (AtkUnitBase*)Svc.GameGui.GetAddonByName("AddonContextSub").Address;

                    if (subcontext != null && subcontext->IsVisible)
                        return;

                    subcontext = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu").Address;
                    if (subcontext != null && subcontext->IsVisible)
                        return;

                    var timerWindow = Svc.GameGui.GetAddonByName("ContentsInfoDetail");
                    if (timerWindow == IntPtr.Zero)
                        return;

                    var atkUnitBase = (AtkUnitBase*)timerWindow.Address;
                    if (atkUnitBase == null)
                        return;

                    // 🔴 AtkValues 與 NodeList 都是原生指標陣列，長度分別恰為 AtkValuesCount 與
                    // NodeListCount。越界讀到的是堆積垃圾不是 null ⇒ 判空擋不住，而 [233] 這一格
                    // 只是讀 Type 就已經是越界讀，[97] 更是要當節點指標交給 IsVisible() 原生呼叫。
                    // 外面那層 try/catch 對 AVE（corrupted-state exception）無效。
                    // 本區塊用到 AtkValues[233] 與 NodeList[97]。
                    if (atkUnitBase->AtkValues == null || atkUnitBase->AtkValuesCount <= 233)
                        return;

                    if (atkUnitBase->AtkValues[233].Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
                        return;

                    if (atkUnitBase->UldManager.NodeList == null || atkUnitBase->UldManager.NodeListCount <= 97)
                        return;

                    var node = atkUnitBase->UldManager.NodeList[97];

                    if (node == null || !node->IsVisible())
                        return;

                    var position = AtkResNodeFunctions.GetNodePosition(node);
                    var scale = AtkResNodeFunctions.GetNodeScale(node);
                    var size = new Vector2(node->Width, node->Height) * scale;
                    var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);

                    var textSize = ImGui.CalcTextSize("Create Crafting List".Loc());

                    ImGuiHelpers.ForceNextWindowMainViewport();
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X, position.Y - (textSize.Y * scale.Y) - (5f * scale.Y)));

                    ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 2f * scale.Y));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3f * scale.X, 3f * scale.Y));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                    ImGui.Begin($"###SupplyTimerWindow", ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings
                        | ImGuiWindowFlags.AlwaysAutoResize);

                    if (ImGui.GetIO().KeyShift)
                    {
                        if (ImGui.Button("Create Crafting List (Star only)".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCList(atkUnitBase, false, true);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                        var s = ImGui.GetItemRectSize();
                        ImGui.SameLine();
                        var oldScale = ImGui.GetIO().FontGlobalScale;
                        ImGui.GetIO().FontGlobalScale = 0.80f * scale.X;
                        using (var f = ImRaii.PushFont(ImGui.GetFont()))
                        {
                            if (ImGui.Button(SharedText.CreateListWithSubcraftsStarOnly.Loc(), new Vector2(size.X / 2, s.Y)))
                            {
                                CreateGCList(atkUnitBase, true, true);
                                P.PluginUi.IsOpen = true;
                                P.PluginUi.OpenWindow = OpenWindow.Lists;
                            }
                        }
                        ImGui.GetIO().FontGlobalScale = oldScale;
                    }
                    else
                    {
                        if (ImGui.Button("Create Crafting List".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCList(atkUnitBase, false, false);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Create Crafting List (with subcrafts)".Loc(), new Vector2(size.X / 2, 0)))
                        {
                            CreateGCList(atkUnitBase, true, false);
                            P.PluginUi.IsOpen = true;
                            P.PluginUi.OpenWindow = OpenWindow.Lists;
                        }
                    }

                    ImGui.End();
                    ImGui.PopStyleVar(5);
                    ImGui.PopStyleColor();


                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }
        }

        private static unsafe void CreateGCListAgent(AtkUnitBase* atkUnitBase, bool withSubcrafts, bool boostedCraftsOnly)
        {
            NewCraftingList craftingList = new NewCraftingList();
            craftingList.Name = $"GC Supply List ({DateTime.Now.ToShortDateString()})";

            // 🔴 AtkValues 的長度恰為 AtkValuesCount；越界讀到的是堆積垃圾不是 null ⇒ 判空擋不住。
            // 這個迴圈用到 [i]、[i-40]、[i-360]、[i-400]，衍生索引全都小於 i，所以驗 i 就夠。
            // 台服的佈局若與這組寫死索引對不上，失敗形式是「清單建不出來」而不是崩潰。
            var valueCount = atkUnitBase == null ? 0 : atkUnitBase->AtkValuesCount;
            if (atkUnitBase == null || atkUnitBase->AtkValues == null || valueCount <= 425)
            {
                Svc.Log.Information($"Artisan: GrandCompanySupplyList 的 AtkValues 只有 {valueCount} 格（需要 433），無法建立補給任務清單");
                return;
            }

            for (int i = 425; i <= 432; i++)
            {
                if (i >= valueCount)
                    break;

                if (atkUnitBase->AtkValues[i].Type == 0)
                    continue;

                var ItemId = atkUnitBase->AtkValues[i].Int;
                var requested = atkUnitBase->AtkValues[i - 40].Int;
                uint job = TextureIdToJob(atkUnitBase->AtkValues[i - 360].Int);
                bool starred = atkUnitBase->AtkValues[i - 400].Byte == 1;

                if (!boostedCraftsOnly || (boostedCraftsOnly && starred))
                {
                    if (LuminaSheets.RecipeSheet.Values.FindFirst(x => x.ItemResult.RowId == ItemId && x.CraftType.RowId + 8 == job, out var recipe))
                    {
                        var timesToAdd = requested / recipe.AmountResult;

                        if (withSubcrafts)
                            CraftingListUI.AddAllSubcrafts(recipe, craftingList, timesToAdd);

                        if (craftingList.Recipes.Any(x => x.ID == recipe.RowId))
                        {
                            craftingList.Recipes.First(x => x.ID == recipe.RowId).Quantity = timesToAdd;
                        }
                        else
                        {
                            craftingList.Recipes.Add(new() { Quantity = timesToAdd, ID = recipe.RowId });
                        }

                    }
                }
            }

            craftingList.SetID();
            craftingList.Save(true);

            Notify.Success("Crafting List Created".Loc());
        }

        private static uint TextureIdToJob(int textureId)
        {
            return textureId switch
            {
                62008 => 8,
                62009 => 9,
                62010 => 10,
                62011 => 11,
                62012 => 12,
                62013 => 13,
                62014 => 14,
                62015 => 15,
                _ => 0
            };
        }

        private static unsafe void CreateGCList(AtkUnitBase* atkUnitBase, bool withSubcrafts, bool boostedCraftOnly)
        {
            NewCraftingList craftingList = new NewCraftingList();
            craftingList.Name = $"GC Supply List ({DateTime.Now.ToShortDateString()})";

            // 同 CreateGCListAgent。這裡衍生索引是 [i+16]／[i+8]／[i+40]，最大是 i+40，
            // 所以每一圈驗的是 i + 40 而不是 i —— 只驗 i 會漏掉後面三個。
            var valueCount = atkUnitBase == null ? 0 : atkUnitBase->AtkValuesCount;
            if (atkUnitBase == null || atkUnitBase->AtkValues == null || valueCount <= 273)
            {
                Svc.Log.Information($"Artisan: ContentsInfoDetail 的 AtkValues 只有 {valueCount} 格（需要 281），無法建立補給任務清單");
                return;
            }

            for (int i = 233; i <= 240; i++)
            {
                if (i + 40 >= valueCount)
                    break;

                if (atkUnitBase->AtkValues[i].Type == 0)
                    continue;

                var ItemId = atkUnitBase->AtkValues[i].Int;
                var requested = atkUnitBase->AtkValues[i + 16].Int;
                uint job = TextureIdToJob(atkUnitBase->AtkValues[i + 8].Int);
                bool starred = atkUnitBase->AtkValues[i + 40].Byte == 1;

                if (!boostedCraftOnly || (boostedCraftOnly && starred))
                {
                    if (LuminaSheets.RecipeSheet.Values.FindFirst(x => x.ItemResult.RowId == ItemId && x.CraftType.RowId + 8 == job, out var recipe))
                    {
                        var timesToAdd = requested / recipe.AmountResult;

                        if (withSubcrafts)
                            CraftingListUI.AddAllSubcrafts(recipe, craftingList, timesToAdd);

                        if (craftingList.Recipes.Any(x => x.ID == recipe.RowId))
                        {
                            craftingList.Recipes.First(x => x.ID == recipe.RowId).Quantity = timesToAdd;
                        }
                        else
                        {
                            craftingList.Recipes.Add(new() { Quantity = timesToAdd, ID = recipe.RowId });
                        }
                    }
                }
            }

            craftingList.SetID();
            craftingList.Save(true);

            Notify.Success("Crafting List Created".Loc());
        }

        /// <summary>
        /// 取出 SubmarinePartsMenu 的指定文字節點。
        /// 🔴 上界（NodeListCount）只是三層裡的第一層：NodeList 元素本身可為 null，
        /// 而 GetAsAtkTextNode() 在節點型別不是文字節點時也回 null。
        /// 三層任一沒過就回 null，呼叫端 fail-closed 不做事，不要拿去解參考 NodeText。
        /// </summary>
        private static unsafe AtkTextNode* GetWorkshopTextNode(AtkUnitBase* addonPtr, uint index)
        {
            if (addonPtr == null || addonPtr->UldManager.NodeList == null || addonPtr->UldManager.NodeListCount <= index)
                return null;

            var node = addonPtr->UldManager.NodeList[index];
            if (node == null)
                return null;

            return node->GetAsAtkTextNode();
        }

        /// <summary>
        /// 由 SubmarinePartsMenu 的文字節點推出目前階段並建立對應的製作清單。
        /// 兩顆按鈕（含／不含前置素材）除了 withPrecrafts 以外邏輯完全相同，收斂在此。
        /// </summary>
        private static unsafe void CreateWorkshopPhaseList(AtkUnitBase* addonPtr, bool withPrecrafts)
        {
            var itemNameNode = GetWorkshopTextNode(addonPtr, 37);
            var phaseProgress = GetWorkshopTextNode(addonPtr, 26);

            if (itemNameNode == null || phaseProgress == null)
            {
                // 使用者按下按鈕才會走到這裡（不是每影格路徑），安靜失敗會讓人以為清單已經建好了。
                Svc.Log.Information("Artisan: SubmarinePartsMenu 的成品名或階段進度文字節點取不到，未建立工坊清單。");
                return;
            }

            var itemName = itemNameNode->NodeText.ExtractText();

            if (!LuminaSheets.WorkshopSequenceSheet.Values.Any(x => x.ResultItem.Value.Name.ExtractText() == itemName))
                return;

            var project = LuminaSheets.WorkshopSequenceSheet.Values.First(x => x.ResultItem.Value.Name.ExtractText() == itemName);
            var phaseNum = Convert.ToInt32(phaseProgress->NodeText.ToString().First().ToString());

            if (project.CompanyCraftPart.Count(x => x.RowId > 0) == 1)
            {
                var part = project.CompanyCraftPart.First(x => x.RowId > 0).Value;
                var phase = part.CompanyCraftProcess[phaseNum - 1];

                FCWorkshopUI.CreatePhaseList(phase.Value!, part.CompanyCraftType.Value.Name.ExtractText(), phaseNum, withPrecrafts, null, project);
                Notify.Success("FC Workshop List Created".Loc());
            }
            else
            {
                var currentPartNode = GetWorkshopTextNode(addonPtr, 28);
                if (currentPartNode == null)
                {
                    Svc.Log.Information("Artisan: SubmarinePartsMenu 的目前部件文字節點取不到，未建立工坊清單。");
                    return;
                }

                string partStep = currentPartNode->NodeText.ExtractText().Split(":").Last();

                if (project.CompanyCraftPart.Any(x => x.Value.CompanyCraftType.Value.Name.ExtractText() == partStep))
                {
                    var part = project.CompanyCraftPart.First(x => x.Value.CompanyCraftType.Value.Name.ExtractText() == partStep).Value;
                    var phase = part.CompanyCraftProcess[phaseNum - 1];

                    FCWorkshopUI.CreatePhaseList(phase.Value!, part.CompanyCraftType.Value.Name.ExtractText(), phaseNum, withPrecrafts, null, project);
                    Notify.Success("FC Workshop List Created".Loc());
                }
            }
        }

        private unsafe void DrawWorkshopOverlay()
        {
            try
            {
                var subWindow = Svc.GameGui.GetAddonByName("SubmarinePartsMenu", 1);
                if (subWindow == IntPtr.Zero)
                    return;

                var addonPtr = (AtkUnitBase*)subWindow.Address;
                if (addonPtr == null)
                    return;

                if (addonPtr->UldManager.NodeList == null || addonPtr->UldManager.NodeListCount < 38)
                    return;

                var node = addonPtr->UldManager.NodeList[2];

                if (node == null || !node->IsVisible())
                    return;

                var position = AtkResNodeFunctions.GetNodePosition(node);
                var scale = AtkResNodeFunctions.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);
                var textSize = ImGui.CalcTextSize("Create crafting list for this phase".Loc());

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + (4f * scale.X), position.Y + size.Y - textSize.Y - (34f * scale.Y)));

                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
                float oldSize = ImGui.GetFont().Scale;
                ImGui.GetFont().Scale *= scale.X;
                ImGui.PushFont(ImGui.GetFont());
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 5f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3f, 3f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin($"###WorkshopButton{node->NodeId}", ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);


                if (ImGui.Button("Create crafting list for this phase".Loc()))
                {
                    CreateWorkshopPhaseList(addonPtr, false);
                }

                if (ImGui.Button("Create crafting list for this phase (including precrafts)".Loc()))
                {
                    CreateWorkshopPhaseList(addonPtr, true);
                }

                ImGui.End();
                ImGui.PopStyleVar(5);
                ImGui.GetFont().Scale = oldSize;
                ImGui.PopFont();
                ImGui.PopStyleColor();

            }
            catch { }
        }

        public override void PreDraw()
        {
            if (!P.Config.DisableTheme)
            {
                P.Style.Push();
                P.StylePushed = true;
            }
            // Dalamud 的 Window 基底類別在 PreDraw() 裡推每視窗不透明度(標題列右鍵選單的
            // 「不透明度」滑桿)。這個 override 原本沒有呼叫 base，等於把那個內建功能對本
            // 視窗靜默關掉了一半(ApplyConditionals 讀得到 internalAlpha 所以背景會變，
            // 但內容不會)。
            // 🔴 base 必須放在 P.Style.Push() **之後**:StyleModel.Push() 自己會推一個
            // 絕對值的 ImGuiStyleVar.Alpha(Dalamud/Interface/Style/StyleModelV1.cs:263)，
            // 先呼叫 base 再 Push 的話 base 推的不透明度會被主題的 Alpha 直接蓋掉。
            base.PreDraw();
        }

        public override void PostDraw()
        {
            // 後進先出:base 在 PreDraw 的最後才推，所以這裡要最先 pop。
            base.PostDraw();
            if (P.StylePushed)
            {
                P.Style.Pop();
                P.StylePushed = false;
            }
        }


        public unsafe static void DrawOptions()
        {
            var recipeWindow = Svc.GameGui.GetAddonByName("RecipeNote", 1);
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            var baseX = addonPtr->X;
            var baseY = addonPtr->Y;

            if (addonPtr->UldManager.NodeListCount > 1)
            {
                // 節點數只保證陣列長度，不保證元素非空：元素為 null 時解參考會直接 AVE（每影格路徑，安靜跳過）。
                if (addonPtr->UldManager.NodeList != null && addonPtr->UldManager.NodeList[1] != null
                    && addonPtr->UldManager.NodeList[1]->IsVisible())
                {
                    var node = addonPtr->UldManager.NodeList[1];

                    if (!node->IsVisible())
                        return;

                    if (P.Config.LockMiniMenuR)
                    {
                        var position = AtkResNodeFunctions.GetNodePosition(node);
                        var scale = AtkResNodeFunctions.GetNodeScale(node);
                        var size = new Vector2(node->Width, node->Height) * scale;
                        var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);
                        //position += ImGuiHelpers.MainViewport.Pos;

                        ImGuiHelpers.ForceNextWindowMainViewport();

                        if ((AtkResNodeFunctions.ResetPosition && position.X != 0) || P.Config.LockMiniMenuR)
                        {
                            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.Always);
                            AtkResNodeFunctions.ResetPosition = false;
                        }
                        else
                        {
                            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.FirstUseEver);
                        }
                    }

                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(7f, 7f));
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                    var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.AlwaysUseWindowPadding;
                    if (P.Config.PinMiniMenu)
                        flags |= ImGuiWindowFlags.NoMove;

                    ImGui.Begin($"###Options{node->NodeId}", flags);


                    DrawCopyOfCraftMenu();

                    ImGui.End();
                    ImGui.PopStyleVar(2);
                }
            }

        }

        private static void DrawCopyOfCraftMenu()
        {
            // ECommons dropped AddHeaderIcon in favor of Window.TitleBarButton, but this
            // window uses NoTitleBar so the native title bar button row never renders;
            // draw an equivalent inline icon button instead.
            if (ImGuiEx.IconButton(FontAwesomeIcon.Cog, "OpenConfig"))
            {
                P.PluginUi.IsOpen = true;
            }
            ImGuiEx.Tooltip("Open Config".Loc());

            bool autoMode = P.Config.AutoMode;

            if (ImGui.Checkbox("Automatic Action Execution Mode".Loc(), ref autoMode))
            {
                P.Config.AutoMode = autoMode;
                P.Config.Save();
            }
            bool enable = Endurance.Enable;

            if (!CraftingListFunctions.HasItemsForRecipe(Endurance.RecipeID) && !Endurance.Enable)
                ImGui.BeginDisabled();

            if (ImGui.Checkbox("Endurance Mode Toggle".Loc(), ref enable))
            {
                Endurance.ToggleEndurance(enable);
            }

            if (!CraftingListFunctions.HasItemsForRecipe(Endurance.RecipeID) && !Endurance.Enable)
            {
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    var recipe = LuminaSheets.RecipeSheet!.First(x => x.Key == Endurance.RecipeID).Value;
                    ImGui.BeginTooltip();
                    ImGui.Text("You cannot start Endurance as you do not possess ingredients to craft this recipe.\nMissing: ??".Loc(string.Join(", ", PreCrafting.MissingIngredients(recipe))));
                    ImGui.EndTooltip();
                }
            }
        }

        public unsafe static void DrawMacroOptions()
        {
            var recipeWindow = Svc.GameGui.GetAddonByName("RecipeNote", 1);
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            var baseX = addonPtr->X;
            var baseY = addonPtr->Y;

            // 節點數只保證陣列長度，不保證元素非空：元素為 null 時解參考會直接 AVE（每影格路徑，安靜跳過）。
            if (addonPtr->UldManager.NodeListCount >= 2 && addonPtr->UldManager.NodeList != null
                && addonPtr->UldManager.NodeList[1] != null && addonPtr->UldManager.NodeList[1]->IsVisible())
            {
                var node = addonPtr->UldManager.NodeList[1];

                if (!node->IsVisible())
                    return;

                var position = AtkResNodeFunctions.GetNodePosition(node);
                var scale = AtkResNodeFunctions.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);

                ImGuiHelpers.ForceNextWindowMainViewport();
                if ((AtkResNodeFunctions.ResetPosition && position.X != 0) || P.Config.LockMiniMenuR)
                {
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.FirstUseEver);
                    AtkResNodeFunctions.ResetPosition = false;
                }
                else
                {
                    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + size.X + 7, position.Y + 7), ImGuiCond.FirstUseEver);
                }

                //Svc.Log.Debug($"{position.X + node->Width + 7}");
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(7f, 7f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
                ImGui.Begin($"###Options{node->NodeId}", ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.AlwaysUseWindowPadding);

                ImGui.Spacing();

                if (SimpleTweaks.IsFocusTweakEnabled())
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, SharedText.AutoFocusRecipeSearchWarning.Loc());
                }
                if (Endurance.RecipeID != 0)
                {
                    var config = P.Config.RecipeConfigs.GetValueOrDefault(Endurance.RecipeID) ?? new();
                    if (config.Draw(Endurance.RecipeID))
                    {
                        P.Config.RecipeConfigs[Endurance.RecipeID] = config;
                        P.Config.Save();
                    }
                }

                ImGui.End();
                ImGui.PopStyleVar(2);
            }
        }

        

        internal static unsafe void DrawEnduranceCounter()
        {
            if (Endurance.RecipeID == 0)
                return;

            var recipeWindow = Svc.GameGui.GetAddonByName("RecipeNote", 1);
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null)
                return;

            // 守衛數字必須 >= 本區塊用到的最大索引 + 1(這裡用到 [8] 與 [35],所以是 36)。
            // 原本寫 >= 5,同上一處的 bug class:count 落在 5..35 時 NodeList[35] 會讀到
            // 陣列尾端之外約 240 bytes 的堆積垃圾,當成 AtkResNode* 解參考 → 攔不到的 AVE。
            // 對照:本檔 :574 的 `NodeListCount < 38` 配 NodeList[37] 才是正確寫法。
            if (GenericHelpers.IsAddonReady(addonPtr) && addonPtr->UldManager.NodeListCount >= 36)
            {
                //var node = addonPtr->UldManager.NodeList[1]->GetAsAtkComponentNode()->Component->UldManager.NodeList[4];
                var node = addonPtr->UldManager.NodeList[8];
                var countNode = addonPtr->UldManager.NodeList[35];
                if (node == null || countNode == null)
                    return;

                var countTextNode = countNode->GetAsAtkTextNode();
                if (countTextNode == null)
                    return;

                var position = AtkResNodeFunctions.GetNodePosition(node);
                var scale = AtkResNodeFunctions.GetNodeScale(node);
                var size = new Vector2(node->Width, node->Height) * scale;
                var center = new Vector2((position.X + size.X) / 2, (position.Y - size.Y) / 2);
                //position += ImGuiHelpers.MainViewport.Pos;
                var textHeight = ImGui.CalcTextSize("Craft X Times:".Loc());
                var countText = countTextNode->NodeText.ToString();
                var craftableCount = countText == "" ? 0 : Convert.ToInt32(countText.GetNumbers());

                if (craftableCount == 0) return;

                ImGuiHelpers.ForceNextWindowMainViewport();
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + (4f * scale.X) - 40f, position.Y - 16f - (17f * scale.Y)));

                //Svc.Log.Debug($"Length: {size.Length()}, Width: {node->Width}, Scale: {scale.Y}");

                DrawCounter(node, scale, craftableCount);
            }
        }

        private static unsafe void DrawCounter(AtkResNode* node, Vector2 scale, int craftableCount)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5f, 2.5f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3f, 3f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            ImGui.Begin($"###Repeat{node->NodeId}", ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNavFocus
                | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

            var oldScale = ImGui.GetIO().FontGlobalScale;
            ImGui.GetIO().FontGlobalScale = 1f * scale.X;
            using (var font = ImRaii.PushFont(ImGui.GetFont()))
            {
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Craft X Times:".Loc());
                ImGui.SameLine();
                ImGui.PushItemWidth(110f * scale.X);
                if (ImGui.InputInt($"###TimesRepeat{node->NodeId}", ref P.Config.CraftX))
                {
                    if (P.Config.CraftX < 0)
                        P.Config.CraftX = 0;

                    if (P.Config.CraftX > craftableCount)
                        P.Config.CraftX = craftableCount;

                }
                ImGui.SameLine();
                if (P.Config.CraftX > 0)
                {
                    if (ImGui.Button("Craft ??".Loc(P.Config.CraftX)))
                    {
                        P.Config.CraftingX = true;
                        Endurance.ToggleEndurance(true);
                    }
                }
                else
                {
                    if (ImGui.Button("Craft All (??)".Loc(craftableCount)))
                    {
                        P.Config.CraftX = craftableCount;
                        P.Config.CraftingX = true;
                        Endurance.ToggleEndurance(true);
                    }
                }

                ImGui.GetIO().FontGlobalScale = oldScale;
            }

            ImGui.End();
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor();
        }
    }
}
