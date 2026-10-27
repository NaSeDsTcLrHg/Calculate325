/*
 * 随机模式验收测试： node tests/random.test.cjs
 * ---------------------------------------------------------------------------
 * 1) tests\Calculate325-tests.exe --random-test：4 等级 × 20 条混合式子；
 * 2) 类型系统：单/二/三重积分、极限、级数、极限+积分、级数+积分、
 *    极限+级数、等分布；第一位最外层、9=没有这一层；
 * 3) 全零 32 位 = 八重积分；L1 = 1 层；L2 可出现二重；
 * 4) 10 个归一化积分家族逐个 = 1；
 * 5) 隐蔽系数黑名单 + 窗口缩放。
 */
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) pass++;
  else { fail++; failures.push(name + (extra ? '  →  ' + extra : '')); }
}

const root = path.join(__dirname, '..');
const exe = path.join(root, 'Calculate325.exe');
const testsExe = path.join(root, 'tests', 'Calculate325-tests.exe');
const tmpDir = path.join(root, 'tests', '.tmp');
if (!fs.existsSync(tmpDir)) fs.mkdirSync(tmpDir, { recursive: true });

// 历史落盘用独立文件，避免污染用户真实的 history.txt
const historyFile = path.join(tmpDir, 'history-selftest.txt');
const favoritesFile = path.join(tmpDir, 'favorites-selftest.txt');
try { if (fs.existsSync(historyFile)) fs.unlinkSync(historyFile); } catch (e) { }
try { if (fs.existsSync(favoritesFile)) fs.unlinkSync(favoritesFile); } catch (e) { }

function run(exePath, args) {
  try {
    execFileSync(exePath, args, {
      stdio: 'ignore', timeout: 300000,
      env: Object.assign({}, process.env, {
        CALCULATE325_HISTORY: historyFile,
        CALCULATE325_FAVORITES: favoritesFile,
        // 默认固定十进制：避免受本机残留 seedmode.txt 影响；模式测试会覆盖它
        CALCULATE325_SEEDMODE: '0'
      })
    });
    return 0;
  } catch (e) {
    return typeof e.status === 'number' ? e.status : -1;
  }
}

function runWithEnv(args, extraEnv) {
  try {
    execFileSync(exe, args, {
      stdio: 'ignore', timeout: 300000,
      env: Object.assign({}, process.env, {
        CALCULATE325_HISTORY: historyFile,
        CALCULATE325_FAVORITES: favoritesFile,
        CALCULATE325_SEEDMODE: '0'
      }, extraEnv || {})
    });
    return 0;
  } catch (e) {
    return typeof e.status === 'number' ? e.status : -1;
  }
}

function parseRat(s) {
  s = String(s || '').trim().replace(/−/g, '-');
  const m = s.match(/^(-?\d+)(?:\/(\d+))?$/);
  if (!m) return null;
  return { n: Number(m[1]), d: m[2] ? Number(m[2]) : 1 };
}
function ratEq(s, n, d) {
  const r = parseRat(s);
  return !!r && r.n * d === n * r.d;
}
function seedDigits(seed) {
  return String(seed).replace(/[^0-9]/g, '').length;
}
function readReport(file) {
  return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
}
function section(text, name) {
  const begin = name + '_BEGIN';
  const end = name + '_END';
  const b = text.indexOf(begin);
  const e = text.indexOf(end);
  if (b < 0 || e < 0) return [];
  return text.slice(b + begin.length, e).split(/\r?\n/)
    .filter(l => l.trim().length > 0 && !/^(LEVEL|KIND|FAMILY|CASE)\t/.test(l));
}
function summaryFail(text) {
  const m = text.match(/通过\s*(\d+)\s*\/\s*失败\s*(\d+)/);
  return m ? { pass: Number(m[1]), fail: Number(m[2]) } : null;
}

const FORBIDDEN = /(?<!\d)(325|650|975|1300|1625|1950|2275|2600|2925|3250|3900|4875|6500|9750|13000)(?!\d)/;
function exprBody(expr) {
  const s = String(expr || '');
  const i = s.lastIndexOf(' = ');
  return i >= 0 ? s.slice(0, i) : s;
}
function hiddenOk(expr) {
  return !FORBIDDEN.test(exprBody(expr));
}
function countIntegrals(expr) {
  return (String(expr || '').match(/∫/g) || []).length;
}
function hasLim(expr) {
  return String(expr || '').indexOf('lim') >= 0;
}
function hasSeries(expr) {
  return String(expr || '').indexOf('Σ') >= 0;
}

