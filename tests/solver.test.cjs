/*
 * Calculate325 求解器测试 / 基准
 * 运行： node tests/solver.test.cjs
 */
'use strict';
const path = require('path');
const S = require(path.join(__dirname, '..', 'src', 'solver.js'));

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) pass++;
  else { fail++; failures.push(name + (extra ? '  →  ' + extra : '')); }
}

// ---------- 独立求值器（与 solver 的实现方式不同：调度场 + 浮点） ----------
function independentEval(text) {
  const s = String(text)
    .replace(/[×✕✖]/g, '*').replace(/÷/g, '/').replace(/[−–—]/g, '-')
    .replace(/（/g, '(').replace(/）/g, ')').replace(/！/g, '!').replace(/\s+/g, '');
  const out = [], ops = [];
  const prec = { '+': 1, '-': 1, '*': 2, '/': 2 };
  const apply1 = (op) => {
    if (op === '!') {
      const a = out.pop();
      if (!Number.isInteger(a) || a < 0) throw new Error('bad factorial');
      let r = 1;
      for (let i = 2; i <= a; i++) r *= i;
      out.push(r);
      return;
    }
    const b = out.pop(), a = out.pop();
    out.push(op === '+' ? a + b : op === '-' ? a - b : op === '*' ? a * b : a / b);
  };
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (/[0-9.]/.test(c)) {
      let j = i; while (j < s.length && /[0-9.]/.test(s[j])) j++;
      out.push(parseFloat(s.slice(i, j)));
      i = j - 1;
    } else if (c === '!') apply1('!');
    else if (c === '(') ops.push(c);
    else if (c === ')') { while (ops.length && ops[ops.length - 1] !== '(') apply1(ops.pop()); ops.pop(); }
    else { while (ops.length && ops[ops.length - 1] !== '(' && prec[ops[ops.length - 1]] >= prec[c]) apply1(ops.pop()); ops.push(c); }
  }
  while (ops.length) apply1(ops.pop());
  return out[0];
}
const digitsOnly = (t) => String(t).replace(/[^0-9]/g, '');
const asNumber = (r) => Number(r.n) / Number(r.d);
const closeTo = (a, b) => Math.abs(a - b) <= 1e-9 * Math.max(1, Math.abs(b));

function assertSolution(tag, r) {
  check(`${tag} 有解`, r.ok, JSON.stringify(r.notes) + (r.error ? ' ' + r.error : ''));
  if (!r.ok) return;
  const mode = r.orderMode || 'fixed';
  const v = S.verify(r.expression, r.target, { orderMode: mode });
  const digits = digitsOnly(r.expression);
  const digitsOkExpected = mode === 'any'
    ? (() => { const c = { 3: 0, 2: 0, 5: 0 }; for (const d of digits) c[d]++; return c[3] === c[2] && c[2] === c[5] && c[3] > 0; })()
    : digits === '325'.repeat(r.blocks);
  check(`${tag} 数字序列符合规则(${mode})`, v.digitsOk && digitsOkExpected, digits);
  check(`${tag} 精确求值等于目标`, v.valueOk === true, JSON.stringify(v.value && { n: v.value.n.toString(), d: v.value.d.toString() }));
  check(`${tag} 独立求值等于目标`, closeTo(independentEval(r.expression), asNumber(r.target)), String(independentEval(r.expression)));
  check(`${tag} 内部标志自洽`, r.digitsOk === true && r.valueOk === true && r.tokenCount === (r.expression.match(/\d+(\.\d+)?/g) || []).length);
  for (const alt of r.alternatives) {
    const av = S.verify(alt.expression, r.target, { orderMode: mode });
    check(`${tag} 备选写法合法: ${alt.expression}`, av.digitsOk && av.valueOk === true, JSON.stringify(av.error || ''));
  }
}

// ---------- 1. 基本用例（fixed 模式，最多 2 块） ----------
console.log('=== 基本用例（fixed，最多 2 块） ===');
for (const c of ['325', '1', '0', '-1', '3.25', '6', '32.5', '0.5', '2', '10']) {
  const r = S.solve(c, { maxBlocks: 2 });
  console.log(`  ${c} → ${r.ok ? r.expressionPretty : '(无解，走保底)'}   块数=${r.blocks} 用时=${r.elapsedMs}ms 备选=${r.alternatives.length}`);
  assertSolution('基本 ' + c, r);
}

