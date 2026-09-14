import { describe, expect, it } from 'vitest';
import {
  classesToCourses,
  defaultRegistry,
  extractDataObject,
  importTimetable,
  materializeTimetable,
  weeksToMask,
} from '../src/index.js';

const previewHtml = `<!DOCTYPE html><html><body>
<script>
const DATA = {"term": {"id": 122, "year": 2026, "termNo": 1}, "classes": [
 {"code": "5000295005501", "courseCode": "50002950055", "name": "习近平新时代中国特色社会主义思想概论",
  "teachers": "姚莉萍(07154)", "room": "一教126", "faculty": "马克思主义学院",
  "periods": [{"day": 4, "start": 5, "end": 7, "weeksMask": 65535, "weeksLabel": "1-16"}]},
 {"code": "32000105", "courseCode": "320001", "name": "体育(1)", "teachers": "秦海权(09102)",
  "room": "游泳馆", "faculty": "体育部",
  "periods": [{"day": 1, "start": 1, "end": 2, "weeksMask": 21845, "weeksLabel": "1, 3, 5, 7, 9, 11, 13, 15"}]}
]};
</script></body></html>`;

describe('preview-html adapter', () => {
  it('从 HTML 中提取 DATA 并导入教学班', () => {
    const result = importTimetable({ text: previewHtml });
    expect(result.adapterId).toBe('preview-html');
    expect(result.term.id).toBe('122');
    expect(result.term.year).toBe(2026);
    expect(result.candidates).toHaveLength(2);
    expect(result.candidates![0]!.sessions[0]).toMatchObject({ day: 4, startSlot: 5, endSlot: 7, room: '一教126' });
    expect(result.candidates![0]!.teachers).toEqual(['姚莉萍(07154)']);
    expect(result.candidates![1]!.sessions[0]!.weeks).toBe(weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]));
  });

  it('括号配对扫描能容忍字符串里的花括号', () => {
    const extracted = extractDataObject('const DATA = {"a": "}{", "b": 1};');
    expect(extracted).toEqual({ a: '}{', b: 1 });
  });

  it('classesToCourses 直接可用', () => {
    const data = extractDataObject(previewHtml) as Parameters<typeof classesToCourses>[0];
    const { term, courses } = classesToCourses(data);
    expect(term.totalWeeks).toBe(16);
    expect(courses).toHaveLength(2);
  });
});

describe('generic-json adapter', () => {
  const timetableJson = JSON.stringify({
    schemaVersion: 1,
    term: { id: '2026-1', name: '2026-2027学年第1学期', year: 2026, termNo: 1, startDate: '2026-09-14', totalWeeks: 16 },
    courses: [
      {
        id: 'c1',
        name: '线性代数',
        teachers: ['李四(22334)'],
        sessions: [{ day: 2, startSlot: 3, endSlot: 4, weeks: 65535, room: '南202' }],
      },
    ],
  });

  it('导入本项目导出格式（直接给出课程，无需勾选）', () => {
    const result = importTimetable({ text: timetableJson });
    expect(result.adapterId).toBe('generic-json');
    expect(result.courses).toHaveLength(1);
    expect(result.candidates).toBeUndefined();
    const timetable = materializeTimetable(result);
    expect(timetable.courses[0]!.sessions[0]).toMatchObject({ day: 2, startSlot: 3, endSlot: 4, room: '南202' });
  });

  it('支持 weeks 用数组写法', () => {
    const result = importTimetable({
      text: JSON.stringify({
        term: { totalWeeks: 16 },
        courses: [{ name: '大学化学', sessions: [{ day: 5, startSlot: 1, endSlot: 2, weeks: [1, 3, 5] }] }],
      }),
    });
    const course = result.courses[0]!;
    expect(course.sessions[0]!.weeks).toBe(weeksToMask([1, 3, 5]));
    expect(course.name).toBe('大学化学');
  });

  it('探测优先级：同济 > 排课导出 > 通用', () => {
    expect(defaultRegistry.best({ text: timetableJson })?.adapter.id).toBe('generic-json');
    expect(defaultRegistry.best({ text: previewHtml })?.adapter.id).toBe('preview-html');
    expect(defaultRegistry.best({ text: '{"foo":1}' })).toBeNull();
  });

  it('指定未知适配器报错', () => {
    expect(() => importTimetable({ text: timetableJson, adapterId: 'nope' })).toThrow(/未知适配器/);
  });
});
