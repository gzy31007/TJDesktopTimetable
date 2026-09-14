/**
 * @tjt/core —— 课表核心：统一模型 + 算法 + 多校适配器。
 *
 * 该包不依赖 Electron / Vue / DOM，可同时在 Node、浏览器与单元测试中运行。
 */

export * from './model.js';
export * from './weeks.js';
export * from './conflict.js';
export * from './colors.js';
export * from './layout.js';
export * from './time.js';
export * from './import.js';
export * from './adapters/types.js';
export * from './adapters/registry.js';
export { tongjiMajorAdapter, TONGJI_MAJOR_ADAPTER_ID, parseTeachersFromValue } from './adapters/tongji-major.js';
export { tongjiStudentAdapter, TONGJI_STUDENT_ADAPTER_ID } from './adapters/tongji-student.js';
export { previewHtmlAdapter, PREVIEW_HTML_ADAPTER_ID, extractDataObject, classesToCourses } from './adapters/preview-html.js';
export { genericJsonAdapter, GENERIC_ADAPTER_ID } from './adapters/generic.js';
