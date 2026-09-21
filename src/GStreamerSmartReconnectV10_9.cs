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

            string upper = line.ToUpperInvariant();
            string codec = "";

            if (upper.IndexOf("ENCODING-NAME=(STRING)H264") >= 0)
                codec = "H264";
            else if (upper.IndexOf("ENCODING-NAME=(STRING)H265") >= 0 ||
                     upper.IndexOf("ENCODING-NAME=(STRING)HEVC") >= 0)
                codec = "H265";
            else if (upper.IndexOf("ENCODING-NAME=(STRING)VP9") >= 0)
                codec = "VP9";
            else if (upper.IndexOf("ENCODING-NAME=(STRING)VP8") >= 0)
                codec = "VP8";

            if (!String.IsNullOrWhiteSpace(codec) &&
                !String.Equals(detectedCodec, codec, StringComparison.OrdinalIgnoreCase))
            {
                detectedCodec = codec;
                AppendGstLog("[INFO] RTP codec detected: " + codec);
            }

            string payload = ExtractCapsNumber(line, "payload=(int)");
            if (!String.IsNullOrWhiteSpace(payload))
                detectedPayload = payload;

            string clock = ExtractCapsNumber(line, "clock-rate=(int)");
            if (!String.IsNullOrWhiteSpace(clock))
                detectedClockRate = clock;
        }

        string ExtractCapsNumber(string line, string markerText)
        {
            int p = line.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
            if (p < 0)
                return "";

            p += markerText.Length;
            StringBuilder b = new StringBuilder();

            while (p < line.Length && Char.IsDigit(line[p]))
            {
                b.Append(line[p]);
                p++;
            }

            return b.ToString();
        }

        string DetectedStreamText()
        {
            return detectedCodec +
                " / payload=" + detectedPayload +
                " / clock=" + detectedClockRate;
        }

        // ============================================================
        // V10.4 - GStreamer runtime log
        // ============================================================

        void ClearGstLog()
        {
            lock (logLock)
            {
                gstLog.Length = 0;
            }
        }

        void AppendGstLog(string line)
        {
            lock (logLock)
            {
                gstLog.AppendLine(
                    DateTime.Now.ToString("HH:mm:ss.fff") +
                    " " + line);

                if (gstLog.Length > MaxLogChars)
                {
                    int remove =
                        gstLog.Length - MaxLogChars;

                    gstLog.Remove(0, remove);
                }
            }
        }

        string GetGstLog()
        {
            lock (logLock)
            {
                return gstLog.ToString();
            }
        }

        void ShowLog()
        {
            Form f = new Form();
            f.Text = "GStreamer V10.9 로그";
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new System.Drawing.Size(1000, 650);

            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Font = new System.Drawing.Font(
                "Consolas",
                9.0f);
            box.Text = GetGstLog();

            Button refresh = new Button();
            refresh.Text = "새로고침";
            refresh.Dock = DockStyle.Bottom;
            refresh.Height = 34;
            refresh.Click += delegate
            {
                box.Text = GetGstLog();
                box.SelectionStart = box.Text.Length;
                box.ScrollToCaret();
            };

            Button copy = new Button();
            copy.Text = "로그 전체 복사";
            copy.Dock = DockStyle.Bottom;
            copy.Height = 34;
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(GetGstLog());
                }
                catch { }
            };

            f.Controls.Add(box);
            f.Controls.Add(copy);
            f.Controls.Add(refresh);

            box.SelectionStart = box.Text.Length;
            box.ScrollToCaret();

            f.Show();
        }

        // ============================================================
        // Settings persistence
        // ============================================================

        void LoadCfg()
        {
            // V10 값이 없으면 V9 값을 우선 이어받습니다.
            url = S10("url", S9("url", url));
            proto = S10("proto", S9("proto", proto)).ToUpperInvariant();

            if (proto != "AUTO" &&
                proto != "UDP" &&
                proto != "TCP")
            {
                proto = "UDP";
            }

            latency = I10("latency", I9("latency", latency, 0, 5000), 0, 5000);
            buffers = I10("buffers", I9("buffers", buffers, 1, 60), 1, 60);
            leaky = I10("leaky", I9("leaky", leaky, 0, 2), 0, 2);
            tcpTimeout = I10("tcp", I9("tcp", tcpTimeout, 1, 60), 1, 60);
            udpTimeoutSec = I10("udp_timeout", 5, 1, 60);
            retryDelay = I10("retry", I9("retry", retryDelay, 1, 30), 1, 30);
            connectWatchdogSec = I10("connect_watchdog", 8, 3, 60);

            retrans = B10("retrans", B9("retrans", retrans));
            doRtcp = B10("do_rtcp", doRtcp);
            rtspKeepAlive = B10("rtsp_keep_alive", rtspKeepAlive);
            proxyBypass = B10("proxy_bypass", proxyBypass);
            autoRetry = B10("auto", B9("auto", autoRetry));

            lossMode = I10("loss_mode", 0, 0, 1);
            detectedCodec = S10("detected_codec", "미감지");
            detectedPayload = S10("detected_payload", "-");
            detectedClockRate = S10("detected_clock", "-");

            gstLaunchPath = S10("gst_path", "");
            lastProbedGstPath = S10("gst_probe_path", "");
            gstVersionText = S10("gst_version", "미확인");
            waitKeyframeSupported = B10("gst_wait_supported", false);
            requestKeyframeSupported = B10("gst_request_supported", false);

            // 저장된 경로가 없거나 사라졌으면 한 번 자동 검색.
            if (String.IsNullOrWhiteSpace(gstLaunchPath) ||
                !File.Exists(gstLaunchPath))
            {
                string autoPath = AutoFindGst();

                if (!String.IsNullOrWhiteSpace(autoPath))
                    gstLaunchPath = autoPath;
            }

        }

        void SaveCfg()
        {
            try
            {
                Settings.Instance[K10 + "url"] = url;
                Settings.Instance[K10 + "proto"] = proto;
                Settings.Instance[K10 + "latency"] = latency.ToString();
                Settings.Instance[K10 + "buffers"] = buffers.ToString();
                Settings.Instance[K10 + "leaky"] = leaky.ToString();
                Settings.Instance[K10 + "tcp"] = tcpTimeout.ToString();
                Settings.Instance[K10 + "udp_timeout"] =
                    udpTimeoutSec.ToString();
                Settings.Instance[K10 + "retry"] = retryDelay.ToString();
                Settings.Instance[K10 + "connect_watchdog"] =
                    connectWatchdogSec.ToString();
                Settings.Instance[K10 + "retrans"] = retrans.ToString();
                Settings.Instance[K10 + "do_rtcp"] = doRtcp.ToString();
                Settings.Instance[K10 + "rtsp_keep_alive"] =
                    rtspKeepAlive.ToString();

                Settings.Instance[K10 + "proxy_bypass"] =
                    proxyBypass.ToString();

                Settings.Instance[K10 + "auto"] = autoRetry.ToString();

                Settings.Instance[K10 + "loss_mode"] =
                    lossMode.ToString();

                Settings.Instance[K10 + "detected_codec"] =
                    detectedCodec;

                Settings.Instance[K10 + "detected_payload"] =
                    detectedPayload;

                Settings.Instance[K10 + "detected_clock"] =
                    detectedClockRate;

                Settings.Instance[K10 + "gst_path"] =
                    gstLaunchPath;

                Settings.Instance[K10 + "gst_probe_path"] =
                    lastProbedGstPath;

                Settings.Instance[K10 + "gst_version"] =
                    gstVersionText;

                Settings.Instance[K10 + "gst_wait_supported"] =
                    waitKeyframeSupported.ToString();

                Settings.Instance[K10 + "gst_request_supported"] =
                    requestKeyframeSupported.ToString();
            }
            catch { }
        }

        string S10(string k, string d)
        {
            return GetSetting(K10 + k, d);
        }

        string S9(string k, string d)
        {
            return GetSetting(K9 + k, d);
        }

        string GetSetting(string key, string d)
        {
            try
            {
                string v = Settings.Instance[key];
                return String.IsNullOrWhiteSpace(v) ? d : v;
            }
            catch
            {
                return d;
            }
        }

        int I10(string k, int d, int min, int max)
        {
            return GetInt(K10 + k, d, min, max);
        }

        int I9(string k, int d, int min, int max)
        {
            return GetInt(K9 + k, d, min, max);
        }

        int GetInt(string key, int d, int min, int max)
        {
            int v;

            if (!Int32.TryParse(GetSetting(key, d.ToString()), out v))
                v = d;

            return Math.Max(min, Math.Min(max, v));
        }

        bool B10(string k, bool d)
        {
            return GetBool(K10 + k, d);
        }

        bool B9(string k, bool d)
        {
            return GetBool(K9 + k, d);
        }

        bool GetBool(string key, bool d)
        {
            bool v;

            return Boolean.TryParse(
                GetSetting(key, d.ToString()), out v)
                ? v : d;
        }

        // ============================================================
        // V10.3 - non-blocking GStreamer probe
        // ============================================================

        delegate void ProbeComplete(
            string path,
            string version,
            bool waitSupported,
            bool requestSupported,
            string info);

        void StartProbeAsync(string path, ProbeComplete complete)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string ver;
                bool w;
                bool r;
                string info;

                ProbeGstSafe(
                    path,
                    out ver,
                    out w,
                    out r,
                    out info);

                try
                {
                    if (cfg != null && !cfg.IsDisposed)
                    {
                        cfg.BeginInvoke((MethodInvoker)delegate
                        {
                            complete(path, ver, w, r, info);
                        });
                    }
                }
                catch { }
            });
        }

        delegate void ElementProbeComplete(string result);

        void StartElementProbeAsync(

            string gstLaunch,
            ElementProbeComplete complete)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result = ProbeElements(gstLaunch);

                try
                {
                    if (cfg != null && !cfg.IsDisposed)
                    {
                        cfg.BeginInvoke((MethodInvoker)delegate
                        {
                            complete(result);
                        });
                    }
                }
                catch { }
            });
        }

        string ProbeElements(string gstLaunch)
        {
            string dir;

            try
            {
                dir = Path.GetDirectoryName(gstLaunch);
            }
            catch
            {
                return "경로 오류";
            }

            string inspect =
                Path.Combine(dir, "gst-inspect-1.0.exe");

            if (!File.Exists(inspect))
                return "gst-inspect-1.0.exe 없음";

            string[] elements = new string[]
            {
                "rtspsrc",
                "rtph264depay",
                "decodebin3",
                "videoconvert",
                "autovideosink",
                "d3d11videosink",
                "d3d12videosink",
                "avdec_h264"
            };

            StringBuilder report = new StringBuilder();
            report.AppendLine("선택 GStreamer 영상 요소 검사");
            report.AppendLine();

            foreach (string element in elements)
            {
                string so;
                string se;

                bool ok = RunProcessWithTimeout(
                    inspect,
                    element,
                    dir,
                    6000,
                    out so,
                    out se);

                string all =
                    (so + "\n" + se).ToLowerInvariant();

                bool exists =
                    ok &&
                    all.IndexOf("no such element") < 0 &&
                    all.IndexOf("no such element or plugin") < 0 &&
                    !String.IsNullOrWhiteSpace(so);

                report.AppendLine(
                    element.PadRight(18) +
                    (exists ? "OK" : "없음/실패"));
            }

            return report.ToString();
        }

        void RefreshDepayCapabilities()
        {
            // 저장된 검사 결과만 사용합니다.
        }

        void EnsureDepayCapabilities()
        {
            // 동기 검사 없음.
        }

        void ProbeGstSafe(
            string gstLaunch,
            out string version,
            out bool waitSupported,
            out bool requestSupported,
            out string info)
        {
            version = "확인 실패";
            waitSupported = false;
            requestSupported = false;
            info = "";

            if (String.IsNullOrWhiteSpace(gstLaunch) ||
                !File.Exists(gstLaunch))
            {
                info = "경로 없음";
                return;
            }

            string dir;

            try
            {
                dir = Path.GetDirectoryName(gstLaunch);
            }
            catch
            {
                info = "경로 오류";
                return;
            }

            string outText;
            string errText;

            if (RunProcessWithTimeout(
                    gstLaunch,
                    "--version",
                    dir,
                    10000,
                    out outText,
                    out errText))
            {
                string all = outText + "\n" + errText;
                string[] lines = all.Split(
                    new char[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

                string picked = "";

                foreach (string line in lines)
                {
                    if (line.ToLowerInvariant().IndexOf("gstreamer") >= 0)
                    {
                        picked = line.Trim();
                        break;
                    }
                }

                if (String.IsNullOrWhiteSpace(picked) &&
                    lines.Length > 0)
                {
                    picked = lines[0].Trim();
                }

                version =
                    String.IsNullOrWhiteSpace(picked)
                    ? "버전 문자열 없음"
                    : picked;
            }
            else
            {
                version = "버전 검사 timeout/실패 (10초)";
            }

            try
            {
                string inspect =
                    Path.Combine(dir, "gst-inspect-1.0.exe");

                if (!File.Exists(inspect))
                {
                    info = "gst-inspect 없음";
                    return;
                }

                if (!RunProcessWithTimeout(
                        inspect,
                        "rtph264depay",
                        dir,
                        20000,
                        out outText,
                        out errText))
                {
                    info = "gst-inspect timeout/실패 (20초)";
                    return;
                }

                string all =
                    (outText + "\n" + errText).ToLowerInvariant();

                waitSupported =
                    all.IndexOf("wait-for-keyframe") >= 0;

                requestSupported =
                    all.IndexOf("request-keyframe") >= 0;

                if (all.IndexOf("no such element") >= 0 ||
                    all.IndexOf("no such element or plugin") >= 0)
                {
                    info = "rtph264depay 없음";
                }
            }
            catch (Exception ex)
            {
                info = "inspect 실패: " + ex.GetType().Name;
            }
        }

        bool RunProcessWithTimeout(
            string file,
            string args,
            string workDir,
            int timeoutMs,
            out string stdout,
            out string stderr)
        {
            stdout = "";
            stderr = "";

            StringBuilder outBuf = new StringBuilder();
            StringBuilder errBuf = new StringBuilder();

            Process p = null;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = workDir;

                // Mission Planner가 예전 GStreamer를 찾는 과정에서 설정한
                // GST_PLUGIN_PATH/PATH가 최신 설치본 검사에 섞이지 않도록 정리.
                ConfigureSelectedGstEnvironment(psi, workDir);

                p = new Process();
                p.StartInfo = psi;

                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (outBuf)
                        {
                            outBuf.AppendLine(e.Data);
                        }
                    }
                };

                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (errBuf)
                        {
                            errBuf.AppendLine(e.Data);
                        }
                    }
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(timeoutMs);

                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(1000); } catch { }
                }
                else
                {
                    // 비동기 stdout/stderr event flush
                    try { p.WaitForExit(); } catch { }
                }

                lock (outBuf)
                {
                    stdout = outBuf.ToString();
                }

                lock (errBuf)
                {
                    stderr = errBuf.ToString();
                }

                return exited;
            }
            catch (Exception ex)
            {
                stderr = ex.ToString();
                return false;
            }
            finally
            {
                if (p != null)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        // ============================================================
        // V10.3.2 - isolate selected GStreamer environment
        // ============================================================

        void ConfigureSelectedGstEnvironment(
            ProcessStartInfo psi,
            string binDir)
        {
            try
            {
                if (psi == null ||
                    String.IsNullOrWhiteSpace(binDir))
                    return;

                string oldPath = "";

                try
                {
                    oldPath = psi.EnvironmentVariables["PATH"];
                }
                catch { }

                // 선택한 GStreamer의 DLL을 가장 먼저 찾게만 합니다.
                psi.EnvironmentVariables["PATH"] =
                    binDir +
                    (String.IsNullOrWhiteSpace(oldPath)
                        ? ""
                        : ";" + oldPath);

                // Mission Planner가 구버전용으로 설정했을 수 있는 "추가" plugin 경로만 제거.
                // GST_PLUGIN_SYSTEM_PATH는 강제로 새 값으로 지정하지 않습니다.
                // 최신 GStreamer가 자신의 설치 위치 기준으로 표준 plugin 경로를
                // 자동 탐색하도록 맡깁니다.
                RemoveEnv(psi, "GST_PLUGIN_PATH");
                RemoveEnv(psi, "GST_PLUGIN_PATH_1_0");

                // 실제 실패 원인을 stderr로 볼 수 있도록 적당한 debug level.
                psi.EnvironmentVariables["GST_DEBUG"] = "1";

                // V10.6: libgiolibproxy/GIO proxy 문제를 피하고
                // 로컬 RTSP 주소로 직접 연결하도록 강제할 수 있습니다.
                if (proxyBypass)
                {
                    psi.EnvironmentVariables["GIO_USE_PROXY_RESOLVER"] = "dummy";
                    psi.EnvironmentVariables["NO_PROXY"] = "*";
                    psi.EnvironmentVariables["no_proxy"] = "*";
                }

                else
                {
                    RemoveEnv(psi, "GIO_USE_PROXY_RESOLVER");
                    RemoveEnv(psi, "NO_PROXY");
                    RemoveEnv(psi, "no_proxy");
                }
            }
            catch
            {
            }
        }

        void RemoveEnv(
            ProcessStartInfo psi,
            string key)
        {
            try
            {
                if (psi.EnvironmentVariables.ContainsKey(key))
                    psi.EnvironmentVariables.Remove(key);
            }
            catch { }
        }

        // ============================================================
        // gst-launch location
        // ============================================================

        string FindGst()
        {
            // 사용자가 선택한 경로가 있으면 무조건 그 버전을 우선 사용.
            if (!String.IsNullOrWhiteSpace(gstLaunchPath))
            {
                if (File.Exists(gstLaunchPath))
                    return gstLaunchPath;

                return null;
            }

            return AutoFindGst();
        }

        string AutoFindGst()
        {
            string lib = GStreamer.LookForGstreamer();

            if (!String.IsNullOrWhiteSpace(lib))
            {
                string dir = Path.GetDirectoryName(lib);

                if (!String.IsNullOrWhiteSpace(dir))
                {
                    string a =
                        Path.Combine(dir, "gst-launch-1.0.exe");

                    if (File.Exists(a))
                        return a;

                    DirectoryInfo di = new DirectoryInfo(dir);

                    if (di.Parent != null)
                    {
                        string b = Path.Combine(
                            di.Parent.FullName,
                            "bin",
                            "gst-launch-1.0.exe");

                        if (File.Exists(b))
                            return b;
                    }
                }
            }

            // 일반적인 공식 GStreamer 설치 위치도 추가 검색.
            string[] roots = new string[]
            {
                @"C:\gstreamer\1.0\mingw_x86_64\bin\gst-launch-1.0.exe",
                @"C:\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe",
                @"C:\Program Files\gstreamer\1.0\mingw_x86_64\bin\gst-launch-1.0.exe",
                @"C:\Program Files\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe",
                @"C:\Program Files (x86)\gstreamer\1.0\mingw_x86_64\bin\gst-launch-1.0.exe",
                @"C:\Program Files (x86)\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe"
            };

            foreach (string path in roots)
            {
                try
                {
                    if (File.Exists(path))
                        return path;
                }
                catch { }
            }

            return null;
        }

        // ============================================================
        // External video window embedding
        // ============================================================

        void Embed(IntPtr w)
        {
            videoWnd = w;

            long st =
                GetWindowLongPtr(w, GWL_STYLE).ToInt64();

            st &=
                ~(WS_CAPTION |
                  WS_THICKFRAME |
                  WS_MINIMIZEBOX |
                  WS_MAXIMIZEBOX |
                  WS_SYSMENU);

            st |= WS_CHILD | WS_VISIBLE;

            SetWindowLongPtr(
                w,
                GWL_STYLE,
                new IntPtr(st));

            SetParent(w, panel.Handle);
            ResizeVideo();
            ShowWindow(w, 5);
        }

        void ResizeVideo()
        {
            if (videoWnd == IntPtr.Zero ||
                !IsWindow(videoWnd))
                return;

            MoveWindow(
                videoWnd,
                0,
                0,
                panel.ClientSize.Width,
                panel.ClientSize.Height,
                true);
        }

        IntPtr FindWindow(int pid)
        {
            IntPtr r = IntPtr.Zero;

            EnumWindows(
                delegate(IntPtr w, IntPtr l)
                {
                    uint p;
                    GetWindowThreadProcessId(w, out p);

                    if (p == pid && IsWindowVisible(w))
                    {
                        r = w;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);

            return r;
        }

        const int GWL_STYLE = -16;

        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        const long WS_MINIMIZEBOX = 0x00020000L;
        const long WS_MAXIMIZEBOX = 0x00010000L;
        const long WS_SYSMENU = 0x00080000L;
        const long WS_CHILD = 0x40000000L;
        const long WS_VISIBLE = 0x10000000L;

        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(
            EnumWindowsProc f,
