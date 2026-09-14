using Tjt.Core;
using Tjt.Core.Adapters;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 适配器层导入验收（阶段一：不依赖布局/时间模块）。
///
/// 逐条对齐 TS 侧 <c>packages/core/test/import.spec.ts</c> 的 7 个用例
/// （preview-html 3 个 + generic-json 4 个），并补上适配器注册表与同济个人课表
/// 字段语义的边界断言（探测打分、detect 抛异常兜底、教师正则、三键不混用、room 为空）。
///
/// 跨模块断言（<c>Layout.BuildBoard</c> / <c>Time.TermWeekAt</c> / 单双周过滤）在
/// <c>E2ETimetableTests.cs</c> 的阶段二补齐。
/// </summary>
public class ImportTests
{
    private const string PreviewHtml = """
<!DOCTYPE html><html><body>
<script>
const DATA = {"term": {"id": 122, "year": 2026, "termNo": 1}, "classes": [
 {"code": "5000295005501", "courseCode": "50002950055", "name": "习近平新时代中国特色社会主义思想概论",
  "teachers": "姚莉萍(07154)", "room": "一教126", "faculty": "马克思主义学院",
  "periods": [{"day": 4, "start": 5, "end": 7, "weeksMask": 65535, "weeksLabel": "1-16"}]},
 {"code": "32000105", "courseCode": "320001", "name": "体育(1)", "teachers": "秦海权(09102)",
  "room": "游泳馆", "faculty": "体育部",
  "periods": [{"day": 1, "start": 1, "end": 2, "weeksMask": 21845, "weeksLabel": "1, 3, 5, 7, 9, 11, 13, 15"}]}
]};
</script></body></html>
""";

    private static readonly string TimetableJson = """
{"schemaVersion": 1,
 "term": {"id": "2026-1", "name": "2026-2027学年第1学期", "year": 2026, "termNo": 1, "startDate": "2026-09-14", "totalWeeks": 16},
 "courses": [{"id": "c1", "name": "线性代数", "teachers": ["李四(22334)"],
   "sessions": [{"day": 2, "startSlot": 3, "endSlot": 4, "weeks": 65535, "room": "南202"}]}]}
""";

    private static ImportResult Import(ImportInput input) => ImportPipeline.ImportTimetable(input);

    // ── preview-html adapter（TS import.spec.ts 前 3 例）──────────────────────────

    [Fact]
    public void 从HTML中提取DATA并导入教学班()
    {
        var result = Import(new ImportInput { Text = PreviewHtml });
        Assert.Equal("preview-html", result.AdapterId);
        Assert.Equal("122", result.Term.Id);
        Assert.Equal(2026, result.Term.Year);
        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates!.Count);

