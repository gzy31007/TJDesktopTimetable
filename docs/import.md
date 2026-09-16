# 课表导入：实现细节与踩坑

> 本文件是 AGENTS.md 里**课表导入**细节的逐字搬运（2026-09-16 结构拆分）。入口、分层、载入顺序、安全口径、诊断 CLI、验收仍在 AGENTS.md；这里放实现细节与踩坑。

  - **`ImportService` 不认识窗口**：构造时拿三个回调（`apply` = 落盘 + 重画 / `reload` = 按载入顺序重读 /
    `describe` = 当前课表摘要），由 `App` 接到挂件窗口；设置窗口也**不认识 `MainWindow`**，一切经
    `SettingsWindow.SettingsHost`。`apply` 里对"窗口还没建"做了兜底（直接落盘）—— 否则 `--import`
    会因为那一刻 `_windows` 还是空的而静默失败。
  - **落盘格式沿用旧 Electron 线的形状**：`TimetableJson` 用 camelCase、逐字段对齐当年 TS 的 `Timetable` 接口，
    因此 `%APPDATA%\TJDesktopTimetable\timetable.json` **能直接读入旧版本写出的文件**
    （`credentials.json` 同理：`{ tongjiRequest, savedAt }`）。`Term.Label` 加了 `[JsonIgnore]`
    （当年 TS 没这个字段，写进去会让人工编辑的文件形状不一致）。
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
  - ⚠️ **说明文字别放进水平 `StackPanel`**：水平 StackPanel 用**无限宽度**测量子元素 → `TextWrapping`
    直接失效，设置窗口一缩小文字就横着溢出、不会折行（2026-09-17 用户实测"提示没有折叠"）。
    要么让它单独占一行（放进垂直 StackPanel），要么给固定宽度。导入页登录区就是这么改的：
    `登录行（下拉框 + 按钮）` 与 `说明文字` 拆成两个子元素。
  - **学校选择用下拉框 + 一个按钮**，不是"每校一个按钮"：按钮写死校名，加一所学校就要加一个按钮
    加一列回调（`SettingsHost.OpenLogin` / `OpenSjtuLogin` 现在是按 `schoolCombo.SelectedIndex` 分发）。
  - **设置窗口尺寸要按 DPI 折算**（`ResizeForDpi`）：`AppWindow.ResizeClient(980, 720)` 收的是**物理像素**，
    150% 缩放下窗口在屏幕上只有 653×480 DIP —— 左导航吃掉 208 DIP 后卡片只剩约 380 DIP 宽，
    说明文字一行只放得下十个字。现在按 `GetDpiForWindow()/96` 折算成 DIP 意图，并夹到工作区内
    （实测 150% → 客户区 1470×1080 px = 980×720 DIP）。
  - **它认两种同济课表接口**（诊断码 `tongji.personal` 与 `tongji.report`）：课表页那条报表接口把
    `calendarId` 放在 **URL 上**，所以抓取时由 `HttpRequestParser.QueryValue(spec, "calendarId")` 取出来
    当 `ImportInput.TermId` —— 不这么做学期会退化成"未知"（详见「易错知识点」里"两条接口两种包法"）。
  - **内置登录窗口与粘贴请求共用同一条落盘路径**：`TongjiLoginWindow`（WebView2）把捕获到的响应交给
    `ImportService.ApplyCapturedResponse` → 内部的 `Apply`（与 `FetchAsync` 完全一致），所以诊断、
    探测行、学期解析、"解析出 0 门课"这些行为两边不会分叉。窗口本身只做三件事：把 WebView2 事件喂给
    `TjtCore/TongjiWebCapture`（纯函数：URL 是不是课表接口 / `calendarId` 在哪 / cookie 怎么拼）、
    读响应体、成功后关窗。**不猜 `studentCode`**（前端加密的 uid）：页面自己发那条请求，我们只旁观。
  - **`TongjiFetcher.FetchSpecAsync` 是从 `FetchAsync` 里抽出来的**：前者吃"已解析好的
    `HttpRequestSpec`"并负责发送/探测/文案，后者只剩"解析粘贴文本 + 没 Cookie 时的引导提示"。
    登录窗口的兜底（响应体读不到时用页面 cookie 重发一次 GET）复用它 —— 这是**抽取而不是复制**，
    以后改发送细节只需改一处。
  - **交大（学在交大 / `j.sjtu.edu.cn`）走的是"改写请求"而不是"原样重发"**：课表页按周拉取
    （`GET /app/stu/lesson/listByWeek?year=&semester=&week=`），而按周响应里的 `time` 是 `null`
    —— 没有"这门课上哪些周"（实测 14 条全部如此），照它建挂件只会剩本周有课。所以
    `Data/SchoolFetcher.cs` 按**主机**分派：交大交给 `Data/SjtuFetcher.cs`，只取粘贴请求里的
    `year`/`semester` 与 Cookie，依次取三条 —— `calendar/info`（当前学期）→
    `listBySemester`（整学期课表，带 `time`）→ `semester/calendar`（教务日历，作为附加
    `ImportFile` 交给适配器）。请求一律重建到 `SjtuTerms.SjtuHost`，**不跟随粘贴内容里的主机**
    （粘错了地址也不会把登录态发去别处）。课表页那条 URL 只用来取参数，`userId` 前端自己也传
    `undefined`（被 axios 丢掉），我们同样不传。
  - **交大开学日是"第 1 周周一"，不是 `startDay`**：`semester/calendar` 返回**逐日**日历
    （`{week, weekDay, day}`，week 0-21），`startDay=2026-09-07` 是第 0 周（报到周），
    第 1 周周一是 `2026-09-14` —— 与 `listByWeek?week=1` 里各条的 `detailTime` 对得上。
    总周数取日历里最大的 `week`（实测 21，正好覆盖到 `endDay`）。
  - **交大单双周必须在区间展开之后过滤**：`time="5周,9周,13-15周"` + `suffix=["单周"]` 的正确结果是
    5/9/13/15；反过来先按单周筛会把 `13-15` 当成"第 13 周"、只剩 5/9/13。见
    `SjtuTerms.ParseWeeks` 与 `SjtuCaptureTests.单双周在区间展开之后过滤`。
  - **交大相邻节次要并成一块**：前端 `WeekTable.mergeSameClass` 按 `day + jxbId` 分组、节次相邻
    （`下一段起始 == 当前段结束 + 1`）就合并 —— 实测"民法总论"是 `["6","6"]` + `["7","8"]`，
    前端显示成 6-8 一整块。适配器同口径，但**周次取并集**（前端合并时只 `{...list[m]}` 保留第一条，
    会把周次丢掉）。同一天同一教学班的**重复记录**也算命中，避免画出两层色块。
  - **交大节次是 13 节**（教务处《上海交通大学学生上课时间表》）：1-4 上午、5-6 中午、7-10 下午、
    11-13 晚上（官方原文"第十一、十二、十三节 晚上 3 节连上 18:00-20:20"）。后端偶尔给第 14 节，
    前端 `14 == t && (t = 13)` 折算成 13，适配器同口径（`SjtuStudentAdapter.ClampSlot`）。
  - **交大响应包装是 `{errno, error, data}`**（同济是 `{code, msg, data}`）：成功 `errno="0"`、
    `error="成功"`；课表为空时前端会看到 `errno="99999"`（`WeekTable` 专门判了它）。
    探测在 `TjtCore/SjtuResponseProbe.cs`（与同济的 `TongjiResponseProbe` 并列，互不干扰）。
  - **交大没有教师字段**：`listBySemester` 只给 `name`/`code`/`jxbId`/`address`/`duration`/`time`/
    `suffix`/`credit`，教师只在 `lesson/detail`（参数 `code=jxbId`）里 —— 挂件色块不显示教师，
    所以不额外发那一次请求，`Course.Teachers` 留空。
  - **交大登录窗口是"主动取"而不是"接住页面响应"**：jAccount 是标准 OAuth2，课表接口只要明文的
    `year`/`semester` —— 所以 `TongjiLoginWindow`（现按 `LoginSchool` 分派，两校共用）在撞到
    `j.sjtu.edu.cn` 的课表/日历接口后，用页面自己的 cookie 调 `SjtuFetcher` 取整学期数据，
    再走与同济同一个 `ImportService.ApplyCapturedResponse`（多两个参数：`adapterId` 与附加 `files`）。
    触发用 `_sjtuCapturing` 闸门保证只抓一次（页面一次加载会连发好几条接口）。
