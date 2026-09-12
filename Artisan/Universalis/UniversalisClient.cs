using ECommons;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        //
        // 🔴 2026-09-13：區域範圍的批次請求在實機上長期吐 HTTP 504。真因**不是**批次太大，
        //    而是 `entries` 參數 —— 不帶它時 Universalis 預設要算「最近 5 筆成交紀錄」，
        //    而那是把整個區域所有世界的成交歷史合併起來算，區域範圍下穩定超過它自己閘道的
        //    10 秒預算。實測同一個 18 件的區域批次：
        //      不帶參數                 → 504（10.7 秒，連測三次都一樣）
        //      ?listings=8（只縮掛售）   → 504（10.7 秒，**縮掛售完全沒用**）
        //      ?entries=0（只關成交歷史）→ 200（1.25 秒，801 KB）
        //      ?listings=8&entries=0    → 200（0.78 秒，72 KB）
        //    ⇒ `entries=0` 是修好 504 的那一個參數；`listings` 只管回應大小。
        //    這也解釋了為什麼舊的「原批重送三次」從來沒成功過：同一個大請求的歷史計算
        //    每次都一樣慢。
        private const int MaxItemsPerRequestWorld = 20;

        // 區域／資料中心範圍一批放 10 件（世界範圍仍放 20）。
        // 理由：`entries=0` 之後區域請求已經回到 1 秒級，但區域查詢在 Universalis 那端仍然要
        // 跨世界合併掛售，成本本質上高於單一世界；而實機證據顯示小批次即使真的碰到 504 也能
        // 靠重試救回來（n=6、n=12 各在第一次重試後成功），大批次三次重試全滅
        // （n=13／14／19／20 共 9 個批次直接放棄）。10 是「單批仍然省請求數」與
        // 「碰到問題時還救得回來」之間的折衷。
        private const int MaxItemsPerRequestRegion = 10;

        // 切批時的下限：批次大於這個數才對半切，否則走一般的重試退避。
        // 🔴 沒有下限的遞迴切批在最壞情況下會放大請求數（20 → 10+10 → 5×4 → … → 20 個單件
        //    ＝ 51 個請求），對一個「已經在喘」的免費公開服務是反效果。
        private const int MinSplitSize = 5;

        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        // 逾時形狀的失敗只重試**一次**,其餘(429／其他 5xx)照舊退避三次。
        // 🔴 這個差別是實機證據直接給的:504 的重試紀錄裡,n=6 與 n=12 都在**第一次**重試後成功,
        //    而 n=13／14／19／20 三次重試全滅 —— 第二、三次重試對閘道逾時沒有救回任何一批,
        //    只是每次白等 10 秒(單一批次因此浪費 30 秒)。切批＋退到 aggregated 比多等兩次有用。
        private static readonly TimeSpan[] TimeoutRetryDelays = [TimeSpan.FromSeconds(2)];

        private static readonly TimeSpan MaxServerRetryAfter = TimeSpan.FromSeconds(60);

        private readonly HttpClient httpClient;
        public uint? PlayerWorld;

        // Live-fetched from Universalis itself instead of a hardcoded world-ID table, so any
        // region it tracks (including the Traditional Chinese one) resolves correctly.
        private static List<(string Name, string Region, uint[] Worlds)>? cachedDataCenters;
        private static DateTime dataCentersCacheExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim dataCentersGate = new(1, 1);

        // 世界 id → 世界名稱。只有降級路徑（aggregated 端點）用得到：那個端點回的是 worldId，
        // 而畫面上要顯示的是名稱，`/li <世界> mb` 傳送也吃名稱。
        // 📌 刻意向 Universalis 自己問，而不是查遊戲的 World 資料表：這樣降級路徑顯示的世界名
        //    與正常路徑的 listings[].worldName 逐字相同（實測台服 4028~4035 回的就是繁中名稱），
        //    而且這段完全跑在執行緒池上、不碰任何遊戲狀態。
        private static Dictionary<uint, string>? cachedWorldNames;
        private static DateTime worldNamesCacheExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim worldNamesGate = new(1, 1);

        private readonly object pendingLock = new();
        private readonly Dictionary<string, List<(ulong ItemId, TaskCompletionSource<MarketboardData?> Completion)>> pending = new();
        private readonly CancellationTokenSource cts = new();
        private bool flushRunning;
        private bool disposed;
        private DateTime nextRequestAllowed = DateTime.MinValue;

        /// <summary>
        /// 已經問到答案的道具，在 TTL 內不再向 Universalis 重問。
        /// </summary>
        /// <remarks>
        /// 🔴 為什麼需要：重建一次製作清單就把整份材料重問一遍，而實機上使用者一個 session
        /// 重建了 32 次清單 —— 那是 419 次區域請求的主要來源。價格十分鐘內不會有意義的變化，
        /// 所以同一個範圍＋同一件道具在 TTL 內直接回快取。
        /// ⚠️ 只快取「問到了」的結果；失敗與「沒有市場資料」不入快取，
        /// 否則使用者再按一次「取得價格」會什麼都不做。
        /// </remarks>
        private readonly ConcurrentDictionary<(string Scope, ulong ItemId), (MarketboardData Data, DateTime FetchedAt)> resultCache = new();

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

        /// <summary>
        /// 世界 id → 名稱對照表（只在降級路徑需要，所以是延遲取得的）。
        /// 取不到時回空字典：降級結果的世界欄位會是空字串，而那與「不知道」是同一件事。
        /// </summary>
        private async Task<Dictionary<uint, string>> GetWorldNamesAsync()
        {
            if (cachedWorldNames != null && DateTime.Now < worldNamesCacheExpiry)
                return cachedWorldNames;

            await worldNamesGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cachedWorldNames != null && DateTime.Now < worldNamesCacheExpiry)
                    return cachedWorldNames;

                try
                {
                    var json = await httpClient.GetStringAsync(Endpoint + "worlds", cts.Token).ConfigureAwait(false);
                    var parsed = JsonConvert.DeserializeObject<JArray>(json);
                    var map = new Dictionary<uint, string>();
                    foreach (var w in parsed ?? new JArray())
                    {
                        var id = w["id"]?.Value<uint>();
                        var name = w["name"]?.ToString();
                        if (id != null && !string.IsNullOrEmpty(name))
                            map[id.Value] = name!;
                    }

                    cachedWorldNames = map;
                    worldNamesCacheExpiry = DateTime.Now.AddHours(12);
                }
                catch (Exception ex)
                {
                    ex.Log();
                    cachedWorldNames ??= new();
                }

                return cachedWorldNames;
            }
            finally
            {
                worldNamesGate.Release();
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
        /// 一個 scope 字串是不是「單一世界」（純數字＝世界 id；否則是資料中心或區域名稱）。
        /// </summary>
        private static bool IsWorldScope(string scope)
            => scope.Length > 0 && scope.All(char.IsAsciiDigit);

        private static int MaxItemsPerRequest(string scope)
            => IsWorldScope(scope) ? MaxItemsPerRequestWorld : MaxItemsPerRequestRegion;

        /// <summary>
        /// 這次查詢要附的查詢字串。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>entries=0</c> 是無條件帶的 —— 它就是 504 的解法（見檔頭的實測）。
        /// 它唯一的代價是 <c>averagePriceNQ/HQ</c>（那兩個是從回傳的成交紀錄算出來的）會變成 0，
        /// 所以解析端會把它們寫成 <c>null</c> 而不是 0；把「不知道」寫成 0 會被讀成「賣過 0 gil」。
        /// <c>listings=N</c> 只管回應大小，N 來自設定（0＝不限）。
        /// </remarks>
        private static string BuildQuery(out bool historySuppressed, out int listingsCap)
        {
            historySuppressed = true;
            listingsCap = Math.Max(0, P.Config.UniversalisListingsPerItem);
            return listingsCap > 0 ? $"?listings={listingsCap}&entries=0" : "?entries=0";
        }

        /// <summary>
        /// Queues one item for the next batched request against <paramref name="scope"/> (a world
        /// ID, DC name or region name - all three are valid in the same URL slot).
        /// </summary>
        private Task<MarketboardData?> EnqueueAsync(string scope, ulong itemId)
        {
            // 🔴 先查快取再排隊。清單重建時整份材料都會走到這裡，命中就是一個請求都不送。
            if (TryGetCached(scope, itemId, out var cached))
                return Task.FromResult<MarketboardData?>(cached);

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

        private bool TryGetCached(string scope, ulong itemId, out MarketboardData? data)
        {
            data = null;
            var ttlMinutes = P.Config.UniversalisCacheMinutes;
            if (ttlMinutes <= 0)
                return false;

            if (!resultCache.TryGetValue((scope, itemId), out var entry))
                return false;

            if (DateTime.UtcNow - entry.FetchedAt > TimeSpan.FromMinutes(ttlMinutes))
            {
                resultCache.TryRemove((scope, itemId), out _);
                return false;
            }

            // 交出複製品：消費端會就地改寫 LowestWorld（見 MarketboardData.Clone 的說明）。
            data = entry.Data.Clone();
            return true;
        }

        private void StoreCached(string scope, ulong itemId, MarketboardData? data)
        {
            if (data == null || P.Config.UniversalisCacheMinutes <= 0)
                return;

            resultCache[(scope, itemId)] = (data.Clone(), DateTime.UtcNow);
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
                        batch = next.Value.Take(MaxItemsPerRequest(next.Key)).ToList();
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

        /// <summary>
        /// 一次請求的結局，用來決定「要不要切批」。
        /// </summary>
        private enum BatchOutcome
        {
            /// <summary>拿到 200。</summary>
            Ok = 0,

            /// <summary>逾時形狀的失敗（504／502／503／408／客戶端逾時）＝「這個請求太貴」。</summary>
            Timeout = 1,

            /// <summary>其他失敗（4xx、500、連線錯誤、取消）。切批對這些沒有幫助。</summary>
            Other = 2,
        }

        /// <summary>
        /// 送一批，必要時對半切、最後退到 aggregated 端點。
        /// </summary>
        /// <remarks>
        /// 🔑 三層退路，每一層解決不同的症狀：
        /// ①<c>entries=0</c>（在 <see cref="BuildQuery"/>）－ 讓 504 一開始就不要發生；
        /// ②對半切 － 萬一還是逾時，小請求比「同一個大請求重送三次」有機會（實機證據：
        ///   n=6／n=12 第一次重試就成功，n=13/14/19/20 三次重試全滅）；
        /// ③aggregated 端點 － 連小批次都不行時，用一個極小的回應至少拿到最低價與世界。
        /// </remarks>
        private async Task<Dictionary<ulong, MarketboardData?>?> RequestBatchAsync(string scope, List<ulong> itemIds)
        {
            if (itemIds.Count == 0)
                return null;

            var query = BuildQuery(out var historySuppressed, out var listingsCap);
            var url = Endpoint + Uri.EscapeDataString(scope) + "/" + string.Join(",", itemIds) + query;
            var stopwatch = Stopwatch.StartNew();

            // 🔴 大批次不做「原批重送」：實機上 9 個批次各重送三次、每次都 504、全部放棄，
            //    而每次 504 要花 10 秒（總共白等 30 秒）。可以切的時候就直接去切。
            var splittable = itemIds.Count > MinSplitSize;
            var (response, outcome, failure) = await SendWithRetryAsync(
                url, scope, itemIds.Count, allowRetry: !splittable).ConfigureAwait(false);

            if (response != null)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    var parsed = ParseBatch(scope, itemIds, body, historySuppressed, listingsCap);
                    stopwatch.Stop();
                    LogBatch(scope, itemIds.Count, query, stopwatch.Elapsed,
                        $"ok {parsed.Count(x => x.Value != null)}/{itemIds.Count}", split: false, fallback: false);
                    foreach (var (itemId, data) in parsed)
                        StoreCached(scope, itemId, data);
                    return parsed;
                }
            }

            if (outcome == BatchOutcome.Timeout && splittable)
            {
                // 對半切，各自遞迴（遞迴會再切，直到 MinSplitSize）。
                var half = itemIds.Count / 2;
                stopwatch.Stop();
                LogBatch(scope, itemIds.Count, query, stopwatch.Elapsed, failure, split: true, fallback: false);

                var left = await RequestBatchAsync(scope, itemIds.Take(half).ToList()).ConfigureAwait(false);
                var right = await RequestBatchAsync(scope, itemIds.Skip(half).ToList()).ConfigureAwait(false);

                var merged = new Dictionary<ulong, MarketboardData?>();
                foreach (var part in new[] { left, right })
                {
                    if (part == null) continue;
                    foreach (var kv in part)
                        merged[kv.Key] = kv.Value;
                }

                return merged.Count > 0 ? merged : null;
            }

            if (outcome == BatchOutcome.Timeout)
            {
                // 切不動了（批次已經小於下限）－ 退到 aggregated 端點。
                var fallback = await RequestAggregatedAsync(scope, itemIds).ConfigureAwait(false);
                stopwatch.Stop();
                LogBatch(scope, itemIds.Count, query, stopwatch.Elapsed,
                    fallback == null ? $"{failure}; fallback failed" : $"{failure}; fallback {fallback.Count(x => x.Value != null)}/{itemIds.Count}",
                    split: false, fallback: true);

                if (fallback != null)
                {
                    foreach (var (itemId, data) in fallback)
                        StoreCached(scope, itemId, data);
                }

                return fallback;
            }

            stopwatch.Stop();
            LogBatch(scope, itemIds.Count, query, stopwatch.Elapsed, failure, split: false, fallback: false);
            return null;
        }

        /// <summary>
        /// 每一批一行 Information：使用者回報問題時靠這一行判斷發生了什麼。
        /// </summary>
        /// <remarks>
        /// 🔴 刻意用 <c>Svc.Log</c> 而不是 <c>DuoLog</c>：<c>DuoLog</c> 的<b>每一個</b>等級都會
        /// 無條件把訊息印到使用者的聊天視窗。
        /// </remarks>
        private static void LogBatch(string scope, int itemCount, string query, TimeSpan elapsed, string result, bool split, bool fallback)
        {
            Svc.Log.Information(
                "[Universalis] scope={0} items={1} query={2} elapsed={3:0.00}s result={4} split={5} fallback={6}",
                scope, itemCount, query, elapsed.TotalSeconds, result, split ? "yes" : "no", fallback ? "yes" : "no");
        }

        /// <summary>
        /// Issues one request, spaced at least <see cref="MinRequestInterval"/> after the previous
        /// one, retrying while Universalis reports a transient condition. Returns a null response
        /// once the request has definitively failed, plus why it failed so the caller can decide
        /// between splitting the batch and falling back.
        /// </summary>
        /// <param name="allowRetry">
        /// <c>false</c> 時逾時形狀的失敗<b>立刻</b>回報（交給呼叫端切批）；429 與其他暫時性
        /// 狀況照樣退避重試 —— 切批對「被限流」只會讓情況更糟。
        /// </param>
        private async Task<(HttpResponseMessage? Response, BatchOutcome Outcome, string Failure)> SendWithRetryAsync(
            string url, string scope, int itemCount, bool allowRetry)
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
                        return (response, BatchOutcome.Ok, string.Empty);

                    status = response.StatusCode;
                    failure = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                    serverRetryAfter = response.Headers.RetryAfter?.Delta
                                       ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                    response.Dispose();
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    response?.Dispose();
                    return (null, BatchOutcome.Other, "cancelled");
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

                var timeoutShaped = IsTimeoutShaped(status);

                // 逾時形狀 ＋ 呼叫端還能切批 ⇒ 不要重送同一個請求，直接回去讓它切。
                if (timeoutShaped && !allowRetry)
                    return (null, BatchOutcome.Timeout, failure);

                var transient = timeoutShaped || IsTransient(status);
                var delays = timeoutShaped ? TimeoutRetryDelays : RetryDelays;
                if (!transient || attempt >= delays.Length)
                {
                    if (transient)
                    {
                        // Universalis being busy is not something the user can fix, and it is not a
                        // broken plugin either - but they should still be able to see why the price
                        // column stayed empty, so this stays above the default log level.
                        Svc.Log.Warning(
                            "Universalis is still unavailable ({0}) after {1} retries; giving up on {2} item(s) for scope {3}.",
                            failure, delays.Length, itemCount, scope);
                    }
                    else
                    {
                        Svc.Log.Error(
                            "Failed to retrieve data from Universalis for {0} item(s) / scope {1}: {2}.",
                            itemCount, scope, failure);
                    }

                    return (null, timeoutShaped ? BatchOutcome.Timeout : BatchOutcome.Other, failure);
                }

                var delay = serverRetryAfter is { } advised && advised > TimeSpan.Zero
                    ? (advised < MaxServerRetryAfter ? advised : MaxServerRetryAfter)
                    : delays[attempt];

                // Expected condition for a free public API, so Information rather than Error.
                Svc.Log.Information(
                    "Universalis returned {0} for {1} item(s) on scope {2}; retrying in {3:0.#}s.",
                    failure, itemCount, scope, delay.TotalSeconds);
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 「這個請求對伺服器來說太貴」形狀的失敗：閘道逾時（504）、閘道錯誤／服務不可用
        /// （502／503，Cloudflare 對超時的上游常回這兩個）、408，以及我方自己的逾時。
        /// 這一類切小之後有機會成功。
        /// </summary>
        private static bool IsTimeoutShaped(HttpStatusCode? status)
            => status is HttpStatusCode.GatewayTimeout
                      or HttpStatusCode.BadGateway
                      or HttpStatusCode.ServiceUnavailable
                      or HttpStatusCode.RequestTimeout;

        /// <summary>
        /// Statuses worth another attempt: 429 plus anything 5xx. Everything else is a
        /// permanent client-side error that retrying cannot fix.
        /// </summary>
        private static bool IsTransient(HttpStatusCode? status)
            => status is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

        private Dictionary<ulong, MarketboardData?> ParseBatch(
            string scope, List<ulong> itemIds, string body, bool historySuppressed, int listingsCap)
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
                        results[itemId] = ParseItem(node, scope, itemId, fallbackWorldName, historySuppressed, listingsCap);
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
                results[itemIds[0]] = ParseItem(root, scope, itemIds[0], fallbackWorldName, historySuppressed, listingsCap);
            }
            else
            {
                Svc.Log.Error("Unexpected Universalis response shape for scope {0}.", scope);
            }

            return results;
        }

        private static MarketboardData? ParseItem(
            JObject node, string scope, ulong itemId, string? fallbackWorldName, bool historySuppressed, int listingsCap)
        {
            try
            {
                dynamic json = node;
                var marketBoardData = new MarketboardData
                {
                    Source = MarketboardSource.Listings,
                    LastCheckTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    LastUploadTime = json.lastUploadTime?.Value,
                    // 🔴 averagePrice* 是從「回傳的成交紀錄」算出來的，帶 entries=0 之後一律是 0。
                    //    把它寫成 0 會被讀成「均價 0 gil」；沒有資料就寫 null。
                    AveragePriceNQ = historySuppressed ? null : json.averagePriceNQ?.Value,
                    AveragePriceHQ = historySuppressed ? null : json.averagePriceHQ?.Value,
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
                    // 🔴 listingsCount／unitsForSale 算的是「回傳的那幾筆」，不是市場真實總數。
                    //    帶了 listings=N 又剛好收到 N 筆時無法排除「其實還有更多」⇒ 標成只知下界。
                    if (listingsCap > 0 && listings.Count >= listingsCap)
                        marketBoardData.ListingsTruncated = true;

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

        /// <summary>
        /// 最後的退路：<c>/api/v2/aggregated/{scope}/{ids}</c>。
        /// </summary>
        /// <remarks>
        /// 📌 這個端點的回應極小（2026-09-13 實測：區域範圍 4 件共 1505 bytes，0.77 秒），
        /// 因為它不回掛售明細，只回每件的 <c>minListing</c>／<c>recentPurchase</c>／
        /// <c>averageSalePrice</c>／<c>dailySaleVelocity</c>，各自再分 <c>world</c>／<c>dc</c>／
        /// <c>region</c> 三種範圍。
        /// ⚠️ 它只認得自己那份「可上市清單」裡的道具，其餘放進 <c>failedItems</c>
        /// （實測道具 5730 就是）。這裡當退路用，<c>failedItems</c> 直接算「沒有資料」。
        /// 🔴 回來的東西<b>不包含掛售明細</b>，所以「買 N 件要多少錢」算不出來 ——
        /// 合成的那一筆掛售刻意只放 1 件，讓畫面上的 Qty 顯示 1，使用者看得出覆蓋不足；
        /// 另外用 <see cref="MarketboardSource.Aggregated"/> 讓 UI 標「約略」。
        /// </remarks>
        private async Task<Dictionary<ulong, MarketboardData?>?> RequestAggregatedAsync(string scope, List<ulong> itemIds)
        {
            var url = Endpoint + "aggregated/" + Uri.EscapeDataString(scope) + "/" + string.Join(",", itemIds);
            var (response, _, _) = await SendWithRetryAsync(url, scope, itemIds.Count, allowRetry: true).ConfigureAwait(false);
            if (response == null)
                return null;

            string body;
            using (response)
                body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            var worldNames = await GetWorldNamesAsync().ConfigureAwait(false);

            try
            {
                var root = JsonConvert.DeserializeObject<JObject>(body);
                if (root == null)
                    return null;

                var results = new Dictionary<ulong, MarketboardData?>();
                if (root["results"] is JArray array)
                {
                    foreach (var entry in array.OfType<JObject>())
                    {
                        var itemId = entry["itemId"]?.Value<ulong>();
                        if (itemId == null)
                            continue;

                        var data = ParseAggregatedItem(entry, worldNames);
                        if (data != null)
                            results[itemId.Value] = data;
                    }
                }

                var failed = (root["failedItems"] as JArray)?.Select(x => x.Value<ulong>()).ToList() ?? new List<ulong>();
                if (failed.Count > 0)
                {
                    Svc.Log.Information(
                        "[Universalis] aggregated endpoint has no entry for item(s) {0} on scope {1}; treating as no data.",
                        string.Join(", ", failed), scope);
                }

                return results;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Failed to parse Universalis aggregated response for scope {0}.", scope);
                return null;
            }
        }

        /// <summary>
        /// aggregated 端點的一件道具 → <see cref="MarketboardData"/>。
        /// 拿不到任何最低價時回 null（＝與「沒有市場資料」同一個結論）。
        /// </summary>
        private static MarketboardData? ParseAggregatedItem(JObject entry, Dictionary<uint, string> worldNames)
        {
            // world → dc → region：範圍越窄越貼近使用者實際買得到的地方。世界範圍的查詢只有
            // world，區域範圍的查詢只有 region（2026-09-13 實測），所以取第一個有值的。
            static (double Price, uint? WorldId)? Pick(JToken? aggregate, string field)
            {
                if (aggregate?[field] is not JObject scoped)
                    return null;

                foreach (var key in new[] { "world", "dc", "region" })
                {
                    if (scoped[key] is not JObject node)
                        continue;
                    var price = node["price"]?.Value<double>();
                    if (price == null)
                        continue;
                    return (price.Value, node["worldId"]?.Value<uint>());
                }

                return null;
            }

            var nqMin = Pick(entry["nq"], "minListing");
            var hqMin = Pick(entry["hq"], "minListing");
            if (nqMin == null && hqMin == null)
                return null;

            var data = new MarketboardData
            {
                Source = MarketboardSource.Aggregated,
                LastCheckTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MinimumPriceNQ = nqMin?.Price,
                MinimumPriceHQ = hqMin?.Price,
                AveragePriceNQ = Pick(entry["nq"], "averageSalePrice")?.Price,
                AveragePriceHQ = Pick(entry["hq"], "averageSalePrice")?.Price,
                // 🔴 掛售筆數與在售總量這條路徑拿不到。留 null（＝不知道），
                //    不要填 0 —— 0 會被讀成「市場上一件都沒有在賣」。
                TotalNumberOfListings = null,
                TotalQuantityOfUnits = null,
            };

            if (entry["worldUploadTimes"] is JArray uploads)
            {
                var newest = uploads.OfType<JObject>()
                    .Select(x => x["timestamp"]?.Value<long>() ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();
                data.LastUploadTime = newest;
            }

            // 取兩種品質裡便宜的那一個當「目前最低價」，與正常路徑（不分品質取最便宜的掛售）一致。
            var best = nqMin;
            if (hqMin != null && (best == null || hqMin.Value.Price < best.Value.Price))
                best = hqMin;
            if (best == null)
                return null;

            var worldName = best.Value.WorldId is { } wid && worldNames.TryGetValue(wid, out var name)
                ? name
                : string.Empty;

            data.CurrentMinimumPrice = best.Value.Price;
            data.LowestWorld = worldName;
            data.ListingQuantity = 1;
            // 合成一筆掛售，讓既有的消費端（AllListings.Count > 0 當「有價格」旗標、
            // GetCheapestWorldCost）不必個別處理降級資料。數量刻意是 1：那是這條路徑真正知道的量。
            data.AllListings.Add(new Listing
            {
                World = worldName,
                Quantity = 1,
                TotalPrice = best.Value.Price,
                UnitPrice = best.Value.Price,
            });

            return data;
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
