import { createApp } from 'vue';
import { applyPreviewWallpaper } from '../shared/api';
import ManageApp from './ManageApp.vue';
import './manage.css';

applyPreviewWallpaper();
createApp(ManageApp).mount('#app');
