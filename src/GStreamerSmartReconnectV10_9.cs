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

            host.Show();
            host.BringToFront();

            if (!Running())
            {
                SetStatus("RTSP 연결 중...");
                StartGst();
            }
        }

        void StopVideo()
        {
            active = false;
            manualStop = true;
            KillGst();
            manualStop = false;
            SetStatus("영상 연결 종료됨");
        }

        // ============================================================
        // Settings UI
        // ============================================================

        void ShowCfg()
        {
            if (cfg != null && !cfg.IsDisposed)
            {
                cfg.Show();
                cfg.BringToFront();
                return;
            }

            cfg = new Form();
            cfg.Text = "GStreamer V10.9 실시간 설정";
            cfg.StartPosition = FormStartPosition.CenterScreen;
            cfg.FormBorderStyle = FormBorderStyle.FixedDialog;
            cfg.MaximizeBox = false;
            cfg.MinimizeBox = false;
            cfg.ClientSize = new System.Drawing.Size(760, 925);

            int lx = 18;
            int cx = 190;
            int y = 18;
            int dy = 36;

            TextBox tGst = new TextBox();
            tGst.Left = cx;
            tGst.Top = y - 3;
            tGst.Width = 420;
            tGst.Text = gstLaunchPath;
            AddLabel("gst-launch-1.0.exe", lx, y);
            cfg.Controls.Add(tGst);

            Button browseGst = new Button();
            browseGst.Text = "찾아보기";
            browseGst.Left = 620;
            browseGst.Top = y - 5;
            browseGst.Width = 110;
            browseGst.Height = 27;
            cfg.Controls.Add(browseGst);
            y += dy;

            Button autoFind = new Button();
            autoFind.Text = "자동 검색";
            autoFind.Left = cx;
            autoFind.Top = y - 5;
            autoFind.Width = 100;
            autoFind.Height = 27;
            cfg.Controls.Add(autoFind);

            Button inspectGst = new Button();
            inspectGst.Text = "버전/기능 검사";
            inspectGst.Left = cx + 110;
            inspectGst.Top = y - 5;
            inspectGst.Width = 120;
            inspectGst.Height = 27;
            cfg.Controls.Add(inspectGst);

            Button testElements = new Button();
            testElements.Text = "영상 요소 검사";
            testElements.Left = cx + 240;
            testElements.Top = y - 5;
            testElements.Width = 115;
            testElements.Height = 27;
            cfg.Controls.Add(testElements);

            Label versionLabel = new Label();
            versionLabel.Left = cx + 245;
            versionLabel.Top = y;
            versionLabel.AutoSize = true;
            versionLabel.Text = "버전: " + gstVersionText;
            cfg.Controls.Add(versionLabel);
            y += 30;

            Label gstCompatLabel = new Label();
            gstCompatLabel.Left = cx;
            gstCompatLabel.Top = y;
            gstCompatLabel.AutoSize = true;
            gstCompatLabel.Text =
                "rtph264depay: wait-for-keyframe=" +
                (waitKeyframeSupported ? "지원" : "미확인/자동제외") +
                " / request-keyframe=" +
                (requestKeyframeSupported ? "지원" : "미확인/자동제외");
            cfg.Controls.Add(gstCompatLabel);
            y += 34;

            TextBox tUrl = new TextBox();
            tUrl.Left = cx;
            tUrl.Top = y - 3;
            tUrl.Width = 390;
            tUrl.Text = url;
            AddLabel("RTSP 주소", lx, y);
            cfg.Controls.Add(tUrl);
            y += dy;

            ComboBox cProto = new ComboBox();
            cProto.Left = cx;
            cProto.Top = y - 3;
            cProto.Width = 110;
            cProto.DropDownStyle = ComboBoxStyle.DropDownList;
            cProto.Items.Add("AUTO");
            cProto.Items.Add("UDP");
            cProto.Items.Add("TCP");
            cProto.SelectedItem = proto;

            if (cProto.SelectedIndex < 0)
                cProto.SelectedItem = "UDP";
            AddLabel("Protocol", lx, y);
            cfg.Controls.Add(cProto);
            y += dy;

            NumericUpDown nLat = Num(cx, y - 3, 0, 5000, latency);
            AddLabel("Latency (ms)", lx, y);
            cfg.Controls.Add(nLat);
            y += dy;

            NumericUpDown nBuf = Num(cx, y - 3, 1, 60, buffers);
            AddLabel("Queue buffers", lx, y);
            cfg.Controls.Add(nBuf);
            y += dy;

            ComboBox cLeaky = new ComboBox();
            cLeaky.Left = cx;
            cLeaky.Top = y - 3;
            cLeaky.Width = 280;
            cLeaky.DropDownStyle = ComboBoxStyle.DropDownList;
            cLeaky.Items.Add("0 - 버리지 않음");
            cLeaky.Items.Add("1 - 새 프레임 버림");
            cLeaky.Items.Add("2 - 오래된 프레임 버림 (저지연)");
            cLeaky.SelectedIndex = Math.Max(0, Math.Min(2, leaky));
            AddLabel("Leaky", lx, y);
            cfg.Controls.Add(cLeaky);
            y += dy;

            NumericUpDown nTcp = Num(cx, y - 3, 1, 60, tcpTimeout);
            AddLabel("TCP timeout (sec)", lx, y);
            cfg.Controls.Add(nTcp);
            y += dy;

            NumericUpDown nUdpTimeout =
                Num(cx, y - 3, 1, 60, udpTimeoutSec);
            AddLabel("UDP no-data timeout (sec)", lx, y);
            cfg.Controls.Add(nUdpTimeout);
            y += dy;

            CheckBox cRetrans = new CheckBox();
            cRetrans.Left = cx;
            cRetrans.Top = y - 2;
            cRetrans.Width = 260;
            cRetrans.Text = "do-retransmission=true";
            cRetrans.Checked = retrans;
            AddLabel("UDP retransmission", lx, y);
            cfg.Controls.Add(cRetrans);
            y += dy;

            CheckBox cRtcp = new CheckBox();
            cRtcp.Left = cx;
            cRtcp.Top = y - 2;
            cRtcp.Width = 260;

            cRtcp.Text = "do-rtcp=true";
            cRtcp.Checked = doRtcp;
            AddLabel("RTCP", lx, y);
            cfg.Controls.Add(cRtcp);
            y += dy;

            CheckBox cKeepAlive = new CheckBox();
            cKeepAlive.Left = cx;
            cKeepAlive.Top = y - 2;
            cKeepAlive.Width = 300;
            cKeepAlive.Text = "do-rtsp-keep-alive=true";
            cKeepAlive.Checked = rtspKeepAlive;
            AddLabel("RTSP Keep Alive", lx, y);
            cfg.Controls.Add(cKeepAlive);
            y += dy;

            CheckBox cProxyBypass = new CheckBox();
            cProxyBypass.Left = cx;
            cProxyBypass.Top = y - 2;
            cProxyBypass.Width = 360;
            cProxyBypass.Text = "GIO system proxy 우회 (권장)";
            cProxyBypass.Checked = proxyBypass;
            AddLabel("Proxy bypass", lx, y);
            cfg.Controls.Add(cProxyBypass);
            y += dy;

            Label detectedLabel = new Label();
            detectedLabel.Left = cx;
            detectedLabel.Top = y;
            detectedLabel.AutoSize = true;
            detectedLabel.Text = DetectedStreamText();
            AddLabel("감지된 RTP 스트림", lx, y);
            cfg.Controls.Add(detectedLabel);
            y += dy;

            ComboBox cLossMode = new ComboBox();
            cLossMode.Left = cx;
            cLossMode.Top = y - 3;
            cLossMode.Width = 420;
            cLossMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cLossMode.Items.Add("0 - 호환성 우선 (decodebin3, 권장)");
            cLossMode.Items.Add("1 - 안전 저지연 (decodebin3 + drop-on-latency)");
            cLossMode.SelectedIndex = Math.Max(0, Math.Min(1, lossMode));
            AddLabel("손실 대응 모드", lx, y);
            cfg.Controls.Add(cLossMode);
            y += dy;

            Label safeNote = new Label();
            safeNote.Left = cx;
            safeNote.Top = y - 5;
            safeNote.Width = 500;
            safeNote.Height = 32;
            safeNote.Text = "V10.8은 rtph264depay를 강제로 넣지 않습니다. 코덱 선택은 decodebin3에 맡깁니다.";
            cfg.Controls.Add(safeNote);
            y += 34;

            CheckBox cAuto = new CheckBox();
            cAuto.Left = cx;
            cAuto.Top = y - 2;
            cAuto.Width = 100;
            cAuto.Text = "사용";
            cAuto.Checked = autoRetry;
            AddLabel("Auto reconnect", lx, y);
            cfg.Controls.Add(cAuto);
            y += dy;

            NumericUpDown nRetry = Num(cx, y - 3, 1, 30, retryDelay);
            AddLabel("Reconnect base delay (sec)", lx, y);
            cfg.Controls.Add(nRetry);
            y += dy;

            NumericUpDown nConnectWatchdog =
                Num(cx, y - 3, 3, 60, connectWatchdogSec);
            AddLabel("Connect watchdog (sec)", lx, y);
            cfg.Controls.Add(nConnectWatchdog);
            y += 40;

            Label previewLabel = new Label();
            previewLabel.Text = "현재 생성될 Pipeline";
            previewLabel.Left = lx;
            previewLabel.Top = y;
            previewLabel.AutoSize = true;
            cfg.Controls.Add(previewLabel);

            TextBox preview = new TextBox();
            preview.Left = lx;
            preview.Top = y + 22;
            preview.Width = 712;
            preview.Height = 105;
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Vertical;
            cfg.Controls.Add(preview);

            Button apply = new Button();
            apply.Text = "적용";
            apply.Left = 535;
            apply.Top = 878;
            apply.Width = 90;
            apply.Height = 30;
            cfg.Controls.Add(apply);

            Button close = new Button();
            close.Text = "닫기";
            close.Left = 635;
            close.Top = 878;
            close.Width = 90;
            close.Height = 30;
            cfg.Controls.Add(close);

            Action updateUi = delegate
            {
                string selectedProto =
                    Convert.ToString(cProto.SelectedItem);

                bool isTcp =
                    String.Equals(
                        selectedProto,
                        "TCP",
                        StringComparison.OrdinalIgnoreCase);

                bool isAuto =
                    String.Equals(
                        selectedProto,
                        "AUTO",
                        StringComparison.OrdinalIgnoreCase);

                nTcp.Enabled = isTcp || isAuto;
                nUdpTimeout.Enabled = !isTcp;
                cRetrans.Enabled = !isTcp;

                detectedLabel.Text = DetectedStreamText();

                bool sameAsProbed =
                    String.Equals(
                        tGst.Text.Trim(),
                        lastProbedGstPath,
                        StringComparison.OrdinalIgnoreCase);

                bool w = sameAsProbed && waitKeyframeSupported;
                bool r = sameAsProbed && requestKeyframeSupported;

                preview.Text = BuildPipelineFromValues(
                    tUrl.Text.Trim(),
                    Convert.ToString(cProto.SelectedItem),
                    (int)nLat.Value,
                    (int)nBuf.Value,
                    cLeaky.SelectedIndex,
                    (int)nTcp.Value,
                    (int)nUdpTimeout.Value,
                    cRetrans.Checked,
                    cRtcp.Checked,
                    cKeepAlive.Checked,
                    cLossMode.SelectedIndex);
            };

            tUrl.TextChanged += delegate { updateUi(); };
            tGst.TextChanged += delegate
            {
                if (!String.Equals(
                    tGst.Text.Trim(),
                    lastProbedGstPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    versionLabel.Text = "버전: 검사 필요";
                    gstCompatLabel.Text =
                        "rtph264depay: 새 경로는 '버전/기능 검사'를 눌러 확인";
                }

                updateUi();
            };

            cProto.SelectedIndexChanged += delegate { updateUi(); };
            nLat.ValueChanged += delegate { updateUi(); };
            nBuf.ValueChanged += delegate { updateUi(); };
            cLeaky.SelectedIndexChanged += delegate { updateUi(); };
            nTcp.ValueChanged += delegate { updateUi(); };
            nUdpTimeout.ValueChanged += delegate { updateUi(); };
            cRetrans.CheckedChanged += delegate { updateUi(); };
            cRtcp.CheckedChanged += delegate { updateUi(); };

            cKeepAlive.CheckedChanged += delegate { updateUi(); };
            cLossMode.SelectedIndexChanged += delegate { updateUi(); };

            // 중요: 찾아보기는 경로만 고릅니다. 검사 실행 안 함.
            browseGst.Click += delegate
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Title = "사용할 gst-launch-1.0.exe 선택";
                dlg.Filter =
                    "GStreamer gst-launch|gst-launch-1.0.exe|실행 파일|*.exe|모든 파일|*.*";

                try
                {
                    string current = tGst.Text.Trim();

                    if (!String.IsNullOrWhiteSpace(current) &&
                        File.Exists(current))
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(current);
                        dlg.FileName = Path.GetFileName(current);
                    }
                }
                catch { }

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    tGst.Text = dlg.FileName;
                }

                dlg.Dispose();
            };

            // 자동 검색도 경로만 넣고 검사 실행 안 함.
            autoFind.Click += delegate
            {
                string found = AutoFindGst();

                if (String.IsNullOrWhiteSpace(found))
                {
                    MessageBox.Show(
                        "자동 검색으로 gst-launch-1.0.exe를 찾지 못했습니다.",
                        "GStreamer V10.9");
                    return;
                }

                tGst.Text = found;
            };

            // 검사만 백그라운드에서 수행.
            inspectGst.Click += delegate
            {
                string path = tGst.Text.Trim();

                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    MessageBox.Show(
                        "먼저 올바른 gst-launch-1.0.exe를 선택하세요.",
                        "GStreamer V10.9");
                    return;
                }

                inspectGst.Enabled = false;
                browseGst.Enabled = false;
                autoFind.Enabled = false;
                versionLabel.Text = "버전: 검사 중...";
                gstCompatLabel.Text = "rtph264depay: 검사 중...";

                StartProbeAsync(
                    path,
                    delegate(
                        string probedPath,
                        string ver,
                        bool w,
                        bool r,
                        string info)
                    {
                        if (cfg == null || cfg.IsDisposed)
                            return;

                        lastProbedGstPath = probedPath;
                        gstVersionText = ver;
                        waitKeyframeSupported = w;
                        requestKeyframeSupported = r;
        
                        versionLabel.Text = "버전: " + ver;
                        gstCompatLabel.Text =
                            "rtph264depay: wait-for-keyframe=" +
                            (w ? "지원" : "미지원/자동제외") +
                            " / request-keyframe=" +
                            (r ? "지원" : "미지원/자동제외") +
                            (String.IsNullOrWhiteSpace(info)
                                ? ""
                                : "  (" + info + ")");

                        inspectGst.Enabled = true;
                        browseGst.Enabled = true;
                        autoFind.Enabled = true;

                        updateUi();
                    });
            };

            testElements.Click += delegate
            {
                string path = tGst.Text.Trim();

                if (String.IsNullOrWhiteSpace(path) ||
                    !File.Exists(path))
                {
                    MessageBox.Show(
                        "먼저 gst-launch-1.0.exe를 선택하세요.",
                        "GStreamer V10.9");
                    return;
                }

                testElements.Enabled = false;
                testElements.Text = "검사 중...";

                StartElementProbeAsync(
                    path,
                    delegate(string result)
                    {
                        if (cfg == null || cfg.IsDisposed)
                            return;

                        testElements.Enabled = true;
                        testElements.Text = "영상 요소 검사";

                        MessageBox.Show(
                            result,
                            "GStreamer 영상 요소 검사");
                    });
            };

            apply.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(tUrl.Text))
                {
                    MessageBox.Show("RTSP 주소를 입력하세요.");
                    return;
                }

                string selectedPath = tGst.Text.Trim();

                if (String.IsNullOrWhiteSpace(selectedPath) ||
                    !File.Exists(selectedPath))
                {
                    MessageBox.Show(
                        "사용할 gst-launch-1.0.exe 경로가 올바르지 않습니다.",
                        "GStreamer V10.9");
                    return;
                }

                gstLaunchPath = selectedPath;

                bool pathWasProbed =
                    String.Equals(
                        selectedPath,
                        lastProbedGstPath,
                        StringComparison.OrdinalIgnoreCase);

                // 검사하지 않은 새 경로는 안전하게 keyframe 고급 속성을 제외.
                if (!pathWasProbed)
                {
                    gstVersionText = "검사 안 함";
                    waitKeyframeSupported = false;
                    requestKeyframeSupported = false;
                    }

                url = tUrl.Text.Trim();
                proto = Convert.ToString(cProto.SelectedItem);
                latency = (int)nLat.Value;
                buffers = (int)nBuf.Value;
                leaky = cLeaky.SelectedIndex;
                tcpTimeout = (int)nTcp.Value;
                udpTimeoutSec = (int)nUdpTimeout.Value;
                retrans = cRetrans.Checked;
                doRtcp = cRtcp.Checked;
                rtspKeepAlive = cKeepAlive.Checked;
                proxyBypass = cProxyBypass.Checked;

                lossMode = cLossMode.SelectedIndex;
                autoRetry = cAuto.Checked;
                retryDelay = (int)nRetry.Value;
                connectWatchdogSec = (int)nConnectWatchdog.Value;

                SaveCfg();

                if (active)
                {
                    manualStop = true;
                    KillGst();
                    manualStop = false;
                    SetStatus("설정 적용 중...");
                    nextRetry = DateTime.Now;
                    StartGst();

                    MessageBox.Show(
                        "설정을 저장했고 선택한 GStreamer로 재시작했습니다.",
                        "GStreamer V10.9");
                }
                else
                {
                    MessageBox.Show(
                        "설정을 저장했습니다. 다음 시작부터 적용됩니다.",
                        "GStreamer V10.9");
                }
            };

            close.Click += delegate { cfg.Close(); };

            updateUi();

            cfg.Show();
            cfg.BringToFront();
        }

        void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Left = x;
            l.Top = y;
            l.AutoSize = true;
            cfg.Controls.Add(l);
        }

        NumericUpDown Num(int x, int y, int min, int max, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Left = x;
            n.Top = y;
            n.Width = 110;
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, val));
            return n;
        }

        // ============================================================
        // Pipeline
        // ============================================================

        string Pipeline()
        {
            return BuildPipelineFromValues(
                url, proto, latency, buffers, leaky, tcpTimeout,
                udpTimeoutSec, retrans, doRtcp, rtspKeepAlive, lossMode);
        }

        string BuildPipelineFromValues(
            string pUrl,
            string pProto,
            int pLatency,
            int pBuffers,
            int pLeaky,
            int pTcpTimeout,
            int pUdpTimeoutSec,
            bool pRetrans,
            bool pDoRtcp,
            bool pRtspKeepAlive,
            int pLossMode)
        {
            string src;

            string common =
                " latency=" + pLatency +
                " do-rtcp=" + (pDoRtcp ? "true" : "false") +
                " do-rtsp-keep-alive=" +
                (pRtspKeepAlive ? "true" : "false");

            // V10.7 안전 저지연 모드:
            // RTP depayloader를 강제하지 않고 rtspsrc jitterbuffer의 최대 지연만 제한합니다.
            if (pLossMode == 1)
                common += " drop-on-latency=true";

            if (String.Equals(
                    pProto,
                    "TCP",
                    StringComparison.OrdinalIgnoreCase))
            {
                long timeoutUs =
                    (long)pTcpTimeout * 1000000L;

                src =
                    "rtspsrc location=" + pUrl +
                    " protocols=tcp" +
                    common +
                    " tcp-timeout=" + timeoutUs;
            }
            else if (String.Equals(
                         pProto,
                         "AUTO",
                         StringComparison.OrdinalIgnoreCase))
            {
                long timeoutUs =
                    (long)pTcpTimeout * 1000000L;

                long udpTimeoutUs =
                    (long)pUdpTimeoutSec * 1000000L;

                src =
                    "rtspsrc location=" + pUrl +
                    common +
                    " udp-reconnect=1" +
                    " timeout=" + udpTimeoutUs +
                    " tcp-timeout=" + timeoutUs +
                    " do-retransmission=" +
                    (pRetrans ? "true" : "false");
            }
            else
            {
                long udpTimeoutUs =
                    (long)pUdpTimeoutSec * 1000000L;

                src =
                    "rtspsrc location=" + pUrl +
                    " protocols=udp" +
                    common +
                    " udp-reconnect=1" +
                    " timeout=" + udpTimeoutUs +
                    " do-retransmission=" +
                    (pRetrans ? "true" : "false");
            }

            // 핵심: 항상 기존에 정상 동작한 decodebin3 경로를 사용합니다.
            // rtph264depay / rtph265depay를 직접 삽입하지 않습니다.
            return src +
                " ! application/x-rtp" +
                " ! decodebin3" +
                " ! queue max-size-buffers=" + pBuffers +
                " max-size-bytes=0 max-size-time=0" +
                " leaky=" + pLeaky +
                " ! videoconvert" +
                " ! autovideosink sync=false";
        }

        // ============================================================
        // V10.9 - smart reconnect backoff
        // ============================================================

        int BaseRecoveryDelaySec()
        {
            // 사용자가 Reconnect delay를 더 크게 잡으면 그 값을 존중하되,
            // 임베디드 RTSP 서버 정리 시간을 위해 최소 8초는 기다립니다.
            return Math.Max(MinServerRecoveryWaitSec, retryDelay);
        }

        int NextFailedConnectDelaySec()
        {
            reconnectFailureCount++;

            int baseSec = BaseRecoveryDelaySec();
            int shift = Math.Min(reconnectFailureCount - 1, 3);

            long delay = (long)baseSec << shift;

            if (delay > MaxReconnectBackoffSec)
                delay = MaxReconnectBackoffSec;

            return (int)delay;

        }

        void ScheduleReconnectAfterDrop(string reason)
        {
            if (!active || manualStop || !autoRetry)
                return;

            // 정상 PLAY까지 갔다가 끊어진 경우에는 카메라/RTSP 서버가
            // 이전 세션을 정리할 시간을 먼저 줍니다.
            reconnectFailureCount = 0;

            int delay = BaseRecoveryDelaySec();
            nextRetry = DateTime.Now.AddSeconds(delay);

            AppendGstLog(
                "[RECONNECT] Stream dropped. Waiting " +
                delay +
                " sec before retry. Reason: " +
                reason);

            SetStatus(
                "영상 끊김 - 서버 복구 대기 " +
                delay +
                "초");
        }

        void ScheduleReconnectAfterFailedAttempt(string reason)
        {
            if (!active || manualStop || !autoRetry)
                return;

            int delay = NextFailedConnectDelaySec();
            nextRetry = DateTime.Now.AddSeconds(delay);

            AppendGstLog(
                "[RECONNECT] Connection attempt failed. " +
                "Retry #" +
                reconnectFailureCount +
                " in " +
                delay +
                " sec. Reason: " +
                reason);

            SetStatus(
                "RTSP 서버 대기 - " +
                delay +
                "초 후 재시도");
        }

        // ============================================================
        // Process / reconnect
        // ============================================================

        void Tick(object s, EventArgs e)
        {
            try
            {
                if (Running())
                {
                    // V10.8: gst-launch가 Connecting 상태로 살아만 있고
                    // PLAY까지 못 가면 기존 코드는 영원히 재시작하지 못했습니다.
                    if (!rtspPlayStarted &&
                        gstLaunchAt != DateTime.MinValue &&
                        (DateTime.Now - gstLaunchAt).TotalSeconds >=
                            connectWatchdogSec)
                    {
                        AppendGstLog(
                            "[WATCHDOG] RTSP connection stalled for " +
                            connectWatchdogSec +
                            " sec. Restarting gst-launch.");

                        SetStatus(
                            "RTSP 연결 정지 감지 - 프로세스 재시작...");

                        bool hadPlay = attemptReachedPlay;

                        KillGst();

                        if (hadPlay)
                            ScheduleReconnectAfterDrop(
                                "connect watchdog after PLAY");
                        else
                            ScheduleReconnectAfterFailedAttempt(
                                "connect watchdog");

                        return;
                    }

                    if (videoWnd == IntPtr.Zero || !IsWindow(videoWnd))
                    {
                        IntPtr w = FindWindow(gst.Id);

                        if (w != IntPtr.Zero)
                        {
                            Embed(w);

                            if (rtspPlayStarted)
                                SetStatus("");
                        }
                    }

                    return;
                }

                videoWnd = IntPtr.Zero;

                if (!active || manualStop)
                    return;

                if (!autoRetry)
                {
                    SetStatus("영상 끊김 - 자동 재연결 OFF");
                    return;
                }

                if (nextRetry == DateTime.MinValue)
                {
                    ScheduleReconnectAfterFailedAttempt(
                        "no running gst process");
                    return;
                }

                if (DateTime.Now >= nextRetry)
                {
                    StartGst();
                }
            }
            catch { }
        }

        void StartGst()
        {
            if (!active || Running())
                return;

            try
            {
                string exe = FindGst();

                if (String.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    active = false;
                    MessageBox.Show(
                        "선택한 gst-launch-1.0.exe를 찾지 못했습니다.\r\n\r\n" +
                        "실시간 설정에서 최신 GStreamer의 gst-launch-1.0.exe 경로를 다시 지정하세요.\r\n" +
                        "현재 경로: " + gstLaunchPath,
                        "GStreamer V10.9");
                    return;
                }

                ProcessStartInfo p = new ProcessStartInfo();
                p.FileName = exe;
                p.Arguments = "-e " + Pipeline();
                p.UseShellExecute = false;
                p.CreateNoWindow = true;
                p.RedirectStandardOutput = true;
                p.RedirectStandardError = true;
                p.WorkingDirectory = Path.GetDirectoryName(exe);

                // V10.7: 선택한 bin PATH만 우선하고 plugin system path는 GStreamer 자동 탐색.
                ConfigureSelectedGstEnvironment(
                    p,
                    Path.GetDirectoryName(exe));

                AppendGstLog("=== GStreamer V10.9 launch ===");
                AppendGstLog("EXE: " + exe);
                AppendGstLog("PROXY_BYPASS: " + proxyBypass);
                AppendGstLog("VERBOSE_STATS: OFF");
                AppendGstLog("PIPELINE: " + Pipeline());
                AppendGstLog("");

                gst = new Process();
                gst.StartInfo = p;
                gst.EnableRaisingEvents = true;

                gst.OutputDataReceived += delegate(object os, DataReceivedEventArgs oe)
                {
                    if (oe.Data != null)
                    {
                        ObserveRtspLine(oe.Data);

                        InspectCapsLine(oe.Data);
                        AppendGstLog("[OUT] " + oe.Data);
                    }
                };

                gst.ErrorDataReceived += delegate(object es, DataReceivedEventArgs ee)
                {
                    if (ee.Data != null)
                    {
                        ObserveRtspLine(ee.Data);
                        InspectCapsLine(ee.Data);
                        AppendGstLog("[ERR] " + ee.Data);
                    }
                };

                gst.Exited += delegate
                {
                    try
                    {
                        if (host != null && !host.IsDisposed)
                        {
                            host.BeginInvoke((MethodInvoker)delegate
                            {
                                videoWnd = IntPtr.Zero;
                                rtspPlayStarted = false;
                                gstLaunchAt = DateTime.MinValue;

                                try
                                {
                                    AppendGstLog(
                                        "=== gst-launch exited, code=" +
                                        gst.ExitCode + " ===");
                                }
                                catch
                                {
                                    AppendGstLog("=== gst-launch exited ===");
                                }

                                if (active && !manualStop)
                                {
                                    if (!autoRetry)
                                    {
                                        SetStatus(
                                            "영상 끊김 - 자동 재연결 OFF");
                                    }
                                    else if (attemptReachedPlay)
                                    {
                                        ScheduleReconnectAfterDrop(
                                            "gst-launch exited after PLAY");
                                    }
                                    else
                                    {
                                        ScheduleReconnectAfterFailedAttempt(
                                            "gst-launch exited before PLAY");
                                    }
                                }
                            });
                        }
                    }
                    catch { }
                };

                rtspPlayStarted = false;
                attemptReachedPlay = false;
                gstLaunchAt = DateTime.Now;

                gst.Start();
                gst.BeginOutputReadLine();
                gst.BeginErrorReadLine();

                videoWnd = IntPtr.Zero;
                SetStatus(
                    "RTSP 연결 중... (watchdog " +
                    connectWatchdogSec +
                    "초)");
            }
            catch (Exception ex)
            {
                gst = null;

                AppendGstLog(
                    "[RECONNECT] StartGst exception: " +
                    ex.GetType().Name +
                    " - " +
                    ex.Message);

                ScheduleReconnectAfterFailedAttempt(
                    "StartGst exception");
            }
        }

        bool Running()
        {
            try
            {
                return gst != null && !gst.HasExited;
            }
            catch
            {
                return false;
            }
        }

        void KillGst()
        {
            videoWnd = IntPtr.Zero;
            rtspPlayStarted = false;
            gstLaunchAt = DateTime.MinValue;

            if (gst == null)
                return;

            try
            {
                if (!gst.HasExited)
                {
                    try { gst.Kill(); } catch { }
                    try { gst.WaitForExit(1000); } catch { }
                }
            }
            catch { }

            try { gst.Dispose(); } catch { }
            gst = null;

            if (manualStop)
                attemptReachedPlay = false;
        }

        void SetStatus(string text)
        {
            if (status == null || status.IsDisposed)
                return;

            status.Text = text;
            status.Visible = !String.IsNullOrEmpty(text);
            status.BringToFront();
        }

        // ============================================================
        // V10.9 - RTSP connection state observer
        // ============================================================

        void ObserveRtspLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
                return;

            if (line.IndexOf(
                    "Sent PLAY request",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf(
                    "Setting pipeline to PLAYING",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!rtspPlayStarted)
                {
                    rtspPlayStarted = true;
                    attemptReachedPlay = true;
                    reconnectFailureCount = 0;
                    nextRetry = DateTime.MinValue;

                    AppendGstLog(
                        "[RECONNECT] RTSP PLAY reached. " +
                        "Backoff reset.");

                    AppendGstLog(
                        "[WATCHDOG] Connect watchdog disarmed.");
                }
            }
        }

        // ============================================================
        // V10.9 - RTP caps auto detection (read-only, never forces codec)
        // ============================================================

        void InspectCapsLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
                return;
