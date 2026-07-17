using ECommons;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Artisan.Universalis
{
    internal class UniversalisClient
    {
        private const string Endpoint = "https://universalis.app/api/v2/";
        private readonly HttpClient httpClient;
        public uint? PlayerWorld;

        // Live-fetched from Universalis itself instead of a hardcoded world-ID table, so any
        // region it tracks (including e.g. the Traditional Chinese "繁中服" DC) resolves correctly.
        private static List<(string Name, string Region, uint[] Worlds)>? cachedDataCenters;
        private static DateTime dataCentersCacheExpiry = DateTime.MinValue;

        public UniversalisClient()
        {
            this.httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(10000),
            };
        }

        public MarketboardData? GetMarketBoard(string region, ulong ItemId)
        {
            var marketBoardFromAPI = this.GetMarketBoardData(region, ItemId);
            return marketBoardFromAPI;
        }

        private List<(string Name, string Region, uint[] Worlds)> GetDataCenters()
        {
            if (cachedDataCenters != null && DateTime.Now < dataCentersCacheExpiry)
                return cachedDataCenters;

            try
            {
                var json = httpClient.GetStringAsync(Endpoint + "data-centers").Result;
                var parsed = JsonConvert.DeserializeObject<List<dynamic>>(json);
                cachedDataCenters = parsed!.Select(d => (
                    (string)d.name,
                    (string)d.region,
                    ((IEnumerable<dynamic>)d.worlds).Select(w => (uint)w).ToArray()
                )).ToList();
                dataCentersCacheExpiry = DateTime.Now.AddHours(12);
            }
            catch (Exception ex)
            {
                ex.Log();
                cachedDataCenters ??= new();
            }

            return cachedDataCenters;
        }

        public bool IsWorldKnown(uint world) => GetDataCenters().Any(d => d.Worlds.Contains(world));

        public MarketboardData? GetRegionData(ulong ItemId, ref MarketboardData output)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            var region = GetDataCenters().FirstOrDefault(d => d.Worlds.Contains(world.Value)).Region;
            if (region == null)
                return null;

            return output = GetMarketBoard(region, ItemId);
        }

        public MarketboardData? GetDCData(ulong ItemId, ref MarketboardData output)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            // Universalis' data-centers response has a known encoding bug for some CJK DC
            // names (e.g. the Traditional Chinese DC), so a DC name string isn't reliable.
            // Querying by the raw numeric world ID always works and is narrower than a DC query anyway.
            return output = GetMarketBoard(world.Value.ToString(), ItemId);
        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }

        private MarketboardData? GetMarketBoardData(string region, ulong ItemId)
        {
            HttpResponseMessage result;
            try
            {
                result = this.GetMarketBoardDataAsync(region, ItemId).Result;
            }
            catch (Exception ex)
            {
                ex.Log();
                return null;
            }


            if (result.StatusCode != HttpStatusCode.OK)
            {
                Svc.Log.Error(
                    "Failed to retrieve data from Universalis for ItemId {0} / worldId {1} with HttpStatusCode {2}.",
                    ItemId,
                    region,
                    result.StatusCode);
                return null;
            }

            var json = JsonConvert.DeserializeObject<dynamic>(result.Content.ReadAsStringAsync().Result);
            if (json == null)
            {
                Svc.Log.Error(
                    "Failed to deserialize Universalis response for ItemId {0} / worldId {1}.",
                    ItemId,
                    region);
                return null;
            }

            try
            {
                var marketBoardData = new MarketboardData
                {
                    LastCheckTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    LastUploadTime = json.lastUploadTime?.Value,
                    AveragePriceNQ = json.averagePriceNQ?.Value,
                    AveragePriceHQ = json.averagePriceHQ?.Value,
                    CurrentAveragePriceNQ = json.currentAveragePriceNQ?.Value,
                    CurrentAveragePriceHQ = json.currentAveragePriceHQ?.Value,
                    MinimumPriceNQ = json.minPriceNQ?.Value,
                    MinimumPriceHQ = json.minPriceHQ?.Value,
                    MaximumPriceNQ = json.maxPriceNQ?.Value,
                    MaximumPriceHQ = json.maxPriceHQ?.Value,
                    TotalNumberOfListings = json.listingsCount?.Value,
                    TotalQuantityOfUnits = json.unitsForSale?.Value
                };
                if (json.listings.Count > 0)
                {
                    foreach (var item in json.listings)
                    {
                        Listing listing = new()
                        {
                            World = item.worldName.Value,
                            Quantity = item.quantity.Value,
                            TotalPrice = item.total.Value,
                            UnitPrice = item.pricePerUnit.Value
                        };

                        if (listing.World != "Cloudtest01" && listing.World != "Cloudtest02")
                            marketBoardData.AllListings.Add(listing);
                    }

                    marketBoardData.CurrentMinimumPrice = marketBoardData.AllListings.First().TotalPrice;
                    marketBoardData.LowestWorld = marketBoardData.AllListings.First().World;
                    marketBoardData.ListingQuantity = marketBoardData.AllListings.First().Quantity;
                }

                return marketBoardData;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(
                    ex,
                    "Failed to parse marketBoard data for ItemId {0} / worldId {1}.",
                    ItemId,
                    region);
                return null;
            }
        }

        private async Task<HttpResponseMessage> GetMarketBoardDataAsync(string? worldId, ulong ItemId)
        {
            var request = Endpoint + worldId + "/" + ItemId;
            Svc.Log.Debug($"universalisRequest={request}");
            return await this.httpClient.GetAsync(new Uri(request));
        }
    }

    // Shared by every "获取价格" button (per-row and "一键全搜索" bulk buttons alike) so the
    // region/DC-known check and Universalis call only live in one place.
    public static class MarketboardFetch
    {
        public static void Fetch(uint itemId, Action onFailed, Action<MarketboardData?> onComplete)
        {
            var world = Svc.ClientState.LocalPlayer?.CurrentWorld.RowId;
            P.UniversalsisClient.PlayerWorld = world;
            Task.Run(() =>
            {
                // DC-limited mode queries by raw world ID, which always resolves;
                // region mode needs the world to be one Universalis actually tracks.
                if (world == null || (!P.Config.LimitUnversalisToDC && !P.UniversalsisClient.IsWorldKnown(world.Value)))
                {
                    onFailed();
                    return;
                }

                MarketboardData? data = null;
                if (P.Config.LimitUnversalisToDC)
                    P.UniversalsisClient.GetDCData(itemId, ref data);
                else
                    P.UniversalsisClient.GetRegionData(itemId, ref data);

                onComplete(data);
            });
        }
    }

    public class MarketboardLookup
    {
        public MarketboardData? Data;
        public bool FetchFailed;
    }

    public readonly record struct CheapestWorldCost(string World, double Qty, double Cost);

    public static class MarketboardPricing
    {
        // Finds the single world where buying `quantity` units is cheapest, summing listings
        // cheapest-first until the quantity is covered. Shared by the ingredient table's own
        // price column and the finished-product stock tab so both price the same way.
        public static CheapestWorldCost GetCheapestWorldCost(MarketboardData data, double quantity)
        {
            double currentWorldCost = 0;
            string currentWorld = "";
            double currentWorldQty = 0;

            foreach (var world in data.AllListings.Select(x => x.World).Distinct())
            {
                double totalCost = 0;
                double qty = 0;

                foreach (var listing in data.AllListings.Where(x => x.World == world).OrderBy(x => x.TotalPrice))
                {
                    if (qty >= quantity) break;
                    qty += listing.Quantity;
                    totalCost += listing.TotalPrice;
                }

                if ((totalCost < currentWorldCost && qty >= quantity) || currentWorldCost == 0 || (qty > currentWorldQty && qty < quantity))
                {
                    currentWorldCost = totalCost;
                    currentWorld = world;
                    currentWorldQty = qty;
                }
            }

            return new(currentWorld, currentWorldQty, currentWorldCost);
        }

        // NPC shop price is only meaningful for items an NPC actually sells - a common
        // material's PriceLow can be a nonzero "junk sell" value with no shop selling it.
        public static bool TryGetNpcPrice(Lumina.Excel.Sheets.Item item, out uint unitPrice)
        {
            unitPrice = item.PriceLow;
            return unitPrice > 0 && IPC.ItemVendorLocation.ItemHasVendor(item.RowId);
        }
    }
}
