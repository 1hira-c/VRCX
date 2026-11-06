import { VueQueryPlugin } from '@tanstack/vue-query';
import { createApp } from 'vue';

import { i18n, initComponents, initRouter, loadLocalizedStrings } from '../plugins';
import { initDayjs } from '../plugins/dayjs';
import { languageCodes, resolveSystemLanguage } from '../localization';
import { queryClient } from '../queries';
import { pinia } from '../stores';
import Root from './Root.vue';

import '../styles/globals.css';
import './web-app.css';

initDayjs();

const requestedLocale = resolveSystemLanguage(navigator.language, languageCodes) || 'en';
await loadLocalizedStrings(requestedLocale);
i18n.global.locale.value = requestedLocale;
document.documentElement.lang = requestedLocale;
document.documentElement.classList.add('dark');

const app = createApp(Root);
app.use(pinia).use(i18n).use(VueQueryPlugin, { queryClient });
initComponents(app);
initRouter(app);
app.mount('#root');
