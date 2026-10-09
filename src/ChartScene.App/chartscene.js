// Host diagnostics only. The charts, input pipeline and application UI are implemented by Uno/C#.
(() => {
  'use strict';
  window.chartScene = window.chartScene || { ready: false };
  window.addEventListener('error', event => {
    (window.chartScene.hostErrors ||= []).push(String(event.error || event.message));
  });
  window.addEventListener('unhandledrejection', event => {
    (window.chartScene.hostErrors ||= []).push(String(event.reason));
  });
})();
