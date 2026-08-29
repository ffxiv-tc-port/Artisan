using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.QuestSync;
using Artisan.RawInformation;
using Dalamud.Interface.Windowing;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using System;

namespace Artisan.UI
{
    internal class QuestHelper : Window
    {
        public QuestHelper() : base("Quest Helper###QuestHelper", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar)
        {
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
        }
        public override bool DrawConditions()
        {
            if (P.Config.HideQuestHelper || (!QuestList.HasIngredientsForAny() && !QuestList.IsOnSayQuest() && !QuestList.IsOnEmoteQuest()))
                return false;

            return true;
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

        public unsafe override void Draw()
        {
            bool hasIngredientsAny = QuestList.HasIngredientsForAny();
            if (hasIngredientsAny)
            {
                ImGui.Text("Quest Helper (click to open recipe)".Loc());
                foreach (var quest in QuestList.Quests)
                {
                    if (QuestList.IsOnQuest((ushort)quest.Key))
                    {
                        var hasIngredients = CraftingListFunctions.HasItemsForRecipe(QuestList.GetRecipeForQuest((ushort)quest.Key));
                        if (hasIngredients)
                        {
                            if (ImGui.Button($"{((ushort)quest.Key).NameOfQuest()}"))
                            {

                                if (Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
                                {
                                    var recipe = LuminaSheets.RecipeSheet[QuestList.GetRecipeForQuest((ushort)quest.Key)];
                                    PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(500)));
                                }
                            }
                        }
                    }

                }

            }
            bool isOnSayQuest = QuestList.IsOnSayQuest();
            if (isOnSayQuest)
            {
                ImGui.Text("Quest Helper (click to say)".Loc());
                foreach (var quest in QuestManager.Instance()->DailyQuests)
                {
                    string message = QuestList.GetSayQuestString(quest.QuestId);
                    if (message != "")
                    {
                        if (ImGui.Button("Say \"??\"".Loc(message)))
                        {
                            CommandProcessor.ExecuteThrottled($"/say {message}");
                        }
                    }
                }
            }
            bool isOnEmoteQuest = QuestList.IsOnEmoteQuest();
            if (isOnEmoteQuest)
            {
                ImGui.Text("Quest Helper (click to target and emote)".Loc());
                foreach (var quest in QuestManager.Instance()->DailyQuests)
                {
                    if (quest.IsCompleted) continue;

                    if (QuestList.EmoteQuests.TryGetValue(quest.QuestId, out var data))
                    {
                        if (ImGui.Button("Target ?? and do ??".Loc(LuminaSheets.ENPCResidentSheet[data.NPCDataId].Singular.ExtractText(), data.Emote)))
                        {
                            QuestList.DoEmoteQuest(quest.QuestId);
                        }
                    }

                    if (quest.QuestId == 2318)
                    {
                        {
                            if (QuestList.EmoteQuests.TryGetValue(9998, out var npc1))
                            {
                                if (ImGui.Button("Target ?? and do ??".Loc(LuminaSheets.ENPCResidentSheet[npc1.NPCDataId].Singular.ExtractText(), npc1.Emote)))
                                {
                                    QuestList.DoEmoteQuest(9998);
                                }
                            }

                            if (QuestList.EmoteQuests.TryGetValue(9999, out var npc2))
                            {
                                if (ImGui.Button("Target ?? and do ??".Loc(LuminaSheets.ENPCResidentSheet[npc2.NPCDataId].Singular.ExtractText(), npc2.Emote)))
                                {
                                    QuestList.DoEmoteQuest(9999);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