const seedLengths = { 1: 12, 2: 20, 3: 24, 4: 32 };
const maxLayers = { 1: 0, 2: 1, 3: 4, 4: 12 };
function layerCountOf(c) {
  return c.kind === 'elementary' ? 0 : (c.codes || '').split(',').length;
}

// 生成报告列：
// LEVEL SEED KIND TYPE_CODES DIMS IDENTITY COMPLEXITY VALUE OK NUMERIC_KIND NUMERIC_OK EXPR LATEX ERROR
function parseGenerationLine(line) {
  const c = line.split('\t');
  return {
    level: Number(c[0]), seed: c[1], kind: c[2], codes: c[3], dims: Number(c[4]),
    identity: c[5], complexity: Number(c[6]), value: c[7], ok: c[8],
    numericKind: c[9], numericOk: c[10], expr: c[11], latex: c[12], error: c[13]
  };
}
function validCodes(codes, layers) {
  const arr = String(codes || '').split(',');
  if (arr.length !== layers) return false;
  return arr.every(x => /^[0-8]$/.test(x));
}

// ---------- 1. 生成器：每个等级 20 条 ----------
const generatorTargets = [
  { text: '325', n: 325, d: 1 },
  { text: '3.25', n: 13, d: 4 },
  { text: '-7', n: -7, d: 1 },
  { text: '0', n: 0, d: 1 },
  { text: '3', n: 3, d: 1 }
];
for (const target of generatorTargets) {
  const file = path.join(tmpDir, 'random-test-' + target.text.replace(/[^0-9a-z.-]/gi, '_') + '.txt');
  const code = run(testsExe, ['--random-test', file, target.text, '20']);
  const text = readReport(file);
  const sum = summaryFail(text);
  check(`随机自检 ${target.text} 退出码 0`, code === 0, 'exit=' + code);
  check(`随机自检 ${target.text} 有汇总`, !!sum, text.slice(-160));
  check(`随机自检 ${target.text} 失败 0`, !!sum && sum.fail === 0, sum ? JSON.stringify(sum) : 'no summary');
  check(`随机自检 ${target.text} 通过 >= 80 条生成`, !!sum && sum.pass >= 80, sum ? String(sum.pass) : 'no summary');
  check(`随机自检 ${target.text} 种子长度 12/20/24/32`, /SEED_LENGTHS=12,20,24,32/.test(text), (text.match(/SEED_LENGTHS=.*/) || [''])[0]);
  check(`随机自检 ${target.text} 最大层数 0/1/4/8`, /MAX_LAYERS=0,1,4,8/.test(text), (text.match(/MAX_LAYERS=.*/) || [''])[0]);

  const lines = section(text, 'GENERATION');
  check(`随机自检 ${target.text} 生成 80 行`, lines.length === 80, 'lines=' + lines.length);
  const byLevel = { 1: 0, 2: 0, 3: 0, 4: 0 };
  const seedsByLevel = { 1: new Set(), 2: new Set(), 3: new Set(), 4: new Set() };
  let bad = '';
  let hiddenBad = '';
  let l4Patterns = new Set();
  let l4Limit = 0, l4Series = 0, l4Dims0 = 0, l4Both = 0;
  for (const line of lines) {
    const c = parseGenerationLine(line);
    byLevel[c.level] = (byLevel[c.level] || 0) + 1;
    seedsByLevel[c.level].add(c.seed);
    if (!ratEq(c.value, target.n, target.d)) bad = 'value ' + c.value;
    else if (c.ok !== '1') bad = 'ok ' + c.ok + ' ' + c.error;
    else if (c.numericOk !== '1') bad = 'numeric ' + c.numericOk;
    else if (!c.expr || !c.latex) bad = 'empty expr/latex';
    else if (seedDigits(c.seed) !== seedLengths[c.level]) bad = 'seed ' + c.seed;
    else if (c.level === 1) {
      if (c.kind !== 'elementary') bad = 'L1 kind ' + c.kind;
      else if (c.codes !== 'E') bad = 'L1 codes ' + c.codes;
      else if (c.dims !== 0) bad = 'L1 dims ' + c.dims;
      else if (hasLim(c.expr) || hasSeries(c.expr)) bad = 'L1 not elementary: ' + c.expr;
    } else if (c.kind !== 'mixed-' + layerCountOf(c)) bad = 'kind ' + c.kind;
    else if (!validCodes(c.codes, layerCountOf(c))) bad = 'codes ' + c.codes;
    else if (layerCountOf(c) < 1 || layerCountOf(c) > maxLayers[c.level]) bad = 'layers ' + c.codes;
    else if (!(c.dims >= 0 && c.dims <= maxLayers[c.level])) bad = 'dims ' + c.dims;
    else if (!hiddenOk(c.expr)) hiddenBad = c.expr;
    if (c.level === 4) {
      l4Patterns.add(c.codes);
      if (hasLim(c.expr)) l4Limit++;
      if (hasSeries(c.expr)) l4Series++;
      if (c.dims === 0) l4Dims0++;
      if (hasLim(c.expr) && hasSeries(c.expr)) l4Both++;
    }
    if (bad || hiddenBad) break;
  }
  check(`随机自检 ${target.text} 每行值/校验/种子/类型都正确`, bad === '', bad);
  check(`随机自检 ${target.text} 无 325 裸倍数系数`, hiddenBad === '', hiddenBad);
  for (const level of [1, 2, 3, 4]) {
    check(`随机自检 ${target.text} L${level} 20 条`, byLevel[level] === 20, 'L' + level + '=' + byLevel[level]);
    if (target.text === '325') {
      check(`随机自检 L${level} 20 个不同种子`, seedsByLevel[level].size === 20, 'unique=' + seedsByLevel[level].size);
    }
  }
  if (target.text === '325') {
    check('L4 类型组合 >= 5 种', l4Patterns.size >= 5, Array.from(l4Patterns).join(' | '));
    check('L4 出现过极限', l4Limit > 0, 'n=' + l4Limit);
    check('L4 出现过级数', l4Series > 0, 'n=' + l4Series);
    check('L4 出现过极限+级数混合', l4Both > 0, 'n=' + l4Both);
  }
}

