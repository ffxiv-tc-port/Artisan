using System;
using System.Text;

namespace Artisan.RawInformation
{
    /// <summary>
    /// 原生名字欄（以 NUL 結尾的 SeString 位元組）是純文字時，不配置記憶體地取出它的文字值。
    /// </summary>
    /// <remarks>
    /// 契約：回 <see langword="true"/> 時 <c>dest[..written]</c> 與 Dalamud <c>SeString.Parse(raw).ToString()</c> 逐字相同 ——
    /// 第一個 NUL 之前沒有 0x02 ⇒ 整段只解出一個 TextPayload，文字就是 <c>Encoding.UTF8.GetString</c>（無效位元組照樣換成 U+FFFD）。
    /// 含 payload 回 <see langword="false"/>，呼叫端照原式解析。
    /// </remarks>
    internal static class PlainSeStringText
    {
        private const byte PayloadStart = 0x02;

        /// <param name="dest">至少 <c>Encoding.UTF8.GetMaxCharCount(raw.Length)</c> 格。</param>
        internal static bool TryDecode(ReadOnlySpan<byte> raw, Span<char> dest, out int written)
        {
            var nul = raw.IndexOf((byte)0);
            if (nul >= 0)
                raw = raw[..nul];
            if (raw.IndexOf(PayloadStart) >= 0)
            {
                written = 0;
                return false;
            }
            written = Encoding.UTF8.GetChars(raw, dest);
            return true;
        }
    }
}
