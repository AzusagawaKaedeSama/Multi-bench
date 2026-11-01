// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 原生桥接：在页面里注入，连接页面与桌面壳。
//
// 页面无改动也能用；有这份桥接时，页面可主动调用原生能力：
//   window.__workbench.notify('标题', '正文')        → Windows 通知
//   window.__workbench.pickFiles()                  → 打开原生文件对话框
//   window.__workbench.onContext = fn              → 注册右键目标回调
//
// 壳不存在时（普通浏览器打开）全部静默降级，不影响原有功能。

(function () {
  'use strict';
  if (window.__workbench) return;

  var hasHost = function () {
    return !!(window.chrome && window.chrome.webview);
  };

  var bridge = {
    /** 弹一条 Windows 通知。 */
    notify: function (title, body) {
      if (!hasHost()) return false;
      window.chrome.webview.postMessage({
        kind: 'notify',
        title: String(title || '工作台'),
        body: String(body || ''),
      });
      return true;
    },

    /** 打开原生文件选择对话框，把文件收进临时灵感目录。 */
    pickFiles: function () {
      if (!hasHost()) return false;
      window.chrome.webview.postMessage({ kind: 'open-file-dialog' });
      return true;
    },

    /** 在资源管理器中定位路径。 */
    reveal: function (path) {
      if (!hasHost()) return false;
      window.chrome.webview.postMessage({ kind: 'reveal-path', path: String(path || '') });
      return true;
    },

    /** 注册右键目标回调；壳在弹出原生菜单前会先调用它拿到目标串。 */
    onContext: function (fn) {
      bridge._contextHandler = typeof fn === 'function' ? fn : null;
    },
    _contextHandler: null,

    /** 页面主动上报右键对象（供壳读取）。 */
    reportContext: function (target) {
      if (!hasHost()) return;
      window.chrome.webview.postMessage({ kind: 'context-target', target: String(target || '') });
    },

    /**
     * 页面刷新完成：告诉壳「这份清单已经是最新的」。
     * 壳收到后用自己的侧边栏清单接口同步一次（走服务端缓存，不会重复计算），
     * 于是页面顶部的「刷新」和侧边栏标题旁的刷新按钮改的是同一份数据。
     */
    refreshDone: function (detail) {
      if (!hasHost()) return false;
      var payload = { kind: 'workbench-refresh-done' };
      if (detail && typeof detail === 'object') {
        if (typeof detail.ms === 'number') payload.ms = Math.round(detail.ms);
        if (typeof detail.at === 'string') payload.at = detail.at;
        payload.fresh = detail.fresh === true;
      }
      window.chrome.webview.postMessage(payload);
      return true;
    },

    /** 注册「壳刷新完成，请把页面内容也更新一遍」回调。 */
    onReload: function (fn) {
      bridge._reloadHandler = typeof fn === 'function' ? fn : null;
    },
    _reloadHandler: null,
  };

  window.__workbench = bridge;

  if (!hasHost()) return;

  // 全局捕获右键：阻止网页自带菜单，把目标串交给壳，由壳弹原生菜单。
  document.addEventListener('contextmenu', function (event) {
    var element = event.target;
    var target = '';
    var node = element;
    while (node && node !== document.body) {
      if (node.dataset && node.dataset.menuTarget) {
        target = node.dataset.menuTarget;
        break;
      }
      node = node.parentElement;
    }
    if (!target) return;
    event.preventDefault();
    bridge.reportContext(target);
  }, true);

  // 接收壳发来的消息。
  window.chrome.webview.addEventListener('message', function (event) {
    var data = event.data;
    if (!data || typeof data !== 'object') return;
    if (data.kind === 'task-action') {
      document.dispatchEvent(new CustomEvent('workbench:task-action', { detail: data }));
    } else if (data.kind === 'task-open') {
      document.dispatchEvent(new CustomEvent('workbench:task-open', { detail: data }));
    } else if (data.kind === 'delivery-copy-path' || data.kind === 'delivery-reveal') {
      document.dispatchEvent(new CustomEvent('workbench:' + data.kind, { detail: data }));
    } else if (data.kind === 'review-complete' || data.kind === 'review-reopen') {
      document.dispatchEvent(new CustomEvent('workbench:' + data.kind, { detail: data }));
    } else if (data.kind === 'inbox-received') {
      document.dispatchEvent(new CustomEvent('workbench:inbox-received', { detail: data }));
    } else if (data.kind === 'workbench-reload') {
      if (typeof bridge._reloadHandler === 'function') bridge._reloadHandler(data);
      document.dispatchEvent(new CustomEvent('workbench:reload', { detail: data }));
    }
  });
})();
