/* 许可控制台 — 唯一的客户端脚本。
   只做一件事：把 data-copy 上的文本复制到剪贴板。
   用事件委托挂在 document 上，所以不需要给布局加 @RenderSection，
   页面里也不必为每个按钮写一段脚本。 */

(function () {
  'use strict';

  /* 为什么需要三层降级：
     部署形态是明文 HTTP（18080），而 navigator.clipboard 只在「安全上下文」
     —— HTTPS 或 localhost —— 下才存在。管理员通过局域网 IP 或域名访问时
     它压根不存在，`navigator.clipboard.writeText` 会直接抛 TypeError。
     也就是说降级路径不是保险，而是主路径。 */
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      // 即使存在也可能失败（权限被拒、文档未聚焦），所以接了再降一层。
      return navigator.clipboard.writeText(text).catch(function () {
        return legacyCopy(text);
      });
    }
    return legacyCopy(text);
  }

  function legacyCopy(text) {
    return new Promise(function (resolve, reject) {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');

      // 必须挪出视口而不是 display:none —— 不可见元素无法被 select() 选中。
      ta.style.position = 'fixed';
      ta.style.top = '-1000px';
      ta.style.left = '0';
      ta.style.opacity = '0';
      document.body.appendChild(ta);

      ta.select();
      ta.setSelectionRange(0, ta.value.length);

      var ok = false;
      try {
        ok = document.execCommand('copy');
      } catch (err) {
        ok = false;
      }
      document.body.removeChild(ta);

      ok ? resolve() : reject(new Error('execCommand copy failed'));
    });
  }

  /* 反馈：只改可访问名称与一个 data 属性，不改布局，
     所以不会把按钮撑大、也不会让表格行跳动。 */
  function feedback(btn, ok) {
    var label = btn.querySelector('.sr-only');
    var original = label ? label.textContent : null;

    btn.dataset.copied = ok ? '1' : '0';
    if (label) label.textContent = ok ? '已复制' : '复制失败，请手动选中后按 Ctrl+C';

    window.setTimeout(function () {
      delete btn.dataset.copied;
      if (label && original !== null) label.textContent = original;
    }, 1800);
  }

  /* 最后一层兜底：连 execCommand 都不可用时，把目标文本选中，
     让用户直接按 Ctrl+C。至少不留下一个「点了没反应」的按钮。 */
  function selectFallback(btn) {
    var sel = btn.getAttribute('data-copy-target');
    var el = sel ? document.querySelector(sel) : null;
    if (!el || !window.getSelection || !document.createRange) return;
    var range = document.createRange();
    range.selectNodeContents(el);
    var s = window.getSelection();
    s.removeAllRanges();
    s.addRange(range);
  }

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-copy]');
    if (!btn) return;

    e.preventDefault();
    var text = btn.getAttribute('data-copy');

    copyText(text).then(function () {
      feedback(btn, true);
    }, function () {
      selectFallback(btn);
      feedback(btn, false);
    });
  });

  /* 有效期预设：点一下把天数填进输入框，并同步高亮。
     这是快捷键而不是字段 —— 提交上去的仍然只有 ValidDays 一个值。 */
  document.addEventListener('click', function (e) {
    var chip = e.target.closest('[data-fill-days]');
    if (!chip) return;

    var input = document.getElementById(chip.getAttribute('data-fill-target'));
    if (!input) return;

    e.preventDefault();
    input.value = chip.getAttribute('data-fill-days');
    syncChips(input, chip);
  });

  /* 手动改输入框时同步高亮，否则会出现"输入框写着 45 天、却有颗 1 个月亮着"
     这种界面自相矛盾的状态。填了非预设值就全部取消高亮。 */
  document.addEventListener('input', function (e) {
    var input = e.target;
    if (!input || !input.id) return;
    syncChips(input, null);
  });

  function syncChips(input, active) {
    var scope = input.closest('.field') || document;
    var chips = scope.querySelectorAll('[data-fill-target="' + input.id + '"]');
    if (!chips.length) return;
    Array.prototype.forEach.call(chips, function (c) {
      var on = active
        ? c === active
        : c.getAttribute('data-fill-days') === input.value;
      c.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
  }
})();
