/*
 * 生成 C# 版的对齐基准： node tests/gen-expectations.cjs
 * 输出 tests/expected-js.tsv（制表符分隔，供 Calculate325-tests.exe 逐条比对）
 * 列：target, maxBlocks, orderMode, allowDecimal, allowFactorial, factMax, ok, expression, blocks, fallback, pattern
 */
'use strict';
const fs = require('fs');
const path = require('path');
const S = require(path.join(__dirname, '..', 'src', 'solver.js'));

const rows = [];
function add(target, opt) {
  const o = Object.assign({ maxBlocks: 2, orderMode: 'fixed', allowDecimal: true, allowFactorial: true, factMax: 12, timeBudgetMs: 30000, pattern: '325' }, opt || {});
  const r = S.solve(target, o);
  const fallbackExpr = r.fallback ? r.fallback.expression : null;
  rows.push([
    target,
    o.maxBlocks,
    o.orderMode,
    o.allowDecimal ? 1 : 0,
    o.allowFactorial ? 1 : 0,
    o.factMax,
    r.ok ? 1 : 0,
    r.ok ? r.expression : '-',
    r.ok ? r.blocks : 0,
    fallbackExpr || '-',
    o.pattern
  ]);
}

// ---- 325：与之前保持一致的覆盖面 ----
for (let n = -30; n <= 60; n++) add(String(n), { maxBlocks: 2 });
for (const n of ['325', '1', '0', '-1', '3.25', '6', '32.5', '0.5', '10', '2', '1000', '123456']) add(n, { maxBlocks: 1 });
for (const n of ['1000', '2025', '12345', '99999', '123456', '1000000', '987654321', '-98765', '1234.5', '-3.14159', '-325', '-3.25', '-0.4', '105625', '325325']) {
  add(n, { maxBlocks: 2 });
  add(n, { maxBlocks: 1 });
}
for (const n of ['2', '4', '7', '62', '325', '1000', '-1', '3.25']) {
  add(n, { maxBlocks: 1, orderMode: 'any' });
  add(n, { maxBlocks: 2, orderMode: 'any' });
}
for (const n of ['62', '1000', '2025', '0.5', '3.25', '30']) {
  add(n, { maxBlocks: 2, allowFactorial: false });
  add(n, { maxBlocks: 2, allowDecimal: false });
  add(n, { maxBlocks: 2, factMax: 4 });
}

// ---- 新增数字串：799 / 325799 / 114514 ----
for (const pat of ['799', '325799', '114514']) {
  for (const n of ['1', '0', '-1', '10', '1000', '3.25', '2', '99']) {
    add(n, { maxBlocks: 1, pattern: pat });
    add(n, { maxBlocks: 2, pattern: pat });
  }
  add('1', { maxBlocks: 1, pattern: pat, orderMode: 'any' });
  add('7', { maxBlocks: 2, pattern: pat, orderMode: 'any' });
}

const header = ['target', 'maxBlocks', 'orderMode', 'allowDecimal', 'allowFactorial', 'factMax', 'ok', 'expression', 'blocks', 'fallback', 'pattern'].join('\t');
const body = rows.map(r => r.join('\t')).join('\n');
const outPath = path.join(__dirname, 'expected-js.tsv');
fs.writeFileSync(outPath, header + '\n' + body + '\n', 'utf8');

const solved = rows.filter(r => r[6] === 1).length;
const byPat = {};
for (const r of rows) byPat[r[10]] = (byPat[r[10]] || 0) + 1;
console.log(`已写入 ${path.basename(outPath)}：${rows.length} 条（有解 ${solved}，走保底 ${rows.length - solved}）`);
console.log('按数字串统计：' + Object.keys(byPat).map(k => k + '=' + byPat[k]).join('  '));
