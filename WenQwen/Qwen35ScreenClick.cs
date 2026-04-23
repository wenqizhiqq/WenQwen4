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

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("shell32.dll")]
        private static extern int ShellExecute(int hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;
        private const byte VK_RETURN = 0x0D;
        private const byte VK_ESCAPE = 0x1B;

        static async Task Main(string[] args)
        {
            Console.WriteLine("===== 全自动任务执行助手（含搜索+桌面查找）=====");
            Console.WriteLine("指令格式举例：");
            Console.WriteLine("在Windows搜索框输入微信");
            Console.WriteLine("打开浏览器（找不到就桌面找）");
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

                // 步骤1：判断任务是否需要在搜索框输入
                if (task.Contains("搜索框输入") || task.Contains("在搜索框输入"))
                {
                    string keyword = Regex.Match(task, @"输入\s*(.+)").Groups[1].Value;
                    await DoSearchInputTask(keyword);
                    return;
                }

                // 步骤2：普通任务：先找按钮，找不到就桌面找
                using var screen = CaptureScreen();
                string tempImg = Path.Combine(Path.GetTempPath(), "screen.png");
                screen.Save(tempImg, ImageFormat.Png);

                string prompt = $@"
用户任务：{task}
请分析当前屏幕：
1. 是否能找到任务需要点击的目标？
2. 如果能，返回坐标：x,y（0~1）
3. 如果不能，返回：not_found
只返回结果，不要多余描述";

                string aiResult = await CallOllama(tempImg, prompt);
                Console.WriteLine($"AI判断：{aiResult}");

                var (x, y) = ParsePos(aiResult);

                if (x >= 0 && y >= 0)
                {
                    int w = Screen.PrimaryScreen.Bounds.Width;
                    int h = Screen.PrimaryScreen.Bounds.Height;
                    int cx = (int)(x * w);
                    int cy = (int)(y * h);

                    Console.WriteLine($"找到目标 → 点击 ({cx},{cy})");
                    Click(cx, cy);
                }
                else
                {
                    Console.WriteLine("未找到目标 → 自动到桌面查找");
                    await FindOnDesktop(task);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"执行失败：{ex.Message}");
            }
        }

        // 【核心功能】点击Windows搜索框并输入文本
        static async Task DoSearchInputTask(string keyword)
        {
            Console.WriteLine($"→ 开始执行「搜索框输入{keyword}」任务");

            // 1. 自动打开Windows搜索框（Win键+S）
            keybd_event(0x5B, 0, 0, 0); // Win键按下
            keybd_event((byte)'S', 0, 0, 0); // S键按下
            keybd_event((byte)'S', 0, 2, 0); // S键抬起
            keybd_event(0x5B, 0, 2, 0); // Win键抬起
            await Task.Delay(1000); // 等待搜索框打开

            // 2. 模拟输入文本
            Console.WriteLine($"→ 输入关键词：{keyword}");
            SendKeys(keyword);
            await Task.Delay(500);

            // 3. 按回车搜索
            keybd_event(VK_RETURN, 0, 0, 0);
            keybd_event(VK_RETURN, 0, 2, 0);
            await Task.Delay(1000);
        }

        // 【核心功能】找不到时自动切换到桌面查找
        static async Task FindOnDesktop(string task)
        {
            Console.WriteLine("→ 切换到桌面查找目标...");
            keybd_event(0x5B, 0, 0, 0); // Win键+D
            keybd_event((byte)'D', 0, 0, 0);
            keybd_event((byte)'D', 0, 2, 0);
            keybd_event(0x5B, 0, 2, 0);
            await Task.Delay(1000);

            // 重新截图分析桌面
            using var desktopScreen = CaptureScreen();
            string tempImg = Path.Combine(Path.GetTempPath(), "desktop.png");
            desktopScreen.Save(tempImg, ImageFormat.Png);

            string prompt = $@"
用户需要找：{task}
当前是Windows桌面，请找到对应的图标或文件，返回它的中心坐标：x,y（0~1）
找不到就返回：not_found";

            string aiResult = await CallOllama(tempImg, prompt);
            var (x, y) = ParsePos(aiResult);

            if (x >= 0 && y >= 0)
            {
                int w = Screen.PrimaryScreen.Bounds.Width;
                int h = Screen.PrimaryScreen.Bounds.Height;
                int cx = (int)(x * w);
                int cy = (int)(y * h);

                Console.WriteLine($"→ 在桌面找到目标 → 双击 ({cx},{cy})");
                Click(cx, cy);
                await Task.Delay(100);
                Click(cx, cy); // 双击打开
            }
            else
            {
                Console.WriteLine("→ 桌面也未找到，任务结束");
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
            var m = Regex.Match(text, @"(\d[\d\.]*)\s*[,，]\s*(\d[\d\.]*)");
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

        // 模拟键盘输入文本
        static void SendKeys(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    bool shift = char.IsUpper(c);
                    if (shift) keybd_event(0x10, 0, 0, 0); // Shift按下
                    keybd_event((byte)char.ToLower(c), 0, 0, 0);
                    keybd_event((byte)char.ToLower(c), 0, 2, 0);
                    if (shift) keybd_event(0x10, 0, 2, 0); // Shift抬起
                }
                else if (char.IsDigit(c))
                {
                    keybd_event((byte)c, 0, 0, 0);
                    keybd_event((byte)c, 0, 2, 0);
                }
                // 其他符号可以自行扩展
            }
        }
    }
}