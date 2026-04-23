using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;
using NAudio.Wave;
using Whisper.net;

namespace QwenVoiceChat
{
    class Program
    {
        // 模型路径
        private static string _llmModelPath = "E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf";
        private static string _asrModelPath = "ggml-small.bin"; // 换成你下载的：base.bin / small.bin

        // LLM
        private static LLamaWeights _llmModel;
        private static LLamaContext _llmContext;
        private static InteractiveExecutor _executor;

        // 实时语音
        private static WaveInEvent _waveIn;
        private static WaveOutEvent _realTimePlayback;
        private static BufferedWaveProvider _bufferedWaveProvider;

        // ASR
        private static WhisperProcessor _whisperProcessor;
        private static byte[] _audioBuffer = Array.Empty<byte>();
        private static bool _isProcessing = false;

        // 推理参数
        private static readonly InferenceParams _infParams = new()
        {
            MaxTokens = 500,
            AntiPrompts = new[] { "<|im_end|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f }
        };

        static async Task Main4(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            await InitLLM();
            InitASR();
            InitRealTimeVoiceChat(); // 👈 实时语音聊天

            Console.WriteLine("==============================================================================");
            Console.WriteLine("                    本地离线·实时语音聊天AI");
            Console.WriteLine("          说话即可听见自己声音 + AI自动回答并朗读");
            Console.WriteLine("              纯本地运行 | 无需联网 | 实时对讲");
            Console.WriteLine("==============================================================================");

            _waveIn.StartRecording();
            Console.WriteLine("🎤 实时语音已启动，开始说话……\n");

            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    break;
                await Task.Delay(100);
            }

            Cleanup();
        }

        #region 初始化
        static async Task InitLLM()
        {
            var parameters = new ModelParams(_llmModelPath)
            {
                ContextSize = 8192,
                GpuLayerCount = 99,
                Threads = Environment.ProcessorCount
            };

            using var sw = new StringWriter();
            Console.SetOut(sw);
            _llmModel = LLamaWeights.LoadFromFile(parameters);
            _llmContext = new LLamaContext(_llmModel, parameters);
            _executor = new InteractiveExecutor(_llmContext);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        static void InitASR()
        {
            var factory = WhisperFactory.FromPath(_asrModelPath);
            _whisperProcessor = factory.CreateBuilder().WithLanguage("zh").Build();
        }

        static void InitRealTimeVoiceChat()
        {
            // 麦克风输入
            _waveIn = new WaveInEvent();
            _waveIn.WaveFormat = new WaveFormat(16000, 16, 1);

            // 实时回放（你说话立刻听见自己）
            _realTimePlayback = new WaveOutEvent();
            _bufferedWaveProvider = new BufferedWaveProvider(_waveIn.WaveFormat);
            _realTimePlayback.Init(_bufferedWaveProvider);
            _realTimePlayback.Play();

            // 麦克风数据 → 一边播放，一边识别
            _waveIn.DataAvailable += async (s, e) =>
            {
                // 实时播放自己声音
                _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

                // 累积语音用于识别
                var newBuf = new byte[_audioBuffer.Length + e.BytesRecorded];
                Buffer.BlockCopy(_audioBuffer, 0, newBuf, 0, _audioBuffer.Length);
                Buffer.BlockCopy(e.Buffer, 0, newBuf, _audioBuffer.Length, e.BytesRecorded);
                _audioBuffer = newBuf;

                // 说话停顿后识别
                if (e.BytesRecorded < 100 && _audioBuffer.Length > 2000 && !_isProcessing)
                {
                    await ProcessVoiceInput(_audioBuffer);
                    _audioBuffer = Array.Empty<byte>();
                }
            };
        }
        #endregion

        #region 语音识别 + AI回答 + 朗读
        static async Task ProcessVoiceInput(byte[] data)
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                // 1. 语音转文字
                using var ms = new MemoryStream();
                using var writer = new WaveFileWriter(ms, new WaveFormat(16000, 16, 1));
                writer.Write(data, 0, data.Length);
                ms.Position = 0;

                var sb = new StringBuilder();
                await foreach (var seg in _whisperProcessor.ProcessAsync(ms))
                    sb.Append(seg.Text);

                var text = sb.ToString().Trim();
                if (string.IsNullOrEmpty(text))
                {
                    _isProcessing = false;
                    return;
                }

                Console.WriteLine($"你：{text}");

                // 2. AI 生成回答
                Console.Write("AI：");
                var prompt = $@"<|im_start|>system
你是有用的AI助手，简短回答<|im_end|>
<|im_start|>user
{text}<|im_end|>
<|im_start|>assistant
";

                var ansSb = new StringBuilder();
                await foreach (var t in _executor.InferAsync(prompt, _infParams))
                {
                    if (t.Contains("<|im_end|>")) break;
                    Console.Write(t);
                    ansSb.Append(t);
                }
                Console.WriteLine();

                // 3. 朗读AI回答
                using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
                using var audio = new MemoryStream();
                synth.SetOutputToWaveStream(audio);
                synth.Speak(ansSb.ToString());
                audio.Position = 0;

                using var reader = new WaveFileReader(audio);
                var ttsPlayer = new WaveOutEvent();
                ttsPlayer.Init(reader);
                ttsPlayer.Play();
                while (ttsPlayer.PlaybackState == PlaybackState.Playing)
                    await Task.Delay(50);
                ttsPlayer.Dispose();
            }
            catch
            {
                // 忽略识别错误
            }
            finally
            {
                _isProcessing = false;
            }
        }
        #endregion

        static void Cleanup()
        {
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _realTimePlayback.Dispose();
            _llmContext.Dispose();
            _llmModel.Dispose();
        }
    }
}