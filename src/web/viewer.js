/* MdView renderer. Talks to the Windows host through window.chrome.webview;
   also usable standalone via window.MdView.load(name, text, baseUrl). */
(function () {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const $ = (s) => document.querySelector(s);
  const content = $('#content');
  const tocEl = $('#toc');
  const themeBtn = $('#btn-theme');
  const mq = matchMedia('(prefers-color-scheme: dark)');

  let current = { name: '', text: '', baseUrl: '' };
  let headings = [];
  let mermaidLoading = null;
  let renderGen = 0;

  const send = (msg) => { if (host) host.postMessage(JSON.stringify(msg)); };
  const store = {
    get(k, d) { try { return localStorage.getItem(k) || d; } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} }
  };

  /* ---------------- theme ---------------- */
  let themeMode = store.get('mdview-theme', 'auto');
  const isDark = () => themeMode === 'dark' || (themeMode === 'auto' && mq.matches);

  function applyTheme(rerender) {
    const dark = isDark();
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    $('#hljs-light').disabled = dark;
    $('#hljs-dark').disabled = !dark;
    themeBtn.dataset.mode = themeMode;
    themeBtn.title = 'Theme: ' + themeMode + ' (click to change)';
    send({ type: 'theme', dark });
    if (rerender) renderMermaid(content, renderGen);
  }
  themeBtn.addEventListener('click', () => {
    themeMode = { auto: 'light', light: 'dark', dark: 'auto' }[themeMode] || 'auto';
    store.set('mdview-theme', themeMode);
    applyTheme(true);
  });
  mq.addEventListener('change', () => { if (themeMode === 'auto') applyTheme(true); });

  /* ---------------- markdown ---------------- */
  marked.setOptions({ gfm: true, breaks: false });

  function slugger() {
    const seen = new Map();
    return (text) => {
      let s = text.toLowerCase().trim()
        .replace(/[^\p{L}\p{N}\s_-]/gu, '')
        .replace(/\s/g, '-');
      if (!s) s = 'section';
      const n = seen.get(s) || 0;
      seen.set(s, n + 1);
      return n ? s + '-' + n : s;
    };
  }

  function escapeHtml(s) {
    return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  }

  function splitFrontMatter(text) {
    const m = /^﻿?---\r?\n([\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/.exec(text);
    if (!m) return { fm: null, body: text };
    return { fm: m[1], body: text.slice(m[0].length) };
  }

  function render(doc, isReload) {
    const gen = ++renderGen;
    const prevScroll = content.scrollTop;
    current = doc;
    document.title = doc.name ? doc.name + ' — MdView' : 'MdView';
    $('#doc-name').textContent = doc.path || doc.name || '';

    const { fm, body } = splitFrontMatter(doc.text || '');
    let html = DOMPurify.sanitize(marked.parse(body), { ADD_ATTR: ['open'] });
    if (fm !== null) {
      html = '<details class="frontmatter"><summary>Front matter</summary><pre><code class="language-yaml">' +
        escapeHtml(fm) + '</code></pre></details>' + html;
    }
    content.innerHTML = html;

    enhance(content);
    buildToc();
    renderMermaid(content, gen);

    if (isReload) {
      content.style.scrollBehavior = 'auto';
      content.scrollTop = prevScroll;
      content.style.scrollBehavior = '';
      const f = $('#reload-flash');
      f.classList.add('show');
      setTimeout(() => f.classList.remove('show'), 900);
    } else {
      content.scrollTop = 0;
      if (doc.fragment) scrollToId(doc.fragment, false);
    }
  }

  function enhance(root) {
    // Headings: ids + anchor links
    const slug = slugger();
    headings = [];
    root.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach((h) => {
      if (!h.id) h.id = slug(h.textContent);
      const a = document.createElement('a');
      a.className = 'anchor'; a.href = '#' + h.id; a.textContent = '#';
      a.setAttribute('aria-hidden', 'true');
      h.appendChild(a);
      headings.push(h);
    });

    // Task list items
    root.querySelectorAll('li > input[type=checkbox]').forEach((cb) => cb.parentElement.classList.add('task'));

    // GitHub alerts: > [!NOTE]
    root.querySelectorAll('blockquote').forEach((bq) => {
      const p = bq.firstElementChild;
      if (!p || p.tagName !== 'P') return;
      const m = /^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*/i.exec(p.innerHTML);
      if (!m) return;
      const kind = m[1].toLowerCase();
      p.innerHTML = p.innerHTML.slice(m[0].length).replace(/^<br>\s*/, '');
      if (!p.textContent.trim() && !p.querySelector('img')) p.remove();
      const div = document.createElement('div');
      div.className = 'alert alert-' + kind;
      div.innerHTML = '<p class="alert-title">' + kind[0].toUpperCase() + kind.slice(1) + '</p>';
      while (bq.firstChild) div.appendChild(bq.firstChild);
      bq.replaceWith(div);
    });

    // Code blocks: mermaid → placeholder, others → highlight + copy button
    root.querySelectorAll('pre > code').forEach((code) => {
      const pre = code.parentElement;
      const lm = /(?:^|\s)language-([\w#+.-]+)/.exec(code.className);
      const lang = lm ? lm[1].toLowerCase() : '';
      if (lang === 'mermaid') {
        const div = document.createElement('div');
        div.className = 'mermaid';
        div.dataset.src = code.textContent;
        pre.replaceWith(div);
        return;
      }
      if (lang && hljs.getLanguage(lang)) {
        try { hljs.highlightElement(code); } catch (e) {}
      }
      if (lang) {
        const l = document.createElement('span');
        l.className = 'lang'; l.textContent = lang;
        pre.appendChild(l);
      }
      const btn = document.createElement('button');
      btn.className = 'copy'; btn.textContent = 'Copy';
      btn.addEventListener('click', () => {
        copyText(code.textContent).then(() => {
          btn.textContent = 'Copied';
          setTimeout(() => (btn.textContent = 'Copy'), 1200);
        });
      });
      pre.appendChild(btn);
    });

    // Relative images → served from the document's folder
    if (current.baseUrl) {
      root.querySelectorAll('img[src]').forEach((img) => {
        const src = img.getAttribute('src');
        if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(src)) return;
        try { img.src = new URL(src.replace(/\\/g, '/'), current.baseUrl).href; } catch (e) {}
      });
    }
  }

  function copyText(t) {
    if (navigator.clipboard) return navigator.clipboard.writeText(t).catch(() => fallbackCopy(t));
    return Promise.resolve(fallbackCopy(t));
  }
  function fallbackCopy(t) {
    const ta = document.createElement('textarea');
    ta.value = t; document.body.appendChild(ta); ta.select();
    document.execCommand('copy'); ta.remove();
  }

  /* ---------------- mermaid (loaded only when needed) ---------------- */
  function loadMermaid() {
    if (window.mermaid) return Promise.resolve(window.mermaid);
    if (!mermaidLoading) {
      mermaidLoading = new Promise((res, rej) => {
        const s = document.createElement('script');
        s.src = 'lib/mermaid.min.js';
        s.onload = () => res(window.mermaid);
        s.onerror = rej;
        document.head.appendChild(s);
      });
    }
    return mermaidLoading;
  }

  async function renderMermaid(root, gen) {
    const blocks = [...root.querySelectorAll('.mermaid')];
    if (!blocks.length) return;
    let mermaid;
    try { mermaid = await loadMermaid(); } catch (e) { return; }
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: isDark() ? 'dark' : 'default',
      fontFamily: getComputedStyle(document.body).fontFamily
    });
    for (let i = 0; i < blocks.length; i++) {
      if (gen !== renderGen) return;
      const div = blocks[i];
      try {
        const { svg } = await mermaid.render('mmd-' + gen + '-' + i + '-' + Date.now(), div.dataset.src);
        if (gen !== renderGen) return;
        div.innerHTML = svg;
      } catch (e) {
        div.innerHTML = '<div class="mermaid-error">Mermaid error: ' +
          escapeHtml(String((e && e.message) || e)) + '\n\n' + escapeHtml(div.dataset.src) + '</div>';
        document.querySelectorAll('body > [id^="dmmd-"]').forEach((n) => n.remove());
      }
    }
  }

  /* ---------------- table of contents ---------------- */
  function buildToc() {
    tocEl.innerHTML = '';
    const items = headings.filter((h) => +h.tagName[1] <= 4);
    if (!items.length) {
      tocEl.innerHTML = '<li class="none">No headings</li>';
      return;
    }
    // Shift levels so the top-most level used is rendered as level 1
    const min = Math.min(...items.map((h) => +h.tagName[1]));
    for (const h of items) {
      const li = document.createElement('li');
      li.className = 'l' + (+h.tagName[1] - min + 1);
      const a = document.createElement('a');
      a.href = '#' + h.id;
      a.textContent = [...h.childNodes]
        .filter((n) => !(n.classList && n.classList.contains('anchor')))
        .map((n) => n.textContent).join('').trim();
      a.title = a.textContent;
      a.dataset.target = h.id;
      li.appendChild(a);
      tocEl.appendChild(li);
    }
    applyFilter();
    spy();
  }

  function applyFilter() {
    const q = $('#toc-filter').value.trim().toLowerCase();
    tocEl.querySelectorAll('li').forEach((li) => {
      li.style.display = !q || li.textContent.toLowerCase().includes(q) ? '' : 'none';
    });
  }
  $('#toc-filter').addEventListener('input', applyFilter);

  let spyQueued = false;
  let pinned = null, pinnedUntil = 0;
  function spy() {
    spyQueued = false;
    if (pinned && Date.now() < pinnedUntil) { markActive(pinned); return; }
    const top = content.scrollTop + 80;
    let active = null;
    for (const h of headings) {
      if (h.offsetTop <= top) active = h; else break;
    }
    if (content.scrollTop + content.clientHeight >= content.scrollHeight - 4 && headings.length) {
      // At the bottom: highlight the last heading that is visible
      active = headings.filter((h) => h.offsetTop <= content.scrollTop + content.clientHeight).pop() || active;
    }
    markActive(active || headings[0]);
  }
  function markActive(active) {
    tocEl.querySelectorAll('a.active').forEach((a) => a.classList.remove('active'));
    if (!active) return;
    const link = tocEl.querySelector('a[data-target="' + CSS.escape(active.id) + '"]');
    if (link) {
      link.classList.add('active');
      const r = link.getBoundingClientRect(), tr = tocEl.getBoundingClientRect();
      if (r.top < tr.top || r.bottom > tr.bottom) link.scrollIntoView({ block: 'nearest' });
    }
  }
  content.addEventListener('scroll', () => {
    if (!spyQueued) { spyQueued = true; requestAnimationFrame(spy); }
  });

  function toggleToc() {
    const hidden = document.documentElement.classList.toggle('toc-hidden');
    store.set('mdview-toc', hidden ? 'hidden' : 'shown');
  }
  $('#btn-toc').addEventListener('click', toggleToc);

  function scrollToId(id, smooth) {
    let target = null;
    try { target = document.getElementById(decodeURIComponent(id)); } catch (e) { target = document.getElementById(id); }
    if (!target) target = document.getElementById(id.toLowerCase());
    if (!target) return;
    pinned = target; pinnedUntil = Date.now() + 900; markActive(target);
    if (!smooth) content.style.scrollBehavior = 'auto';
    content.scrollTop = target.offsetTop - 8;
    content.style.scrollBehavior = '';
  }

  /* ---------------- links ---------------- */
  document.addEventListener('click', (e) => {
    const a = e.target.closest('a[href]');
    if (!a) return;
    const href = a.getAttribute('href');
    e.preventDefault();
    if (href.startsWith('#')) { scrollToId(href.slice(1), true); return; }
    if (/^(https?|mailto|ftp):/i.test(href)) { send({ type: 'external', url: href }); return; }
    if (/^[a-z][a-z0-9+.-]*:/i.test(href) && !/^file:/i.test(href)) return; // ignore javascript:, etc.
    send({ type: 'open', href });
  });

  /* ---------------- toolbar & keys ---------------- */
  $('#btn-open').addEventListener('click', () => send({ type: 'openDialog' }));
  $('#btn-reload').addEventListener('click', () => send({ type: 'reload' }));
  $('#btn-print').addEventListener('click', () => window.print());

  document.addEventListener('keydown', (e) => {
    const k = e.key.toLowerCase();
    if (e.ctrlKey && k === 'o') { e.preventDefault(); send({ type: 'openDialog' }); }
    else if (k === 'f5' || (e.ctrlKey && k === 'r')) { e.preventDefault(); send({ type: 'reload' }); }
    else if (e.altKey && k === 'arrowleft') { e.preventDefault(); send({ type: 'back' }); }
    else if (e.ctrlKey && k === 'w') { e.preventDefault(); send({ type: 'close' }); }
    else if (e.ctrlKey && k === 'b') { e.preventDefault(); toggleToc(); }
    else if (e.ctrlKey && k === 'p') { e.preventDefault(); window.print(); }
    else if (e.ctrlKey && e.shiftKey && k === 'l') { e.preventDefault(); themeBtn.click(); }
    else if (k === 'escape' && document.activeElement === $('#toc-filter')) {
      $('#toc-filter').value = ''; applyFilter(); content.focus();
    }
  });

  document.addEventListener('mouseup', (e) => {
    if (e.button === 3) { e.preventDefault(); send({ type: 'back' }); }
  });

  /* ---------------- host bridge ---------------- */
  function onHostMessage(msg) {
    if (msg.type === 'doc') render(msg, !!msg.reload);
    else if (msg.type === 'error') {
      content.innerHTML = '<div class="empty"><h1>Can\'t open file</h1><p class="error"></p></div>';
      content.querySelector('.error').textContent = msg.message;
    }
  }

  window.MdView = {
    load(name, text, baseUrl, reload) { render({ name, text, baseUrl: baseUrl || '' }, !!reload); }
  };

  applyTheme(false);
  if (host) {
    host.addEventListener('message', (e) => {
      let msg = e.data;
      if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch (x) { return; } }
      onHostMessage(msg);
    });
    send({ type: 'ready' });
  }
})();
