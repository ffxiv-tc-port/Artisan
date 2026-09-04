using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.GameInterop;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// Artisan 所有對 addon 的按法(<c>Callback.Fire</c>、<c>FireCallback</c>、<c>ClickAddonButton</c>、
/// <c>AddonMaster</c> 的 <c>Yes()</c>／<c>Click()</c>／<c>Materialize()</c>／<c>RepairAll()</c>／<c>Material()</c>…)
/// 都要先問過 <see cref="TryBeginPress(string, AtkUnitBase*, string, int)"/>;解除點集中在 <see cref="Tick"/> 與
/// AddonLifecycle 的 PreFinalize／PostSetup。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類「按下即關」的窗被按下之後
/// 有<b>「正在關閉中」的幾幀</b>,這段期間 <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
/// <c>UldManager.LoadedState == Loaded</c> 也都還成立(＝ <c>IsAddonReady</c> 三關全過、擋不住這個窗口)。
/// 此時再對它送 callback／輸入事件就是原生 AccessViolation(C0000005)。AVE 在 .NET Core 是
/// corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到,遊戲當場關閉 ——
/// <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
/// </para>
/// <para>
/// 🔴 節流<b>不是</b>防護:<c>Autocraft.Throttler</c>／<c>RepairManager._nextRetry</c>／<c>Spiritbond._nextRetry</c>
/// 這些記的是「上一次動作在哪個時刻」,不是「這扇窗已經按過」。一次 ≥ 節流長度的幀停頓就會讓下一輪
/// 正好落在關閉中的第 1 幀;<c>RetainerInfo.GenericThrottle</c>(EzThrottler 100ms)首次必放行、
/// key 全外掛共用;<c>RetainerInfo.Tick</c> 對 Talk 的 <c>Click()</c> 更是連節流都沒有(每幀)。
/// </para>
/// <para>
/// 🔴 「按過的按鈕會被遊戲停用所以不會重按」<b>不成立</b>:ECommons <c>AddonMaster.SelectYesno.Yes()</c>
/// 遇到停用的「是」鈕會翻 <c>NodeFlags</c> 強制啟用再點,遊戲那層天然的防護被這條碼路徑破壞掉了。
/// </para>
/// <para>
/// 🔑 <b>粒度＝(窗,位址,參數組)</b>:
/// <list type="bullet">
/// <item>「回答一次即終結」的窗(<see cref="SingleAnswerAddons"/>)不分按法,整扇窗一把 key ——
/// 按過任何一個之後窗就在關閉中,別的都不准再送。</item>
/// <item>按下不會關的窗(RecipeNote 的指派素材／開始製作、WKSRecipeNotebook 的選取、Materialize 開對話框、
/// Talk 翻頁…)帶 <paramref name="pressKey"/>,同一扇窗對不同參數組各准按一次,保住
/// 「同幀對同窗連送不同參數」的正常流程(<c>SetIngredients</c> 同幀按 NQ 再按 HQ 全選鈕、
/// <c>TaskEquipItem</c> 同一趟對 ContextMenu 先送裝備再送關閉)。</item>
/// <item><see cref="ClosePressKey"/> 是萬用鍵:對某扇窗送過關閉(<c>Fire(-1)</c>)之後、還沒觀察到它收掉之前,
/// 對同一位址的<b>任何</b>按法都會被擋;反過來同一位址任何按法還熱著時也不准再送關閉
/// (同一幀內先按後關的正常流程除外,見 <see cref="FindBlocking"/>)。</item>
/// </list>
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>(兩條都只會讓封鎖<b>提早</b>解除,不會延後):
/// <list type="number">
/// <item><b>輪詢</b>(<see cref="Tick"/>,每幀從 <c>Artisan.OnFrameworkUpdate</c> 最前面呼叫):
/// 被記下的位址已經不在該名稱的 addon 清單裡(掃全索引,掃到第一個空的停)⇒ 那扇窗真的收乾淨了。
/// Artisan 的按下點全部由 Framework.Update／LegacyTaskManager(同樣跑在 Framework.Update 上)／
/// ImGui Draw／ReceiveEvent detour 同步呼叫驅動,沒有 AddonLifecycle PostDraw 驅動的按下點,所以輪詢有效。</item>
/// <item><b>AddonLifecycle 事件</b>:<see cref="AddonEvent.PreFinalize"/>(這一扇正在被銷毀)與
/// <see cref="AddonEvent.PostSetup"/>(有新的一扇被建立起來),只清<b>該事件那個位址</b>的紀錄。
/// 同名 addon 關掉再開常常重用同一塊位址,只靠第 1 條的話重開的那扇會被誤認成「按過的那扇還沒收掉」
/// 而白白被擋到逃生口。⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 當解除點:它可能在關閉中那幾幀觸發。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>:單答終結窗 <see cref="DefaultEscapeFrames"/>(90 幀,遠大於關閉所需,走到＝異常,
/// 寫 Information);「按一次翻一頁／按下不關」的多次互動窗 <see cref="RoutineRePressEscapeFrames"/>(15 幀,
/// 走到是常態,寫 Debug 不洗版)。沒有逃生口的話呼叫端會永遠按不下去,等於把崩潰換成靜默失效。
/// 用<b>幀數</b>而不是毫秒:危險窗口的長度本來就是以幀計的,遊戲卡頓時兩者一起拉長。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>:第一次看到某扇窗的某個按法一律當場按下去;被擋下時回 <see langword="false"/>,
/// 呼叫端一律走它原本「addon 還沒出現／還沒 ready」那條既有路徑(輪詢型任務下個 tick 再來、
/// <c>PreCrafting</c> 任務回 <c>Retry</c>)。🔴 絕不回 <see langword="null"/>:LegacyTaskManager 的
/// <c>bool?</c> 三態裡 <see langword="null"/> 是 Abort,會清掉整條佇列。
/// </para>
/// <para>🔴 全程只做<b>位址等值比較,永遠不解參</b> —— 被記下的那個位址隨時可能已經失效。</para>
/// <para>⚠️ 只在主執行緒使用(與所有呼叫端相同的前提)。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次(單答終結窗)。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」,這個值只是防死鎖的逃生口。
    /// 90 幀(60fps 下約 1.5 秒)遠遠大於「關閉中的那幾幀」,補按永遠不會落在危險窗口內。
    /// </remarks>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗(Talk 是代表;RecipeNote／WKSRecipeNotebook／
    /// Materialize／Repair 這些按下不關的持久窗同形狀)用的短逃生口(15 幀)。
    /// </summary>
    /// <remarks>
    /// 這類窗整段都不關也不重建,輪詢與生命週期兩條解除點都不會觸發,走逃生口是<b>常態</b>而不是異常
    /// (下一頁／下一次製作就是這樣送出去的),所以放行 log 寫 Debug 不洗版。
    /// 關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面;每頁多等 0.25 秒幾乎無感。
    /// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據:關閉中的窗文字會讀壞(U+FFFD)。
    /// (2026-09-02 艦隊政策:Talk 類一律 15 幀。)
    /// </remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>
    /// 「關閉」這個按法的萬用鍵(<c>Callback.Fire(addon, true, -1)</c> 或等價的關閉鈕)。
    /// 送過關閉之後、還沒觀察到它收掉之前,<see cref="TryBeginPress(string, AtkUnitBase*, string, int)"/>
    /// 對同一位址的<b>任何</b>按法都會被擋。
    /// </summary>
    internal const string ClosePressKey = "Close";

    /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
    /// <remarks>同名視窗同時開著超過這個數量在實務上不存在;掃到第一個空的就提早停。</remarks>
    private const int MaxAddonIndex = 32;

    /// <summary>
    /// <see cref="PersistentAddons"/> 裡的窗被按下之後,最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀,才把按下記號解除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼需要這條路徑</b>:<see cref="Tick"/> 與 AddonLifecycle 兩條既有解除點對常駐窗
    /// <b>都是死路</b>(它從頭到尾都在清單裡、位址不變,PreFinalize/PostSetup 都不會發生)⇒ 記號一旦記下就
    /// <b>永不解除</b>,整扇窗退化成「每 <see cref="RoutineRePressEscapeFrames"/> 幀才准動作一次」。
    /// AutoRetainer 2026-09-04 的實機記錄:<c>ContextMenu</c> 送出 295 次,其中 290 次是走逃生口走出來的,
    /// 另有 144 次使用者的右鍵被整個吞掉。
    /// </para>
    /// <para>
    /// 🔴 <b>為什麼是「連續 N 幀」而不是「這一幀不可見就解除」</b>:窗在拆除途中會有幾幀「已經被設成不可見、
    /// 但還沒拆完」,那正是這個守衛在防的危險窗口(實測 &lt;10 幀)。要求連續觀察到隱藏才解除,等於「等到穩定
    /// 隱藏＝拆除已經結束」才放行;看到可見(或這一幀不在清單裡)就歸零重數,短暫閃一下不會累積。
    /// </para>
    /// <para>
    /// 📌 <b>20 幀換算成多久</b>:使用者實測的幀率是 <b>10.07 ms/幀</b>(僱員鈴前,n=290)⇒ 20 幀 ≈ <b>201 毫秒</b>。
    /// (不要用 60fps 去換算成 333 毫秒 —— 那個幀率在這台機器上不成立。)
    /// </para>
    /// <para>
    /// 🔴 <b>這個數字不承重。</b>真正把崩潰面拆掉的是 <see cref="TryBeginPress(string, nint, string, int)"/>
    /// 對常駐窗多要求的那道「送出前必須可見」:它的放行條件(可見)與這裡的解除條件(連續不可見)
    /// <b>互為邏輯反面</b> ⇒ 記號被解除之後還要能送出,中間<b>必須</b>有一次遊戲自己把窗重新 Show 起來,
    /// 而正在拆除的窗不會被重新 Show。所以<b>就算這個幀數設得太短</b>,最壞也只是「那一發沒送出」,
    /// 不會變成對正在拆除的窗再送一次(＝攔不到的 AccessViolation)。N 只決定使用者要等多久。
    /// </para>
    /// <para>🔴 這個值只在 <see cref="PersistentAddons"/> 裡的窗名上生效;其餘窗名的解除條件<b>一個字都沒有改</b>。</para>
    /// </remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key,而且<b>同一幀</b>也不豁免。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只放<b>回答一次就結束</b>的窗。RecipeNote／WKSRecipeNotebook／Materialize／Repair／Talk 這種
    /// 「窗一直開著、刻意連送不同 callback」的<b>絕對不能</b>放進來,那會把正常流程一起擋掉。
    /// <c>SelectIconString</c>／<c>SelectString</c> 刻意不在此:巢狀選單常常重用同一個實例只換內容
    /// (不觸發 PostSetup),那兩個改用與參數一致的 key 對齊。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "MaterializeDialog",
        "SynthesisSimpleDialog",
    };

    /// <summary>
    /// 已經<b>實機證實</b>是「常駐」的窗名:關閉只是被設成不可見,實例與位址永遠留在 <c>AllLoadedUnitsList</c> 裡,
    /// 既不會從 <see cref="Tick"/> 掃的清單消失,PreFinalize/PostSetup 也不會再發生。
    /// 只有這些窗名才套用「連續隱藏 <see cref="HiddenReleaseFrames"/> 幀就解除」這條新規則。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>加名字進來的門檻＝實機 log 裡「真的送出的次數」≈「走逃生口的次數」</b>
    /// (兩者幾乎相等＝記號從來沒有被「消失/銷毀」那條路徑解除過)。<c>ContextMenu</c> 在 AutoRetainer
    /// 的同一場實機記錄裡是 295 次送出 : 290 次逃生口 : 144 次被吞掉,因此收錄。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有這種證據的名字一律不要加。</b>猜錯的方向是危險的:把一扇「會被銷毀」的窗誤標成常駐,
    /// 等於在它拆除途中提早 <see cref="HiddenReleaseFrames"/> 幀解除封鎖。已知的候選但<b>證據不足、刻意沒收</b>
    /// 的是 <c>ContextIconMenu</c>(本 repo 的 <c>CraftingList</c> 有它的按下點,與 <c>ContextMenu</c> 同一個
    /// <c>AgentContext</c> 家族、直覺上同樣常駐,但實機 log 裡沒有它的逃生口紀錄,無從證實)——
    /// 憑直覺把它加進來,就是把一個沒被驗證的假設寫進安全關鍵路徑。
    /// </para>
    /// <para>
    /// 🔴 <b>加名字進來的代價:這份名單同時是「送出前必須可見」的名單。</b>
    /// <see cref="TryBeginPress(string, nint, string, int)"/> 對名單內的窗多要求一道就地可見性檢查,
    /// 那是 <see cref="Tick"/> 裡新解除條件的邏輯反面。名單外的窗一個字都沒有改。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
    };

    /// <param name="Address">被按的那個實例的位址,<b>只做等值比較</b>。</param>
    /// <param name="Frame">按下時的 framework tick 幀號(見 <see cref="frameCount"/>)。</param>
    /// <param name="EscapeFrames">登記當時呼叫端給的逃生口。</param>
    /// <remarks>
    /// 🔴 <b>刻意是 class 不是 struct。</b>寫成 struct 時 <c>TryGetValue</c> 拿到的是複本,
    /// 「改完要寫回字典」就成了承重的一行 —— 而漏掉寫回<b>不會編譯失敗</b>,只會讓
    /// <see cref="HiddenFrames"/> 永遠停在 0,常駐窗的記號就再也解除不掉(＝這次要修的那個形狀)。
    /// 改成 class 之後取出的是參考、就地改就生效,這個地雷從根本上不存在。
    /// ⚠️ 同時存在的紀錄實務上只有 0~3 個,多出來的配置可以忽略。
    /// </remarks>
    private sealed record PressRecord(nint Address, long Frame, int EscapeFrames)
    {
        /// <summary>
        /// 連續觀察到「位址還在清單裡、但那扇窗已經被隱藏」的幀數。只有 <see cref="PersistentAddons"/> 裡的
        /// 窗名會累加;看到可見就歸零。到達 <see cref="HiddenReleaseFrames"/> 就解除記號。
        /// </summary>
        /// <remarks>
        /// 🔴 重新按下時<b>一定要歸零</b>。這裡不必額外寫一行:每次登記都是 <c>new PressRecord(...)</c>,
        /// 新物件的計數天然是 0 —— 改成就地更新既有紀錄的話,這一行就變成承重的。
        /// </remarks>
        public int HiddenFrames;
    }

    /// <summary>addon 名稱 → (按法 → 上一次按的是哪個實例、在第幾幀)。</summary>
    private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);

    // Tick 用的可重用緩衝,沒有窗被記著時 Tick 是一個整數比較就回來,不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<string> KeysBuf = [];
    // 本幀掃到「還在清單裡、但被隱藏」的位址;只在常駐窗名上填。
    private static readonly HashSet<nint> HiddenBuf = [];
    // 「這個窗名第一次走隱藏解除」只寫一行 Information,之後不再寫(每場遊戲每個窗名最多一行,不會洗版)。
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>
    /// 守衛自己的時鐘,單位是 <b>framework tick</b>(遊戲主迴圈每更新一次 +1),在 <see cref="Tick"/> 的最前面遞增。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不用 <c>UiBuilder.FrameCount</c></b>(2026-09-02 換掉):那個計數器是在 Dalamud
    /// <c>UiBuilder.OnDraw</c> 的<b>結尾</b>才 <c>++</c>,而 <c>OnDraw</c> 在「使用者隱藏 UI」、
    /// 「<b>過場動畫</b>」(<c>ToggleUiHideDuringCutscenes</c>,<b>預設開</b>)、「GPose」三種情況下
    /// 會在遞增之前就 <c>return</c> ⇒ <b>外掛 UI 被隱藏的整段期間繪製幀號完全停住</b>。
    /// 拿它當時鐘的話,兩個逃生口(<see cref="DefaultEscapeFrames"/>／<see cref="RoutineRePressEscapeFrames"/>)
    /// 在過場中<b>永遠不會到期</b>:守衛退化成永久封鎖 —— 不會崩(fail-closed),但「按一次翻一頁、
    /// 窗不會消失」的站(僱員補貨對 <c>Talk</c> 的每幀點擊)會停在第一頁直到過場結束。
    /// <para>
    /// <c>Framework.Update</c> 掛在遊戲主迴圈的更新上,與繪製、與 UI 隱藏<b>完全無關</b>,過場中照跑
    /// (Dalamud 那層唯一的閘門是 <c>DispatchUpdateEvents</c>,只有遊戲關閉時才會變 false)。
    /// 📌 正常情況下遊戲每個 frame 更新一次也繪製一次,兩個計數器是 1:1,所以逃生口的幀數不必跟著調整;
    /// 兩者唯一會分岔的方向就是上面那三種「繪製停、更新照跑」的情況,那正是要修的。
    /// </para>
    /// <para>
    /// 🔴 <b>不變式:<see cref="Tick"/> 必須每個 framework tick 無條件被呼叫一次</b>
    /// (<c>Artisan.OnFrameworkUpdate</c> 的第一個敘述,在登入判斷與所有開關之前)。
    /// 把它移到任何條件底下,時鐘就會停,守衛會靜默退化成永久封鎖。
    /// </para>
    /// </remarks>
    private static long frameCount;

    /// <summary>目前的 framework tick 幀號(見 <see cref="frameCount"/>)。</summary>
    private static long CurrentFrame => frameCount;

    /// <summary>
    /// 登記「即將對這扇視窗送出這一個按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱(解除封鎖的監聽器與輪詢都以它為準)。</param>
    /// <param name="addon">目標實例。<b>只當作識別用的位址,本方法不解參。</b></param>
    /// <param name="pressKey">
    /// 這一次的「按法」(參數組)。同一扇窗上不同的按法互不干擾;要擋的是<b>同一個按法重複送</b>。
    /// 空字串代表「整扇窗只有一種按法」;<see cref="ClosePressKey"/> 是關閉的萬用鍵。
    /// </param>
    /// <param name="escapeFrames">
    /// 逃生口幀數:單答終結窗用 <see cref="DefaultEscapeFrames"/>(走到寫 Information),
    /// 按下不關的持久窗／Talk 用 <see cref="RoutineRePressEscapeFrames"/>(走到寫 Debug)。
    /// </param>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b> —— 這支一回 <see langword="true"/> 就已經把「按過了」記下去,
    /// 登記完卻不按的話會白白封鎖到逃生口為止。條件鏈裡有消耗型節流(<c>Throttler.Throttle</c>、
    /// <c>GenericThrottle</c>)時把本方法放在節流<b>之後</b>:被擋只多等一輪節流,不會留下沒按的登記。
    /// </remarks>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
        => TryBeginPress(addonName, (nint)addon, pressKey, escapeFrames);

    /// <inheritdoc cref="TryBeginPress(string, AtkUnitBase*, string, int)"/>
    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
    {
        if (address == 0 || string.IsNullOrEmpty(addonName)) return false;

        var singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer && pressKey != ClosePressKey) pressKey = string.Empty;

        // 🔴🔴 常駐窗專用的「送出前必須可見」閘門。
        //    ⚠️ 這一道**不是**「擋得住正在關閉中的窗」的檢查 —— IsAddonReady 的三關(非 null / IsVisible /
        //    LoadedState == Loaded)在拆除途中是**全過**的(本檔開頭那段講的就是這件事),單獨看它一個東西都擋不到。
        //    **這個結論不可以當成通用結論搬去別的地方用。**
        //
        //    它在這裡有效的唯一理由是:**與 Tick 對常駐窗的解除條件互為邏輯反面**。
        //    那邊的解除條件是「連續 HiddenReleaseFrames 幀**不可見**」,這裡的放行條件是「**可見**」
        //    ⇒ 記號被解除之後還要能再送出一發,中間**必須**有一次遊戲自己把這扇窗重新 Show 起來,
        //    而正在拆除的窗不會被重新 Show。兩者一組才是防護;只做其中一半的話,記號可以在拆除中途被解除,
        //    下一發直接打在正在拆的窗上 ＝ 攔不到的存取違規。
        //
        // 🔴 只對 PersistentAddons 生效。名單外的窗完全不走這一行,行為與改動前逐字相同 ——
        //    這是刻意的:本 repo 有刻意在窗不可見時仍要送出的按下點(MarkClosing 之後的關閉補送),
        //    無差別加上這道檢查會把它們靜默改成不送。
        if (PersistentAddons.Contains(addonName) && !PersistentAddonVisible(addonName, address))
        {
            if (EzThrottler.Throttle($"AddonPressGuard-PersistentHidden-{addonName}", 1000))
                Svc.Log.Information($"[AddonPressGuard] 「{addonName}」是常駐窗,實例 0x{address:X} 這一幀不可見" +
                                    "(或不在同名清單裡),不送 —— 它與「連續隱藏才解除」互為反面,兩者一組才是防護。");
            return false;
        }

        EnsureWatching(addonName);

        var frame = CurrentFrame;
        var routine = escapeFrames <= RoutineRePressEscapeFrames;

        if (!PressedByAddon.TryGetValue(addonName, out var presses))
        {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }
        else
        {
            if (FindBlocking(presses, address, pressKey, singleAnswer, frame, out var blockingKey))
            {
                // 🔴 這就是崩潰的那一幀。
                LogHold(addonName, address, pressKey, blockingKey, routine);
                return false;
            }

            if (presses.TryGetValue(pressKey, out var same) && same.Address == address)
            {
                // 同一個按法對同一扇窗按過、已經冷掉(超過逃生口)仍是同一位址:視為那次沒生效
                // (或這是另一扇重用了同一塊記憶體、且沒觸發 PostSetup 的新窗),放行補按一次。
                var waited = frame - same.Frame;
                if (routine)
                {
                    if (EzThrottler.Throttle($"AddonPressGuard-RoutineRelease-{addonName}", 10000))
                        Svc.Log.Debug($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                      $"按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次。");
                }
                else if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                {
                    Svc.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                        $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立,判定為「上一次按下沒生效」" +
                                        "而不是「正在關閉」,解除封鎖讓呼叫端重試。");
                }
            }
        }

        presses[pressKey] = new PressRecord(address, frame, escapeFrames);
        // 跨外掛重按診斷:全艦隊每個外掛各有一份本守衛,只擋得住自己按過的位址。
        // 格式必須與其他外掛逐字一致才能交叉比對;不節流(按壓頻率天然很低)、不解參考位址。
        Svc.Log.Information($"[按窗診斷] plugin=Artisan addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// 只<b>看</b>不登記:這扇視窗的這一個按法現在是不是被擋著(判準與 <see cref="TryBeginPress(string, AtkUnitBase*, string, int)"/> 完全相同)。
    /// </summary>
    /// <remarks>
    /// 給「同一趟要連按好幾個按法、其中一個被擋就整趟不做」的呼叫端用(<c>SetIngredients</c> 的 NQ／HQ 全選鈕),
    /// 先看再登記才不會留下「登記了卻沒按」的紀錄白白封鎖到逃生口。
    /// <para>⚠️ 回 <see langword="true"/> ＝ 這一幀不要碰。<paramref name="addon"/> 為 null 也算不要碰。</para>
    /// </remarks>
    internal static bool IsHeld(string addonName, AtkUnitBase* addon, string pressKey = "")
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return true;

        var singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer && pressKey != ClosePressKey) pressKey = string.Empty;

        if (!PressedByAddon.TryGetValue(addonName, out var presses)) return false;

        var address = (nint)addon;
        if (!FindBlocking(presses, address, pressKey, singleAnswer, CurrentFrame, out var blockingKey)) return false;

        LogHold(addonName, address, pressKey, blockingKey, false);
        return true;
    }

    /// <summary>
    /// 這扇窗(位址)是不是「我們自己剛把它關了、還沒觀察到它收掉」。
    /// </summary>
    /// <remarks>
    /// 給「進來先確認這扇持久窗不是關閉中」的整段流程用(<c>SetIngredients</c> 對 RecipeNote／WKSRecipeNotebook、
    /// <c>TaskEquipItem</c> 對上一趟關掉的 ContextMenu):是的話整段當「窗還沒開好」跳過,走既有的下一輪路徑。
    /// 只看 <see cref="ClosePressKey"/> 與 <see cref="MarkClosing"/> 留下的紀錄,持久窗上那些按下不關的按法不算。
    /// </remarks>
    internal static bool IsClosing(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return true;
        if (!PressedByAddon.TryGetValue(addonName, out var presses)) return false;
        if (!presses.TryGetValue(ClosePressKey, out var closed) || closed.Address != (nint)addon) return false;

        var waited = CurrentFrame - closed.Frame;
        if (waited >= closed.EscapeFrames) return false;

        LogHold(addonName, (nint)addon, ClosePressKey, ClosePressKey, closed.EscapeFrames <= RoutineRePressEscapeFrames);
        return true;
    }

    /// <summary>
    /// 記下「這扇窗正要被<b>非按鈕</b>的手段關掉」(<c>ActionManager.UseAction</c> 的修理／精製切換動作
    /// 在窗開著時就是關閉):之後對同一位址的任何按法都會被擋到它收掉為止。
    /// </summary>
    /// <remarks>
    /// 不做閘門、不改呼叫端流程 —— 那些切換動作不是 addon 按法,不在 AVE 的路徑上;這裡只是把
    /// 「我們自己關了它」這件事告訴守衛,讓後面的 <c>RepairAll()</c>／<c>FireCallback</c> 不會在
    /// 一次 ≥ 節流長度的幀停頓之後正好落在關閉中的那幾幀。
    /// </remarks>
    internal static void MarkClosing(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return;
        EnsureWatching(addonName);
        if (!PressedByAddon.TryGetValue(addonName, out var presses))
        {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }
        presses[ClosePressKey] = new PressRecord((nint)addon, CurrentFrame, DefaultEscapeFrames);
    }

    /// <summary>
    /// 讀窗上的文字來做判定的站,讀到 U+FFFD 就代表視窗記憶體正在變動(多半是關閉中),<b>這一幀不碰</b>。
    /// </summary>
    /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做。</returns>
    /// <remarks>
    /// 這是崩潰的旁證而不是防護本體(防護是 <see cref="TryBeginPress(string, AtkUnitBase*, string, int)"/>):
    /// 實機崩潰前 log 裡的 prompt 就是這種亂碼。寫 Information 讓使用者回報時看得到。
    /// </remarks>
    internal static bool IsTextCorrupt(string addonName, string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\uFFFD') < 0) return false;

        if (EzThrottler.Throttle($"AddonPressGuard-Corrupt-{addonName}", 1000))
            Svc.Log.Information($"[AddonPressGuard] 「{addonName}」的文字讀到 U+FFFD 亂碼(視窗記憶體正在變動,多半是關閉中),這一幀不碰它。");

        return true;
    }

    /// <summary>
    /// 每幀從 <c>Artisan.OnFrameworkUpdate</c> 的最前面無條件呼叫,做兩件事:
    /// (1) 讓守衛的時鐘 <see cref="frameCount"/> 前進一格;
    /// (2) 被記下的位址已經從該窗名的清單裡消失時解除封鎖。
    /// </summary>
    /// <remarks>
    /// 🔴 只做位址等值比較,永遠不解參。
    /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」:關閉中的那幾幀三關全過,拿那個當「窗不見了」
    /// 會在最危險的那幾幀把封鎖解除掉,等於沒有這道防線。
    /// 🔴 <b>呼叫點必須無條件、每個 framework tick 一次</b>:除了解除封鎖,這裡還是守衛時鐘唯一的來源
    /// (見 <see cref="frameCount"/>);挪到任何條件底下都會讓逃生口停止倒數。
    /// 放在 Tick 最前面且不受任何開關限制:解除點若只長在各自的分支裡,開關剛好在按下之後轉為關閉時
    /// 記號會一直留著,下一扇重用同一塊位址的窗會被白白擋到逃生口。
    /// </remarks>
    internal static void Tick()
    {
        // 🔴 時鐘必須在這裡遞增,而且要在下面那個「沒有記號就回來」之前 ——
        // 放到 early return 後面的話,沒有窗被記著時時鐘就停住,逃生口的幀數會被算少,等於沒修。
        frameCount++;

        if (PressedByAddon.Count == 0) return;

        NamesBuf.Clear();
        foreach (var name in PressedByAddon.Keys) NamesBuf.Add(name);

        foreach (var name in NamesBuf)
        {
            if (!PressedByAddon.TryGetValue(name, out var presses)) continue;

            // 常駐窗名才需要知道「可不可見」;其餘窗名這一幀連讀都不讀,行為與改動前逐字相同。
            var persistent = PersistentAddons.Contains(name);
            PresentBuf.Clear();
            HiddenBuf.Clear();
            for (var i = 1; i <= MaxAddonIndex; i++)
            {
                var unit = Svc.GameGui.GetAddonByName(name, i);
                var live = unit.Address;
                if (live == 0) break;
                PresentBuf.Add(live);
                // 🔑 這是本檔唯一一次解參,而且解的是「本幀剛從 GetAddonByName 拿回來」的指標,
                //    不是跨幀保存的 rec.Address(那些永遠只做等值比較)。
                //    AtkUnitBasePtr.IsVisible 先判 null、再讀 AtkUnitBase 固定位移的旗標,不再往下追任何指標。
                if (persistent && !unit.IsVisible) HiddenBuf.Add(live);
            }

            KeysBuf.Clear();
            foreach (var (key, rec) in presses)
            {
                // ①既有路徑:位址從清單裡消失 ＝ 那扇窗已經收乾淨。行為完全沒有改。
                if (!PresentBuf.Contains(rec.Address))
                {
                    KeysBuf.Add(key);
                    continue;
                }
                // ②新路徑:只有常駐窗名走得到。它們永遠不會從清單消失,①對它們是死路。
                if (!persistent) continue;
                if (!HiddenBuf.Contains(rec.Address))
                {
                    // 還看得見 ＝ 要嘛還沒關、要嘛正在關閉中的危險窗口內。歸零重數。
                    rec.HiddenFrames = 0;
                    continue;
                }
                if (++rec.HiddenFrames < HiddenReleaseFrames) continue;
                KeysBuf.Add(key);
                if (HiddenReleaseReported.Add(name))
                    Svc.Log.Information($"[AddonPressGuard] 「{name}」:偵測到這是常駐窗(隱藏而不銷毀),按下記號改由" +
                                        $"「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個窗名每次遊戲只寫一次。");
            }
            foreach (var key in KeysBuf) presses.Remove(key);

            if (presses.Count == 0) PressedByAddon.Remove(name);
        }
    }

    /// <summary>
    /// 在 <paramref name="addonName"/> 的<b>所有</b>同名實例裡找出 <paramref name="address"/>,回報它這一幀看不看得見。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>零值方向是刻意的:找不到就回 <see langword="false"/>(＝不准送)。</b>這支只給「放行端」用,
    /// 任何查不到/查歪的情形都必須表現成「這一輪不送」,而不是「放行」—— 後者是崩潰,前者只是多等一幀。
    /// 🔴 位址只做等值比較;解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標。掃到第一個空的就停。
    /// </remarks>
    private static bool PersistentAddonVisible(string addonName, nint address)
    {
        if (address == 0 || string.IsNullOrEmpty(addonName)) return false;

        for (var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if (unit.Address == 0) break;
            if (unit.Address != address) continue;
            return unit.IsVisible;
        }

        return false;
    }

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)。</summary>
    internal static void ForceTeardown()
    {
        foreach (var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }

        Watchers.Clear();
        PressedByAddon.Clear();
    }

    /// <summary>
    /// 這扇窗(位址)上有沒有一筆還熱著的紀錄會擋住 <paramref name="pressKey"/> 這個按法。
    /// </summary>
    /// <remarks>
    /// 判準(逐條):
    /// <list type="bullet">
    /// <item>同一個按法、同一位址、還在逃生口內 ⇒ 擋(<b>同一幀也擋</b>:那是字面上的重按)。</item>
    /// <item>單答終結窗:同一位址<b>任何</b>熱紀錄 ⇒ 擋(同一幀也擋 —— 先送關閉再按「是」這種接力就是崩潰形狀)。</item>
    /// <item>其他窗:對方或自己是 <see cref="ClosePressKey"/> ⇒ 擋,<b>但同一幀登記的除外</b> ——
    /// 「同一趟先送裝備再送關閉」(<c>TaskEquipItem</c>)是刻意的正常流程,擋的是<b>下一幀起</b>的任何按法。</item>
    /// <item>其他窗、不同參數組、都不是關閉 ⇒ 不擋(同幀對同窗連送不同參數是正常流程)。</item>
    /// </list>
    /// </remarks>
    private static bool FindBlocking(Dictionary<string, PressRecord> presses, nint address, string pressKey, bool singleAnswer, long frame, out string blockingKey)
    {
        foreach (var (key, rec) in presses)
        {
            if (rec.Address != address) continue;
            var waited = frame - rec.Frame;
            if (waited >= rec.EscapeFrames) continue; // 冷了:交給同 key 的逃生口去判

            var sameKey = string.Equals(key, pressKey, StringComparison.Ordinal);
            var blocks = sameKey
                         || singleAnswer
                         || ((key == ClosePressKey || pressKey == ClosePressKey) && rec.Frame != frame);
            if (!blocks) continue;

            blockingKey = key;
            return true;
        }

        blockingKey = string.Empty;
        return false;
    }

    /// <summary>被擋那一幀的診斷:單答終結窗寫 Information(使用者跑 LogLevel 1),多次互動窗寫 Debug;每扇窗 1 秒節流免得洗版。</summary>
    private static void LogHold(string addonName, nint address, string pressKey, string blockingKey, bool routine)
    {
        if (!EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000)) return;

        var msg = $"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                  $"按過之後(紀錄「{blockingKey}」)還沒觀察到它收掉,這一幀不再碰它 —— " +
                  "對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine) Svc.Log.Debug(msg); else Svc.Log.Information(msg);
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器:PreFinalize／PostSetup 時只清<b>該事件那個位址</b>的紀錄。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆(只在 <see cref="ForceTeardown"/> 拆):這兩條監聽器只做一次字典移除,成本可忽略,
    /// 而動態掛／拆比較容易留下懸空的監聽器。本 pin 的 <c>AddonLifecyclePluginScoped</c> 在外掛卸載時也會自動拆。
    /// 只清該位址而不是整個名字:SelectYesno 可以同時開好幾扇,A 按過關閉中、B 剛建立(PostSetup)時
    /// 若把 A 的紀錄一起清掉,下一幀對 A 的重按就沒人擋了。
    /// </remarks>
    private static void EnsureWatching(string addonName)
    {
        if (Watchers.ContainsKey(addonName)) return;

        IAddonLifecycle.AddonEventDelegate handler = (_, args) =>
        {
            var address = (nint)args.Addon.Address;
            if (address == 0 || !PressedByAddon.TryGetValue(addonName, out var presses)) return;

            foreach (var key in presses.Where(kv => kv.Value.Address == address).Select(kv => kv.Key).ToArray())
                presses.Remove(key);

            if (presses.Count == 0) PressedByAddon.Remove(addonName);
        };

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
