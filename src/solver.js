/*
 * Calculate325 —— 求解器核心
 * ---------------------------------------------------------------------------
 * 规则（用户确定，默认 orderMode = 'fixed'）：
 *   1. 数字串固定为 "325" 的重复，顺序不可改变（325325325…）；
 *   2. 只能在数字之间插入运算符与括号；相邻数字之间不插运算符就拼成多位数；
 *   3. 允许 + − × ÷ 与括号、后缀阶乘 !、小数点（如 3.25 / 32.5）；
 *   4. 自动选择最少的 "325" 块数 k。
 *   5. orderMode = 'any' 时放宽为“数字位置可任意交换”，只要求 3/2/5 出现次数相同。
 *
 * 算法（区间 DP + 按“用数个数”分轮）：
 *   dp(sub, t) = 用 sub 的数字、恰好 t 个“数”（token）能算出的全部精确有理数。
 *     · t = 1：整串作为一个数（整数，或在任意位置插入小数点）；
 *     · t ≥ 2：切成 a|b，取 dp(a, i) 与 dp(b, t−i) 做二元运算；
 *              fixed 模式只允许 a+b、a−b、a×b、a÷b（保持数字原顺序）；
 *     · 后缀阶乘不改变 token 个数，所以结果留在同一轮 t。
 *   轮 t 由小到大构建、命中即停 ⇒ 先找到的解“用数最少”，通常也最短；
 *   某些子串的值个数按轮限量（classCaps），轮数小的“简单值”优先保留，
 *   不像过去那样“先到先得”把 325*3 这种简单值挤掉。
 *   子串结果可跨目标、跨块数复用；每轮只构建到需要的深度。
 *
 * 数值全部使用 BigInt 精确有理数，无浮点误差（浮点只用于快速剪枝判断）。
 */
