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
using System.Windows.Forms;

namespace AutoTaskClickerWithOverlay
{
    static class Program
    {
        private const string OllamaApiUrl = "http://localhost:11434/api/chat";
        private const string ModelName = "qwen3:0.6b";

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;
        private const byte VK_RETURN = 0x0D;

        public static LogForm LogForm;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 启动悬浮日志窗口
            LogForm = new LogForm();
            LogForm.Show();

            // 启动控制台交互循环
            Task.Run(async () => await RunConsoleLoop());

            Application.Run();
        }

        static async Task RunConsoleLoop()
        {
            Console.WriteLine("===== 全自动任务执行助手（带悬浮日志）=====");
            Console.WriteLine("指令格式举例：");
            Console.WriteLine("在搜索框输入微信");
            Console.WriteLine("打开浏览器（找不到就桌面找）");
            Console.WriteLine("输入 exit 退出\n");

            while (true)
            {
                Console.Write("请输入你的任务：");
                var input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input) || input.ToLower() == "exit") break;

                await RunAutoTask(input);
                LogForm.AddLog("任务执行完成，等待新指令...");
                Console.WriteLine("====================================\n");
            }

            LogForm.AddLog("程序即将退出");
            await Task.Delay(2000);
            Application.Exit();
        }

        static async Task RunAutoTask(string task)
        {
            try
            {
                LogForm.AddLog($"【新任务】{task}");

                // 1. 处理搜索框输入任务
                if (task.Contains("搜索框输入"))
                {
                    string keyword = Regex.Match(task, @"输入\s*(.+)").Groups[1].Value;
                    await DoSearchInputTask(keyword);
                    return;
                }

                // 2. 普通任务：先在当前屏幕找
                LogForm.AddLog("正在截取当前屏幕...");
                using var screen = CaptureScreen();
                string tempImg = Path.Combine(Path.GetTempPath(), "screen.png");
                screen.Save(tempImg, ImageFormat.Png);

                LogForm.AddLog("正在让AI分析目标位置...");
                string prompt = $@"
用户任务：{task}
请分析当前屏幕：
1. 是否能找到任务需要点击的目标？
2. 如果能，返回坐标：x,y（0~1）
3. 如果不能，返回：not_found";

                string aiResult = await CallOllama(tempImg, prompt);
                LogForm.AddLog($"AI判断结果：{aiResult}");

                var (x, y) = ParsePos(aiResult);

                if (x >= 0 && y >= 0)
                {
                    int w = Screen.PrimaryScreen.Bounds.Width;
                    int h = Screen.PrimaryScreen.Bounds.Height;
                    int cx = (int)(x * w);
                    int cy = (int)(y * h);

                    LogForm.AddLog($"找到目标，点击坐标：({cx},{cy})");
                    Click(cx, cy);
                }
                else
                {
                    LogForm.AddLog("未找到目标，自动切换到桌面查找...");
                    await FindOnDesktop(task);
                }
            }
            catch (Exception ex)
            {
                LogForm.AddLog($"❌ 执行失败：{ex.Message}");
            }
        }

        // 点击Windows搜索框并输入文本
        static async Task DoSearchInputTask(string keyword)
        {
            LogForm.AddLog($"执行「搜索框输入{keyword}」任务");

            // 打开Windows搜索框（Win+S）
            LogForm.AddLog("正在打开Windows搜索...");
            keybd_event(0x5B, 0, 0, 0);
            keybd_event((byte)'S', 0, 0, 0);
            keybd_event((byte)'S', 0, 2, 0);
            keybd_event(0x5B, 0, 2, 0);
            await Task.Delay(1000);

            // 输入关键词
            LogForm.AddLog($"正在输入：{keyword}");
            SendKeys(keyword);
            await Task.Delay(500);

            // 按回车搜索
            keybd_event(VK_RETURN, 0, 0, 0);
            keybd_event(VK_RETURN, 0, 2, 0);
            LogForm.AddLog("已发送搜索指令");
        }

        // 找不到时自动切换到桌面查找
        static async Task FindOnDesktop(string task)
        {
            // 切换到桌面（Win+D）
            LogForm.AddLog("正在切换到桌面...");
            keybd_event(0x5B, 0, 0, 0);
            keybd_event((byte)'D', 0, 0, 0);
            keybd_event((byte)'D', 0, 2, 0);
            keybd_event(0x5B, 0, 2, 0);
            await Task.Delay(1000);

            // 截图桌面
            LogForm.AddLog("正在截取桌面...");
            using var desktopScreen = CaptureScreen();
            string tempImg = Path.Combine(Path.GetTempPath(), "desktop.png");
            desktopScreen.Save(tempImg, ImageFormat.Png);

            // 分析桌面图标
            LogForm.AddLog("正在AI分析桌面图标...");
            string prompt = $@"
用户需要找：{task}
当前是Windows桌面，请找到对应的图标，返回中心坐标：x,y（0~1）
找不到就返回：not_found";

            string aiResult = await CallOllama(tempImg, prompt);
            LogForm.AddLog($"AI判断结果：{aiResult}");

            var (x, y) = ParsePos(aiResult);

            if (x >= 0 && y >= 0)
            {
                int w = Screen.PrimaryScreen.Bounds.Width;
                int h = Screen.PrimaryScreen.Bounds.Height;
                int cx = (int)(x * w);
                int cy = (int)(y * h);

                LogForm.AddLog($"在桌面找到目标，双击打开：({cx},{cy})");
                Click(cx, cy);
                await Task.Delay(100);
                Click(cx, cy); // 双击
            }
            else
            {
                LogForm.AddLog("桌面也未找到，任务结束");
            }
        }

        // 调用Ollama
        static async Task<string> CallOllama(string imgPath, string prompt)
        {
            string base64 = Convert.ToBase64String(File.ReadAllBytes(imgPath));

            var req = new
            {
                model = ModelName,
                messages = new[] { new { role = "user", content = prompt, images = new[] { base64 } } },
                stream = false
            };

            using var client = new HttpClient();
            var json = JsonSerializer.Serialize(req);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = await client.PostAsync(OllamaApiUrl, content);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        // 截图
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

        // 模拟键盘输入
        static void SendKeys(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    bool shift = char.IsUpper(c);
                    if (shift) keybd_event(0x10, 0, 0, 0);
                    keybd_event((byte)char.ToLower(c), 0, 0, 0);
                    keybd_event((byte)char.ToLower(c), 0, 2, 0);
                    if (shift) keybd_event(0x10, 0, 2, 0);
                }
                else if (char.IsDigit(c))
                {
                    keybd_event((byte)c, 0, 0, 0);
                    keybd_event((byte)c, 0, 2, 0);
                }
            }
        }
    }

    // 悬浮日志窗口
    public class LogForm : Form
    {
        private readonly ListBox listBox;

        public LogForm()
        {
            // 窗口基本设置
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            ForeColor = Color.White;

            // 半透明设置（兼容版）
            BackColor = Color.Black; // 先设为纯黑
            Opacity = 0.8; // 用 Opacity 属性实现半透明，而不是 Color.FromArgb

            // 窗口位置：屏幕右下角
            Width = 300;
            Height = 200;
            var screen = Screen.PrimaryScreen.Bounds;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(screen.Right - Width - 10, screen.Bottom - Height - 50);

            // 日志列表框
            listBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black, // 列表框背景和窗口保持一致
                ForeColor = Color.White,
                Font = new Font("Consolas", 9)
            };
            Controls.Add(listBox);
        }

        public void AddLog(string text)
        {
            if (listBox.InvokeRequired)
            {
                listBox.Invoke(new Action(() => AddLog(text)));
                return;
            }

            string logLine = $"[{DateTime.Now:HH:mm:ss}] {text}";
            listBox.Items.Add(logLine);
            listBox.SelectedIndex = listBox.Items.Count - 1;

            // 限制日志条数，防止溢出
            if (listBox.Items.Count > 100)
                listBox.Items.RemoveAt(0);
        }
    }
}