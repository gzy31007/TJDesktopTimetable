namespace Tjt.Linux.Data;

/// <summary>
/// 内置样例课表（Tjt.App/Data/DemoData.cs 的原样移植）：
/// 同一套字段、同一组"同格撞车"场景，但只有 5 门课，方便肉眼核对并排 / 单双周 / 跨节重叠。
///
/// 只在读不到 fixtures 时兜底。刻意**不**含任何个人信息。
/// </summary>
internal static class DemoData
{
    /// <summary>选课服务格式（<c>data.selectedCourses[].course.times[]</c>）的最小样例。</summary>
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
              "teachClassCode": "9000000000000001",
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
              "courseCode": "90000000004",
              "courseName": "示例跨节实践（部分重叠）",
              "teachClassId": 9000000000000004,
              "teachClassCode": "9000000000000004",
              "times": [
                { "timeStart": 1, "timeEnd": 3, "dayOfWeek": 1, "weeks": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16],
                  "teacherCode": "10004", "teacherCodeI18n": "赵六", "roomIdI18n": "实验楼" }
              ]
            }
          },
          {
            "course": {
              "courseCode": "90000000005",
              "courseName": "示例并行交替甲",
              "teachClassId": 9000000000000005,
              "teachClassCode": "9000000000000005",
              "times": [
                { "timeStart": 5, "timeEnd": 6, "dayOfWeek": 3, "weeks": [1,2,3,4,5,6,7,8],
                  "teacherCode": "10005", "teacherCodeI18n": "孙七", "roomIdI18n": "南102" },
                { "timeStart": 5, "timeEnd": 6, "dayOfWeek": 3, "weeks": [9,10,11,12,13,14,15,16],
                  "teacherCode": "10006", "teacherCodeI18n": "周八", "roomIdI18n": "南102" }
              ]
            }
          }
        ]
      }
    }
    """;
}
