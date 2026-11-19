# -*- coding: utf-8 -*-
"""
Markdown → PDF 交付流水线
=========================

流程：
  1. Python-Markdown 把三份培训文档渲染成 HTML（表格、代码块、引用块）；
  2. 套 A4 打印 CSS（中文字体、表格边框、图片自适应、颜色保真、分页控制）；
  3. Edge 无头模式 --print-to-pdf 生成同名 PDF；
  4. 校验 PDF 文件头与体积（>50 KB）；
  5. 默认删除 HTML 中间件，保持 docs/ 干净；--keep-html 可保留预览。

为什么用 Edge：
  Windows 自带、无需安装 LaTeX/Pandoc；Chromium 打印对中文、表格、图片的支持稳定，
  且能通过 --no-pdf-header-footer 去掉页眉页脚。

用法：
    python md_to_pdf.py                # 转换全部三份，成功后删 HTML
    python md_to_pdf.py --keep-html    # 保留 docs/_preview/*.html 供浏览器预览
    python md_to_pdf.py 01_操作员版_生产统计.md   # 只转指定文件
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import markdown

TOOL_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = TOOL_DIR.parents[1]
DOCS_DIR = PROJECT_ROOT / "docs"
PREVIEW_DIR = DOCS_DIR / "_preview"

EDGE_CANDIDATES = [
    Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
    Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
]

DOCUMENTS = [
    "01_操作员版_生产统计.md",
    "02_客户技术工艺版_生产统计.md",
    "03_内部版_生产统计.md",
]

MIN_PDF_BYTES = 50 * 1024

CSS = r"""
@page {
    size: A4;
    margin: 16mm 14mm 18mm 14mm;
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body {
    margin: 0;
    font-family: "Microsoft YaHei", "微软雅黑", "Segoe UI", "SimSun", sans-serif;
    font-size: 10.5pt;
    line-height: 1.72;
    color: #1f2430;
    -webkit-print-color-adjust: exact;
    print-color-adjust: exact;
}
h1, h2, h3, h4 {
    font-family: "Microsoft YaHei", "微软雅黑", "Segoe UI", sans-serif;
    color: #16233a;
    page-break-after: avoid;
    break-after: avoid;
}
h1 {
    font-size: 24pt;
    line-height: 1.3;
    margin: 0 0 18px;
    padding-bottom: 10px;
    border-bottom: 3px solid #2f6fed;
    letter-spacing: 0.5px;
}
h2 {
    font-size: 15.5pt;
    margin: 26px 0 10px;
    padding-left: 9px;
    border-left: 4px solid #2f6fed;
}
h3 {
    font-size: 12.5pt;
    margin: 18px 0 7px;
    color: #24427a;
}
h4 { font-size: 11pt; margin: 14px 0 6px; }
p { margin: 7px 0; text-align: justify; }
strong { color: #101b30; }
a { color: #2f6fed; text-decoration: none; }
blockquote {
    margin: 12px 0;
    padding: 10px 14px;
    background: #f1f6ff;
    border-left: 4px solid #8fb4ff;
    color: #33405a;
    page-break-inside: avoid;
}
blockquote p { margin: 3px 0; }
ul, ol { margin: 7px 0 7px 22px; padding: 0; }
li { margin: 2px 0; }
hr {
    border: 0;
    border-top: 1px solid #d7deea;
    margin: 22px 0;
}
table {
    width: 100%;
    border-collapse: collapse;
    margin: 10px 0 14px;
    font-size: 9pt;
    line-height: 1.45;
    page-break-inside: auto;
}
thead { display: table-header-group; }
tr { page-break-inside: avoid; break-inside: avoid; }
th, td {
    border: 1px solid #b8c3d6;
    padding: 3px 7px;
    vertical-align: top;
    text-align: left;
}
th {
    background: #eaf1fd;
    color: #16233a;
    font-weight: 600;
}
tbody tr:nth-child(even) { background: #fafcff; }
img {
    display: block;
    max-width: 100%;
    height: auto;
    margin: 12px auto 6px;
    border: 1px solid #d9e0eb;
    border-radius: 4px;
    page-break-inside: avoid;
    break-inside: avoid;
}
figure {
    margin: 12px 0 14px;
    page-break-inside: avoid;
    break-inside: avoid;
}
figure img { margin: 0 auto 6px; }
figcaption {
    text-align: center;
    color: #68758c;
    font-size: 9pt;
    line-height: 1.5;
    margin-top: 2px;
}
p > em:only-child {
    display: block;
    text-align: center;
    color: #68758c;
    font-size: 9pt;
    font-style: normal;
    margin: 2px 0 14px;
}
code {
    font-family: Consolas, "Cascadia Mono", monospace;
    font-size: 9pt;
    background: #f1f4f9;
    border: 1px solid #e0e6ef;
    border-radius: 3px;
    padding: 1px 4px;
}
pre {
    background: #f7f9fc;
    border: 1px solid #dde4ee;
    border-radius: 5px;
    padding: 10px 12px;
    line-height: 1.55;
    overflow-wrap: break-word;
    white-space: pre-wrap;
    page-break-inside: avoid;
}
pre code { background: none; border: 0; padding: 0; font-size: 8.6pt; }
"""

HTML_TEMPLATE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>{title}</title>
<base href="{base_href}">
<style>{css}</style>
</head>
<body>
{body}
</body>
</html>
"""

FIGURE_RE = re.compile(
    r"<p>\s*(<img[^>]*>)\s*</p>\s*<p>\s*<em>(.*?)</em>\s*</p>",
    re.DOTALL,
)


def bind_figures(body: str) -> str:
    """把「图片段落 + 斜体图注」合并成 figure。

    Markdown 会输出两个相邻的 <p>；若只对 img 设 break-inside: avoid，
    图注会被分页拆到下一页（实测图 3 出现过图注单独留在页首）。合并成
    figure 后整块不可拆，图注永远跟着图走。
    """
    return FIGURE_RE.sub(r"<figure>\1<figcaption>\2</figcaption></figure>", body)


def find_edge() -> Path:
    for path in EDGE_CANDIDATES:
        if path.is_file():
            return path
    raise FileNotFoundError("找不到 Edge：请确认已安装 Microsoft Edge")


def render_html(md_path: Path, html_path: Path, edge_safe_base: str) -> None:
    text = md_path.read_text(encoding="utf-8-sig")
    body = markdown.markdown(
        text,
        extensions=["tables", "fenced_code", "sane_lists"],
        output_format="html5",
    )
    body = bind_figures(body)
    title = md_path.stem
    html_doc = HTML_TEMPLATE.format(title=title, base_href=edge_safe_base, css=CSS, body=body)
    html_path.parent.mkdir(parents=True, exist_ok=True)
    html_path.write_text(html_doc, encoding="utf-8")


def print_pdf(edge: Path, html_path: Path, pdf_path: Path) -> subprocess.CompletedProcess:
    profile = Path(tempfile.gettempdir()) / "docshot_edge_profile"
    profile.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(edge),
        "--headless=new",
        "--disable-gpu",
        "--no-pdf-header-footer",
        "--run-all-compositor-stages-before-draw",
        "--virtual-time-budget=8000",
        f"--user-data-dir={profile}",
        f"--print-to-pdf={pdf_path}",
        html_path.as_uri(),
    ]
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=180,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def verify_pdf(pdf_path: Path) -> tuple[bool, str]:
    if not pdf_path.is_file():
        return False, "PDF 没有生成"
    size = pdf_path.stat().st_size
    if size <= MIN_PDF_BYTES:
        return False, f"PDF 只有 {size} 字节，≤ 50 KB"
    with pdf_path.open("rb") as handle:
        if handle.read(5) != b"%PDF-":
            return False, "文件头不是 %PDF-"
    return True, f"{size // 1024} KB"


def convert(md_name: str, edge: Path, keep_html: bool) -> bool:
    md_path = DOCS_DIR / md_name
    if not md_path.is_file():
        print(f"[FAIL] 缺少 {md_name}")
        return False
    pdf_path = md_path.with_suffix(".pdf")
    html_path = PREVIEW_DIR / (md_path.stem + ".html")

    print(f"[1/3] 渲染 HTML  {md_name}")
    render_html(md_path, html_path, DOCS_DIR.as_uri() + "/")

    print(f"[2/3] Edge 打印  {pdf_path.name}")
    try:
        proc = print_pdf(edge, html_path, pdf_path)
    except subprocess.TimeoutExpired:
        print("[FAIL] Edge 打印超时（180 秒）")
        return False
    if proc.returncode != 0:
        print(f"[FAIL] Edge 退出码 {proc.returncode}")
        print((proc.stderr or proc.stdout or "")[-1200:])
        return False

    ok, detail = verify_pdf(pdf_path)
    if not ok:
        print(f"[FAIL] {detail}")
        return False
    print(f"[3/3] 校验通过  {detail}")
    if keep_html:
        print(f"       预览 HTML 保留：{html_path}")
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="把 docs 下的培训文档转换成 PDF")
    parser.add_argument("files", nargs="*", help="只转换指定 Markdown；缺省转换三份")
    parser.add_argument("--keep-html", action="store_true", help="保留 HTML 预览文件")
    args = parser.parse_args(argv)

    try:
        edge = find_edge()
    except FileNotFoundError as exc:
        print(f"[FAIL] {exc}")
        return 1

    targets = args.files or DOCUMENTS
    failures = 0
    for name in targets:
        print("=" * 70)
        if not convert(name, edge, args.keep_html):
            failures += 1

    if not args.keep_html:
        shutil.rmtree(PREVIEW_DIR, ignore_errors=True)
        print("已清理 HTML 中间件：docs/_preview")

    print("=" * 70)
    if failures:
        print(f"结果：{failures} 份失败")
        return 1
    print(f"结果：{len(targets)} 份 PDF 全部生成并通过体积校验")
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
