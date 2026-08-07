using Artisan.RawInformation.Character;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using Action = Lumina.Excel.Sheets.Action;
using Status = Lumina.Excel.Sheets.Status;

namespace Artisan.RawInformation
{
    public class LuminaSheets
    {

        public static Dictionary<uint, Recipe>? RecipeSheet;

        public static ILookup<string, Recipe>? recipeLookup;

        public static Dictionary<uint, GatheringItem>? GatheringItemSheet;

        public static Dictionary<uint, SpearfishingItem>? SpearfishingItemSheet;

        public static Dictionary<uint, GatheringPointBase>? GatheringPointBaseSheet;

        public static Dictionary<uint, FishParameter>? FishParameterSheet;

        public static Dictionary<uint, ClassJob>? ClassJobSheet;

        public static Dictionary<uint, Item>? ItemSheet;

        public static Dictionary<uint, Action>? ActionSheet;

        public static Dictionary<uint, Status>? StatusSheet;

        public static Dictionary<uint, CraftAction>? CraftActions;

        public static Dictionary<uint, RecipeLevelTable>? RecipeLevelTableSheet;

        public static Dictionary<uint, Addon>? AddonSheet;

        public static Dictionary<uint, SpecialShop>? SpecialShopSheet;

        // Item.PriceMid is populated even for items only obtainable via SpecialShop
        // (tribal/GC scrip, item-for-item trades) that no NPC actually sells for gil.
        // Cross-reference GilShopItem to confirm an item is really purchasable with gil.
        public static HashSet<uint>? GilShopItemIds;

        public static Dictionary<uint, LogMessage>? LogMessageSheet;

        public static Dictionary<uint, ItemFood>? ItemFoodSheet;

        public static Dictionary<uint, ENpcResident>? ENPCResidentSheet;

        public static Dictionary<uint, Quest>? QuestSheet;

        public static Dictionary<uint, CompanyCraftPart>? WorkshopPartSheet;

        public static Dictionary<uint, CompanyCraftProcess>? WorkshopProcessSheet;

        public static Dictionary<uint, CompanyCraftSequence>? WorkshopSequenceSheet;

        public static Dictionary<uint, CompanyCraftSupplyItem>? WorkshopSupplyItemSheet;

        public static void Init()
        {
            RecipeSheet = Svc.Data?.GetExcelSheet<Recipe>()?
           .Where(x => x.ItemResult.RowId > 0)
                .DistinctBy(x => x.RowId)
                .OrderBy(x => x.RecipeLevelTable.Value.ClassJobLevel)
                .ThenBy(x => x.ItemResult.Value.Name.ToDalamudString().ToString())
                .ToDictionary(x => x.RowId, x => x);

            // Preprocess the recipe data into a lookup table (ILookup) for faster access.
            recipeLookup = LuminaSheets.RecipeSheet.Values
                .ToLookup(x => x.ItemResult.Value.Name.ToDalamudString().ToString());

            GatheringItemSheet = Svc.Data?.GetExcelSheet<GatheringItem>()?
                .Where(x => x.GatheringItemLevel.Value.GatheringItemLevel > 0)
                .ToDictionary(i => i.RowId, i => i);

            SpearfishingItemSheet = Svc.Data?.GetExcelSheet<SpearfishingItem>()?
                .Where(x => x.GatheringItemLevel.Value.GatheringItemLevel > 0)
                .ToDictionary(i => i.RowId, i => i);

            GatheringPointBaseSheet = Svc.Data?.GetExcelSheet<GatheringPointBase>()?
               .Where(x => x.GatheringLevel > 0)
               .ToDictionary(i => i.RowId, i => i);

            FishParameterSheet = Svc.Data?.GetExcelSheet<FishParameter>()?
                 .Where(x => x.GatheringItemLevel.Value.GatheringItemLevel > 0)
                 .ToDictionary(i => i.RowId, i => i);

            ClassJobSheet = Svc.Data?.GetExcelSheet<ClassJob>()?
                       .ToDictionary(i => i.RowId, i => i);

            ItemSheet = Svc.Data?.GetExcelSheet<Item>()?
                       .ToDictionary(i => i.RowId, i => i);

            ActionSheet = Svc.Data?.GetExcelSheet<Action>()?
                        .ToDictionary(i => i.RowId, i => i);

            StatusSheet = Svc.Data?.GetExcelSheet<Status>()?
                       .ToDictionary(i => i.RowId, i => i);

            CraftActions = Svc.Data?.GetExcelSheet<CraftAction>()?
                       .ToDictionary(i => i.RowId, i => i);

            RecipeLevelTableSheet = Svc.Data?.GetExcelSheet<RecipeLevelTable>()?
                       .ToDictionary(i => i.RowId, i => i);

            AddonSheet = Svc.Data?.GetExcelSheet<Addon>()?
                       .ToDictionary(i => i.RowId, i => i);

            SpecialShopSheet = Svc.Data?.GetExcelSheet<SpecialShop>()?
                       .ToDictionary(i => i.RowId, i => i);

            GilShopItemIds = Svc.Data?.GetSubrowExcelSheet<GilShopItem>()?
                       .SelectMany(row => row)
                       .Select(i => i.Item.RowId)
                       .ToHashSet();

            LogMessageSheet = Svc.Data?.GetExcelSheet<LogMessage>()?
                       .ToDictionary(i => i.RowId, i => i);

            ItemFoodSheet = Svc.Data?.GetExcelSheet<ItemFood>()?
                       .ToDictionary(i => i.RowId, i => i);

            ENPCResidentSheet = Svc.Data?.GetExcelSheet<ENpcResident>()?
                       .Where(x => x.Singular.ExtractText().Length > 0)
                       .ToDictionary(i => i.RowId, i => i);

            QuestSheet = Svc.Data?.GetExcelSheet<Quest>()?
                        .Where(x => x.Id.ExtractText().Length > 0)
                        .ToDictionary(i => i.RowId, i => i);

            WorkshopPartSheet = Svc.Data?.GetExcelSheet<CompanyCraftPart>()?
                       .ToDictionary(i => i.RowId, i => i);

            WorkshopProcessSheet = Svc.Data?.GetExcelSheet<CompanyCraftProcess>()?
                       .ToDictionary(i => i.RowId, i => i);

            WorkshopSequenceSheet = Svc.Data?.GetExcelSheet<CompanyCraftSequence>()?
                       .ToDictionary(i => i.RowId, i => i);

            WorkshopSupplyItemSheet = Svc.Data?.GetExcelSheet<CompanyCraftSupplyItem>()?
                       .ToDictionary(i => i.RowId, i => i);

            Svc.Log.Debug("Lumina sheets initialized");
        }

