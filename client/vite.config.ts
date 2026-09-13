import { defineConfig } from 'vite';

export default defineConfig({
  // Относительные пути: одна сборка работает и на GitHub Pages (/sro/), и с корня игрового сервера.
  base: './',
  server: {
    host: true, // доступ с iPhone по Wi-Fi
    proxy: {
      // В dev-режиме клиент подключается к тому же хосту, Vite проксирует сокет на игровой сервер.
      '/ws': { target: 'ws://127.0.0.1:5000', ws: true },
    },
    // Клиент импортирует общий с сервером ../shared (параметры корпусов); пути — от client/.
    fs: { allow: ['.', '../shared'] },
  },
  build: { target: 'es2022' },
});
