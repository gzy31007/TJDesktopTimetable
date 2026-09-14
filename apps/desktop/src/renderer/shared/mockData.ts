import { TONGJI_DEFAULT_SLOTS, weeksToMask, type Timetable } from '@tjt/core';

/** `pnpm dev:web` 用的示例课表（真实同济时间表结构 + 单双周样例）。 */

const ALL = weeksToMask(Array.from({ length: 16 }, (_, i) => i + 1));
const ODD = weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]);
const EVEN = weeksToMask([2, 4, 6, 8, 10, 12, 14, 16]);

export function mockTimetable(): Timetable {
  return {
    schemaVersion: 1,
    term: {
      id: '122',
      name: '2026-2027学年第1学期',
      year: 2026,
      termNo: 1,
      startDate: '2026-09-14',
      totalWeeks: 16,
      slots: TONGJI_DEFAULT_SLOTS.map((s) => ({ ...s })),
    },
    courses: [
      {
        id: 'demo-1',
        name: '高等数学（工科类）',
        courseCode: 'MATH101',
        teachingClassCode: 'MATH10101',
        teachers: ['张三(12345)'],
        faculty: '数学科学学院',
        sessions: [
          { id: 'demo-1-a', day: 1, startSlot: 1, endSlot: 2, weeks: ALL, room: '南101' },
          { id: 'demo-1-b', day: 3, startSlot: 3, endSlot: 4, weeks: ALL, room: '南101' },
        ],
      },
      {
        id: 'demo-2',
        name: '大学物理',
        courseCode: 'PHYS101',
        teachingClassCode: 'PHYS10102',
        teachers: ['李四(22334)'],
        faculty: '物理科学与工程学院',
        sessions: [{ id: 'demo-2-a', day: 1, startSlot: 3, endSlot: 4, weeks: ODD, room: '北201' }],
      },
      {
        id: 'demo-3',
        name: '大学英语',
        courseCode: 'ENG101',
        teachingClassCode: 'ENG10103',
        teachers: ['王五(33445)'],
        faculty: '外国语学院',
        sessions: [{ id: 'demo-3-a', day: 1, startSlot: 3, endSlot: 4, weeks: EVEN, room: '一教324' }],
      },
      {
        id: 'demo-4',
        name: '程序设计基础',
        courseCode: 'CS101',
        teachingClassCode: 'CS10101',
        teachers: ['赵六(44556)'],
        faculty: '计算机科学与技术学院',
        sessions: [
          { id: 'demo-4-a', day: 2, startSlot: 5, endSlot: 7, weeks: ALL, room: '机房 A' },
          { id: 'demo-4-b', day: 4, startSlot: 9, endSlot: 11, weeks: ODD, room: '机房 B' },
        ],
      },
      {
        id: 'demo-5',
        name: '思想道德与法治',
        courseCode: 'POL101',
        teachingClassCode: 'POL10107',
        teachers: ['孙七(55667)'],
        faculty: '马克思主义学院',
        sessions: [{ id: 'demo-5-a', day: 3, startSlot: 7, endSlot: 8, weeks: ALL, room: '一教126' }],
      },
      {
        id: 'demo-6',
        name: '体育（1）',
        courseCode: 'PE101',
        teachingClassCode: 'PE10112',
        teachers: ['周八(66778)'],
        faculty: '体育部',
        sessions: [{ id: 'demo-6-a', day: 5, startSlot: 1, endSlot: 2, weeks: ALL, room: '游泳馆' }],
      },
      {
        id: 'demo-7',
        name: '工程实践',
        courseCode: 'PRAC101',
        teachingClassCode: 'PRAC10101',
        teachers: ['吴九(77889)'],
        faculty: '工程实践中心',
        sessions: [{ id: 'demo-7-a', day: 6, startSlot: 1, endSlot: 4, weeks: weeksToMask([9, 10, 11, 12]) }],
      },
    ],
    source: { adapterId: 'mock', adapterVersion: '1.0.0', importedAt: new Date().toISOString() },
  };
}