        public static void Dispose()
        {
            var type = typeof(LuminaSheets);
            foreach (var prop in type.GetFields(System.Reflection.BindingFlags.Static))
            {
                prop.SetValue(null, null);
            }
        }
    }

    public static class SheetExtensions
    {
        public static string NameOfAction(this Skills skill, bool raphParseEn = false)
        {
            if (skill == Skills.TouchCombo) return "Touch Combo";
            if (skill == Skills.TouchComboRefined) return "Touch Combo (Refined Touch Route)";
            var id = skill.ActionId(ECommons.ExcelServices.Job.CRP);
            return id == 0 ? "Artisan Recommendation" : id < 100000 ? Svc.Data.GetExcelSheet<Action>(raphParseEn ? Dalamud.Game.ClientLanguage.English : Svc.ClientState.ClientLanguage)[id].Name.ToString() : Svc.Data.GetExcelSheet<CraftAction>(raphParseEn ? Dalamud.Game.ClientLanguage.English : Svc.ClientState.ClientLanguage)[id].Name.ToString();
        }

        public static ushort IconOfAction(this Skills skill, ECommons.ExcelServices.Job job)
        {
            var id = skill.ActionId(job);
            return id == 0 ? default : id < 100000 ? LuminaSheets.ActionSheet[id].Icon : LuminaSheets.CraftActions[id].Icon;
        }

        public static int StandardCPCost(this Skills skill)
        {
            var id = skill.ActionId(ECommons.ExcelServices.Job.CRP);
            return id == 0 ? 0 : id < 100000 ? LuminaSheets.ActionSheet[id].PrimaryCostValue : LuminaSheets.CraftActions[id].Cost;
        }

        public static string GetSkillDescription(this Skills skill)
        {
            var id = skill.ActionId(ECommons.ExcelServices.Job.CRP);
            string description = id == 0 ? "" : id < 100000 ? Svc.Data.Excel.GetSheet<ActionTransient>().GetRow(id).Description.ToDalamudString().ToString() : LuminaSheets.CraftActions[id].Description.ToDalamudString().ToString();
            description = skill switch
            {
                Skills.BasicSynthesis => description.Replace($": %", $": 100%/120%").Replace($"効率：", $"効率：100/120").Replace($"Effizienz: ", $"Effizienz: 100/120"),
                Skills.CarefulSynthesis => description.Replace($": %", $": 150%/180%").Replace($"効率：", $"効率：150/180").Replace($"Effizienz: ", $"Effizienz: 150/180"),
                Skills.RapidSynthesis => description.Replace($": %", $": 250%/500%").Replace($"効率：", $"効率：250/500").Replace($"Effizienz: ", "Effizienz: 250/500"),
                Skills.Groundwork => description.Replace($": %", $": 300%/360%").Replace($"効率：", $"効率：300/360").Replace("Effizienz: ", "Effizienz: 300/360"),
                _ => description
            };
            return description;
        }
        public static string NameOfBuff(this ushort id)
        {
            if (id == 0) return "";

            return LuminaSheets.StatusSheet[id].Name.ToString();
        }

