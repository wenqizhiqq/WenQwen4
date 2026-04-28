using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace QwenConsole3
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
         
        static async Task Main2(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("正在加载模型...");

            // 屏蔽模型加载日志
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

            // 恢复控制台输出
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            // 打印和截图完全一致的欢迎界面
            Console.WriteLine("================================================================================================");
            Console.WriteLine("                              本地离线大模型聊天助理");
            Console.WriteLine($"模型：  E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf");
            Console.WriteLine("          纯本地运行，无需联网，保护隐私");
            Console.WriteLine("    输入 exit 退出，直接输入内容开始对话");
            Console.WriteLine("================================================================================================");
            Console.WriteLine();

            while (true)
            {
                Console.Write("你：");
                string input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                if (input.ToLower() == "exit") break;

                await InferAnswer(input);
                Console.WriteLine("\n");
            }

            _context?.Dispose();
            _model?.Dispose();
        }

        static async Task InferAnswer(string question)
        {
            // 单次对话，无任何上下文记忆
            string prompt = $@"<|im_start|>system
你是一个有用的AI助手<|im_end|>
<|im_start|>user
{question}<|im_end|>
<|im_start|>assistant
";

            Console.Write("AI：");
            await foreach (var token in _executor.InferAsync(prompt, _infParams))
            {
                if (token.Contains("<|im_end|>")) break;
                Console.Write(token);
            }
        }
    }
}