using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.FCWorkshops;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using PunishLib.ImGuiMethods;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ThreadLoadImageHandler = ECommons.ImGuiMethods.ThreadLoadImageHandler;

namespace Artisan.UI
{
    unsafe internal class PluginUI : Window
    {
        public event EventHandler<bool>? CraftingWindowStateChanged;


        private bool visible = false;
        public OpenWindow OpenWindow { get; set; }

        public bool Visible
        {
            get { return this.visible; }
            set { this.visible = value; }
        }

        private bool settingsVisible = false;
        public bool SettingsVisible
        {
            get { return this.settingsVisible; }
            set { this.settingsVisible = value; }
        }

        private bool craftingVisible = false;
        public bool CraftingVisible
        {
            get { return this.craftingVisible; }
            set { if (this.craftingVisible != value) CraftingWindowStateChanged?.Invoke(this, value); this.craftingVisible = value; }
        }

        public PluginUI() : base($"{P.Name} {P.GetType().Assembly.GetName().Version}###Artisan")
        {
            this.RespectCloseHotkey = false;
            this.SizeConstraints = new()
            {
                MinimumSize = new(250, 100),
                MaximumSize = new(9999, 9999)
            };
            P.ws.AddWindow(this);
        }

        public override void PreDraw()
        {
            if (!P.Config.DisableTheme)
            {
                P.Style.Push();
                P.StylePushed = true;
            }

        }

        public override void PostDraw()
        {
            if (P.StylePushed)
            {
                P.Style.Pop();
                P.StylePushed = false;
            }
        }

        public void Dispose()
        {

        }

        public override void Draw()
        {
            if (DalamudInfo.IsOnStaging())
            {
                var scale = ImGui.GetIO().FontGlobalScale;
                ImGui.GetIO().FontGlobalScale = scale * 1.5f;
                using (var f = ImRaii.PushFont(ImGui.GetFont()))
                {
                    ImGuiEx.TextWrapped("Listen buddy, you're on Dalamud staging, there's every chance any problems you might encounter is specific to Dalamud's testing and not Artisan. I don't make this plugin to work on staging, so don't expect any fixes unless the problem makes it to Dalamud release.".Loc());
                    ImGui.Separator();

                    ImGui.Spacing();
                    ImGui.GetIO().FontGlobalScale = scale;
                }

            }
            var region = ImGui.GetContentRegionAvail();
            var itemSpacing = ImGui.GetStyle().ItemSpacing;

            var topLeftSideHeight = region.Y;

            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(5f.Scale(), 0));
            try
            {
                ShowEnduranceMessage();

                using (var table = ImRaii.Table($"ArtisanTableContainer", 2, ImGuiTableFlags.Resizable))
                {
                    if (!table)
                        return;

                    ImGui.TableSetupColumn("##LeftColumn", ImGuiTableColumnFlags.WidthFixed, ImGui.GetWindowWidth() / 2);

                    ImGui.TableNextColumn();

                    var regionSize = ImGui.GetContentRegionAvail();

                    ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
                    using (var leftChild = ImRaii.Child($"###ArtisanLeftSide", regionSize with { Y = topLeftSideHeight }, false, ImGuiWindowFlags.NoDecoration))
                    {
                        var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/artisan-icon.png");

                        if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var logo))
                        {
                            ImGuiEx.LineCentered("###ArtisanLogo", () =>
                            {
                                ImGui.Image(logo.Handle, new(100f.Scale(), 100f.Scale()));
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.BeginTooltip();
                                    ImGui.Text("You are the 69th person to find this secret. Nice!".Loc());
                                    ImGui.EndTooltip();
                                }
                            });

                        }
                        ImGui.Spacing();
                        ImGui.Separator();

