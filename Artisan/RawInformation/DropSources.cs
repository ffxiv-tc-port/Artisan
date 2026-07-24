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

        // Sources used to be populated by a synchronous HttpClient GET running inside this
        // static field's initializer, executed implicitly and synchronously on whatever thread
        // first touched the type. Kick the fetch off in the background on first access instead
        // and return the (initially empty) cached list until it lands.
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

        public DropSources(uint ItemId, List<uint> monsterId)
        {
            ItemId = ItemId;
            MonsterId = monsterId;
            CanObtainFromRetainer = Svc.Data.GetExcelSheet<RetainerTaskNormal>()!.Any(x => x.Item.RowId == ItemId);
            UsedInRecipes = LuminaSheets.RecipeSheet.Values.Any(y => y.Ingredients().Any(x => x.Item.RowId == ItemId));
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
                    foreach (var item in file)
                    {
                        List<uint> monsters = new();
                        foreach (var monster in item.Value)
                        {
                            monsters.Add((uint)monster);
                        }
                        DropSources source = new DropSources(Convert.ToUInt32(item.Key), monsters);
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
