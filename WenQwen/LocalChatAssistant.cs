using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        private static readonly InferenceParams _infParams = new InferenceParams
        {
            MaxTokens = 800,
            AntiPrompts = new[] { "<|im_end|>", "User:", "用户:", "Assistant:", "助手:" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f }
        };

        private static string _modelPath = "E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf";
        private static string _knowledgeFolder = "资料";
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

        static async Task Main2(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("正在加载模型...");

            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = 8192,
                    GpuLayerCount = 99,
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
                Console.WriteLine($"加载失败：{ex.Message}");
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("             工业离线AI助手（RAG增强版）");
            Console.WriteLine("   模型：Qwen2.5-7B-Instruct");
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