        public static string NameOfItem(this uint id)
        {
            if (id == 0) return "";

            return LuminaSheets.ItemSheet[id].Name.ExtractText();
        }

        public static string NameOfRecipe(this uint id)
        {
            if (id == 0) return "";
            if (!LuminaSheets.RecipeSheet.ContainsKey(id))
                return "";

            return LuminaSheets.RecipeSheet[id].ItemResult.Value.Name.ToDalamudString().ToString();
        }

        public static string NameOfQuest(this ushort id)
        {
            if (id == 9998 || id == 9999)
                id = 1493;

            if (id > 0)
            {
                var digits = id.ToString().Length;
                if (LuminaSheets.QuestSheet!.Any(x => Convert.ToInt16(x.Value.Id.ToString().GetLast(digits)) == id))
                {
                    return LuminaSheets.QuestSheet!.First(x => Convert.ToInt16(x.Value.Id.ToString().GetLast(digits)) == id).Value.Name.ExtractText().Replace("", "").Trim();
                }
            }
            return "";

        }

        public static bool MissionHasMaterialMiracle(this Recipe recipe) => recipe.MissionMaterialMiracle().Has;

        /// <summary>
        /// 這個配方所屬的宇宙探索任務有沒有奇蹟之材,以及**給幾次**。
        /// </summary>
        /// <remarks>
        /// 🔑 <c>Charges</c> 讀的是 <c>WKSMissionToDo.Unknown14</c>。2026-08-07 用使用者實機 log 做過
        /// **四點雙向校準**(對照的是 Artisan 自己印在 log 裡的 <c>MaterialMiracleCharges</c>,
        /// 那個數字來自 <c>DutyActionManager</c>,是遊戲的真值):
        /// <list type="bullet">
        /// <item>任務 31【高難+】補充優質製作工具(配方 36205/36206):Unknown14=1 ↔ 實機 1</item>
        /// <item>任務 38【高難】製作休息設施所需的材料(配方 36214):Unknown14=3 ↔ 實機 3</item>
        /// <item>任務 32/40(沒有奇蹟之材):Unknown14=0 ↔ 實機 0</item>
        /// </list>
        /// ⚠️ <c>Unknown15</c> 曾是候選,但它在**完全沒有奇蹟之材**的任務上也恆為 3,已排除。
        /// 🔴 回傳值取 <c>Max(1, …)</c> 是刻意的 fail-safe:欄位對應萬一是錯的,最壞也只是退回
        /// 改動前寫死的 1,不會變成 0(那等於奇蹟之材在模擬器裡整個消失,而且是靜默的)。
        /// 📌 實機那條路**不吃這個值** —— <c>Crafting.MaterialMiracleCharges()</c> 直接問
        /// <c>DutyActionManager</c>。這裡只影響模擬器/前瞻,所以就算讀錯也不會改變實機行為。
        /// </remarks>
        public static (bool Has, uint Charges) MissionMaterialMiracle(this Recipe recipe)
        {
            try
            {

                Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch = false;
                var id = recipe.RowId;
                //First, find the MissionRecipe with our recipe
                var missionRec = Svc.Data.GetExcelSheet<WKSMissionRecipe>().FirstOrDefault(missionRec => missionRec.Recipe.Any(recipe =>  recipe.RowId == id));
                //Bail if there's no MissionRecipe (this isn't a Cosmic Craft)
                if (missionRec.RowId == 0)
                    return (false, 0);

                //Next, find the MissionUnit that has our MissionRecipe row
                var missionUnit = Svc.Data.GetExcelSheet<WKSMissionUnit>().First(missionUnit => missionUnit.WKSMissionRecipe.RowId == missionRec.RowId);

                //Get the MissionToDo from the MissionUnit
                // Lumina's WKSMissionUnit schema now exposes this directly as a
                // MissionToDo row-ref collection instead of the old scalar
                // "Unknown7" field id; take the first entry (matches prior
                // single-value lookup behavior).
                var missionToDo = missionUnit.MissionToDo[0].Value;

                //Svc.Log.Verbose($"{id} -> {missionRec.RowId} -> {missionUnit.RowId} -> {missionToDo.RowId} -> {missionToDo.Unknown0}");
                if (missionToDo.Unknown0 != (uint)Skills.MaterialMiracle)
                    return (false, 0);

                return (true, Math.Max(1u, (uint)missionToDo.Unknown14));
            }
            catch (Exception e)
            {
                Svc.Log.Error($"Error in MissionMaterialMiracle: {e}");
                return (false, 0);
            }
        }
    }
}
