// 把 src/index.template.html 里的 __SOLVER__ / __APP__ 占位符替换成源码，生成单文件 index.html。
// 用法：node build.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const template = read('src/index.template.html');
const solver = read('src/solver.js').trim();
const app = read('src/app.js').trim();

for (const [src, name] of [[solver, 'src/solver.js'], [app, 'src/app.js']]) {
  if (/<\/script/i.test(src)) throw new Error(name + ' 里出现了 </script，无法内联');
}

const html = template
  .replace('/*__SOLVER__*/', () => solver)
  .replace('/*__APP__*/', () => app);

if (html.includes('__SOLVER__') || html.includes('__APP__')) {
  throw new Error('模板占位符没有被替换：检查 src/index.template.html 里的 /*__SOLVER__*/ 与 /*__APP__*/');
}

fs.writeFileSync(path.join(root, 'index.html'), html, 'utf8');
console.log('index.html 已生成：' + Buffer.byteLength(html, 'utf8') + ' 字节');
