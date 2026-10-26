'use strict';
const path = require('path');
const S = require(path.join(__dirname, '..', 'src', 'solver.js'));

const targets = ['1000', '2025', '123456', '99999', '62', '3.14159', '777', '325325', '105625'];
console.log('=== 单次求解（cold cache，默认参数） ===');
for (const maxBlocks of [1, 2, 3]) {
  for (const t of targets) {
    const r = S.solve(t, { maxBlocks, timeBudgetMs: 20000 });
    console.log(`  k≤${maxBlocks} n=${t}: ${r.ok ? r.blocks + '块/' + r.tokens + '数 ' + r.expression : '(无解)'}  ${r.elapsedMs}ms` +
      ' 规模=' + r.stats.map(s => `k${s.k}:${s.values}v/${s.tokensBuilt}t/${s.ms}ms`).join(' ') +
      ' 条目=' + r.entries + (r.pruned ? ' [剪枝]' : '') + (r.aborted ? ' [超时]' : '') + (r.limited ? ' [超限]' : ''));
  }
}

console.log('\n=== 覆盖率（-40..80，k≤2，会话复用） ===');
{
  const session = S.createSession({ maxBlocks: 2 });
  const t0 = Date.now();
  const unsolved = [];
  for (let n = -40; n <= 80; n++) if (!session.solve(String(n)).ok) unsolved.push(n);
  console.log(`  直接解出 ${121 - unsolved.length}/121，用时 ${Date.now() - t0}ms，缓存子串 ${session.cacheSize()}`);
  console.log(`  未解出：${unsolved.join(', ') || '无'}`);
}

console.log('\n=== 覆盖率（-40..80，k≤3，会话复用） ===');
{
  const session = S.createSession({ maxBlocks: 3, timeBudgetMs: 600000 });
  const t0 = Date.now();
  const unsolved = [];
  for (let n = -40; n <= 80; n++) if (!session.solve(String(n)).ok) unsolved.push(n);
  console.log(`  直接解出 ${121 - unsolved.length}/121，用时 ${Date.now() - t0}ms，缓存子串 ${session.cacheSize()}`);
  console.log(`  未解出：${unsolved.join(', ') || '无'}`);
}

console.log('\n=== 容量上限对覆盖率与耗时的影响（-40..80，k≤2） ===');
for (const cap of [[0, 200000, 30000, 30000, 12000, 6000, 3000], [0, 200000, 10000, 10000, 4000, 2000, 1000], [0, 200000, 4000, 4000, 1500, 800, 400]]) {
  const session = S.createSession({ maxBlocks: 2, classCaps: cap, classCapTail: 400 });
  const t0 = Date.now();
  let solved = 0;
  for (let n = -40; n <= 80; n++) if (session.solve(String(n)).ok) solved++;
  console.log(`  caps[2..6]=${cap.slice(2).join('/')}: 解出 ${solved}/121，用时 ${Date.now() - t0}ms，缓存 ${session.cacheSize()}`);
}

console.log('\n=== 大目标（k≤3，会话复用，保底 vs 真解） ===');
{
  const session = S.createSession({ maxBlocks: 3, timeBudgetMs: 30000 });
  for (const t of ['1000', '2025', '12345', '99999', '123456', '1000000']) {
    const r = session.solve(t);
    console.log(`  ${t}: ${r.ok ? r.blocks + '块/' + r.tokens + '数 ' + r.expression : '(无解，需保底)'}  ${r.elapsedMs}ms` +
      (r.ok ? '' : ' 保底长度=' + (r.fallback ? r.fallback.expression.length : 'n/a')));
  }
}
