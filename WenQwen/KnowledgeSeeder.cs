// ============================================================
// 温启志  18719361399
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace QwenConsole
{
    /// <summary>
    /// 资料文件夹为空时，自动落一整套运动控制示例资料（txt / docx / pdf / xlsx）。
    /// 目的有两个：知识库开箱就有内容，同时演示“一个主题一个文件、文件名带关键词”的写法。
    /// 同名文件会被覆盖，用户自己新增的资料一律不动。
    /// </summary>
    internal static class KnowledgeSeeder
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 生成整套示例资料，返回成功写入的文件名。
        /// </summary>
        internal static List<string> Seed(string folder)
        {
            Directory.CreateDirectory(folder);
            var made = new List<string>();

            Try(folder, "使用说明.txt", made, path =>
                File.WriteAllText(path, ReadmeLines(), Utf8NoBom));

            Try(folder, "运动控制基础术语.txt", made, path =>
                File.WriteAllText(path, GlossaryLines(), Utf8NoBom));

            Try(folder, "SV-2000 伺服轴调试手册.docx", made, path =>
                WriteDocx(path, "SV-2000 伺服轴调试手册", ManualLines()));

            Try(folder, "SV-2000 报警代码处理.pdf", made, path =>
                WritePdf(path, "SV-2000 驱动器报警代码与处理", AlarmLines()));

            Try(folder, "气缸与IO点位表.xlsx", made, path =>
                WriteXlsx(path, "IO点位", IoHeader(), IoRows()));

            return made;
        }

        private static void Try(string folder, string name, List<string> made, Action<string> write)
        {
            string path = Path.Combine(folder, name);
            try
            {
                write(path);
                made.Add(name);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   生成 {name} 失败：{ex.Message}");
                Console.ResetColor();
            }
        }

        // ==================== 内容 ====================

        private static string ReadmeLines() => string.Join(Environment.NewLine, new[]
        {
            "资料文件夹使用说明",
            "================",
            "",
            "这里是本机的离线知识库。程序启动时读取本文件夹（含子文件夹）里的资料，抽取文字后分块，回答问题前先检索这里。",
            "支持的格式：txt、md、csv、docx、doc、xlsx、xls、pdf。不支持的格式：扫描件 PDF（没有文字层）、chm、图片、网页文件。",
            "",
            "本次已自动生成 5 个示例文件，覆盖四种常用格式，可以直接提问试效果：",
            "  1. 运动控制基础术语.txt     —— 术语与名词解释",
            "  2. SV-2000 伺服轴调试手册.docx —— 参数设置与调试步骤",
            "  3. SV-2000 报警代码处理.pdf  —— 报警代码、原因与处理办法",
            "  4. 气缸与IO点位表.xlsx       —— 输入输出点位定义",
            "  5. 使用说明.txt（本文件）     —— 格式支持与写法建议",
            "",
            "换成自己的资料时的写法建议：",
            "1. 一个主题一个文件，文件名直接写设备或主题名，例如“AL-51 报警处理.txt”，提问时更容易命中。",
            "2. 表格类资料尽量用 xlsx，或转成文本时保留表头、每行写成“列名=值”，否则容易看串列。",
            "3. 处理步骤按顺序编号，一条一步，不要整段抄录说明书。",
            "4. 页眉页脚、目录页码可以留着，程序会跳过重复行和过短的碎片。",
            "",
            "这 5 个文件都是示例，放入自己的资料后可以整批删除。需要重新生成时执行：WenQwen.exe --seed-knowledge"
        });

        private static string GlossaryLines() => string.Join(Environment.NewLine, new[]
        {
            "运动控制基础术语说明",
            "==================",
            "",
            "伺服系统：由伺服驱动器、伺服电机和编码器组成的闭环驱动系统。驱动器接收上位机的脉冲或总线指令，控制电机的位置、速度和转矩。",
            "",
            "编码器：装在电机尾部的位置反馈元件。增量式编码器输出 A/B/Z 三相脉冲，Z 相每转输出一个零位信号；绝对值编码器直接输出多圈绝对位置，断电后不需重新回零。",
            "",
            "脉冲当量：上位机每发一个脉冲，负载实际移动的距离。例如丝杆导程 5mm、电机每转 10000 脉冲、直连无减速时，脉冲当量 = 5 / 10000 = 0.0005mm，即 0.5 微米每脉冲。",
            "",
            "电子齿轮比：用来匹配“上位机指令脉冲数”和“电机实际转一圈所需脉冲数”的比例。SV-2000 中由 Pr0.09 设定分子、Pr0.10 设定分母；例如分子 4、分母 1 表示上位机发 2500 个脉冲电机转一圈（每转脉冲数 Pr0.08 = 10000，即一万个脉冲）。",
            "",
            "回零（原点回归）：让轴寻找机械原点并建立坐标系的动作。常见方式为“先快速找原点开关，再反向低速找 Z 相”，保证每次停在同一位置。回零完成后当前位置清零。",
            "",
            "硬限位与软限位：硬限位是装在行程两端的物理开关，触发后必须断使能或反向退出；软限位是参数里设定的坐标范围，控制器在到达边界前主动减速停止，属于保护性限制。",
            "",
            "加减速时间：速度从 0 升到额定速度（或从额定降到 0）所用的时间。时间过短会过流或丢步，过长会影响节拍。SV-2000 的位置控制默认加减速时间为 150ms。",
            "",
            "三环控制：位置环在最外层，输入是位置偏差，输出是速度指令；速度环在中间，输出是转矩指令；电流环在最内层，直接控制电机电流。位置环增益决定跟随误差大小，速度环增益决定抗扰动能力。",
            "",
            "惯量比：负载折算到电机轴的转动惯量与电机自身惯量之比，用百分数表示。惯量比越大，同样的增益越容易振荡，需要相应降低增益或加装减速机。",
            "",
            "多段位置运行：把若干目标位置、速度、加减速时间预先写入点位表，由输入信号或通讯指令按序号调用，实现简单的点位搬运流程，不依赖上位机逐点下发。",
            "",
            "插补：多轴协同运动时，控制器按直线或圆弧规律同时分配各轴脉冲，使末端走出一条连续轨迹。两轴直线插补时，合成速度为各轴速度的矢量和。"
        });

        private static string[] ManualLines() => new[]
        {
            "## 适用范围",
            "本手册适用于 SV-2000 系列交流伺服驱动器配 750W 增量式编码器电机的单轴调试，控制方式为位置控制（脉冲+方向）。",
            "",
            "## 调试前检查",
            "1. 确认主回路 L1、L2 供电为单相 220V，接线端子拧紧，PE 可靠接地。",
            "2. 确认电机动力线 U、V、W 与编码器线分开走线，编码器线使用屏蔽双绞线，屏蔽层单端接地。",
            "3. 手动盘车确认机构无卡阻，限位开关动作正常，急停按钮可断开使能。",
            "4. 上位机脉冲输出方式设为“脉冲+方向”，脉冲电平 24V，最大脉冲频率不超过 500kHz。",
            "",
            "## 关键参数",
            "Pr0.01 控制模式 = 0，位置控制模式。",
            "Pr0.08 电机每转脉冲数 = 10000（一万），即上位机发 10000 个脉冲电机转一圈。",
            "Pr0.09 电子齿轮比分子 = 4。",
            "Pr0.10 电子齿轮比分母 = 1。分子除以分母即 4 倍，配合 Pr0.08 后每转实际指令脉冲为 2500。",
            "Pr1.01 速度环增益 = 180Hz，出厂值 120Hz，惯量比小于 300% 时可提到 180Hz。",
            "Pr1.02 位置环增益 = 45 rad/s，跟随误差偏大时提高，出现停止抖动时降低。",
            "Pr1.03 加减速时间 = 150ms，节拍紧张时可降到 100ms，但不得低于 60ms。",
            "Pr1.04 最大速度 = 3000 r/min，与电机额定转速一致，不要超过。",
            "Pr1.05 惯量比 = 250%，按实际负载填写，用于内部前馈补偿。",
            "Pr2.01 到位允差 = 20 个脉冲，即 0.01mm（脉冲当量 0.0005mm）。",
            "Pr2.02 定位完成输出延时 = 10ms，避免到位信号抖动。",
            "",
            "## 调试步骤",
            "1. 断开使能，只给控制电源，用面板 JOG 以 200 r/min 正反各转 3 秒，确认电机转向与编码器反馈方向一致。",
            "2. 按上表写入参数，断电重启使参数生效，读回确认无写入失败项。",
            "3. 执行回零：先以 50mm/s 找原点开关，再以 5mm/s 反向找 Z 相，回零完成后当前位置显示 0.000。",
            "4. 用手轮以 10% 倍率点动，检查软限位是否在设定坐标处提前停止。",
            "5. 下发单点定位指令，从 0 走到 100.000mm，用百分表测量实际位移，偏差应小于 0.02mm。",
            "6. 连续运行 30 分钟，观察驱动器温度不超过 55℃，位置偏差不累积。",
            "",
            "## 常见现象与处理",
            "低速爬行：多为速度环增益偏低或机械润滑不足，先把 Pr1.01 提高 20Hz 观察，再检查导轨润滑。",
            "停止时过冲：位置环增益过高，把 Pr1.02 从 45 降到 35 rad/s，同时确认 Pr2.01 到位允差不要设得过小。",
            "运行时异响：检查惯量比是否与实际负载偏差过大，Pr1.05 按实测值填写；仍异响则降低 Pr1.01。",
            "跟随误差报警：加速时间过短或负载过重，先加长 Pr1.03 到 200ms，再看报警是否消失。",
            "回零重复精度差：原点开关触发位置抖动，改用 Z 相定位，并把找原点速度降到 5mm/s 以下。"
        };

        private static string[] AlarmLines() => new[]
        {
            "SV-2000 驱动器报警代码与处理",
            "",
            "## AL-10 过流",
            "现象：使能瞬间或运行中报 AL-10，驱动器切断输出。",
            "原因：电机动力线短路或对地短路、功率模块损坏、加速时间过短导致冲击电流过大。",
            "处理：断电测量 U、V、W 之间及对地电阻，正常应为兆欧级；加速时间加长到 200ms 以上再试；仍报警则更换驱动器。",
            "",
            "## AL-21 过载",
            "现象：运行一段时间后报 AL-21，驱动器温度升高。",
            "原因：负载超过电机额定转矩、机械卡阻、惯量比设置偏小、散热风扇停转。",
            "处理：手动盘车确认无卡阻；核对 Pr1.05 惯量比与实际负载；清理散热片并确认风扇运转；必要时换大一级电机。",
            "",
            "## AL-33 主回路欠压",
            "现象：上电即报 AL-33，面板显示母线电压低于 200V。",
            "原因：输入电源缺相或电压偏低、主回路接线端子松动、内部软启动电阻未短接。",
            "处理：测量 L1、L2 输入电压应在 220V±10%；紧固端子；断电等待 5 分钟后重新上电。",
            "",
            "## AL-51 编码器通讯中断",
            "现象：使能后立即报 AL-51，电机不转，面板显示位置反馈为 0 或乱跳。",
            "原因：编码器线缆断线或插头未插紧、屏蔽层未接地导致干扰、编码器供电 5V 异常、编码器本体损坏。",
            "处理：重新插拔编码器插头并锁紧；用万用表逐芯通断检查 A、B、Z 与 5V、0V；确认屏蔽层单端接地且与动力线分开走线；测量编码器供电应在 5V±5%，异常则更换驱动器或电机。",
            "",
            "## AL-60 位置超差",
            "现象：定位过程中报 AL-60，轴停止但未到位。",
            "原因：位置偏差超过 Pr2.03 设定值、增益过低、负载突变、机械打滑。",
            "处理：检查 Pr2.03 位置超差阈值是否设得过小；把 Pr1.02 位置环增益提高到 45 rad/s；检查联轴器是否松动。",
            "",
            "## AL-90 参数校验异常",
            "现象：上电报 AL-90，无法使能。",
            "原因：参数区数据损坏、写入过程中断电、更换电池后未初始化。",
            "处理：执行参数初始化恢复出厂值，再逐项重新写入；若反复出现，更换驱动器内部电池并检查存储芯片。",
            "",
            "## 报警复位方法",
            "排除故障后，给 Y01 报警复位输出一个 200ms 的低电平脉冲，或在面板按 MODE 键进入报警清除菜单执行清除。AL-51、AL-90 必须断电重启后才能清除。"
        };

        private static string[] IoHeader() => new[] { "点位", "名称", "类型", "默认状态", "说明" };

        private static IEnumerable<string[]> IoRows() => new[]
        {
            new[] { "X00", "急停按钮", "输入", "常闭", "断开时驱动器立即断使能，需手动复位" },
            new[] { "X01", "左限位", "输入", "常闭", "负方向硬限位，触发后禁止继续负向运动" },
            new[] { "X02", "右限位", "输入", "常闭", "正方向硬限位，触发后禁止继续正向运动" },
            new[] { "X03", "原点传感器", "输入", "常开", "回零时寻找的原点开关，触发区间约 8mm" },
            new[] { "X04", "气压低报警", "输入", "常开", "气压低于 0.4MPa 时闭合，流程暂停" },
            new[] { "X05", "安全门", "输入", "常闭", "门打开时断开，所有轴停止" },
            new[] { "X06", "气缸1 伸出到位", "输入", "常开", "磁性开关信号，伸出后 0.3s 内应闭合" },
            new[] { "X07", "气缸1 缩回到位", "输入", "常开", "磁性开关信号，缩回后 0.3s 内应闭合" },
            new[] { "Y00", "伺服使能", "输出", "低电平", "输出低电平时伺服 ON" },
            new[] { "Y01", "报警复位", "输出", "低电平", "输出 200ms 低电平脉冲清除报警" },
            new[] { "Y02", "气缸1 伸出", "输出", "低电平", "单控电磁阀，与 Y03 互锁" },
            new[] { "Y03", "气缸1 缩回", "输出", "低电平", "单控电磁阀，与 Y02 互斥，禁止同时输出" },
            new[] { "Y04", "运行指示灯", "输出", "低电平", "自动运行时常亮，暂停时闪烁" },
            new[] { "Y05", "蜂鸣器", "输出", "低电平", "报警时输出 1Hz 断续信号" }
        };

        // ==================== docx ====================

        private static void WriteDocx(string path, string title, string[] lines)
        {
            using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var body = main.Document.Body;

            body.Append(WordParagraph(title, 32, true));

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    body.Append(new W.Paragraph());
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                    body.Append(WordParagraph(line.Substring(3), 26, true));
                else
                    body.Append(WordParagraph(line, 21, false));
            }

            main.Document.Save();
        }

        private static W.Paragraph WordParagraph(string text, int halfPointSize, bool bold)
        {
            var props = new W.RunProperties(new W.FontSize { Val = halfPointSize.ToString() });
            if (bold) props.Append(new W.Bold());

            return new W.Paragraph(new W.Run(props, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        // ==================== pdf ====================

        private static void WritePdf(string path, string title, string[] lines)
        {
            iTextSharp.text.pdf.BaseFont baseFont = LoadCjkFont();
            var bodyFont = new iTextSharp.text.Font(baseFont, 10.5f);
            var titleFont = new iTextSharp.text.Font(baseFont, 16f, iTextSharp.text.Font.BOLD);
            var headFont = new iTextSharp.text.Font(baseFont, 12f, iTextSharp.text.Font.BOLD);

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            var document = new iTextSharp.text.Document(iTextSharp.text.PageSize.A4, 56f, 56f, 56f, 56f);
            iTextSharp.text.pdf.PdfWriter.GetInstance(document, stream);
            document.Open();

            document.Add(new iTextSharp.text.Paragraph(title, titleFont) { SpacingAfter = 12f });

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    document.Add(new iTextSharp.text.Paragraph(" ", bodyFont) { SpacingAfter = 2f });
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    document.Add(new iTextSharp.text.Paragraph(line.Substring(3), headFont)
                    {
                        SpacingBefore = 10f,
                        SpacingAfter = 4f
                    });
                }
                else
                {
                    document.Add(new iTextSharp.text.Paragraph(line, bodyFont) { SpacingAfter = 3f });
                }
            }

            document.Close();
        }

        /// <summary>
        /// iTextSharp 内置字体不含中文，必须嵌入系统里的中文字体；.ttc 需要带索引。
        /// </summary>
        private static iTextSharp.text.pdf.BaseFont LoadCjkFont()
        {
            // 优先整字体文件（.ttf）：iTextSharp 对 .ttc 的嵌入支持不全，
            // 走 .ttc 时可能只写 Unicode 映射而不带字形，别的机器打开会显示空白
            string[] candidates =
            {
                @"C:\Windows\Fonts\simhei.ttf",
                @"C:\Windows\Fonts\Deng.ttf",
                @"C:\Windows\Fonts\msyh.ttf",
                @"C:\Windows\Fonts\msyh.ttc,0",
                @"C:\Windows\Fonts\simsun.ttc,0"
            };

            foreach (string candidate in candidates)
            {
                string file = candidate.Split(',')[0];
                if (!File.Exists(file)) continue;

                try
                {
                    return iTextSharp.text.pdf.BaseFont.CreateFont(
                        candidate,
                        iTextSharp.text.pdf.BaseFont.IDENTITY_H,
                        iTextSharp.text.pdf.BaseFont.EMBEDDED);
                }
                catch (Exception ex)
                {
                    // 当前字体不可嵌入就换下一个；排查时 WENQWEN_DEBUG_SEED=1 看具体原因
                    if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_SEED") == "1")
                        Console.WriteLine($"   字体不可用：{candidate} → {ex.GetType().Name}: {ex.Message}");
                }
            }

            return iTextSharp.text.pdf.BaseFont.CreateFont(
                iTextSharp.text.pdf.BaseFont.HELVETICA,
                iTextSharp.text.pdf.BaseFont.WINANSI,
                iTextSharp.text.pdf.BaseFont.NOT_EMBEDDED);
        }

        // ==================== xlsx ====================

        private static void WriteXlsx(string path, string sheetName, string[] header, IEnumerable<string[]> rows)
        {
            using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();

            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();
            sheetPart.Worksheet = new S.Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(sheetPart),
                SheetId = 1U,
                Name = sheetName
            });

            uint rowIndex = 1;
            sheetData.Append(SheetRow(rowIndex++, header, true));
            foreach (string[] row in rows)
                sheetData.Append(SheetRow(rowIndex++, row, false));

            // 固定列宽，表格在 Excel 里打开就能看清
            sheetPart.Worksheet.InsertBefore(new S.Columns(
                new S.Column { Min = 1U, Max = 1U, Width = 10, CustomWidth = true },
                new S.Column { Min = 2U, Max = 2U, Width = 20, CustomWidth = true },
                new S.Column { Min = 3U, Max = 3U, Width = 10, CustomWidth = true },
                new S.Column { Min = 4U, Max = 4U, Width = 12, CustomWidth = true },
                new S.Column { Min = 5U, Max = 5U, Width = 48, CustomWidth = true }
            ), sheetData);

            workbookPart.Workbook.Save();
        }

        private static S.Row SheetRow(uint index, IEnumerable<string> values, bool bold)
        {
            var row = new S.Row { RowIndex = index };
            int column = 0;

            foreach (string value in values)
            {
                var cell = new S.Cell
                {
                    CellReference = ColumnName(column++) + index,
                    DataType = S.CellValues.InlineString
                };

                var inline = new S.InlineString();
                if (bold) inline.Append(new S.RunProperties(new S.Bold()));
                inline.Append(new S.Text(value ?? "") { Space = SpaceProcessingModeValues.Preserve });
                cell.Append(inline);

                row.Append(cell);
            }

            return row;
        }

        private static string ColumnName(int index)
        {
            var name = new StringBuilder();
            int n = index;
            do
            {
                name.Insert(0, (char)('A' + n % 26));
                n = n / 26 - 1;
            } while (n >= 0);

            return name.ToString();
        }
    }
}
