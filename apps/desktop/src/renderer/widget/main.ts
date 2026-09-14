import { createApp } from 'vue';
import { applyPreviewWallpaper } from '../shared/api';
import WidgetApp from './WidgetApp.vue';
import './widget.css';

applyPreviewWallpaper();
createApp(WidgetApp).mount('#app');
