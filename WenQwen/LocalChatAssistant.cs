// ============================================================
// 温启志  18719361399
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using ExcelDataReader;
using iTextSharp.text.pdf;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace QwenConsole
{
    class Program
    {
        private static LLamaWeights _model;
        private static StatelessExecutor _executor;

        // RAG 知识库：分块存储
        private static List<KnowledgeChunk> _knowledgeChunks = new();

        // 内部记忆
        private static List<ChatHistoryItem> _chatHistory = new();
        private static int _historyId = 1;

        // 模型与推荐配置（本项目仅引用 CPU 后端，GpuLayerCount 不生效）：
        //   0.5B / 1.5B Q4_K_M：上下文 4096~8192，内存约 1~2GB，速度快
        //   3B / 4B     Q4_K_M：上下文 8192，内存约 2~3GB，速度中等（当前默认）
        //   7B          Q4_K_M：上下文 8192，内存约 5~6GB，速度较慢
        //   14B 及以上          ：建议改用 GPU 后端
        private const int ModelContextSize = 8192;
        private const int ModelGpuLayers = 99; // 仅 GPU 后端生效
        private const int MaxOutputTokens = 600;

        private static readonly InferenceParams _infParams = new InferenceParams
        {
            MaxTokens = MaxOutputTokens,
            AntiPrompts = new[] { "<|im_end|>", "User:", "用户:", "Assistant:", "助手:" },
            // 3B 在低温度下会把同一段“现象/处理”反复刷屏，必须加重复惩罚压住；
            // 0.2 是为了压住自由发挥：RAG 场景要求照抄资料，温度高了会掺常识、改参数
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.2f,
                TopK = 40,
                TopP = 0.9f,
                RepeatPenalty = 1.18f,
                PenaltyCount = 128,
                FrequencyPenalty = 0.2f,
                PresencePenalty = 0.2f
            }
        };

        private static string _modelPath = "D:\\wqz\\models\\qwen2.5-vl-3b-instruct-q4_k_m.gguf";
        private static string _knowledgeFolder = "资料";
        private static readonly string[] KnowledgeExtensions =
            { "*.txt", "*.md", "*.csv", "*.docx", "*.doc", "*.xlsx", "*.xls", "*.pdf" };
        private const string ModelDownloadUrl = "https://modelscope.cn/models?name=Qwen2.5-VL-3B-Instruct-GGUF&page=1&tabKey=task";
        private static int _chunkSize = 800; // 知识库分块大小

        // 知识库分块实体
        public class KnowledgeChunk
        {
            public string FileName { get; set; }
            public string Content { get; set; }
            /// <summary>小写文本，检索时直接匹配，避免每次重复转换。</summary>
            public string SearchText { get; set; }
            /// <summary>小写文件名，用于“文件名命中即主题命中”的加权。</summary>
            public string SearchName { get; set; }
        }

        public class ChatHistoryItem
        {
            public int Id { get; set; }
            public string Question { get; set; }
            public string Answer { get; set; }
        }

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // .NET 8 默认只带 UTF-8/ASCII 等少数编码。iTextSharp 5 解析字体指标时会取
            // windows-1252，不注册 CodePages 就直接抛 ArgumentException，PDF 只能退回 Helvetica（中文全丢）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // 管道/文件重定向输入时按 UTF-8 解码，避免中文被按本地代码页解析成乱码
            if (Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;

            // 维护入口：只重建示例资料，不加载模型
            if (args.Any(a => string.Equals(a, "--seed-knowledge", StringComparison.OrdinalIgnoreCase)))
            {
                SeedKnowledgeFolder(force: true);
                return;
            }

            // 排查入口：只跑抽取与分块，确认资料能被读进来，不加载模型
            if (args.Any(a => string.Equals(a, "--list-knowledge", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("WENQWEN_DEBUG_RAG", "1");
                EnsureKnowledgeFolder();
                LoadRagKnowledge();
                Console.WriteLine($"知识库总块数：{_knowledgeChunks.Count}");
                ShowLoadedFiles();
                return;
            }

            // 排查入口：只跑检索，看一个问题命中哪篇资料，不加载模型
            int searchIndex = Array.FindIndex(args, a => string.Equals(a, "--search", StringComparison.OrdinalIgnoreCase));
            if (searchIndex >= 0 && searchIndex + 1 < args.Length)
            {
                string query = string.Join(" ", args.Skip(searchIndex + 1));
                Environment.SetEnvironmentVariable("WENQWEN_DEBUG_RAG", "1");
                EnsureKnowledgeFolder();
                LoadRagKnowledge();

                Console.WriteLine($"问题：{query}");
                string context = SearchKnowledgeRag(query);
                Console.WriteLine(string.IsNullOrWhiteSpace(context)
                    ? "命中结果：无"
                    : $"命中结果：{context.Length} 字{Environment.NewLine}{context}");
                return;
            }

            SilenceLlamaLog();
            EnsureKnowledgeFolder();
            Console.WriteLine("正在加载模型...");
            EnsureModelAvailable();
            VerifyModelFile();

            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = ModelContextSize,
                    GpuLayerCount = ModelGpuLayers,
                    Threads = Environment.ProcessorCount,
                };

                _model = LLamaWeights.LoadFromFile(parameters);
                // StatelessExecutor 每次推理都重新吃整段提示词、自己管 KV 缓存，
                // 不会把上一轮问答残留在会话状态里（InteractiveExecutor 会，导致第二轮起吐 EOS）
                _executor = new StatelessExecutor(_model, parameters);

                // 加载 RAG 知识库
                LoadRagKnowledge();
            }
            catch (Exception ex)
            {
                PromptError($"模型加载失败：{ex.Message}",
                    "请确认模型文件完整、未被其他程序占用，且本机支持该模型格式（GGUF）。");
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("             工业离线AI助手（RAG增强版）");
            Console.WriteLine($"   模型：{Path.GetFileNameWithoutExtension(_modelPath)}");
            Console.WriteLine($"   配置：上下文 {ModelContextSize} | 线程 {Environment.ProcessorCount} | CPU 推理 | 最大输出 {MaxOutputTokens} token");
            Console.WriteLine("   模式：RAG检索增强生成 + 本地知识库");
            Console.WriteLine($"   已加载文件夹：{_knowledgeFolder}");
            Console.WriteLine($"   知识库总块数：{_knowledgeChunks.Count}");
            ShowLoadedFiles();
            Console.WriteLine("  输入数字 = 追问上一轮 | 直接输入 = 新问题");
            Console.WriteLine("        exit 退出 | clear 清空记忆");
            Console.WriteLine("==================================================\n");

            while (true)
            {
                Console.Write("你：");
                // 从 UTF-8 带 BOM 的文件/管道喂输入时，首行会带一个 U+FEFF，
                // 不清理会显示成乱码点，也会混进检索分词
                string input = Console.ReadLine()?.Replace("\uFEFF", "").Trim();
                if (string.IsNullOrEmpty(input)) continue;

                if (input.ToLower() == "exit") break;
                if (input.ToLower() == "clear")
                {
                    _chatHistory.Clear();
                    _historyId = 1;
                    Console.WriteLine("✅ 记忆已清空\n");
                    continue;
                }

                string userQuestion = input;

                // 数字追问
                if (int.TryParse(input, out int num) && num == _historyId - 1 && _chatHistory.Count > 0)
                {
                    var last = _chatHistory.Last();
                    userQuestion = $"请详细解释：{last.Question}";
                    Console.WriteLine($"▶ 继续追问：{last.Question}");
                }

                // RAG 检索
                string know = SearchKnowledgeRag(userQuestion);
                string answer = await InferAnswer(userQuestion, know);

                _chatHistory.Add(new ChatHistoryItem
                {
                    Id = _historyId++,
                    Question = userQuestion,
                    Answer = answer
                });

                Console.WriteLine("\n");
            }

            _model?.Dispose();
        }

        /// <summary>
        /// llama.cpp 默认把 KV cache、计算图、缓冲区分配等内部细节逐行打到控制台，
        /// 对使用者没有意义。默认全部静默，排查加载问题时可设 WENQWEN_DEBUG_LLAMA=1 打开。
        /// </summary>
        static void SilenceLlamaLog()
        {
            if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_LLAMA") == "1")
                LLama.Native.NativeLogConfig.llama_log_set((level, message) => Console.Error.WriteLine(message));
            else
                LLama.Native.NativeLogConfig.llama_log_set((level, message) => { });
        }

        /// <summary>
        /// 确保资料文件夹存在，并提示用户放入资料后重启软件。
        /// </summary>
        private static void EnsureKnowledgeFolder()
        {
            try
            {
                _knowledgeFolder = Path.GetFullPath(_knowledgeFolder);
                bool created = !Directory.Exists(_knowledgeFolder);
                if (created) Directory.CreateDirectory(_knowledgeFolder);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(created
                    ? $"已自动创建资料文件夹：{_knowledgeFolder}"
                    : $"资料文件夹：{_knowledgeFolder}");
                Console.ResetColor();
                Console.WriteLine("   把 txt / md / docx / xlsx / pdf 资料放进该文件夹，重启软件后自动读取。");

                // 一个资料都没有时自动落一整套示例资料（txt / docx / pdf / xlsx），
                // 空目录不再只有一份说明，知识库开箱就有内容可问
                if (!HasAnyKnowledgeFile())
                {
                    var made = KnowledgeSeeder.Seed(_knowledgeFolder);
                    if (made.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"   资料文件夹里还没有资料，已自动生成 {made.Count} 个示例文件：");
                        foreach (string name in made) Console.WriteLine("     " + name);
                        Console.WriteLine("   这些文件可以直接提问，也可以整批删除后换成自己的资料。");
                        Console.ResetColor();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"资料文件夹创建失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 手动重建示例资料（WenQwen.exe --seed-knowledge），同名文件覆盖，不动用户自己的资料。
        /// </summary>
        private static void SeedKnowledgeFolder(bool force)
        {
            try
            {
                _knowledgeFolder = Path.GetFullPath(_knowledgeFolder);
                Directory.CreateDirectory(_knowledgeFolder);

                if (!force && HasAnyKnowledgeFile())
                {
                    Console.WriteLine($"资料文件夹已有资料，跳过生成：{_knowledgeFolder}");
                    return;
                }

                var made = KnowledgeSeeder.Seed(_knowledgeFolder);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"已生成 {made.Count} 个示例资料：{_knowledgeFolder}");
                foreach (string name in made) Console.WriteLine("  " + name);
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"生成示例资料失败：{ex.Message}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// 资料文件夹里是否存在至少一个可解析的资料文件。
        /// </summary>
        private static bool HasAnyKnowledgeFile()
        {
            try
            {
                foreach (string ext in KnowledgeExtensions)
                {
                    if (Directory.GetFiles(_knowledgeFolder, ext, SearchOption.AllDirectories).Length > 0)
                        return true;
                }
                return false;
            }
            catch
            {
                // 目录读不了就当作“有资料”，不要凭猜测往里写文件
                return true;
            }
        }

        #region 模型可用性与提示
        /// <summary>
        /// 启动前检查模型文件是否存在；缺失时给出明确提示，并允许手动指定路径。
        /// </summary>
        private static void EnsureModelAvailable()
        {
            if (File.Exists(_modelPath)) return;

            string autoFound = ModelLocator.Resolve(_modelPath);
            if (!string.Equals(autoFound, _modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _modelPath = autoFound;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"已在其他文件夹自动找到模型：{_modelPath}");
                Console.ResetColor();
                return;
            }

            string msg = $"未找到本地模型文件：\n{_modelPath}\n\n已为你打开 ModelScope 下载页面，请下载 Qwen2.5-VL-3B-Instruct Q4_K_M 的 GGUF 模型。\n下载完成后，在下方输入模型的实际路径。";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠️ " + msg);
            Console.ResetColor();

            OpenModelDownloadPage();
            ShowModelConfigGuide();
            ShowMessageBox(msg, "模型未找到");

            Console.Write("模型路径（直接回车使用默认路径）> ");
            string input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input)) _modelPath = input;

            if (!File.Exists(_modelPath))
            {
                PromptError("模型文件仍然不存在，程序无法启动。",
                    "请从 ModelScope 下载 Qwen 的 .gguf 模型，放到上述路径后重新运行本程序。");
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 加载前体检：文件头必须是 "GGUF" 且体积合理。
        /// 网盘边下边读、下载中断、占位文件都会在这里被挡住，
        /// 比 llama.cpp 统一抛的 "Failed to load model" 更容易定位。
        /// </summary>
        private static void VerifyModelFile()
        {
            var info = new FileInfo(_modelPath);
            long size = info.Exists ? info.Length : 0;
            string sizeText = size >= 1024L * 1024 * 1024
                ? $"{size / 1024.0 / 1024 / 1024:F2} GB"
                : $"{size / 1024.0 / 1024:F1} MB";
            Console.WriteLine($"   文件：{_modelPath}");
            Console.WriteLine($"   大小：{sizeText}");

            if (size < 64L * 1024 * 1024)
            {
                PromptError($"模型文件不完整：{_modelPath}（仅 {sizeText}）",
                    "Q4_K_M 的 3B 模型应在 1.5GB 以上。请等下载真正结束（网盘客户端显示“已完成”）后再运行，或重新下载。");
                Environment.Exit(0);
            }

            string magic;
            try
            {
                byte[] head = new byte[4];
                using (var fs = File.OpenRead(_modelPath))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < head.Length) throw new IOException("文件头不足 4 字节");
                }
                magic = Encoding.ASCII.GetString(head);
            }
            catch (Exception ex)
            {
                PromptError($"模型文件无法读取：{_modelPath}", ex.Message);
                Environment.Exit(0);
                return;
            }

            if (!string.Equals(magic, "GGUF", StringComparison.Ordinal))
            {
                PromptError($"模型文件格式不对：{_modelPath}（文件头为 \"{magic}\"，应为 \"GGUF\"）",
                    "常见原因：下载未完成、下到的是网页或种子、网盘占位文件还没落地到本地。请核对文件大小后重新下载。");
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 打开 ModelScope 模型下载页面，浏览器不可用时退化为打印链接。
        /// </summary>
        private static void OpenModelDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(ModelDownloadUrl) { UseShellExecute = true });
                Console.WriteLine($"   已打开浏览器：{ModelDownloadUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   无法自动打开浏览器（{ex.Message}），请手动访问：{ModelDownloadUrl}");
            }
        }

        /// <summary>
        /// 说明不同模型大小对应的推荐配置（本项目为 CPU 后端）。
        /// </summary>
        private static void ShowModelConfigGuide()
        {
            Console.WriteLine("   模型与推荐配置（CPU 推理）：");
            Console.WriteLine("     0.5B / 1.5B  Q4_K_M   上下文 4096~8192   内存约 1~2GB   速度快");
            Console.WriteLine("     3B / 4B      Q4_K_M   上下文 8192        内存约 2~3GB   速度中等（当前默认）");
            Console.WriteLine("     7B           Q4_K_M   上下文 8192        内存约 5~6GB   速度较慢");
            Console.WriteLine("     14B 及以上            建议使用 GPU 后端（本项目仅带 CPU 后端）");
        }

        /// <summary>
        /// 输出错误并弹窗提示后等待用户确认退出。
        /// </summary>
        private static void PromptError(string detail, string suggestion)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ " + detail);
            Console.WriteLine(suggestion);
            Console.ResetColor();
            ShowMessageBox(detail + "\n\n" + suggestion, "无法启动");
            if (Console.IsInputRedirected) return; // 管道/文件输入时没有可读按键，直接返回由调用方退出
            Console.WriteLine("按任意键退出...");
            try { Console.ReadKey(); } catch (InvalidOperationException) { }
        }

        /// <summary>
        /// 弹窗提示（无界面环境下静默降级为控制台输出）。
        /// </summary>
        private static void ShowMessageBox(string text, string caption)
        {
            try { MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { /* 非桌面环境下忽略，已通过控制台输出 */ }
        }
        #endregion

        #region RAG 知识库核心
        /// <summary>
        /// 加载所有知识文件 → 抽取纯文本 → 自动分块
        /// </summary>
        static void LoadRagKnowledge()
        {
            _knowledgeChunks.Clear();
            if (!Directory.Exists(_knowledgeFolder)) return;

            var exts = KnowledgeExtensions;
            var files = exts.SelectMany(ext => Directory.GetFiles(_knowledgeFolder, ext, SearchOption.AllDirectories)).ToList();

            var skipped = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    string content = ExtractFileText(file);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        skipped.Add(Path.GetFileName(file));
                        continue;
                    }

                    int added = 0;
                    foreach (string chunk in SplitContent(content, _chunkSize))
                    {
                        if (chunk.Length < 40) continue; // 封面、页眉这类碎片没有检索价值
                        _knowledgeChunks.Add(new KnowledgeChunk
                        {
                            FileName = Path.GetFileName(file),
                            Content = chunk,
                            SearchText = chunk.ToLowerInvariant(),
                            SearchName = Path.GetFileName(file).ToLowerInvariant()
                        });
                        added++;
                    }

                    if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_RAG") == "1")
                        Console.WriteLine($"[RAG] 加载 {Path.GetFileName(file)}：{content.Length} 字 → {added} 块");
                }
                catch (Exception ex)
                {
                    string reason = ex is FileNotFoundException || ex is FileLoadException
                        ? (ex as FileNotFoundException)?.FileName ?? ex.Message
                        : ex.GetType().Name;
                    skipped.Add($"{Path.GetFileName(file)}（{reason}）");
                }
            }

            if (skipped.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   已跳过 {skipped.Count} 个文件（无文本或格式不支持）：{string.Join("、", skipped.Take(5))}{(skipped.Count > 5 ? " 等" : "")}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// 按扩展名抽取纯文本。二进制 Office 文档必须按格式解析，直接读文本只会得到乱码。
        /// </summary>
        static string ExtractFileText(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".docx":
                    return ExtractDocxText(file);
                case ".doc":
                    return ExtractLegacyDocText(file);
                case ".xlsx":
                    return ExtractXlsxText(file);
                case ".xls":
                    return ExtractExcelText(file);
                case ".pdf":
                    return ExtractPdfText(file);
                default:
                    return NormalizeText(File.ReadAllText(file, Encoding.UTF8));
            }
        }

        /// <summary>
        /// docx：按文档顺序取段落与表格单元格文字。
        /// </summary>
        static string ExtractDocxText(string file)
        {
            using WordprocessingDocument doc = WordprocessingDocument.Open(file, false);
            Body body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "";

            var sb = new StringBuilder();
            string lastLine = "";
            foreach (Paragraph p in body.Descendants<Paragraph>())
            {
                string line = NormalizeText(p.InnerText);
                if (line.Length == 0) continue;
                if (IsNoiseLine(line)) continue;
                if (line == lastLine) continue; // 页眉页脚常整段重复
                sb.AppendLine(line);
                lastLine = line;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 目录域代码、纯页码、纯符号行对问答没有价值，抽文本时直接丢掉。
        /// </summary>
        static bool IsNoiseLine(string line)
        {
            if (line.IndexOf("PAGEREF", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf(" TOC ", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Regex.IsMatch(line, @"^[\s\.\-—_·、]*\d{1,3}[\s\.\-—_·、]*$");
        }

        /// <summary>
        /// 旧版 .doc：无 OpenXML，按 UTF-16LE 解码后保留可读片段兜底。
        /// </summary>
        static string ExtractLegacyDocText(string file)
        {
            string raw = Encoding.Unicode.GetString(File.ReadAllBytes(file));
            var sb = new StringBuilder();
            var run = new StringBuilder();

            void Flush()
            {
                if (run.Length >= 6) sb.AppendLine(NormalizeText(run.ToString()));
                run.Clear();
            }

            foreach (char c in raw)
            {
                bool readable = c == '\r' || c == '\n' || c == '\t'
                                || (c >= 0x20 && c <= 0x7E)
                                || (c >= 0x4E00 && c <= 0x9FFF)
                                || (c >= 0x3000 && c <= 0x303F)
                                || (c >= 0xFF00 && c <= 0xFFEF);
                if (readable) run.Append(c);
                else Flush();
            }
            Flush();
            return sb.ToString();
        }

        /// <summary>
        /// xlsx：按行列还原成“表头=值”的自描述文本。
        /// 直接拼单元格值会让小模型看串列（把加减速时间当成速度），带上表头就不会误读。
        /// </summary>
        static string ExtractXlsxText(string file)
        {
            var sb = new StringBuilder();
            using SpreadsheetDocument doc = SpreadsheetDocument.Open(file, false);
            var workbookPart = doc.WorkbookPart;
            if (workbookPart == null) return "";

            var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
            int sharedCount = sharedStrings?.ChildElements.Count ?? 0;

            foreach (WorksheetPart sheetPart in workbookPart.WorksheetParts)
            {
                List<string> header = null;

                foreach (Row row in sheetPart.Worksheet.Descendants<Row>())
                {
                    var cells = new SortedDictionary<int, string>();
                    foreach (Cell cell in row.Descendants<Cell>())
                    {
                        string v = cell.CellValue?.InnerText;
                        if (v == null) v = cell.InlineString?.InnerText;
                        if (string.IsNullOrEmpty(v)) continue;

                        if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString
                            && int.TryParse(v, out int idx) && idx >= 0 && idx < sharedCount)
                            v = sharedStrings.ElementAt(idx).InnerText;

                        v = v.Trim();
                        if (v.Length == 0) continue;
                        cells[ColumnIndex(cell.CellReference?.Value)] = v;
                    }
                    if (cells.Count == 0) continue;

                    // 第一行当表头，原样保留
                    if (header == null)
                    {
                        header = cells.Values.ToList();
                        sb.AppendLine("【" + string.Join(" | ", header) + "】");
                        continue;
                    }

                    var parts = new List<string>();
                    foreach (var pair in cells)
                    {
                        string name = pair.Key < header.Count ? header[pair.Key] : "";
                        parts.Add(string.IsNullOrWhiteSpace(name) ? pair.Value : $"{name}={pair.Value}");
                    }
                    if (parts.Count > 0) sb.AppendLine(string.Join(" | ", parts));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把单元格引用（如 "C7"）换成从 0 开始的列号，空单元格不会让后面整行错位。
        /// </summary>
        static int ColumnIndex(string cellReference)
        {
            if (string.IsNullOrEmpty(cellReference)) return 0;

            int index = 0;
            foreach (char c in cellReference)
            {
                if (!char.IsLetter(c)) break;
                index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            }
            return index - 1;
        }

        /// <summary>
        /// xls（旧版二进制）：用 ExcelDataReader 兜底。
        /// </summary>
        static string ExtractExcelText(string file)
        {
            var sb = new StringBuilder();
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                while (reader.Read())
                {
                    var cells = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string v = reader.GetValue(i)?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(v)) cells.Add(v);
                    }
                    if (cells.Count > 0) sb.AppendLine(string.Join("\t", cells));
                }
            } while (reader.NextResult());

            return sb.ToString();
        }

        /// <summary>
        /// pdf：逐页抽取文字，扫描件得不到内容（会走跳过提示）。
        /// </summary>
        static string ExtractPdfText(string file)
        {
            var sb = new StringBuilder();
            using var reader = new PdfReader(File.ReadAllBytes(file));
            for (int page = 1; page <= reader.NumberOfPages; page++)
            {
                string text = iTextSharp.text.pdf.parser.PdfTextExtractor.GetTextFromPage(reader, page);
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(NormalizeText(text));
            }
            reader.Close();
            return sb.ToString();
        }

        /// <summary>
        /// 统一空白：去掉控制字符、把零散换行压成单换行。
        /// </summary>
        static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            bool lastSpace = false;
            foreach (char c in text)
            {
                if (c == '\r') continue;
                char ch = char.IsControl(c) && c != '\n' && c != '\t' ? ' ' : c;
                if (ch == '\n' || ch == '\t' || ch == ' ' || ch == '\u3000')
                {
                    if (!lastSpace) sb.Append(ch == '\n' ? '\n' : ' ');
                    lastSpace = true;
                    continue;
                }
                sb.Append(ch);
                lastSpace = false;
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 按段落分块，块长接近 maxSize；单段超长时才硬切。
        /// </summary>
        static List<string> SplitContent(string content, int maxSize)
        {
            var list = new List<string>();
            var buf = new StringBuilder();

            foreach (string raw in content.Split('\n'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;

                if (p.Length > maxSize)
                {
                    if (buf.Length > 0) { list.Add(buf.ToString().Trim()); buf.Clear(); }
                    for (int i = 0; i < p.Length; i += maxSize)
                        list.Add(p.Substring(i, Math.Min(maxSize, p.Length - i)).Trim());
                    continue;
                }

                if (buf.Length + p.Length + 1 > maxSize && buf.Length > 0)
                {
                    list.Add(buf.ToString().Trim());
                    buf.Clear();
                }
                buf.AppendLine(p);
            }

            if (buf.Length > 0) list.Add(buf.ToString().Trim());
            return list;
        }

        /// <summary>
        /// 查询里的泛词（疑问词、口语词），参与匹配只会拉进无关内容。
        /// </summary>
        private static readonly HashSet<string> QueryStopWords = new(StringComparer.Ordinal)
        {
            "怎样", "怎么", "什么", "如何", "是否", "哪些", "为什", "么样",
            "请问", "一下", "帮我", "可以", "应该", "这个", "那个", "现在",
            "问题", "的", "了", "吗", "呢",
            // 常见口语/计算词，几乎每篇资料都可能出现，留着只会拉进无关文档
            "等于", "多少", "多久", "大概", "介绍", "说明", "告诉", "需要", "是不"
        };

        /// <summary>
        /// 查询分词：英文/数字型号词 + 中文二元组，解决中文没有空格切不出词的问题。
        /// </summary>
        static List<string> TokenizeQuery(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) return tokens.ToList();

            string lower = text.ToLowerInvariant();

            foreach (Match m in Regex.Matches(lower, "[a-z0-9][a-z0-9_.\\-]{1,}"))
            {
                string tk = m.Value.Trim('.', '-', '_');
                if (tk.Length >= 2) tokens.Add(tk);
            }

            var run = new StringBuilder();
            foreach (char c in lower)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) { run.Append(c); continue; }
                FlushCjk(run, tokens);
            }
            FlushCjk(run, tokens);

            return tokens.Where(t => !QueryStopWords.Contains(t)).ToList();
        }

        static void FlushCjk(StringBuilder run, HashSet<string> tokens)
        {
            if (run.Length == 0) return;
            if (run.Length == 1) { tokens.Add(run.ToString()); run.Clear(); return; }
            for (int i = 0; i + 1 < run.Length; i++) tokens.Add(run.ToString(i, 2));
            run.Clear();
        }

        /// <summary>
        /// RAG 检索：按命中词打分，稀有词权重更高，取最相关几块拼接。
        /// </summary>
        static string SearchKnowledgeRag(string question)
        {
            bool debug = Environment.GetEnvironmentVariable("WENQWEN_DEBUG_RAG") == "1";
            if (_knowledgeChunks.Count == 0) { if (debug) Console.WriteLine("[RAG] 跳过：知识库为空"); return ""; }

            var tokens = TokenizeQuery(question);
            if (debug) Console.WriteLine("[RAG] 分词：" + string.Join(" / ", tokens));
            if (tokens.Count == 0) { if (debug) Console.WriteLine("[RAG] 跳过：没有可用查询词"); return ""; }

            int total = _knowledgeChunks.Count;

            // 统计每个词的命中块数：0 命中丢弃，几乎块块都命中的视为泛词
            var hits = new List<(string Token, int Df)>();
            foreach (string tk in tokens)
            {
                int df = 0;
                foreach (var chunk in _knowledgeChunks)
                    if (chunk.SearchText.Contains(tk)) df++;
                if (df > 0) hits.Add((tk, df));
            }
            if (hits.Count == 0) { if (debug) Console.WriteLine("[RAG] 跳过：查询词在资料里都没有出现"); return ""; }

            var use = hits.Where(h => h.Df <= total * 0.7).ToList();
            if (use.Count == 0) use = hits;

            // 打分 = 词权重 × 出现次数（对数压制）+ 文件名命中加分。
            // 文件名命中说明整篇文档就是这个主题，权重远高于正文偶发一次。
            var weights = use.Select(h => new
            {
                h.Token,
                Weight = (1.0 + Math.Log((double)total / (h.Df + 1))) * (char.IsAsciiLetterOrDigit(h.Token[0]) ? 1.5 : 1.0)
            }).ToList();

            var scored = new List<(KnowledgeChunk Chunk, double Score)>();
            foreach (var chunk in _knowledgeChunks)
            {
                double score = 0;
                foreach (var w in weights)
                {
                    int tf = CountOccurrences(chunk.SearchText, w.Token);
                    if (tf == 0) continue;
                    score += w.Weight * (1.0 + Math.Log(1.0 + Math.Min(tf, 10)));
                }
                if (score > 0)
                {
                    // 代码片段的汉字占比很低，排查类问题更需要说明文字，给一点降权
                    if (CjkRatio(chunk.Content) < 0.2) score *= 0.5;
                    scored.Add((chunk, score));
                }
            }
            if (scored.Count == 0) return "";

            // 先定文档再定块：文件名（标题）命中说明整篇就是这个主题，给文档级加分；
            // 这样“正文里某词出现很多次的文件”不会抢走主题最匹配的文件。
            var docScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string doc in _knowledgeChunks.Select(c => c.FileName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                double bestChunk = 0;
                foreach (var (chunk, score) in scored)
                    if (string.Equals(chunk.FileName, doc, StringComparison.OrdinalIgnoreCase) && score > bestChunk)
                        bestChunk = score;

                double titleBonus = 0;
                string title = doc.ToLowerInvariant();
                foreach (var w in weights)
                    if (title.Contains(w.Token)) titleBonus += w.Weight * 5.0;

                if (bestChunk > 0 || titleBonus > 0) docScores[doc] = bestChunk + titleBonus;
            }
            if (docScores.Count == 0) return "";

            // 第一名明显领先时只带这一篇：小模型看到两篇主题不同的资料容易串题，
            // 例如问“AL-51 报警”却答成另一篇里的“回零方向设置错误”。
            var ranked = docScores.OrderByDescending(x => x.Value).ToList();
            var topDocs = new List<string> { ranked[0].Key };
            if (ranked.Count > 1 && ranked[0].Value < ranked[1].Value * 1.5)
                topDocs.Add(ranked[1].Key);

            // 每个文档取最相关的 2 块；正文没命中的（靠标题入选）取后续块，通常就是现象与解决方案
            var top = new List<(KnowledgeChunk Chunk, double Score)>();
            foreach (string doc in topDocs)
            {
                var docChunks = _knowledgeChunks
                    .Where(c => string.Equals(c.FileName, doc, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var picked = scored.Where(s => string.Equals(s.Chunk.FileName, doc, StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(s => s.Score)
                                   .Take(2)
                                   .Select(s => s.Chunk)
                                   .ToList();

                if (picked.Count < 2)
                {
                    foreach (var chunk in docChunks)
                    {
                        if (picked.Contains(chunk)) continue;
                        picked.Add(chunk);
                        if (picked.Count >= 2) break;
                    }
                }

                foreach (var chunk in picked)
                {
                    // 代码块对排查类回答帮助有限，整篇最多带 1 块，避免大段 Lua 淹没说明文字
                    if (CjkRatio(chunk.Content) < 0.15 && top.Any(t => CjkRatio(t.Chunk.Content) < 0.15)) continue;
                    top.Add((chunk, docScores[doc]));
                }
            }

            // 排查检索效果：设置环境变量 WENQWEN_DEBUG_RAG=1 可打印命中块
            if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_RAG") == "1")
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("[RAG] 查询词：" + string.Join(" / ", use.Select(h => $"{h.Token}(df={h.Df})")));
                foreach (var (chunk, score) in top)
                {
                    Console.WriteLine($"[RAG] --- {score:F2}  长度{chunk.Content.Length}  {chunk.FileName}");
                    Console.WriteLine(chunk.Content);
                }
                Console.ResetColor();
            }

            var sb = new StringBuilder();
            int budget = 3000; // 控制拼进提示词的总长度
            foreach (var (chunk, _) in top)
            {
                if (budget <= 0) break;
                string body = chunk.Content;
                if (body.Length > budget) body = body.Substring(0, budget) + "…";
                budget -= body.Length;
                sb.AppendLine($"【来自文件：{chunk.FileName}】");
                sb.AppendLine(body);
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 统计子串出现次数（不重叠），用于词频加权。
        /// </summary>
        static int CountOccurrences(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;

            int count = 0, index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
                if (count >= 50) break;
            }
            return count;
        }

        /// <summary>
        /// 汉字占比，用来区分说明文字与代码/表格片段。
        /// </summary>
        static double CjkRatio(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int cjk = 0, valid = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                valid++;
                if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            }
            return valid == 0 ? 0 : (double)cjk / valid;
        }
        #endregion

        #region 辅助功能
        static void ShowLoadedFiles()
        {
            var files = _knowledgeChunks.Select(c => c.FileName).Distinct().ToList();
            if (!files.Any())
            {
                Console.WriteLine("   未加载任何知识文件");
                Console.WriteLine($"   请把资料放入 {_knowledgeFolder} 后重启软件");
                return;
            }
            Console.WriteLine("   已学习文件：");
            int i = 1;
            foreach (var f in files) Console.WriteLine($"     {i++}. {f}");
        }
        #endregion

        #region AI 推理（RAG 提示词优化）
        static async Task<string> InferAnswer(string question, string knowledge)
        {
            // RAG 严格系统提示
            // 小模型对 system 里的长文本不敏感，资料放到 user 消息里更稳
            bool hasKnowledge = !string.IsNullOrWhiteSpace(knowledge);

            string systemPrompt = hasKnowledge
                ? "你是工业设备资料查询工具，不是顾问。只能依据用户给出的【参考资料】作答，禁止使用参考资料以外的知识、经验、常识或推测。回答必须忠于原文：可以精简、归纳、分条，但参数号、型号、数值必须原样照抄，不得推导资料未写明的因果、步骤或操作建议。参考资料没写的，直接说“资料中没有提到”，不要补全。回答只包含资料里的内容，不要自己写来源标记，程序会统一附上。先定位与问题对应的那一行再回答，不要混用其他条目，也不要整段抄录。"
                : "你是工业设备资料查询工具。这次没有检索到资料，只输出一句：知识库中没有相关资料。不要给建议，不要推测，不要补充任何常识内容。";

            string userPrompt = hasKnowledge
                ? $"【参考资料】\n{knowledge}\n\n【问题】\n{question}"
                : question;

            // 不拼接历史问答：3B 会把上一轮答案原样搬进新答案（问齿轮比却先答回零），
            // 每问的参考资料已足够独立作答，历史只保留在内存里供追问与展示。
            string prompt = $@"<|im_start|>system
{systemPrompt}<|im_end|>
<|im_start|>user
{userPrompt}<|im_end|>
<|im_start|>assistant
";

            Console.Write("AI：");
            if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_PROMPT") == "1")
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "wenqwen_prompt.txt"), prompt, Encoding.UTF8); }
                catch { }
            }

            // 单次生成：逐 token 打印并累积答案
            async Task<(string Text, int Count, string Stop)> GenerateAsync(InferenceParams infParams)
            {
                var buffer = new StringBuilder();
                int n = 0;
                string stop = "结束";
                const int limit = MaxOutputTokens;

                await foreach (var token in _executor.InferAsync(prompt, infParams))
                {
                    if (token.Contains("<|im_end|>")) { stop = "im_end"; break; }
                    if (token.Contains("User:")) { stop = "User:"; break; }
                    if (n > limit) { stop = "达到上限token"; break; }
                    n++;
                    Console.Write(token);
                    buffer.Append(token);
                }
                return (buffer.ToString(), n, stop);
            }

            var (text, count, stopReason) = await GenerateAsync(_infParams);

            // 3B 偶尔第一轮直接吐 EOS，空回答时略提高温度重试一次（不能太高，否则开始自由发挥）
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Write("（重试）");
                var retryParams = new InferenceParams
                {
                    MaxTokens = MaxOutputTokens,
                    AntiPrompts = _infParams.AntiPrompts,
                    SamplingPipeline = new DefaultSamplingPipeline
                    {
                        Temperature = 0.35f,
                        TopK = 40,
                        TopP = 0.9f,
                        RepeatPenalty = 1.18f,
                        PenaltyCount = 128,
                        FrequencyPenalty = 0.2f,
                        PresencePenalty = 0.2f
                    }
                };
                (text, count, stopReason) = await GenerateAsync(retryParams);
            }

            if (Environment.GetEnvironmentVariable("WENQWEN_DEBUG_PROMPT") == "1")
                Console.WriteLine($"\n[INF] tokens={count} stop={stopReason} chars={text.Length}");

            if (string.IsNullOrWhiteSpace(text))
                Console.WriteLine("（模型这次没有给出内容，请重试或换个问法）");

            // 来源与参数号由程序核对，不依赖模型自觉：流式已经打印完正文，这里补一段核对结果
            string footer = hasKnowledge ? BuildVerificationFooter(text, knowledge) : "";
            if (footer.Length > 0) Console.WriteLine("\n" + footer);

            return (text.Trim() + (footer.Length > 0 ? "\n\n" + footer : "")).Trim();
        }

        /// <summary>
        /// 回答后核对：附上本次真实命中的文件名，并检查回答里的参数号是否真的出现在资料中。
        /// 模型偶尔会把 Pr0.10 写成 Pr1.08，这种错必须按原文比对，不能靠它自己保证。
        /// </summary>
        static string BuildVerificationFooter(string answer, string knowledge)
        {
            var lines = new List<string>();

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(knowledge, @"[Pp][Rr]\s?\d{1,2}\.\d{1,2}"))
                known.Add(NormalizeParam(m.Value));

            var unknown = new List<string>();
            foreach (Match m in Regex.Matches(answer, @"[Pp][Rr]\s?\d{1,2}\.\d{1,2}"))
            {
                string code = NormalizeParam(m.Value);
                if (!known.Contains(code) && !unknown.Contains(code)) unknown.Add(code);
            }
            if (unknown.Count > 0)
                lines.Add($"⚠ 核对：资料中没有 {string.Join("、", unknown)}，相关数值可能失真，请以原文为准。");

            var files = new List<string>();
            foreach (Match m in Regex.Matches(knowledge, @"【来自文件：(.+?)】"))
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length > 0 && !files.Contains(name)) files.Add(name);
            }
            if (files.Count > 0) lines.Add("来源：" + string.Join("、", files));

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>参数号归一化：去掉空格、统一小写，便于和原文比对。</summary>
        static string NormalizeParam(string raw)
        {
            return raw.Replace(" ", "").Replace("\u3000", "").ToLowerInvariant();
        }
        #endregion
    }
}