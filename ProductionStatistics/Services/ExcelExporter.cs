using System;
using System.Globalization;
using System.IO;
using OfficeOpenXml;

namespace ProductionStatistics
{
    /// <summary>
    /// Excel 导出：把某一天的完整生产履历（白班+夜班各 12 行+合计）写成 xlsx。
    ///
    /// 用 EPPlus（OfficeOpenXml）生成，**电脑不需要安装 Office**（查看时需要有 Excel/WPS）。
    /// 文件内容与 WinForms 版完全一致：表头首列固定写"时段"（界面首列会随班次变，文件里必须稳定）。
    /// </summary>
    public static class ExcelExporter
    {
        /// <summary>
        /// 导出指定日期的履历到 xlsx。
        /// 行结构：行0 表头；行1~12 白班 12 小时；行13 白班合计；行14~25 夜班；行26 夜班合计。列结构与界面一致。
        /// </summary>
        public static void 导出(string 文件路径, DateTime 日期, DayRecord rec, string 设备名, string[] 列头)
        {
            object[,] 快照 = 生成快照(rec, 列头);
            using (var pkg = new ExcelPackage())
            {
                var ws = pkg.Workbook.Worksheets.Add("生产履历");
                ws.Cells[1, 1, 1, 10].Merge = true;
                ws.Cells[1, 1].Value = 设备名 + "  生产履历  " + 日期.ToString("yyyy-MM-dd");
                ws.Cells[1, 1].Style.Font.Bold = true;
                for (int r = 0; r < 快照.GetLength(0); r++)
                    for (int c = 0; c < 快照.GetLength(1); c++)
                        ws.Cells[r + 3, c + 1].Value = 快照[r, c];

                // 表头行与两行合计行加粗（行号按 12 行/班推导，改行数不用动这里）
                int 表头行 = 3;
                int 白班合计行 = 表头行 + ShiftStore.RowsPerShift + 1;      // 3 + 12 + 1 = 16
                int 夜班合计行 = 白班合计行 + ShiftStore.RowsPerShift + 1;  // 16 + 12 + 1 = 29
                ws.Cells[表头行, 1, 表头行, 10].Style.Font.Bold = true;
                ws.Cells[白班合计行, 1, 白班合计行, 10].Style.Font.Bold = true;
                ws.Cells[夜班合计行, 1, 夜班合计行, 10].Style.Font.Bold = true;
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                pkg.SaveAs(new FileInfo(文件路径));
            }
        }

        /// <summary>把一天两班的数据拷成二维表（纯内存快照）</summary>
        private static object[,] 生成快照(DayRecord rec, string[] 列头)
        {
            object[,] table = new object[27, 10];
            table[0, 0] = "时段";
            for (int c = 1; c < 10; c++) table[0, c] = 列头[c];
            string[] dayTitles = ShiftStore.ShiftTitles(true);
            string[] nightTitles = ShiftStore.ShiftTitles(false);
            int row = 1;
            写一个班次(table, ref row, "白班", dayTitles, rec.DayShift);
            写一个班次(table, ref row, "夜班", nightTitles, rec.NightShift);
            return table;
        }

        /// <summary>一个班次 12 行 + 合计行写入快照表；时间列保留分钟（1 位小数），比率列保留 2 位小数</summary>
        private static void 写一个班次(object[,] table, ref int row, string 班次名, string[] titles, HourStat[] stats)
        {
            double okSum = 0, ngSum = 0, prodSum = 0, waitSum = 0, alarmSum = 0, stopSum = 0;
            for (int i = 0; i < 12; i++)
            {
                HourStat s = stats[i];
                table[row, 0] = titles[i];
                table[row, 1] = s.TotalCount;
                table[row, 2] = s.OkCount;
                table[row, 3] = s.NgCount;
                table[row, 4] = (s.OkCount + s.NgCount) > 0 ? Math.Round(100.0 * s.OkCount / (s.OkCount + s.NgCount), 2) : 0d;
                table[row, 5] = Math.Round(s.ProductionSec / 60.0, 1);
                table[row, 6] = Math.Round(s.WaitSec / 60.0, 1);
                table[row, 7] = Math.Round(s.AlarmSec / 60.0, 1);
                table[row, 8] = Math.Round(s.StopSec / 60.0, 1);
                double totalSec = s.ProductionSec + s.WaitSec + s.AlarmSec + s.StopSec;
                table[row, 9] = totalSec > 0 ? Math.Round(100.0 * s.ProductionSec / totalSec, 2) : 0d;
                row++;
                okSum += s.OkCount; ngSum += s.NgCount;
                prodSum += s.ProductionSec; waitSum += s.WaitSec; alarmSum += s.AlarmSec; stopSum += s.StopSec;
            }
            table[row, 0] = 班次名 + "合计";
            table[row, 1] = (int)(okSum + ngSum);
            table[row, 2] = (int)okSum;
            table[row, 3] = (int)ngSum;
            table[row, 4] = (okSum + ngSum) > 0 ? Math.Round(100.0 * okSum / (okSum + ngSum), 2) : 0d;
            table[row, 5] = Math.Round(prodSum / 60.0, 1);
            table[row, 6] = Math.Round(waitSum / 60.0, 1);
            table[row, 7] = Math.Round(alarmSum / 60.0, 1);
            table[row, 8] = Math.Round(stopSum / 60.0, 1);
            double sumSec = prodSum + waitSum + alarmSum + stopSum;
            table[row, 9] = sumSec > 0 ? Math.Round(100.0 * prodSum / sumSec, 2) : 0d;
            row++;
        }
    }
}
