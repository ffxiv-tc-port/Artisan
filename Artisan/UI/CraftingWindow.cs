using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.Toast;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using System;

namespace Artisan.UI
{
    internal class CraftingWindow : Window, IDisposable
    {
        public bool RepeatTrial;
        private DateTime _estimatedCraftEnd;

        // 視窗上的「附近有沒有人」只是給人看的，每 500 毫秒重算一次；真正決定延遲的是 OnRecommendationReady 當下重算的那次。
        private bool _playersNearbyShown;
        private long _playersNearbyNextCheck;
        private static bool? _lastPlayersNearbyDecision;

        public CraftingWindow() : base("Artisan Crafting Window###MainCraftWindow", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
        {
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            this.SizeConstraints = new()
            {
                MinimumSize = new System.Numerics.Vector2(150f, 0f),
                MaximumSize = new System.Numerics.Vector2(310f, 500f)
            };

            CraftingProcessor.SolverStarted += OnSolverStarted;
            CraftingProcessor.SolverFailed += OnSolverFailed;
            CraftingProcessor.SolverFinished += OnSolverFinished;
            CraftingProcessor.RecommendationReady += OnRecommendationReady;
        }

        public void Dispose()
        {
            CraftingProcessor.SolverStarted -= OnSolverStarted;
            CraftingProcessor.SolverFailed -= OnSolverFailed;
            CraftingProcessor.SolverFinished -= OnSolverFinished;
            CraftingProcessor.RecommendationReady -= OnRecommendationReady;
        }

        public override bool DrawConditions()
        {
            return P.PluginUi.CraftingVisible;
        }

        public override void PreDraw()
        {
            if (!P.Config.DisableTheme)
            {
                P.Style.Push();
                P.StylePushed = true;
            }
            // base.PreDraw() 推入 Dalamud 的每視窗不透明度(標題列右鍵選單的「不透明度」滑桿)。
            // 🔴 base 必須放在 P.Style.Push() **之後**:StyleModel.Push() 自己會推一個
            // 絕對值的 ImGuiStyleVar.Alpha(Dalamud/Interface/Style/StyleModelV1.cs)，
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

        public override void Draw()
        {
            if (!P.Config.DisableHighlightedAction)
                Hotbars.MakeButtonsGlow(CraftingProcessor.NextRec.Action);

            // ECommons dropped AddHeaderIcon in favor of Window.TitleBarButton, but this
            // window uses NoTitleBar so the native title bar button row never renders;
            // draw an equivalent inline icon button instead.
            if (ImGuiEx.IconButton(FontAwesomeIcon.Cog, "OpenConfig"))
            {
                P.PluginUi.IsOpen = true;
            }
            ImGuiEx.Tooltip("Open Config".Loc());

            if (Crafting.CurCraft != null && !Crafting.CurCraft.CraftExpert && Crafting.CurRecipe?.SecretRecipeBook.RowId > 0 && Crafting.CurCraft?.CraftLevel == Crafting.CurCraft?.StatLevel && !CraftingProcessor.ActiveSolver.IsType<MacroSolver>())
            {
                ImGui.Dummy(new System.Numerics.Vector2(12f));
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "This is a current level master recipe. Your success rate may vary so it is recommended to use an Artisan macro or manually solve this.".Loc());
            }

            bool autoMode = P.Config.AutoMode;
            if (ImGui.Checkbox("Auto Action Mode".Loc(), ref autoMode))
            {
                P.Config.SetAutoMode(autoMode);
                P.Config.Save();
            }

            if (autoMode && !P.Config.ReplicateMacroDelay)
            {
                if (P.Config.ReplicateMacroDelayWhenPlayersNearby)
                {
                    var now = Environment.TickCount64;
                    if (now >= _playersNearbyNextCheck)
                    {
                        _playersNearbyShown = OtherPlayersNearby();
                        _playersNearbyNextCheck = now + 500;
                    }
                    ImGuiEx.Text(_playersNearbyShown ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudWhite,
                        _playersNearbyShown ? "Other players nearby: replicating macro delay".Loc() : "No other players nearby: using your delay".Loc());
                }

                var delay = P.Config.AutoDelay;
                ImGui.PushItemWidth(200);
                if (ImGui.SliderInt("Set delay (ms)".Loc(), ref delay, 0, 1000))
                {
                    if (delay < 0) delay = 0;
                    if (delay > 1000) delay = 1000;

                    P.Config.AutoDelay = delay;
                }
                // 同 PluginUI 的 Execution Delay：滑桿拖曳中每畫格都會回傳 true，
                // 存檔延到放手時才做，避免以幀率同步寫磁碟。
                if (ImGui.IsItemDeactivatedAfterEdit())
                    P.Config.Save();
            }

            if (Endurance.RecipeID != 0 && !CraftingListUI.Processing && Endurance.Enable)
            {
                if (ImGui.Button("Disable Endurance".Loc()))
                {
                    Endurance.ToggleEndurance(false);
                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                    PreCrafting.Tasks.Clear();
                }
            }

            if (!Endurance.Enable && Crafting.IsTrial)
                ImGui.Checkbox("Trial Craft Repeat".Loc(), ref RepeatTrial);

            if (CraftingProcessor.ActiveSolver)
            {
                var text = "Using ??".Loc(CraftingProcessor.ActiveSolver.Name);
                if (CraftingProcessor.NextRec.Comment.Length > 0)
                    text += $" ({CraftingProcessor.NextRec.Comment})";
                ImGuiEx.TextWrapped(text.Replace("%", ""));
            }

            if (P.Config.CraftingX && Endurance.Enable)
                ImGui.Text("Remaining Crafts: ??".Loc(P.Config.CraftX));

            if (_estimatedCraftEnd != default)
            {
                var diff = _estimatedCraftEnd - DateTime.Now;
                string duration = string.Format("{0:D2}h {1:D2}m {2:D2}s", diff.Hours, diff.Minutes, diff.Seconds);
                ImGui.Text("Approximate Remaining Duration: ??".Loc(duration));
            }

            if (!P.Config.AutoMode)
            {
                ImGui.Text("Semi-Manual Mode".Loc());

                var action = CraftingProcessor.NextRec.Action;
                using var disable = ImRaii.Disabled(action == Skills.None);

                if (ImGui.Button("Execute recommended action".Loc()))
                {
                    ActionManagerEx.UseSkill(action);
                }
                if (ImGui.Button("Fetch Recommendation".Loc()))
                {
                    ShowRecommendation(action);
                }
            }
        }

        private void ShowRecommendation(Skills action)
        {
            if (!P.Config.DisableToasts)
            {
                QuestToastOptions options = new() { IconId = action.IconOfAction(CharacterInfo.JobID) };
                Svc.Toasts.ShowQuest("Use ??".Loc(action.NameOfAction()), options);
            }
        }

        private void OnSolverStarted(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState initialStep)
        {
            if (P.Config.AutoMode && solver)
            {
                var estimatedTime = SolverUtils.EstimateCraftTime(solver.Clone()!, craft, initialStep.Quality);
                var count = P.Config.CraftingX && Endurance.Enable ? P.Config.CraftX : 1;
                _estimatedCraftEnd = DateTime.Now + count * estimatedTime;
            }
        }

        private void OnSolverFailed(Lumina.Excel.Sheets.Recipe recipe, string reason)
        {
            var text = "??. Artisan will not continue.".Loc(reason);
            Svc.Toasts.ShowError(text);
            DuoLog.Error(text);

            // 耐力／清單會停在這場製作上等人接手；自動模式關著時技能本來就是使用者自己按的，不響。
            if (P.Config.AutoMode || Endurance.IPCOverride)
                Endurance.NotifyForcedStopIfRunning("解算器不能用");

            // 由別的外掛透過 IPC 驅動時(ICE 的宇宙製作),沒有人在看這個錯誤訊息,而耐力模式
            // 還開著就會在這場製作結束後照樣開下一件、再撞同一個問題一次。既然已經宣告
            // 「Artisan will not continue」,就真的停下來,把控制權交回呼叫端自己的守衛。
            if (Endurance.IPCOverride)
                Endurance.ToggleEndurance(false);
        }

        private void OnSolverFinished(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState finalStep)
        {
            _estimatedCraftEnd = default;
        }

        private void OnRecommendationReady(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState step, Solver.Recommendation recommendation)
        {
            if (!Simulator.CanUseAction(craft, step, recommendation.Action))
            {
                return;
            }
            ShowRecommendation(recommendation.Action);
            if (P.Config.AutoMode || Endurance.IPCOverride)
            {
                var replicateMacroDelay = ShouldReplicateMacroDelay();
                if (!replicateMacroDelay)
                    P.CTM.DelayNext(P.Config.AutoDelay);
                P.CTM.Enqueue(() => Crafting.CurState == Crafting.State.InProgress, 3000, true, "WaitForStateToUseAction");
                var recommendationStep = step.Index;
                P.CTM.Enqueue(() =>
                {
                    // A manual action, Endurance, or another automation source may have
                    // already advanced the craft past the step this recommendation was
                    // computed for by the time this task actually runs. Treat that as the
                    // recommendation being stale and let it pass instead of retrying
                    // UseSkill for up to the task's timeout and blocking every later step.
                    if (Crafting.CurState != Crafting.State.InProgress || Crafting.CurStep?.Index != recommendationStep)
                        return true;

                    return ActionManagerEx.UseSkill(recommendation.Action);
                });
                if (replicateMacroDelay)
                    P.CTM.DelayNext(Calculations.ActionIsLengthyAnimation(recommendation.Action) ? 3000 : 2000);
            }
        }

        /// <summary>選項關著時恆等於 <c>ReplicateMacroDelay</c>。</summary>
        private static bool ShouldReplicateMacroDelay()
        {
            if (P.Config.ReplicateMacroDelay)
                return true;
            if (!P.Config.ReplicateMacroDelayWhenPlayersNearby)
                return false;

            var nearby = OtherPlayersNearby();
            if (_lastPlayersNearbyDecision != nearby)
            {
                _lastPlayersNearbyDecision = nearby;
                Svc.Log.Information(nearby
                    ? "[Artisan] 附近有其他玩家：自動製作改用遊戲巨集延遲（每個技能 2～3 秒）。"
                    : $"[Artisan] 附近沒有其他玩家：自動製作改回自訂延遲 {P.Config.AutoDelay} 毫秒。");
            }
            return nearby;
        }

        /// <summary>
        /// 物件表裡有沒有自己以外、名字不為空的玩家角色（隊友也算，範圍＝遊戲目前載入的範圍，不另設距離）。
        /// 只在主執行緒呼叫；當下這一幀讀完，不保存任何物件。
        /// </summary>
        private static bool OtherPlayersNearby()
        {
            var local = Svc.Objects.LocalPlayer;
            if (local == null)
                return false;

            var selfId = local.EntityId;
            foreach (var obj in Svc.Objects)
            {
                if (obj is IPlayerCharacter player && player.EntityId != selfId && !string.IsNullOrEmpty(player.Name.TextValue))
                    return true;
            }
            return false;
        }
    }
}