        var session = result.Candidates[0].Sessions[0];
        Assert.Equal(Weekday.Thursday, session.Day);
        Assert.Equal(5, session.StartSlot);
        Assert.Equal(7, session.EndSlot);
        Assert.Equal("一教126", session.Room);
        Assert.Equal(["姚莉萍(07154)"], result.Candidates[0].Teachers);
        Assert.Equal(
            Weeks.FromWeeks([1, 3, 5, 7, 9, 11, 13, 15]),
            result.Candidates[1].Sessions[0].Weeks);
    }

    [Fact]
    public void 括号配对扫描能容忍字符串里的花括号()
    {
        using var extracted = PreviewHtmlAdapter.ExtractDataObject("""const DATA = {"a": "}{", "b": 1};""");
        Assert.NotNull(extracted);
        Assert.Equal("}{", extracted!.RootElement.GetProperty("a").GetString());
        Assert.Equal(1, extracted.RootElement.GetProperty("b").GetInt32());
    }

    [Fact]
    public void classesToCourses直接可用()
    {
        using var data = PreviewHtmlAdapter.ExtractDataObject(PreviewHtml);
        Assert.NotNull(data);
        var (term, courses) = PreviewHtmlAdapter.ClassesToCourses(data!.RootElement);
        Assert.Equal(16, term.TotalWeeks);
        Assert.Equal(2, courses.Count);
    }

    // ── generic-json adapter（TS import.spec.ts 后 4 例）──────────────────────────

    [Fact]
    public void 导入本项目导出格式()
    {
        var result = Import(new ImportInput { Text = TimetableJson });
        Assert.Equal("generic-json", result.AdapterId);
        Assert.Single(result.Courses);
        Assert.Null(result.Candidates);

        var timetable = ImportPipeline.MaterializeTimetable(result);
        var session = timetable.Courses[0].Sessions[0];
        Assert.Equal(Weekday.Tuesday, session.Day);
        Assert.Equal(3, session.StartSlot);
        Assert.Equal(4, session.EndSlot);
        Assert.Equal("南202", session.Room);
    }

    [Fact]
    public void 支持weeks用数组写法()
    {
        var result = Import(new ImportInput
        {
            Text = """
            {"term": {"totalWeeks": 16},
             "courses": [{"name": "大学化学", "sessions": [{"day": 5, "startSlot": 1, "endSlot": 2, "weeks": [1, 3, 5]}]}]}
            """,
        });
        var course = result.Courses[0];
        Assert.Equal(Weeks.FromWeeks([1, 3, 5]), course.Sessions[0].Weeks);
        Assert.Equal("大学化学", course.Name);
    }

    [Fact]
    public void 探测优先级为同济大于排课导出大于通用()
    {
        var registry = AdapterRegistry.Default;
        Assert.Equal("generic-json", registry.Best(new ImportInput { Text = TimetableJson })!.Value.Adapter.Id);
        Assert.Equal("preview-html", registry.Best(new ImportInput { Text = PreviewHtml })!.Value.Adapter.Id);
        Assert.Null(registry.Best(new ImportInput { Text = """{"foo":1}""" }));
    }

    [Fact]
    public void 指定未知适配器报错()
    {
        var error = Assert.Throws<ImportException>(() =>
            Import(new ImportInput { Text = TimetableJson, AdapterId = "nope" }));
        Assert.Equal("adapter.unknown", error.Code);
        Assert.Contains("未知适配器", error.Message, StringComparison.Ordinal);
    }

    // ── 注册表与探测打分的边界（阶段一补充）──────────────────────────────────────

    [Fact]
    public void 注册表的列表顺序与查找()
    {
        var registry = AdapterRegistry.Default;
        Assert.Equal(
            ["tongji-student", "preview-html", "generic-json"],
            registry.List().Select(adapter => adapter.Id));
        Assert.Same(PreviewHtmlAdapter.Instance, registry.Get("preview-html"));
        Assert.Same(TongjiStudentAdapter.Instance, registry.Get("tongji-student"));
        Assert.Null(registry.Get("nope"));

        // 导入管线不带 registry 时能拿到内置注册表（Registry 的静态构造已完成装配）
        Assert.Same(registry, ImportPipeline.ResolveRegistry());
    }

    [Fact]
    public void 探测打分与异常兜底()
    {
        // 同济：selectedCourses 是数组就算命中（空数组也算 0.98），扁平 weekState 0.9
        Assert.Equal(0.98, TongjiStudentAdapter.Instance.Detect(new ImportInput
        {
            Text = """{"data": {"selectedCourses": []}}""",
        }), 6);
        Assert.Equal(0.9, TongjiStudentAdapter.Instance.Detect(new ImportInput
        {
            Text = """{"data": [{"dayOfWeek": 1, "weekState": 65535}]}""",
        }), 6);
        Assert.Equal(0.0, TongjiStudentAdapter.Instance.Detect(new ImportInput { Text = """{"foo":1}""" }), 6);

        // 通用 JSON：带 schemaVersion 0.9 / 只有 courses 0.6 / classes 结构 0.4
        Assert.Equal(0.9, GenericJsonAdapter.Instance.Detect(new ImportInput { Text = TimetableJson }), 6);
        Assert.Equal(0.6, GenericJsonAdapter.Instance.Detect(new ImportInput
        {
            Text = """{"courses": [{"name": "x", "sessions": []}]}""",
        }), 6);
        Assert.Equal(0.4, GenericJsonAdapter.Instance.Detect(new ImportInput { Text = PreviewHtmlAsJson }), 6);

        // preview-html：HTML 里的 const DATA 0.85 / 裸 JSON 的 classes 0.75
        Assert.Equal(0.85, PreviewHtmlAdapter.Instance.Detect(new ImportInput { Text = PreviewHtml }), 6);
        Assert.Equal(0.75, PreviewHtmlAdapter.Instance.Detect(new ImportInput { Text = PreviewHtmlAsJson }), 6);

        // detect 抛异常 → 记 0 分，不向上冒泡
        var throwing = AdapterRegistry.Create([new ThrowingAdapter()]);
        Assert.Null(throwing.Best(new ImportInput { Text = "任意内容" }));

        // 一个正常 + 一个抛异常：正常者胜出
        var mixed = AdapterRegistry.Create([new ThrowingAdapter(), TongjiStudentAdapter.Instance]);
        Assert.Equal("tongji-student", mixed.Best(new ImportInput
        {
            Text = """{"data": {"selectedCourses": []}}""",
        })!.Value.Adapter.Id);
    }

    [Fact]
    public void 无法识别的输入报adapter_nomatch()
    {
        var error = Assert.Throws<ImportException>(() => Import(new ImportInput { Text = """{"foo":1}""" }));
        Assert.Equal("adapter.nomatch", error.Code);
    }

    // ── 同济个人课表的字段语义（阶段一补充）──────────────────────────────────────

    [Fact]
    public void 教师正则与姓名工号边界()
    {
        Assert.Equal(["欧凯(21158)"], TongjiStudentAdapter.ParseTeachersFromValue("大学物理 欧凯(21158) 星期一1-2节"));
        Assert.Equal(["李四(22334)", "王五(45678)"], TongjiStudentAdapter.ParseTeachersFromValue("李四(22334) 王五(45678)"));
        Assert.Empty(TongjiStudentAdapter.ParseTeachersFromValue("没有括号"));
        Assert.Empty(TongjiStudentAdapter.ParseTeachersFromValue("张三(12)"));        // 工号少于 3 位
        Assert.Empty(TongjiStudentAdapter.ParseTeachersFromValue("张三(1234567)"));   // 工号多于 6 位
        Assert.Empty(TongjiStudentAdapter.ParseTeachersFromValue("A(12345)"));        // 姓名不是 2-8 个汉字
        Assert.Empty(TongjiStudentAdapter.ParseTeachersFromValue(null));
    }

    [Fact]
    public void 教学班id与code与courseCode三键不混用()
    {
        var selected = """
        {"data": {"calendarId": 122, "selectedCourses": [
          {"course": {"courseName": "专业课", "courseCode": "50002810095", "teachClassId": 1111111124960363,
                      "teachClassCode": "5000281009505",
                      "times": [{"dayOfWeek": 3, "timeStart": 1, "timeEnd": 2, "weeks": [1]}]}}
        ]}}
        """;
        var course = Import(new ImportInput { Text = selected }).Courses[0];
        // 去重键用 teachClassId（可能出现超 int 的 16 位数字），教学班代码与课程代码各自独立
        Assert.Equal("1111111124960363", course.Id);
        Assert.Equal("5000281009505", course.TeachingClassCode);
        Assert.Equal("50002810095", course.CourseCode);

        // 扁平格式：没有 teachingClassId 时退到 code，再没有才拼 courseCode-day-startSlot
        var flatWithCode = """
        {"data": [{"code": "X01", "courseCode": "X", "courseName": "大学化学", "dayOfWeek": 2, "timeStart": 3,
                   "timeEnd": 4, "weekState": 65535}]}
        """;
        Assert.Equal("X01", Import(new ImportInput { Text = flatWithCode }).Courses[0].Id);

        var flatWithoutCode = """
        {"data": [{"courseCode": "X", "courseName": "大学化学", "dayOfWeek": 2, "timeStart": 3, "timeEnd": 4,
                   "weekState": 65535}]}
        """;
        Assert.Equal("X-2-3", Import(new ImportInput { Text = flatWithoutCode }).Courses[0].Id);
    }

    [Fact]
    public void 同一格多条times合并且周次取并集()
    {
        var payload = """
        {"data": {"calendarId": 122, "selectedCourses": [
          {"course": {"courseName": "专业导论", "courseCode": "P", "teachClassId": 7, "teachClassCode": "P01",
            "times": [
              {"dayOfWeek": 3, "timeStart": 9, "timeEnd": 10, "weeks": [1, 2], "roomIdI18n": "北201",
               "teacherCodeI18n": "张三", "teacherCode": "11111"},
              {"dayOfWeek": 3, "timeStart": 9, "timeEnd": 10, "weeks": [3], "roomIdI18n": "北201",
               "teacherCodeI18n": "李四", "teacherCode": "22222"}
            ]}}
        ]}}
        """;
        var course = Import(new ImportInput { Text = payload }).Courses[0];
        Assert.Single(course.Sessions);
        Assert.Equal(Weeks.FromWeeks([1, 2, 3]), course.Sessions[0].Weeks);
        Assert.Equal(["张三(11111)", "李四(22222)"], course.Teachers);

        // 教室不同的两条 times 不合并（并排显示）
        var twoRooms = """
        {"data": {"calendarId": 122, "selectedCourses": [
          {"course": {"courseName": "实验", "courseCode": "E", "teachClassId": 8, "teachClassCode": "E01",
            "times": [
              {"dayOfWeek": 3, "timeStart": 9, "timeEnd": 10, "weeks": [1], "roomIdI18n": "北201"},
              {"dayOfWeek": 3, "timeStart": 9, "timeEnd": 10, "weeks": [2], "roomIdI18n": "北202"}
            ]}}
        ]}}
        """;
        Assert.Equal(2, Import(new ImportInput { Text = twoRooms }).Courses[0].Sessions.Count);
    }

    [Fact]
    public void 没有教室时room为空而不是0()
    {
        var payload = """
        {"data": {"calendarId": 122, "selectedCourses": [
          {"course": {"courseName": "体育(1)", "courseCode": "TY", "teachClassId": 9, "teachClassCode": "TY01",
            "times": [{"dayOfWeek": 1, "timeStart": 1, "timeEnd": 2, "weeks": [1, 2, 3]}]}}
        ]}}
        """;
        var session = Import(new ImportInput { Text = payload }).Courses[0].Sessions[0];
        Assert.Null(session.Room);
        Assert.Equal(Weeks.FromWeeks([1, 2, 3]), session.Weeks);
    }

    /// <summary>`{term, classes}` 结构的裸 JSON（preview-html 与 generic 都能识别）。</summary>
    private static readonly string PreviewHtmlAsJson = """
    {"term": {"id": 122, "year": 2026, "termNo": 1},
     "classes": [{"code": "C1", "courseCode": "CC", "name": "课程", "teachers": "张三(12345)",
                  "room": "一教101", "periods": [{"day": 1, "start": 1, "end": 2, "weeksMask": 65535}]}]}
    """;

    /// <summary>探测会抛异常的适配器：验证注册表"Detect 抛异常记 0 分"。</summary>
    private sealed class ThrowingAdapter : ISchoolAdapter
    {
        public string Id => "throwing";

        public string DisplayName => "会抛异常的适配器";

        public string Version => "0.0.0";

        public string Description => "只用于注册表兜底测试。";

        public double Detect(ImportInput input) => throw new InvalidOperationException("detect 故意失败");

        public ImportResult Parse(ImportInput input, AdapterContext context) => throw new InvalidOperationException("parse 故意失败");
    }
}
