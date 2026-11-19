# -*- coding: utf-8 -*-
"""
文档核验配置（唯一事实源）
==========================

本文件集中登记「生产统计（WPF 0.0.2）」这次交付的真实边界：
  · 实际存在的功能 / 明确不存在的功能
  · 硬件品牌全集与实际配置（仅三菱 FX5U，无相机 / 光源 / 扫码枪）
  · 三份文档各自的角色、字数、表格、图片、必需章节
  · 客户两版（操作员版、客户技术工艺版）的禁用词与禁用章节
  · 截图白名单（防止把内部调试图误放进交付目录）

设计原则：
  1. 只登记「已经核实过」的事实。写文档前先跑本脚本，看到的就是现场实况。
  2. 禁词分两类：
       - FORBIDDEN_TERMS_*：客户两版出现即失败，没有任何例外；
       - NEGATION_REQUIRED_TERMS：功能边界词（MES / 配方 / 相机 …），
         允许出现在「不支持 / 未配置 / 不含」等否定语境里，防止文档吹牛。
  3. 本脚本既能独立运行（打印实况摘要），也能 --json 给 check_docs.py 消费。

用法：
    python check_config.py            # 打印现场配置与交付约束摘要
    python check_config.py --json     # 输出 JSON（check_docs.py 用）
    python check_config.py --strict   # 配置自检失败时返回非 0
"""

from __future__ import annotations

import argparse
import configparser
import json
import re
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# 路径与常量
# ---------------------------------------------------------------------------

TOOL_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = TOOL_DIR.parents[1]
WPF_DIR = PROJECT_ROOT / "ProductionStatistics"
CSProj = WPF_DIR / "ProductionStatistics.csproj"
INI_PATH = WPF_DIR / "bin" / "Debug" / "net472" / "生产统计.ini"
DOCS_DIR = PROJECT_ROOT / "docs"
IMAGES_DIR = DOCS_DIR / "images"

APP_NAME = "生产统计"
APP_NAME_EN = "Production Statistics"
TARGET = "点胶固化收料机"
DELIVERY_VERSION = "0.0.2"

# 三份文档：key 供 check_docs.py 引用，file 必须与实际文件名一致
DOCUMENTS = {
    "operator": {
        "file": "01_操作员版_生产统计.md",
        "title": "生产统计 · 操作员版",
        "audience": "产线操作员、班组长",
        "min_chinese_chars": 3500,
        "min_tables": 2,
        "min_images": 8,
        "need_opening_quote": True,
    },
    "customer": {
        "file": "02_客户技术工艺版_生产统计.md",
        "title": "生产统计 · 客户技术工艺版",
        "audience": "客户设备工程师、工艺工程师、信息化对接人",
        "min_chinese_chars": 7000,
        "min_tables": 6,
        "min_images": 3,
        "need_opening_quote": True,
    },
    "internal": {
        "file": "03_内部版_生产统计.md",
        "title": "生产统计 · 内部版",
        "audience": "我方售后、实施、开发工程师",
        "min_chinese_chars": 9000,
        "min_tables": 6,
        "min_images": 1,
        "need_opening_quote": True,
    },
}

DOC_ORDER = ["operator", "customer", "internal"]
CUSTOMER_ROLES = ("operator", "customer")  # 这两个版本面向客户现场

# ---------------------------------------------------------------------------
# 功能边界（已逐行核对源码与界面，2026-11-16）
# ---------------------------------------------------------------------------