// ---------- 2. 最少块数与顺序规则 ----------
console.log('=== 块数与顺序规则 ===');
{
  const one = S.solve('2', { maxBlocks: 1 });
  check('fixed: 2 在 1 块内无解', !one.ok, one.ok ? '意外得到 ' + one.expression : '');
  const two = S.solve('2', { maxBlocks: 2 });
  check('fixed: 2 在 2 块内有解且块数=2', two.ok && two.blocks === 2, 'blocks=' + two.blocks + ' expr=' + two.expression);
  const p = S.solve('325', { maxBlocks: 1 });
  check('325 直接用 1 块', p.ok && p.blocks === 1 && p.expression === '325', p.expression);
  // any 模式应当更强：2 可以在 1 块内解决
  const anyOne = S.solve('2', { maxBlocks: 1, orderMode: 'any' });
  console.log(`  any 模式 2 → ${anyOne.ok ? anyOne.expressionPretty : '(无解)'} (${anyOne.blocks} 块)`);
  check('any: 2 在 1 块内有解', anyOne.ok && anyOne.blocks === 1, anyOne.ok ? anyOne.expression : JSON.stringify(anyOne.notes));
  if (anyOne.ok) assertSolution('any 2', anyOne);
}

// ---------- 3. 覆盖率（-40..80，最多 2 块，会话复用 DP） ----------
console.log('=== 覆盖率（-40..80，最多 2 块） ===');
let fixedCoverage = 0;
{
  const session = S.createSession({ maxBlocks: 2 });
  const unsolved = [], invalid = [];
  const t0 = Date.now();
  for (let n = -40; n <= 80; n++) {
    const r = session.solve(String(n));
    if (!r.ok) {
      unsolved.push(n);
      // 未解出的整数目标必须给出合法保底构造
      if (!r.fallback) invalid.push(n + '(无保底)');
      else {
        const fv = S.verify(r.fallback.expression, r.target, { orderMode: r.orderMode });
        if (!(fv.digitsOk && fv.valueOk)) invalid.push(n + '(保底非法)');
      }
      continue;
    }
    const v = S.verify(r.expression, r.target, { orderMode: r.orderMode });
    const ind = independentEval(r.expression);
    if (!(v.digitsOk && v.valueOk === true)) invalid.push(n + '(校验失败:' + r.expression + ')');
    else if (!closeTo(ind, n)) invalid.push(n + '(独立求值 ' + ind + '≠' + n + ')');
  }
  fixedCoverage = 121 - unsolved.length;
  console.log(`  直接解出 ${fixedCoverage}/121，用时 ${Date.now() - t0}ms，缓存子串 ${session.cacheSize()}`);
  console.log(`  未直接解出（有保底构造）：${unsolved.join(', ') || '无'}`);
  check('覆盖率样例全部合法', invalid.length === 0, invalid.join(', '));
  check('覆盖率不低于 120/121（防止算法退化）', fixedCoverage >= 120, 'coverage=' + fixedCoverage);
}

// ---------- 4. 小数 / 负数 ----------
console.log('=== 小数 / 负数（fixed，最多 2 块） ===');
for (const t of ['3.25', '-3.25', '-325', '0.5', '1234.5', '-0.4']) {
  const r = S.solve(t, { maxBlocks: 2 });
  console.log(`  ${t} → ${r.ok ? r.expressionPretty : '(无解，走保底)'}${r.ok ? `  (${r.blocks} 块)` : ''}`);
  if (r.ok) assertSolution('小数/负数 ' + t, r);
  else check('小数/负数 ' + t + ' 未解出时不应谎报', !r.ok);
}

// ---------- 5. 保底构造 ----------
console.log('=== 保底构造 ===');
{
  const r = S.solve('1000', { maxBlocks: 1 });
  check('1000 在 1 块内无解', !r.ok);
  check('1000 有保底构造', !!r.fallback);
  if (r.fallback) {
    const v = S.verify(r.fallback.expression, r.target, { orderMode: r.orderMode });
    check('保底构造数字顺序（fixed）', v.digitsOk && v.digits === '325'.repeat(r.fallback.blocks), 'len=' + v.digits.length);
    check('保底构造求值正确', v.valueOk === true);
    console.log(`  1000 → 保底 ${r.fallback.blocks} 块，式子长度 ${r.fallback.expression.length}`);
  }
  const neg = S.solve('-500', { maxBlocks: 1 });
  check('负数有保底构造', !!neg.fallback);
  if (neg.fallback) {
    const v2 = S.verify(neg.fallback.expression, neg.target, { orderMode: neg.orderMode });
    check('负数保底构造合法', v2.digitsOk && v2.valueOk === true);
  }
}

