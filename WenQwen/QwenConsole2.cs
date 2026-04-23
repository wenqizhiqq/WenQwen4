using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace QwenConsole2
{
    class Program
    {
        private static LLamaWeights _model;
        private static LLamaContext _context;
        private static InteractiveExecutor _executor;
        private static Dictionary<string, string> _knowledge;

        // 内部记忆（不显示）
        private static List<ChatHistoryItem> _chatHistory = new();
        private static int _historyId = 1;

        private static readonly InferenceParams _infParams = new InferenceParams
        {
            MaxTokens = 500,
            AntiPrompts = new[] { "<|im_end|>", "User:", "用户:", "Assistant:", "助手:" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f } // 低温度=更听话
        };

        private static string _modelPath = "E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf";
        private static string _knowledgeFolder = "knowledge";

        public class ChatHistoryItem
        {
            public int Id { get; set; }
            public string Question { get; set; }
            public string Answer { get; set; }
        }

        static async Task Main3(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8; 
            Console.WriteLine("正在加载模型...");

            using (var nullWriter = new StringWriter())
            {
                Console.SetOut(nullWriter);
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = 8192,
                    GpuLayerCount = 0,
                    Threads = Environment.ProcessorCount,
                };

                _model = LLamaWeights.LoadFromFile(parameters);
                _context = new LLamaContext(_model, parameters);
                _executor = new InteractiveExecutor(_context);
                _knowledge = FileHelper.LoadAllKnowledgeFiles();
            }

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            Console.WriteLine("==================================================");
            Console.WriteLine("             工业离线AI助手 · IO自动控制");
            Console.WriteLine("   模型：Qwen2.5-7B-Instruct 通义千问");
            Console.WriteLine("   模式：CPU推理 | 内存≈6GB");
            Console.WriteLine($"   文件夹：{_knowledgeFolder} | 文件总数：{_knowledge.Count}");
            Console.WriteLine("   指令示例：打开IO1 | 关闭IO2 | 读取IO3");
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

                // 生成AI回答
                string know = QueryKnowledge(input);
                string aiReply = await InferAnswer(input, know);

                // ==========================================
                // 核心：AI 输出后自动解析 IO 控制指令
                // ==========================================
                ParseAndExecuteIOCommand(aiReply);

                // 保存记忆
                _chatHistory.Add(new ChatHistoryItem
                {
                    Id = _historyId++,
                    Question = input,
                    Answer = aiReply
                });

                Console.WriteLine("\n");
            }

            _context?.Dispose();
            _model?.Dispose();
        }

        // ==============================================
        // 【核心功能】解析AI输出，执行IO命令
        // ==============================================
        static void ParseAndExecuteIOCommand(string reply)
        {
            reply = reply.ToLower().Trim();

            // 打开 IO
            if (reply.Contains("打开io") || reply.Contains("开启io"))
            {
                for (int i = 0; i <= 16; i++)
                {
                    if (reply.Contains($"io{i}") || reply.Contains($"i/o{i}"))
                    {
                        Console.WriteLine($"\n⚡ 执行命令：打开IO{i}");
                        SetIO(i, true); // 这里调用你的硬件IO库
                        return;
                    }
                }
            }

            // 关闭 IO
            if (reply.Contains("关闭io"))
            {
                for (int i = 0; i <= 16; i++)
                {
                    if (reply.Contains($"io{i}") || reply.Contains($"i/o{i}"))
                    {
                        Console.WriteLine($"\n⚡ 执行命令：关闭IO{i}");
                        SetIO(i, false);
                        return;
                    }
                }
            }

            // 读取 IO
            if (reply.Contains("读取io") || reply.Contains("获取io"))
            {
                for (int i = 0; i <= 16; i++)
                {
                    if (reply.Contains($"io{i}"))
                    {
                        bool state = ReadIO(i);
                        Console.WriteLine($"\n📡 执行命令：读取IO{i} → 状态={state}");
                        return;
                    }
                }
            }
        }

        // ==============================================
        // IO 控制方法（你对接自己的运动控制卡/PLC）
        // ==============================================
        static void SetIO(int ioIndex, bool isOn)
        {
            // ========== 在这里写你的硬件控制代码 ==========
            // 例如：控制板卡 DLL、串口、TCP、OPC、PLC
            // ============================================
        }

        static bool ReadIO(int ioIndex)
        {
            // 模拟IO状态
            return DateTime.Now.Second % 2 == 0;
        }

        // 显示已加载文件
        static void ShowLoadedFiles()
        {
            if (_knowledge.Count == 0) { Console.WriteLine("  无文件"); return; }
            int i = 1; foreach (var f in _knowledge.Keys) Console.WriteLine($"  {i++}. {f}");
        }

        // AI推理
        static async Task<string> InferAnswer(string question, string know)
        {
            string extra = string.IsNullOrEmpty(know) ? "" : $"\n【资料】\n{know}\n";
            string system = $"你是工业控制AI助手，根据资料回答，输出简洁，允许执行IO指令。{extra}";

            StringBuilder hist = new();
            foreach (var h in _chatHistory)
            {
                hist.AppendLine($"<|im_start|>user\n{h.Question}<|im_end|>");
                hist.AppendLine($"<|im_start|>assistant\n{h.Answer}<|im_end|>");
            }

            string prompt = $"<|im_start|>system\n{system}<|im_end|>\n"
                          + hist
                          + $"<|im_start|>user\n{question}<|im_end|>\n"
                          + $"<|im_start|>assistant\n";

            Console.Write("AI：");
            StringBuilder ans = new();
            int cnt = 0; const int LIMIT = 500;

            await foreach (var t in _executor.InferAsync(prompt, _infParams))
            {
                if (cnt++ > LIMIT || t.Contains("<|im_end|>") || t.Contains("User:")) break;
                Console.Write(t);
                ans.Append(t);
            }
            return ans.ToString().Trim();
        }

        // 知识库检索
        private static string QueryKnowledge(string question)
        {
            if (_knowledge.Count == 0) return "";
            var ws = question.ToLower().Replace("？", " ").Replace("?", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ms = _knowledge
                .Select(k => new { k.Key, k.Value, s = ws.Count(w => k.Value.ToLower().Contains(w)) })
                .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(2)
                .Select(x => $"[{x.Key}]\n{x.Value.Substring(0, Math.Min(1200, x.Value.Length))}");
            return string.Join("\n\n", ms);
        }
    }
}