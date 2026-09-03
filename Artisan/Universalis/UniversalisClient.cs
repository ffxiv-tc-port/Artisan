using ECommons;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.Universalis
{
    internal class UniversalisClient
    {
        private const string Endpoint = "https://universalis.app/api/v2/";

        // Universalis is a free public service and answers a burst of one-request-per-ingredient
        // with HTTP 429 (and, once requests queue up behind each other, with client-side timeouts
        // on top of that). A crafting list routinely holds 40+ ingredients, so requests are
        // coalesced into a few multi-item requests, issued one at a time with a minimum gap, and
        // retried with a backoff when the service says it is busy.
        private const int MaxItemsPerRequest = 20;
        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
        private static readonly TimeSpan MaxServerRetryAfter = TimeSpan.FromSeconds(60);

        private readonly HttpClient httpClient;
        public uint? PlayerWorld;

        // Live-fetched from Universalis itself instead of a hardcoded world-ID table, so any
        // region it tracks (including the Traditional Chinese one) resolves correctly.
        private static List<(string Name, string Region, uint[] Worlds)>? cachedDataCenters;
        private static DateTime dataCentersCacheExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim dataCentersGate = new(1, 1);

        private readonly object pendingLock = new();
        private readonly Dictionary<string, List<(ulong ItemId, TaskCompletionSource<MarketboardData?> Completion)>> pending = new();
        private readonly CancellationTokenSource cts = new();
        private bool flushRunning;
        private bool disposed;
        private DateTime nextRequestAllowed = DateTime.MinValue;

        public UniversalisClient()
        {
            this.httpClient = new HttpClient(new SocketsHttpHandler
            {
                // Region responses run to tens of KB per item; gzip keeps a 20-item batch small.
                AutomaticDecompression = DecompressionMethods.All,
            })
            {
                // A request now carries up to MaxItemsPerRequest items, so it is both larger and
                // far rarer than the old one-request-per-item traffic. The previous 10s budget was
                // being spent on requests merely queued behind other requests.
                Timeout = TimeSpan.FromSeconds(30),
            };
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Artisan/{Assembly.GetExecutingAssembly().GetName().Version}");
        }

        private async Task<List<(string Name, string Region, uint[] Worlds)>> GetDataCentersAsync()
        {
            if (cachedDataCenters != null && DateTime.Now < dataCentersCacheExpiry)
                return cachedDataCenters;

            // Without this gate a bulk price fetch fires one identical data-centers request per
            // ingredient while the cache is still cold, which on its own is enough to get rate limited.
            await dataCentersGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cachedDataCenters != null && DateTime.Now < dataCentersCacheExpiry)
                    return cachedDataCenters;

                try
                {
                    var json = await httpClient.GetStringAsync(Endpoint + "data-centers", cts.Token).ConfigureAwait(false);
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
            finally
            {
                dataCentersGate.Release();
            }
        }

        public async Task<bool> IsWorldKnownAsync(uint world)
            => (await GetDataCentersAsync().ConfigureAwait(false)).Any(d => d.Worlds.Contains(world));

        public async Task<MarketboardData?> GetRegionDataAsync(ulong itemId)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            var region = (await GetDataCentersAsync().ConfigureAwait(false))
                .FirstOrDefault(d => d.Worlds.Contains(world.Value)).Region;
            if (region == null)
                return null;

            return await EnqueueAsync(region, itemId).ConfigureAwait(false);
        }

        public async Task<MarketboardData?> GetDCDataAsync(ulong itemId)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            // Universalis' data-centers response has a known encoding bug for some CJK DC names,
            // so a DC name string isn't reliable. Querying by the raw numeric world ID always
            // works and is narrower than a DC query anyway.
            return await EnqueueAsync(world.Value.ToString(), itemId).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues one item for the next batched request against <paramref name="scope"/> (a world
        /// ID, DC name or region name - all three are valid in the same URL slot).
        /// </summary>
        private Task<MarketboardData?> EnqueueAsync(string scope, ulong itemId)
        {
            var completion = new TaskCompletionSource<MarketboardData?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingLock)
            {
                if (disposed)
                    return Task.FromResult<MarketboardData?>(null);

                if (!pending.TryGetValue(scope, out var queue))
                    pending[scope] = queue = new();
                queue.Add((itemId, completion));

                if (!flushRunning)
                {
                    flushRunning = true;
                    _ = Task.Run(FlushLoopAsync);
                }
            }

            return completion.Task;
        }

        private async Task FlushLoopAsync()
        {
            try
            {
                while (true)
                {
                    // Let the rest of a burst land before the first request goes out, so a
                    // 40-ingredient list becomes two multi-item requests, not 40 single-item ones.
                    await Task.Delay(BatchWindow, cts.Token).ConfigureAwait(false);

                    string scope;
                    List<(ulong ItemId, TaskCompletionSource<MarketboardData?> Completion)> batch;
                    lock (pendingLock)
                    {
                        var next = pending.FirstOrDefault(x => x.Value.Count > 0);
                        if (next.Value == null || next.Value.Count == 0)
                        {
                            // Cleared inside the lock so a caller enqueueing right now starts a new
                            // loop instead of waiting on one that is about to return.
                            flushRunning = false;
                            return;
                        }

                        scope = next.Key;
                        batch = next.Value.Take(MaxItemsPerRequest).ToList();
                        next.Value.RemoveRange(0, batch.Count);
                    }

                    Dictionary<ulong, MarketboardData?>? results = null;
                    try
                    {
                        results = await RequestBatchAsync(scope, batch.Select(x => x.ItemId).Distinct().ToList()).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }

                    // Every queued caller is answered even when the request failed, so nothing is
                    // left awaiting a task that will never complete.
                    foreach (var (itemId, completion) in batch)
                        completion.TrySetResult(results != null && results.TryGetValue(itemId, out var data) ? data : null);
                }
            }
            catch (OperationCanceledException)
            {
                // Plugin unloading - not an error.
            }
            catch (Exception ex)
            {
                ex.Log();
            }
            finally
            {
                List<TaskCompletionSource<MarketboardData?>> abandoned = new();
                lock (pendingLock)
                {
                    // Still true only when the loop exited abnormally; the normal exit above
                    // already cleared it and left the queues empty.
                    if (flushRunning)
                    {
                        flushRunning = false;
                        foreach (var queue in pending.Values)
                        {
                            abandoned.AddRange(queue.Select(x => x.Completion));
                            queue.Clear();
                        }
                    }
                }

                foreach (var completion in abandoned)
                    completion.TrySetResult(null);
            }
        }

        private async Task<Dictionary<ulong, MarketboardData?>?> RequestBatchAsync(string scope, List<ulong> itemIds)
        {
            if (itemIds.Count == 0)
                return null;

            var url = Endpoint + Uri.EscapeDataString(scope) + "/" + string.Join(",", itemIds);
            Svc.Log.Debug($"universalisRequest={url}");

            using var response = await SendWithRetryAsync(url, scope, itemIds.Count).ConfigureAwait(false);
            if (response == null)
                return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseBatch(scope, itemIds, body);
        }

        /// <summary>
        /// Issues one request, spaced at least <see cref="MinRequestInterval"/> after the previous
        /// one, retrying while Universalis reports a transient condition. Returns null once the
        /// request has definitively failed; the caller then reports "no data" for those items.
        /// </summary>
        private async Task<HttpResponseMessage?> SendWithRetryAsync(string url, string scope, int itemCount)
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = nextRequestAllowed - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cts.Token).ConfigureAwait(false);

                HttpStatusCode? status;
                string failure;
                TimeSpan? serverRetryAfter = null;
                HttpResponseMessage? response = null;

                try
                {
                    response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
                    nextRequestAllowed = DateTime.UtcNow + MinRequestInterval;

                    if (response.StatusCode == HttpStatusCode.OK)
                        return response;

                    status = response.StatusCode;
                    failure = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                    serverRetryAfter = response.Headers.RetryAfter?.Delta
                                       ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                    response.Dispose();
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    response?.Dispose();
                    return null;
                }
                catch (Exception ex)
                {
                    response?.Dispose();
                    nextRequestAllowed = DateTime.UtcNow + MinRequestInterval;
                    // An HttpClient.Timeout expiry surfaces as a TaskCanceledException rather than
                    // an HttpRequestException, and deserves the same treatment as a server 408.
                    status = ex is TaskCanceledException
                        ? HttpStatusCode.RequestTimeout
                        : (ex as HttpRequestException)?.StatusCode;
                    failure = ex.Message;
                }

                var transient = IsTransient(status);
                if (!transient || attempt >= RetryDelays.Length)
                {
                    if (transient)
                    {
                        // Universalis being busy is not something the user can fix, and it is not a
                        // broken plugin either - but they should still be able to see why the price
                        // column stayed empty, so this stays above the default log level.
                        Svc.Log.Warning(
                            "Universalis is still unavailable ({0}) after {1} retries; giving up on {2} item(s) for scope {3}.",
                            failure, RetryDelays.Length, itemCount, scope);
                    }
                    else
                    {
                        Svc.Log.Error(
                            "Failed to retrieve data from Universalis for {0} item(s) / scope {1}: {2}.",
                            itemCount, scope, failure);
                    }

                    return null;
                }

                var delay = serverRetryAfter is { } advised && advised > TimeSpan.Zero
                    ? (advised < MaxServerRetryAfter ? advised : MaxServerRetryAfter)
                    : RetryDelays[attempt];

                // Expected condition for a free public API, so Information rather than Error.
                Svc.Log.Information(
                    "Universalis returned {0} for {1} item(s) on scope {2}; retrying in {3:0.#}s.",
                    failure, itemCount, scope, delay.TotalSeconds);
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Statuses worth another attempt: 408/429 plus anything 5xx. Everything else is a
        /// permanent client-side error that retrying cannot fix.
        /// </summary>
        private static bool IsTransient(HttpStatusCode? status)
            => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

        private Dictionary<ulong, MarketboardData?> ParseBatch(string scope, List<ulong> itemIds, string body)
        {
            var results = new Dictionary<ulong, MarketboardData?>();

            JObject? root;
            try
            {
                root = JsonConvert.DeserializeObject<JObject>(body);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Failed to deserialize Universalis response for scope {0}.", scope);
                return results;
            }

            if (root == null)
            {
                Svc.Log.Error("Failed to deserialize Universalis response for scope {0}.", scope);
                return results;
            }

            // A world-scoped query omits worldName on each individual listing (they are all from
            // the one world that was asked about) and puts it on the response root instead.
            var fallbackWorldName = root["worldName"]?.ToString();

            // A single-item request answers with the item object itself; a multi-item request wraps
            // the same objects in an "items" map keyed by item ID.
            if (root["items"] is JObject items)
            {
                foreach (var itemId in itemIds)
                {
                    if (items[itemId.ToString()] is JObject node)
                        results[itemId] = ParseItem(node, scope, itemId, fallbackWorldName);
                }

                var unresolved = itemIds.Where(x => !results.ContainsKey(x)).ToList();
                if (unresolved.Count > 0)
                {
                    Svc.Log.Information(
                        "Universalis has no market data for item(s) {0} on scope {1}.",
                        string.Join(", ", unresolved), scope);
                }
            }
            else if (itemIds.Count == 1)
            {
                results[itemIds[0]] = ParseItem(root, scope, itemIds[0], fallbackWorldName);
            }
            else
            {
                Svc.Log.Error("Unexpected Universalis response shape for scope {0}.", scope);
            }

            return results;
        }

        private static MarketboardData? ParseItem(JObject node, string scope, ulong itemId, string? fallbackWorldName)
        {
            try
            {
                dynamic json = node;
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

                if (node["listings"] is JArray listings && listings.Count > 0)
                {
                    foreach (var item in listings)
                    {
                        // Indexers rather than dynamic member access: a world-scoped response has
                        // no worldName on the listing at all, and a missing member on a dynamic
                        // JObject throws instead of yielding null.
                        var world = item["worldName"]?.ToString() ?? fallbackWorldName ?? string.Empty;
                        Listing listing = new()
                        {
                            World = world,
                            Quantity = item["quantity"]!.Value<double>(),
                            TotalPrice = item["total"]!.Value<double>(),
                            UnitPrice = item["pricePerUnit"]!.Value<double>()
                        };

                        if (listing.World != "Cloudtest01" && listing.World != "Cloudtest02")
                            marketBoardData.AllListings.Add(listing);
                    }

                    if (marketBoardData.AllListings.Count > 0)
                    {
                        marketBoardData.CurrentMinimumPrice = marketBoardData.AllListings.First().TotalPrice;
                        marketBoardData.LowestWorld = marketBoardData.AllListings.First().World;
                        marketBoardData.ListingQuantity = marketBoardData.AllListings.First().Quantity;
                    }
                }

                return marketBoardData;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(
                    ex,
                    "Failed to parse marketBoard data for ItemId {0} / scope {1}.",
                    itemId,
                    scope);
                return null;
            }
        }

        public void Dispose()
        {
            lock (pendingLock)
                disposed = true;

            // Not disposing the CTS: the flush loop may still be observing its token, and an
            // ObjectDisposedException there would be raised on a background thread during unload.
            cts.Cancel();
            this.httpClient.Dispose();
        }
    }

    // Shared by every "获取价格" button (per-row and "一键全搜索" bulk buttons alike) so the
    // region/DC-known check and Universalis call only live in one place.
    public static class MarketboardFetch
    {
        public static void Fetch(uint itemId, Action onFailed, Action<MarketboardData?> onComplete)
        {
            var world = Svc.Objects.LocalPlayer?.CurrentWorld.RowId;
            P.UniversalsisClient.PlayerWorld = world;
            _ = Task.Run(async () =>
            {
                // DC-limited mode queries by raw world ID, which always resolves;
                // region mode needs the world to be one Universalis actually tracks.
                if (world == null ||
                    (!P.Config.LimitUnversalisToDC && !await P.UniversalsisClient.IsWorldKnownAsync(world.Value).ConfigureAwait(false)))
                {
                    onFailed();
                    return;
                }

                // The client batches and paces these internally, so firing one call per row is fine.
                var data = P.Config.LimitUnversalisToDC
                    ? await P.UniversalsisClient.GetDCDataAsync(itemId).ConfigureAwait(false)
                    : await P.UniversalsisClient.GetRegionDataAsync(itemId).ConfigureAwait(false);

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

        // NPC shop price is only meaningful for items an NPC actually sells for gil - PriceMid
        // is populated even for items only obtainable via a SpecialShop trade (tribal/GC scrip,
        // item-for-item exchange), so cross-check GilShopItem to confirm a real gil shop sells it.
        public static bool TryGetNpcPrice(Lumina.Excel.Sheets.Item item, out uint unitPrice)
        {
            unitPrice = item.PriceMid;
            return unitPrice > 0
                && (RawInformation.LuminaSheets.GilShopItemIds?.Contains(item.RowId) ?? false)
                && IPC.ItemVendorLocation.ItemHasVendor(item.RowId);
        }
    }
}
