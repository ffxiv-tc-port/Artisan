using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;
using static ECommons.GenericHelpers;
using YesAlreadyBotherPause = Artisan.IPC.YesAlreadyBotherPause;

namespace Artisan.Autocraft
{
    internal unsafe class RepairManager
    {
        // 「全部修理」之後的確認框由 Artisan 自己按；這段時間請 YesAlready 不要按同一扇。
        private const int YesAlreadyPauseMs = 15_000;

        private const int YesAlreadyFailedRetryMs = 5_000;

        // 這段時間內遊戲回「製作狀態下無法進行該操作」視為修理被拒，不算進連續錯誤。
        private const long RepairRejectWindowMs = 5_000;

        // 以下只在框架執行緒讀寫（ProcessRepair 與錯誤提示的掛鉤都在框架執行緒上）。
        private static long _lastRepairPressAt;
        private static long _nextYesAlreadyFailLogAt;
        private static bool _waitingForCraftStateLogged;
        private static long? _yesnoOpenSince;
        private static bool _yesnoStuckLogged;
        private static string[]? _craftStateRejectTexts;

        /// <summary>製作中／準備製作／製作動作執行中任一旗標還在。</summary>
        /// <remarks>離開製作筆記時「製作中」先清，另外兩個會晚一兩百毫秒；三個都清掉之前按的修理確認會被遊戲拒絕。</remarks>
        internal static bool InCraftingState()
            => Svc.Condition[ConditionFlag.Crafting]
               || Svc.Condition[ConditionFlag.PreparingToCraft]
               || Svc.Condition[ConditionFlag.ExecutingCraftingAction];

        /// <summary>Artisan 剛按過「全部修理」或修理確認框的「是」。</summary>
        internal static bool RecentlyPressedRepair
            => _lastRepairPressAt != 0 && Environment.TickCount64 - _lastRepairPressAt <= RepairRejectWindowMs;

        /// <summary>這段文字是不是遊戲因為製作狀態拒絕操作的那幾則訊息（LogMessage 7705／7740／7741）。</summary>
        internal static bool IsCraftStateRejectText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            if (_craftStateRejectTexts == null)
            {
                var sheet = Svc.Data.GetExcelSheet<LogMessage>();
                _craftStateRejectTexts = new uint[] { 7705, 7740, 7741 }
                    .Select(id => sheet != null && sheet.TryGetRow(id, out var row) ? row.Text.ExtractText() : string.Empty)
                    .Where(x => x.Length > 0)
                    .ToArray();
            }

            return Array.IndexOf(_craftStateRejectTexts, text) >= 0;
        }

        /// <returns>false＝YesAlready 暫停確認框失敗、這一次沒有按（呼叫端稍後再試）；其餘一律 true。</returns>
        internal static bool Repair()
        {
            // 🔴 AtkComponentButton.IsEnabled 解的是 OwnerNode->AtkResNode.NodeFlags，而 FFXIVClientStructs 對 OwnerNode 零空指標檢查；
            // 視窗剛開、元件還沒 setup 完或已被拆除時 OwnerNode 就是空，直接讀就是攔不到的 AccessViolationException。
            // IsComponentEnabled 會把 button 與 OwnerNode 兩層都驗過，任一層為空回 false ⇒ 這一幀不修理。
            // 守衛放在節流之後:被擋只多等一輪節流,不會留下「登記了卻沒按」的紀錄。修理窗按下「全部修理」不會關(它會彈 SelectYesno),
            // 所以是多次互動窗的 15 幀逃生口;真正要擋的是 UseRepair 關窗後(MarkClosing)一次長幀停頓讓下一輪正好落在關閉中的那幾幀。
            if (TryGetAddonByName<AddonRepair>("Repair", out var addon) && addon->AtkUnitBase.IsVisible && IsComponentEnabled(addon->RepairAllButton) && Throttler.Throttle(500))
            {
                // 確認框不讓 YesAlready 在彈出當下搶按（它不看製作狀態，也會和 ConfirmYesNo 按同一扇）；問不到就這次不修。
                var yesAlready = YesAlreadyBotherPause.Pause("SelectYesno", YesAlreadyPauseMs);
                if (yesAlready == YesAlreadyBotherPause.Result.Failed)
                {
                    var now = Environment.TickCount64;
                    if (now >= _nextYesAlreadyFailLogAt)
                    {
                        _nextYesAlreadyFailLogAt = now + 30_000;
                        Svc.Log.Information($"[Artisan][修理] 無法請 YesAlready 暫停確認框（可能和它同時按到同一扇），這次不按「全部修理」，{YesAlreadyFailedRetryMs / 1000} 秒後再試。");
                    }
                    return false;
                }

                if (AddonPressGuard.TryBeginPress("Repair", &addon->AtkUnitBase, "RepairAll", AddonPressGuard.RoutineRePressEscapeFrames))
                {
                    new AddonMaster.Repair((IntPtr)addon).RepairAll();
                    _lastRepairPressAt = Environment.TickCount64;
                    Svc.Log.Information($"[Artisan][修理] 按下「全部修理」（YesAlready：{yesAlready}），確認框由 Artisan 自己按。");
                }
            }
            return true;
        }

