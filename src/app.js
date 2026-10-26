/* Calculate325 —— 网页界面逻辑（依赖同页面内联的 Calculate325 求解器） */
(function () {
  'use strict';

  var S = window.Calculate325;
  var $ = function (sel) { return document.querySelector(sel); };
  var EXAMPLES = ['325', '1000', '2025', '62', '777', '3.25', '-7', '123456', '105625', '30'];
  var history = [];
  var sessionCache = new Map();

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = text;
    return n;
  }

  var PATTERNS = ['325', '799', '325799', '114514'];

  function selectedPatterns() {
    var sel = document.getElementById('patternSel');
    var v = sel ? sel.value : '325';
    if (v === 'all') return PATTERNS.slice();
    return PATTERNS.indexOf(v) >= 0 ? [v] : ['325'];
  }

  function currentOptions(pattern) {
    return {
      pattern: pattern || '325',
      maxBlocks: parseInt($('#maxBlocks').value, 10) || 2,
      orderMode: $('#orderMode').value === 'any' ? 'any' : 'fixed',
      allowFactorial: $('#allowFactorial').checked,
      allowDecimal: $('#allowDecimal').checked,
      factMax: Math.max(0, Math.min(20, parseInt($('#factMax').value, 10) || 0)),
      timeBudgetMs: 5000,
      solutionListLimit: 8
    };
  }

  function sessionFor(opts) {
    var key = JSON.stringify(opts);
    if (!sessionCache.has(key)) sessionCache.set(key, S.createSession(opts));
    return sessionCache.get(key);
  }

  function copyText(text, btn) {
    var done = function () {
      if (!btn) return;
      var old = btn.textContent;
      btn.textContent = '已复制';
      setTimeout(function () { btn.textContent = old; }, 1200);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, function () { fallbackCopy(text, done); });
    } else {
      fallbackCopy(text, done);
    }
  }

  function fallbackCopy(text, done) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.top = '-1000px';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); done(); } catch (e) { /* 忽略：用户可手动选择 */ }
    document.body.removeChild(ta);
  }

  function renderExamples() {
    var box = $('#examples');
    box.textContent = '';
    box.appendChild(el('span', 'muted', '试试：'));
    EXAMPLES.forEach(function (t) {
      var b = el('button', 'chip', t);
      b.type = 'button';
      b.addEventListener('click', function () { $('#target').value = t; solve(); });
      box.appendChild(b);
    });
    if (history.length) {
      box.appendChild(el('span', 'muted', ' 最近：'));
      history.slice(-6).forEach(function (t) {
        var b = el('button', 'chip', t);
        b.type = 'button';
        b.addEventListener('click', function () { $('#target').value = t; solve(); });
        box.appendChild(b);
      });
    }
  }

  function setBusy(busy) {
    $('#go').disabled = busy;
    $('#go').textContent = busy ? '计算中…' : '求解';
  }

  function revealResult() {
    var box = $('#result');
    if (box && typeof box.scrollIntoView === 'function') {
      try { box.scrollIntoView({ block: 'start' }); }
      catch (e) { box.scrollIntoView(); }
    }
  }

  function solve() {
    var raw = $('#target').value;
    var parsed = S.parseTarget(raw);
    var box = $('#result');
    if (!parsed.ok) {
      box.textContent = '';
      var c = el('div', 'card');
      c.appendChild(el('span', 'badge warn', '输入无效'));
      c.appendChild(el('p', null, parsed.error));
      box.appendChild(c);
      return;
    }
    var pats = selectedPatterns();
    if (!pats.length) {
      box.textContent = '';
      var noPat = el('div', 'card');
      noPat.appendChild(el('span', 'badge warn', '请至少选择一个数字串'));
      noPat.appendChild(el('p', null, '在“数字串”那一行勾选 325 / 799 / 325799 / 114514 中至少一个。'));
      box.appendChild(noPat);
      return;
    }
    setBusy(true);
    box.textContent = '';
    var loading = el('div', 'card');
    loading.appendChild(el('span', 'badge gray', '计算中…'));
    loading.appendChild(el('p', 'muted', '正在搜索 ' + pats.join(' / ') + ' 的切分与运算组合。'));
    box.appendChild(loading);

    setTimeout(function () {
      var all = [];
      var overflow = [];
      try {
        pats.forEach(function (p) {
          var opts = currentOptions(p);
          all.push({ pattern: p, r: sessionFor(opts).solve(parsed.value), opts: opts });
        });
      } catch (e) {
        all.push({ pattern: pats[0], r: { ok: false, error: '求解异常：' + (e && e.message ? e.message : e), notes: [], alternatives: [] }, opts: currentOptions(pats[0]) });
      }
      setBusy(false);
      renderAll(all);
      revealResult();
      var key = parsed.text;
      history = history.filter(function (x) { return x !== key; });
      history.push(key);
      renderExamples();
    }, 30);
  }

  // 把所有选中数字串的结果都输出出来（每段一张卡）
  function renderAll(all) {
    var box = $('#result');
    box.textContent = '';
    all.forEach(function (item) {
      box.appendChild(cardFor(item.r, item.opts, item.pattern));
    });
  }

  // 单个数字串的结果 → 一张卡（ok / 保底 / 无解 都在这里处理，输出区只有这一处）
  function cardFor(r, opts, pattern) {
    var card;
    if (r.ok) {
      card = formulaCard(r, opts);
    } else if (r.fallback) {
      var synthetic = {
        ok: true, digitsOk: true, valueOk: true,
        blocks: r.fallback.blocks, tokenCount: S.countTokens(r.fallback.expression),
        elapsedMs: r.elapsedMs, expression: r.fallback.expression,
        expressionPretty: r.fallback.pretty, targetText: r.targetText,
        digits: S.digitsOf(r.fallback.expression), alternatives: [], notes: [],
        stats: r.stats || [], entries: r.entries, tokens: 0
      };
      card = formulaCard(synthetic, opts);
      card.className = 'card fallback-box';
    } else {
      card = el('div', 'card');
      card.appendChild(el('p', null, '无解'));
    }
    var head = el('div', 'row');
    head.style.margin = '0 0 8px';
    head.appendChild(el('span', 'badge gray', '数字串 ' + (pattern || opts.pattern || '325')));
    card.insertBefore(head, card.firstChild);
    return card;
  }

  function formulaCard(r, opts) {
    var card = el('div', 'card');

    var bar = el('div', 'row');
    bar.style.gap = '6px';
    if (r.ok && r.digitsOk && r.valueOk) {
      bar.appendChild(el('span', 'badge', '✓ 数字序列校验通过'));
      bar.appendChild(el('span', 'badge', '✓ 精确求值校验通过'));
    }
    bar.appendChild(el('span', 'badge gray', r.blocks + ' 块（' + (opts.pattern || '325') + '）'));
    bar.appendChild(el('span', 'badge gray', r.tokenCount + ' 个数'));
    bar.appendChild(el('span', 'badge gray', '用时 ' + r.elapsedMs + ' ms'));
    bar.appendChild(el('span', 'badge gray', opts.orderMode === 'any' ? '数字可换序' : '数字顺序固定'));
    card.appendChild(bar);

    var f = el('div', 'formula');
    f.appendChild(document.createTextNode(r.expressionPretty));
    f.appendChild(el('span', 'eq', '='));
    f.appendChild(document.createTextNode(r.targetText));
    card.appendChild(f);

    var digits = el('div', 'digits', '数字串：' + r.digits);
    card.appendChild(digits);

    var btns = el('div', 'row');
    btns.style.marginTop = '10px';
    var b1 = el('button', null, '复制算式（ASCII）');
    b1.type = 'button';
    b1.addEventListener('click', function () { copyText(r.expression, b1); });
    var b2 = el('button', null, '复制算式（× ÷ −）');
    b2.type = 'button';
    b2.addEventListener('click', function () { copyText(r.expressionPretty, b2); });
    btns.appendChild(b1);
    btns.appendChild(b2);
    card.appendChild(btns);

    if (r.alternatives && r.alternatives.length > 1) {
      var det = el('details');
      var sum = el('summary', null, '其它 ' + (r.alternatives.length - 1) + ' 种写法');
      det.appendChild(sum);
      var ul = el('ul', 'alts');
      r.alternatives.forEach(function (alt, idx) {
        if (idx === 0) return;
        var li = el('li');
        li.appendChild(el('span', null, alt.pretty));
        var cb = el('button', null, '复制');
        cb.type = 'button';
        cb.addEventListener('click', function () { copyText(alt.expression, cb); });
        li.appendChild(cb);
        ul.appendChild(li);
      });
      det.appendChild(ul);
      card.appendChild(det);
    }

    if (r.notes && r.notes.length) {
      var ul2 = el('ul', 'notes');
      r.notes.forEach(function (n) { ul2.appendChild(el('li', null, n)); });
      card.appendChild(ul2);
    }

    var stats = el('div', 'stats');
    (r.stats || []).forEach(function (s) {
      var box = el('div', 'stat');
      box.appendChild(el('b', null, String(s.values)));
      box.appendChild(el('span', null, s.k + ' 块：值个数 / 已建 ' + s.tokensBuilt + ' 轮 / ' + s.ms + ' ms'));
      stats.appendChild(box);
    });
    var box2 = el('div', 'stat');
    box2.appendChild(el('b', null, String(r.entries || 0)));
    box2.appendChild(el('span', null, '缓存中的中间结果总数'));
    stats.appendChild(box2);
    card.appendChild(stats);

    return card;
  }

  function render(r, opts) {
    var box = $('#result');
    box.textContent = '';

    if (r.ok) {
      box.appendChild(formulaCard(r, opts));
      return;
    }

    // 单一输出区：搜索不到更短算式时，直接把保底算式作为答案输出，不再单独提示“未找到”
    if (r.fallback) {
      var synthetic = {
        ok: true, digitsOk: true, valueOk: true,
        blocks: r.fallback.blocks, tokenCount: S.countTokens(r.fallback.expression),
        elapsedMs: r.elapsedMs, expression: r.fallback.expression,
        expressionPretty: r.fallback.pretty, targetText: r.targetText,
        digits: S.digitsOf(r.fallback.expression), alternatives: [], notes: [],
        stats: r.stats || [], entries: r.entries, tokens: 0
      };
      var fb = formulaCard(synthetic, opts);
      fb.className = 'card fallback-box';
      var head = el('p', null, '在最多 ' + opts.maxBlocks + ' 个 325 块内没有更短算式，以下直接给出保底算式（用 n = 325×q + r 递归拼出，一定成立）：');
      fb.insertBefore(head, fb.firstChild);
      if (r.notes && r.notes.length) {
        var ul = el('ul', 'notes');
        r.notes.forEach(function (n) { ul.appendChild(el('li', null, n)); });
        fb.appendChild(ul);
      }
      box.appendChild(fb);
      return;
    }

    var card = el('div', 'card');
    card.appendChild(el('span', 'badge warn', '未能给出算式'));
    card.appendChild(el('p', null, (r.error ? r.error + ' ' : '') +
      '在“最多 ' + opts.maxBlocks + ' 个 325 块”的范围内没有找到等于 ' + (r.targetText || $('#target').value) + ' 的算式，保底构造也不适用（保底只保证整数与有限小数）。'));
    var tips = el('ul', 'notes');
    tips.appendChild(el('li', null, '把“最多用几个 325 块”调到 ' + Math.min(4, opts.maxBlocks + 1) + ' 再试（搜索空间更大，也更慢）。'));
    if (!opts.allowFactorial) tips.appendChild(el('li', null, '勾选“允许后缀阶乘”会显著扩大可表示的数值。'));
    if (!opts.allowDecimal) tips.appendChild(el('li', null, '勾选“允许小数点”会扩大可表示的数值。'));
    (r.notes || []).forEach(function (n) { tips.appendChild(el('li', null, n)); });
    card.appendChild(tips);
    box.appendChild(card);
  }

  function init() {
    if (!S) {
      document.getElementById('result').textContent = '求解器未加载。';
      return;
    }
    renderExamples();
    $('#go').addEventListener('click', solve);
    $('#target').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); solve(); }
    });
    ['#maxBlocks', '#orderMode', '#allowFactorial', '#allowDecimal', '#factMax', '#patternSel'].forEach(function (sel) {
      var node = document.querySelector(sel);
      if (node) node.addEventListener('change', function () {
        // 选项变化后重新求解，方便对比
        if ($('#result').children.length) solve();
      });
    });
    solve();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
