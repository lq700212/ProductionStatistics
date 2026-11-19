# -*- coding: utf-8 -*-
"""
三份培训文档交付核验
====================

对 docs/ 下的 Markdown、截图、PDF 做一次性全量体检，覆盖技能里的硬性红线：

  1. 三份文档齐全、UTF-8 可解码、无乱码、无 truncated / TODO / 待补充
  2. 图片引用只允许 images/ASCII 文件名，文件必须存在，每张交付图至少被引用一次
  3. 每张图必须紧跟图注，图注必须注明「演示数据」
  4. 客户两版（操作员版 / 客户技术工艺版）禁内部机制词、禁内部章节
  5. 功能边界词（MES / 配方 / 相机 / 数据库 …）必须处于否定语境，防止吹牛
  6. 开篇必须有引用块，版本号必须是 0.0.2，旧版本号直接失败
  7. 每份文档有最低字数 / 表格数 / 图片数，必需章节齐全
  8. 客户技术版必须列全 11 个 PLC 地址键
  9. 内部版必须有至少 8 处「源码文件:行号」定位
 10. 同名 PDF 存在且 > 50 KB，docs/ 下不得残留 HTML 中间件
 11. 调用 check_config.py 做现场配置自检（ini / 版本 / 源码常量）

用法：
    python check_docs.py
    python check_docs.py --verbose   # 打印每份文档的统计明细

退出码：0 = 全绿；1 = 有 FAIL。
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = TOOL_DIR.parents[1]
DOCS_DIR = PROJECT_ROOT / "docs"
IMAGES_DIR = DOCS_DIR / "images"
PDF_MIN_BYTES = 50 * 1024

CN_CHAR = re.compile(r"[\u4e00-\u9fff]")
IMAGE_REF = re.compile(r"!\[[^\]]*\]\(([^)]+)\)")
IMAGE_PATH_OK = re.compile(r"^images/[A-Za-z0-9_.-]+\.(?:png|jpg)$")
HEADING = re.compile(r"^(#{1,6})\s+(.+?)\s*$")
OLD_VERSION = re.compile(r"\b2\.[01]\.\d+\.\d+\b")
ANY_VERSION = re.compile(r"\b\d+\.\d+\.\d+\.\d+\b")
TABLE_SEPARATOR = re.compile(r"^\s*\|(?:\s*:?-{2,}:?\s*\|)+\s*$")


class Report:
    def __init__(self) -> None:
        self.issues: list[dict] = []
        self.stats: list[dict] = []

    def fail(self, doc: str, msg: str, line: int = 0) -> None:
        self.issues.append({"level": "FAIL", "doc": doc, "line": line, "msg": msg})

    def warn(self, doc: str, msg: str, line: int = 0) -> None:
        self.issues.append({"level": "WARN", "doc": doc, "line": line, "msg": msg})

    @property
    def failed(self) -> bool:
        return any(item["level"] == "FAIL" for item in self.issues)

    def count(self, level: str) -> int:
        return sum(1 for item in self.issues if item["level"] == level)


def load_config() -> dict:
    script = TOOL_DIR / "check_config.py"
    proc = subprocess.run(
        [sys.executable, str(script), "--json"],
        cwd=str(TOOL_DIR),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        encoding="utf-8",
        errors="replace",
    )
    if proc.returncode != 0 or not proc.stdout.strip():
        raise RuntimeError(
            f"check_config.py 执行失败（退出码 {proc.returncode}）：\n{proc.stderr}"
        )
    return json.loads(proc.stdout)


def decode_document(path: Path) -> tuple[str, str]:
    """返回 (文本, 错误描述)。错误为空表示可解码。"""
    raw = path.read_bytes()
    if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
        return "", "文件是 UTF-16 编码，必须转为 UTF-8"
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        return "", f"不是合法 UTF-8：{exc}"
    if "\ufffd" in text:
        return text, "正文含替换字符 U+FFFD，可能已损坏"
    return text, ""


def iter_structural_lines(text: str):
    """逐行产出 (行号, 行文本, 是否位于代码围栏内)，标题/表格统计要跳过代码块。"""
    in_fence = False
    for index, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            in_fence = not in_fence
            yield index, line, True
            continue
        yield index, line, in_fence


def collect_headings(text: str) -> list[tuple[int, int, str]]:
    headings = []
    for lineno, line, in_fence in iter_structural_lines(text):
        if in_fence:
            continue
        match = HEADING.match(line)
        if match:
            headings.append((lineno, len(match.group(1)), match.group(2).strip()))
    return headings


def count_table_separators(text: str) -> int:
    count = 0
    for _, line, in_fence in iter_structural_lines(text):
        if not in_fence and TABLE_SEPARATOR.match(line):
            count += 1
    return count


def check_required_headings(cfg: dict, role: str, headings: list[tuple[int, int, str]],
                            report: Report, doc_name: str) -> None:
    titles = [title for _, _, title in headings]
    for keyword in cfg["required_headings"].get(role, []):
        if not any(keyword in title for title in titles):
            report.fail(doc_name, f"缺少必需章节（标题需含「{keyword}」）")


def check_forbidden_terms(report: Report, doc_name: str, text: str, terms: list[str],
                          label: str) -> None:
    lower_lines = [(i, line.lower()) for i, line in enumerate(text.splitlines(), start=1)]
    for term in terms:
        needle = term.lower()
        for lineno, line in lower_lines:
            if needle in line:
                report.fail(doc_name, f"{label}出现禁用词「{term}」", lineno)
                break  # 同一个词只报第一处，避免刷屏


def check_negation_terms(report: Report, doc_name: str, text: str, cfg: dict) -> None:
    """功能边界词必须处于否定/限定语境：检查命中行及前后各 2 行。"""
    lines = text.splitlines()
    lowered = [line.lower() for line in lines]
    negation_words = cfg["negation_words"]
    for term in cfg["negation_required_terms"]:
        needle = term.lower()
        for index, line in enumerate(lowered):
            if needle not in line:
                continue
            window = "\n".join(lowered[max(0, index - 2): min(len(lines), index + 2)])
            if not any(word.lower() in window for word in negation_words):
                report.fail(
                    doc_name,
                    f"功能边界词「{term}」缺少否定语境（不支持 / 未配置 / 不含 等）",
                    index + 1,
                )
            break


def check_customer_headings(report: Report, doc_name: str,
                            headings: list[tuple[int, int, str]], cfg: dict) -> None:
    for lineno, _, title in headings:
        for word in cfg["forbidden_heading_words_customer"]:
            if word in title:
                report.fail(doc_name, f"客户版出现内部章节标题「{title}」", lineno)


def check_images(report: Report, cfg: dict, role: str, doc_name: str, text: str) -> int:
    lines = text.splitlines()
    seen = set()
    for index, line in enumerate(lines):
        for raw_path in IMAGE_REF.findall(line):
            path = raw_path.strip()
            if not IMAGE_PATH_OK.match(path):
                report.fail(
                    doc_name,
                    f"图片路径不合规「{path}」（必须 images/ASCII 名.png|jpg）",
                    index + 1,
                )
                continue
            filename = path.split("/", 1)[1]
            seen.add(filename)
            if filename not in cfg["image_files"]:
                report.fail(doc_name, f"引用了白名单外的图片「{filename}」", index + 1)
            if filename in cfg["dev_only_images"] and role != "internal":
                report.fail(doc_name, f"客户版引用了内部专用图「{filename}」", index + 1)
            if not (DOCS_DIR / path).is_file():
                report.fail(doc_name, f"图片文件不存在「{path}」", index + 1)

            # 图注：图片行之后 4 行内第一个非空行，必须注明演示数据
            caption = ""
            for follow in lines[index + 1: index + 5]:
                if follow.strip():
                    caption = follow.strip()
                    break
            if "演示数据" not in caption:
                report.fail(
                    doc_name,
                    f"图片「{filename}」的图注未注明「演示数据」",
                    index + 1,
                )
    return len(seen)


def check_document(report: Report, cfg: dict, role: str) -> None:
    meta = cfg["documents"][role]
    doc_name = meta["file"]
    path = DOCS_DIR / doc_name
    if not path.is_file():
        report.fail(doc_name, "文档不存在")
        return

    text, error = decode_document(path)
    if error:
        report.fail(doc_name, error)
    if not text:
        return

    lines = text.splitlines()
    headings = collect_headings(text)
    h1 = [item for item in headings if item[1] == 1]
    if len(h1) != 1:
        report.fail(doc_name, f"H1 标题数量应为 1，实际 {len(h1)}")
    elif "生产统计" not in h1[0][2]:
        report.fail(doc_name, f"H1 标题缺少「生产统计」：{h1[0][2]}", h1[0][0])

    # 开篇引用块 + 版本
    opening = "\n".join(lines[:20])
    if ">" not in opening:
        report.fail(doc_name, "开篇 20 行内缺少引用块（> 适用对象 / 版本）")
    elif "版本" not in opening:
        report.fail(doc_name, "开篇引用块缺少「版本」说明")

    if cfg["required_version"] not in text:
        report.fail(doc_name, f"正文未出现交付版本 {cfg['required_version']}")
    old = OLD_VERSION.search(text)
    if old:
        line_no = text[: old.start()].count("\n") + 1
        report.fail(doc_name, f"出现旧版本号「{old.group(0)}」", line_no)

    chinese = len(CN_CHAR.findall(text))
    tables = count_table_separators(text)
    images = check_images(report, cfg, role, doc_name, text)

    if chinese < meta["min_chinese_chars"]:
        report.fail(
            doc_name,
            f"汉字数不足：{chinese} < {meta['min_chinese_chars']}",
        )
    if tables < meta["min_tables"]:
        report.fail(doc_name, f"表格数不足：{tables} < {meta['min_tables']}")
    if images < meta["min_images"]:
        report.fail(doc_name, f"配图数不足：{images} < {meta['min_images']}")

    check_required_headings(cfg, role, headings, report, doc_name)
    check_forbidden_terms(report, doc_name, text, cfg["forbidden_terms_all"], "通用禁词")

    if role in ("operator", "customer"):
        check_forbidden_terms(report, doc_name, text,
                              cfg["forbidden_terms_customer"], "客户版禁词")
        check_forbidden_terms(report, doc_name, text,
                              cfg["forbidden_terms_customer_only"], "客户版禁词")
        check_customer_headings(report, doc_name, headings, cfg)
        check_negation_terms(report, doc_name, text, cfg)

    if role == "operator":
        if "30 秒" not in text and "30秒" not in text:
            report.fail(doc_name, "未说明 CSV 每 30 秒落盘（操作员必须知道断电会丢多少）")

    if role == "customer":
        missing = [key for key in cfg["plc_address_keys"] if key not in text]
        if missing:
            report.fail(doc_name, "PLC 地址表缺少键：" + "、".join(missing))
        if cfg["hardware"]["plc_brand"].split("（")[0] not in text:
            report.fail(doc_name, "未写明 PLC 品牌（三菱）")
        if "FX5U" not in text:
            report.fail(doc_name, "未写明 PLC 型号（FX5U）")

    if role == "internal":
        code_refs = len(re.findall(cfg["code_ref_pattern"], text))
        if code_refs < cfg["min_code_references"]:
            report.fail(
                doc_name,
                f"源码定位不足：{code_refs} 处 < {cfg['min_code_references']} 处"
                "（格式：文件.cs:行号）",
            )

    report.stats.append({
        "doc": doc_name,
        "chars": chinese,
        "tables": tables,
        "images": images,
        "lines": len(lines),
        "headings": len(headings),
        "code_refs": len(re.findall(cfg["code_ref_pattern"], text)),
    })


def check_images_dir(report: Report, cfg: dict) -> None:
    if not IMAGES_DIR.is_dir():
        report.fail("docs/images", "截图目录不存在")
        return
    actual = {path.name for path in IMAGES_DIR.iterdir() if path.is_file()}
    expected = set(cfg["image_files"])
    for name in sorted(expected - actual):
        report.fail("docs/images", f"缺少白名单截图「{name}」")
    for name in sorted(actual - expected):
        if Path(name).suffix.lower() in (".png", ".jpg", ".jpeg"):
            report.fail("docs/images", f"存在未登记截图「{name}」（可能是内部调试图）")
        else:
            report.warn("docs/images", f"截图目录存在非图片文件「{name}」")


def check_coverage(report: Report, cfg: dict, referenced: set[str]) -> None:
    for name in cfg["must_reference_images"]:
        if name not in referenced:
            report.fail("docs", f"交付截图「{name}」从未被任何文档引用")


def check_pdfs(report: Report, cfg: dict) -> None:
    for role in cfg["documents"]:
        meta = cfg["documents"][role]
        md = DOCS_DIR / meta["file"]
        pdf = md.with_suffix(".pdf")
        if not pdf.is_file():
            report.fail(meta["file"], "同名 PDF 不存在")
            continue
        size = pdf.stat().st_size
        if size <= PDF_MIN_BYTES:
            report.fail(meta["file"], f"PDF 体积 {size} 字节 ≤ 50 KB")
        with pdf.open("rb") as handle:
            head = handle.read(5)
        if head != b"%PDF-":
            report.fail(meta["file"], "PDF 文件头不是 %PDF-，可能已损坏")

    pdfs = {path.name for path in DOCS_DIR.glob("*.pdf")}
    expected = {Path(cfg["documents"][role]["file"]).with_suffix(".pdf").name
                for role in cfg["documents"]}
    for name in sorted(pdfs - expected):
        report.warn("docs", f"存在多余的 PDF「{name}」")


def check_no_html(report: Report) -> None:
    leftovers = [path for path in DOCS_DIR.rglob("*.html")]
    leftovers += [path for path in DOCS_DIR.rglob("*.htm")]
    for path in leftovers:
        report.fail(str(path.relative_to(PROJECT_ROOT)), "残留 HTML 中间件，交付前必须删除")


def check_extra_files(report: Report, cfg: dict) -> None:
    allowed = {cfg["documents"][role]["file"] for role in cfg["documents"]}
    allowed |= {Path(name).with_suffix(".pdf").name for name in allowed}
    for path in DOCS_DIR.iterdir():
        if path.name in ("images", "README.md"):
            continue
        if path.is_file() and path.name not in allowed:
            report.warn("docs", f"docs 根目录存在额外文件「{path.name}」")


def main() -> int:
    report = Report()
    try:
        cfg = load_config()
    except Exception as exc:  # noqa: BLE001
        print(f"[FAIL] 无法加载 check_config.py：{exc}")
        return 1

    # 现场配置自检：ini 被截图脚本改过没还原、版本漂移都会在这里拦住
    if not cfg.get("version_matches"):
        report.fail("check_config", f"工程版本 {cfg['app']['version']} ≠ 交付版本 {cfg['required_version']}")
    if not cfg["ini_status"]["ok"]:
        for item in cfg["ini_status"]["items"]:
            if not item["ok"]:
                report.fail("check_config", f"现场 ini「{item['key']}」= {item['actual']!r}，"
                                            f"期望 {item['expected']!r}")
    if not cfg["source_constants"]["ok"]:
        for item in cfg["source_constants"]["items"]:
            if not item["ok"]:
                report.fail("check_config", f"源码常量抽查失败：{item['label']}")

    check_images_dir(report, cfg)

    referenced: set[str] = set()
    for role in cfg["documents"]:
        check_document(report, cfg, role)
    # 重新扫一遍引用集合（check_images 内部已核对存在性，这里只收集并集）
    for role in cfg["documents"]:
        path = DOCS_DIR / cfg["documents"][role]["file"]
        if path.is_file():
            text, _ = decode_document(path)
            referenced.update(
                match.split("/", 1)[1]
                for match in IMAGE_REF.findall(text)
                if match.startswith("images/") and "/" in match
            )

    check_coverage(report, cfg, referenced)
    check_pdfs(report, cfg)
    check_no_html(report)
    check_extra_files(report, cfg)

    print("=" * 78)
    print("生产统计 培训文档交付核验")
    print("=" * 78)
    for stat in report.stats:
        print(f"[INFO] {stat['doc']}")
        print(f"       汉字 {stat['chars']:,} / 表格 {stat['tables']} / 配图 {stat['images']} / "
              f"行数 {stat['lines']:,} / 标题 {stat['headings']} / 源码定位 {stat['code_refs']}")
    print("-" * 78)

    if not report.issues:
        print("[OK] 全部检查通过：文档、截图、PDF、现场配置一致。")
        return 0

    for item in report.issues:
        location = f"{item['doc']}:{item['line']}" if item["line"] else item["doc"]
        print(f"[{item['level']}] {location}  {item['msg']}")

    print("-" * 78)
    print(f"结果：{report.count('FAIL')} 项失败，{report.count('WARN')} 项提醒。")
    return 1 if report.failed else 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