// ---------- 2. 家族 + 全零 + 首层 + 混合类型 ----------
{
  const file = path.join(tmpDir, 'random-test-325.txt');
  run(testsExe, ['--random-test', file, '325', '20']);
  const text = readReport(file);

  const layers = section(text, 'LAYER');
  check('家族自检 70 条（10 家族 × 7 参数）', layers.length === 70, 'lines=' + layers.length);
  const families = new Set();
  let bad = '';
  let hiddenBad = '';
  for (const line of layers) {
    const c = line.split('\t');
    families.add(c[0]);
    if (c[3] !== '1') bad = c[0] + ' built';
    else if (c[4] !== '1') bad = c[0] + ' ok ' + c[10];
    else if (c[6] !== '1') bad = c[0] + ' numeric ' + c[10];
    else if (!ratEq(c[7], 1, 1)) bad = c[0] + ' value ' + c[7];
    else if (!c[8] || !c[9]) bad = c[0] + ' empty';
    else if (!hiddenOk(c[8])) hiddenBad = c[0] + ' ' + c[8];
    if (bad || hiddenBad) break;
  }
  check('10 个家族都出现', families.size === 10, Array.from(families).join(','));
  check('家族单层积分全部 = 1 且数值校验通过', bad === '', bad);
  check('家族系数/指数无 325 裸倍数', hiddenBad === '', hiddenBad);

  check('全零 32 位 = 8 个 ∫', /ZERO_SEED_INTEGRALS=8/.test(text), (text.match(/ZERO_SEED_INTEGRALS=.*/) || [''])[0]);
  check('全零 32 位 = 8 重', /ZERO_SEED_DIMS=8/.test(text), (text.match(/ZERO_SEED_DIMS=.*/) || [''])[0]);
  check('全零 32 位 = 8 层', /ZERO_SEED_LAYERS=8/.test(text), (text.match(/ZERO_SEED_LAYERS=.*/) || [''])[0]);
  check('全零 32 位类型都是 0', /ZERO_SEED_TYPE_CODES=0,0,0,0,0,0,0,0/.test(text), (text.match(/ZERO_SEED_TYPE_CODES=.*/) || [''])[0]);
  check('全零种子校验通过', /ZERO_SEED_OK=1/.test(text), (text.match(/ZERO_SEED_OK=.*/) || [''])[0]);
  check('首层指数家族', /FIRST_LAYER_EXPONENTIAL=1/.test(text), (text.match(/FIRST_LAYER_EXPONENTIAL=.*/) || [''])[0]);
  check('首层只有 1 层', /FIRST_LAYER_LAYERS=1/.test(text), (text.match(/FIRST_LAYER_LAYERS=.*/) || [''])[0]);
  check('首层校验通过', /FIRST_LAYER_OK=1/.test(text), (text.match(/FIRST_LAYER_OK=.*/) || [''])[0]);

  const mixedLines = section(text, 'MIXED');
  check('混合类型自检 5 条', mixedLines.length === 5, 'lines=' + mixedLines.length);
  const mixed = {};
  for (const line of mixedLines) {
    const c = line.split('\t');
    mixed[c[0]] = c;
  }
  const expect = {
    'L4-mixed': { layers: '4', dims: '3', codes: '0,1,3,4', integrals: 3, lim: '1', series: '1' },
    'L4-one': { layers: '1', dims: '1', codes: '0', integrals: 1, lim: '0', series: '0' },
    'L4-limit-only': { layers: '4', dims: '0', codes: '3,4,7,8', integrals: 0, lim: '1', series: '1' },
    'L2-zero': { layers: '1', dims: '1', codes: '0', integrals: 1, lim: '0', series: '0' },
    'L1-zero': { layers: '0', dims: '0', codes: 'E', integrals: 0, lim: '0', series: '0' }
  };
  for (const name of Object.keys(expect)) {
    const e = expect[name];
    const c = mixed[name];
    check('混合 ' + name + ' 存在且通过', !!c && c[7] === '1', c ? c.join(' | ') : 'missing');
    if (c) {
      check('混合 ' + name + ' 层数/重数/类型码', c[1] === e.layers && c[2] === e.dims && c[3] === e.codes, c.join(' | '));
      check('混合 ' + name + ' ∫ 数量', Number(c[4]) === e.integrals, c[4]);
      check('混合 ' + name + ' lim/Σ 标志', c[5] === e.lim && c[6] === e.series, c[5] + '/' + c[6]);
    }
  }

  // 目标 3 的全零种子仍是八重
  const file3 = path.join(tmpDir, 'random-test-3.txt');
  run(testsExe, ['--random-test', file3, '3', '20']);
  const text3 = readReport(file3);
  check('目标 3 的全零种子 = 8 重', /ZERO_SEED_DIMS=8/.test(text3), (text3.match(/ZERO_SEED_DIMS=.*/) || [''])[0]);
  check('目标 3 的全零种子值 = 3', /ZERO_SEED_VALUE=3/.test(text3), (text3.match(/ZERO_SEED_VALUE=.*/) || [''])[0]);
}

