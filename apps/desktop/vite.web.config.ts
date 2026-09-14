/** 纯浏览器预览渲染层（在没有 Electron 的环境下调视觉）。 */
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vite';

const root = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  root: resolve(root, 'src/renderer'),
  plugins: [vue()],
  resolve: {
    alias: {
      '@renderer': resolve(root, 'src/renderer'),
      '@tjt/core': resolve(root, '../../packages/core/src/index.ts'),
    },
  },
  server: {
    port: 5199,
    strictPort: false,
    host: '127.0.0.1',
  },
});
