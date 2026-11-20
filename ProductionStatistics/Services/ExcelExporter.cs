using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OfficeOpenXml;

namespace ProductionStatistics
{
    /// <summary>
    /// Excel 导出【V2.4.0】：把历史查询面板的当前查询结果（日期区间 × 班次 × 小时行，
    /// 与查询表格所见一致）写成 xlsx。
    ///
    /// 做什么（口径变化说明）：
    ///   旧版是"顶栏导出 Excel"——按单个班次日期导出整天 27 行固定格式；
    ///   V2.4.0 顶栏按钮改为"设置"下拉后，Excel 导出收拢进历史查询面板，
    ///   导出口径随之改为"当前查询结果"（界面看到什么就导什么，与 CSV 导出同口径）。
    ///
    /// 用 EPPlus（OfficeOpenXml）生成，**电脑不需要安装 Office**（查看时需要有 Excel/WPS）。
    /// 数值列写真正的数字（Excel 可直接求和/筛选），比率列保留 2 位小数、时间列保留 1 位。
    /// </summary>
    public static class ExcelExporter
    {
        /// <summary>
        /// 导出查询结果到 xlsx。
        /// 行结构：行 1 合并标题（设备名 + 查询区间 + 单元口径）；行 2 表头（加粗）；行 3 起数据。
        /// 列头由调用方传入（取查询表格当前显示列头，随界面语言）。
        /// </summary>
        /// <param name="文件路径">另存为对话框选定的 xlsx 路径</param>
        /// <param name="行">查询结果行快照（查询行 集合的拷贝，写文件期间采集线程不受影响）</param>
        /// <param name="列头">列头文字（已去换行），长度应与行数据的列数一致</param>
        /// <param name="设备名">设备显示名（随界面语言）</param>
        /// <param name="单元名">单元筛选口径文字（"全部单元"/"焊接单元"/…）</param>
        public static void 导出查询结果(string 文件路径, List<QueryRow> 行, string[] 列头, string 设备名, string 单元名)
        {
            using (var pkg = new ExcelPackage())
            {
                var ws = pkg.Workbook.Worksheets.Add("生产履历查询");
                ws.Cells[1, 1, 1, 列头.Length].Merge = true;
                ws.Cells[1, 1].Value = 设备名 + "  生产履历查询  " + 单元名;
                ws.Cells[1, 1].Style.Font.Bold = true;

                // 表头（第 2 行）
                for (int c = 0; c < 列头.Length; c++)
                    ws.Cells[2, c + 1].Value = 列头[c];
                ws.Cells[2, 1, 2, 列头.Length].Style.Font.Bold = true;

                // 数据行（第 3 行起）：日期/班次/时段为文字，数值列写数字（写不进数字的兜底写字符串）
                int r = 3;
                foreach (QueryRow row in 行)
                {
                    ws.Cells[r, 1].Value = row.日期;
                    ws.Cells[r, 2].Value = row.班次;
                    ws.Cells[r, 3].Value = row.时段;
                    ws.Cells[r, 4].Value = row.Total;   // 小时产能：int 直写
                    ws.Cells[r, 5].Value = row.Ok;      // OK 产能：int 直写
                    ws.Cells[r, 6].Value = row.Ng;      // NG 产能：int 直写
                    写数值(ws.Cells[r, 7], row.Yield);        // 良率%："87.50" → 87.5
                    写数值(ws.Cells[r, 8], row.Production);   // 生产时间(分钟)
                    写数值(ws.Cells[r, 9], row.Wait);         // 待料时间(分钟)
                    写数值(ws.Cells[r, 10], row.Alarm);       // 报警时间(分钟)
                    写数值(ws.Cells[r, 11], row.Stop);        // 停止时间(分钟)
                    写数值(ws.Cells[r, 12], row.Utilization); // 稼动率%
                    r++;
                }
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                pkg.SaveAs(new FileInfo(文件路径));
            }
        }

        /// <summary>
        /// 把格式化好的数字字符串写回真正的数值单元格（InvariantCulture 解析，
        /// 与 QueryRow 生成字符串的口径一致；解析失败保持原字符串，不让导出中断）。
        /// </summary>
        private static void 写数值(OfficeOpenXml.ExcelRange 单元格, string 文字)
        {
            double 数值;
            if (double.TryParse(文字, NumberStyles.Float, CultureInfo.InvariantCulture, out 数值))
                单元格.Value = 数值;
            else
                单元格.Value = 文字;
        }
    }
}
