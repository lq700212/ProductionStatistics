#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成生产统计软件的演示种子数据（三天 CSV）。

用途：文档截图时让界面有真实的班次履历，不是 P 图——软件启动会按自己的
ShiftStore.Load 逻辑读回这些 CSV，继续累计、继续落盘，界面数字全部来自真实数据通路。

格式（与 ShiftStore 完全一致）：
  表头 14 列 + 白班 12 行 + 夜班 12 行；时间单位分钟，1 位小数；UTF-8 带 BOM。
  软件读取时只认「分单元 OK/NG」与四类时间，合计列由软件自己算，这里写对是为了文件可读。

用法：
  python seed_data.py --out "…\\bin\\Debug\\net472\\数据" [--today 2026-11-16]
"""
import argparse
import datetime as dt
import os

HEADER = ["时段", "小时产能(PCS)", "OK产能(PCS)", "NG产能(PCS)",
          "焊接OK", "焊接NG", "气密OK", "气密NG", "贴标OK", "贴标NG",
          "生产时间(Min)", "待料时间(Min)", "报警时间(Min)", "停止时间(Min)"]

OK_RATIO = (0.36, 0.33, 0.31)
NG_RATIO = (0.40, 0.35, 0.25)


def split(total, ratios):
    a = int(total * ratios[0])
    b = int(total * ratios[1])
    c = total - a - b
    return [a, b, c]


def row(ok, ng, prod, wait, alarm, stop):
    ok3 = split(ok, OK_RATIO)
    ng3 = split(ng, NG_RATIO)
    return [ok + ng, ok, ng,
            ok3[0], ng3[0], ok3[1], ng3[1], ok3[2], ng3[2],
            round(prod, 1), round(wait, 1), round(alarm, 1), round(stop, 1)]


def titles(day_shift):
    out = []
    for i in range(12):
        start = (8 if day_shift else 20) + i
        out.append("%02d:00-%02d:00" % (start % 24, (start + 1) % 24))
    return out


def normal_rows(values):
    return [row(v[0], v[1], 55.0, 2.5, 1.0, 1.5) for v in values]


def write_day(path, day_rows, night_rows):
    lines = [",".join(HEADER)]
    for i, title in enumerate(titles(True)):
        lines.append(",".join(map(str, [title] + day_rows[i])))
    for i, title in enumerate(titles(False)):
        lines.append(",".join(map(str, [title] + night_rows[i])))
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        f.write("\r\n".join(lines) + "\r\n")


def zero_row():
    return [0] * 14


def demo_today(today):
    """今天：白班完整；夜班按真实时间，只填已经结束/正在进行的小时（未来小时为 0）。"""
    day = [
        row(590, 15, 55.0, 2.5, 1.0, 1.5),
        row(620, 12, 56.5, 2.0, 0.5, 1.0),
        row(615, 14, 56.5, 2.0, 0.5, 1.0),
        row(600, 15, 55.0, 2.5, 1.0, 1.5),
        row(300, 8, 32.0, 5.0, 0.0, 23.0),      # 12:00-13:00 午餐停机
        row(410, 25, 42.0, 3.0, 12.0, 3.0),      # 13:00-14:00 报警
        row(625, 12, 56.5, 2.0, 0.5, 1.0),
        row(610, 14, 56.5, 2.0, 0.5, 1.0),
        row(595, 16, 55.0, 2.5, 1.0, 1.5),
        row(585, 15, 55.0, 2.5, 1.0, 1.5),
        row(570, 14, 54.5, 3.0, 1.0, 1.5),
        row(540, 12, 54.0, 3.0, 1.5, 1.5),
    ]

    night = [zero_row() for _ in range(12)]
    now = dt.datetime.now()
    # 夜班 20:00 起，归属今天；只填到今天实际经过的小时
    for i in range(12):
        start = dt.datetime.combine(today, dt.time(20, 0)) + dt.timedelta(hours=i)
        end = start + dt.timedelta(hours=1)
        if now >= end:
            # 已结束的小时：完整 60 分钟
            if i == 0:
                night[i] = row(580, 18, 55.0, 2.5, 1.0, 1.5)
            else:
                night[i] = row(560, 16, 55.0, 2.5, 1.0, 1.5)
        elif now > start:
            # 正在进行的小时：按已过分钟折算，界面看起来就是"这一小时刚开始"
            mins = max(1.0, (now - start).total_seconds() / 60.0)
            pieces = int(min(600, mins * 10))
            ok = int(pieces * 0.97)
            ng = pieces - ok
            prod = mins * 0.92
            wait = mins * 0.04
            stop = mins - prod - wait
            night[i] = row(ok, ng, prod, wait, 0.0, max(0.0, stop))
        else:
            night[i] = zero_row()

    return day, night


def demo_other(seed, day_alarm_hour=None):
    """昨天/前天：两个班次都完整。数字随 seed 微调，避免三天一模一样。"""
    day = []
    night = []
    for i in range(12):
        ok = 585 - seed * 10 + ((i * 7) % 25)
        ng = 14 + (i % 4)
        if day_alarm_hour is not None and i == day_alarm_hour:
            day.append(row(420, 22, 44.0, 3.0, 10.0, 3.0))
        elif i == 4:
            day.append(row(320 + seed * 5, 9, 34.0, 5.0, 0.0, 21.0))
        else:
            day.append(row(ok, ng, 55.0, 2.5, 1.0, 1.5))
        okn = 555 - seed * 8 + ((i * 5) % 22)
        ngn = 13 + (i % 3)
        night.append(row(okn, ngn, 54.5, 3.0, 1.0, 1.5))
    return day, night


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--today", default=dt.date.today().isoformat())
    args = ap.parse_args()

    today = dt.date.fromisoformat(args.today)
    os.makedirs(args.out, exist_ok=True)

    three = [today, today - dt.timedelta(days=1), today - dt.timedelta(days=2)]
    for idx, d in enumerate(three):
        if idx == 0:
            day, night = demo_today(today)
        elif idx == 1:
            day, night = demo_other(1, day_alarm_hour=2)
        else:
            day, night = demo_other(2, day_alarm_hour=None)
        path = os.path.join(args.out, "生产履历_%s.csv" % d.isoformat())
        write_day(path, day, night)
        print("WROTE " + path)


if __name__ == "__main__":
    main()
