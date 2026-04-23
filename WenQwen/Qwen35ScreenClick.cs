using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace AutoTaskClicker
{
    class Program
    {
        private const string OllamaApiUrl = "http://localhost:11434/api/chat";
        private const string ModelName = "qwen3:0.6b";

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("shell32.dll")]
        private static extern int ShellExecute(int hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;

        static async Task Main(string[] args)
        {
            Console.WriteLine("===== 全自动任务执行助手 =====");
            Console.WriteLine("指令格式举例：");
            Console.WriteLine("打开微信 → 点击登录 → 找不到就搜索");
            Console.WriteLine("输入 exit 退出\n");

            while (true)
            {
                Console.Write("请输入你的任务：");
                var input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input) || input.ToLower() == "exit") break;

                await RunAutoTask(input);
                Console.WriteLine("====================================\n");
            }
        }

        static async Task RunAutoTask(string task)
        {
            try
            {
                Console.WriteLine($"【任务开始】{task}");

                // 1. 截图当前屏幕
                using var screen = CaptureScreen();
                string tempImg = Path.Combine(Path.GetTempPath(), "screen.png");
                screen.Save(tempImg, ImageFormat.Png);

                // 2. 让AI判断屏幕上有没有目标按钮
                string prompt = $@"
用户任务：{task}
请分析当前屏幕：
1. 是否能找到任务需要点击的按钮？
2. 如果能，返回坐标：x,y（0~1）
3. 如果不能，返回：not_found
只返回结果，不要多余描述";

                string aiResult = await CallOllama(tempImg, prompt);
                Console.WriteLine($"AI判断：{aiResult}");

                // 3. 解析坐标
                var (x, y) = ParsePos(aiResult);

                if (x >= 0 && y >= 0)
                {
                    // 找到 → 点击
                    int w = Screen.PrimaryScreen.Bounds.Width;
                    int h = Screen.PrimaryScreen.Bounds.Height;
                    int cx = (int)(x * w);
                    int cy = (int)(y * h);

                    Console.WriteLine($"找到目标 → 点击 ({cx},{cy})");
                    Click(cx, cy);
                }
                else
                {
                    // 找不到 → 调用 Windows 搜索
                    Console.WriteLine("未找到目标 → 启动 Windows 搜索");
                    WinSearch(task);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"执行失败：{ex.Message}");
            }
        }

        // 调用 Ollama 看图识别
        static async Task<string> CallOllama(string imgPath, string prompt)
        {
            string base64 = Convert.ToBase64String(File.ReadAllBytes(imgPath));

            var req = new
            {
                model = ModelName,
                messages = new[] {
                    new { role = "user", content = prompt, images = new[] { base64 } }
                },
                stream = false
            };

            using var client = new HttpClient();
            var json = JsonSerializer.Serialize(req);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = await client.PostAsync(OllamaApiUrl, content);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        // 屏幕截图
        static Bitmap CaptureScreen()
        {
            var b = Screen.PrimaryScreen.Bounds;
            var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(0, 0, 0, 0, b.Size);
            return bmp;
        }

        // 解析坐标
        static (float x, float y) ParsePos(string text)
        {
            var m = Regex.Match(text, @"(\d[\d\.]*)\s*[,，]\s*(\d[\d\.])");
            if (m.Success)
                return (float.Parse(m.Groups[1].Value), float.Parse(m.Groups[2].Value));
            return (-1, -1);
        }

        // 鼠标点击
        static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        // Windows 搜索（找不到时自动触发）
        static void WinSearch(string keyword)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-screenclip:?search=" + Uri.EscapeDataString(keyword)));
            }
            catch
            {
                ShellExecute(0, "open", "search-ms:", keyword, "", 1);
            }
        }
    }
}