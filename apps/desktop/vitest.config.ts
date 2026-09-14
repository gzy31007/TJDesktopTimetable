import { defineConfig } from 'vitest/config';

/**
 * 主进程侧的纯逻辑单测。
 *
 * 只覆盖"零平台依赖"的部分（例如静息落点策略）：涉及 koffi/user32 的模块在
 * WSL/Linux 下根本加载不了，它们只能在 Windows 真机验收。
 */
export default defineConfig({
  test: {
    include: ['test/**/*.spec.ts'],
    environment: 'node',
    globals: false,
  },
});
