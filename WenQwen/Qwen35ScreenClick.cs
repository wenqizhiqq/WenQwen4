using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OllamaScreenClick
{
    class Program
    {
        // Ollama 默认 API 地址
        private const string OllamaApiUrl = "http://localhost:11434/api/chat";
        private const string ModelName = "qwen3:0.6b";
        private const string Prompt = "图片中需要点击的目标中心点坐标，只输出 x,y，范围 0~1，不要多余文字";

        // 原生鼠标 API
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== .NET8 + Ollama 本地AI自动点击 ===");

            try
            {
                // 1. 截取屏幕
                Console.WriteLine("正在截取屏幕...");
                using var screen = CaptureScreen();
                string tempImg = Path.Combine(Path.GetTempPath(), "screen_temp.png");
                screen.Save(tempImg, ImageFormat.Png);
                Console.WriteLine($"截图已保存到：{tempImg}");

                // 2. 调用 Ollama 识别坐标
                Console.WriteLine("正在调用本地AI识别坐标...");
                string response = await CallOllamaApi(tempImg);
                Console.WriteLine("\n模型返回结果：" + response);

                // 3. 解析坐标
                var (xRatio, yRatio) = ParseCoordinate(response);
                if (xRatio < 0 || yRatio < 0)
                {
                    Console.WriteLine("未识别到有效坐标，程序退出");
                    return;
                }

                // 4. 转换为屏幕像素
                int screenW = Screen.PrimaryScreen.Bounds.Width;
                int screenH = Screen.PrimaryScreen.Bounds.Height;
                int clickX = (int)(xRatio * screenW);
                int clickY = (int)(yRatio * screenH);

                Console.WriteLine($"点击坐标：({clickX}, {clickY})");
                MoveMouseAndClick(clickX, clickY);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"程序出错：{ex.Message}");
            }

            Console.WriteLine("\n按回车键退出...");
            Console.ReadLine();
        }

        #region 调用 Ollama API
        static async Task<string> CallOllamaApi(string imagePath)
        {
            // 图片转 Base64
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            string base64Image = Convert.ToBase64String(imageBytes);

            // 构造请求体
            var request = new
            {
                model = ModelName,
                messages = new[]
                {
                    new { role = "user", content = Prompt, images = new[] { base64Image } }
                },
                stream = false
            };

            using var client = new HttpClient();
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(OllamaApiUrl, content);
            response.EnsureSuccessStatusCode();

            // 解析返回结果
            var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        #endregion

        #region 工具函数
        static Bitmap CaptureScreen()
        {
            var bounds = Screen.PrimaryScreen.Bounds;
            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(0, 0, 0, 0, bounds.Size);
            return bmp;
        }

        static (float x, float y) ParseCoordinate(string text)
        {
            var match = Regex.Match(text, @"(\d+\.?\d*)\s*[,，]\s*(\d+\.?\d*)");
            if (match.Success)
            {
                float x = float.Parse(match.Groups[1].Value);
                float y = float.Parse(match.Groups[2].Value);
                return (x, y);
            }
            return (-1, -1);
        }

        static void MoveMouseAndClick(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
        #endregion
    }
}