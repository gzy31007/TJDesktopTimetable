<script setup lang="ts">
import { computed, ref } from 'vue';
import { formatWeeksLabel, WEEKDAY_LABELS, type Course } from '@tjt/core';

/** 教学班勾选清单（迁移 `select_preview.html` 的左侧列表 + 冲突拦截语义）。 */

const props = defineProps<{
  candidates: Course[];
  selectedIds: string[];
  totalWeeks: number;
  title?: string;
}>();

const emit = defineEmits<{ (event: 'toggle', course: Course): void }>();

const keyword = ref('');
const openGroups = ref<Record<string, boolean>>({});
const flashId = ref('');

const selected = computed(() => new Set(props.selectedIds));

const filtered = computed(() => {
  const key = keyword.value.trim();
  if (!key) return props.candidates;
  return props.candidates.filter(
    (course) => course.name.includes(key) || course.teachers.some((t) => t.includes(key)) || (course.teachingClassCode ?? '').includes(key),
  );
});

const groups = computed(() => {
  const map = new Map<string, Course[]>();
  for (const course of filtered.value) {
    const list = map.get(course.name);
    if (list) list.push(course);
    else map.set(course.name, [course]);
  }
  return [...map.entries()].map(([name, items]) => ({
    name,
    items,
    selectedCount: items.filter((c) => selected.value.has(c.id)).length,
  }));
});

function isOpen(name: string, index: number): boolean {
  const state = openGroups.value[name];
  if (state === undefined) return index < 4;
  return state;
}

function toggleGroup(name: string, index: number): void {
  openGroups.value = { ...openGroups.value, [name]: !isOpen(name, index) };
}

function timeText(course: Course): string {
  return course.sessions
    .map(
      (session) =>
        `${WEEKDAY_LABELS[session.day]} 第${session.startSlot}-${session.endSlot}节 [${formatWeeksLabel(session.weeks, props.totalWeeks)}]`,
    )
    .join('；');
}

function onToggle(course: Course): void {
  emit('toggle', course);
}

/** 外部（冲突拦截）调用：红色抖动提示。 */
function flash(courseId: string): void {
  flashId.value = courseId;
  window.setTimeout(() => {
    if (flashId.value === courseId) flashId.value = '';
  }, 900);
}

defineExpose({ flash });
</script>

<template>
  <section class="picker">
    <header class="picker-head">
      <h2>{{ title ?? '2 · 勾选我的教学班' }}</h2>
      <span class="stats">已选 {{ selectedIds.length }} / {{ candidates.length }}</span>
    </header>

    <input v-model="keyword" class="search" type="search" placeholder="搜索课程名 / 教师 / 教学班代码" />

    <div class="list">
      <div v-for="(group, index) in groups" :key="group.name" class="group">
        <button type="button" class="group-head" @click="toggleGroup(group.name, index)">
          <span class="name">{{ group.name }}</span>
          <span class="meta">{{ group.items.length }} 班 · 已选 {{ group.selectedCount }}</span>
          <span class="chev">{{ isOpen(group.name, index) ? '▾' : '▸' }}</span>
        </button>

        <div v-show="isOpen(group.name, index)" class="group-body">
          <button
            v-for="course in group.items"
            :key="course.id"
            type="button"
            class="class-card"
            :class="{ sel: selected.has(course.id), blocked: flashId === course.id }"
            @click="onToggle(course)"
          >
            <span class="row1">
              <b>{{ course.teachers.join('、') || '教师待定' }}</b>
              <span class="code">{{ course.teachingClassCode ?? course.id }}</span>
            </span>
            <span class="time">{{ timeText(course) }}</span>
            <span class="room">{{ course.sessions[0]?.room ?? course.campus ?? '' }}{{ course.faculty ? ` · ${course.faculty}` : '' }}</span>
            <span class="badge">时间冲突</span>
          </button>
        </div>
      </div>

      <p v-if="!groups.length" class="empty">没有匹配的教学班</p>
    </div>
  </section>
</template>

<style scoped>
.picker {
  display: flex;
  flex-direction: column;
  min-height: 0;
  border: 1px solid #d5dde8;
  border-radius: 10px;
  background: #fff;
  overflow: hidden;
  flex: 1 1 auto;
}
.picker-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid #e8edf4;
  background: #f8fafc;
}
.picker-head h2 {
  font-size: 15px;
  margin: 0;
}
.stats {
  font-size: 12px;
  color: #6b7280;
}
.search {
  margin: 8px 12px;
  padding: 5px 10px;
  border: 1px solid #d5dde8;
  border-radius: 6px;
  font-size: 13px;
}
.list {
  overflow: auto;
  min-height: 0;
  flex: 1 1 auto;
  padding-bottom: 8px;
}
.group + .group {
  border-top: 1px solid #e8edf4;
}
.group-head {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  border: none;
  background: #fbfcfe;
  padding: 8px 12px;
  cursor: pointer;
  font-size: 13px;
  text-align: left;
}
.group-head:hover {
  background: #f0f5ff;
}
.group-head .name {
  flex: 1 1 auto;
  font-weight: 600;
}
.group-head .meta {
  font-size: 12px;
  color: #6b7280;
}
.group-head .chev {
  color: #9ca3af;
}
.group-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 6px 12px 10px;
}
.class-card {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 3px;
  border: 1px solid #d5dde8;
  border-radius: 8px;
  background: #fff;
  padding: 7px 10px;
  text-align: left;
  cursor: pointer;
  font-size: 12px;
  line-height: 1.5;
}
.class-card:hover {
  border-color: #2563eb;
}
.class-card.sel {
  border-color: #059669;
  background: #f0fdf4;
  box-shadow: inset 0 0 0 1px #059669;
}
.class-card.blocked {
  border-color: #dc2626;
  background: #fef2f2;
  animation: shake 0.3s;
}
.class-card .row1 {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}
.class-card .code {
  font-family: Consolas, monospace;
  color: #6b7280;
}
.class-card.sel .code {
  color: #059669;
}
.class-card .time {
  color: #2563eb;
  font-weight: 500;
}
.class-card .room {
  color: #6b7280;
}
.class-card .badge {
  display: none;
  position: absolute;
  right: 8px;
  bottom: 6px;
  font-size: 11px;
  color: #dc2626;
  background: #fee2e2;
  border-radius: 4px;
  padding: 0 6px;
}
.class-card.blocked .badge {
  display: inline-block;
}
.empty {
  padding: 16px;
  text-align: center;
  color: #6b7280;
  font-size: 13px;
}
@keyframes shake {
  0%,
  100% {
    transform: translateX(0);
  }
  25% {
    transform: translateX(-4px);
  }
  75% {
    transform: translateX(4px);
  }
}
</style>
