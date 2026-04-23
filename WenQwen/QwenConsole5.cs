using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace QwenConsole5
{
    class Program
    {
        private static LLamaWeights _model;
        private static LLamaContext _context;
        private static InteractiveExecutor _executor;

        private static readonly InferenceParams _infParams = new InferenceParams
        {
            MaxTokens = 800,
            AntiPrompts = new[] { "<|im_end|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f }
        };

        private static string _modelPath = "E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf";

        // 历史文件
        private static readonly string _historyFile = "chat_history.json";
        private static List<ChatHistory> _chatHistory = new List<ChatHistory>();

        private class ChatHistory
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        static async Task Main5(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // ========== 启动时只加载一次 ==========
            LoadHistory();

            Console.WriteLine("正在加载模型...");

            // 屏蔽加载日志
            using (var nullWriter = new StringWriter())
            {
                Console.SetOut(nullWriter);
                var parameters = new ModelParams(_modelPath)
                {
                    ContextSize = 8192,
                    GpuLayerCount = 99,
                    Threads = Environment.ProcessorCount,
                };

                _model = LLamaWeights.LoadFromFile(parameters);
                _context = new LLamaContext(_model, parameters);
                _executor = new InteractiveExecutor(_context);
            }

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            Console.WriteLine("================================================================================================");
            Console.WriteLine("                              本地离线大模型聊天助理");
            Console.WriteLine($"模型：  E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf");
            Console.WriteLine("          纯本地运行，无需联网，保护隐私");
            Console.WriteLine("    输入 exit 退出，输入 clear 清空记忆");
            Console.WriteLine("================================================================================================");
            Console.WriteLine();

            while (true)
            {
                Console.Write("你：");
                string input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                if (input.ToLower() == "exit")
                    break;

                if (input.ToLower() == "clear")
                {
                    _chatHistory.Clear();
                    Console.WriteLine("[系统] 记忆已清空\n");
                    continue;
                }

                await InferAnswer(input);
                Console.WriteLine("\n");
            }

            // ========== 退出时统一保存一次 ==========
            SaveHistory();

            _context?.Dispose();
            _model?.Dispose();
        }

        static async Task InferAnswer(string question)
        {
            _chatHistory.Add(new ChatHistory { Role = "user", Content = question });

            StringBuilder promptBuilder = new StringBuilder();
            promptBuilder.Append(@"<|im_start|>system
你是一个有用的AI助手<|im_end|>
");

            foreach (var chat in _chatHistory)
            {
                promptBuilder.Append($@"<|im_start|>{chat.Role}
{chat.Content}<|im_end|>
");
            }

            promptBuilder.Append(@"<|im_start|>assistant
");
            string prompt = promptBuilder.ToString();

            Console.Write("AI：");
            StringBuilder aiAnswer = new StringBuilder();

            await foreach (var token in _executor.InferAsync(prompt, _infParams))
            {
                if (token.Contains("<|im_end|>")) break;
                Console.Write(token);
                aiAnswer.Append(token);
            }

            _chatHistory.Add(new ChatHistory { Role = "assistant", Content = aiAnswer.ToString() });
        }

        // 启动加载
        private static void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFile))
                {
                    string json = File.ReadAllText(_historyFile, Encoding.UTF8);
                    _chatHistory = JsonSerializer.Deserialize<List<ChatHistory>>(json) ?? new List<ChatHistory>();
                }
            }
            catch
            {
                _chatHistory = new List<ChatHistory>();
            }
        }

        // 退出保存
        private static void SaveHistory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_chatHistory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_historyFile, json, Encoding.UTF8);
            }
            catch { }
        }
    }
}