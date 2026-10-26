# -*- coding: utf-8 -*-
"""check_dump.py -- independent verification of Calculate325 formula dumps.

Every record of the JSONL dump is parsed with :mod:`latex_ast` and evaluated
from scratch with :mod:`numengine`; the result is compared with the record's
``claim`` value using the relative error

    |computed - claim| / max(1, |claim|)  <=  --tol        (default 1e-6)

A parse error or an evaluation error is a *failure* and is reported together
with its reason -- nothing is skipped and nothing is silently treated as
correct.

Usage
-----
    python check_dump.py <dump.jsonl> [--tol 1e-6] [--verbose]
                         [--json <results.json>] [--quiet]

Output is always UTF-8 (the console encoding is reconfigured on start-up).
The final summary line is exactly

    ===== 独立校验：通过 N / 失败 M =====

Exit status is 0 if and only if every record passes.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import time

import numengine


def _force_utf8():
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def _short(text, width=150):
    text = " ".join(str(text).split())
    if len(text) <= width:
        return text
    return text[: width - 3] + "..."


def _fmt_value(v):
    if v is None:
        return "n/a"
    if abs(v.imag) <= 1e-12 * max(1.0, abs(v.real)):
        return "%.12g" % v.real
    return "%.12g%+.12gj" % (v.real, v.imag)


def main(argv=None):
    _force_utf8()
    ap = argparse.ArgumentParser(
        description="Calculate325 公式独立校验器（LaTeX -> AST -> 数值求值）")
    ap.add_argument("dump", help="JSONL 语料文件，每行一个公式记录")
    ap.add_argument("--tol", type=float, default=1e-6,
                    help="允许的最大相对误差（默认 1e-6）")
    ap.add_argument("--verbose", action="store_true",
                    help="打印失败项的 latex、算出的值与原因，以及恒等式明细")
    ap.add_argument("--quiet", action="store_true",
                    help="只打印失败项与汇总")
    ap.add_argument("--json", metavar="PATH", default=None,
                    help="把逐条结果写成 JSON 数组（顺序与输入一致）")
    args = ap.parse_args(argv)

    rows = []
    with open(args.dump, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
                rows.append(json.loads(line))

    npass = nfail = 0
    max_rel = 0.0
    max_rel_idx = 0
    ident_rows = 0
    ident_total = 0
    per_level = {}
    failures = []
    results = []
    t0 = time.time()

    for idx, row in enumerate(rows):
        latex = row.get("latex", "")
        claim = row.get("claim")
        level = row.get("level", "?")
        value = None
        reason = ""
        ok = False
        ctx = None
        rel = float("inf")
        try:
            if claim is None:
                raise numengine.EvalError("记录缺少 claim 字段")
            claim_f = float(claim)
            value, ctx = numengine.evaluate_latex(latex)
            if not math.isfinite(value.real) or not math.isfinite(value.imag):
                raise numengine.EvalError("结果不是有限数")
            if abs(value.imag) > 1e-6 * max(1.0, abs(value.real)):
                raise numengine.EvalError("结果不是实数（虚部 %.3g）" % value.imag)
            got = value.real
            rel = abs(got - claim_f) / max(1.0, abs(claim_f))
            if not math.isfinite(rel):
                raise numengine.EvalError("误差不是有限数")
            ok = rel <= args.tol
            if not ok:
                reason = "值与 claim 不符：算出 %.12g，声称 %g" % (got, claim_f)
        except Exception as exc:          # noqa: BLE001 - report *why* it failed
            value = None
            ok = False
            rel = float("inf")
            reason = "%s: %s" % (type(exc).__name__, exc)

        if ok:
            npass += 1
            if rel > max_rel:
                max_rel = rel
                max_rel_idx = idx + 1
        else:
            nfail += 1
            failures.append((idx + 1, level, latex, value, reason, ctx))

        lv = per_level.setdefault(level, [0, 0])
        lv[0 if ok else 1] += 1

        id_names = sorted(set(ctx.identities)) if ctx is not None else []
        if id_names:
            ident_rows += 1
            ident_total += len(id_names)

        results.append({
            "index": idx,
            "level": level,
            "seed": row.get("seed"),
            "ok": ok,
            "value": (float(value.real) if value is not None
                      and math.isfinite(value.real) else None),
            "rel": (rel if math.isfinite(rel) else None),
            "claim": row.get("claim"),
            "reason": reason if not ok else "",
            "identities": id_names,
        })

        if not args.quiet:
            status = "OK  " if ok else "FAIL"
            detail = ("rel=%.3e" % rel) if ok else ("reason=%s" % _short(reason, 90))
            print("[%3d/%3d] L%-2s %s value=%-22s %s"
                  % (idx + 1, len(rows), level, status, _fmt_value(value), detail))
            if args.verbose:
                print("          latex: %s" % latex)
                if id_names:
                    print("          identities: %s" % ", ".join(id_names))
                if ctx is not None and ctx.warnings:
                    print("          warnings: %s" % "; ".join(ctx.warnings))

    elapsed = time.time() - t0

    if failures and args.quiet:
        print()
        print("---- 失败明细 ----")
        for idx, level, latex, value, reason, ctx in failures:
            print("[%3d] L%-2s value=%s" % (idx, level, _fmt_value(value)))
            print("      latex: %s" % latex)
            print("      reason: %s" % reason)

    print()
    print("===== 独立校验：通过 %d / 失败 %d =====" % (npass, nfail))
    if npass:
        print("最大相对误差: %.6e（第 %d 条）" % (max_rel, max_rel_idx))
    else:
        print("最大相对误差: n/a")
    print("使用解析恒等式的条目: %d / %d（共触发 %d 次）"
          % (ident_rows, len(rows), ident_total))
    lv_bits = []
    for lv in sorted(per_level, key=lambda x: str(x)):
        p, f = per_level[lv]
        lv_bits.append("L%s %d/%d" % (lv, p, p + f))
    print("分级通过: " + "  ".join(lv_bits))
    print("耗时: %.1f s" % elapsed)
    if nfail:
        print("提示：用 --verbose 查看失败项细节。")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(results, fh, ensure_ascii=False, indent=1)
        print("结果已写入: %s" % args.json)

    return 0 if nfail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
