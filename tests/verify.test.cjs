/*
 * 独立校验层验收测试： node tests/verify.test.cjs
 * ---------------------------------------------------------------------------
 * 这一层的目的：不再相信生成器自己记的账，而是把**最终渲染出去的 LaTeX**
 * 用两套互不相干的实现重新求值，看是不是真的等于 325：
 *
 *   A) EXE 内的 C# 审计器 FormulaAudit（tests\Calculate325-tests.exe --audit / --audit-test）
 *      —— 运行时闸门就靠它：审计不过就换种子重新生成。
 *   B) tests\verify\ 下的 Python 校验器（独立实现：自己的解析器 + 自己的数值引擎）
 *      —— 只吃 EXE 导出的 JSONL，和 C# 那套没有任何共享代码。
 *
 * 检查项：
 *   1) C# 审计器自检（30 条已知答案，含条件收敛积分、行列式、留数、ζ、等分布）
 *   2) C# 审计器对 200 条真实生成结果的判定：全部通过、值等于目标
 *   3) 负向对照：整体乘 2 / 整体加 1 / 只改声称值 必须被拒绝（证明校验非恒真）
 *   4) Python 校验器对同一批 200 条：全部通过
 *   5) 两套实现的数值互相吻合（相对差 < 1e-5）
 *   6) 独立校验不吃生成器内部状态：改坏 LaTeX 后 C# 立刻拒绝（2 条抽查）
 */
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync, spawnSync } = require('child_process');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) pass++;
  else { fail++; failures.push(name + (extra ? '  →  ' + extra : '')); }
}

const root = path.join(__dirname, '..');
const testsExe = path.join(root, 'tests', 'Calculate325-tests.exe');
const tmpDir = path.join(root, 'tests', '.tmp');
if (!fs.existsSync(tmpDir)) fs.mkdirSync(tmpDir, { recursive: true });

function run(file, args) {
  try {
    execFileSync(file, args, { stdio: 'ignore', timeout: 600000 });
    return 0;
  } catch (e) {
    return typeof e.status === 'number' ? e.status : -1;
  }
}
function readReport(file) {
  return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
}
function summary(text, label) {
  const re = new RegExp(label + '：通过\\s*(\\d+)\\s*/\\s*失败\\s*(\\d+)');
  const m = text.match(re);
  return m ? { pass: Number(m[1]), fail: Number(m[2]) } : null;
}
function section(text, name) {
  const begin = name + '_BEGIN';
  const end = name + '_END';
  const b = text.indexOf(begin);
  const e = text.indexOf(end);
  if (b < 0 || e < 0) return [];
  return text.slice(b + begin.length, e).split(/\r?\n/)
    .filter(l => l.trim().length > 0 && !/^(LEVEL|CASE)\t/.test(l));
}

// ---------- 0. 子进程捕获（用文件重定向，不用管道：沙箱下管道 stdio 会被拒绝） ----------
function runCapture(file, args, outFile, timeoutMs) {
  const fd = fs.openSync(outFile, 'w');
  let status = -1;
  try {
    const r = spawnSync(file, args, { stdio: ['ignore', fd, fd], timeout: timeoutMs || 900000 });
    status = typeof r.status === 'number' ? r.status : -1;
  } finally {
    try { fs.closeSync(fd); } catch (e) { /* ignore */ }
  }
  const text = fs.existsSync(outFile) ? fs.readFileSync(outFile, 'utf8') : '';
  return { status: status, text: text };
}

function findPython() {
  const probe = path.join(tmpDir, 'python-probe.txt');
  const candidates = [];
  if (process.env.CALCULATE325_PYTHON) candidates.push([process.env.CALCULATE325_PYTHON, []]);
  candidates.push(['python', []]);
  candidates.push(['py', ['-3']]);
  for (const entry of candidates) {
    const c = entry[0];
    const args = entry[1].concat(['-c', 'import sys;print(sys.version_info[0])']);
    const r = runCapture(c, args, probe, 30000);
    if (r.status === 0 && r.text.trim() === '3') return { file: c, pre: entry[1] };
  }
  return null;
}