                        if (ImGui.Selectable("Overview".Loc(), OpenWindow == OpenWindow.Overview))
                        {
                            OpenWindow = OpenWindow.Overview;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Settings".Loc(), OpenWindow == OpenWindow.Main))
                        {
                            OpenWindow = OpenWindow.Main;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Endurance".Loc(), OpenWindow == OpenWindow.Endurance))
                        {
                            OpenWindow = OpenWindow.Endurance;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Macros".Loc(), OpenWindow == OpenWindow.Macro))
                        {
                            OpenWindow = OpenWindow.Macro;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Raphael Cache".Loc(), OpenWindow == OpenWindow.RaphaelCache))
                        {
                            OpenWindow = OpenWindow.RaphaelCache;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Recipe Assigner".Loc(), OpenWindow == OpenWindow.Assigner))
                        {
                            OpenWindow = OpenWindow.Assigner;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Crafting Lists".Loc(), OpenWindow == OpenWindow.Lists))
                        {
                            OpenWindow = OpenWindow.Lists;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("List Builder".Loc(), OpenWindow == OpenWindow.SpecialList))
                        {
                            OpenWindow = OpenWindow.SpecialList;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("FC Workshops".Loc(), OpenWindow == OpenWindow.FCWorkshop))
                        {
                            OpenWindow = OpenWindow.FCWorkshop;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Simulator".Loc(), OpenWindow == OpenWindow.Simulator))
                        {
                            OpenWindow = OpenWindow.Simulator;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("About".Loc(), OpenWindow == OpenWindow.About))
                        {
                            OpenWindow = OpenWindow.About;
                        }


#if DEBUG
                        ImGui.Spacing();
                        if (ImGui.Selectable("DEBUG", OpenWindow == OpenWindow.Debug))
                        {
                            OpenWindow = OpenWindow.Debug;
                        }
                        ImGui.Spacing();
#endif

                    }

                    ImGui.PopStyleVar();
                    ImGui.TableNextColumn();
                    using (var rightChild = ImRaii.Child($"###ArtisanRightSide", Vector2.Zero, false))
                    {
                        switch (OpenWindow)
                        {
                            case OpenWindow.Main:
                                DrawMainWindow();
                                break;
                            case OpenWindow.Endurance:
                                Endurance.Draw();
                                break;
                            case OpenWindow.Lists:
                                CraftingListUI.Draw();
                                break;
                            case OpenWindow.About:
                                AboutTab.Draw("Artisan");
                                break;
                            case OpenWindow.Debug:
                                DebugTab.Draw();
                                break;
                            case OpenWindow.Macro:
                                MacroUI.Draw();
                                break;
                            case OpenWindow.RaphaelCache:
                                RaphaelCacheUI.Draw();
                                break;
                            case OpenWindow.Assigner:
                                AssignerUI.Draw();
                                break;
                            case OpenWindow.FCWorkshop:
                                FCWorkshopUI.Draw();
                                break;
                            case OpenWindow.SpecialList:
                                SpecialLists.Draw();
                                break;
                            case OpenWindow.Overview:
                                DrawOverview();
                                break;
                            case OpenWindow.Simulator:
                                SimulatorUI.Draw();
                                break;
                            case OpenWindow.None:
                                break;
                            default:
                                break;
                        }
                        ;
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
            ImGui.PopStyleVar();
        }

        private void DrawOverview()
        {
            var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/artisan.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var logo))
            {
                ImGuiEx.LineCentered("###ArtisanTextLogo", () =>
                {
                    ImGui.Image(logo.Handle, new Vector2(logo.Width, 100f.Scale()));
                });
            }

            ImGuiEx.LineCentered("###ArtisanOverview", () =>
            {
                ImGuiEx.TextUnderlined("Artisan - Overview".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("I would first like to thank you for downloading my little crafting plugin. I have been working on Artisan consistently since June 2022 and it's my magnum opus of a plugin.".Loc());
            ImGuiEx.TextWrapped("It is free and you should not be paying anyone for it.".Loc());
            ImGui.Spacing();
            ImGuiEx.TextWrapped("Before you get started with Artisan, we should go over a few things about how the plugin works. Artisan is simple to use once you understand a few key factors.".Loc());

            ImGui.Spacing();
            ImGuiEx.LineCentered("###ArtisanModes", () =>
            {
                ImGuiEx.TextUnderlined("Crafting Modes".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped(("Artisan features an \"Automatic Action Execution Mode\" which merely takes the suggestions provided to it and performs the action on your behalf." +
                                " By default, this will fire as fast as the game allows, which is faster than normal macros." +
                                " You are not bypassing any sort of game restrictions doing this, however you can set a delay should you choose to." +
                                " Enabling this has nothing to do with the suggestion making process Artisan uses by default.").Loc());

            var automode = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/AutoMode.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(automode, out var example))
            {
                ImGuiEx.LineCentered("###AutoModeExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGuiEx.TextWrapped(("If you do not have the automatic mode enabled, you will have access to 2 more modes. \"Semi-Manual Mode\" and \"Full Manual\"." +
                                " \"Semi-Manual Mode\" will appear in a small pop-up window when you start crafting.").Loc());

            var craftWindowExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/ThemeCraftingWindowExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(craftWindowExample, out example))
            {
                ImGuiEx.LineCentered("###CraftWindowExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGuiEx.TextWrapped(("By clicking the \"Execute recommended action\" button, you are instructing the plugin to perform the suggestion it has recommended." +
                " This considered semi-manual as you still have to click each action, but you don't have to worry about finding them on your hotbars." +
                " \"Full-Manual\" mode is performed by pressing the buttons on your hotbar as normal." +
                " You are provided with an aid by default as Artisan will highlight the action on your hotbar if it is slotted. (This can be disabled in the settings)").Loc());

            var outlineExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/OutlineExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(outlineExample, out example))
            {
                ImGuiEx.LineCentered("###OutlineExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###ArtisanSuggestions", () =>
            {
                ImGuiEx.TextUnderlined("Solvers/Macros".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped(("Artisan by default will provide you with suggestions on what your next crafting step should be. This solver is not perfect however and it is definitely not a substitute for having appropriate gear. " +
                "You do not need to do anything to enable this behaviour other than have Artisan enabled. " +
                "\n\n" +
                "If you are trying to tackle a craft that the default solver cannot craft, Artisan allows you to build macros which can be used as the suggestions instead of the default solver. " +
                "Artisan macros have the benefit of not being restricted in length, can fire off as fast as the game allows and also allows some additional options to tweak on the fly.").Loc());

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("Click here to be taken to the Macro menu.".Loc());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Macro;
            }
            ImGui.Spacing();
            ImGuiEx.TextWrapped("Once you have created a macro, you will have to assign it to a recipe. This is easily accomplished by using the Recipe Window dropdown. By default, this is attached to the top right of the in-game crafting log window but can be unattached in the settings.".Loc());


            var recipeWindowExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/RecipeWindowExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(recipeWindowExample, out example))
            {
                ImGuiEx.LineCentered("###RecipeWindowExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }


            ImGuiEx.TextWrapped(("Select a macro you have created from the dropdown box. " +
                "When you go to craft this item, the suggestions will be replaced by the contents of your macro.").Loc());


            ImGui.Spacing();
            ImGuiEx.LineCentered("###Endurance", () =>
            {
                ImGuiEx.TextUnderlined("Endurance".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped(("Artisan has a mode titled \"Endurance Mode\" which is basically a fancier way of saying \"Auto-repeat mode\" which will continually try to craft the same item for you. " +
                "Endurance Mode works by selecting a recipe from the in-game crafting log and enabling the feature. " +
                "Your character will then attempt to keep crafting that item as many times as you have materials for it. " +
                "\n\n" +
                "The other features should hopefully be self-explanatory as Endurance Mode can also manage the usage of your food, potions, manuals, repairs and materia extraction between crafts. " +
                "The repair feature only supports repairing with dark matter and does not support repair NPCs.").Loc());

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("Click here to be taken to the Endurance menu.".Loc());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Endurance;
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###Lists", () =>
            {
                ImGuiEx.TextUnderlined("Crafting Lists".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped(("Artisan also has the ability to create a list of items and have it start crafting each of them, one after another, automatically. " +
                "Crafting lists have a lot of powerful tools to streamline the process of going from materials to final products. " +
                "It also supports importing and exporting to Teamcraft.").Loc());

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("Click here to be taken to the Crafting List menu.".Loc());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Lists;
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###Questions", () =>
            {
                ImGuiEx.TextUnderlined("Got Questions?".Loc());
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("If you have questions about things not outlined here, you can drop a question in our".Loc());
            ImGui.SameLine(ImGui.GetCursorPosX(), 1.5f);
            ImGuiEx.TextUnderlined("Discord server.".Loc());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsItemClicked())
                {
                    Util.OpenLink("https://discord.gg/Zzrcc8kmvy");
                }
            }

            ImGuiEx.TextWrapped("You can also raise issues on our".Loc());
            ImGui.SameLine(ImGui.GetCursorPosX(), 2f);
            ImGuiEx.TextUnderlined("Github page.".Loc());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (ImGui.IsItemClicked())
                {
                    Util.OpenLink("https://github.com/PunishXIV/Artisan");
                }
            }

        }

        public static void DrawMainWindow()
        {
            ImGui.TextWrapped("Here you can change some settings Artisan will use. Some of these can also be toggled during a craft.".Loc());
            ImGui.TextWrapped("In order to use Artisan's manual highlight, please slot every crafting action you have unlocked to a visible hotbar.".Loc());
            bool autoEnabled = P.Config.AutoMode;
            bool delayRec = P.Config.DelayRecommendation;
            bool failureCheck = P.Config.DisableFailurePrediction;
            int maxQuality = P.Config.MaxPercentage;
            bool useTricksGood = P.Config.UseTricksGood;
            bool useTricksExcellent = P.Config.UseTricksExcellent;
            bool useSpecialist = P.Config.UseSpecialist;
            //bool showEHQ = P.Config.ShowEHQ;
            //bool useSimulated = P.Config.UseSimulatedStartingQuality;
            bool disableGlow = P.Config.DisableHighlightedAction;
            bool disableToasts = P.Config.DisableToasts;

            ImGui.Separator();

            if (ImGui.CollapsingHeader("General Settings".Loc()))
            {
                if (ImGui.Checkbox("Automatic Action Execution Mode".Loc(), ref autoEnabled))
                {
                    P.Config.AutoMode = autoEnabled;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("Automatically use each recommended action.".Loc());
                if (autoEnabled)
                {
                    if (ImGui.Checkbox("Replicate Macro Delay".Loc(), ref P.Config.ReplicateMacroDelay))
                    {
                        P.Config.Save();
                    }

                    if (!P.Config.ReplicateMacroDelay)
                    {
                        var delay = P.Config.AutoDelay;
                        ImGui.PushItemWidth(200);
                        if (ImGui.SliderInt("Execution Delay (ms)".Loc() + "###ActionDelay", ref delay, 0, 1000))
                        {
                            if (delay < 0) delay = 0;
                            if (delay > 1000) delay = 1000;

                            P.Config.AutoDelay = delay;
                        }
                        // 0～1000 的滑桿快速拖過去，每畫格都會跨過好幾個整數而回傳 true，
                        // 存檔留在區塊內等於以幀率同步寫磁碟。數值照樣即時套用，只把存檔延到放手時。
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            P.Config.Save();
                    }
                }

                bool requireFoodPot = P.Config.AbortIfNoFoodPot;
                if (ImGui.Checkbox("Enforce Consumables".Loc(), ref requireFoodPot))
                {
                    P.Config.AbortIfNoFoodPot = requireFoodPot;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("Artisan will require the configured food, manuals or medicine and refuse to craft if it cannot be found.".Loc());

                if (ImGui.Checkbox("Use Consumables for Trial Crafts".Loc(), ref P.Config.UseConsumablesTrial))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("Use Consumables for Quick Synth Crafts".Loc(), ref P.Config.UseConsumablesQuickSynth))
                {
                    P.Config.Save();
                }

                ImGui.Indent();
                if (ImGui.CollapsingHeader("Default Consumables".Loc()))
                {
                    bool changed = false;
                    changed |= P.Config.DefaultConsumables.DrawFood();
                    changed |= P.Config.DefaultConsumables.DrawPotion();
                    changed |= P.Config.DefaultConsumables.DrawManual();
                    changed |= P.Config.DefaultConsumables.DrawSquadronManual();

                    if (changed)
                    {
                        P.Config.Save();
                    }
                }
                ImGui.Unindent();

                if (ImGui.Checkbox("Prioritize NPC repairs above self-repairs".Loc(), ref P.Config.PrioritizeRepairNPC))
                {
                    P.Config.Save();
                }

                ImGuiComponents.HelpMarker("When repairing, if a repair NPC is nearby it will try to repair with them instead of self-repairs. Will still try to use self-repairs if no NPC is found and you have the required levels to repair.".Loc());

                if (ImGui.Checkbox("Disable Endurance if unable to repair".Loc(), ref P.Config.DisableEnduranceNoRepair))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("Once you hit the repair threshold, if you're unable to repair either yourself or through an NPC, disable Endurance.".Loc());

                if (ImGui.Checkbox("Pause lists if unable to repair".Loc(), ref P.Config.DisableListsNoRepair))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("Once you hit the repair threshold, if you're unable to repair either yourself or through an NPC, pause the current list.".Loc());

                bool requestStop = P.Config.RequestToStopDuty;
                bool requestResume = P.Config.RequestToResumeDuty;
                int resumeDelay = P.Config.RequestToResumeDelay;

                if (ImGui.Checkbox("Have Artisan turn off Endurance / pause lists when Duty Finder is ready".Loc(), ref requestStop))
                {
                    P.Config.RequestToStopDuty = requestStop;
                    P.Config.Save();
                }

                if (requestStop)
                {
                    if (ImGui.Checkbox("Have Artisan resume Endurance / unpause lists after leaving Duty".Loc(), ref requestResume))
                    {
                        P.Config.RequestToResumeDuty = requestResume;
                        P.Config.Save();
                    }

                    if (requestResume)
                    {
                        if (ImGui.SliderInt("Delay to resume (seconds)".Loc(), ref resumeDelay, 5, 60))
                        {
                            P.Config.RequestToResumeDelay = resumeDelay;
                        }
                    }
                }

                if (ImGui.Checkbox("Disable Automatically Equipping Required Items for Crafts".Loc(), ref P.Config.DontEquipItems))
                    P.Config.Save();

                if (ImGui.Checkbox("Play Sound After Endurance Is Complete".Loc(), ref P.Config.PlaySoundFinishEndurance))
                    P.Config.Save();

                if (ImGui.Checkbox("Play Sound After List Is Complete".Loc(), ref P.Config.PlaySoundFinishList))
                    P.Config.Save();

                if (P.Config.PlaySoundFinishEndurance || P.Config.PlaySoundFinishList)
                {
                    ImGui.SliderFloat("Sound Volume".Loc(), ref P.Config.SoundVolume, 0f, 1f, "%.2f");
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        P.Config.Save();
                }

                if (ImGuiEx.ButtonCtrl("Reset Cosmic Exploration Crafting Configs".Loc()))
                {
                    // c.Key 是使用者設定檔裡累積下來的配方 ID，不保證還存在於本地資料表：
                    // 台服的 Recipe 表不連續（0..6407 與 30000..38000 之間有一段兩萬多的空洞），
                    // 裸 GetRow 命中空洞就擲例外。這段在 Draw 裡，Dalamud 攔到之後會把 Artisan
                    // 的 Draw 委派設成 null，整個外掛介面到重開遊戲前都不會再出現。
                    // 查不到的項目「保留而不刪」：這裡是「重設宇宙探索設定」，把一筆讀不到的
                    // 設定當成宇宙配方刪掉是猜測，留著並記一筆 Information 讓它可被回報。
                    var recipeSheet = Svc.Data.GetExcelSheet<Recipe>();
                    var copy = P.Config.RecipeConfigs;
                    foreach (var c in copy)
                    {
                        if (!recipeSheet.TryGetRow(c.Key, out var recipeRow))
                        {
                            Svc.Log.Information($"[Artisan] 設定檔中的配方 ID {c.Key} 不存在於本地 Recipe 資料表，重設宇宙探索設定時略過該筆。");
                            continue;
                        }
                        if (recipeRow.Number == 0)
                            P.Config.RecipeConfigs.Remove(c.Key);
                    }
                }
            }
            if (ImGui.CollapsingHeader("Macro Settings".Loc()))
            {
                if (ImGui.Checkbox("Skip Macro Steps if Unable To Use Action".Loc(), ref P.Config.SkipMacroStepIfUnable))
                    P.Config.Save();

                if (ImGui.Checkbox("Prevent Artisan from Continuing After Macro Finishes".Loc(), ref P.Config.DisableMacroArtisanRecommendation))
                    P.Config.Save();
            }
            if (ImGui.CollapsingHeader("Standard Recipe Solver Settings".Loc()))
            {
                if (ImGui.Checkbox("Use ?? - ??".Loc(Skills.TricksOfTrade.NameOfAction(), LuminaSheets.AddonSheet[227].Text.ToString()), ref useTricksGood))
                {
                    P.Config.UseTricksGood = useTricksGood;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Checkbox("Use ?? - ??".Loc(Skills.TricksOfTrade.NameOfAction(), LuminaSheets.AddonSheet[228].Text.ToString()), ref useTricksExcellent))
                {
                    P.Config.UseTricksExcellent = useTricksExcellent;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("These 2 options allow you to make ?? a priority when condition is ?? or ??.\n\nThis will replace ?? & ?? usage.\n\n?? will still be used before learning these or under certain circumstances regardless of settings.".Loc(Skills.TricksOfTrade.NameOfAction(), LuminaSheets.AddonSheet[227].Text.ToString(), LuminaSheets.AddonSheet[228].Text.ToString(), Skills.PreciseTouch.NameOfAction(), Skills.IntensiveSynthesis.NameOfAction(), Skills.TricksOfTrade.NameOfAction()));
                if (ImGui.Checkbox("Use Specialist Actions".Loc(), ref useSpecialist))
                {
                    P.Config.UseSpecialist = useSpecialist;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("If the current job is a specialist, spends any Crafter's Delineation you may have.\nCareful Observation replaces Observe.\nHeart and Soul will be used for an early Precise Touch.".Loc());
                ImGui.TextWrapped("Max Quality%%".Loc());
                ImGuiComponents.HelpMarker("Once quality has reached the below percentage, Artisan will focus on progress only.".Loc());
                if (ImGui.SliderInt("###SliderMaxQuality", ref maxQuality, 0, 100, $"%d%%"))
                {
                    P.Config.MaxPercentage = maxQuality;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    P.Config.Save();

                ImGui.Text("Collectible Threshold Breakpoint".Loc());
                ImGuiComponents.HelpMarker("The solver will stop going for quality once a collectible has hit a certain breakpoint.".Loc());

                if (ImGui.RadioButton("Minimum".Loc(), P.Config.SolverCollectibleMode == 1))
                {
                    P.Config.SolverCollectibleMode = 1;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton("Middle".Loc(), P.Config.SolverCollectibleMode == 2))
                {
                    P.Config.SolverCollectibleMode = 2;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton("Maximum".Loc(), P.Config.SolverCollectibleMode == 3))
                {
                    P.Config.SolverCollectibleMode = 3;
                    P.Config.Save();
                }

                if (ImGui.Checkbox("Use Quality Starter (??)".Loc(Skills.Reflect.NameOfAction()), ref P.Config.UseQualityStarter))
                    P.Config.Save();
                ImGuiComponents.HelpMarker("This tends to be more favourable at lower durability crafts.".Loc());

                //if (ImGui.Checkbox("Low Stat Mode", ref P.Config.LowStatsMode))
                //    P.Config.Save();

                //ImGuiComponents.HelpMarker("This swaps out Waste Not II & Groundwork for Prudent Synthesis");

                ImGui.TextWrapped("?? - Max ?? stacks".Loc(Skills.PreparatoryTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()));
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("Will only use ?? up to the number of ?? stacks. This is useful to tweak conservation of CP.".Loc(Skills.PreparatoryTouch.NameOfAction(), Buffs.InnerQuiet.NameOfBuff()));
                ImGui.SliderInt($"###MaxIQStacksPrepTouch", ref P.Config.MaxIQPrepTouch, 0, 10);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    P.Config.Save();

                if (ImGui.Checkbox("Use Material Miracle when available".Loc(), ref P.Config.UseMaterialMiracle))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("This will switch the standard recipe solver to the expert solver for the duration of the buff. As this is a timed buff and not a permanent one with stacks, this will not give you correct simulator results as we can't really simulate it properly.".Loc());

                // 🔴 上面那句 tooltip 說了「buff 期間換成專家解算器代打」,但**沒有說那要付多少代價**。
                //    2026-08-07 離線量測(9 個宇宙配方 × 每格 1500 次製作,能力值 工5624/加5293/製674,
                //    2x2 隔離出唯一變數就是這個旗標):對**標準解算器**在宇宙配方上是淨損失,
                //    而且 9 個配方**每一個**的做出來率都變差 ——
                //      旗標關:做出來 100.0% / 期望品質 56.2
                //      旗標開:做出來  46.1% / 期望品質 35.5
                //    把專家解算器的「先做完進度」自適應打開也只救回一半(46.1% -> 64.5%)。
                //    最極端的是非專家的宇宙配方(#36214):100% -> 9%,因為專家解算器接手不了非專家配方。
                //    ⚠️ 專家解算器與 Raphael 解算器**不受影響**(Raphael 那條有自己的安全閘門)。
                //    「有沒有問題」要在列上看得見,tooltip 只放「為什麼」。
                if (P.Config.UseMaterialMiracle)
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow,
                        "注意:這個選項會讓標準解算器在整段 buff 期間交給專家解算器代打。離線量測顯示宇宙配方的做出來率因此從 100% 掉到約 46%(專家解算器與 Raphael 解算器不受影響)。");
                    ImGuiEx.Tooltip("量測條件:9 個宇宙配方、每個 1500 次模擬製作。把專家解算器的「先做完進度」自適應選項打開可回升到約 64%,仍低於不開這個選項。\n若你只是想在宇宙任務用奇蹟之材,把該配方指派給專家解算器或 Raphael 解算器不會有這個代價。");

                    ImGui.Indent();
                    if (ImGui.Checkbox("Use more than once per craft.".Loc(), ref P.Config.MaterialMiracleMulti))
                        P.Config.Save();

                    ImGui.Unindent();
                }

            }
            bool openExpert = false;
            if (ImGui.CollapsingHeader("Expert Recipe Solver Settings".Loc()))
            {
                openExpert = true;
                if (P.Config.ExpertSolverConfig.Draw())
                    P.Config.Save();
            }
            if (!openExpert)
            {
                // 移除图标显示代码
            }

            if (ImGui.CollapsingHeader("Raphael Solver Settings".Loc()))
            {
                if (P.Config.RaphaelSolverConfig.Draw())
                    P.Config.Save();
            }

            using (ImRaii.Disabled())
            {
                if (ImGui.CollapsingHeader("Script Solver Settings (Currently Disabled)".Loc()))
                {
                    if (P.Config.ScriptSolverConfig.Draw())
                        P.Config.Save();
                }
            }
            if (ImGui.CollapsingHeader("UI Settings".Loc()))
            {
                if (ImGui.Checkbox("Disable highlighting box".Loc(), ref disableGlow))
                {
                    P.Config.DisableHighlightedAction = disableGlow;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("This is the box that highlights the actions on your hotbars for manual play.".Loc());

                if (ImGui.Checkbox("Disable recommendation toasts".Loc(), ref disableToasts))
                {
                    P.Config.DisableToasts = disableToasts;
                    P.Config.Save();
                }

                ImGuiComponents.HelpMarker("These are the pop-ups whenever a new action is recommended.".Loc());

                bool lockMini = P.Config.LockMiniMenuR;
                if (ImGui.Checkbox("Keep Recipe List mini-menu position attached to Recipe List.".Loc(), ref lockMini))
                {
                    P.Config.LockMiniMenuR = lockMini;
                    P.Config.Save();
                }

                if (!P.Config.LockMiniMenuR)
                {
                    if (ImGui.Checkbox("Pin mini-menu position".Loc(), ref P.Config.PinMiniMenu))
                    {
                        P.Config.Save();
                    }
                }

                if (ImGui.Button("Reset Recipe List mini-menu position".Loc()))
                {
                    AtkResNodeFunctions.ResetPosition = true;
                }

                if (ImGui.Checkbox("Expanded Search Bar Functionality".Loc(), ref P.Config.ReplaceSearch))
                {
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("Expands the search bar in the recipe menu with instant results and functionality to click to open recipes.".Loc());

                bool hideQuestHelper = P.Config.HideQuestHelper;
                if (ImGui.Checkbox("Hide Quest Helper".Loc(), ref hideQuestHelper))
                {
                    P.Config.HideQuestHelper = hideQuestHelper;
                    P.Config.Save();
                }

                bool hideTheme = P.Config.DisableTheme;
                if (ImGui.Checkbox("Disable Custom Theme".Loc(), ref hideTheme))
                {
                    P.Config.DisableTheme = hideTheme;
                    P.Config.Save();
                }
                ImGui.SameLine();

                if (IconButtons.IconTextButton(FontAwesomeIcon.Clipboard, "Copy Theme".Loc()))
                {
                    ImGui.SetClipboardText("DS1H4sIAAAAAAAACq1YS3PbNhD+Kx2ePR6AeJG+xXYbH+KOJ3bHbW60REusaFGlKOXhyX/v4rEACEqumlY+ECD32/cuFn7NquyCnpOz7Cm7eM1+zy5yvfnDPL+fZTP4at7MHVntyMi5MGTwBLJn+HqWLZB46Ygbx64C5kQv/nRo8xXQ3AhZZRdCv2jdhxdHxUeqrJO3Ftslb5l5u/Fa2rfEvP0LWBkBPQiSerF1Cg7wApBn2c5wOMv2juNn9/zieH09aP63g+Kqyr1mI91mHdj5mj3UX4bEG+b5yT0fzRPoNeF1s62e2np+EuCxWc+7z5cLr1SuuCBlkTvdqBCEKmaQxCHJeZmXnFKlgMHVsmnnEZ5IyXMiFUfjwt6yCHvDSitx1212m4gHV0QURY4saMEYl6Q4rsRl18/rPuCZQ+rFJxeARwyAJb5fVmD4NBaJEK3eL331UscuAgflOcY0J5zLUioHpHmhCC0lCuSBwU23r3sfF/0N0wKdoxcGFqHezYZmHypJIkgiSCJIalc8NEM7Utb6ErWlwngt9aUoFRWSB3wilRUl5SRwISUFvhJt9lvDrMgLIjgLzK66tq0228j0H+R3W693l1UfmUd9kqA79MKn9/2sB9lPI8hbofb073vdh1BbQYRgqKzfGbTfTWVqHmnMOcXUpI6BXhzGJjEQCNULmy4x9GpZz1a3Vb8KqaIDz4RPVGZin6dlZPKDSS29baAyRqYfzVGnr0ekaaowTbEw9MLjLnfD0GGT1unHSSlKr2lRyqLA2qU5ESovi6m+lkvqYiZ1/ygxyqrgjDKF8Yr2lp1pd4R7dokhvOBUQk37TCVKQbX4TMVtyuymruKWJCURVEofClYWbNpWCQfFifDwsWnYyXXS8ZxDOI+H0uLToPzrhKg3VV8N3amt1dP/t5goW/E85pg2pB8N8sd623yr3/dNOPYVstELg9cLA8zFCJKapQpEYkPVi9CMA/L/Uv8hrk1hmg9WKKMQXyIxnGFrm6i06MkhBHlIiQ8rI0xx4k/rsLWBsWpbTmmhqFIypcvUHTRgQ859V/bbKaPf1s/dbBcfD0R6NnCWwg/dS3lB4MfQMSrnCY9EK8qEw9uUl4YdHjRQRVFTuu5mq2a9uOvrfVOH0SDHqtXxMjDfi1RA/fyyGb7G5y5KdJg8EnTXdsOHZl1vQyJJQrlCQTDsEBi80HdhO+VwrEP48hwdTRp202yHbgGzhRfu03/UCA4gjglDd44mUT2D2i4UH9coSy8mfjEYN54NfbcOOIZnn15M7YqAH5rFEmdl3eJ8r0N5E9zH0fz71nQQyN+1/zSP6yR2A/l93dazoY6n5DdyiumWc91Xi+u+2zxU/aI+Jipq2QD5tdrfgO3t2P5jcqz9gLEXAEjgFHzcMJUgr5uXyDQsNSxZtCvX81s3r1qLOw0EztC3ORiEs4vssu9W9fqn2263HqpmncFF016PqklGjh1kjQ2NUyUJH08mcIk9gSrqn+jg0XFoqeqTrmDPwQv+PDEr6wl3oljaxcRSRTCyMc/lJJ/lAcnNhMr3WWZ+ES3exrXE+HJ2yNOrowkb97A2cExdXcrYjaFToVDfGSMqnCaDa0pi/vzNMyLG/wQEyzmzfhx7KAwJUn93Fz6v5shD8B+DRAG4Oh+QHYapovAd3/OEQzuiDSdE4c8wjJHh7iiBFFozvP3+NxT8RWGlEQAA");
                    Notify.Success("Theme copied to clipboard".Loc());
                }

                if (ImGui.Checkbox("Disable Allagan Tools Integration With Lists".Loc(), ref P.Config.DisableAllaganTools))
                    P.Config.Save();

                if (ImGui.Checkbox("Disable Artisan Context Menu Options".Loc(), ref P.Config.HideContextMenus))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("These are the new options when you right click or press square on a recipe in the recipe list.".Loc());

                ImGui.Indent();
                if (ImGui.CollapsingHeader("Simulator Settings".Loc()))
                {
                    if (ImGui.Checkbox("Hide Recipe Window Simulator Result".Loc(), ref P.Config.HideRecipeWindowSimulator))
                        P.Config.Save();

                    ImGui.SliderFloat("Simulator Action Image Size".Loc(), ref P.Config.SimulatorActionSize, 5f, 70f);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        P.Config.Save();
                    }
                    ImGuiComponents.HelpMarker("Sets the scale of the action images that appear in the simulator tab.".Loc());

                    if (ImGui.Checkbox("Enable Manual Mode Hover Preview".Loc(), ref P.Config.SimulatorHoverMode))
                        P.Config.Save();

                    if (ImGui.Checkbox("Hide Action Tooltips".Loc(), ref P.Config.DisableSimulatorActionTooltips))
                        P.Config.Save();

                    ImGuiComponents.HelpMarker("When hovering over actions in manual mode, the description tooltip will not show.".Loc());
                }
                ImGui.Unindent();
            }
            if (ImGui.CollapsingHeader("List Settings".Loc()))
            {
                ImGui.TextWrapped("These settings will automatically be applied when creating a crafting list.".Loc());

                if (ImGui.Checkbox("Skip items you already have enough of".Loc(), ref P.Config.DefaultListSkip))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("Automatically Extract Materia".Loc(), ref P.Config.DefaultListMateria))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("Automatic Repairs".Loc(), ref P.Config.DefaultListRepair))
                {
                    P.Config.Save();
                }

                if (P.Config.DefaultListRepair)
                {
                    ImGui.TextWrapped("Repair at".Loc());
                    ImGui.SameLine();
                    ImGui.SliderInt("###SliderRepairDefault", ref P.Config.DefaultListRepairPercent, 0, 100, $"%d%%");
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        P.Config.Save();
                    }
                }

                if (ImGui.Checkbox(SharedText.NewItemsAsQuickSynth.Loc(), ref P.Config.DefaultListQuickSynth))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("Reset \"Number of Times to Add\" after adding to list.".Loc(), ref P.Config.ResetTimesToAdd))
                    P.Config.Save();

                if (ImGui.Checkbox("Restock finished products from retainers as well".Loc(), ref P.Config.RestockFinishedProductsFromRetainers))
                    P.Config.Save();

                if (ImGui.Checkbox("Use AutoRetainer's fast item retrieval".Loc(), ref P.Config.UseDirectRetainerRetrieval))
                    P.Config.Save();
                ImGuiComponents.HelpMarker("When AutoRetainer is installed, restocking asks it to send the game's own retrieve command for each stack instead of clicking through the retainer window and its quantity dialog, which is several times faster. Whole stacks are taken rather than exact amounts. Turn this off to always drive the retainer window. Has no effect if AutoRetainer is missing or too old - the retainer window is used automatically in that case.".Loc());

                // SetNextItemWidth rather than PushItemWidth: the surrounding block pushes widths without
                // ever popping them, so adding a matching pop here would unbalance what follows.
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderInt("Free bag slots needed to take a whole stack".Loc(), ref P.Config.RestockFullStackFreeSlots, 1, 10))
                {
                    if (P.Config.RestockFullStackFreeSlots < 1)
                        P.Config.RestockFullStackFreeSlots = 1;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    P.Config.Save();
                ImGuiComponents.HelpMarker("Only applies to the retainer window path, where restocking takes the whole stack instead of the exact amount still needed - but only while the bag has at least this many free slots, because a whole stack landing on an existing partial stack can split across two of them. Lower it to take whole stacks more often and make fewer return trips, at the risk of the withdrawal coming up short when the bag is nearly full. Raise it to be more cautious. While the free-slot count cannot be read at all, the exact amount is used regardless of this setting.".Loc());

                ImGui.PushItemWidth(100);
                if (ImGui.InputInt("Times to Add with Context Menu".Loc(), ref P.Config.ContextMenuLoops))
                {
                    if (P.Config.ContextMenuLoops <= 0)
                        P.Config.ContextMenuLoops = 1;

                    P.Config.Save();
                }

                ImGui.PushItemWidth(400);
                if (ImGui.SliderFloat("Delay Between Crafts".Loc(), ref P.Config.ListCraftThrottle2, 0f, 2f, "%.1f"))
                {
                    if (P.Config.ListCraftThrottle2 < 0f)
                        P.Config.ListCraftThrottle2 = 0f;

                    if (P.Config.ListCraftThrottle2 > 2f)
                        P.Config.ListCraftThrottle2 = 2f;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    P.Config.Save();
                }

                ImGui.Indent();
                if (ImGui.CollapsingHeader("Ingredient Table Settings".Loc()))
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "All Column Settings do not have an effect if you have already viewed the ingredients table for a list.".Loc());

                    if (ImGui.Checkbox("Subtract owned finished products from the ingredient table".Loc(), ref P.Config.SubtractOwnedFinishedProductFromIngredientTable))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Inventory\" Column".Loc(), ref P.Config.DefaultHideInventoryColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Retainers\" Column".Loc(), ref P.Config.DefaultHideRetainerColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Remaining Needed\" Column".Loc(), ref P.Config.DefaultHideRemainingColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Sources\" Column".Loc(), ref P.Config.DefaultHideCraftableColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Number Craftable\" Column".Loc(), ref P.Config.DefaultHideCraftableCountColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Used to Craft\" Column".Loc(), ref P.Config.DefaultHideCraftItemsColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Category\" Column".Loc(), ref P.Config.DefaultHideCategoryColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"Gathered Zone\" Column".Loc(), ref P.Config.DefaultHideGatherLocationColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default Hide \"ID\" Column".Loc(), ref P.Config.DefaultHideIdColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default \"Only show HQ Crafts\" Enabled".Loc(), ref P.Config.DefaultHQCrafts))
                        P.Config.Save();

                    if (ImGui.Checkbox("Default \"Colour Validation\" Enabled".Loc(), ref P.Config.DefaultColourValidation))
                        P.Config.Save();

                    if (ImGui.Checkbox("Fetch Prices from Universalis".Loc(), ref P.Config.UseUniversalis))
                        P.Config.Save();

                    if (P.Config.UseUniversalis)
                    {
                        if (ImGui.Checkbox("Limit Universalis to current DC".Loc(), ref P.Config.LimitUnversalisToDC))
                            P.Config.Save();

                        if (ImGui.Checkbox("Only Fetch Prices on Demand".Loc(), ref P.Config.UniversalisOnDemand))
                            P.Config.Save();

                        ImGuiComponents.HelpMarker("You will have to click a button to fetch the price per item.".Loc());
                    }
                }

                ImGui.Unindent();
            }
        }

        private void ShowEnduranceMessage()
        {
            if (!P.Config.ViewedEnduranceMessage)
            {
                P.Config.ViewedEnduranceMessage = true;
                P.Config.Save();

                ImGui.OpenPopup("EndurancePopup");

                var windowSize = new Vector2(512 * ImGuiHelpers.GlobalScale,
                    ImGui.GetTextLineHeightWithSpacing() * 13 + 2 * ImGui.GetFrameHeightWithSpacing() * 2f);
                ImGui.SetNextWindowSize(windowSize);
                ImGui.SetNextWindowPos((ImGui.GetIO().DisplaySize - windowSize) / 2);

                using var popup = ImRaii.Popup("EndurancePopup",
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.Modal);
                if (!popup)
                    return;

                ImGui.TextWrapped("I have been receiving quite a number of messages regarding \"buggy\" Endurance mode not setting ingredients anymore. As of the previous update, the old functionality of Endurance has been moved to a new setting.".Loc());
                ImGui.Dummy(new Vector2(0));

                var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/EnduranceNewSetting.png");

                if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var img))
                {
                    ImGuiEx.ImGuiLineCentered("###EnduranceNewSetting", () =>
                    {
                        ImGui.Image(img.Handle, new Vector2(img.Width, img.Height));
                    });
                }

                ImGui.Spacing();

                ImGui.TextWrapped("This change was made to bring back the very original behaviour of Endurance mode. If you do not care about your ingredient ratio, please make sure to enable Max Quantity Mode.".Loc());

                ImGui.SetCursorPosY(windowSize.Y - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y);
                if (ImGui.Button("Close".Loc(), -Vector2.UnitX))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    public enum OpenWindow
    {
        None = 0,
        Main = 1,
        Endurance = 2,
        Macro = 3,
        Lists = 4,
        About = 5,
        Debug = 6,
        FCWorkshop = 7,
        SpecialList = 8,
        Overview = 9,
        Simulator = 10,
        RaphaelCache = 11,
        Assigner = 12,
    }
}
