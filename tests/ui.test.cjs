/*
 * 界面逻辑冒烟测试： node tests/ui.test.cjs
 * 用极简 DOM 桩把 src/app.js 真跑一遍，检查渲染结果，确保双击打开网页不会白屏/报错。
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
const solverSrc = fs.readFileSync(path.join(root, 'src', 'solver.js'), 'utf8');
const appSrc = fs.readFileSync(path.join(root, 'src', 'app.js'), 'utf8');

// ---------------- 极简 DOM 桩 ----------------
function makeNode(tag) {
  const node = {
    tagName: String(tag).toUpperCase(),
    children: [],
    parentNode: null,
    attrs: {},
    style: {},
    className: '',
    _text: '',
    value: '',
    checked: false,
    disabled: false,
    listeners: {},
    appendChild(c) { this.children.push(c); c.parentNode = this; return c; },
    insertBefore(n, ref) {
      const i = ref ? this.children.indexOf(ref) : -1;
      if (i < 0) this.children.push(n); else this.children.splice(i, 0, n);
      n.parentNode = this;
      return n;
    },
    get firstChild() { return this.children[0] || null; },
    setAttribute(k, v) { this.attrs[k] = v; },
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); },
    select() {},
    textContentAll() {
      return this._text + this.children.map(c => c.textContentAll()).join('');
    }
  };
  Object.defineProperty(node, 'textContent', {
    get() { return this.textContentAll(); },
    set(v) { this.children.length = 0; this._text = v === undefined || v === null ? '' : String(v); }
  });
  return node;
}

function makeDocument(values) {
  const byId = {};
  const defs = {
    target: 'input', go: 'button', result: 'div', examples: 'div',
    maxBlocks: 'select', orderMode: 'select', factMax: 'input',
    allowFactorial: 'input', allowDecimal: 'input',
    patternSel: 'select'
  };
  for (const id of Object.keys(defs)) {
    byId[id] = makeNode(defs[id]);
  }
  byId.target.value = values.target;
  byId.maxBlocks.value = String(values.maxBlocks || 2);
  byId.orderMode.value = values.orderMode || 'fixed';
  byId.factMax.value = String(values.factMax === undefined ? 12 : values.factMax);
  byId.allowFactorial.checked = values.allowFactorial !== false;
  byId.allowDecimal.checked = values.allowDecimal !== false;
  const pats = values.patterns || ['325'];
  byId.patternSel.value = values.patternValue || (pats.length > 1 ? 'all' : pats[0]);
  const body = makeNode('body');
  return {
    readyState: 'complete',
    body,
    createElement: makeNode,
    createTextNode: (t) => { const n = makeNode('#text'); n.textContent = t; return n; },
    getElementById: (id) => byId[id] || null,
    querySelector: (sel) => (sel && sel[0] === '#' ? (byId[sel.slice(1)] || null) : null),
    execCommand: () => true,
    addEventListener: () => {}
  };
}

function runApp(values) {
  const doc = makeDocument(values);
  const win = { Calculate325: null };
  const errors = [];
  const sandboxModule = { exports: {} };
  new Function('module', 'window', solverSrc)(sandboxModule, win);
  const api = sandboxModule.exports.Calculate325 || win.Calculate325 || sandboxModule.exports;
  win.Calculate325 = api;
  const nav = { clipboard: null };
  try {
    new Function('window', 'document', 'navigator', 'setTimeout', appSrc)(win, doc, nav, setTimeout);
  } catch (e) {
    errors.push('初始化异常：' + e.message);
  }
  return { doc, api, errors };
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async function main() {
  // 场景 1：默认目标 325（1 块）
  {
    const { doc, errors } = runApp({ target: '325' });
    await wait(300);
    const text = doc.querySelector('#result').textContentAll();
    check('场景1 无异常', errors.length === 0, errors.join('; '));
    check('场景1 渲染出算式', text.includes('325') && text.includes('✓'), text.slice(0, 120));
    check('场景1 结果区非空', doc.querySelector('#result').children.length > 0);
    check('场景1 示例按钮已生成', doc.querySelector('#examples').children.length > 5);
  }

  // 场景 2：2 块求解 1000
  {
    const { doc, errors } = runApp({ target: '1000', maxBlocks: 2 });
    await wait(600);
    const text = doc.querySelector('#result').textContentAll();
    check('场景2 无异常', errors.length === 0, errors.join('; '));
    check('场景2 找到算式', text.includes('=1000') || text.includes('= 1000') || text.includes('1000'), text.slice(0, 160));
    check('场景2 报告块数', /2 块（325）/.test(text), text.slice(0, 160));
    check('场景2 校验徽章', text.includes('精确求值校验通过'), text.slice(0, 160));
  }

  // 场景 3：1 块内无解 → 直接把保底算式作为唯一答案输出
  {
    const { doc, errors } = runApp({ target: '123456', maxBlocks: 1 });
    await wait(1500);
    const text = doc.querySelector('#result').textContentAll();
    check('场景3 无异常', errors.length === 0, errors.join('; '));
    check('场景3 输出区只有一个卡片', doc.querySelector('#result').children.length === 1, 'cards=' + doc.querySelector('#result').children.length);
    check('场景3 直接给出算式（无“保底构造”之类提示）', text.includes('=123456') || text.includes('123456'), text.slice(0, 200));
    check('场景3 不含被移除的提示', !text.includes('保底构造') && !text.includes('未找到') && !text.includes('超时'), text.slice(0, 200));
    check('场景3 保底算式带校验徽章', text.includes('精确求值校验通过'), text.slice(0, 200));
    check('场景3 不再单独提示未找到', !text.includes('未找到算式'), text.slice(0, 200));
  }

  // 场景 4：非法输入
  {
    const { doc, errors } = runApp({ target: 'abc' });
    await wait(200);
    const text = doc.querySelector('#result').textContentAll();
    check('场景4 无异常', errors.length === 0, errors.join('; '));
    check('场景4 提示输入无效', text.includes('输入无效'), text.slice(0, 120));
  }

  // 场景 5：任意换序模式 + 关掉小数点
  {
    const { doc, errors } = runApp({ target: '2', maxBlocks: 1, orderMode: 'any', allowDecimal: false });
    await wait(400);
    const text = doc.querySelector('#result').textContentAll();
    check('场景5 无异常', errors.length === 0, errors.join('; '));
    check('场景5 换序模式有解', text.includes('数字可换序') && text.includes('精确求值校验通过'), text.slice(0, 200));
    check('场景5 未使用小数点', !/数字串：[^ ]*\./.test(text));
  }

  console.log('===== 界面冒烟测试：通过 ' + pass + ' / 失败 ' + fail + ' =====');
  if (failures.length) { console.log('失败项：'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
})();
