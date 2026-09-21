using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using MissionPlanner.Utilities;

namespace GStreamerV109
{
    public class Plugin : MissionPlanner.Plugin.Plugin
    {
        ToolStripMenuItem root, start, settings, stop, logMenu;
        Form host, cfg;
        Panel panel;
        Label status;
        System.Windows.Forms.Timer timer;
        Process gst;
        IntPtr videoWnd = IntPtr.Zero;

        readonly object logLock = new object();
        readonly StringBuilder gstLog = new StringBuilder();
        const int MaxLogChars = 50000;

        bool active = false;
        bool manualStop = false;
        DateTime nextRetry = DateTime.MinValue;

        // ---------- V10.9 smart reconnect ----------
        DateTime gstLaunchAt = DateTime.MinValue;
        volatile bool rtspPlayStarted = false;
        volatile bool attemptReachedPlay = false;
        int udpTimeoutSec = 5;
        int connectWatchdogSec = 8;

        // ---------- V10.9 smart reconnect ----------
        int reconnectFailureCount = 0;
        const int MinServerRecoveryWaitSec = 8;
        const int MaxReconnectBackoffSec = 30;

        // ---------- 기본 설정 ----------
        string url = "rtsp://192.168.50.25:8554/main.264";
        string proto = "UDP";
        int latency = 300;
        int buffers = 6;
        int leaky = 2;
        int tcpTimeout = 3;
        int retryDelay = 2;

        bool retrans = false;
        bool doRtcp = true;
        bool rtspKeepAlive = true;
        bool proxyBypass = true;
        bool autoRetry = true;

        // ---------- V10.9 안전 손실 대응 ----------
        // 0 = 호환성 우선 (기존 decodebin3 경로)
        // 1 = 안전 저지연 (decodebin3 유지 + rtspsrc drop-on-latency)
        int lossMode = 0;

        // 실제 성공한 RTP caps에서 자동 감지. 재생 파이프라인은 이 값으로 강제하지 않습니다.
        string detectedCodec = "미감지";
        string detectedPayload = "-";
        string detectedClockRate = "-";

        // ---------- V10.2: 사용할 GStreamer를 직접 지정 ----------
        string gstLaunchPath = "";
        string gstVersionText = "미확인";
        string lastProbedGstPath = "";

        // ---------- V10.1 호환성 자동 감지 ----------
        bool waitKeyframeSupported = false;
        bool requestKeyframeSupported = false;

        const string K10 = "gst_v10_";
        const string K9 = "gst_v9_";

        public override string Name { get { return "GStreamer Smart Reconnect V10.9"; } }
        public override string Version { get { return "10.9"; } }
        public override string Author { get { return "OpenAI"; } }
        public override bool Init() { return true; }

        public override bool Loaded()
        {
            LoadCfg();
            MakeHost();

            root = new ToolStripMenuItem("GStreamer 영상 전용 V10.9");

            start = new ToolStripMenuItem("영상 팝아웃 시작");
            settings = new ToolStripMenuItem("실시간 설정");
            stop = new ToolStripMenuItem("영상 연결 종료");
            logMenu = new ToolStripMenuItem("GStreamer 로그 보기");

            start.Click += delegate { StartVideo(); };
            settings.Click += delegate { ShowCfg(); };
            stop.Click += delegate { StopVideo(); };
            logMenu.Click += delegate { ShowLog(); };

            root.DropDownItems.Add(start);
            root.DropDownItems.Add(settings);
            root.DropDownItems.Add(stop);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(logMenu);
            Host.FDMenuHud.Items.Add(root);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += Tick;
            timer.Start();

            return true;
        }

        public override bool Loop() { return true; }

        public override bool Exit()
        {
            active = false;
            manualStop = true;
            try { if (timer != null) timer.Stop(); } catch { }
            KillGst();
            return true;
        }

        // ============================================================
        // Video host
        // ============================================================

        void MakeHost()
        {
            panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = System.Drawing.Color.Black;
            panel.Resize += delegate { ResizeVideo(); };

            status = new Label();
            status.AutoSize = true;
            status.ForeColor = System.Drawing.Color.White;
            status.BackColor = System.Drawing.Color.Black;
            status.Font = new System.Drawing.Font(
                status.Font.FontFamily, 14, System.Drawing.FontStyle.Bold);
            status.Left = 20;
            status.Top = 20;
            status.Text = "영상 연결 대기";
            panel.Controls.Add(status);

            host = new Form();
            host.Text = "GStreamer Video";
            host.StartPosition = FormStartPosition.CenterScreen;
            host.ClientSize = new System.Drawing.Size(1280, 720);
            host.MinimumSize = new System.Drawing.Size(400, 300);
            host.Controls.Add(panel);

            host.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    active = false;
                    manualStop = true;
                    KillGst();
                    manualStop = false;
                }
            };
        }

        void StartVideo()
        {
            if (host == null || host.IsDisposed)
                MakeHost();

            if (!active)
            {
                ClearGstLog();
                reconnectFailureCount = 0;
                nextRetry = DateTime.MinValue;
            }

            active = true;
            manualStop = false;
