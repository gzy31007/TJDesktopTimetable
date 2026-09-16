namespace Tjt.App.Data;

/// <summary>
/// 内置样例课表：**虚构的演示数据**（课程名都以「示例」开头，教师 / 教室都是编的），
/// 覆盖周一到周五、1-10 节，并保留两种值得肉眼核对的场景：同格并排分列、单双周交替。
///
/// <para>它是"没有导入过课表"时挂件上显示的东西。刻意**不**用 <c>dotnet/fixtures/</c> 里的
/// 黄金数据当回退：那是真实抓包（脱敏但不虚构），拿它当演示会让用户以为"程序怎么有我的课表"。
/// 黄金数据只服务于测试与 <c>--fixture</c>。</para>
///
/// <para>布局口径由 <c>TjtCore.Tests/LayoutTests.内置样例形状_…</c> 用同形状的构造课程钉住
/// （那份测试不读本文件，改这里的课程不会影响它）。</para>
/// </summary>
internal static class DemoData
{
    /// <summary>选课服务格式（<c>data.selectedCourses[].course.times[]</c>）的演示样例。</summary>
    public const string PersonalJson = """
    {
      "code": 200,
      "data": {
        "calendarId": 122,
        "selectedCourses": [
          {
            "course": {
              "courseCode": "90000000001",
              "courseName": "示例高等数学（工科类）",
              "teachClassId": 9000000000000001,
              "teachClassCode": "9000000000001",
              "times": [
                { "timeStart": 1, "timeEnd": 2, "dayOfWeek": 1, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10001", "teacherCodeI18n": "张三", "roomIdI18n": "南101" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000002",
              "courseName": "示例大学物理",
              "teachClassId": 9000000000000002,
              "teachClassCode": "9000000000000002",
              "times": [
                { "timeStart": 1, "timeEnd": 2, "dayOfWeek": 1, "weeks": [1,3,5,7,9,11,13,15],
                  "teacherCode": "10002", "teacherCodeI18n": "李四", "roomIdI18n": "北201" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000003",
              "courseName": "示例大学英语",
              "teachClassId": 9000000000000003,
              "teachClassCode": "9000000000000003",
              "times": [
                { "timeStart": 1, "timeEnd": 2, "dayOfWeek": 1, "weeks": [2,4,6,8,10,12,14,16],
                  "teacherCode": "10003", "teacherCodeI18n": "王五", "roomIdI18n": "南305" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000005",
              "courseName": "示例并行交替",
              "teachClassId": 9000000000000005,
              "teachClassCode": "9000000000000005",
              "times": [
                { "timeStart": 5, "timeEnd": 6, "dayOfWeek": 3, "weeks": [1,2,3,4,5,6,7,8],
                  "teacherCode": "10005", "teacherCodeI18n": "孙七", "roomIdI18n": "南102" },
                { "timeStart": 5, "timeEnd": 6, "dayOfWeek": 3, "weeks": [9,10,11,12,13,14,15,16],
                  "teacherCode": "10006", "teacherCodeI18n": "周八", "roomIdI18n": "南102" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000006",
              "courseName": "示例程序设计基础",
              "teachClassId": 9000000000000006,
              "teachClassCode": "9000000000000006",
              "times": [
                { "timeStart": 3, "timeEnd": 4, "dayOfWeek": 2, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10007", "teacherCodeI18n": "陈九", "roomIdI18n": "东203" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000007",
              "courseName": "示例线性代数",
              "teachClassId": 9000000000000007,
              "teachClassCode": "9000000000000007",
              "times": [
                { "timeStart": 7, "timeEnd": 8, "dayOfWeek": 2, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10008", "teacherCodeI18n": "吴十", "roomIdI18n": "北105" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000008",
              "courseName": "示例数据结构",
              "teachClassId": 9000000000000008,
              "teachClassCode": "9000000000000008",
              "times": [
                { "timeStart": 3, "timeEnd": 4, "dayOfWeek": 4, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10009", "teacherCodeI18n": "郑一", "roomIdI18n": "南401" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000009",
              "courseName": "示例体育（篮球）",
              "teachClassId": 9000000000000009,
              "teachClassCode": "9000000000000009",
              "times": [
                { "timeStart": 5, "timeEnd": 6, "dayOfWeek": 5, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10010", "teacherCodeI18n": "冯二", "roomIdI18n": "体育馆" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000010",
              "courseName": "示例专业导论",
              "teachClassId": 9000000000000010,
              "teachClassCode": "9000000000000010",
              "times": [
                { "timeStart": 9, "timeEnd": 10, "dayOfWeek": 5, "weeks": [1,2,3,4,5,6,7,8],
                  "teacherCode": "10011", "teacherCodeI18n": "褚三", "roomIdI18n": "东报告厅" }
              ]
            }
          }
        ]
      }
    }
    """;
}
