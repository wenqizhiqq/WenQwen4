using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LLama;
using LLama.Common;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Threading.Tasks;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace VisionAGI
{
    public partial class VisionAGI : Form
    {
        private LLamaWeights _model;
        private InteractiveExecutor _executor;
        private readonly Dictionary<string, Bitmap> _templateLibrary = new();

        private readonly InferenceParams _infParams = new()
        {
            MaxTokens = 150, 
            AntiPrompts = new[] { "<|im_end|>" }
        };

        public VisionAGI()
        {
            InitializeComponent();
            CheckForIllegalCrossThreadCalls = false;
            LoadModel();
            Log("✅ 视觉自主AGI 启动完成 · 图像识别 + Qwen本地大模型");
        }

        private void LoadModel()
        {
            try
            {
                string modelPath = ModelLocator.Resolve("E:\\wqz\\model\\qwen2.5-7b-instruct-1m-q4_k_m.gguf");
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 4096,
                    GpuLayerCount = 0,
                    Threads = Environment.ProcessorCount
                };

                _model = LLamaWeights.LoadFromFile(parameters);
                var context = new LLamaContext(_model, parameters);
                _executor = new InteractiveExecutor(context);
                Log("🤖 Qwen2.5 7B 模型加载成功");
            }
            catch (Exception ex)
            {
                Log($"❌ 模型加载失败：{ex.Message}");
            }
        }

        private void btnLearn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtBtnName.Text))
            {
                MessageBox.Show("请输入按钮名称，例如：启动、确认");
                return;
            }

            Log($"👀 3秒后截取鼠标位置，学习按钮：{txtBtnName.Text}");
            Task.Delay(3000).Wait();

            System.Drawing.Point p = Cursor.Position;
            Rectangle rect = new(p.X - 20, p.Y - 20, 40, 40);
            Bitmap bmp = new(rect.Width, rect.Height);

            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size);

            _templateLibrary[txtBtnName.Text.Trim()] = bmp;
            Log($"✅ 已学习：{txtBtnName.Text}");
        }

        private async void btnExecute_Click(object sender, EventArgs e)
        {
            if (_executor == null || _templateLibrary.Count == 0)
            {
                Log("⚠️ 请先加载模型并学习至少一个按钮");
                return;
            }

            string prompt = txtCommand.Text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            Log($"🧠 指令：{prompt}");
            Log("🔍 AGI 思考中...");

            string buttons = string.Join("、", _templateLibrary.Keys);
            string system = $"你是桌面AGI，只输出要点击的按钮名称，已知按钮：{buttons}";
            string fullPrompt = $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n";

            string target = "";
            await foreach (var t in _executor.InferAsync(fullPrompt, _infParams))
            {
                target += t;
                if (target.Length > 40) break;
            }

            target = target.Trim();
            Log($"🤖 AGI 决策：点击 {target}");

            if (_templateLibrary.ContainsKey(target))
                AutoClick(target);
            else
                Log($"❌ 未找到按钮：{target}");
        }

        private void AutoClick(string btnName)
        {
            try
            {
                Log($"🔎 图像识别中：{btnName}");
                Bitmap temp = _templateLibrary[btnName];

                using Bitmap screen = new(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
                using (Graphics g = Graphics.FromImage(screen))
                    g.CopyFromScreen(0, 0, 0, 0, screen.Size);

                using Mat matScreen = BitmapConverter.ToMat(screen);
                using Mat matTemp = BitmapConverter.ToMat(temp);
                using Mat result = new();

                Cv2.MatchTemplate(matScreen, matTemp, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                if (maxVal < 0.85)
                {
                    Log("❌ 未找到目标");
                    return;
                }

                int cx = maxLoc.X + temp.Width / 2;
                int cy = maxLoc.Y + temp.Height / 2;

                Mouse.Move(cx, cy);
                Task.Delay(200).Wait();
                Mouse.LeftClick();

                Log($"🎯 点击成功！相似度：{maxVal:F2}\r\n");
            }
            catch (Exception ex)
            {
                Log($"⚠️ 执行错误：{ex.Message}");
            }
        }

        private void Log(string text)
        {
            rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
            rtbLog.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _model?.Dispose();
            base.OnFormClosing(e);
        }

        #region 界面布局
        private RichTextBox rtbLog;
        private TextBox txtBtnName;
        private Button btnLearn;
        private TextBox txtCommand;
        private Button btnExecute;

        private void InitializeComponent()
        {
            this.rtbLog = new RichTextBox();
            this.txtBtnName = new TextBox();
            this.btnLearn = new Button();
            this.txtCommand = new TextBox();
            this.btnExecute = new Button();
            this.SuspendLayout();

            this.rtbLog.Location = new Point(10, 10);
            this.rtbLog.Size = new Size(760, 300);
            this.rtbLog.ReadOnly = true;
            this.rtbLog.Font = new Font("微软雅黑", 9);

            this.txtBtnName.Location = new Point(10, 320);
            this.txtBtnName.Size = new Size(180, 24);
            this.txtBtnName.PlaceholderText = "按钮名称：启动/确认/关闭";

            this.btnLearn.Location = new Point(200, 320);
            this.btnLearn.Size = new Size(100, 24);
            this.btnLearn.Text = "📷 学习按钮";
            this.btnLearn.Click += btnLearn_Click;

            this.txtCommand.Location = new Point(10, 355);
            this.txtCommand.Size = new Size(600, 24);
            this.txtCommand.PlaceholderText = "输入指令，例如：帮我点击启动按钮";

            this.btnExecute.Location = new Point(620, 355);
            this.btnExecute.Size = new Size(150, 24);
            this.btnExecute.Text = "🚀 AGI 执行";
            this.btnExecute.Click += btnExecute_Click;

            this.ClientSize = new Size(780, 390);
            this.Text = "🔥 Qwen视觉自主AGI · 兼容稳定版";
            this.Controls.Add(this.rtbLog);
            this.Controls.Add(this.txtBtnName);
            this.Controls.Add(this.btnLearn);
            this.Controls.Add(this.txtCommand);
            this.Controls.Add(this.btnExecute);
            this.ResumeLayout(false);
        }
        #endregion
    }

    public static class Mouse
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(int flags, int dx, int dy, int data, int extra);

        private const int LEFTDOWN = 0x02;
        private const int LEFTUP = 0x04;

        public static void Move(int x, int y) => SetCursorPos(x, y);
        public static void LeftClick()
        {
            mouse_event(LEFTDOWN, 0, 0, 0, 0);
            mouse_event(LEFTUP, 0, 0, 0, 0);
        }
    }
}