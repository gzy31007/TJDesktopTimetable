import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import vue from '@vitejs/plugin-vue';
import { defineConfig, externalizeDepsPlugin } from 'electron-vite';

const root = fileURLToPath(new URL('.', import.meta.url));
const coreAlias = { '@tjt/core': resolve(root, '../../packages/core/src/index.ts') };

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias: coreAlias },
    build: {
      rollupOptions: {
        input: { index: resolve(root, 'src/main/index.ts') },
      },
    },
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias: coreAlias },
    build: {
      rollupOptions: {
        input: { index: resolve(root, 'src/preload/index.ts') },
      },
    },
  },
  renderer: {
    root: resolve(root, 'src/renderer'),
    plugins: [vue()],
    resolve: {
      alias: {
        '@renderer': resolve(root, 'src/renderer'),
        ...coreAlias,
      },
    },
    build: {
      rollupOptions: {
        input: {
          widget: resolve(root, 'src/renderer/widget/index.html'),
          manage: resolve(root, 'src/renderer/manage/index.html'),
        },
      },
    },
  },
});
