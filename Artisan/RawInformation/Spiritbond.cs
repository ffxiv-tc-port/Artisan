using Artisan.GameInterop;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static ECommons.GenericHelpers;

namespace Artisan.RawInformation
{
    public unsafe static class Spiritbond
    {
        /// <summary>已裝備欄位的精魂值安全讀取。讀不到一律回 0。
        /// 擋三件事：
        /// (a) 換區／剛登入時 <c>GetInventoryContainer</c> 直接回 null，<c>-&gt;Items</c>（偏移 0x08）會炸；
        /// (b) 容器已存在但 <c>Items</c> 尚未配置 —— 此時 <c>Size</c> 可能已非 0，
        ///     而 <c>Items[slot]</c> 會從「null + slot * 0x48」這個小偏移假位址讀出垃圾精魂值；
        /// (c) 容器只載入一半、<c>Size</c> 還沒到 13 —— 原本的寫法對 slot 完全不設防。
        /// ⚠️ 回 0 是刻意的收斂方向：唯一的決策型消費者是 <see cref="IsSpiritbondReadyAny"/> 的
        ///    <c>== 10000</c> 比較，0 會讓它回 false，也就是「沒有東西可以抽魔晶石」而少做事。
        ///    回一個垃圾值則可能剛好命中 10000，把流程推去開精製介面對空氣操作。
        ///    0 與「該欄位真的沒裝備」同值，語意上沒有損失（空欄位本來就讀 0）。</summary>
        private static ushort GetEquippedSpiritbond(int slot)
        {
            var equipment = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipment == null || equipment->Items == null) return 0;
            if (slot < 0 || slot >= equipment->Size) return 0;
            return equipment->Items[slot].SpiritbondOrCollectability;
        }

        public static ushort Weapon { get => GetEquippedSpiritbond(0); }

        public static ushort Offhand { get => GetEquippedSpiritbond(1); }

        public static ushort Helm { get => GetEquippedSpiritbond(2); }

        public static ushort Body { get => GetEquippedSpiritbond(3); }

        public static ushort Hands { get => GetEquippedSpiritbond(4); }

        public static ushort Legs { get => GetEquippedSpiritbond(6); }

        public static ushort Feet { get => GetEquippedSpiritbond(7); }

        public static ushort Earring { get => GetEquippedSpiritbond(8); }

        public static ushort Neck { get => GetEquippedSpiritbond(9); }

        public static ushort Wrist { get => GetEquippedSpiritbond(10); }

        public static ushort Ring1 { get => GetEquippedSpiritbond(11); }

        public static ushort Ring2 { get => GetEquippedSpiritbond(12); }

        public static bool IsSpiritbondReadyAny()
        {
            if (Weapon == 10000) return true;
            if (Offhand == 10000) return true;
            if (Helm == 10000) return true;
            if (Body == 10000) return true;
            if (Hands == 10000) return true;
            if (Legs == 10000) return true;
            if (Feet == 10000) return true;
            if (Earring == 10000) return true;
            if (Neck == 10000) return true;
            if (Wrist == 10000) return true;
            if (Ring1 == 10000) return true;
            if (Ring2 == 10000) return true;

            return false;
        }

        public static bool IsMateriaMenuOpen() => Svc.GameGui.GetAddonByName("Materialize", 1) != IntPtr.Zero;

        public static bool IsMateriaMenuDialogOpen() => Svc.GameGui.GetAddonByName("MaterializeDialog", 1) != IntPtr.Zero;
        public unsafe static void OpenMateriaMenu()
        {
            if (Svc.GameGui.GetAddonByName("Materialize", 1) == IntPtr.Zero)
            {
                ActionManagerEx.UseMateriaExtraction();
            }
        }

        public unsafe static void CloseMateriaMenu()
        {
            if (Svc.GameGui.GetAddonByName("Materialize", 1) != IntPtr.Zero)
            {
                ActionManagerEx.UseMateriaExtraction();
            }
        }

        public unsafe static void ConfirmMateriaDialog()
        {
            try
            {
                var materializePTR = Svc.GameGui.GetAddonByName("MaterializeDialog", 1);
                if (materializePTR == IntPtr.Zero)
                    return;

                var materalizeWindow = (AtkUnitBase*)materializePTR.Address;
                if (materalizeWindow == null)
                    return;

                new AddonMaster.MaterializeDialog(materializePTR).Materialize();
            }
            catch
            {

            }
        }

        private static DateTime _nextRetry;

        public unsafe static bool ExtractMateriaTask(bool option)
        {
            if (!CharacterInfo.MateriaExtractionUnlocked()) return true;
            if (CharacterOther.GetInventoryFreeSlotCount() == 0) return true;

            if (option)
            {
                if (IsMateriaMenuOpen() && !IsSpiritbondReadyAny())
                {
                    if (DateTime.Now < _nextRetry) return false;
                    CloseMateriaMenu();
                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(500));
                    return false;
                }

                if (IsSpiritbondReadyAny())
                {
                    if (DateTime.Now < _nextRetry) return false;
                    if (!IsMateriaMenuOpen())
                    {
                        OpenMateriaMenu();
                        _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(500));
                        return false;
                    }

                    if (IsMateriaMenuOpen() && !PreCrafting.Occupied())
                    {
                        ExtractFirstMateria();
                        _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(500));
                        return false;
                    }

                    _nextRetry = DateTime.Now.Add(TimeSpan.FromMilliseconds(500));
                    return false;
                }
            }

            return true;
        }

        public unsafe static void ExtractFirstMateria()
        {
            try
            {
                if (IsSpiritbondReadyAny())
                {
                    if (IsMateriaMenuDialogOpen())
                    {
                        ConfirmMateriaDialog();
                    }
                    else
                    {
                        var materializePTR = Svc.GameGui.GetAddonByName("Materialize", 1);
                        if (materializePTR == IntPtr.Zero)
                            return;

                        var materalizeWindow = (AtkUnitBase*)materializePTR.Address;
                        if (materalizeWindow == null)
                            return;

                        var list = (AtkComponentList*)materalizeWindow->UldManager.NodeList[5];

                        var values = stackalloc AtkValue[2];
                        values[0] = new()
                        {
                            Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                            Int = 2,
                        };
                        values[1] = new()
                        {
                            Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt,
                            UInt = 0,
                        };

                        materalizeWindow->FireCallback(1, values);



                    }
                }


            }
            catch (Exception e)
            {
                e.Log();
            }
        }
    }
}
