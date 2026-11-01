// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 原生桥接：在页面里注入，连接页面与桌面壳。
//
// 页面无改动也能用；有这份桥接时，页面可主动调用原生能力：
//   window.__workbench.notify('标题', '正文')        → Windows 通知
//   window.__workbench.pickFiles()                  → 打开原生文件对话框
//   window.__workbench.reveal('D:\path\to\file')    → 在资源管理器中定位
//   window.__workbench.onContext = fn              → 注册右键目标回调
//
// 壳不存在时（普通浏览器打开）全部静默降级，不影响原有功能。
//
// ★ 右键上下文（2026-10-31 新增）
//   壳在弹原生菜单之前会调用 window.__wbDescribe()，拿回一份「光标处是什么」的描述：
//     imgSrc / imgAlt / href / linkText / selection / editable / target(data-menu-target)
//   描述由 DOM 现场反查得到，比宿主凭 Kind 猜要准 ——
//   能区分「点在图片上」和「点在图片旁边的空白上」，也能拿到 alt 文字与 data-menu-target。

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
  };

  window.__workbench = bridge;

  // ──────────────── 右键上下文描述 ────────────────

  /** 最近一次鼠标位置（页面坐标）。 */
  var lastPoint = { x: 0, y: 0 };

  /** 沿祖先链找带指定属性的元素（用于 data-menu-target）。 */
  function nearestAttr(node, attr) {
    while (node && node !== document.documentElement) {
      if (node.getAttribute) {
        var v = node.getAttribute(attr);
        if (v) return v;
      }
      node = node.parentElement;
    }
    return '';
  }

  /** 把一个元素描述成壳能读的 JSON 对象。 */
  function describe(el, x, y) {
    var info = {
      target: '',
      tag: '',
      x: x || 0,
      y: y || 0,
      imgSrc: '',
      imgAlt: '',
      imgW: 0,
      imgH: 0,
      href: '',
      linkText: '',
      selection: '',
      editable: false,
      pageUrl: location.href,
      pageTitle: document.title || '',
    };

    try {
      var sel = window.getSelection ? window.getSelection() : null;
      if (sel && sel.rangeCount && String(sel).length) info.selection = String(sel);
    } catch (e) { /* 某些页面会抛，忽略 */ }

    if (el && el.nodeType === 1) {
      info.tag = el.tagName ? el.tagName.toUpperCase() : '';
      info.target = nearestAttr(el, 'data-menu-target');
      info.editable = (info.tag === 'INPUT' || info.tag === 'TEXTAREA' || !!el.isContentEditable);

      var img = el.closest ? el.closest('img') : null;
      if (img) {
        info.imgSrc = img.currentSrc || img.src || img.getAttribute('src') || '';
        info.imgAlt = img.getAttribute('alt') || '';
        info.imgW = img.naturalWidth || img.width || 0;
        info.imgH = img.naturalHeight || img.height || 0;
      }

      var a = el.closest ? el.closest('a[href]') : null;
      if (a) {
        info.href = a.href || a.getAttribute('href') || '';
        info.linkText = (a.innerText || a.textContent || '').replace(/\s+/g, ' ').trim();
      }
    }

    return info;
  }

  /** 按坐标反查元素并描述。 */
  window.__wbDescribeAt = function (x, y) {
    var el = null;
    try { el = document.elementFromPoint(x, y); } catch (e) { el = null; }
    if (!el) el = document.body;
    return describe(el, x, y);
  };

  /** 壳调用这个拿当前右键上下文。 */
  window.__wbDescribe = function () {
    if (window.__wbCtx) return window.__wbCtx;
    return window.__wbDescribeAt(lastPoint.x, lastPoint.y);
  };

  if (!hasHost()) return;

  document.addEventListener('mousemove', function (event) {
    lastPoint.x = event.clientX;
    lastPoint.y = event.clientY;
  }, true);

  // 全局捕获右键：记录上下文，并把 data-menu-target 交给壳。
  // 壳会自己弹原生菜单，所以这里只拦「页面声明了业务目标」的情况。
  document.addEventListener('contextmenu', function (event) {
    window.__wbCtx = describe(event.target, event.clientX, event.clientY);
    var target = window.__wbCtx.target;
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
    }
  });
})();
