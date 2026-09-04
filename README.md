# Artisan

製作自動化插件：內建多種求解器自動執行製作技能、批次製作清單、持久模式、
公會工坊管理與製作模擬器。

原作者：[PunishXIV](https://github.com/PunishXIV/Artisan)

## 功能

- **自動製作**：依巨集或內建求解器自動執行製作技能，求解器可選標準、專家、
  機會主義、材料奇蹟、進度優先，或呼叫 Raphael 演算法求最佳解。
- **巨集編輯器**：撰寫、匯入匯出、逐步模擬巨集執行結果。
- **製作清單**：排入多項配方依序自動製作，可設定自動補素材、跳過已足夠
  數量、自動修理裝備等條件。
- **持久模式（Endurance）**：重複製作或採集直到材料用盡或條件達成才停止。
- **製作模擬器**：離線試算技能組合對進度、品質、耐久、CP 的影響。
- **公會工坊**：檢視與管理部隊工坊排程。
- **Raphael 快取檢視器**：管理 Raphael 求解器產生的最佳解快取。
- **求解器批次指派**：一次替多筆配方套用求解器設定。
- **任務助手**：追蹤帶有製作／收集要求的任務進度。
- **右鍵選單整合**：從配方或物品清單直接開始製作。
- **市場價格整合**：透過 Universalis 顯示素材與成品行情。
- **跨插件整合**：與 AutoRetainer、SimpleTweaks、ChatTwo、TataruPraise 等插件
  互通，可自動取用雇員庫存素材、朗讀製作提示等。

## 指令

- `/artisan`：開啟主選單。
- `/artisan lists`：開啟製作清單；`/artisan lists <ID>` 開啟指定清單，
  `/artisan lists <ID> start` 直接開始該清單。
- `/artisan macros`：開啟巨集列表；`/artisan macros <ID>` 開啟指定巨集。
- `/artisan endurance`：開啟持久模式；`/artisan endurance start|stop` 開始或停止。
- `/artisan settings`：開啟設定視窗。
- `/artisan workshops`：開啟公會工坊。
- `/artisan builder`：開啟清單建構器。
- `/artisan automode`：切換自動技能執行模式開關。
