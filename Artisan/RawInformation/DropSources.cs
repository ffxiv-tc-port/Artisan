using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Artisan.RawInformation
{
    public class DropSources
    {
        private static List<DropSources> _sources = new();
        private static bool checkStarted = false;

        // Fetched in the background on first access; returns the (initially empty) cached list until
        // it lands. A synchronous GET in a static initializer would block whatever thread first
        // touched the type.
        public static List<DropSources> Sources
        {
            get
            {
                if (!checkStarted)
                {
                    checkStarted = true;
                    Task.Run(FetchDropList);
                }

                return _sources;
            }
        }

        public DropSources(uint itemId, List<uint> monsterId)
        {
            ItemId = itemId;
            MonsterId = monsterId;
            CanObtainFromRetainer = Svc.Data.GetExcelSheet<RetainerTaskNormal>()!.Any(x => x.Item.RowId == itemId);
            UsedInRecipes = LuminaSheets.RecipeSheet.Values.Any(y => y.Ingredients().Any(x => x.Item.RowId == itemId));
        }

        // 與上面那支同值，但兩張表由呼叫端整批各走一次，不必每個道具都整表重掃配方。
        private DropSources(uint itemId, List<uint> monsterId, HashSet<uint> retainerTaskItems, HashSet<uint> recipeIngredientItems)
        {
            ItemId = itemId;
            MonsterId = monsterId;
            CanObtainFromRetainer = retainerTaskItems.Contains(itemId);
            UsedInRecipes = recipeIngredientItems.Contains(itemId);
        }

        /// <summary>僱員探險（一般）任務的道具 id；與 <c>RetainerTaskNormal.Any(x =&gt; x.Item.RowId == id)</c> 同值。</summary>
        internal static HashSet<uint> RetainerTaskItemIds()
        {
            var ids = new HashSet<uint>();
            foreach (var x in Svc.Data.GetExcelSheet<RetainerTaskNormal>()!)
                ids.Add(x.Item.RowId);
            return ids;
        }

        /// <summary>任一配方的材料道具 id；與 <c>RecipeSheet.Values.Any(y =&gt; y.Ingredients().Any(x =&gt; x.Item.RowId == id))</c> 同值。</summary>
        internal static HashSet<uint> RecipeIngredientItemIds()
        {
            var ids = new HashSet<uint>();
            foreach (var recipe in LuminaSheets.RecipeSheet.Values)
                foreach (var ing in recipe.Ingredients())
                    ids.Add(ing.Item.RowId);
            return ids;
        }

        public bool CanObtainFromRetainer { get; set; }
        public uint ItemId { get; set; }

        public List<uint> MonsterId { get; set; }
        public bool UsedInRecipes { get; set; }

        private static void FetchDropList()
        {
            List<DropSources> output = new();
            try
            {
                using HttpResponseMessage? sources = new HttpClient().GetAsync("https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/master/libs/data/src/lib/json/drop-sources.json").Result;
                sources.EnsureSuccessStatusCode();
                string? data = sources.Content.ReadAsStringAsync().Result;

                if (data != null)
                {
                    JObject? file = JsonConvert.DeserializeObject<JObject>(data);
                    HashSet<uint>? retainerTaskItems = null, recipeIngredientItems = null;
                    foreach (var item in file)
                    {
                        List<uint> monsters = new();
                        foreach (var monster in item.Value)
                        {
                            monsters.Add((uint)monster);
                        }
                        var itemId = Convert.ToUInt32(item.Key);
                        retainerTaskItems ??= RetainerTaskItemIds();
                        recipeIngredientItems ??= RecipeIngredientItemIds();
                        DropSources source = new DropSources(itemId, monsters, retainerTaskItems, recipeIngredientItems);
                        if (source.UsedInRecipes && !source.CanObtainFromRetainer)
                            output.Add(source);
                    }
                }

                _sources = output;
            }
            catch (Exception ex)
            {
            }
        }
    }
}
