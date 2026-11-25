# Artisan

[Artisan](https://github.com/PunishXIV/Artisan) 的台服移植版。製作自動化插件：內建多種求解器自動執行製作技能、
批次製作清單、耐力模式、公會工坊管理與製作模擬器。

原作者：[PunishXIV](https://github.com/PunishXIV/Artisan)

## 功能

- **自動製作**：依巨集或內建求解器自動執行製作技能，求解器可選標準、專家、
  機會主義、材料奇蹟、進度優先，或呼叫 Raphael 演算法求最佳解（隨插件附帶
  [raphael-rs](https://github.com/KonaeAkira/raphael-rs) 求解程式）。
- **巨集編輯器**：撰寫、匯入匯出、逐步模擬巨集執行結果。
- **製作清單**：排入多項配方依序自動製作，可設定從僱員補貨、跳過已足夠
  數量、自動修理裝備、精製魔晶石等條件；可從 Teamcraft 匯入清單。
- **搜尋配方**：依名稱、職業、等級、星級、類別等條件篩選配方，勾選多筆後一次加入
  製作清單。
- **耐力模式（Endurance）**：在製作筆記選好配方後重複製作，直到材料用盡或次數達成才停止，
  並可在製作之間自動使用食物、藥品、指南。
- **製作模擬器**：離線試算技能組合對進度、品質、耐久、CP 的影響。
- **公會工坊**：檢視公會工坊的專案與所需材料，可轉成製作清單。
- **Raphael 快取檢視器**：管理 Raphael 求解器產生的最佳解快取。
- **求解器批次指派**：一次替多筆配方套用求解器設定。
- **任務助手**：追蹤帶有製作／收集要求的任務進度。
- **右鍵選單整合**：從配方或物品清單直接開始製作。
- **市場價格整合**：透過 Universalis 顯示素材與成品行情。
- **跨插件整合**：與 AutoRetainer、Allagan Tools、GatherBuddy Reborn、SimpleTweaks、ChatTwo、
  TataruPraise 等插件互通，可自動取用僱員身上的素材、把缺的素材交給 GatherBuddy 採集、
  請塔塔露出聲提醒等。

## 指令

- `/artisan`：開啟主選單；已開啟時則關閉。
- `/artisan lists`：開啟製作清單；`/artisan lists <ID>` 開啟指定清單，
  `/artisan lists <ID> start` 直接開始該清單，`/artisan lists stop` 取消執行中的清單
  （正在做的那一件會先做完）。
- `/artisan macros`：開啟巨集列表；`/artisan macros <ID>` 開啟指定巨集。
- `/artisan endurance`：開啟耐力模式；`/artisan endurance start|stop` 開始或停止。
- `/artisan settings`：開啟設定視窗。
- `/artisan workshop`：開啟公會工坊。
- `/artisan builder`：開啟清單建構器。
- `/artisan search`：開啟搜尋配方分頁；`/artisan search <文字>` 另把文字填進名稱欄。
- `/artisan sim`：開啟模擬器。
- `/artisan automode`：切換自動技能執行模式開關。

## 台服版的修改

- 介面繁體中文化（依台服官方譯名）。
- Teamcraft 匯入可直接貼清單網址線上抓取，品名支援英文、簡體中文或遊戲用戶端語言。
- 主視窗新增「搜尋配方」分頁：依名稱或 ID、職業、等級、星級、類別，以及是不是收藏品、專家配方、
  需要秘笈的配方、做過沒有來篩選，也可以只顯示有材料的。數量填想要幾個成品，會自動換算製作次數
  （滑鼠滾輪可調，按住 Shift 一次 10）；勾選多筆後一次加入製作清單，可連同所有子配方，
  加入目標可以當場建立新清單。製作清單編輯器的［用搜尋加入…］會直接帶到這一頁。
- 素材表的缺料可一鍵交給 GatherBuddy Reborn 的自動採集清單（設為所需數量、重按不會疊加），
  之後可直接按「開始自動採集」。送出前會先列出來自限時採集點的素材，附市價與預估採集時間，
  可以取消勾選想改用購買的再送出（可在設定關閉這個確認）。
- 從僱員補貨：可改走 AutoRetainer 的快速取物；清單開了自動修理時一併補足黑暗物質；
  補貨途中傳送、換區或視窗被關掉會立刻中止並說明原因。
- 每份製作清單可指定這份清單專用的消耗品；設定的食物或藥品不在背包時提醒一次，
  開「強制使用消耗品」時則暫停清單。
- 製作清單編輯器頂端新增「開工前」檢查：哪些配方在目前裝備下作業精度／加工精度不足、
  哪些要靠食物或藥品才夠、還缺幾種素材、背包格位夠不夠；讀不到的數值顯示「?」。
- 製作清單編輯器新增「依世界採購」頁：把還缺的素材依世界分組，列出在各世界要買幾件與小計，
  可選「越少世界越好」或「每項各自最便宜」。這一頁只顯示、不會代買，需開啟 Universalis 查價；
  裝了 Lifestream 時可按鈕前往該世界的市場佈告板。
- 製作清單會預估需要幾格背包，放不下時提示大約還差幾格；新增設定「背包剩幾格就暫停清單」
  （預設 0＝不啟用）。
- 宇宙探索的製作：沒有指定解算器的配方也會改用 Raphael 的解；專家與標準配方解算器
  對宇宙探索配方追求滿品質，不停在第三個門檻（都可在設定關閉，預設開）。
- 專家配方解算器與 Raphael 解算器多項調整：依製作狀態（高品質／最高品質／低品質）調整步驟順序、
  品質滿了就跳過剩下的品質技能、剩下的步驟做不完進展時先把製作做完等，各有設定可以關閉。
- 耐力模式、製作清單與其他插件要求的製作，開工前會先自動下坐騎。
- 新增設定「手動按『製作』鈕時由 Artisan 接手」（預設開；關掉後手動按「製作」照遊戲原本的行為）。
- 搭配 TataruPraise：清單完成、耐力模式跑完時請塔塔露誇獎，製作被迫停下時請她喊人。
- 新增設定「製作因錯誤停下時播放提示音」與「附近有其他玩家時模擬遊戲巨集延遲」（都預設關）。
- 背包滿、換不了套裝、一直選不到配方等情況會停下並在聊天視窗說明一次，不再無限重試。
- 遊戲改版後某條特徵碼對不上時，只停用對應的功能，插件照常載入。
- Universalis 查價改為分批、失敗重試並快取十分鐘，減少逾時。
- 多處降低記憶體用量。

## 安裝

1. 在遊戲內輸入 `/xlsettings` 開啟「Dalamud 設定」，到「試驗性功能」分頁的「第三方插件倉庫」加入
   `https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
   勾選啟用後儲存。
2. 輸入 `/xlplugins` 開啟「插件管理員」，搜尋「Artisan」安裝。

同一個插件只裝這裡的版本，不要同時安裝上游（國際服）版本。

## 需要搭配的插件

- 選用：[AutoRetainer](https://github.com/ffxiv-tc-port/AutoRetainer)、
  [Allagan Tools](https://github.com/ffxiv-tc-port/InventoryTools)（僱員相關功能）、
  [GatherBuddy Reborn](https://github.com/ffxiv-tc-port/GatherBuddyReborn)（採集缺料）、
  [Lifestream](https://github.com/ffxiv-tc-port/Lifestream)（前往其他世界的市場佈告板）、
  [TataruPraise](https://github.com/ffxiv-tc-port/TataruPraise)（語音提醒）。

授權：BSD-3-Clause（見 LICENCE.md）
