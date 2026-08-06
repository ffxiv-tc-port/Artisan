using Artisan.CraftingLists;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
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

namespace Artisan.Autocraft
{
    internal unsafe class RepairManager
    {
        internal static void Repair()
        {
            // 🔴 AtkComponentButton.IsEnabled 解的是 OwnerNode->AtkResNode.NodeFlags，而 FFXIVClientStructs
            // 對 OwnerNode 零空指標檢查。視窗剛開、元件還沒 setup 完或已被拆除時 OwnerNode 就是空，
            // 直接讀這個屬性等於 AccessViolationException；AVE 是 corrupted-state exception，
            // try/catch 與任何例外隔離包裝都攔不到，只能在讀取前擋。
            // IsComponentEnabled 會把 button 與 OwnerNode 兩層都驗過，任一層為空回 false ⇒ 這一幀不修理。
            if (TryGetAddonByName<AddonRepair>("Repair", out var addon) && addon->AtkUnitBase.IsVisible && IsComponentEnabled(addon->RepairAllButton) && Throttler.Throttle(500))
            {
                new AddonMaster.Repair((IntPtr)addon).RepairAll();
            }
        }

        internal static void ConfirmYesNo()
        {
            if (TryGetAddonByName<AddonRepair>("Repair", out var r) &&
                r->AtkUnitBase.IsVisible && TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) &&
                addon->AtkUnitBase.IsVisible &&
                // 原本只驗了 YesButton 本身非空，但 IsEnabled 解的是 OwnerNode（AtkComponentBase 的
                // [0xA8]，和 [0xA0] 的 AtkResNode 是兩個不同欄位）—— 檢查了錯的欄位，看起來有守衛
                // 其實沒擋到。IsComponentEnabled 是既有 null 檢查的超集（button != null && OwnerNode
                // != null && IsEnabled），順帶把「讀兩次 YesButton」的 TOCTOU 也消掉。
                IsComponentEnabled(addon->YesButton) &&
                addon->AtkUnitBase.UldManager.NodeList[15]->IsVisible())
            {
                new AddonMaster.SelectYesno((IntPtr)addon).Yes();
            }
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
            if (Svc.ClientState.LocalPlayer != null)
            {
                foreach (var obj in Svc.Objects.Where(x => x.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                {
                    if (Svc.Data.Excel.GetSheet<ENpcBase>().TryGetRow(obj.DataId, out var enpcsheet))
                    {
                        if (enpcsheet.ENpcData.Any(x => x.RowId == 720915))
                        {
                            var npcDistance = Vector3.Distance(obj.Position, Svc.ClientState.LocalPlayer.Position);
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
                    var index = GenericHelpers.IndexOf(Svc.Data.Excel.GetSheet<ENpcBase>().GetRow(npc.DataId).ENpcData, x => x.RowId == 720915);
                    Callback.Fire(&addonSelectIconString->AtkUnitBase, true, index);
                }

                if (TryGetAddonByName<AddonRepair>("AddonRepair", out var addonRepair))
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

                if (!repairAllButton->IsEnabled)
                {
                    ActionManagerEx.UseRepair();
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                    return false;
                }

                if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39])
                {
                    ConfirmYesNo();
                    Repair();
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

            // 🔴 走到這裡代表上面每一條「能修理」的路都判 false，接下來是破壞性結論：
            // 關掉持久模式／暫停製作清單，兩者都要使用者手動回去重開。
            // 但那些判斷全都建立在背包讀數上 —— HasDarkMatterOrBetter 走
            // GetInventoryItemCount，NPC 修理那條走金幣讀數 —— 而換區與剛登入的短暫視窗內
            // 這些讀數會一起假性歸零（ICE 實機事故同形狀：使用者身上有 999 個餌，
            // BetweenAreas 那一毫秒讀到 0 就中止流程，觸發源還是完全不相干的機甲行動傳送）。
            // 「這個功能不會切區域」是錯的假設，所以讀不到就不下這個結論：
            // 回 false 讓呼叫端照既有的延後路徑重試（Endurance/CraftingList 對 false 的處理
            // 就是離開製作並下一輪再來），下一輪讀得到時該關還是會關。
            if (!IsInventoryStateReadable())
            {
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            if (Endurance.Enable && P.Config.DisableEnduranceNoRepair)
            {
                Endurance.ToggleEndurance(false);
                DuoLog.Warning($"Endurance has stopped due to being unable to repair.");
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            if (CraftingListUI.Processing && P.Config.DisableListsNoRepair)
            {
                CraftingListFunctions.Paused = true;
                DuoLog.Warning($"List has been paused due to being unable to repair.");
                _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(1000));
                return false;
            }

            return true;
        }
    }
}