FEATURES = {
    # 真实存在
    "plc_realtime_collect": True,       # 100 ms 轮询 MC 3E 二进制
    "three_unit_count": True,           # 焊接 / 气密 / 贴标 三单元 OK/NG/IDLE
    "twelve_hour_table": True,          # 每班 12 行小时明细 + 合计
    "six_shift_tabs": True,             # 今天/昨天/前天的白班+夜班
    "day_night_shift": True,            # 08:00 / 20:00 固定班次
    "auto_reset_signal": True,          # 9 个自锁位读 true 后写回 false
    "csv_persist": True,                # 每 30 秒整文件覆盖
    "csv_autoload": True,               # 启动载入今天/昨天/前天
    "excel_export": True,               # EPPlus，导出整天两班
    "bilingual_ui": True,               # 中文 / English 运行中切换
    "dark_light_theme": True,           # 深色 / 浅色
    "alarm_duration": True,             # 报警时长累计（分钟，1 位小数）
    "idle_duration": True,              # 待料时长累计
    "auto_pause_state": True,           # 设备自动中信号 → 运行 / 暂停
    "offline_detect": True,             # 通讯失败 → 离线
    "rolling_log": True,                # 按天日志，保留 30 天
    "error_log_mirror": True,           # ERROR 镜像到 错误日志.txt
    "single_instance_guard": False,     # 未实现！双开互相覆盖 CSV
    "login_permission": False,          # 无账号 / 权限 / 授权体系
    "mes_integration": False,
    "recipe_management": False,
    "database": False,                  # 纯 CSV 文件
    "camera_vision": False,
    "barcode_scanner": False,
    "spc_oee": False,
    "email_report": False,
    "cloud_remote": False,
    "multi_plc_brand": False,
    "plc_write_back_configurable": True,  # 不复位地址 可配置
}

HARDWARE = {
    "plc_brand": "三菱（Mitsubishi）",
    "plc_model": "FX5U",
    "plc_protocol": "MC 协议 3E 帧、二进制、TCP",
    "plc_port_default": 6060,
    "units": ["焊接", "气密", "贴标"],
    "camera_count": 0,
    "light_count": 0,
    "scanner_count": 0,
    "other_brands_supported": [],
}

# 关键运行参数：文档中引用时必须与 ini / 源码一致
RUNTIME = {
    "poll_interval_ms": 100,
    "signal_off_rounds": 2,
    "min_count_interval_ms": 1500,
    "csv_flush_seconds": 30,
    "memory_retain_days": 7,
    "log_retain_days": 30,
    "csv_columns": 14,
    "csv_lines_per_day": 25,      # 表头 + 白班 12 + 夜班 12
    "export_rows": 27,            # 表头 + 白班 12 + 白班合计 + 夜班 12 + 夜班合计
    "day_shift_start": 8,
    "night_shift_start": 20,
}

# 现场配置的期望值：ini 被截图脚本改过后若忘记还原，这里会报错
EXPECTED_INI = {
    "IP": "192.168.1.156",
    "Port": "6060",
    "轮询间隔Ms": "100",
    "信号确认OFF毫秒": "300",
    "计数最小间隔Ms": "1500",
    "设备名称": "点胶固化收料机",
    "语言": "中文",
    "主题": "浅色",
    "不复位地址": "",  # 注释状态 / 空值都算未启用
}

PLC_ADDRESS_KEYS = [
    "设备自动中", "设备报警",
    "焊接OK", "焊接NG", "焊接IDLE",
    "气密OK", "气密NG", "气密IDLE",
    "贴标OK", "贴标NG", "贴标IDLE",
]

# ---------------------------------------------------------------------------
# 截图白名单
# ---------------------------------------------------------------------------

# 交付目录只允许这些图片；多一张少一张都报错，防止内部调试图混入
IMAGE_FILES = [
    "01-main-today-day.png",
    "02-main-yesterday-day.png",
    "03-main-yesterday-night.png",
    "04-main-english.png",
    "05-main-dark.png",
    "06-main-alarm.png",
    "07-main-idle.png",
    "08-main-offline.png",
    "09-export-save-dialog.png",
    "10-export-success.png",
]

# 每张图至少被一份文档引用
MUST_REFERENCE_IMAGES = list(IMAGE_FILES)

# 仅内部版可用的图：保存对话框左侧会露出本机目录结构，客户两版不得引用
DEV_ONLY_IMAGES = ["09-export-save-dialog.png"]

# ---------------------------------------------------------------------------
# 客户两版禁词
# ---------------------------------------------------------------------------