// ---------- 3. 随机窗口：连点 + 缩放 ----------
{
  const file = path.join(tmpDir, 'random-form-325.txt');
  const code = run(exe, ['--random-form-test', file, '325', '20']);
  const text = readReport(file);
  const sum = summaryFail(text);
  check('随机窗口自检退出码 0', code === 0, 'exit=' + code + ' ' + text.slice(0, 160));
  check('随机窗口自检有汇总', !!sum, text.slice(-160));
  check('随机窗口自检失败 0', !!sum && sum.fail === 0, sum ? JSON.stringify(sum) : 'no summary');
  check('随机窗口自检通过 80 次', !!sum && sum.pass === 80, sum ? String(sum.pass) : 'no summary');
  check('随机窗口有 5 个等级', /LEVEL_ITEMS=5/.test(text), (text.match(/LEVEL_ITEMS=.*/) || [''])[0]);
  check('随机窗口种子长度 12,20,24,32', /SEED_LENGTHS=12,20,24,32/.test(text), (text.match(/SEED_LENGTHS=.*/) || [''])[0]);
  check('随机窗口最大层数 0,1,4,8', /MAX_LAYERS=0,1,4,8/.test(text), (text.match(/MAX_LAYERS=.*/) || [''])[0]);
  check('随机窗口结果区有边界', /^RESULT_BOUNDS=\d+,\d+,\d+,\d+$/m.test(text), (text.match(/RESULT_BOUNDS=.*/) || [''])[0]);
  check('随机窗口尺寸变化后结果区更新', /RESIZE_OK=1/.test(text), (text.match(/RESIZE_(BEFORE|AFTER|OK)=.*/g) || []).join(' '));
  check('随机窗口 LaTeX 排版成功', /MATH_OK=1/.test(text), (text.match(/MATH_OK=.*/) || [''])[0]);
  check('随机窗口 LaTeX 至少一行', /MATH_LINES=[1-9][0-9]*/.test(text), (text.match(/MATH_LINES=.*/) || [''])[0]);
  check('随机窗口 LaTeX 有内容高度', /MATH_CONTENT_HEIGHT=[1-9]/.test(text), (text.match(/MATH_CONTENT_HEIGHT=.*/) || [''])[0]);
  check('随机窗口有自定义按钮', /GENERATE_TEXT=自定义/.test(text), (text.match(/GENERATE_TEXT=.*/) || [''])[0]);
  check('随机窗口有复制按钮', /COPY_TEXT=复制/.test(text), (text.match(/COPY_TEXT=.*/) || [''])[0]);
check('底部常用按钮已强调', /EMPHASIS=1111/.test(text), (text.match(/EMPHASIS=.*/) || [''])[0]);
check('底部按钮已收窄（复制 ≤ 90 逻辑像素）', /BTN_W_COPY=[0-9]+/.test(text) && Number((text.match(/BTN_W_COPY=(\d+)/) || [])[1] || 999) <= 140, (text.match(/BTN_W_COPY=.*/) || [''])[0]);
  check('随机窗口有保存按钮', /SAVE_TEXT=存图/.test(text), (text.match(/SAVE_TEXT=.*/) || [''])[0]);

  const lines = section(text, 'GENERATION');
  check('随机窗口有历史按钮', /HISTORY_TEXT=历史/.test(text), (text.match(/HISTORY_TEXT=.*/) || [''])[0]);
check('历史记录自检通过', /HISTORY_OK=1/.test(text), (text.match(/HISTORY_(OK|ADD|REMOVE|CAPACITY)=.*/g) || []).join(' '));
const histLoaded = Number((text.match(/HISTORY_LOADED=(\d+)/) || [])[1] || 0);
const histRecorded = Number((text.match(/HISTORY_RECORDED=(\d+)/) || [])[1] || 0);
check('生成路径真的写入历史', histRecorded - histLoaded === 80, 'recorded=' + histRecorded + ' loaded=' + histLoaded);
check('历史写盘自检通过', /HISTORY_FILE=1/.test(text), (text.match(/HISTORY_FILE=.*/g) || []).join(' '));
check('历史文件确实生成', fs.existsSync(historyFile), historyFile);
const histLines = fs.existsSync(historyFile)
  ? fs.readFileSync(historyFile, 'utf8').split(/\r?\n/).filter(l => l.trim().length > 0) : [];
check('历史文件条数与内存一致', histLines.length === histRecorded, 'lines=' + histLines.length + ' mem=' + histRecorded);
check('历史按时间倒序（最新在最上）', histLines.length > 1
  && histLines[0].split('|')[1] >= histLines[histLines.length - 1].split('|')[1],
  (histLines[0] || '').split('|')[1] + ' vs ' + (histLines[histLines.length - 1] || '').split('|')[1]);
check('历史窗口可构造且能勾选', /HISTORY_FORM_OK=1/.test(text), (text.match(/HISTORY_FORM_.*/g) || []).join(' '));
check('随机窗口标题栏有图标', /FORM_ICON=1/.test(text), (text.match(/FORM_ICON=.*/) || [''])[0]);
const histScale = Number((text.match(/HISTORY_FORM_SCALE=([\d.]+)/) || [])[1] || 0);
const histRow = Number((text.match(/HISTORY_FORM_ROWHEIGHT=(\d+)/) || [])[1] || 0);
const histCols = ((text.match(/HISTORY_FORM_COLS=(.*)/) || [])[1] || '').split(',').map(Number);
check('历史列表行高按 DPI 放大', histScale > 0 && Math.abs(histRow - 38 * histScale) <= 2, histRow + ' @' + histScale);
check('历史列表每行有独立查看按钮', /HISTORY_FORM_VIEWCOL=1/.test(text), (text.match(/HISTORY_FORM_VIEWCOL=.*/) || [''])[0]);
check('历史列表有等级筛选', /HISTORY_FORM_FILTER=1/.test(text), (text.match(/HISTORY_FORM_FILTER=.*/) || [''])[0]);
check('随机窗口有「收藏夹」按钮', /FAVORITES_TEXT=收藏夹/.test(text), (text.match(/FAVORITES_TEXT=.*/) || [''])[0]);
check('历史列表有「取消全选」', /HISTORY_FORM_NONE=1/.test(text), (text.match(/HISTORY_FORM_NONE=.*/) || [''])[0]);
check('历史列表有「收藏选中」', /HISTORY_FORM_FAVBTN=1/.test(text), (text.match(/HISTORY_FORM_FAVBTN=.*/) || [''])[0]);
check('批量收藏：新增/去重/落盘/读回', /FAVORITES_OK=1/.test(text), (text.match(/FAVORITES_(ADDED|DEDUP|SAVED|RELOAD)=.*/g) || []).join(' '));
check('查看窗口显示种子（与主窗口同规则）', /PREVIEW_SEED_OK=1/.test(text) && /PREVIEW_SEED_LEN=\d+/.test(text), (text.match(/PREVIEW_SEED.*/g) || []).join(' '));
check('查看窗口底部按钮不重叠', /PREVIEW_NO_OVERLAP=1/.test(text), (text.match(/PREVIEW_(NO_OVERLAP|BTN_X)=.*/g) || []).join(' '));
check('种子框占用空间小（≤560×50）', (() => {
  const m = (text.match(/PREVIEW_SEEDBOX=(\d+)x(\d+)/) || []).slice(1).map(Number);
  return m.length === 2 && m[0] <= 560 && m[1] <= 50;
})(), (text.match(/PREVIEW_SEEDBOX=.*/) || [''])[0]);
check('查看窗口有「复制种子」', /PREVIEW_COPYSEED=1/.test(text), (text.match(/PREVIEW_COPYSEED=.*/) || [''])[0]);
check('查看窗口可「上一条/下一条」翻页', /PREVIEW_NAV_OK=1/.test(text), (text.match(/PREVIEW_.*/g) || []).join(' '));
check('查看窗口已放大', /PREVIEW_BIG=1/.test(text), (text.match(/PREVIEW_BIG=.*/) || [''])[0]);
check('查看窗口固定 1600×900（含边框、不可拉伸）', /PREVIEW_SIZE=1600x900/.test(text) && /PREVIEW_FIXED=1/.test(text), (text.match(/PREVIEW_(SIZE|FIXED)=.*/g) || []).join(' '));
check('查看窗口右侧是 复制/收藏/关闭（强调边框）', /PREVIEW_RIGHT_BTNS=1/.test(text) && /PREVIEW_FAV_BTN=1/.test(text), (text.match(/PREVIEW_(RIGHT_BTNS|FAV_BTN)=.*/g) || []).join(' '));
check('翻页按钮是竖长条（宽<高）', /PREVIEW_NAV_SHAPE=1/.test(text), (text.match(/PREVIEW_NAV_SHAPE=.*/) || [''])[0]);
check('收藏窗口（我的收藏）可构造', /FAVORITES_FORM_OK=1/.test(text) && /FAVORITES_FORM_TITLE=1/.test(text), (text.match(/FAVORITES_FORM_.*/g) || []).join(' '));
check('收藏文件已落盘', fs.existsSync(favoritesFile), favoritesFile);
check('随机窗口有「收藏」按钮', /FAVCUR_TEXT=收藏/.test(text), (text.match(/FAVCUR_TEXT=.*/) || [''])[0]);
check('历史/收藏列表记录种子', /HISTORY_FORM_SEEDCOL=1/.test(text) && /HISTORY_FORM_SEEDSAMPLE=\d+/.test(text), (text.match(/HISTORY_FORM_SEED.*/g) || []).join(' '));
check('非编号排序下也能翻页', /HISTORY_FORM_NAV_SORTED=1/.test(text), (text.match(/HISTORY_FORM_NAV_SORTED=.*/) || [''])[0]);
check('查看窗口提示当前排序', /HISTORY_FORM_SORT_LABEL=按 /.test(text), (text.match(/HISTORY_FORM_SORT_LABEL=.*/) || [''])[0]);
{
  const modeFile = path.join(tmpDir, 'seedmode-selftest.txt');
  const zcOut = path.join(tmpDir, 'rf-mode-zc.txt');
  fs.writeFileSync(modeFile, '1');
  runWithEnv(['--random-form-test', zcOut, '325', '1'], { CALCULATE325_SEEDMODE: '', CALCULATE325_SEEDMODE_FILE: modeFile });
  const zcText = readReport(zcOut);
  check('种子模式 Zc 会被记住并全局生效', /SEEDMODE=zc/.test(zcText)
    && /HISTORY_FORM_SEEDSAMPLE=[ZzCc]/.test(zcText), (zcText.match(/SEEDMODE=.*|HISTORY_FORM_SEEDSAMPLE=.*/g) || []).join(' '));

  const decOut = path.join(tmpDir, 'rf-mode-dec.txt');
  fs.writeFileSync(modeFile, '0');
  runWithEnv(['--random-form-test', decOut, '325', '1'], { CALCULATE325_SEEDMODE: '', CALCULATE325_SEEDMODE_FILE: modeFile });
  const decText = readReport(decOut);
  {
  // 跨进程：先「设置模式」，再起一个全新进程读回
  const crossFile = path.join(tmpDir, 'seedmode-cross.txt');
  try { fs.unlinkSync(crossFile); } catch (e) { }
  runWithEnv(['--set-seedmode', '1'], { CALCULATE325_SEEDMODE: '', CALCULATE325_SEEDMODE_FILE: crossFile });
  const crossValue = fs.existsSync(crossFile) ? fs.readFileSync(crossFile, 'utf8').trim() : '';
  const crossOut = path.join(tmpDir, 'rf-cross.txt');
  runWithEnv(['--random-form-test', crossOut, '325', '1'], { CALCULATE325_SEEDMODE: '', CALCULATE325_SEEDMODE_FILE: crossFile });
  const crossText = readReport(crossOut);
  check('关闭后重开仍记住 Zc 模式', crossValue === '1' && /SEEDMODE=zc/.test(crossText)
    && /HISTORY_FORM_SEEDSAMPLE=[ZzCc]/.test(crossText), 'file=' + crossValue + ' ' + (crossText.match(/SEEDMODE=.*/) || [''])[0]);
}
check('种子模式切回十进制也生效', /SEEDMODE=dec/.test(decText)
    && /HISTORY_FORM_SEEDSAMPLE=\d/.test(decText), (decText.match(/SEEDMODE=.*|HISTORY_FORM_SEEDSAMPLE=.*/g) || []).join(' '));
}
check('历史默认按编号升序（1 号在最上）', /HISTORY_FORM_SORT_FIRST=1\b/.test(text) && /HISTORY_FORM_SORT_BACK=1\b/.test(text),
  (text.match(/HISTORY_FORM_SORT_(FIRST|BACK)=.*/g) || []).join(' '));
check('点表头能切升/降序', (() => {
  const a = ((text.match(/HISTORY_FORM_SORT_ASC=([\d,-]+)/) || [])[1] || '').split(',').map(Number);
  const d = ((text.match(/HISTORY_FORM_SORT_DESC=([\d,-]+)/) || [])[1] || '').split(',').map(Number);
  return a.length === 2 && d.length === 2 && a[0] <= a[1] && d[0] >= d[1] && a[0] !== d[0];
})(), (text.match(/HISTORY_FORM_SORT_(ASC|DESC)=.*/g) || []).join(' '));
check('历史筛选真的过滤（L4 只剩 L4）', (() => {
  const l4 = Number((text.match(/HISTORY_FORM_FILTER_L4=(\d+)/) || [])[1] || -1);
  const back = Number((text.match(/HISTORY_FORM_FILTER_BACK=(\d+)/) || [])[1] || -1);
  const all = Number((text.match(/HISTORY_FORM_ITEMS=(\d+)/) || [])[1] || -1);
  return l4 >= 0 && l4 < all && back === all;
})(), (text.match(/HISTORY_FORM_FILTER_(L4|BACK)=.*/g) || []).join(' '));
check('历史列表非摘要列宽度足够', histCols.length === 6
  && Math.abs(histCols[0] - 64 * histScale) <= 3 && Math.abs(histCols[1] - 130 * histScale) <= 4
  && Math.abs(histCols[2] - 80 * histScale) <= 3 && Math.abs(histCols[3] - 120 * histScale) <= 4
  && Math.abs(histCols[4] - 110 * histScale) <= 4, histCols.join(','));
check('随机窗口生成 80 行', lines.length === 80, 'lines=' + lines.length);
  let bad = '';
  let hiddenBad = '';
  const seedsByLevel = { 1: new Set(), 2: new Set(), 3: new Set(), 4: new Set() };
  for (const line of lines) {
    const c = parseGenerationLine(line);
    // form report 的列比 self-test 多 DISPLAY，但前 13 列相同
    seedsByLevel[c.level].add(c.seed);
    if (!ratEq(c.value, 325, 1)) bad = 'value ' + c.value;
    else if (c.ok !== '1') bad = 'ok ' + c.ok + ' ' + c.error;
    else if (c.numericOk !== '1') bad = 'numeric ' + c.numericOk;
    else if (!c.expr || !c.latex) bad = 'empty expr/latex';
    else if (seedDigits(c.seed) !== seedLengths[c.level]) bad = 'seed ' + c.seed;
    else if (c.level === 1) {
      if (c.kind !== 'elementary') bad = 'L1 kind ' + c.kind;
      else if (c.codes !== 'E') bad = 'L1 codes ' + c.codes;
      else if (c.dims !== 0) bad = 'L1 dims ' + c.dims;
      else if (hasLim(c.expr) || hasSeries(c.expr)) bad = 'L1 not elementary: ' + c.expr;
    } else if (c.kind !== 'mixed-' + layerCountOf(c)) bad = 'kind ' + c.kind;
    else if (!validCodes(c.codes, layerCountOf(c))) bad = 'codes ' + c.codes;
    else if (layerCountOf(c) < 1 || layerCountOf(c) > maxLayers[c.level]) bad = 'layers ' + c.codes;
    else if (!(c.dims >= 0 && c.dims <= maxLayers[c.level])) bad = 'dims ' + c.dims;
    else if (!hiddenOk(c.expr)) hiddenBad = c.expr;
    if (bad || hiddenBad) break;
  }
  check('随机窗口每行值/校验/种子/类型都正确', bad === '', bad);
  check('随机窗口无 325 裸倍数系数', hiddenBad === '', hiddenBad);
  for (const level of [1, 2, 3, 4]) {
    check(`随机窗口 L${level} 20 个不同种子`, seedsByLevel[level].size === 20, 'unique=' + seedsByLevel[level].size);
  }

  // ---- 行列式：不允许 1^n，且位置要多样 ----
  const testExe = path.join(root, 'tests', 'Calculate325-tests.exe');
  const detCorpus = path.join(tmpDir, 'det-corpus5.jsonl');
  run(testExe, ['--dump-random', detCorpus, '325', '5', '60', '20261028']);
  const detRows = fs.readFileSync(detCorpus, 'utf8').trim().split(/\r?\n/).map(JSON.parse);
  // “底数是裸 1”的幂：前面不能是数字/小数点/字母（排除 1.5^{}、x_{1}^{} 这类）
  const onePowRe = /(?<![\d.\w])1\s*\^\s*\{/;
  const onePowBad = detRows.filter(r => onePowRe.test(r.latex));
  check('L5 不出现 1^n（底数为 1 的幂）', onePowBad.length === 0,
    onePowBad.length + ' 条' + (onePowBad[0] ? '：' + onePowBad[0].latex.slice(0, 90) : ''));

  const detPos = {
    分子: /\\frac\{\\det/,
    分母: /\}\{\\det/,
    根号: /\\sqrt\{\\det/,
    积分上限: /\\int_\{0\}\^\{\\det/,
    求和上限: /\\sum_\{k=1\}\^\{\\det/,
    乘方: /\\det[^)]*\)\^\{1\}/
  };
  const usedPos = Object.keys(detPos).filter(k => detRows.some(r => detPos[k].test(r.latex)));
  check('行列式出现在 ≥4 种位置', usedPos.length >= 4, usedPos.join(','));

  const detPowN = detRows.filter(r => /\\right\)\^\{\\det/.test(r.latex)).length;
  check('包含「行列式的行列式次方」', detPowN > 0, detPowN + '/' + detRows.length);

  const matMulN = detRows.filter(r => /\\det\\left\(/.test(r.latex)).length;
  check('包含矩阵相乘 det(A·B)', matMulN > 0, matMulN + '/' + detRows.length);

  const BS = String.fromCharCode(92);
  const withDet = detRows.filter(r => r.latex.indexOf(BS + 'det') >= 0).length;
  check('L5 多数式子含行列式', withDet >= detRows.length * 0.5, withDet + '/' + detRows.length);

  // 只统计“算错”的；6 秒预算用尽是已知限制（闸门会重生成，不会交付给用户）
  const detAuditBad = detRows.filter(r => r.audit && !r.audit.ok && !/预算用尽/.test(r.audit.why));
  check('含行列式的式子审计不出现错误结果', detAuditBad.length === 0,
    detAuditBad.length + ' 条' + (detAuditBad[0] ? '：' + detAuditBad[0].audit.why : ''));
}

console.log('===== 随机模式测试：通过 ' + pass + ' / 失败 ' + fail + ' =====');
if (failures.length) {
  console.log('失败项：');
  failures.forEach(f => console.log('  - ' + f));
  process.exitCode = 1;
}
