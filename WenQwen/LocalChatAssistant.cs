// ============================================================
// 温启志  18719361399
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace QwenConsole
{
    class Program
    {
        private static LLamaWeights _model;
        private static LLamaContext _context;
        private static InteractiveExecutor _executor;

        // RAG 知识库：分块存储
        private static List<KnowledgeChunk> _knowledgeChunks = new();

        // 内部记忆
        private static List<ChatHistoryItem> _chatHistory = new();
        private static int _historyId = 1;

        // 模型与推荐配置（本项目仅引用 CPU 后端，GpuLayerCount 不生效）：
        //   0.5B / 1.5B Q4_K_M：上下文 4096~8192，内存约 1~2GB，速度快
        //   3B / 4B     Q4_K_M：上下文 8192，内存约 3~4GB，速度中等
        //   7B          Q4_K_M：上下文 8192，内存约 5~6GB，速度较慢（当前默认）
        //   14B 及以上          ：建议改用 GPU 后端
        private const int ModelContextSize = 8192;
        private const int ModelGpuLayers = 99; // 仅 GPU 后端生效
        private const int MaxOutputTokens = 800;

        private static readonly InferenceParams _infParams = new InferenceParams
        {
            MaxTokens = MaxOutputTokens,
            AntiPrompts = new[] { "<|im_end|>", "User:", "用户:", "Assistant:", "助手:" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f }
        };

        private static string _modelPath = "e:\\wqz\\models\\qwen2.5-7b-instruct-1m-q4_k_m.gguf";
        private static string _knowledgeFolder = "资料";
        private const string ModelDownloadUrl = "https://modelscope.cn/models?name=Qwen2.5-4B-Instruct-GGUF&page=1&tabKey=task";
        private static int _chunkSize = 800; // 知识库分块大小

        // 知识库分块实体
        public class KnowledgeChunk
        {
            public string FileName { get; set; }
            public string Content { get; set; }
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
            EnsureKnowledgeFolder();
            Console.WriteLine("正在加载模型...");
            EnsureModelAvailable();

            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = ModelContextSize,
                    GpuLayerCount = ModelGpuLayers,
                    Threads = Environment.ProcessorCount,
                };

                _model = LLamaWeights.LoadFromFile(parameters);
                _context = new LLamaContext(_model, parameters);
                _executor = new InteractiveExecutor(_context);

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
                string input = Console.ReadLine()?.Trim();
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

            _context?.Dispose();
            _model?.Dispose();
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
                Console.WriteLine("   把 txt / md / doc / docx 资料放进该文件夹，重启软件后自动读取。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"资料文件夹创建失败：{ex.Message}");
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

            string msg = $"未找到本地模型文件：\n{_modelPath}\n\n已为你打开 ModelScope 下载页面，请下载 Qwen2.5-4B-Instruct 的 GGUF 模型。\n下载完成后，在下方输入模型的实际路径。";
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
            Console.WriteLine("     3B / 4B      Q4_K_M   上下文 8192        内存约 3~4GB   速度中等");
            Console.WriteLine("     7B           Q4_K_M   上下文 8192        内存约 5~6GB   速度较慢（当前默认）");
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
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
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
        /// 加载所有知识文件 → 自动分块
        /// </summary>
        static void LoadRagKnowledge()
        {
            _knowledgeChunks.Clear();
            if (!Directory.Exists(_knowledgeFolder)) return;

            var exts = new[] { "*.txt", "*.md", "*.doc", "*.docx" };
            var files = exts.SelectMany(ext => Directory.GetFiles(_knowledgeFolder, ext, SearchOption.AllDirectories)).ToList();

            foreach (var file in files)
            {
                try
                {
                    string content = File.ReadAllText(file, Encoding.UTF8).Trim();
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // 分块
                    var chunks = SplitContent(content, _chunkSize);
                    foreach (var chunk in chunks)
                    {
                        _knowledgeChunks.Add(new KnowledgeChunk
                        {
                            FileName = Path.GetFileName(file),
                            Content = chunk
                        });
                    }
                }
                catch { continue; }
            }
        }

        /// <summary>
        /// 文本分块
        /// </summary>
        static List<string> SplitContent(string content, int maxSize)
        {
            var list = new List<string>();
            for (int i = 0; i < content.Length; i += maxSize)
            {
                int len = Math.Min(maxSize, content.Length - i);
                list.Add(content.Substring(i, len).Trim());
            }
            return list;
        }

        /// <summary>
        /// RAG 检索：关键词匹配 + 相似度评分
        /// </summary>
        static string SearchKnowledgeRag(string question)
        {
            if (!_knowledgeChunks.Any()) return "";

            var words = question.ToLower()
                .Replace("？", " ").Replace("?", "")
                .Split(new[] { ' ', '，', '。', '：', ':' }, StringSplitOptions.RemoveEmptyEntries);

            // 匹配打分
            var results = _knowledgeChunks
                .Select(chunk => new
                {
                    chunk.FileName,
                    chunk.Content,
                    Score = CalculateScore(chunk.Content.ToLower(), words)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(2) // 取最相关2块
                .ToList();

            if (!results.Any()) return "";

            var sb = new StringBuilder();
            foreach (var item in results)
            {
                sb.AppendLine($"【来自文件：{item.FileName}】");
                sb.AppendLine(item.Content);
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 匹配评分（命中越多分越高）
        /// </summary>
        static int CalculateScore(string content, string[] words)
        {
            return words.Sum(w => content.Contains(w) ? 1 : 0);
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
            string systemPrompt = string.IsNullOrEmpty(knowledge)
                ? "你是专业工业AI助手，用户没问你就简洁回答。如果不知道答案，直接说：抱歉，知识库中没有相关信息。"
                : $"你是专业工业AI助手，请**只根据下面的参考资料**精准回答，不要编造。\n\n参考资料：\n{knowledge}";

            // 拼接历史
            var history = new StringBuilder();
            foreach (var h in _chatHistory)
            {
                history.AppendLine($"<|im_start|>user\n{h.Question}<|im_end|>");
                history.AppendLine($"<|im_start|>assistant\n{h.Answer}<|im_end|>");
            }

            // Qwen 官方模板
            string prompt = $@"<|im_start|>system
{systemPrompt}<|im_end|>
{history}
<|im_start|>user
{question}<|im_end|>
<|im_start|>assistant
";

            Console.Write("AI：");
            var answer = new StringBuilder();
            int count = 0;
            const int limit = 800;

            await foreach (var token in _executor.InferAsync(prompt, _infParams))
            {
                if (count++ > limit || token.Contains("<|im_end|>") || token.Contains("User:")) break;
                Console.Write(token);
                answer.Append(token);
            }

            return answer.ToString().Trim();
        }
        #endregion
    }
}