        private static bool IsYesnoOpen()
            => TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && addon->AtkUnitBase.IsVisible;

        internal static void ConfirmYesNo()
        {
            if (TryGetAddonByName<AddonRepair>("Repair", out var r) &&
                r->AtkUnitBase.IsVisible && TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) &&
                addon->AtkUnitBase.IsVisible &&
                // IsEnabled 解的是 OwnerNode（AtkComponentBase 的 [0xA8]，和 [0xA0] 的 AtkResNode 是兩個不同欄位）——
                // 只驗 YesButton 本身非空等於檢查了錯的欄位。IsComponentEnabled 驗的是 button != null && OwnerNode
                // != null && IsEnabled，順帶把「讀兩次 YesButton」的 TOCTOU 也消掉。
                IsComponentEnabled(addon->YesButton) &&
                IsNode15Visible(addon) &&
                // 🔴 SelectYesno 按下「是」之後有幾幀「關閉中」仍然 IsVisible 且按鈕可用(Yes() 遇停用鈕還會強制啟用),
                // 外層 _nextRetry 1000ms 是節流不是防護(一次幀停頓就失效)。同一扇窗只按一次,直到它真的收掉。
                AddonPressGuard.TryBeginPress("SelectYesno", &addon->AtkUnitBase))
            {
                new AddonMaster.SelectYesno((IntPtr)addon).Yes();
                _lastRepairPressAt = Environment.TickCount64;
            }
        }

        /// <summary>
        /// SelectYesno 的 NodeList[15]（確認文字節點）是否可見。
        /// </summary>
        /// <remarks>
        /// 🔴 不能寫成 <c>NodeList[15]-&gt;IsVisible()</c> 裸鏈。
        /// <c>UldManager.NodeList</c> 的長度<b>恰為</b> <c>NodeListCount</c>，索引越界讀到的是陣列
        /// 後方的堆積垃圾 —— <b>不是 null</b>，所以「補判空」是半套、擋不住。再把那串垃圾當
        /// <c>AtkResNode*</c> 交給 <c>IsVisible()</c>（那是 <c>[MemberFunction]</c> 原生呼叫）就是
        /// AccessViolationException，而 AVE 是 corrupted-state exception，<c>try/catch</c> 無效。
        /// <para>順序：①容器判空 ②上界檢查 ③節點判空 ④才呼叫原生函式。
        /// 任一層過不了就回 false ⇒ 這一輪不按確認，下一輪節流到期再試。</para>
        /// </remarks>
        private static bool IsNode15Visible(AddonSelectYesno* addon)
        {
            var uld = addon->AtkUnitBase.UldManager;
            if (uld.NodeList == null || uld.NodeListCount <= 15)
                return false;

            var node = uld.NodeList[15];
            return node != null && node->IsVisible();
        }

        /// <summary>換區與剛登入的短暫視窗內背包容器還沒載入，InventoryManager 的各種計數會
        /// 整片回 0 —— 「還沒讀到」與「真的沒有」在呼叫端完全同形，分不出來。
        /// 🔴 只拿它當「讀到 0 之後要做破壞性動作」的閘門，讀不到就這一幀不下結論、下一輪重來。
        /// ⚠️ 刻意不折進 <see cref="CanRepairAny"/>／<see cref="HasDarkMatterOrBetter"/>：
        /// 那會讓「讀不到」變成「假設有暗物質」，是寬鬆方向，會讓流程拿著空氣去修理。</summary>
        internal static bool IsInventoryStateReadable()
        {
            if (!ECommons.GameHelpers.Player.Available) return false;
            if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]) return false;
            var im = InventoryManager.Instance();
            if (im == null) return false;
            InventoryType[] types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
            foreach (var t in types)
            {
                var inv = im->GetInventoryContainer(t);
                if (inv == null || inv->Items == null || inv->Size <= 0) return false;
            }
            return true;
        }

        internal static bool HasDarkMatterOrBetter(uint darkMatterID)
        {
            var repairResources = Svc.Data.Excel.GetSheet<ItemRepairResource>();
            foreach (var dm in repairResources)
            {
                if (dm.Item.RowId < darkMatterID)
                    continue;

                if (InventoryManager.Instance()->GetInventoryItemCount(dm.Item.RowId) > 0)
                    return true;
            }
            return false;
        }

        /// <summary>已裝備容器的安全取得。兩種「讀不到」都回 null：
        /// (a) 換區／剛登入時 <c>GetInventoryContainer</c> 本身就回 null，
        ///     此時 <c>equipment-&gt;Size</c>（偏移 0x14）會直接炸在呼叫端；
        /// (b) 容器物件已存在但 <c>Items</c>（偏移 0x08）尚未配置 —— 這時 <c>Size</c> 可能已非 0，
        ///     而 <c>GetInventorySlot(i)</c> 回的是 <c>Items + i * sizeof(InventoryItem)</c>，
        ///     也就是「非 null 但其實是小偏移」的假指標，會直接通過呼叫端的 <c>item != null</c>。
        /// ⚠️ 拿到 null 的呼叫端各自回「少做事」方向的值，不要回中性值 ——
        /// 讀不到不等於沒事，但寧可少修一輪，也不要拿空氣去做破壞性決定。</summary>
        private static InventoryContainer* GetEquippedContainer()
        {
            var equipment = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            return equipment != null && equipment->Items != null ? equipment : null;
        }

        internal static int GetNPCRepairPrice()
        {
            var output = 0;
            var equipment = GetEquippedContainer();
            // 讀不到時回 int.MaxValue 而不是 0：唯一的決策型呼叫端是
            // 「持有金幣 >= 修理價」這個閘門，回 0 會被讀成「免費」而放行去跟 NPC 互動；
            // 回 MaxValue 則必定不放行（金幣上限 999,999,999 遠小於它）。
            // ⚠️ 這個值也會被設定頁的 help marker 顯示出來，但那只是換區瞬間的暫態，
            //    比起誤觸發 NPC 修理互動，顯示一個怪數字是可接受的取捨。
            if (equipment == null) return int.MaxValue;
            for (var i = 0; i < equipment->Size; i++)
            {
                var item = equipment->GetInventorySlot(i);
                if (item != null && item->ItemId > 0)
                {
                    double actualCond = Math.Round(item->Condition / (float)300, 2);
                    if (actualCond < 100)
                    {
                        var lvl = LuminaSheets.ItemSheet[item->ItemId].LevelEquip;
                        var condDif = (100 - actualCond) / 100;
                        var price = Math.Round(Svc.Data.GetExcelSheet<ItemRepairPrice>().GetRow(lvl).Unknown0 * condDif, 0, MidpointRounding.ToPositiveInfinity);
                        output += (int)price;
                    }
                }
            }

            return output;
        }

        internal static int GetMinEquippedPercent()
        {
            ushort ret = ushort.MaxValue;
            var equipment = GetEquippedContainer();
            // 讀不到時整段跳過迴圈，落到與「身上一件裝備都沒有」完全相同的回傳值（219）。
            // 刻意不另外寫一個 return 常數：那會在日後有人改下面的算式時靜默分岔。
            // 219 遠高於任何 repairPercent（0-100），所以收斂方向是「裝備沒事、不要去修」，
            // 同時避開 CraftingProcessor.OnCraftStarted 的 `== 0` →「你的裝備已損壞」誤判。
            if (equipment != null)
            {
                for (var i = 0; i < equipment->Size; i++)
                {
                    var item = equipment->GetInventorySlot(i);
                    if (item != null && item->ItemId > 0)
                    {
                        if (item->Condition < ret) ret = item->Condition;
                    }
                }
            }
            return (int)Math.Ceiling((double)ret / 300);
        }

        internal static bool CanRepairAny(int repairPercent = 0)
        {
            var equipment = GetEquippedContainer();
            // 讀不到時回 false，與既有的「掃完整排都沒有可修的」同值 —— 收斂方向是「沒東西可修」。
            // ⚠️ 這裡刻意不去分辨「讀不到」與「真的沒有」：破壞性後果（關閉持久模式／暫停清單）
            //    由 ProcessRepair 尾端的 IsInventoryStateReadable 閘門負責擋。
            //    兩者是不同的軸 —— 這裡擋的是解參考，那裡擋的是誤判。
            if (equipment == null) return false;
            for (var i = 0; i < equipment->Size; i++)
            {
                var item = equipment->GetInventorySlot(i);
                if (item != null && item->ItemId > 0)
                {
                    if (CanRepairItem(item->ItemId) && item->Condition / 300 < (repairPercent > 0 ? repairPercent : 100))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static bool CanRepairItem(uint ItemId)
        {
            var item = LuminaSheets.ItemSheet[ItemId];

            if (item.ClassJobRepair.RowId > 0)
            {
                var actualJob = (Job)(item.ClassJobRepair.RowId);
                var repairItem = item.ItemRepair.Value.Item;

                if (!HasDarkMatterOrBetter(repairItem.RowId))
                    return false;

                var jobLevel = CharacterInfo.JobLevel(actualJob);
                if (Math.Max(item.LevelEquip - 10, 1) <= jobLevel)
                    return true;
            }

            return false;
        }

        internal static bool RepairNPCNearby(out IGameObject npc)
        {
            npc = null;
            if (Svc.Objects.LocalPlayer != null)
            {
                foreach (var obj in Svc.Objects.Where(x => x.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                {
                    if (Svc.Data.Excel.GetSheet<ENpcBase>().TryGetRow(obj.BaseId, out var enpcsheet))
                    {
                        if (enpcsheet.ENpcData.Any(x => x.RowId == 720915))
                        {
                            var npcDistance = Vector3.Distance(obj.Position, Svc.Objects.LocalPlayer.Position);
                            if (npcDistance > 7)
                                continue;

                            npc = obj;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        internal static bool RepairWindowOpen()
        {
            if (TryGetAddonByName<AddonRepair>("Repair", out var repairAddon))
                return true;

            return false;
        }
        internal static bool InteractWithRepairNPC()
        {
            if (RepairNPCNearby(out IGameObject npc))
            {
                TargetSystem.Instance()->OpenObjectInteraction(npc.Struct());
                if (TryGetAddonByName<AddonSelectIconString>("SelectIconString", out var addonSelectIconString))
                {
                    var index = GenericHelpers.IndexOf(Svc.Data.Excel.GetSheet<ENpcBase>().GetRow(npc.BaseId).ENpcData, x => x.RowId == 720915);
                    // 選了項目 SelectIconString 就會關;DebugTab 那條路徑把本函式當輪詢任務每幀重跑,同一扇關閉中的選單會被逐幀重按。
                    if (AddonPressGuard.TryBeginPress("SelectIconString", &addonSelectIconString->AtkUnitBase, $"T|{index}"))
                        Callback.Fire(&addonSelectIconString->AtkUnitBase, true, index);
                }

                if (TryGetAddonByName<AddonRepair>("Repair", out var addonRepair))
                {
                    return true;
                }

            }
            return false;
        }

        private static DateTime _nextRetry;

        internal static bool ProcessRepair(NewCraftingList? CraftingList = null)
        {
            int repairPercent = CraftingList != null ? CraftingList.RepairPercent : P.Config.RepairPercent;
            if (GetMinEquippedPercent() >= repairPercent)
            {
                if (TryGetAddonByName<AddonRepair>("Repair", out var r) && r->AtkUnitBase.IsVisible)
                {
                    if (DateTime.Now < _nextRetry) return false;
                    if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39])
                    {
                        if (DebugTab.Debug) Svc.Log.Verbose("Repair visible");
                        if (DebugTab.Debug) Svc.Log.Verbose("Closing repair window");
                        // UseRepair 在窗開著時就是關窗:記下來,之後對這一扇的 RepairAll／SelectYesno 不會落在關閉中那幾幀。
                        AddonPressGuard.MarkClosing("Repair", &r->AtkUnitBase);
                        ActionManagerEx.UseRepair();
                    }
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                    return false;
                }
                return true;
            }

            if (DateTime.Now < _nextRetry) return false;

            if (TryGetAddonByName<AddonRepair>("Repair", out var repairAddon) && repairAddon->AtkUnitBase.IsVisible && repairAddon->RepairAllButton != null)
            {
                // 🔴 這裡是「否定式」判斷，不能直接套 IsComponentEnabled：那個 helper 對空指標回 false，
                // 取反之後反而會把流程送進 UseRepair 分支 —— 等於拿「讀不到」當「按鈕已停用」用。
                // 所以顯式判空，並把指標先取到區域變數再用（避免檢查與解參考之間重取，消 TOCTOU）；
                // 讀不到就照既有節奏排下一次重試，這一幀不做任何動作。
                var repairAllButton = repairAddon->RepairAllButton;
                if (repairAllButton->OwnerNode == null)
                {
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                    return false;
                }

                // 確認框還開著時先把它按掉，不在這時關修理視窗（YesAlready 被暫停，沒有別人會按它）。
                if (!repairAllButton->IsEnabled && !IsYesnoOpen())
                {
                    AddonPressGuard.MarkClosing("Repair", &repairAddon->AtkUnitBase);
                    ActionManagerEx.UseRepair();
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                    return false;
                }

                if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39])
                {
                    // 呼叫端對 false 會先退出製作，旗標清掉之後的下一輪才按。
                    if (InCraftingState())
                    {
                        if (!_waitingForCraftStateLogged)
                        {
                            _waitingForCraftStateLogged = true;
                            Svc.Log.Debug("[Artisan][修理] 製作狀態還沒完全結束，先不按修理。");
                        }
                        _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(300));
                        return false;
                    }
                    _waitingForCraftStateLogged = false;

                    // 確認框開著時只按它，不在同一輪再按一次「全部修理」。
                    if (IsYesnoOpen())
                    {
                        var now = Environment.TickCount64;
                        _yesnoOpenSince ??= now;
                        if (!_yesnoStuckLogged && now - _yesnoOpenSince.Value > 5_000)
                        {
                            _yesnoStuckLogged = true;
                            Svc.Log.Information("[Artisan][修理] 修理確認框開了 5 秒以上還沒按下去（Artisan 按「是」之前的檢查一直沒過）。");
                        }
                        ConfirmYesNo();
                    }
                    else
                    {
                        _yesnoOpenSince = null;
                        _yesnoStuckLogged = false;
                        if (!Repair())
                        {
                            _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(YesAlreadyFailedRetryMs));
                            return false;
                        }
                    }
                }
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            if (P.Config.PrioritizeRepairNPC || !CanRepairAny())
            {
                if (RepairNPCNearby(out var npc) && InventoryManager.Instance()->GetInventoryItemCount(1) >= GetNPCRepairPrice() && !RepairWindowOpen())
                {
                    InteractWithRepairNPC();
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                    return false;
                }
            }

            if (CanRepairAny())
            {
                if (!PreCrafting.Occupied() && !RepairWindowOpen())
                {
                    ActionManagerEx.UseRepair();
                }
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            // 🔴 走到這裡代表上面每一條「能修理」的路都判 false，接下來是破壞性結論：關掉持久模式／暫停製作清單，
            // 兩者都要使用者手動回去重開。但那些判斷全都建立在背包讀數上（暗物質數量、金幣），
            // 而換區與剛登入的短暫視窗內這些讀數會一起假性歸零。所以讀不到就不下這個結論：
            // 回 false 讓呼叫端照既有的延後路徑重試（Endurance/CraftingList 對 false 的處理
            // 就是離開製作並下一輪再來），下一輪讀得到時該關還是會關。
            if (!IsInventoryStateReadable())
            {
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            if (Endurance.Enable && P.Config.DisableEnduranceNoRepair)
            {
                Endurance.NotifyForcedStop("無法修理，停用耐力模式");
                Endurance.ToggleEndurance(false);
                DuoLog.Warning($"Endurance has stopped due to being unable to repair.");
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            if (CraftingListUI.Processing && P.Config.DisableListsNoRepair)
            {
                // 除錯分頁的按鈕也會走到這裡；清單已經暫停就不是新的停止。
                if (!CraftingListFunctions.Paused)
                    Endurance.NotifyForcedStop("無法修理，暫停清單");
                CraftingListFunctions.Paused = true;
                DuoLog.Warning($"List has been paused due to being unable to repair.");
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            return true;
        }
    }
}
