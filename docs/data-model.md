# 数据模型

`packages/core/src/model.ts` 是唯一真源。所有适配器的输出、所有存储与 UI 的输入都是这里的结构。

## 类型

```ts
type Weekday = 1 | 2 | 3 | 4 | 5 | 6 | 7;   // 1 = 周一 … 7 = 周日
type Weeks   = number;                       // 位掩码，bit0 = 第 1 周

interface Slot   { index: number; begin: string; end: string }        // "08:00" / "08:45"
interface Term   { id, name, year, termNo, startDate?, totalWeeks, slots: Slot[] }
interface Session{ id, day, startSlot, endSlot, weeks, room? }
interface Course { id, name, courseCode?, teachingClassCode?, teachers[], faculty?, campus?, color?, sessions[] }
interface Timetable { schemaVersion: 1, term, courses, source: { adapterId, adapterVersion, importedAt } }
```

关键概念：

- **Course = 教学班**（用户视角的"一门课"）。同一门课的不同教学班是不同 `Course`，靠 `courseCode` 相同来识别"同课不同班"。
- **Session = 一次上课安排**（某天 + 连续节次 + 一组周次 + 一个教室）。一门课一周上两次就是两个 Session。
- `id`/`teachingClassCode`/`courseCode` 三者不同：同济数据里分别是 `teachingClassId`（数字 id）、`code`（教学班代码 `00213702`）、`courseCode`（课程代码 `002137`）。**去重用 `id`**。

## 周次掩码

- `bit0` = 第 1 周，`bit15` = 第 16 周，所以 16 周全周 = `0xFFFF` = `65535`（与同济 `weekState` 完全一致）。
- `formatWeeksLabel(65535) === '1-16'`；`'1, 3, 5, 7, 9, 11, 13, 15'` 这种单周课用 `oddWeekMask(16) === 0x5555`。
- 单双周掩码**按 `totalWeeks` 生成**，不要硬编码（20 周学期时 `0x5555` 会截断）。
- 掩码最高位超过 `totalWeeks` 时不会丢数据（`maskToWeeks` 取 `max(totalWeeks, 最高位)`）。

## 时间

- 日期一律用 `YYYY-MM-DD` 字符串表示"学期所在地的日历日"，内部转成 UTC 日序号做纯日期算术，避免时区偏移。
- `Term.startDate` = **第 1 周周一**。同济校历的 `beginDay` 是北京时间午夜的毫秒时间戳（`1789315200000` → `2026-09-14`），适配器用 `msToIsoDate` 换算后再对齐到周一。
- `dayOfWeek` 7 = 周日；JS `Date.getDay()` 0 = 周日，转换见 `dayNumberToWeekday`。

## 同济 `timetable/major` 字段映射

| 原始字段 | 模型字段 | 说明 |
|---|---|---|
| `teachingClassId` | `Course.id` | 数字，去重键（实测 147 条 → 128 个） |
| `code` | `Course.teachingClassCode` | 教学班代码字符串 |
| `courseCode` | `Course.courseCode` | 课程代码 |
| `courseName` | `Course.name` | |
| `value` / `newValue` | `Course.teachers` | 正则 `姓名(工号)`；缺失时退回 `teacherCodes` |
| `facultyI18n` / `campusI18n` | `Course.faculty` / `Course.campus` | |
| `roomName` | `Session.room` | 同班不同时段可不同教室 |
| `dayOfWeek` | `Session.day` | 1–7，7 = 周日 |
| `timeStart` / `timeEnd` | `Session.startSlot` / `endSlot` | 1–11 节 |
| `weekState` | `Session.weeks` | 16 位掩码 |
| 校历 `noWeekendWorkTimes[].classNode/beginTime/endTime` | `Term.slots` | 11 节时间表 |
| 校历 `beginDay` / `teachingWeekStart` / `teachingWeekEnd` | `Term.startDate` / `totalWeeks` | |
| 校历 `fullName` / `year` / `term` | `Term.name` / `year` / `termNo` | |
| 学生信息 `profession(I18n)` / `grade` / `facultyI18n` | `ImportResult.meta.student` | 仅展示，不落盘敏感字段 |

## 兼容与版本

- `Timetable.schemaVersion` 目前为 `1`。修改模型时：新增可选字段不算破坏性变更；删除或改语义需要升版本并在读取时做迁移。
- 落盘文件不做压缩，方便用户手改与版本管理。
- `select_preview.html` 风格的 `{term, classes}` JSON 由 `preview-html` 适配器兼容，属于"旧格式入口"，不是主模型。
