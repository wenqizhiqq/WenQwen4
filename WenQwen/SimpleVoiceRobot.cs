using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace SimpleVoiceChatRobot
{
    public class SimpleVoiceRobot
    {
        private WaveInEvent _mic;
        private WaveOutEvent _speaker;
        private BufferedWaveProvider _buffer;
        private List<byte> _audioBytes = new();
        private bool _isTalking = false;

        public SimpleVoiceRobot()
        {
            // ✅ 修复 WaveFormat 错误（标准写法）
            _mic = new WaveInEvent();
            _mic.WaveFormat = new WaveFormat(16000, 16, 1); // 16kHz 16位 单声道

            _mic.DataAvailable += OnMicData;

            _speaker = new WaveOutEvent();
            _buffer = new BufferedWaveProvider(_mic.WaveFormat);
            _speaker.Init(_buffer);
            _speaker.Play();

            Console.WriteLine("✅ 语音机器人已启动（纯字节流版）");
            _mic.StartRecording();
        }

        // 实时读取麦克风字节
        private void OnMicData(object sender, WaveInEventArgs e)
        {
            _audioBytes.AddRange(e.Buffer);
            int volume = GetVolume(e.Buffer);

            if (volume > 500)
            {
                _isTalking = true;
                Console.Write("🎤 正在说话... ");
            }
            else if (_isTalking && _audioBytes.Count > 8000)
            {
                _isTalking = false;
                Console.WriteLine("\n✅ 检测到语音结束！");
                RobotReply();
                _audioBytes.Clear();
            }
        }

        // 纯字节计算音量
        private int GetVolume(byte[] buffer)
        {
            int sum = 0;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                sum += Math.Abs(sample);
            }
            return sum / (buffer.Length / 2);
        }

        // 机器人回复（纯波形生成，无系统TTS）
        private void RobotReply()
        {
            Console.WriteLine("🤖 机器人：我听到你的声音了！");
            byte[] voice = GenerateRobotVoice();
            _buffer.ClearBuffer();
            _buffer.AddSamples(voice, 0, voice.Length);
        }

        // 生成机器人电子音
        private byte[] GenerateRobotVoice()
        {
            List<byte> data = new();
            int sampleRate = 16000;
            int duration = 1;

            for (int i = 0; i < sampleRate * duration; i++)
            {
                double wave = Math.Sin(i * 0.015) * 15000;
                short sample = (short)wave;
                byte[] b = BitConverter.GetBytes(sample);
                data.Add(b[0]);
                data.Add(b[1]);
            }
            return data.ToArray();
        }
    }
}