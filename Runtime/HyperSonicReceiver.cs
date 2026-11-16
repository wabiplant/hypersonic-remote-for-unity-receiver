using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HyperSonic
{
    /// <summary>
    /// UnityEngine.Touch とほぼ同じ形で扱えるリモートタッチ。
    /// 座標は「Game View のピクセル座標（左下原点）」へ変換済み。
    /// </summary>
    public struct RemoteTouch
    {
        public int fingerId;
        public TouchPhase phase;
        public Vector2 position;      // pixels, bottom-left origin
        public Vector2 rawPosition;   // began position (pixels)
        public Vector2 deltaPosition; // pixels since last frame
        public float pressure;        // 0..1 (端末が取れない場合は 1)
        public float deltaTime;       // seconds since previous event for this finger
    }

    /// <summary>
    /// iPhone の HyperSonicRemote から UDP/47777 でタッチを受け取る受信側。
    /// Play Mode 中（Editor / Development Build）に GameObject へ載せて使う。
    /// 受信はバックグラウンドスレッド、公開はメインスレッドの Update で行う。
    /// 発見手段は2系統: ①Bonjour 広告 _hypersonic._udp (dns-sd で登録、
    /// 名前は「プロジェクトフォルダ名 · マシン名」) ②UDP の discover へ hello 応答。
    /// アプリは接続後に whoami を送り、hello で映像用 IP を受け取る。
    /// </summary>
    public sealed class HyperSonicReceiver : MonoBehaviour
    {
        public const int Port = 47777;
        /// <summary>USB 経路: iproxy が localhost のこのポートを端末の 47779 へ転送する</summary>
        public const int UsbLocalPort = 47778;
        public const int UsbDevicePort = 47779;

        public static HyperSonicReceiver Instance { get; private set; }

        /// <summary>この受信側の安定 ID (マシン名+プロジェクトパスのハッシュ)。アプリ側の登録キー。</summary>
        public static string HostId { get; private set; }
        /// <summary>発見リスト表示名「プロジェクトフォルダ名 · マシン名」</summary>
        public static string HostName { get; private set; }

        /// <summary>映像モードの要求状態。iPhone 側のスイッチ (mode パケット) で変わる。
        /// VideoStreamer はこれが false の間、キャプチャもエンコードも一切行わない。</summary>
        public static bool VideoRequested { get; private set; }
        public static int VideoFps { get; private set; } = 20;
        public static int VideoQuality { get; private set; } = 55;
        /// <summary>端末のネイティブ解像度 (mode パケットの vw/vh)。映像アスペクトの基準。0 は未通知。</summary>
        public static int VideoDeviceWidth { get; private set; }
        public static int VideoDeviceHeight { get; private set; }
        /// <summary>映像の最大幅 (mode パケットの mw、プリセット用)。0 は未通知 (Streamer 側の値を使う)。</summary>
        public static int VideoMaxWidth { get; private set; }

        public enum TouchMappingMode { VisibleArea, FullFrame }
        /// <summary>
        /// タッチ座標の対応付け。VisibleArea (既定) = 端末に表示されている範囲
        /// (アスペクトで切り出された窓) の中へ対応付けるため、「見えている場所と
        /// 触った場所」が一致する。FullFrame = 画面全体へ素直に対応付ける
        /// (Mac 側の画面を見ながら入力だけ使う場合用)。
        /// </summary>
        public TouchMappingMode touchMapping = TouchMappingMode.VisibleArea;

        /// <summary>映像で見えている範囲 (画面全体に対する 0..1 矩形、左下原点)。
        /// Streamer の切り出し窓と同一の計算。</summary>
        public static Rect VisibleRect01(float screenW, float screenH)
        {
            if (VideoDeviceWidth <= 0 || VideoDeviceHeight <= 0 || screenW <= 0 || screenH <= 0)
                return new Rect(0, 0, 1, 1);
            float frameAspect = screenW / screenH;
            float aspect = VideoDeviceWidth / (float)VideoDeviceHeight;
            if (frameAspect > aspect)
            {
                float wf = aspect / frameAspect;
                return new Rect((1f - wf) / 2f, 0, wf, 1);
            }
            float hf = frameAspect / aspect;
            return new Rect(0, (1f - hf) / 2f, 1, hf);
        }

        [Header("USB (usbmux/iproxy 経由) の入力も受け付ける")]
        public bool listenUsb = true;

        /// <summary>現在有効なリモートタッチ（メインスレッドからのみ読むこと）</summary>
        public static RemoteTouch[] Touches => Instance != null ? Instance.m_Touches : Array.Empty<RemoteTouch>();

        [Header("受信中のタッチを Scene 上にデバッグ表示する")]
        public bool drawDebug = false;

        private UdpClient m_Udp;
        private Thread m_Thread;
        private volatile bool m_Running;

        private readonly ConcurrentQueue<string> m_Inbox = new ConcurrentQueue<string>();
        private readonly System.Collections.Generic.Dictionary<int, RemoteTouch> m_Active =
            new System.Collections.Generic.Dictionary<int, RemoteTouch>();
        // 届いた順の未公開状態 (指ごと)。そのまま m_Active へ上書きすると、同じ
        // フレーム内に届いた Began が後続の Moved/Stationary に潰れて読み手から
        // 一度も見えなくなる (2026-11-16 実機で確認: 左スティックが指を捕捉でき
        // ない)。phase の遷移は1フレームに1段だけ進め、Began と Ended/Canceled
        // は必ず丸1フレーム公開する (Unity 標準の Input.touches と同じ意味論)。
        private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<RemoteTouch>> m_Pending =
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<RemoteTouch>>();
        private readonly System.Collections.Generic.Dictionary<int, float> m_LastSeen =
            new System.Collections.Generic.Dictionary<int, float>();
        private RemoteTouch[] m_Touches = Array.Empty<RemoteTouch>();
        private IPEndPoint m_LastSender;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private System.Net.Sockets.TcpClient m_UsbClient;
        private Thread m_UsbThread;
        private System.Diagnostics.Process m_DnsSdInput;
        private System.Diagnostics.Process m_DnsSdVideo;
        private string m_HelloJson;
        private string m_HelloPre;
        private string m_HelloPost;

        private void OnEnable()
        {
            // Editor が背面にあると Play の Update が止まり、映像もタッチ反映も
            // 止まって見える (実測で判明)。リモート道具の前提として常時実行にする。
            // プロジェクト設定は変更せず、このプロセス限りで有効。
            Application.runInBackground = true;
            BuildIdentity();
            m_Udp = new UdpClient(Port);
            m_Running = true;
            m_Thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "HyperSonicReceiver" };
            m_Thread.Start();
            Debug.Log($"[HyperSonic] Listening on UDP {Port}");
            StartBonjour();
            if (listenUsb)
            {
                m_UsbThread = new Thread(UsbLoop) { IsBackground = true, Name = "HyperSonicUsb" };
                m_UsbThread.Start();
            }
        }

        private void BuildIdentity()
        {
            string project = "Unity";
            try { project = System.IO.Path.GetFileName(System.IO.Directory.GetParent(Application.dataPath).FullName); }
            catch { /* ignore */ }
            string machine = Environment.MachineName;
            HostName = $"{project} · {machine}";
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(machine + "|" + Application.dataPath));
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
                HostId = sb.ToString();
            }
            string os = Application.platform.ToString().StartsWith("Windows") ? "Windows" : "macOS";
            m_HelloPre = "{\"v\":1,\"type\":\"hello\",\"id\":\"" + HostId + "\",\"name\":\"" + HostName +
                          "\",\"machine\":\"" + machine + "\",\"project\":\"" + project + "\",\"os\":\"" + os +
                          "\",\"ip\":\"";
            m_HelloPost = "\",\"videoPort\":" + HyperSonicVideoStreamer.VideoPort;
            m_HelloJson = ComposeHello(LocalIpFor(null));
        }

        private string ComposeHello(string ip) =>
            (m_HelloPre ?? "{}") + (ip ?? "") + (m_HelloPost ?? "") +
            $",\"sw\":{m_ScreenW},\"sh\":{m_ScreenH}}}";

        /// <summary>相手ごとに最適な自 IP を入れた hello を返す。
        /// sw/sh は現在のゲーム画面サイズで、アプリ側が映像アスペクトを
        /// 「端末の向き」ではなく「ゲームの向き」に合わせるために使う。</summary>
        private string HelloFor(string remoteIp) => ComposeHello(LocalIpFor(remoteIp));
        private int m_ScreenW, m_ScreenH;

        /// <summary>
        /// 映像接続に使う自 IP を選ぶ。相手と同一サブネットのアドレスを最優先する。
        /// 旧実装は DNS の先頭アドレスを返しており、仮想 NIC (VPN/Tailscale 等) が
        /// 先に来る環境では端末から到達不能な IP を教えて映像だけ死ぬ事故になった
        /// (2026-11-12 実測: UDP は自動接続で通るのに映像 TCP だけ繋がらない)。
        /// </summary>
        private static string LocalIpFor(string remoteIp)
        {
            var locals = new System.Collections.Generic.List<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            locals.Add(ua.Address);
                }
            }
            catch { /* ignore */ }
            if (locals.Count == 0)
            {
                try
                {
                    foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                            return ip.ToString();
                }
                catch { /* ignore */ }
                return "";
            }
            if (!string.IsNullOrEmpty(remoteIp) && IPAddress.TryParse(remoteIp, out var remote))
            {
                var rb = remote.GetAddressBytes();
                IPAddress bestFit = null; int bestScore = -1;
                foreach (var ip in locals)
                {
                    var lb = ip.GetAddressBytes();
                    int prefix = 0;
                    for (int i = 0; i < 4; i++) { if (lb[i] == rb[i]) prefix += 8; else break; }
                    // 同点ならプライベート範囲 (192.168/10/172.16) を優先 (CGNAT 100.64 等は後ろ)
                    int score = prefix * 2 + (IsPrivateLan(ip) ? 1 : 0);
                    if (score > bestScore) { bestScore = score; bestFit = ip; }
                }
                if (bestFit != null) return bestFit.ToString();
            }
            foreach (var ip in locals) if (IsPrivateLan(ip)) return ip.ToString();
            return locals[0].ToString();
        }

        private static bool IsPrivateLan(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
        }

        /// <summary>
        /// Bonjour 広告。macOS は標準搭載の dns-sd、Windows は Bonjour 同梱の dns-sd.exe を使う。
        /// 入力 (_hypersonic._udp:47777) と映像 (_hypersonic-v._tcp:47780) の2件を登録する。
        /// dns-sd が無い環境では静かに諦める (アプリ側の discover/hello と手動 IP が残る)。
        /// </summary>
        private void StartBonjour()
        {
            string exe = null;
            if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer)
                exe = "/usr/bin/dns-sd";
            else if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)
                exe = "dns-sd.exe"; // PATH 上 (Bonjour for Windows)
            if (exe == null) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    Arguments = $"-R \"{HostName}\" _hypersonic._udp local {Port} id={HostId}",
                    UseShellExecute = false, CreateNoWindow = true,
                };
                m_DnsSdInput = System.Diagnostics.Process.Start(psi);
                var psi2 = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    Arguments = $"-R \"{HostName}\" _hypersonic-v._tcp local {HyperSonicVideoStreamer.VideoPort} id={HostId}",
                    UseShellExecute = false, CreateNoWindow = true,
                };
                m_DnsSdVideo = System.Diagnostics.Process.Start(psi2);
                Debug.Log($"[HyperSonic] Bonjour advertising as '{HostName}' (id={HostId})");
            }
            catch (Exception e)
            {
                Debug.Log($"[HyperSonic] Bonjour unavailable (dns-sd): {e.Message}. Manual IP / discover still work.");
            }
        }

        private void StopBonjour()
        {
            try { m_DnsSdInput?.Kill(); } catch { /* ignore */ }
            try { m_DnsSdVideo?.Kill(); } catch { /* ignore */ }
            m_DnsSdInput = null; m_DnsSdVideo = null;
        }

        /// <summary>
        /// USB 経路 (受信用): パッケージ同梱の HyperSonicUsbRelay (Editor) が
        /// usbmuxd へ直接接続し、localhost:47778 への TCP 接続を USB ケーブル越しに
        /// 端末の 47779 へ中継する。利用者側の追加インストールや iproxy の
        /// 手動起動は不要 (ケーブルを繋ぐだけ)。
        /// Unity Remote や Xcode が使う usbmux と同じ仕組みで、Wi-Fi を通らない。
        /// 接続できない間は静かにリトライし続ける (端末未接続は通常状態のため)。
        /// </summary>
        private void UsbLoop()
        {
            while (m_Running)
            {
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        client.NoDelay = true; // 小さな入力パケットを溜めない (遅延対策の要)
                        client.Connect(IPAddress.Loopback, UsbLocalPort);
                        m_UsbClient = client;
                        Debug.Log("[HyperSonic] USB input connected.");
                        var stream = client.GetStream();
                        var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                        string line;
                        while (m_Running && (line = reader.ReadLine()) != null)
                        {
                            if (line.Contains("\"ping\""))
                            {
                                var pong = Encoding.UTF8.GetBytes(line.Replace("\"ping\"", "\"pong\"") + "\n");
                                stream.Write(pong, 0, pong.Length);
                            }
                            else if (line.Contains("\"whoami\"") || line.Contains("\"discover\""))
                            {
                                var hello = Encoding.UTF8.GetBytes(ComposeHello(LocalIpFor(null)) + "\n");
                                stream.Write(hello, 0, hello.Length);
                            }
                            else m_Inbox.Enqueue(line);
                        }
                    }
                }
                catch { /* iproxy 未起動・端末未接続: 1秒後に再試行 */ }
                Thread.Sleep(1000);
            }
        }

        private void OnDisable()
        {
            m_Running = false;
            VideoRequested = false;
            StopBonjour();
            try { m_Udp?.Close(); } catch { /* ignore */ }
            try { m_UsbClient?.Close(); } catch { /* ignore */ }
            m_Udp = null;
            if (Instance == this) Instance = null;
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (m_Running)
            {
                try
                {
                    var data = m_Udp.Receive(ref any);
                    m_LastSender = any;
                    var json = Encoding.UTF8.GetString(data);
                    // ping は計測のため即 echo する（メインスレッドを介さない）
                    if (json.Contains("\"ping\""))
                    {
                        var pong = json.Replace("\"ping\"", "\"pong\"");
                        var bytes = Encoding.UTF8.GetBytes(pong);
                        m_Udp.Send(bytes, bytes.Length, any);
                    }
                    // 発見 (discover) と自己紹介要求 (whoami) には hello を即返す。
                    // ブロードキャストで届いた discover にもユニキャストで返る。
                    else if (json.Contains("\"discover\"") || json.Contains("\"whoami\""))
                    {
                        // 映像用 IP は相手と同一サブネットのものを都度選ぶ
                        var bytes = Encoding.UTF8.GetBytes(HelloFor(any.Address.ToString()));
                        m_Udp.Send(bytes, bytes.Length, any);
                    }
                    else
                    {
                        m_Inbox.Enqueue(json);
                    }
                }
                catch (SocketException) { if (!m_Running) break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        /// <summary>この秒数その指のパケットが来なければ Canceled として解放する。
        /// アプリは押下中の指を 0.1 秒間隔で Stationary 送信し続けるため、
        /// 正常な長押しがここに引っかかることはない。UDP で Ended が落ちた時の安全弁。</summary>
        public float staleTouchTimeout = 1.0f;

        private static bool IsTransition(TouchPhase ph) =>
            ph == TouchPhase.Began || ph == TouchPhase.Ended || ph == TouchPhase.Canceled;

        private void Update()
        {
            // hello に載せるゲーム画面サイズをメインスレッドで最新化する
            m_ScreenW = Screen.width;
            m_ScreenH = Screen.height;
            while (m_Inbox.TryDequeue(out var json))
                ApplyPacket(json);

            // 安全弁: パケットが途絶した指は Canceled を遷移列へ積む (公開は遷移
            // ロジック側で1フレーム行われる)。既に終了遷移が積まれている指や、
            // 終了済みの指には積まない。
            if (staleTouchTimeout > 0)
            {
                float now = Time.realtimeSinceStartup;
                var stale = new System.Collections.Generic.List<int>();
                foreach (var id in new System.Collections.Generic.List<int>(m_LastSeen.Keys))
                {
                    if (now - m_LastSeen[id] <= staleTouchTimeout) continue;
                    if (!LastKnown(id, out var last)) continue;
                    if (last.phase == TouchPhase.Ended || last.phase == TouchPhase.Canceled) continue;
                    if (HasPendingTransition(id, TouchPhase.Canceled) || HasPendingTransition(id, TouchPhase.Ended)) continue;
                    stale.Add(id);
                }
                foreach (var id in stale)
                {
                    LastKnown(id, out var last);
                    last.phase = TouchPhase.Canceled;
                    last.deltaPosition = Vector2.zero;
                    last.deltaTime = now - m_LastSeen[id];
                    EnqueuePending(id, last);
                }
            }

            // 遷移を1段ずつ進めて今フレームの公開分を決める。
            // - 公開中の指が遷移 phase (Began/Ended/Canceled) なら、それは前フレーム
            //   で丸1フレーム見えたので、次の段へ進んでよい。
            // - 公開中が Moved/Stationary なら、積まれた非遷移状態は最新へ畳んで
            //   よいが、遷移が積まれていればそこで止めて遷移を公開する。
            // - Ended/Canceled を公開した指は次フレームで消す (今フレームは見せる)。
            var ids = new System.Collections.Generic.HashSet<int>(m_Active.Keys);
            foreach (var id in m_Pending.Keys) ids.Add(id);
            var finished = new System.Collections.Generic.List<int>();
            foreach (var id in ids)
            {
                bool hasCur = m_Active.TryGetValue(id, out var cur);
                if (hasCur && (cur.phase == TouchPhase.Ended || cur.phase == TouchPhase.Canceled))
                {
                    // 終了は前フレームで公開済み。残りの遷移 (同じ id の再利用による
                    // 新しい Began 等) が無ければ指を消す。
                    if (!HasPending(id)) { finished.Add(id); continue; }
                    hasCur = false; // 次の段 (新しい指) として扱う
                }
                if (!HasPending(id))
                {
                    // 新しい状態が無い Began は、指がまだ押されている間 Stationary
                    // として公開し続ける (ネイティブのタッチと同じ)。
                    if (hasCur && cur.phase == TouchPhase.Began)
                    {
                        cur.phase = TouchPhase.Stationary;
                        cur.deltaPosition = Vector2.zero;
                        m_Active[id] = cur;
                    }
                    continue;
                }
                var q = m_Pending[id];
                RemoteTouch next = default;
                bool have = false;
                while (q.Count > 0)
                {
                    var s = q.Dequeue();
                    if (IsTransition(s.phase))
                    {
                        if (have) { RequeueFront(id, s); break; } // 非遷移を先に公開し、遷移は次フレーム
                        next = s; have = true; break;
                    }
                    // 非遷移は最新へ畳む。delta は最後に公開した位置からの総量にする。
                    if (have) s.deltaPosition = s.position - next.position + next.deltaPosition;
                    else if (hasCur) s.deltaPosition = s.position - cur.position;
                    next = s; have = true;
                }
                if (have) m_Active[id] = next;
                if (q.Count == 0) m_Pending.Remove(id);
            }
            foreach (var id in finished) { m_Active.Remove(id); m_LastSeen.Remove(id); }

            m_Touches = new System.Collections.Generic.List<RemoteTouch>(m_Active.Values).ToArray();
        }

        private bool HasPending(int id) =>
            m_Pending.TryGetValue(id, out var q) && q.Count > 0;

        private bool HasPendingTransition(int id, TouchPhase ph)
        {
            if (!m_Pending.TryGetValue(id, out var q)) return false;
            foreach (var s in q) if (s.phase == ph) return true;
            return false;
        }

        private bool LastKnown(int id, out RemoteTouch last)
        {
            last = default;
            bool found = false;
            if (m_Active.TryGetValue(id, out var a)) { last = a; found = true; }
            if (m_Pending.TryGetValue(id, out var q))
                foreach (var s in q) { last = s; found = true; }
            return found;
        }

        private void EnqueuePending(int id, RemoteTouch s)
        {
            if (!m_Pending.TryGetValue(id, out var q))
            {
                q = new System.Collections.Generic.Queue<RemoteTouch>();
                m_Pending[id] = q;
            }
            q.Enqueue(s);
        }

        private void RequeueFront(int id, RemoteTouch s)
        {
            // Queue には先頭へ戻す操作が無いので、遷移1件のために作り直す (低頻度)。
            var rest = HasPending(id) ? m_Pending[id].ToArray() : System.Array.Empty<RemoteTouch>();
            var q = new System.Collections.Generic.Queue<RemoteTouch>();
            q.Enqueue(s);
            foreach (var r in rest) q.Enqueue(r);
            m_Pending[id] = q;
        }

        private void ApplyPacket(string json)
        {
            Packet pkt;
            try { pkt = JsonUtility.FromJson<Packet>(json); }
            catch { return; }
            if (pkt == null) return;
            if (pkt.type == "mode")
            {
                VideoRequested = pkt.video != 0;
                if (pkt.fps > 0) VideoFps = pkt.fps;
                if (pkt.q > 0) VideoQuality = pkt.q;
                if (pkt.vw > 0 && pkt.vh > 0) { VideoDeviceWidth = pkt.vw; VideoDeviceHeight = pkt.vh; }
                VideoMaxWidth = pkt.mw > 0 ? pkt.mw : 0;
                Debug.Log($"[HyperSonic] Video mode -> {(VideoRequested ? $"ON ({VideoFps}fps q{VideoQuality})" : "OFF")}");
                return;
            }
            if (pkt.type != "touch" || pkt.touches == null) return;

            float now = Time.realtimeSinceStartup;
            float w = Mathf.Max(1, Screen.width);
            float h = Mathf.Max(1, Screen.height);

            // 端末のタッチは「表示されている範囲」に対する正規化座標。
            // 画面全体へ対応付けると、アスペクトが違う時に見た目と触った場所がずれる。
            var vis = touchMapping == TouchMappingMode.VisibleArea
                ? VisibleRect01(w, h)
                : new Rect(0, 0, 1, 1);
            foreach (var t in pkt.touches)
            {
                var pos = new Vector2((vis.x + t.x * vis.width) * w, (vis.y + t.y * vis.height) * h);
                LastKnown(t.id, out var prev);
                float last = m_LastSeen.TryGetValue(t.id, out var ls) ? ls : now;
                var rt = new RemoteTouch
                {
                    fingerId = t.id,
                    phase = (TouchPhase)t.ph,
                    position = pos,
                    rawPosition = t.ph == 0 ? pos : prev.rawPosition,
                    deltaPosition = t.ph == 0 ? Vector2.zero : pos - prev.position,
                    pressure = t.pr,
                    deltaTime = now - last,
                };
                // 直接 m_Active へ書かず遷移列へ積む。Update 側が1フレーム1段で
                // 公開するので、同じバッチで届いた Began が Moved に潰れない。
                EnqueuePending(t.id, rt);
                m_LastSeen[t.id] = now;
            }
        }

        private void OnGUI()
        {
            if (!drawDebug) return;
            foreach (var t in m_Touches)
            {
                // GUI 座標は左上原点なので反転する
                var guiPos = new Vector2(t.position.x, Screen.height - t.position.y);
                GUI.Box(new Rect(guiPos.x - 22, guiPos.y - 22, 44, 44), t.fingerId.ToString());
            }
            GUI.Label(new Rect(12, 12, 600, 24),
                $"[HyperSonic] touches={m_Touches.Length} from={(m_LastSender != null ? m_LastSender.Address.ToString() : "-")}");
        }

        [Serializable] private class Packet { public int v; public long seq; public long t; public string type; public TouchDto[] touches; public int video; public int fps; public int q; public int vw; public int vh; public int mw; }
        [Serializable] private class TouchDto { public int id; public int ph; public float x; public float y; public float pr; }
    }
}