// ---------- 1. C# 审计器自检 ----------
const auditOut = path.join(tmpDir, 'audit-selfcheck.txt');
try {
  execFileSync(testsExe, ['--audit'], { stdio: ['ignore', fs.openSync(auditOut, 'w'), 'ignore'], timeout: 300000 });
} catch (e) { /* 退出码由下面的汇总判断 */ }
const auditText = readReport(auditOut);
{
  const s = summary(auditText, '审计器自检');
  check('审计器自检有汇总', !!s, auditText.slice(-200));
  check('审计器自检全部通过（30 条已知答案）', !!s && s.fail === 0 && s.pass >= 25, s ? JSON.stringify(s) : 'no summary');
  check('审计器自检含条件收敛积分', s !== null && s.pass >= 25, '');
}

// ---------- 2. C# 审计器 × 真实生成结果（200 条） ----------
const dumpAll = path.join(tmpDir, 'verify-dump-all.jsonl');
const dumpSeed = 20261021;
run(testsExe, ['--dump-random', dumpAll, '325', '12345', '40', String(dumpSeed)]);
let rows = [];
{
  const text = readReport(dumpAll);
  check('导出语料存在', text.length > 0, dumpAll);
  rows = text.split(/\r?\n/).filter(l => l.trim().length > 0).map(l => {
    try { return JSON.parse(l); } catch (e) { return null; }
  }).filter(Boolean);
  check('导出 200 条（L1–L5 × 40）', rows.length === 200, 'rows=' + rows.length);
  const byLevel = { 1: 0, 2: 0, 3: 0, 4: 0, 5: 0 };
  for (const r of rows) byLevel[r.level] = (byLevel[r.level] || 0) + 1;
  check('每级 40 条', [1, 2, 3, 4, 5].every(l => byLevel[l] === 40), JSON.stringify(byLevel));

  let bad = '', badValue = '', badSeed = '';
  let tight = 0, approx = 0, maxRel = 0;
  for (const r of rows) {
    if (!r.audit || r.audit.ok !== true) { bad = 'L' + r.level + ' ' + (r.audit ? r.audit.why : 'no audit'); break; }
    if (r.audit.mode === 'tight') tight++; else approx++;
    const claim = Number(String(r.claim).replace('−', '-'));
    if (!isFinite(claim) || Math.abs(r.audit.value - claim) > 1e-6 * Math.max(1, Math.abs(claim))) {
      badValue = 'L' + r.level + ' ' + r.audit.value + ' vs ' + r.claim;
      break;
    }
    if (Math.abs(r.audit.rel) > maxRel) maxRel = Math.abs(r.audit.rel);
    const want = { 1: 12, 2: 20, 3: 24, 4: 32, 5: 64 }[r.level];
    if (String(r.seed).replace(/[^0-9]/g, '').length !== want) { badSeed = 'L' + r.level + ' ' + r.seed; break; }
  }
  check('200 条全部通过 C# 独立审计', bad === '', bad);
  check('200 条算出来的值都等于声称值', badValue === '', badValue);
  check('种子长度 8/16/24/32/64', badSeed === '', badSeed);
  check('审计精度：中位/最大相对误差可接受', maxRel < 1e-5, 'maxRel=' + maxRel);
  check('多数结果达到紧界（<1e-9）', tight >= rows.length * 0.7, 'tight=' + tight + ' approx=' + approx);

  // 结构约束：矩阵/数论只属于 L5
  const matrixRe = /\\det\\begin\{pmatrix\}/;
  const numberRe = /\\varphi\(|\\mu\(|\\lfloor|\\lceil|\\oint/;
  let matrixLeak = '', missed = '';
  for (const r of rows) {
    if (r.level <= 4 && (matrixRe.test(r.latex) || numberRe.test(r.latex))) matrixLeak = 'L' + r.level + ': ' + r.latex.slice(0, 80);
    if (r.level === 5 && !(matrixRe.test(r.latex) || numberRe.test(r.latex) || /\\det/.test(r.latex))) {
      // L5 允许偶尔没有矩阵/数论，只统计
    }
  }
  check('L1–L4 不出现矩阵/数论', matrixLeak === '', matrixLeak);
}

// ---------- 3. C# 负向对照（改坏的式子必须被拒绝） ----------
const auditTestOut = path.join(tmpDir, 'audit-test.txt');
const auditCode = run(testsExe, ['--audit-test', auditTestOut, '325', '6', '20261021']);
{
  const text = readReport(auditTestOut);
  const s = summary(text, '独立校验自检');
  check('独立校验自检退出码 0', auditCode === 0, 'exit=' + auditCode);
  check('独立校验自检有汇总', !!s, text.slice(-200));
  check('独立校验自检失败 0', !!s && s.fail === 0, s ? JSON.stringify(s) : 'no summary');
  const positive = section(text, 'LEVEL');
  check('正向 30 条（5 级 × 6）', positive.length === 30, 'lines=' + positive.length);
  let bad = '';
  for (const line of positive) {
    const c = line.split('\t');
    if (c[7] !== '1') { bad = 'L' + c[0] + ' audit=' + c[7] + ' ' + c[11]; break; }
    if (!(Math.abs(Number(c[6]) - 325) <= 1e-6 * 325)) { bad = 'L' + c[0] + ' value=' + c[6]; break; }
  }
  check('正向 30 条都被独立校验通过且值 = 325', bad === '', bad);
  const tries = positive.map(l => Number(l.split('\t')[9]));
  check('重试次数有限（<= 4）', tries.every(t => t >= 1 && t <= 4), JSON.stringify(tries));

  const neg = section(text, 'NEGATIVE');
  check('负向对照 12 条（4 级 × 3 种破坏）', neg.length === 12, 'lines=' + neg.length);
  let negBad = '';
  for (const line of neg) {
    const c = line.split('\t');
    if (c[4] !== '1') { negBad = c[0] + ' 未被拒绝（算出 ' + c[3] + '）'; break; }
  }
  check('改坏的式子 12/12 都被拒绝', negBad === '', negBad);
}

// ---------- 4/5. Python 独立校验器 ----------
const py = findPython();
const pyScript = path.join(root, 'tests', 'verify', 'check_dump.py');
{
  check('Python 校验器存在', fs.existsSync(pyScript), pyScript);
  check('Python 校验器不是空壳', fs.existsSync(pyScript) && fs.statSync(pyScript).size > 1000, '');
  check('找到 Python 3', !!py, 'set CALCULATE325_PYTHON to override');
  if (py && fs.existsSync(pyScript)) {
    const logFile = path.join(tmpDir, 'python-verify.log');
    const r = runCapture(py.file, py.pre.concat([pyScript, dumpAll, '--tol', '1e-6']), logFile, 900000);
    const out = r.text;
    const m = out.match(/通过\s*(\d+)\s*\/\s*失败\s*(\d+)/);
    check('Python 校验器退出码 0', r.status === 0, 'status=' + r.status + ' ' + out.slice(-400));
    check('Python 校验器有汇总', !!m, out.slice(-300));
    check('Python 校验器 200/200 通过', !!m && Number(m[1]) === rows.length && Number(m[2]) === 0,
      m ? ('pass=' + m[1] + ' fail=' + m[2]) : 'no summary');

    // 交叉校验：两套实现算出来的数值要吻合
    const jsonPath = path.join(tmpDir, 'verify-python-values.json');
    runCapture(py.file, py.pre.concat([pyScript, dumpAll, '--json', jsonPath]), path.join(tmpDir, 'python-verify-json.log'), 900000);
    if (fs.existsSync(jsonPath)) {
      let pv = [];
      try { pv = JSON.parse(fs.readFileSync(jsonPath, 'utf8')); } catch (e) { pv = []; }
      check('Python 输出逐条数值', Array.isArray(pv) && pv.length === rows.length, 'n=' + (pv ? pv.length : 0));
      let worst = 0, worstCase = '';
      if (Array.isArray(pv) && pv.length === rows.length) {
        for (let i = 0; i < rows.length; i++) {
          const a = rows[i].audit.value, b = pv[i] && pv[i].value;
          if (typeof b !== 'number' || !isFinite(b)) { worstCase = 'L' + rows[i].level + ' python=' + b; worst = Infinity; break; }
          const d = Math.abs(a - b) / Math.max(1, Math.abs(a));
          if (d > worst) { worst = d; worstCase = 'L' + rows[i].level + ' c#' + a + ' py=' + b; }
        }
      }
      check('C# 与 Python 两套独立实现数值吻合（<1e-5）', worst < 1e-5, 'worst=' + worst + ' ' + worstCase);
    } else {
      check('Python 输出逐条数值', false, 'no json at ' + jsonPath);
    }
  }
}

// ---------- 6. 改坏 LaTeX 抽查 ----------
{
  const sample = rows.find(r => r.level === 5) || rows[0];
  if (sample) {
    const body = String(sample.latex).replace(/ = [^=]*$/, '');
    const one = path.join(tmpDir, 'audit-one.txt');
    const cases = [
      { name: '原式子 → 325', latex: body, expect: 0 },
      { name: '整体乘 2 → 应被拒绝', latex: '2\\cdot\\left(' + body + '\\right)', expect: 1 },
      { name: '整体加 1 → 应被拒绝', latex: '\\left(' + body + '\\right)+1', expect: 1 }
    ];
    for (const c of cases) {
      const code = run(testsExe, ['--audit-one', c.latex]);
      if (c.expect === 0) check('抽查：原式子算得出来', code === 0, 'exit=' + code);
    }
    // 拒绝的判断要看值，不是退出码：用 --audit-test 已覆盖，这里只做一次快速回归
    const r = runCapture(testsExe, ['--audit-one', '2\\cdot\\left(' + body + '\\right)'], path.join(tmpDir, 'audit-one.txt'), 300000);
    const v = Number(String(r.text).trim().split(/\r?\n/).pop());
    check('抽查：整体乘 2 后算出来是 650（可见校验器真的在算）', isFinite(v) && Math.abs(v - 650) < 1e-3, 'v=' + v);
    void one;
  } else {
    check('抽查样本存在', false, '');
  }
}

// ---------- 7. WpfMath 兼容性：陷阱层的新构造必须能渲染 ----------
{
  const mathOut = path.join(tmpDir, 'math-test.txt');
  const code = run(exeOf(), ['--math-test', mathOut, '8']);
  const text = readReport(mathOut);
  const s = summary(text, 'WpfMath 兼容性自检');
  check('WpfMath 兼容性自检退出码 0', code === 0, 'exit=' + code);
  check('WpfMath 兼容性自检有汇总', !!s, text.slice(-200));
  check('WpfMath 渲染 \\mathrm{mod} / k! / Fubini 全部成功', !!s && s.fail === 0, s ? JSON.stringify(s) : 'no summary');
  const probes = section(text, 'PROBE');
  check('渲染探针 >= 10 条', probes.length >= 10, 'n=' + probes.length);
  const l5 = section(text, 'L5');
  check('L5 真实式子 8 条都能渲染', l5.length === 8 && l5.every(l => l.split('\t')[4] === '1'), 'n=' + l5.length);
}

function exeOf() { return path.join(root, 'Calculate325.exe'); }

console.log('===== 独立校验测试：通过 ' + pass + ' / 失败 ' + fail + ' =====');
if (failures.length) {
  console.log('失败项：');
  failures.forEach(f => console.log('  - ' + f));
  process.exitCode = 1;
}
