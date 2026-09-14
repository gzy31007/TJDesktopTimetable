import { app, Menu, nativeImage, shell, Tray } from 'electron';
import { join } from 'node:path';
import { log, revealLogFile } from './logger.js';
import { loadSettings, saveSettings, userDataDir } from './store.js';
import { createManageWindow } from './windows/manage.js';
import { createWidgetWindow, reapplyLayer, setWidgetVisible } from './windows/widget.js';

/** 托盘：显示/隐藏挂件、打开管理、切换层级模式、点击穿透、开机自启、退出。 */

/** icon.ico 加载失败时的兜底托盘图标（16x16 PNG）。 */
const TRAY_FALLBACK_PNG =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAATklEQVR4nGNkYGBgUE1+/Z+BDHB7rigjI7maYYAJmXNrjsgbUvgoBsAkiaWxuoAcQD0D1FLeiJBCw8BoLAy/WCDLgNtzRRnJ1Xx7rigjAL6pTVNbnGckAAAAAElFTkSuQmCC';

let tray: Tray | null = null;

function resolveIconPath(): string {
  return app.isPackaged ? join(process.resourcesPath, 'icon.ico') : join(__dirname, '../../resources/icon.ico');
}

export function createTray(): Tray {
  if (tray) return tray;
  const loaded = nativeImage.createFromPath(resolveIconPath());
  const icon = loaded.isEmpty() ? nativeImage.createFromDataURL(TRAY_FALLBACK_PNG) : loaded.resize({ width: 16, height: 16 });
  log('[tray] 创建托盘', { iconPath: resolveIconPath(), iconEmpty: loaded.isEmpty() });
  tray = new Tray(icon);
  tray.setToolTip('同济桌面课表');
  tray.on('double-click', () => createManageWindow());
  refreshTrayMenu();
  return tray;
}

export function refreshTrayMenu(): void {
  if (!tray) return;
  const settings = loadSettings();

  tray.setContextMenu(
    Menu.buildFromTemplate([
      {
        label: settings.showWidget ? '隐藏桌面课表' : '显示桌面课表',
        click: () => {
          const next = saveSettings({ showWidget: !settings.showWidget });
          if (!next.showWidget) setWidgetVisible(false);
          else {
            createWidgetWindow();
            setWidgetVisible(true);
          }
          refreshTrayMenu();
        },
      },
      { label: '打开设置 / 导入课表…', click: () => createManageWindow() },
      { type: 'separator' },
      {
        label: '层级模式',
        submenu: [
          {
            label: '贴桌面层（推荐）',
            type: 'radio',
            checked: settings.mode === 'desktop',
            click: () => {
              saveSettings({ mode: 'desktop' });
              createWidgetWindow();
              reapplyLayer();
              refreshTrayMenu();
            },
          },
          {
            label: '壁纸层（WorkerW，贴桌面图标之下）',
            type: 'radio',
            checked: settings.mode === 'wallpaper',
            click: () => {
              saveSettings({ mode: 'wallpaper' });
              createWidgetWindow();
              reapplyLayer();
              refreshTrayMenu();
            },
          },
        ],
      },
      {
        label: '点击穿透（锁定，不响应鼠标）',
        type: 'checkbox',
        checked: settings.clickThrough,
        click: (item) => {
          const next = saveSettings({ clickThrough: item.checked });
          const win = createWidgetWindow();
          win.setIgnoreMouseEvents(next.clickThrough, { forward: true });
          refreshTrayMenu();
        },
      },
      {
        label: '开机自启',
        type: 'checkbox',
        checked: settings.launchAtLogin,
        click: (item) => {
          saveSettings({ launchAtLogin: item.checked });
          app.setLoginItemSettings({ openAtLogin: item.checked });
          refreshTrayMenu();
        },
      },
      { type: 'separator' },
      { label: '打开数据目录', click: () => void shell.openPath(userDataDir()) },
      { label: '查看启动日志', click: () => void shell.openPath(revealLogFile()) },
      { label: '退出', click: () => app.quit() },
    ]),
  );
}