(function (root, factory) {
  if (typeof module === 'object' && module && module.exports) module.exports = factory();
  else root.Calculate325 = factory();
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // ===================== 精确有理数 =====================
  var PREC_ATOM = 3, PREC_MUL = 2, PREC_ADD = 1;

  function absBig(x) { return x < 0n ? -x : x; }
  function gcdBig(a, b) {
    a = absBig(a); b = absBig(b);
    while (b) { var t = a % b; a = b; b = t; }
    return a;
  }
  function rat(n, d) {
    if (d === undefined) d = 1n;
    if (d === 0n) throw new Error('division by zero');
    if (d < 0n) { n = -n; d = -d; }
    var g = gcdBig(n, d);
    if (g === 0n) g = 1n;
    return { n: n / g, d: d / g };
  }
  function Radd(a, b) { return rat(a.n * b.d + b.n * a.d, a.d * b.d); }
  function Rsub(a, b) { return rat(a.n * b.d - b.n * a.d, a.d * b.d); }
  function Rmul(a, b) { return rat(a.n * b.n, a.d * b.d); }
  function Rdiv(a, b) { return b.n === 0n ? null : rat(a.n * b.d, a.d * b.n); }
  function rkey(r) { return r.n + '/' + r.d; }
  function isIntRat(r) { return r.d === 1n; }
  function ratToText(r) { return r.d === 1n ? r.n.toString() : r.n.toString() + '/' + r.d.toString(); }
  function eqRat(a, b) { return a.n === b.n && a.d === b.d; }
  function numOf(x) { return Number(x); }

  var factCache = new Map();
  function factorialBig(v) {
    var k = v.toString();
    var hit = factCache.get(k);
    if (hit !== undefined) return hit;
    var r = 1n;
    for (var i = 2n; i <= v; i++) r *= i;
    factCache.set(k, r);
    return r;
  }

  // ===================== 默认参数 =====================
  var DEFAULTS = {
    pattern: '325',               // 数字串（重复单位）：325 / 799 / 325799 / 114514 …
    maxBlocks: 2,                 // 最多使用几个数字串块
    allowFactorial: true,         // 允许后缀阶乘
    factMax: 12,                  // 阶乘参数上限（12! = 479001600）
    allowDecimal: true,           // 允许小数点
    maxAbsValue: 1e12,            // 中间结果幅度上限
    maxDenominator: 1e9,          // 中间结果分母上限
    // 每个子串按“用数个数”分轮的容量上限；轮数越小越简单、越该保留
    classCaps: [0, 200000, 30000, 30000, 12000, 6000, 3000],
    classCapTail: 1500,           // 轮数超过数组长度时的上限
    timeBudgetMs: 8000,           // 单次求解时间预算
    fallbackBudgetMs: 4000,       // 保底构造阶段的时间预算
    solutionListLimit: 8,         // 最多返回几种写法
    searchMode: 'blocks',         // 'blocks' = 最少块数（默认，命中即停）；'length' = 最短算式（更慢）
    orderMode: 'fixed'            // 'fixed' = 数字必须按 325325… 原顺序；'any' = 允许交换数字位置
  };

  function classCapOf(limits, t) {
    var caps = limits.classCaps;
    if (t < caps.length) return caps[t];
    return limits.classCapTail;
  }

  // 数字串（重复单位）：只允许 1~8 位数字，且不能以 0 开头
  function normalizePattern(p) {
    var s = (p === undefined || p === null || p === '') ? '325' : String(p).trim();
    if (!/^\d{1,8}$/.test(s) || s.charAt(0) === '0') return null;
    return s;
  }

  function repeatPattern(pat, k) {
    var out = '';
    for (var i = 0; i < k; i++) out += pat;
    return out;
  }

  // ===================== 目标值解析 =====================
  function parseTarget(input) {
    if (input === null || input === undefined) return { ok: false, error: '请输入目标数字 n' };
    var s = String(input).trim();
    if (s === '') return { ok: false, error: '请输入目标数字 n' };
    s = s.replace(/[，,]/g, '.').replace(/[−–—]/g, '-').replace(/\s+/g, '');
    if (!/^[+-]?\d+(\.\d+)?$/.test(s)) return { ok: false, error: '只能输入整数或小数（可带负号），例如 325、1000、3.25、-7' };
    var neg = s[0] === '-';
    if (s[0] === '+' || s[0] === '-') s = s.slice(1);
    var parts = s.split('.');
    var intPart = parts[0];
    var fracPart = parts.length > 1 ? parts[1] : '';
    var den = 10n ** BigInt(fracPart.length);
    var num = BigInt(intPart + fracPart);
    if (neg) num = -num;
    return { ok: true, value: rat(num, den), text: (neg ? '-' : '') + intPart + (fracPart ? '.' + fracPart : '') };
  }

  // ===================== 表达式校验（独立于求解器） =====================
  function normalizeExpr(text) {
    return String(text)
      .replace(/[×✕✖]/g, '*')
      .replace(/[÷]/g, '/')
      .replace(/[−–—]/g, '-')
      .replace(/（/g, '(').replace(/）/g, ')')
      .replace(/！/g, '!')
      .replace(/\s+/g, '');
  }

  function parseDecimalToken(tok) {
    if (tok.indexOf('.') >= 0) {
      var p = tok.split('.');
      var den = 10n ** BigInt(p[1].length);
      return rat(BigInt(p[0] || '0') * den + BigInt(p[1] || '0'), den);
    }
    return rat(BigInt(tok));
  }

  // 递归下降求值：expr := term (('+'|'-') term)* ; term := factor (('*'|'/') factor)* ;
  //                factor := primary '!'* ; primary := number | '(' expr ')'
  function evaluateExpression(text, options) {
    var opts = options || {};
    var factLimit = opts.verifyFactMax === undefined ? 5000 : opts.verifyFactMax;
    var s = normalizeExpr(text);
    var pos = 0;
    function peek() { return s[pos]; }
    function parseExpr() {
      var v = parseTerm();
      while (peek() === '+' || peek() === '-') {
        var op = s[pos++];
        var r = parseTerm();
        v = op === '+' ? Radd(v, r) : Rsub(v, r);
      }
      return v;
    }
    function parseTerm() {
      var v = parseFactor();
      while (peek() === '*' || peek() === '/') {
        var op = s[pos++];
        var r = parseFactor();
        if (op === '*') v = Rmul(v, r);
        else { var q = Rdiv(v, r); if (!q) throw new Error('出现除以零'); v = q; }
      }
      return v;
    }
    function parseFactor() {
      var v = parsePrimary();
      while (peek() === '!') {
        pos++;
        if (!isIntRat(v) || v.n < 0n) throw new Error('阶乘只能用于非负整数');
        if (v.n > BigInt(factLimit)) throw new Error('阶乘参数过大，无法校验');
        v = rat(factorialBig(v.n));
      }
      return v;
    }
    function parsePrimary() {
      var c = peek();
      if (c === '(') {
        pos++;
        var v = parseExpr();
        if (peek() !== ')') throw new Error('缺少右括号');
        pos++;
        return v;
      }
      if (c !== undefined && c >= '0' && c <= '9') {
        var st = pos;
        while (pos < s.length && s[pos] >= '0' && s[pos] <= '9') pos++;
        if (s[pos] === '.') {
          pos++;
          while (pos < s.length && s[pos] >= '0' && s[pos] <= '9') pos++;
        }
        return parseDecimalToken(s.slice(st, pos));
      }
      throw new Error('无法识别的字符：' + (c === undefined ? '(式尾)' : c));
    }
    var value = parseExpr();
    if (pos !== s.length) throw new Error('表达式结尾有多余字符');
    return value;
  }

  function digitsOf(text) { return normalizeExpr(text).replace(/[^0-9]/g, ''); }

  // 数字序列是否合法（pattern = 重复单位，如 '325'）
  //   fixed —— 必须严格是 pattern 的原顺序重复
  //   any   —— 允许交换位置：整串数字的多重集合必须等于 pattern 重复 k 次
  function checkDigits(digits, orderMode, pattern) {
    var pat = normalizePattern(pattern) || '325';
    var L = pat.length;
    if (!digits || digits.length === 0 || digits.length % L !== 0) return false;
    var k = digits.length / L;
    if (orderMode === 'any') {
      var counts = {};
      for (var i = 0; i < L; i++) counts[pat.charAt(i)] = (counts[pat.charAt(i)] || 0) + k;
      for (var j = 0; j < digits.length; j++) {
        var c = digits.charAt(j);
        if (!counts[c]) return false;
        counts[c]--;
      }
      return true;
    }
    for (var m = 0; m < digits.length; m++) {
      if (digits.charAt(m) !== pat.charAt(m % L)) return false;
    }
    return true;
  }

  function verify(expression, target, options) {
    var opts = options || {};
    var orderMode = opts.orderMode || 'fixed';
    var pattern = normalizePattern(opts.pattern) || '325';
    var digits = digitsOf(expression);
    var out = {
      digits: digits, blocks: digits.length / pattern.length, digitsOk: checkDigits(digits, orderMode, pattern),
      orderMode: orderMode, pattern: pattern, value: null, valueOk: null, error: null
    };
    try {
      out.value = evaluateExpression(expression);
    } catch (e) {
      out.error = e.message;
      return out;
    }
    if (target !== undefined && target !== null) {
      var parsed = (target && typeof target === 'object' && target.n !== undefined)
        ? { ok: true, value: target } : parseTarget(target);
      if (parsed.ok) out.valueOk = eqRat(out.value, parsed.value);
    }
    return out;
  }

  function countTokens(expression) {
    var m = normalizeExpr(expression).match(/\d+(\.\d+)?/g);
    return m ? m.length : 0;
  }

  function pretty(text) {
    return String(text).replace(/\*/g, '×').replace(/\//g, '÷').replace(/-/g, '−');
  }

  // ===================== 区间 DP：按 token 数分轮 =====================
  function makeContext(limits) {
    return {
      limits: limits,
      cache: new Map(),                 // 子串 → state
      deadline: Date.now() + limits.timeBudgetMs,
      aborted: false,
      pruned: false,
      limited: false,
      steps: 0,
      maxAbsBig: BigInt(Math.round(limits.maxAbsValue)),
      maxDenBig: BigInt(Math.round(limits.maxDenominator)),
      maxDenNum: limits.maxDenominator,
      mulLimit: limits.maxAbsValue * 4,
      factMaxBig: BigInt(Math.round(limits.factMax))
    };
  }

  function magOf(r) { return numOf(r.n) / numOf(r.d); }

  // 结果是否在数值上限内
  function withinLimits(mag, denMag, ctx) {
    if (!(mag <= ctx.limits.maxAbsValue)) return false;
    if (!(denMag <= ctx.maxDenNum)) return false;
    return true;
  }

  function getState(sub, ctx) {
    var st = ctx.cache.get(sub);
    if (st) return st;
    st = { sub: sub, map: new Map(), byTokens: [], counts: [], built: 0 };
    for (var i = 0; i <= sub.length; i++) { st.byTokens.push([]); st.counts.push(0); }
    ctx.cache.set(sub, st);
    return st;
  }

  // 插入一个值（同一轮 t 内 first-wins，文本取最短）
  function putEntry(st, t, val, text, prec, ctx) {
    var k = rkey(val);
    var e = st.map.get(k);
    if (e) {
      if (text.length < e.text.length) { e.text = text; e.prec = prec; }
      return false;
    }
    var mag = magOf(val), denMag = numOf(val.d);
    if (!withinLimits(mag, denMag, ctx)) { ctx.limited = true; return false; }
    if (st.counts[t] >= classCapOf(ctx.limits, t)) { ctx.pruned = true; return false; }
    var entry = { val: val, mag: mag, denMag: denMag, text: text, prec: prec, tokens: t };
    st.map.set(k, entry);
    st.byTokens[t].push(entry);
    st.counts[t]++;
    return true;
  }

  // 已有更短写法时，不必拼接新文本
  function needText(st, key, est) {
    var e = st.map.get(key);
    return !e || e.text.length > est;
  }

  function parenWrap(text) { return '(' + text + ')'; }

  function addPair(st, t, A, B, ctx) {
    var any = ctx.limits.orderMode === 'any';
    var lim = ctx.mulLimit;
    // 需要时才算括号形式，避免无谓字符串拼接
    var B_add = B.prec <= PREC_ADD ? parenWrap(B.text) : B.text;
    var A_add = A.prec <= PREC_ADD ? parenWrap(A.text) : A.text;

    // a + b
    if (A.mag + B.mag <= lim) {
      var v1 = Radd(A.val, B.val), k1 = rkey(v1);
      if (needText(st, k1, A.text.length + 1 + B_add.length)) putEntry(st, t, v1, A.text + '+' + B_add, PREC_ADD, ctx);
    }
    // a - b
    if (A.mag + B.mag <= lim) {
      var v2 = Rsub(A.val, B.val), k2 = rkey(v2);
      if (needText(st, k2, A.text.length + 1 + B_add.length)) putEntry(st, t, v2, A.text + '-' + B_add, PREC_ADD, ctx);
    }
    // a * b
    if (A.mag * B.mag <= lim) {
      var A_mul = A.prec < PREC_MUL ? parenWrap(A.text) : A.text;
      var B_mul = B.prec < PREC_MUL ? parenWrap(B.text) : B.text;
      var v3 = Rmul(A.val, B.val), k3 = rkey(v3);
      if (needText(st, k3, A_mul.length + 1 + B_mul.length)) putEntry(st, t, v3, A_mul + '*' + B_mul, PREC_MUL, ctx);
    }
    // a / b
    if (B.val.n !== 0n && A.mag / B.mag <= lim && A.denMag * Math.abs(numOf(B.val.n)) <= ctx.maxDenNum) {
      var A_mul2 = A.prec < PREC_MUL ? parenWrap(A.text) : A.text;
      var B_mul2 = B.prec <= PREC_MUL ? parenWrap(B.text) : B.text;
      var v4 = Rdiv(A.val, B.val);
      if (v4) {
        var k4 = rkey(v4);
        if (needText(st, k4, A_mul2.length + 1 + B_mul2.length)) putEntry(st, t, v4, A_mul2 + '/' + B_mul2, PREC_MUL, ctx);
      }
    }
    if (!any) return;
    // b - a、b ÷ a 会把右边数字写到左边，仅 orderMode='any' 时合法
    if (A.mag + B.mag <= lim) {
      var v5 = Rsub(B.val, A.val), k5 = rkey(v5);
      if (needText(st, k5, B.text.length + 1 + A_add.length)) putEntry(st, t, v5, B.text + '-' + A_add, PREC_ADD, ctx);
    }
    if (A.val.n !== 0n && B.mag / A.mag <= lim && B.denMag * Math.abs(numOf(A.val.n)) <= ctx.maxDenNum) {
      var B_mul3 = B.prec < PREC_MUL ? parenWrap(B.text) : B.text;
      var A_mul3 = A.prec <= PREC_MUL ? parenWrap(A.text) : A.text;
      var v6 = Rdiv(B.val, A.val);
      if (v6) {
        var k6 = rkey(v6);
        if (needText(st, k6, B_mul3.length + 1 + A_mul3.length)) putEntry(st, t, v6, B_mul3 + '/' + A_mul3, PREC_MUL, ctx);
      }
    }
  }

  // 阶乘不改变 token 个数：对第 t 轮新增的值做闭包
  function applyFactorials(st, t, ctx) {
    if (!ctx.limits.allowFactorial) return;
    var i = 0, rounds = 0;
    while (rounds++ < 8 && !ctx.aborted) {
      var bucket = st.byTokens[t];
      var added = false;
      for (; i < bucket.length && !ctx.aborted; i++) {
        var e = bucket[i];
        if (e.val.d !== 1n || e.val.n < 0n || e.val.n > ctx.factMaxBig) continue;
        var f = rat(factorialBig(e.val.n));
        var text = e.prec < PREC_ATOM ? parenWrap(e.text) + '!' : e.text + '!';
        if (putEntry(st, t, f, text, PREC_ATOM, ctx)) added = true;
      }
      if (!added && i >= bucket.length) break;
    }
  }

  function tokenValues(sub, limits) {
    var out = [{ val: rat(BigInt(sub)), text: sub, prec: PREC_ATOM }];
    if (limits.allowDecimal) {
      for (var m = 1; m < sub.length; m++) {
        var a = sub.slice(0, m), b = sub.slice(m);
        var den = 10n ** BigInt(b.length);
        out.push({ val: rat(BigInt(a) * den + BigInt(b), den), text: a + '.' + b, prec: PREC_ATOM });
      }
    }
    return out;
  }

  // 保证子串 sub 至少构建到 need 轮（token 数），增量构建，可复用
  function ensure(sub, need, ctx) {
    var st = getState(sub, ctx);
    var len = sub.length;
    if (need > len) need = len;
    if (st.built >= need) return st;

    if (st.built === 0) {
      var toks = tokenValues(sub, ctx.limits);
      for (var i = 0; i < toks.length; i++) putEntry(st, 1, toks[i].val, toks[i].text, toks[i].prec, ctx);
      st.built = 1;
      applyFactorials(st, 1, ctx);
      if (need <= 1 || ctx.aborted) return st;
    }

    for (var t = st.built + 1; t <= need; t++) {
      for (var cut = 1; cut < len; cut++) {
        var lt = ensure(sub.slice(0, cut), t - 1, ctx);
        var rt = ensure(sub.slice(cut), t - 1, ctx);
        if (ctx.aborted) break;
        for (var i2 = 1; i2 < t; i2++) {
          var j = t - i2;
          var La = lt.byTokens[i2], Ra = rt.byTokens[j];
          if (!La || !Ra || !La.length || !Ra.length) continue;
          for (var x = 0; x < La.length; x++) {
            var A = La[x];
            for (var y = 0; y < Ra.length; y++) {
              if ((++ctx.steps & 1023) === 0 && Date.now() > ctx.deadline) { ctx.aborted = true; break; }
              addPair(st, t, A, Ra[y], ctx);
            }
            if (ctx.aborted) break;
          }
          if (ctx.aborted) break;
        }
        if (ctx.aborted) break;
      }
      st.built = t;
      if (ctx.aborted) break;
      applyFactorials(st, t, ctx);
    }
    return st;
  }

  // 在已有结果上回溯，收集同一个值的多种写法
  function collectWritings(sub, targetKey, ctx, limit, out, seen) {
    if (out.length >= limit) return;
    var st = ctx.cache.get(sub);
    if (!st) return;
    var tgt = parseKeyToRat(targetKey);
    var direct = st.map.get(targetKey);
    if (direct) addWriting(out, seen, direct.text, limit);
    if (out.length >= limit) return;

    // 顶层阶乘
    if (ctx.limits.allowFactorial) {
      for (var t = 1; t < st.byTokens.length; t++) {
        var bucket = st.byTokens[t];
        for (var i = 0; i < bucket.length && out.length < limit; i++) {
          var e = bucket[i];
          if (e.val.d !== 1n || e.val.n < 0n || e.val.n > ctx.factMaxBig) continue;
          if (rkey(rat(factorialBig(e.val.n))) !== targetKey) continue;
          addWriting(out, seen, e.prec < PREC_ATOM ? parenWrap(e.text) + '!' : e.text + '!', limit);
        }
      }
      if (out.length >= limit) return;
    }

    // 顶层是二元运算
    var any = ctx.limits.orderMode === 'any';
    for (var cut = 1; cut < sub.length && out.length < limit; cut++) {
      var L = ctx.cache.get(sub.slice(0, cut));
      var Rm = ctx.cache.get(sub.slice(cut));
      if (!L || !Rm) continue;
      for (var ti = 1; ti < sub.length && out.length < limit; ti++) {
        var La = L.byTokens[ti];
        if (!La || !La.length) continue;
        for (var tj = 1; tj < sub.length && out.length < limit; tj++) {
          var Ra = Rm.byTokens[tj];
          if (!Ra || !Ra.length) continue;
          for (var x = 0; x < La.length && out.length < limit; x++) {
            var A = La[x];
            for (var y = 0; y < Ra.length && out.length < limit; y++) {
              var B = Ra[y];
              var bAdd = B.prec <= PREC_ADD ? parenWrap(B.text) : B.text;
              if (eqRat(Radd(A.val, B.val), tgt)) addWriting(out, seen, A.text + '+' + bAdd, limit);
              if (out.length >= limit) break;
              if (eqRat(Rsub(A.val, B.val), tgt)) addWriting(out, seen, A.text + '-' + bAdd, limit);
              if (out.length >= limit) break;
              if (eqRat(Rmul(A.val, B.val), tgt)) addWriting(out, seen, (A.prec < PREC_MUL ? parenWrap(A.text) : A.text) + '*' + (B.prec < PREC_MUL ? parenWrap(B.text) : B.text), limit);
              if (out.length >= limit) break;
              var d1 = Rdiv(A.val, B.val);
              if (d1 && eqRat(d1, tgt)) addWriting(out, seen, (A.prec < PREC_MUL ? parenWrap(A.text) : A.text) + '/' + (B.prec <= PREC_MUL ? parenWrap(B.text) : B.text), limit);
              if (!any || out.length >= limit) continue;
              if (eqRat(Rsub(B.val, A.val), tgt)) addWriting(out, seen, B.text + '-' + (A.prec <= PREC_ADD ? parenWrap(A.text) : A.text), limit);
              if (out.length >= limit) break;
              var d2 = Rdiv(B.val, A.val);
              if (d2 && eqRat(d2, tgt)) addWriting(out, seen, (B.prec < PREC_MUL ? parenWrap(B.text) : B.text) + '/' + (A.prec <= PREC_MUL ? parenWrap(A.text) : A.text), limit);
            }
          }
        }
      }
    }
  }

  function parseKeyToRat(k) {
    var p = k.split('/');
    return { n: BigInt(p[0]), d: BigInt(p[1]) };
  }
  function addWriting(out, seen, text, limit) {
    if (out.length >= limit) return;
    if (seen.has(text)) return;
    seen.add(text);
    out.push(text);
  }

  // ===================== 会话：跨目标、跨块数复用 DP =====================
  function mergeLimits(options) {
    var limits = {};
    for (var k in DEFAULTS) if (Object.prototype.hasOwnProperty.call(DEFAULTS, k)) limits[k] = DEFAULTS[k];
    if (options) for (var k2 in options) if (Object.prototype.hasOwnProperty.call(options, k2)) limits[k2] = options[k2];
    limits.maxBlocks = Math.max(1, Math.min(6, Math.round(limits.maxBlocks)));
    return limits;
  }

  function createSession(options) {
    var limits = mergeLimits(options);
    var pat = normalizePattern(limits.pattern);
    if (!pat) pat = '325';
    limits.pattern = pat;
    var ctx = makeContext(limits);

    // ---------- 保底构造（与具体数字串无关） ----------
    // 记 P = 数字串本身组成的整数（325 / 799 / 325799 / 114514 …），1 块 = 数字串写一遍。
    //   n ≥ P      →  P×(q 的式子) + (r 的式子)，q = n÷P，r = n mod P，递归下去
    //   0 ≤ n < P  →  先用少量块直接搜；搜不到就用十进制拆分：Σ dᵢ×10ⁱ
    //                （dᵢ 用「1 的写法」累加，10ⁱ 用「10 的写法」连乘；都来自本数字串的搜索）
    //   n < 0      →  (0 的写法) - (|n| 的式子)
    //   小数 n/d   →  (n 的式子) ÷ (d 的式子)
    // 每一步都是「整块」的子式子，所以数字顺序天然保持 数字串数字串数字串…
    function patToken(pat) { return pat; }

    function lookupValue(pat, value, maxBlocks, maxTokens, ctx) {
      var key = rkey(value);
      for (var k = 1; k <= maxBlocks; k++) {
        if (k > 1 && pat.length * k > 12) break;      // 长数字串只查到 2 块/12 位，避免保底阶段过慢
        // 短的子串（≤6 位）允许搜满所有轮次以找到巧解；长串限流
        var tok = Math.min(maxTokens, (pat.length * k <= 6) ? 6 : 4, pat.length * k);
        var st = ensure(repeatPattern(pat, k), tok, ctx);
        var e = st.map.get(key);
        if (e) return { expr: e.text, blocks: k };
        if (ctx.aborted) break;
      }
      return null;
    }

    function buildFallbackTools(pat, ctx, limits) {
      var P = BigInt(pat);
      var cache = new Map();
      function unit(name, value, maxBlocks, maxTokens) {
        var ck = 'u' + name;
        if (!cache.has(ck)) cache.set(ck, lookupValue(pat, value, maxBlocks, maxTokens, ctx));
        return cache.get(ck);
      }
      function one() { return unit('one', rat(1n), 3, 3); }
      function ten() { return unit('ten', rat(10n), 3, 4); }
      function zero() { return unit('zero', rat(0n), 2, 3); }

      // d 个 unit 相加
      function sumCopies(u, count) {
        if (count === 1) return { expr: '(' + u.expr + ')', blocks: u.blocks };
        var parts = [];
        for (var i = 0; i < count; i++) parts.push('(' + u.expr + ')');
        return { expr: parts.join('+'), blocks: count * u.blocks };
      }
      // unit 连乘 count 次
      function mulCopies(u, count) {
        var parts = [];
        for (var i = 0; i < count; i++) parts.push('(' + u.expr + ')');
        return { expr: parts.join('*'), blocks: count * u.blocks };
      }

      // 1..9 的写法：优先用 DP 里的巧解（例如 325 的 3 可以写成 3!−2+5），否则用 1 累加
      function smallInt(d) {
        var ck = 'si' + d;
        if (cache.has(ck)) return cache.get(ck);
        var r = lookupValue(pat, rat(BigInt(d)), 3, 4, ctx);
        if (!r) {
          var u = one();
          if (u && d <= 9) r = sumCopies(u, d);
        }
        cache.set(ck, r);
        return r;
      }
      // 10^p 的写法：优先查 DP（例如 325 的 100 = 325/3.25），否则用 10 连乘
      function powerUnit(p) {
        var ck = 'p' + p;
        if (cache.has(ck)) return cache.get(ck);
        var target = rat(BigInt('1' + new Array(p + 1).join('0')));
        var r = lookupValue(pat, target, 3, 4, ctx);
        if (!r) {
          var t = ten();
          if (t) r = mulCopies(t, p);
        }
        cache.set(ck, r);
        return r;
      }

      // 0 ≤ m < P：先直接搜，再十进制拆分（系数与 10 的幂都尽量用 DP 巧解）
      function base(m) {
        var ck = 'b' + m;
        if (cache.has(ck)) return cache.get(ck);
        var res = lookupValue(pat, rat(BigInt(m)), 2, 6, ctx);
        if (!res && m === 0) res = zero();
        if (!res && m > 0) {
          var ds = String(m), terms = [], blocks = 0, ok = true;
          for (var i = 0; i < ds.length; i++) {
            var d = Number(ds.charAt(i));
            if (d === 0) continue;
            var power = ds.length - 1 - i;
            var coef = smallInt(d);
            if (!coef) { ok = false; break; }
            if (power === 0) {
              terms.push('(' + coef.expr + ')');
              blocks += coef.blocks;
            } else {
              var pw = powerUnit(power);
              if (!pw) { ok = false; break; }
              terms.push('(' + coef.expr + ')*(' + pw.expr + ')');
              blocks += coef.blocks + pw.blocks;
            }
          }
          res = ok && terms.length ? { expr: terms.join('+'), blocks: blocks } : null;
        }
        cache.set(ck, res);
        return res;
      }

      function build(n) {                       // n: 非负 BigInt
        var ck = 'n' + n.toString();
        if (cache.has(ck)) return cache.get(ck);
        var res = null;
        if (n < P) {
          res = base(Number(n));
        } else {
          var q = n / P, r = n % P;
          var qe = build(q);
          if (qe) {
            var text = pat + '*(' + qe.expr + ')';
            var blocks = 1 + qe.blocks;
            if (r > 0n) {
              var re = base(Number(r));
              if (re) { text += '+(' + re.expr + ')'; blocks += re.blocks; }
              else { text = null; }
            }
            res = text === null ? null : { expr: text, blocks: blocks };
          }
        }
        cache.set(ck, res);
        return res;
      }

      return {
        P: P,
        one: one,
        zero: zero,
        build: build,
        base: base
      };
    }

    function structuredFallback(targetRat, ctx, limits) {
      // 保底阶段不应被主搜索的时间预算饿死
      ctx.aborted = false;
      ctx.deadline = Date.now() + (limits.fallbackBudgetMs || 4000);
      var tools = buildFallbackTools(pat, ctx, limits);
      var z = tools.zero();
      var zeroText = z ? z.expr : null;

      function wrap(t) { return { expression: t, pretty: pretty(t), blocks: 0, kind: 'structured' }; }

      var t = targetRat;
      if (t.d === 1n) {
        if (t.n === 0n) {
          if (!zeroText) return null;
          return wrap(zeroText);
        }
        var b = tools.build(t.n < 0n ? -t.n : t.n);
        if (!b) return null;
        if (t.n > 0n) return wrap(b.expr);
        if (!zeroText) return null;
        return wrap(zeroText + '-(' + b.expr + ')');
      }
      // 小数：target = n/d
      var pos = (t.n < 0n ? -t.n : t.n);
      var numExpr;
      if (pos === 0n) { if (!zeroText) return null; numExpr = zeroText; }
      else {
        var nb = tools.build(pos);
        if (!nb) return null;
        numExpr = nb.expr;
      }
      var db = tools.build(t.d);
      if (!db) return null;
      var fracText = '(' + numExpr + ')÷(' + db.expr + ')';
      if (t.n < 0n) {
        if (!zeroText) return null;
        fracText = zeroText + '-(' + fracText + ')';
      }
      var ascii = fracText.replace(/÷/g, '/');
      return { expression: ascii, pretty: pretty(ascii), blocks: 0, kind: 'structured' };
    }

    function solveOne(targetInput) {
      var parsed = (targetInput && typeof targetInput === 'object' && targetInput.n !== undefined)
        ? { ok: true, value: targetInput, text: ratToText(targetInput) }
        : parseTarget(targetInput);
      var result = {
        ok: false, target: null, targetText: null, expression: null, expressionPretty: null,
        blocks: 0, tokens: 0, digits: '', tokenCount: 0, digitsOk: false, valueOk: false,
        alternatives: [], notes: [], elapsedMs: 0, pruned: false, limited: false,
        aborted: false, searchedBlocks: [], stats: [], fallback: null, error: null,
        searchMode: limits.searchMode, orderMode: limits.orderMode, pattern: pat, entries: 0
      };
      if (!parsed.ok) { result.error = parsed.error; return result; }
      result.target = parsed.value;
      result.targetText = parsed.text;

      var started = Date.now();
      ctx.deadline = started + limits.timeBudgetMs;
      ctx.aborted = false;
      ctx.pruned = false;
      ctx.limited = false;
      var targetKey = rkey(parsed.value);
      var hits = null;

      for (var kk = 1; kk <= limits.maxBlocks; kk++) {
        if (ctx.aborted) break;
        var digits = repeatPattern(pat, kk);
        var t0 = Date.now(), lastTok = 0, lastSize = 0;
        for (var t = 1; t <= digits.length; t++) {
          var st = ensure(digits, t, ctx);
          lastTok = t;
          lastSize = st.map.size;
          var entry = st.map.get(targetKey);
          if (entry) {
            if (!hits || limits.searchMode !== 'length' || entry.text.length < hits.text.length) {
              hits = { blocks: kk, digits: digits, text: entry.text, tokens: entry.tokens || t };
            }
            if (limits.searchMode !== 'length') break;
          }
          if (ctx.aborted) break;
        }
        result.searchedBlocks.push(kk);
        result.stats.push({ k: kk, values: lastSize, tokensBuilt: lastTok, ms: Date.now() - t0, pruned: ctx.pruned, limited: ctx.limited });
        if (hits && limits.searchMode !== 'length') break;
        if (ctx.aborted) break;
      }

      result.elapsedMs = Date.now() - started;
      result.pruned = ctx.pruned;
      result.limited = ctx.limited;
      result.aborted = ctx.aborted;
      result.entries = 0;
      ctx.cache.forEach(function (st) { result.entries += st.map.size; });

      if (hits) {
        result.ok = true;
        result.blocks = hits.blocks;
        result.digits = hits.digits;
        result.expression = hits.text;
        result.expressionPretty = pretty(hits.text);
        result.tokenCount = countTokens(hits.text);
        result.tokens = hits.tokens;

        var out = [hits.text], seen = new Set(out);
        collectWritings(hits.digits, targetKey, ctx, limits.solutionListLimit, out, seen);
        var ranked = out.slice().sort(function (a, b) {
          return a.length - b.length || (a < b ? -1 : a > b ? 1 : 0);
        });
        for (var i = 0; i < ranked.length; i++) {
          var chk0 = verify(ranked[i], parsed.value, { orderMode: limits.orderMode, pattern: pat });
          if (!(chk0.digitsOk && chk0.valueOk)) continue;
          result.alternatives.push({ expression: ranked[i], pretty: pretty(ranked[i]), blocks: hits.blocks, length: ranked[i].length });
        }
        if (!result.alternatives.length) {
          result.alternatives.push({ expression: hits.text, pretty: pretty(hits.text), blocks: hits.blocks, length: hits.text.length });
        }
        var chk = verify(hits.text, parsed.value, { orderMode: limits.orderMode, pattern: pat });
        result.digitsOk = chk.digitsOk;
        result.valueOk = chk.valueOk === true;
        if (!result.digitsOk || !result.valueOk) {
          result.ok = false;
          result.error = '内部校验未通过：' + (chk.error || '数字顺序或求值不符');
        }
      } else {
        var fb = null;
        try {
          fb = structuredFallback(parsed.value, ctx, limits);
        } catch (e) { fb = null; }
        if (fb) {
          var fchk = verify(fb.expression, parsed.value, { orderMode: limits.orderMode, pattern: pat });
          if (fchk.digitsOk && fchk.valueOk) {
            fb.blocks = fchk.blocks;
          } else {
            fb = null;
          }
        }
        if (!fb) {
          // 次选：用本数字串能表达出的「1」累加（若连 1 都表达不出则没有保底）
          var tools = null;
          try { tools = buildFallbackTools(pat, ctx, limits); } catch (e) { tools = null; }
          var u1 = tools ? tools.one() : null;
          if (u1) {
            var simple = fallbackExpression(parsed.value, {
              unitExpr: u1.expr, unitBlocks: u1.blocks,
              zeroExpr: tools.zero() ? tools.zero().expr : null
            });
            if (simple) {
              var schk = verify(simple.expression, parsed.value, { orderMode: limits.orderMode, pattern: pat });
              if (schk.digitsOk && schk.valueOk) { simple.blocks = schk.blocks; fb = simple; }
            }
          }
        }
        if (fb) {
          fb.pretty = pretty(fb.expression);
          result.fallback = fb;
        }
      }
      // 只在“超时”时提示：按设计的剪枝/数值上限属于内部细节，不打扰使用者
      if (result.aborted) result.notes.push('搜索超时，结果可能不完整。');
      return result;
    }

    return {
      solve: solveOne,
      limits: limits,
      reset: function () { ctx.cache = new Map(); ctx.pruned = false; ctx.limited = false; ctx.aborted = false; },
      cacheSize: function () { return ctx.cache.size; }
    };
  }

  function solve(targetInput, options) {
    return createSession(options).solve(targetInput);
  }

  // ===================== 保底构造 =====================
  //   n = 0  -> 3+2-5
  //   n > 0  -> (3*2-5)+(3*2-5)+…          共 n 份，每份 = 1
  //   n < 0  -> (3+2-5)-(3*2-5)-(3*2-5)…   共 |n| 份
  // 数字顺序始终是 325325…，在 fixed 模式下同样合法。
  // 次选保底：用「1 的写法」累加。u / z 由会话用同一数字串搜索得到，数字顺序天然合法。
  function fallbackExpression(target, options) {
    var limits = options || {};
    var cap = limits.fallbackBlockLimit === undefined ? 3000 : limits.fallbackBlockLimit;
    var unit = limits.unitExpr || null;
    var unitBlocks = limits.unitBlocks || 0;
    var zeroExpr = limits.zeroExpr || null;
    var t = (target && target.n !== undefined) ? target : ((parseTarget(target) || {}).value || null);
    if (!t || t.d !== 1n || !unit || unitBlocks <= 0) return null;
    var count = t.n < 0n ? -t.n : t.n;
    if (count > BigInt(cap)) return null;
    var n = Number(count);
    if (t.n > 0n) {
      if (n === 0) return null;
      var parts = [];
      for (var i = 0; i < n; i++) parts.push(n === 1 ? unit : '(' + unit + ')');
      var expr = parts.join('+');
      return { expression: expr, pretty: pretty(expr), blocks: n * unitBlocks };
    }
    if (!zeroExpr) return null;
    if (n === 0) return { expression: zeroExpr, pretty: pretty(zeroExpr), blocks: 0 };
    var p2 = [zeroExpr];
    for (var j = 0; j < n; j++) p2.push('(' + unit + ')');
    var negExpr = p2.join('-');
    return { expression: negExpr, pretty: pretty(negExpr), blocks: 0 };
  }

  return {
    solve: solve,
    createSession: createSession,
    verify: verify,
    evaluateExpression: evaluateExpression,
    parseTarget: parseTarget,
    fallbackExpression: fallbackExpression,
    pretty: pretty,
    countTokens: countTokens,
    digitsOf: digitsOf,
    checkDigits: checkDigits,
    normalizePattern: normalizePattern,
    repeatPattern: repeatPattern,
    PATTERNS: ['325', '799', '325799', '114514'],
    DEFAULTS: DEFAULTS
  };
});
