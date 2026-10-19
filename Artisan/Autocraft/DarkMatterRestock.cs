using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.Autocraft
{
    /// <summary>
    /// 清單「從僱員補貨」時，一併補上這張清單修理要用的黑暗物質。
    /// </summary>
    /// <remarks>
    /// 契約：只在清單開了自動修理時動作；背包／裝備讀不到、或僱員身上也沒有時什麼都不加（沿用原本行為）。
    /// 取回走既有補貨鏈，直取是整格、視窗路徑在背包有空位時拿整疊 ⇒ 實際到手可能多於估計量。
    /// </remarks>
    internal static class DarkMatterRestock
    {
        /// <summary>每次製作讓裝備耐久掉的百分點（估計值，刻意偏高：寧可多拿幾個，也不要清單半路停下）。</summary>
        private const double WearPercentPerCraft = 0.25;

        /// <summary>一次補貨最多要這麼多（一疊）；超長清單分次補，不讓一次補貨佔掉好幾格背包。</summary>
        private const int MaxPerRestock = 999;

        private const string Endpoint = "DarkMatterRestock.ReadPlan";

        /// <summary>主執行緒上讀好的純量快照。</summary>
        internal sealed class Plan
        {
            /// <summary>需要的最低等級（ItemRepairResource 的道具 id）；0＝清單用到的裝備沒有能自己修的。</summary>
            public uint RequiredGrade { get; }
            public int RepairEvents { get; }
            public int Needed { get; }
            /// <summary>身上等級足夠的黑暗物質總數（各級加總）。</summary>
            public int Owned { get; }
            /// <summary>等級足夠的黑暗物質道具 id，由低到高。</summary>
            public uint[] Grades { get; }

            internal Plan(uint requiredGrade, int repairEvents, int needed, int owned, uint[] grades)
            {
                RequiredGrade = requiredGrade;
                RepairEvents = repairEvents;
                Needed = needed;
                Owned = owned;
                Grades = grades;
            }
        }

        /// <summary>
        /// 算缺多少、向僱員要，並把要取回的量併進 <paramref name="requiredItems"/>。
        /// 跑在補貨按鈕的背景執行緒上，與同一支裡的材料計算同一條執行緒。
        /// </summary>
        internal static void AddToRestock(NewCraftingList list, Dictionary<int, int> requiredItems, RetainerInfo.RetainerRoster? roster)
        {
            if (!list.Repair) return;

            var plan = IpcFrameworkGate.Get<Plan?>(Endpoint, () => ReadPlanCore(list), null);
            if (plan is null)
            {
                Svc.Log.Information("[Artisan][Restock] 黑暗物質：背包或裝備現在讀不到，這次不補。");
                return;
            }

            if (plan.RequiredGrade == 0)
            {
                Svc.Log.Information("[Artisan][Restock] 黑暗物質：清單沒有要做的，或用到的裝備沒有能自己修的，不補。");
                return;
            }

            var target = Math.Min(plan.Needed, MaxPerRestock);
            var summary = $"[Artisan][Restock] 黑暗物質：清單預估修理 {plan.RepairEvents} 次、需要 {plan.Needed} 個" +
                          $"（{NameOf(plan.RequiredGrade)} 以上，這次最多補到 {target}），身上 {plan.Owned} 個";
            if (target <= plan.Owned)
            {
                Svc.Log.Information(summary + "，不必補。");
                return;
            }

            var shortfall = target - plan.Owned;
            var picked = new List<string>();
            foreach (var grade in plan.Grades)
            {
                if (shortfall <= 0) break;
                var held = RetainerInfo.GetRetainerItemCount(grade, roster: roster);
                if (held <= 0) continue;
                var take = Math.Min(shortfall, held);
                requiredItems[(int)grade] = requiredItems.GetValueOrDefault((int)grade) + take;
                shortfall -= take;
                picked.Add($"{NameOf(grade)}×{take}（僱員共 {held}）");
            }

            Svc.Log.Information(summary + "；" +
                                (picked.Count == 0
                                    ? "僱員身上也沒有，不補。"
                                    : $"從僱員補 {string.Join("、", picked)}" +
                                      (shortfall > 0 ? $"，仍差 {shortfall} 個。" : "。")));
        }

        private static string NameOf(uint itemId)
            => LuminaSheets.ItemSheet is not null && LuminaSheets.ItemSheet.TryGetValue(itemId, out var item)
                ? $"{item.Name}({itemId})"
                : itemId.ToString();

        /// <summary><b>只能在遊戲主執行緒上呼叫</b>。讀不到時回 null。</summary>
        private static unsafe Plan? ReadPlanCore(NewCraftingList list)
        {
            if (!RepairManager.IsInventoryStateReadable()) return null;
            var player = Svc.Objects.LocalPlayer;
            if (player is null) return null;
            var im = InventoryManager.Instance();
            if (im == null) return null;

            var currentJob = (Job)player.ClassJob.RowId;
            var craftsByJob = new Dictionary<Job, int>();
            foreach (var entry in list.Recipes.ToArray())
            {
                if (entry.ListItemOptions?.Skipping == true || entry.Quantity <= 0) continue;
                if (LuminaSheets.RecipeSheet is null || !LuminaSheets.RecipeSheet.TryGetValue(entry.ID, out var recipe)) continue;
                var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
                craftsByJob[job] = craftsByJob.GetValueOrDefault(job) + entry.Quantity;
            }

            uint requiredGrade = 0;
            var events = 0;
            var needed = 0;
            var gearsets = RaptureGearsetModule.Instance();
            var pieces = new List<uint>();
            foreach (var (job, crafts) in craftsByJob)
            {
                pieces.Clear();
                // -1＝不知道現在耐久（沒穿在身上的裝備組），當成一開始就要修一次。
                int startPercent;
                if (job == currentJob)
                {
                    var equipped = im->GetInventoryContainer(InventoryType.EquippedItems);
                    if (equipped == null || equipped->Items == null) return null;
                    for (var i = 0; i < equipped->Size; i++)
                    {
                        var slot = equipped->GetInventorySlot(i);
                        if (slot != null && slot->ItemId > 0) pieces.Add(slot->ItemId);
                    }
                    startPercent = RepairManager.GetMinEquippedPercent();
                }
                else
                {
                    // 與 PreCrafting.TaskClassChange 挑同一組：第一個有效且職業相符的裝備組。
                    if (gearsets == null) continue;
                    var found = false;
                    foreach (ref var gs in gearsets->Entries)
                    {
                        if (!gearsets->IsValidGearset(gs.Id) || (Job)gs.ClassJob != job) continue;
                        for (var i = 0; i < gs.Items.Length; i++)
                        {
                            var id = gs.Items[i].ItemId % 1000000;
                            if (id > 0) pieces.Add(id);
                        }
                        found = true;
                        break;
                    }
                    if (!found) continue;
                    startPercent = -1;
                }

                var repairable = 0;
                foreach (var id in pieces)
                {
                    var grade = SelfRepairGrade(id);
                    if (grade == 0) continue;
                    repairable++;
                    requiredGrade = Math.Max(requiredGrade, grade);
                }
                if (repairable == 0) continue;

                var e = EstimateRepairs(crafts, startPercent, list.RepairPercent);
                events += e;
                needed += e * repairable;
            }

            if (requiredGrade == 0) return new Plan(0, 0, 0, 0, []);

            var grades = Svc.Data.Excel.GetSheet<ItemRepairResource>()
                .Select(x => x.Item.RowId)
                .Where(id => id != 0 && id >= requiredGrade)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var owned = 0;
            foreach (var g in grades)
                owned += im->GetInventoryItemCount(g);

            return new Plan(requiredGrade, events, needed, owned, grades);
        }

        /// <summary>
        /// 這件裝備自己修要用的黑暗物質道具 id；不能自己修（沒有修理職業或等級不夠）回 0。
        /// 判準與 <see cref="RepairManager.CanRepairItem"/> 相同，只是不看身上有沒有黑暗物質。
        /// </summary>
        private static uint SelfRepairGrade(uint itemId)
        {
            if (LuminaSheets.ItemSheet is null || !LuminaSheets.ItemSheet.TryGetValue(itemId, out var item)) return 0;
            if (item.ClassJobRepair.RowId == 0) return 0;
            var grade = item.ItemRepair.ValueNullable?.Item.RowId ?? 0;
            if (grade == 0) return 0;
            if (Math.Max(item.LevelEquip - 10, 1) > CharacterInfo.JobLevel((Job)item.ClassJobRepair.RowId)) return 0;
            return grade;
        }

        /// <summary>
        /// 做 <paramref name="crafts"/> 次會觸發幾次修理。修理門檻是「最低耐久低於 <paramref name="repairPercent"/>」，
        /// 修完回 100%。<paramref name="startPercent"/> 為 -1 時當作一開始就要修。
        /// </summary>
        private static int EstimateRepairs(int crafts, int startPercent, int repairPercent)
        {
            if (crafts <= 0) return 0;
            var wear = crafts * WearPercentPerCraft;
            // 「低於門檻」是看無條件進位後的百分比 ⇒ 從 100% 掉到觸發要掉 101 - 門檻 個百分點。
            var headroom = startPercent < 0 ? 0 : Math.Max(0, startPercent - repairPercent + 1);
            if (wear < headroom) return 0;
            var cycle = Math.Max(1, 101 - repairPercent);
            return 1 + (int)((wear - headroom) / cycle);
        }
    }
}
