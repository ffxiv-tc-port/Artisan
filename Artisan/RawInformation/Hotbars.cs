using Artisan.CraftingLogic;
using Artisan.GameInterop;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;

namespace Artisan.RawInformation
{
    internal class Hotbars : AtkResNodeFunctions, IDisposable
    {
        private static Skills[] HotBarSkills = new Skills[10 * 12];

        public void Dispose()
        {

        }

        public unsafe Hotbars()
        {
            PopulateHotbarDict();
        }

        public static unsafe void PopulateHotbarDict()
        {
            // GetUIModule() / GetRaptureHotbarModule() 都是原生呼叫,對 null 呼叫即攔不到的 AVE。
            // 取不到時保留上一次的快取內容(不清空),行為與「這次沒更新」相同。
            // 🔴 最外面那層也要判:Framework.Instance() 是 [StaticAddress(isPointer: true)],產生器判空判的是**外層指標槽的位址**,
            //    回傳的卻是**槽裡的內容**,而它在遊戲還沒建好 Framework(這個方法從外掛建構期就會被呼叫)時合法為 null。
            var framework = Framework.Instance();
            if (framework == null)
                return;

            var uiModule = framework->GetUIModule();
            if (uiModule == null)
                return;

            var raptureHotbarModule = uiModule->GetRaptureHotbarModule();
            if (raptureHotbarModule == null)
                return;

            int index = 0;
            foreach (ref var hotbar in raptureHotbarModule->Hotbars.Slice(0, 10))
            {
                foreach (ref var slot in hotbar.Slots.Slice(0, 12))
                {
                    HotBarSkills[index++] = slot.CommandType is HotbarSlotType.Action or HotbarSlotType.CraftAction ? SkillActionMap.ActionToSkill(slot.CommandId) : Skills.None;
                }
            }
        }

        public unsafe static void MakeButtonGlow(int index)
        {
            var hotbar = index / 12;
            var relativeLocation = index % 12;

            // 刻意用純區域變數、每次呼叫都重新解析,不跨幀保存任何原生指標:
            //  ① 存成 static 的話,找不到 addon 時 HotBarRef 變成 null,但 HotBarSlotRef 還留著**上一次**的節點指標;
            //  ② addon 已經 finalize 的話那是懸空指標,DrawOutline 讀它就是 AVE(try/catch 攔不到)。
            var hotBarRef = (AtkUnitBase*)Svc.GameGui
                .GetAddonByName(hotbar == 0 ? "_ActionBar" : $"_ActionBar0{hotbar}", 1).Address;

            if (hotBarRef == null || !hotBarRef->IsVisible)
                return;

            if (hotBarRef->UldManager.LoadedState != AtkLoadState.Loaded)
                return;

            var hotBarSlotRef = hotBarRef->GetNodeById((uint)relativeLocation + 8);
            if (hotBarSlotRef == null)
                return;

            DrawOutline(hotBarSlotRef);
        }

        internal unsafe static void MakeButtonsGlow(Skills rec)
        {
            if (rec == Skills.None || Crafting.CurCraft == null) return;

            if (!Simulator.CanUseAction(Crafting.CurCraft, Crafting.CurStep, CraftingProcessor.NextRec.Action))
                return;

            PopulateHotbarDict();
            for (int i = 0; i < HotBarSkills.Length; ++i)
                if (HotBarSkills[i] == rec)
                    MakeButtonGlow(i);
        }
    }
}