# 内部机制 / 工具 / 源码细节：客户两版（操作员版、技术工艺版）一律不得出现
FORBIDDEN_TERMS_CUSTOMER = [
    # 本项目内部工具链
    "DocShot", "docshot", "FakePlc", "fakeplc", "seed_data", "postprocess",
    "PrintWindow", "SetProcessDPIAware", "UI Automation", "UIAutomation",
    "docshot_export", "backup_2026",
    # 内部机制描述
    "假 PLC", "假PLC", "模拟 PLC", "模拟PLC", "仿真 PLC", "仿真PLC",
    "种子数据", "演示种子", "开发者面板", "开发面板", "最高权限",
    "发码", "后门", "内部工具", "内部脚本", "截图脚本", "截图工具",
    # 源码 / 实现细节
    "源码", "源代码", "代码行", "行号", "反编译", "ILSpy", "dnSpy",
    "HslCommunication", "EPPlus", "App.xaml", "MainWindow.xaml",
    ".cs 文件", ".cs文件", "类图", "调用栈",
    # 容易引起误解的内部路径
    "bin\\Debug", "bin/Debug", "obj\\", "AppData\\Local\\Temp",
]

# 客户技术版额外禁词：不得把「我方内部排障口径」写成客户操作步骤
FORBIDDEN_TERMS_CUSTOMER_ONLY = [
    "回炉", "甩锅", "踩坑", "坑点", "私活", "临时补丁", "硬编码位置",
]

# 客户两版禁止的章节标题关键词（内部实现 / 开发过程）
FORBIDDEN_HEADING_WORDS_CUSTOMER = [
    "源码", "代码实现", "技术实现", "实现细节", "内部版", "开发者",
    "调试", "编译", "构建", "部署脚本", "文件结构", "行号", "类图",
    "已知限制（内部）", "自查回炉",
]

# 功能边界词：客户两版允许出现，但必须在否定 / 限定语境里，防止写成「已支持」
NEGATION_REQUIRED_TERMS = [
    "MES", "配方", "数据库", "OEE", "SPC",
    "相机", "视觉检测", "光源", "扫码枪", "条码枪", "二维码",
    "用户权限", "账号权限", "权限体系", "审计日志",
    "云平台", "远程监控", "邮件报表", "多品牌",
    "西门子", "汇川", "欧姆龙", "台达", "基恩士", "松下", "施耐德", "倍福", "罗克韦尔",
]

NEGATION_WORDS = [
    "不", "无", "未", "没有", "非", "仅", "只", "缺", "禁止", "另行",
    "不含", "不具备", "不提供", "不支持", "不包含", "不涉及", "不设",
    "单一", "独立", "除外", "之外",
]

# 三份文档通用禁词：未完成标记 / 占位符 / 截断痕迹
FORBIDDEN_TERMS_ALL = [
    "truncated", "[截断]", "内容截断", "输出截断",
    "TODO", "TBD", "FIXME", "待补充", "此处省略", "略去", "Lorem ipsum",
]

# 三份文档都必须写清的交付版本
REQUIRED_VERSION = DELIVERY_VERSION

# ---------------------------------------------------------------------------
# 必需章节（按标题子串匹配，写作前先看这里）
# ---------------------------------------------------------------------------

REQUIRED_HEADINGS = {
    "operator": [
        "这台设备是做什么的", "开机", "界面", "班次", "报警", "待料",
        "离线", "导出", "交接班", "禁止", "常见问题",
    ],
    "customer": [
        "系统组成", "权限", "界面", "班次", "统计口径", "PLC", "参数",
        "换型", "数据", "导出", "语言", "排障", "联系", "自查",
    ],
    "internal": [
        "系统全景", "文件", "权限", "界面刷新", "配置", "采集", "存储",
        "导出", "日志", "故障", "交付", "技术实现", "已知限制", "自查回炉",
    ],
}

# 内部版必须给出源码定位（文件:行号），至少这么多处
MIN_CODE_REFERENCES = 8
CODE_REF_PATTERN = r"[\w\u4e00-\u9fa5]+\.(?:cs|xaml|csproj|ini)[:：]\d+"

