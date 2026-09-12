using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using Lumina.Text.ReadOnly;

namespace Artisan.RawInformation
{
    /// <summary>
    /// 把「從遊戲視窗／聊天記憶體讀來的字」攤成<b>可以和 Lumina 資料表逐字比對</b>的純文字。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>為什麼需要這支。</b>同一段位元組有四套互不相容的攤平法。
    /// ⇒ 一端是遊戲視窗、另一端是 Lumina 資料表時,只要名字含連字符或巨集 payload,
    /// 相等比對就<b>恆假</b>,而且失敗形式是「找不到、什麼都不做」不是報錯。
    /// <para>
    /// 🔑 <b>判準只有一條:另一端是什麼,這一端就用什麼。</b>另一端是 Lumina 資料表
    /// (<c>row.Name.ExtractText()</c>／<c>.ToString()</c>)就用這支;另一端若是 Dalamud
    /// 的 <c>TextValue</c> 或同樣走訪 payload 的自家碼,則<b>不要</b>用這支。
    /// </para>
    /// <para>
    /// 📌 <b>作法</b>:交還給 Lumina 自己的解析器,不複製它對 NewLine／NonBreakingSpace／
    /// Hyphen／SoftHyphen 的規則 —— 將來它改了也自動跟上。
    /// (離線實測五種輸入:純文字／連字符／斜體開關／NonBreakingSpace／SoftHyphen,
    /// <c>SeString.Encode()</c> 的位元組都與原始位元組逐字相同,輸出也與 Lumina 端逐字相同。)
    /// </para>
    /// </remarks>
    internal static unsafe class LuminaText
    {
        /// <summary>Dalamud <see cref="SeString"/>(聊天訊息、<c>MemoryHelper.ReadSeString*</c> 的產物)。</summary>
        internal static string Extract(SeString? seString)
            => seString == null
                   ? string.Empty
                   : new ReadOnlySeStringSpan(seString.Encode()).ExtractText();

        /// <summary>原生 <see cref="Utf8String"/>(<c>AtkTextNode->NodeText</c> 之類)。</summary>
        /// <remarks>⚠️ 指標為 <see langword="null"/> 時回空字串 —— 呼叫端原本就是拿它去比對,空字串比不中即可。</remarks>
        internal static string Extract(Utf8String* utf8)
            => utf8 == null
                   ? string.Empty
                   : new ReadOnlySeStringSpan(utf8->AsSpan()).ExtractText();
    }
}
