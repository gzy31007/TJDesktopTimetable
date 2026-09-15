# 课表导入：实现细节与踩坑

> 本文件是 AGENTS.md 里**课表导入**细节的逐字搬运（2026-09-16 结构拆分）。入口、分层、载入顺序、安全口径、诊断 CLI、验收仍在 AGENTS.md；这里放实现细节与踩坑。

  - **`ImportService` 不认识窗口**：构造时拿三个回调（`apply` = 落盘 + 重画 / `reload` = 按载入顺序重读 /
    `describe` = 当前课表摘要），由 `App` 接到挂件窗口；设置窗口也**不认识 `MainWindow`**，一切经
    `SettingsWindow.SettingsHost`。`apply` 里对"窗口还没建"做了兜底（直接落盘）—— 否则 `--import`
    会因为那一刻 `_windows` 还是空的而静默失败。
  - **落盘格式两端同形**：`TimetableJson` 用 camelCase、逐字段对齐 TS 的 `Timetable` 接口，因此
    `%APPDATA%\TJDesktopTimetable\timetable.json` 在 Electron 线与 WinUI 线之间能互相读
    （`credentials.json` 同理：`{ tongjiRequest, savedAt }`）。`Term.Label` 加了 `[JsonIgnore]`
    （TS 没这个字段，写进去会让人工编辑的文件与 TS 形状不一致）。
  - **`TimetableJson.Deserialize` 会把缺失的集合补成空集合**：STJ 对位置记录缺字段给 `null` 且**不报错**，
    不补的话渲染层一遍历 `Slots`/`Sessions` 就崩（实测 `ArgumentNullException`）。缺 `term`/`courses` 返回 `null`，
    上层当"没有导入过"处理（`TimetableStore.Load` 里连"课程数为 0"也一并当没有）。
  - **`StringContent` 的默认 Content-Type 必须换掉**：粘贴来的 `content-type` 是内容头，
    `request.Headers.TryAddWithoutValidation` 加不进去（返回 false），要
    `content.Headers.Remove("Content-Type")` 后再加 —— 否则 POST 会按 `text/plain` 发出去。
    （本地合成服务实测：`contentType=application/json`、`method=POST`、`cookiePresent=True`。）
  - **异步回到 UI 线程**：`ImportService.FetchAsync` 里 `await` **不加** `ConfigureAwait(false)`
    （await 之后要经 `_apply` 回挂件窗口重画，XAML 只能 UI 线程碰）；`TongjiFetcher` 内部加，
    因为它只发请求不碰 UI。CLI 的 `--fetch-check` 会在 UI 线程上 `GetAwaiter().GetResult()` 阻塞等待，
    安全性正来自"网络层全是 `ConfigureAwait(false)`"。
  - **设置窗口句柄与文件选择器**：非打包应用的 `FileOpenPicker` 必须先
    `InitializeWithWindow.Initialize(picker, hwnd)`，否则弹不出来；`ContentDialog` 必须先赋 `XamlRoot`。
  - **"打开时停在第几页"必须走构造参数**（`SettingsWindow(..., initialPage)`）：只调 `SelectPage(n)`
    在"窗口还没加载"时可能不回调 `SelectionChanged`，于是托盘「导入课表…」**第一次点开会落在「常规」页**
    （截图实测到的 bug）。自检里 `shown=` 那一段就是钉这个的。
  - **卡片的主操作放标题行右侧**（`SettingsView.Block(..., action)`）：导入页的「获取我的课表」原本在输入框
    下方，落在首屏之外、打开页面根本看不到（截图实测）。凡是"这一页就是来干这件事"的按钮，都放标题行。
  - **设置窗口尺寸要按 DPI 折算**（`ResizeForDpi`）：`AppWindow.ResizeClient(980, 720)` 收的是**物理像素**，
    150% 缩放下窗口在屏幕上只有 653×480 DIP —— 左导航吃掉 208 DIP 后卡片只剩约 380 DIP 宽，
    说明文字一行只放得下十个字。现在按 `GetDpiForWindow()/96` 折算成 DIP 意图，并夹到工作区内
    （实测 150% → 客户区 1470×1080 px = 980×720 DIP）。
  - **它认两种同济课表接口**（诊断码 `tongji.personal` 与 `tongji.report`）：课表页那条报表接口把
    `calendarId` 放在 **URL 上**，所以抓取时由 `HttpRequestParser.QueryValue(spec, "calendarId")` 取出来
    当 `ImportInput.TermId` —— 不这么做学期会退化成"未知"（详见「易错知识点」里"两条接口两种包法"）。