# 客户技术工艺版必须把 11 个 PLC 地址键全部列清
CUSTOMER_MUST_LIST_PLC_KEYS = True
CUSTOMER_MUST_LIST_BRANDS = True

# ---------------------------------------------------------------------------
# 读取现场配置
# ---------------------------------------------------------------------------


def _read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def read_ini() -> dict:
    """读取 ini 的现场实况（保留中文键名，不做大小写折叠）。"""
    parser = configparser.ConfigParser(
        interpolation=None,
        allow_no_value=True,
        comment_prefixes=("#",),
        inline_comment_prefixes=None,
    )
    parser.optionxform = str  # 保留键名大小写（IP / Port 无所谓，中文键必须原样）
    parser.read_string(_read_text(INI_PATH))
    result = {}
    for section in parser.sections():
        result[section] = {k: (v if v is not None else "") for k, v in parser.items(section)}
    # configparser 会把「# 不复位地址=」整行当注释丢掉；显式判断是否被启用
    text = _read_text(INI_PATH)
    enabled = re.search(r"^\s*不复位地址\s*=\s*(\S.*)$", text, re.MULTILINE)
    result.setdefault("PLC地址", {})["不复位地址"] = enabled.group(1).strip() if enabled else ""
    return result


def read_project_version() -> str:
    text = _read_text(CSProj)
    match = re.search(r"<Version>([^<]+)</Version>", text)
    return match.group(1).strip() if match else ""


def read_ini_status() -> dict:
    """把现场配置与期望值逐项比对，返回 {'ok': bool, 'items': [...]}。"""
    try:
        ini = read_ini()
    except Exception as exc:  # noqa: BLE001 - 自检要报告原因，不能直接崩
        return {"ok": False, "items": [{"key": "(读取 ini)", "actual": repr(exc), "expected": "可读"}]}
    flat = {}
    for section in ini.values():
        flat.update(section)
    items = []
    for key, expected in EXPECTED_INI.items():
        actual = flat.get(key, "")
        actual_norm = "" if actual is None else str(actual).strip()
        expected_norm = "" if expected is None else str(expected).strip()
        items.append({
            "key": key,
            "actual": actual_norm,
            "expected": expected_norm,
            "ok": actual_norm == expected_norm,
        })
    return {"ok": all(item["ok"] for item in items), "items": items}


def read_source_constants() -> dict:
    """抽查源码里的关键常量，防止「配置改了、文档没改」的漂移。"""
    checks = []

    def check(rel_path: str, pattern: str, label: str) -> None:
        path = PROJECT_ROOT / rel_path
        try:
            text = _read_text(path)
            ok = re.search(pattern, text) is not None
        except Exception as exc:  # noqa: BLE001
            checks.append({"label": label, "file": rel_path, "ok": False, "error": repr(exc)})
            return
        checks.append({"label": label, "file": rel_path, "ok": ok, "error": ""})

    check("ProductionStatistics/Services/StatisticsStore.cs", r"7", "内存保留 7 天")
    check("ProductionStatistics/Services/StatisticsStore.cs", r"30", "CSV 30 秒落盘")
    check("ProductionStatistics/Business/ShiftStore.cs", r"RowsPerShift", "每班 12 行")
    check("ProductionStatistics/Business/Log.cs", r"30", "日志保留 30 天")
    check("ProductionStatistics/Services/ExcelExporter.cs", r"27", "导出 27 行")
    return {"ok": all(item["ok"] for item in checks), "items": checks}


