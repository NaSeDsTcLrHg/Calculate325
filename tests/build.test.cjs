/*
 * 构建产物校验： node tests/build.test.cjs
 * 检查 index.html 是否自洽：内联内容与源码一致、脚本可解析、元素 id 齐全、离线可用、
 * 并且用网页里内联的那份求解器真的算得出来。
 */
'use strict';
const fs = require('fs');
const path = require('path');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) pass++;
  else { fail++; failures.push(name + (extra ? '  →  ' + extra : '')); }
}

const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const solverSrc = fs.readFileSync(path.join(root, 'src', 'solver.js'), 'utf8').trim();
const appSrc = fs.readFileSync(path.join(root, 'src', 'app.js'), 'utf8').trim();
const template = fs.readFileSync(path.join(root, 'src', 'index.template.html'), 'utf8');

check('index.html 存在且非空', html.length > 10000, 'len=' + html.length);
check('模板占位符已被替换', !html.includes('__SOLVER__') && !html.includes('__APP__'));

const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1].trim());
check('恰好内联两段脚本', scripts.length === 2, 'count=' + scripts.length);
check('内联求解器与 src/solver.js 完全一致', scripts[0] === solverSrc);
check('内联界面与 src/app.js 完全一致', scripts[1] === appSrc);

// 语法可解析
for (const [name, code] of [['solver', scripts[0]], ['app', scripts[1]]]) {
  let ok = true, msg = '';
  try { new Function(code); } catch (e) { ok = false; msg = e.message; }
  check(`${name} 脚本语法可解析`, ok, msg);
}

// 用内联的那份求解器做冒烟测试（UMD：Node 分支会写到 module.exports）
const sandbox = {};
const mod = { exports: {} };
new Function('module', 'window', scripts[0])(mod, sandbox);
const S = mod.exports.Calculate325 || sandbox.Calculate325 || mod.exports;
check('内联求解器导出 Calculate325', !!(S && S.solve && S.verify));

const cases = [['325', 1], ['1', 1], ['1000', 2], ['2025', 2], ['3.25', 1], ['-7', 1], ['62', 2]];
for (const [n, maxBlocks] of cases) {
  const r = S.solve(n, { maxBlocks });
  const v = r.ok ? S.verify(r.expression, r.target, { orderMode: r.orderMode }) : null;
  check(`内联求解器解出 ${n}`, r.ok && v.digitsOk && v.valueOk === true,
    r.ok ? r.expression : JSON.stringify(r.notes));
  check(`内联求解器 ${n} 未超时`, !r.aborted, 'elapsed=' + r.elapsedMs);
}
{
  const big = S.solve('123456', { maxBlocks: 1, timeBudgetMs: 3000 });
  check('内联求解器大数保底可用', !big.ok && !!big.fallback &&
    S.verify(big.fallback.expression, big.target, { orderMode: big.orderMode }).valueOk === true,
    big.fallback ? big.fallback.expression : 'no fallback');
}

// 界面里用到的元素 id 必须存在于模板
const ids = [...new Set([...appSrc.matchAll(/\$\('#([A-Za-z0-9_-]+)'\)/g)].map(m => m[1]))];
check('界面至少引用若干元素', ids.length >= 5, 'ids=' + ids.join(','));
for (const id of ids) {
  check(`模板包含元素 #${id}`, new RegExp('id="' + id + '"').test(html));
}

// 离线可用：不得引用外部资源
check('没有外部 http(s) 资源引用', !/(src|href)\s*=\s*["']https?:/i.test(html));
check('声明了 UTF-8 编码', /<meta charset="utf-8">/i.test(html));
check('模板仍保留规则说明', template.includes('规则与算法说明'));

// 不得出现会提前结束脚本块的内容
check('内联脚本不含 </script', !/<\/script/i.test(scripts[0]) && !/<\/script/i.test(scripts[1]));

console.log('index.html 大小：' + html.length + ' 字节；内联脚本 ' + scripts.map(s => s.length).join(' + '));
console.log('===== 构建校验：通过 ' + pass + ' / 失败 ' + fail + ' =====');
if (failures.length) { console.log('失败项：'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
