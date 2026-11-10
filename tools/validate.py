#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
内容包校验工具。提交 PR 前跑一下，GitHub 上也会自动跑。

用法：
    python tools/validate.py                         # 只检查格式（不需要游戏）
    python tools/validate.py --tables "D:/xxx/textfiles(sc)"
                                                     # 再对照原版表检查：列名、Id 前缀、引用的 Id 是否存在
    python tools/validate.py --tables ... --dump out # 另外把合并后的整表写到 out/ 目录，方便对照

原版表从哪来：游戏目录里的表是打包的，需要用 AssetRipper 等工具解包出 config/textfiles(sc)，
见 docs/工具与调试.md。没有也没关系，只跑格式检查即可。

退出码：有错误返回 1（GitHub 检查会显示失败），只有警告返回 0。
"""
import argparse
import io
import json
import os
import re
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
errors = []
warnings = []


def err(where, msg):
    errors.append(f"[错误] {where}：{msg}")


def warn(where, msg):
    warnings.append(f"[警告] {where}：{msg}")


# ───────────────────────── 表格读写（规则与插件 TableMerge.cs 一致） ─────────────────────────

def is_data(line):
    return line.strip() != "" and not line.startswith("#")


def read_lines(path, where):
    raw = open(path, "rb").read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        err(where, f"不是 UTF-8 编码（记事本另存为时选 UTF-8）：{e}")
        return None
    return [l.rstrip("\r") for l in text.split("\n")]


class Table:
    """一张原版表：列名、Id → 行（字段列表）"""

    def __init__(self, lines):
        self.lines = lines
        self.h = next((i for i, l in enumerate(lines) if is_data(l)), -1)
        self.cols = lines[self.h].split("\t") if self.h >= 0 else []
        self.col_index = {}
        for i, c in enumerate(self.cols):
            self.col_index.setdefault(c.strip().lower(), i)
        self.row_of = {}
        for i in range(self.h + 1, len(lines)):
            if is_data(lines[i]):
                self.row_of.setdefault(lines[i].split("\t")[0].strip(), i)

    def get(self, id_, col):
        i = self.row_of.get(id_)
        c = self.col_index.get(col.lower())
        if i is None or c is None:
            return None
        f = self.lines[i].split("\t")
        return f[c] if c < len(f) else ""


def parse_delta(path, where):
    """读增量表，返回 (列名列表, [(行号, 字段列表)])"""
    lines = read_lines(path, where)
    if lines is None:
        return None
    h = next((i for i, l in enumerate(lines) if is_data(l)), -1)
    if h < 0:
        warn(where, "文件里没有表头和数据")
        return None
    cols = [c.strip() for c in lines[h].split("\t")]
    if len(cols) == 1 and "," in lines[h]:
        err(where, "表头像是用逗号分隔的，必须用 Tab 分隔")
        return None
    if not any(c.lower() == "id" for c in cols):
        err(where, "表头里没有 Id 列（表头写原表的英文列名，Tab 分隔）")
        return None
    seen = {}
    for c in cols:
        if c and c.lower() in seen:
            err(where, f"表头里列「{c}」写了两次")
        seen[c.lower()] = True
    rows = []
    idc = [c.lower() for c in cols].index("id")
    ids = {}
    for i in range(h + 1, len(lines)):
        if not is_data(lines[i]):
            continue
        cells = lines[i].split("\t")
        if len(cells) > len(cols):
            err(f"{where} 第 {i + 1} 行", f"有 {len(cells)} 格，比表头的 {len(cols)} 列多（多了 Tab？）")
        if idc >= len(cells) or not cells[idc].strip():
            err(f"{where} 第 {i + 1} 行", "没有 Id")
            continue
        id_ = cells[idc].strip()
        if id_ in ids:
            err(f"{where} 第 {i + 1} 行", f"Id {id_} 和第 {ids[id_]} 行重复")
        ids[id_] = i + 1
        if cells[idc] != id_:
            warn(f"{where} 第 {i + 1} 行", f"Id「{cells[idc]}」前后有空格")
        rows.append((i + 1, cells))
    return cols, rows


def merge(base_lines, deltas):
    """把增量合并进原表，返回新的行列表（用于 --dump，规则同插件）"""
    lines = list(base_lines)
    t = Table(lines)
    cols = t.cols
    for cols_d, rows in deltas:
        m = [t.col_index.get(c.lower(), -1) if c else -1 for c in cols_d]
        idc = [c.lower() for c in cols_d].index("id")
        for _, cells in rows:
            id_ = cells[idc].strip()
            at = t.row_of.get(id_)
            new = at is None
            row = [""] * len(cols) if new else (lines[at].split("\t") + [""] * len(cols))[: max(len(cols), len(lines[at].split("\t")))]
            for c in range(len(cols_d)):
                if m[c] < 0:
                    continue
                if c >= len(cells):
                    if new:
                        continue
                    break
                row[m[c]] = cells[c]
            row[0] = id_
            if new:
                lines.append("\t".join(row))
                t.row_of[id_] = len(lines) - 1
            else:
                lines[at] = "\t".join(row)
    return lines


# ───────────────────────── 改招 json ─────────────────────────

def strip_json_comments(text):
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        ch = text[i]
        if in_str:
            out.append(ch)
            if ch == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True; out.append(ch)
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        else:
            out.append(ch)
        i += 1
    # 去掉对象/数组末尾多余的逗号（Newtonsoft 允许）
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def check_skillmod(path, where):
    try:
        text = open(path, encoding="utf-8-sig").read()
        d = json.loads(strip_json_comments(text))
    except Exception as e:
        err(where, f"json 格式有误：{e}")
        return
    if not isinstance(d, dict):
        err(where, "最外层应该是 { ... }")
        return
    if not d.get("base"):
        warn(where, "没有写 base（底子演出），文件名必须和原版演出同名才行")
    for k in ("add", "edit"):
        for i, fx in enumerate(d.get(k) or []):
            if not isinstance(fx, dict) or not fx.get("fx"):
                err(where, f"{k} 第 {i + 1} 项没有写 fx（特效名）")
            if k == "add" and isinstance(fx, dict) and not fx.get("from"):
                err(where, f"add 第 {i + 1} 项没有写 from（从哪个演出借特效）")


# ───────────────────────── 主流程 ─────────────────────────

def main():
    ap = argparse.ArgumentParser(description="古龙风云录 开放 Mod 内容包校验")
    ap.add_argument("--packs", default=os.path.join(ROOT, "packs"), help="内容包目录（默认仓库里的 packs/）")
    ap.add_argument("--tables", help="解包出来的原版 textfiles(sc) 目录，给了才做对照检查")
    ap.add_argument("--dump", help="把合并后的整表写到这个目录")
    a = ap.parse_args()

    if not os.path.isdir(a.packs):
        print("找不到内容包目录：" + a.packs)
        return 1

    packs = []
    for name in sorted(os.listdir(a.packs)):
        d = os.path.join(a.packs, name)
        if not os.path.isdir(d):
            continue
        pj = os.path.join(d, "pack.json")
        if not os.path.isfile(pj):
            err(name, "没有 pack.json")
            continue
        try:
            info = json.loads(open(pj, encoding="utf-8-sig").read())
        except Exception as e:
            err(f"{name}/pack.json", f"json 格式有误：{e}")
            continue
        for k in ("id", "name", "version", "authors", "idPrefix"):
            if not info.get(k):
                (err if k in ("id", "name") else warn)(f"{name}/pack.json", f"缺少 {k}")
        if info.get("id") and info["id"] != name:
            warn(f"{name}/pack.json", f"id「{info['id']}」和文件夹名不一致，建议一样")
        for k, eg in (("idPrefix", '["zs_"]'), ("authors", '["张三"]'), ("requires", '["yinlong"]')):
            if isinstance(info.get(k), str):
                err(f"{name}/pack.json", f"{k} 要写成列表，如 {eg}（写成字符串插件会读不出来）")
                info[k] = [info[k]]
        info["_dir"], info["_name"] = d, name
        packs.append(info)

    # Id 前缀属于作者：不同作者的包之间不能重叠，同一作者的多个包可以共用
    for i, p in enumerate(packs):
        for q in packs[i + 1:]:
            if set(p.get("authors") or []) & set(q.get("authors") or []):
                continue
            for x in p.get("idPrefix") or []:
                for y in q.get("idPrefix") or []:
                    if x.startswith(y) or y.startswith(x):
                        err(f"{p['_name']} / {q['_name']}", f"Id 前缀「{x}」和「{y}」重叠，换一个独特的前缀")
    ids_all = {p.get("id") for p in packs}
    for p in packs:
        for r in p.get("requires") or []:
            if r not in ids_all:
                warn(p["_name"], f"依赖的包 {r} 不在仓库里")

    originals = {}

    def original(table):
        if not a.tables:
            return None
        if table not in originals:
            f = os.path.join(a.tables, table + ".txt")
            originals[table] = Table(read_lines(f, table) or []) if os.path.isfile(f) else None
        return originals[table]

    # 表格
    deltas_by_table = {}  # 表名 → [(pack, cols, rows)]
    order = sorted(packs, key=lambda p: (p.get("order", 0), p["_name"].lower()))
    for p in order:
        for sub in ("tables", "tables_tc"):
            td = os.path.join(p["_dir"], sub)
            if not os.path.isdir(td):
                continue
            for dirpath, _, fs in os.walk(td):
                for f in sorted(fs):
                    path = os.path.join(dirpath, f)
                    rel = os.path.relpath(path, td).replace("\\", "/")
                    where = f"{p['_name']}/{sub}/{rel}"
                    if not f.lower().endswith(".txt"):
                        warn(where, "tables 下只认 .txt 文件")
                        continue
                    r = parse_delta(path, where)
                    if r is None:
                        continue
                    cols, rows = r
                    table = rel[:-4]
                    if sub == "tables":
                        deltas_by_table.setdefault(table, []).append((p, cols, rows))
                    orig = original(table)
                    if a.tables and orig is None and "/" not in table:
                        err(where, f"原版没有 {table} 这张表（表名拼错了？只能往已有的表里加行）")
                        continue
                    if orig is None:
                        continue
                    for c in cols:
                        if c and c.lower() not in orig.col_index:
                            err(where, f"原表没有列「{c}」（注意大小写和拼写，对照原表第一行）")
                    idc = [c.lower() for c in cols].index("id")
                    for ln, cells in rows:
                        id_ = cells[idc].strip()
                        if id_ not in orig.row_of:
                            pre = p.get("idPrefix") or []
                            if pre and not any(id_.startswith(x) for x in pre):
                                err(f"{where} 第 {ln} 行", f"新增的 Id {id_} 没有用本包登记的前缀 {pre}")

    # 多个包改同一行
    for table, lst in deltas_by_table.items():
        owner = {}
        for p, cols, rows in lst:
            idc = [c.lower() for c in cols].index("id")
            for ln, cells in rows:
                id_ = cells[idc].strip()
                if id_ in owner and owner[id_] != p["_name"]:
                    warn(f"{table} 的 {id_}", f"{owner[id_]} 和 {p['_name']} 都改了，后加载的 {p['_name']} 为准")
                owner[id_] = p["_name"]

    # 引用检查（需要原表）
    def exists(table, id_):
        o = original(table)
        if o is not None and id_ in o.row_of:
            return True
        for p, cols, rows in deltas_by_table.get(table, []):
            idc = [c.lower() for c in cols].index("id")
            if any(cells[idc].strip() == id_ for _, cells in rows):
                return True
        return False

    def cell(cols, cells, name):
        lc = [c.lower() for c in cols]
        if name.lower() not in lc:
            return None
        i = lc.index(name.lower())
        return cells[i].strip() if i < len(cells) else ""

    if a.tables:
        for p, cols, rows in deltas_by_table.get("NormalInventoryItem", []):
            for ln, cells in rows:
                sk = cell(cols, cells, "Skill")
                if sk and not exists("SkillItem", sk):
                    err(f"{p['_name']} NormalInventoryItem 第 {ln} 行", f"Skill 列的招式 {sk} 在 SkillItem 里不存在")
                icon = cell(cols, cells, "Icon")
                if icon:
                    png = os.path.join(p["_dir"], "files", "Image", "Icon", "Item", icon + ".png")
                    orig = original("NormalInventoryItem")
                    used = orig is not None and any(orig.get(i, "Icon") == icon for i in orig.row_of)
                    if not os.path.isfile(png) and not used:
                        warn(f"{p['_name']} NormalInventoryItem 第 {ln} 行", f"图标 {icon} 在包里找不到（files/Image/Icon/Item/{icon}.png），原版也没用过这个图标名")
        for p, cols, rows in deltas_by_table.get("RewardItem", []):
            for ln, cells in rows:
                rw = cell(cols, cells, "Rewards") or ""
                for kind, rid in re.findall(r"'(RewardProps|RewardEquips|RewardGifts)'\s*:\s*'([^']+)'", rw):
                    if not any(exists(t, rid) for t in ("NormalInventoryItem", "Equipment")):
                        err(f"{p['_name']} RewardItem 第 {ln} 行", f"{kind} 发放的道具 {rid} 在道具表里不存在")

    # 改招 json
    for p in packs:
        sd = os.path.join(p["_dir"], "skillmod")
        if os.path.isdir(sd):
            for f in sorted(os.listdir(sd)):
                if f.lower().endswith(".json"):
                    check_skillmod(os.path.join(sd, f), f"{p['_name']}/skillmod/{f}")

    # 文件名：files 下的路径要和游戏资源路径一致
    for p in packs:
        fd = os.path.join(p["_dir"], "files")
        if os.path.isdir(fd):
            for dirpath, _, fs in os.walk(fd):
                for f in fs:
                    rel = os.path.relpath(os.path.join(dirpath, f), fd).replace("\\", "/")
                    top = rel.split("/")[0]
                    if top not in ("Image", "Config", "Resources_Audio"):
                        warn(f"{p['_name']}/files/{rel}", "files 下一般是 Image/、Config/、Resources_Audio/ 开头，路径对吗？")

    # 合并后的整表
    if a.dump:
        if not a.tables:
            err("--dump", "需要同时给 --tables")
        else:
            os.makedirs(a.dump, exist_ok=True)
            for table, lst in deltas_by_table.items():
                o = original(table)
                if o is None:
                    continue
                out = merge(o.lines, [(cols, rows) for _, cols, rows in lst])
                fn = os.path.join(a.dump, table + ".txt")
                os.makedirs(os.path.dirname(fn), exist_ok=True)
                with io.open(fn, "w", encoding="utf-8", newline="") as w:
                    w.write("\r\n".join(out))
                print(f"已写出合并后的 {fn}")

    print(f"检查了 {len(packs)} 个内容包：" + "，".join(p.get("name", p["_name"]) for p in packs))
    for m in warnings:
        print(m)
    for m in errors:
        print(m)
    print(f"\n结果：{len(errors)} 个错误，{len(warnings)} 个警告" + ("" if a.tables else "（没给 --tables，只检查了格式）"))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