def snapshot() -> dict:
    version = read_project_version()
    ini = read_ini()
    ini_status = read_ini_status()
    source = read_source_constants()
    return {
        "generated_from": str(PROJECT_ROOT),
        "app": {"name": APP_NAME, "name_en": APP_NAME_EN, "target": TARGET, "version": version},
        "version_matches": version == DELIVERY_VERSION,
        "features": FEATURES,
        "hardware": HARDWARE,
        "runtime": RUNTIME,
        "ini": ini,
        "ini_status": ini_status,
        "source_constants": source,
        "documents": DOCUMENTS,
        "image_files": IMAGE_FILES,
        "must_reference_images": MUST_REFERENCE_IMAGES,
        "dev_only_images": DEV_ONLY_IMAGES,
        "forbidden_terms_all": FORBIDDEN_TERMS_ALL,
        "forbidden_terms_customer": FORBIDDEN_TERMS_CUSTOMER,
        "forbidden_terms_customer_only": FORBIDDEN_TERMS_CUSTOMER_ONLY,
        "forbidden_heading_words_customer": FORBIDDEN_HEADING_WORDS_CUSTOMER,
        "negation_required_terms": NEGATION_REQUIRED_TERMS,
        "negation_words": NEGATION_WORDS,
        "required_headings": REQUIRED_HEADINGS,
        "plc_address_keys": PLC_ADDRESS_KEYS,
        "required_version": REQUIRED_VERSION,
        "min_code_references": MIN_CODE_REFERENCES,
        "code_ref_pattern": CODE_REF_PATTERN,
        "images_dir": str(IMAGES_DIR),
        "docs_dir": str(DOCS_DIR),
    }


# ---------------------------------------------------------------------------
# 独立运行
# ---------------------------------------------------------------------------


def _print_summary(data: dict) -> None:
    print("=" * 74)
    print("生产统计 文档核验配置 · 现场实况")
    print("=" * 74)
    print(f"工程目录    ：{data['generated_from']}")
    print(f"交付版本    ：{data['app']['version']}（期望 {DELIVERY_VERSION}）")
    print(f"目标设备    ：{data['app']['target']}")

    ini = data["ini"]
    plc = ini.get("PLC", {})
    ui = ini.get("界面", {})
    print(f"PLC 连接    ：{plc.get('IP', '?')}:{plc.get('Port', '?')}"
          f"，轮询 {plc.get('轮询间隔Ms', '?')} ms")
    print(f"设备名称    ：{ui.get('设备名称', '?')} / {ui.get('设备名称En', '?')}")
    print(f"界面        ：语言={ui.get('语言', '?')}，主题={ui.get('主题', '?')}")
    print(f"硬件实配    ：{HARDWARE['plc_brand']} {HARDWARE['plc_model']}，"
          f"相机 {HARDWARE['camera_count']}，光源 {HARDWARE['light_count']}，"
          f"扫码枪 {HARDWARE['scanner_count']}")

    print("-" * 74)
    print("现场配置自检：", "通过" if data["ini_status"]["ok"] else "失败")
    for item in data["ini_status"]["items"]:
        if not item["ok"]:
            print(f"  ! {item['key']}：实际={item['actual']!r} 期望={item['expected']!r}")

    print("源码常量自检：", "通过" if data["source_constants"]["ok"] else "失败")
    for item in data["source_constants"]["items"]:
        if not item["ok"]:
            print(f"  ! {item['label']}（{item['file']}）")

    print("-" * 74)
    print("三份文档：")
    for role in DOC_ORDER:
        doc = data["documents"][role]
        print(f"  [{role}] {doc['file']}")
        print(f"         面向 {doc['audience']}；"
              f"≥{doc['min_chinese_chars']} 汉字 / ≥{doc['min_tables']} 表 / "
              f"≥{doc['min_images']} 图")
    print(f"截图白名单：{len(IMAGE_FILES)} 张，全部至少被引用一次")
    print(f"客户两版禁词：{len(FORBIDDEN_TERMS_CUSTOMER)} 个；"
          f"功能边界词：{len(NEGATION_REQUIRED_TERMS)} 个（需否定语境）")
    print("=" * 74)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="生产统计文档核验配置 / 现场实况")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--strict", action="store_true", help="配置自检失败时返回非 0")
    args = parser.parse_args(argv)

    data = snapshot()
    if args.json:
        json.dump(data, sys.stdout, ensure_ascii=False, indent=2)
        sys.stdout.write("\n")
    else:
        _print_summary(data)

    if args.strict and not (data["version_matches"] and data["ini_status"]["ok"]
                            and data["source_constants"]["ok"]):
        return 1
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
