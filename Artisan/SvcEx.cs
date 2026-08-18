using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;

namespace Artisan;
#nullable disable

/// <summary>
/// 補充 ECommons <c>Svc</c> 沒有提供的 Dalamud 服務。
/// </summary>
/// <remarks>
/// API13 把 <c>IClientState.LocalContentId</c> 標為過時，替代品是 <c>IPlayerState.ContentId</c>；
/// 但 ECommons 釘在 <c>pin-wrathcombo-tc-api13</c>，它的 <c>Svc</c> 尚未提供 <c>IPlayerState</c>，
/// 而 ECommons 本體要改動就會牽動全艦隊 repin，因此在本外掛自己這一側補一個服務容器。
/// <para>
/// 註：Dalamud 端 <c>ClientState.LocalContentId</c> 本身就是 <c>=&gt; this.playerState.ContentId</c>
/// 的轉發（含 <c>IsLoaded</c> 判斷），所以改用 <see cref="IPlayerState"/> 取值不會改變行為。
/// </para>
/// </remarks>
public class SvcEx
{
    [PluginService] public static IPlayerState PlayerState { get; private set; }

    /// <summary>
    /// 注入服務。必須在任何使用 <see cref="PlayerState"/> 的程式碼之前呼叫。
    /// </summary>
    public static void Init(IDalamudPluginInterface pi)
    {
        try
        {
            pi.Create<SvcEx>();
        }
        catch(Exception ex)
        {
            ECommons.Logging.PluginLog.Error(ex.ToString());
        }
    }
}
