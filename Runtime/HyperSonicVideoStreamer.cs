using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace HyperSonic
{
    /// <summary>
    /// 映像チャンネル (任意・既定 OFF)。
    ///
    /// 遅延設計の要点:
    /// - 入力経路 (HyperSonicReceiver の UDP/USB) とはソケットもスレッドも完全に別。
    /// - HyperSonicReceiver.VideoRequested が false の間、このコンポーネントは
    ///   Update で bool を 1 回見るだけで、キャプチャ・確保・エンコードを一切しない。
    ///   つまり映像 OFF 時の入力遅延は「入力専用アプリ」と同一になる (専用初期化は不要)。
    /// - ON の時だけ TCP 47780 で接続してきた端末へ、間引きした JPEG を送る。
    ///
    /// 映像ソース (Unity Remote と同じ「ゲーム画面だけ」を送るための方式):
    /// - Editor では Game View 専用の RenderTexture をリフレクションで取得し、
    ///   その中のゲーム矩形 (Screen アスペクトで中央に収まる領域) だけを切り出す。
    ///   ScreenCapture は Editor ではエディタウィンドウ全体 (Scene ビュー含む) を
    ///   拾ってしまうため使わない (2026-11-12 実測で確認)。
    /// - プレイヤー (ビルド) では ScreenCapture がそのままゲーム画面になるのでそれを使う。
    /// - 最後に出力を端末のアスペクト (mode パケットの vw/vh) へ中央クロップする。
    ///   つまり端末には「実機ビルドと同じ条件 (画面比率) のゲーム画面」だけが届く。
    /// </summary>
    public sealed class HyperSonicVideoStreamer : MonoBehaviour
    {
        public const int VideoPort = 47780;

        [Tooltip("映像の最大幅 (高さは端末アスペクトから決定)。小さいほど軽い")]
        public int maxWidth = 854;

        private TcpListener m_Listener;
        private TcpClient m_Client;
        private Thread m_AcceptThread;
        private volatile bool m_Running;

        private RenderTexture m_ScreenRT; // プレイヤー用 ScreenCapture 先
        private bool m_SourceFlipped;     // Game View 系 RT は上下反転して届く
        private RenderTexture m_FrameRT;  // 切り出したゲーム画面 (Screen アスペクト)
        private RenderTexture m_OutRT;    // 端末アスペクトへクロップした最終出力
        private Texture2D m_Readback;
        private Texture2D m_CropTex;    // アスペクト窓をピクセル単位で切り出した中間テクスチャ
        private float m_NextFrameAt;
        private readonly byte[] m_Header = new byte[4];

        // Editor の Game View RenderTexture をリフレクションで引くためのキャッシュ
        private object m_GameViewWindow;
        private System.Reflection.MemberInfo m_GameViewTexMember;
        private float m_NextProbeAt;

        private void OnEnable()
        {
            m_Running = true;
            m_Listener = new TcpListener(IPAddress.Any, VideoPort);
            m_Listener.Start();
            m_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HyperSonicVideoAccept" };
            m_AcceptThread.Start();
            Debug.Log($"[HyperSonic] Video listener on TCP {VideoPort} (idle until requested)");
        }

        private void OnDisable()
        {
            m_Running = false;
            try { m_Client?.Close(); } catch { /* ignore */ }
            try { m_Listener?.Stop(); } catch { /* ignore */ }
            ReleaseBuffers();
        }

        private void AcceptLoop()
        {
            while (m_Running)
            {
                try
                {
                    var client = m_Listener.AcceptTcpClient();
                    client.NoDelay = true;
                    try { m_Client?.Close(); } catch { /* ignore */ }
                    m_Client = client;
                    Debug.Log("[HyperSonic] Video client connected.");
                }
                catch { if (!m_Running) break; }
            }
        }

        private void Update()
        {
            // OFF の間はここで終わる。それ以外何もしない (これが遅延ゼロ影響の担保)。
            if (!HyperSonicReceiver.VideoRequested) return;

            var client = m_Client;
            if (client == null || !client.Connected) return;
            if (Time.realtimeSinceStartup < m_NextFrameAt) return;
            m_NextFrameAt = Time.realtimeSinceStartup + 1f / Mathf.Max(1, HyperSonicReceiver.VideoFps);

            if (!CaptureGameFrame()) return;

            // 端末アスペクトへの中央クロップは「ピクセル単位の窓」で行う。
            // 旧実装は Blit の scale (出力/入力のピクセル比) をサンプリング範囲と
            // 兼用しており、縮小率が窓の大きさに混ざって表示エリアが勝手に狭く
            // なっていた (2026-11-12 実測: 本来 0.78 倍の窓が 0.53 倍で効き、
            // タッチ位置と表示が一致しなかった)。窓はアスペクトだけで決める。
            float frameAspect = m_FrameRT.width / (float)m_FrameRT.height;
            float aspect = frameAspect;
            if (HyperSonicReceiver.VideoDeviceWidth > 0 && HyperSonicReceiver.VideoDeviceHeight > 0)
                aspect = HyperSonicReceiver.VideoDeviceWidth / (float)HyperSonicReceiver.VideoDeviceHeight;
            int cw, ch;
            if (frameAspect > aspect) { ch = m_FrameRT.height; cw = Mathf.RoundToInt(ch * aspect); }
            else { cw = m_FrameRT.width; ch = Mathf.RoundToInt(cw / aspect); }
            cw = Mathf.Clamp(cw, 2, m_FrameRT.width);
            ch = Mathf.Clamp(ch, 2, m_FrameRT.height);
            int x0 = (m_FrameRT.width - cw) / 2;
            int y0 = (m_FrameRT.height - ch) / 2;

            int widthCap = HyperSonicReceiver.VideoMaxWidth > 0 ? HyperSonicReceiver.VideoMaxWidth : maxWidth;
            int outW = Mathf.Min(cw, widthCap);
            int outH = Mathf.Max(1, Mathf.RoundToInt(outW / aspect));
            if (outH > ch) { outH = ch; outW = Mathf.RoundToInt(outH * aspect); }

            // 窓をそのまま読み出す (ReadPixels の矩形はピクセル単位で決定的)。
            // Game View / Simulator の RT は上下反転して届くので、窓の Y は
            // 読み替え、反転自体は縮小 Blit (全面サンプリング) で戻す。
            EnsureTex(ref m_CropTex, cw, ch);
            var prevActive = RenderTexture.active;
            RenderTexture.active = m_FrameRT;
            int readY = m_SourceFlipped ? (m_FrameRT.height - y0 - ch) : y0;
            m_CropTex.ReadPixels(new Rect(x0, readY, cw, ch), 0, 0, false);
            m_CropTex.Apply(false);
            RenderTexture.active = prevActive;

            EnsureRT(ref m_OutRT, outW, outH);
            if (m_SourceFlipped)
                Graphics.Blit(m_CropTex, m_OutRT, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else
                Graphics.Blit(m_CropTex, m_OutRT);

            EnsureReadback(outW, outH);
            var prev = RenderTexture.active;
            RenderTexture.active = m_OutRT;
            m_Readback.ReadPixels(new Rect(0, 0, outW, outH), 0, 0, false);
            m_Readback.Apply(false);
            RenderTexture.active = prev;

            var jpg = m_Readback.EncodeToJPG(Mathf.Clamp(HyperSonicReceiver.VideoQuality, 10, 95));
            try
            {
                var stream = client.GetStream();
                // [4byte BE length][JPEG]
                m_Header[0] = (byte)(jpg.Length >> 24);
                m_Header[1] = (byte)(jpg.Length >> 16);
                m_Header[2] = (byte)(jpg.Length >> 8);
                m_Header[3] = (byte)jpg.Length;
                stream.Write(m_Header, 0, 4);
                stream.Write(jpg, 0, jpg.Length);
            }
            catch
            {
                try { client.Close(); } catch { /* ignore */ }
                if (m_Client == client) m_Client = null;
            }
        }

        /// <summary>ゲーム画面だけを m_FrameRT (Screen アスペクト) へ取り込む。成功で true。</summary>
        private bool CaptureGameFrame()
        {
            int gameW = Mathf.Max(2, Screen.width);
            int gameH = Mathf.Max(2, Screen.height);
            float capScale = Mathf.Min(1f, 1600f / gameW);
            int fw = Mathf.Max(2, Mathf.RoundToInt(gameW * capScale));
            int fh = Mathf.Max(2, Mathf.RoundToInt(gameH * capScale));
            EnsureRT(ref m_FrameRT, fw, fh);

            var gameRT = TryGetGameViewRT();
            m_SourceFlipped = gameRT != null;
            if (gameRT != null)
            {
                // Game View の RT 内で、ゲーム矩形は Screen アスペクトで中央に収まっている
                float scale = Mathf.Min(gameRT.width / (float)gameW, gameRT.height / (float)gameH);
                float rectW = gameW * scale, rectH = gameH * scale;
                float offX = (gameRT.width - rectW) / 2f, offY = (gameRT.height - rectH) / 2f;
                Graphics.Blit(gameRT, m_FrameRT,
                    new Vector2(rectW / gameRT.width, rectH / gameRT.height),
                    new Vector2(offX / gameRT.width, offY / gameRT.height));
                return true;
            }

            // プレイヤー (ビルド) では ScreenCapture がゲーム画面そのもの
            EnsureRT(ref m_ScreenRT, gameW, gameH);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(m_ScreenRT);
            Graphics.Blit(m_ScreenRT, m_FrameRT);
            return true;
        }

        /// <summary>
        /// Editor の Game View が持つ RenderTexture をリフレクションで探す。
        /// 候補メンバー (名前とサイズ) はログへ出す (版ごとの差異を実測で確定するため)。
        /// プレイヤーでは型が無いので null を返し、ScreenCapture 経路に落ちる。
        /// 注意: Unity 6 では GameView 型は UnityEditor.CoreModule 等に分かれており、
        /// Type.GetType("..., UnityEditor") では見つからない。全アセンブリ走査が必須。
        /// </summary>
        private RenderTexture TryGetGameViewRT()
        {
#if UNITY_EDITOR
            try
            {
                // 選択済みの RT が生きている間はそれを使い続ける
                // (シミュレーター等では RT が Screen の等倍でないことがあるため、
                // サイズ一致は選択時の優先条件にだけ使い、維持条件にはしない)
                if (m_GameViewTexMember != null && m_GameViewWindow != null)
                {
                    object v = m_GameViewTexMember is System.Reflection.FieldInfo f
                        ? f.GetValue(m_GameViewWindow)
                        : ((System.Reflection.PropertyInfo)m_GameViewTexMember).GetValue(m_GameViewWindow);
                    if (v is RenderTexture cur && cur.width >= 64 && cur.height >= 64)
                        return cur;
                    m_GameViewTexMember = null;
                    m_GameViewWindow = null;
                }
                if (Time.realtimeSinceStartup < m_NextProbeAt) return null;
                m_NextProbeAt = Time.realtimeSinceStartup + 1f;

                // 対象は PlayModeView 系すべて。Game View 本体の他に、シミュレーター
                // モードのビュー (別クラス) も再生先になるため (2026-11-12 実測:
                // GameView だけ探すとシミュレーター表示中の環境で 0 件になった)。
                var gvType = FindLoadedType("UnityEditor.PlayModeView") ?? FindLoadedType("UnityEditor.GameView");
                if (gvType == null) { Debug.Log("[HyperSonic] PlayModeView/GameView type not found in any assembly."); return null; }

                // ビューは複数開かれていることがある (ドック内 + 独立パネル等)。
                // 再生中のゲームが描かれるビューの RT は Screen.width/height と一致する
                // ので、それを正として選ぶ。一致が無ければ最大の RT を暫定採用する。
                // 注意: GetWindow で新規に開くことはしない (ユーザーのレイアウトを汚すため)。
                const System.Reflection.BindingFlags Flags =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                RenderTexture best = null; long bestArea = -1; bool bestExact = false;
                var allWindows = Resources.FindObjectsOfTypeAll(gvType);
                int rtSeen = 0;
                foreach (var window in allWindows)
                {
                    // private な RT フィールドは基底クラス側に宣言されていることがあるため階層を遡る
                    for (var type = window.GetType(); type != null; type = type.BaseType)
                    {
                        foreach (var field in type.GetFields(Flags))
                        {
                            if (field.FieldType != typeof(RenderTexture)) continue;
                            var rt = field.GetValue(window) as RenderTexture;
                            if (rt == null || rt.width < 64) continue;
                            rtSeen++;
                            bool exact = rt.width == Screen.width && rt.height == Screen.height;
                            long area = (long)rt.width * rt.height;
                            Debug.Log($"[HyperSonic] View RT candidate {window.GetType().Name}.'{field.Name}': {rt.width}x{rt.height} exact={exact}");
                            if (exact && !bestExact || !bestExact && area > bestArea)
                            {
                                best = rt; bestArea = area; bestExact = exact;
                                m_GameViewWindow = window; m_GameViewTexMember = field;
                            }
                        }
                        foreach (var prop in type.GetProperties(Flags))
                        {
                            if (prop.PropertyType != typeof(RenderTexture) || !prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                            RenderTexture rt = null;
                            try { rt = prop.GetValue(window) as RenderTexture; } catch { /* ignore */ }
                            if (rt == null || rt.width < 64) continue;
                            rtSeen++;
                            bool exact = rt.width == Screen.width && rt.height == Screen.height;
                            long area = (long)rt.width * rt.height;
                            Debug.Log($"[HyperSonic] View RT candidate {window.GetType().Name}.'{prop.Name}': {rt.width}x{rt.height} exact={exact}");
                            if (exact && !bestExact || !bestExact && area > bestArea)
                            {
                                best = rt; bestArea = area; bestExact = exact;
                                m_GameViewWindow = window; m_GameViewTexMember = prop;
                            }
                        }
                    }
                }
                if (best == null)
                    Debug.Log($"[HyperSonic] View probe: windows={allWindows.Length} rtCandidates={rtSeen} (Screen {Screen.width}x{Screen.height})");
                if (best != null)
                    Debug.Log($"[HyperSonic] Using GameView RT {best.width}x{best.height} via '{m_GameViewTexMember.Name}' exact={bestExact} (Screen {Screen.width}x{Screen.height})");
                else
                    Debug.Log("[HyperSonic] No GameView RT yet; retrying (ScreenCapture fallback active).");
                return best;
            }
            catch (Exception e)
            {
                Debug.Log($"[HyperSonic] GameView RT probe failed: {e.Message}");
                return null;
            }
#else
            return null;
#endif
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        private void EnsureRT(ref RenderTexture rt, int w, int h)
        {
            if (rt != null && rt.width == w && rt.height == h) return;
            if (rt != null) { rt.Release(); Destroy(rt); }
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        }

        private void EnsureReadback(int w, int h)
        {
            if (m_Readback != null && m_Readback.width == w && m_Readback.height == h) return;
            if (m_Readback != null) Destroy(m_Readback);
            m_Readback = new Texture2D(w, h, TextureFormat.RGB24, false);
        }

        private void EnsureTex(ref Texture2D tex, int w, int h)
        {
            if (tex != null && tex.width == w && tex.height == h) return;
            if (tex != null) Destroy(tex);
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        }

        private void ReleaseBuffers()
        {
            if (m_ScreenRT != null) { m_ScreenRT.Release(); Destroy(m_ScreenRT); m_ScreenRT = null; }
            if (m_FrameRT != null) { m_FrameRT.Release(); Destroy(m_FrameRT); m_FrameRT = null; }
            if (m_OutRT != null) { m_OutRT.Release(); Destroy(m_OutRT); m_OutRT = null; }
            if (m_Readback != null) { Destroy(m_Readback); m_Readback = null; }
            if (m_CropTex != null) { Destroy(m_CropTex); m_CropTex = null; }
        }
    }
}