// ---------- 6. 保底构造：大数 / 小数 / 负数 ----------
console.log('=== 保底构造（1 块内必然无解，考察保底质量） ===');
for (const t of ['12345', '99999', '123456', '1000000', '987654321', '-98765', '1234.5', '-3.14159']) {
  const r = S.solve(t, { maxBlocks: 1, timeBudgetMs: 3000 });
  if (r.ok) { console.log(`  ${t} → 1 块内直接解出 ${r.expression}`); continue; }
  const fb = r.fallback;
  check(`保底存在 ${t}`, !!fb, JSON.stringify(r.notes));
  if (!fb) continue;
  const v = S.verify(fb.expression, t, { orderMode: r.orderMode });
  check(`保底合法 ${t}`, v.digitsOk && v.valueOk === true, fb.expression + ' / ' + JSON.stringify(v.error));
  check(`保底不太长 ${t}`, fb.expression.length <= 300, 'len=' + fb.expression.length);
  console.log(`  ${t} → 保底 ${fb.blocks} 块 / ${fb.expression.length} 字符：${fb.pretty.length > 80 ? fb.pretty.slice(0, 80) + '…' : fb.pretty}`);
}

// ---------- 7. 校验器自身的正确性 ----------
console.log('=== 校验器 ===');{
  check('校验器拒绝乱序数字（fixed）', S.verify('5-2-3', 0).digitsOk === false);
  check('校验器接受乱序数字（any）', S.verify('5-2-3', 0, { orderMode: 'any' }).digitsOk === true);
  check('校验器接受 3+2-5', S.verify('3+2-5', 0).digitsOk === true && S.verify('3+2-5', 0).valueOk === true);
  check('校验器识别求值错误', S.verify('3+2-5', 1).valueOk === false);
  check('校验器处理阶乘', S.verify('3!', 6).valueOk === true);
  check('校验器处理小数点', S.verify('3.25', 3.25).valueOk === true);
  check('校验器识别除零', S.verify('3/(2-2)', 0).error !== null);
}

// ---------- 8. 多种数字串：325 / 799 / 325799 / 114514 ----------
console.log('=== 多种数字串 ===');
{
  for (const pat of S.PATTERNS) {
    for (const t of ['1', '0', '-1', '10', '1000', '3.25']) {
      const r = S.solve(t, { pattern: pat, maxBlocks: 2, timeBudgetMs: 8000 });
      const expr = r.ok ? r.expression : (r.fallback ? r.fallback.expression : null);
      check(`数字串 ${pat} 目标 ${t} 有结果`, !!expr, JSON.stringify(r.notes));
      if (!expr) continue;
      const v = S.verify(expr, r.target, { orderMode: r.orderMode, pattern: pat });
      check(`数字串 ${pat} 目标 ${t} 合法`, v.digitsOk && v.valueOk === true, expr);
      check(`数字串 ${pat} 目标 ${t} 独立求值`, closeTo(independentEval(expr), Number(r.target.n) / Number(r.target.d)), expr);
      // 数字串必须由所选 pattern 重复构成
      const digits = digitsOnly(expr);
      check(`数字串 ${pat} 目标 ${t} 数字序列`, digits === pat.repeat(digits.length / pat.length), digits);
    }
  }
  // 交叉：325 的式子不能当作 799 的式子
  check('325 的式子不能算作 799', S.verify('3*2-5', 1, { pattern: '799' }).digitsOk === false);
  check('799 的式子不能算作 325', S.verify('7*(9-9)', 0, { pattern: '325' }).digitsOk === false);
  check('114514 的式子判定正确', S.verify('(1-1)*4514', 0, { pattern: '114514' }).digitsOk === true);
  check('normalizePattern 校验非法输入', S.normalizePattern('0x') === null && S.normalizePattern('') === '325');
  // 不再输出“剪枝 / 数值上限”之类多余提示；只有真的超时才给一条
  const r2 = S.solve('1000', { pattern: '325', maxBlocks: 2, timeBudgetMs: 8000 });
  check('剪枝不再产生多余提示', r2.notes.length === 0, JSON.stringify(r2.notes));
  const r3 = S.solve('2025', { pattern: '325', maxBlocks: 2, timeBudgetMs: 1 });
  check('超时只给一条提示', r3.aborted !== true || (r3.notes.length === 1 && /超时/.test(r3.notes[0])), JSON.stringify(r3.notes));
}

console.log('\n===== 结果：通过 ' + pass + ' / 失败 ' + fail + ' =====');
if (failures.length) { console.log('失败项：'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
