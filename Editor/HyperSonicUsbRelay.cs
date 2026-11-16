using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace HyperSonic.Editor
{
    /// <summary>
    /// USB 入力の転送をパッケージ内で完結させるリレー。
    ///
    /// 従来は利用者が別途 libimobiledevice の iproxy を導入し
    /// 「iproxy 47778 47779」を手で起動する必要があった。これを廃し、
    /// usbmuxd プロトコルを直接話して localhost:47778 への接続を
    /// USB 接続中の iPhone の 47779 へ中継する。利用者はケーブルを
    /// 繋ぐだけでよい (追加インストール・コマンド不要)。
    ///
    /// usbmuxd の場所:
    ///   macOS / Linux: Unix ソケット /var/run/usbmuxd
    ///     (Linux では usbmuxd デーモン自体が OS 側に必要)
    ///   Windows: 127.0.0.1:27015 (Apple Mobile Device Service。
    ///     iTunes / Apple Devices アプリに同梱)
    /// usbmuxd に届かない環境ではリレーは諦めてポートを空け、
    /// 従来どおり手動 iproxy でも使えるようにする。
    /// </summary>
    [InitializeOnLoad]
    internal static class HyperSonicUsbRelay
    {
        private const int LocalPort = 47778;   // HyperSonicReceiver.UsbLocalPort と一致させる
        private const int DevicePort = 47779;  // HyperSonicReceiver.UsbDevicePort と一致させる
        private const string UsbmuxSocketPath = "/var/run/usbmuxd";
        private const int UsbmuxWindowsPort = 27015;

        private static TcpListener s_Listener;
        private static Thread s_AcceptThread;
        private static volatile bool s_Running;
        private static readonly List<Socket> s_Active = new List<Socket>();
        private static bool s_LoggedUnavailable;

        static HyperSonicUsbRelay()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        public static void Start()
        {
            if (s_Running) return;
            try
            {
                s_Listener = new TcpListener(IPAddress.Loopback, LocalPort);
                s_Listener.Start();
            }
            catch (Exception e)
            {
                // 既に iproxy 等が待ち受けている場合はそちらに任せる。
                Debug.Log($"[HyperSonic] USB relay skipped (port {LocalPort} unavailable: {e.Message}).");
                s_Listener = null;
                return;
            }
            s_Running = true;
            s_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HyperSonicUsbRelay" };
            s_AcceptThread.Start();
        }

        public static void Stop()
        {
            s_Running = false;
            try { s_Listener?.Stop(); } catch { /* ignore */ }
            s_Listener = null;
            lock (s_Active)
            {
                foreach (var s in s_Active) { try { s.Close(); } catch { /* ignore */ } }
                s_Active.Clear();
            }
        }

        private static void AcceptLoop()
        {
            while (s_Running)
            {
                Socket client;
                try { client = s_Listener.AcceptSocket(); }
                catch { break; } // Stop() で閉じられた
                var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "HyperSonicUsbRelayConn" };
                t.Start();
            }
        }

        private static void HandleClient(Socket client)
        {
            lock (s_Active) s_Active.Add(client);
            Socket device = null;
            try
            {
                // 端末がまだ繋がっていない場合は、現れるまで待つ。
                while (s_Running && client.Connected)
                {
                    device = TryConnectToDevice();
                    if (device != null) break;
                    Thread.Sleep(1500);
                }
                if (device == null) return;
                lock (s_Active) s_Active.Add(device);
                Debug.Log("[HyperSonic] USB relay connected to device.");
                PumpBothWays(client, device);
            }
            catch (Exception e)
            {
                Debug.Log($"[HyperSonic] USB relay connection ended: {e.Message}");
            }
            finally
            {
                try { client.Close(); } catch { /* ignore */ }
                try { device?.Close(); } catch { /* ignore */ }
                lock (s_Active) { s_Active.Remove(client); if (device != null) s_Active.Remove(device); }
            }
        }

        private static void PumpBothWays(Socket a, Socket b)
        {
            var t = new Thread(() => Pump(a, b)) { IsBackground = true, Name = "HyperSonicUsbRelayPump" };
            t.Start();
            Pump(b, a);
        }

        private static void Pump(Socket from, Socket to)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (s_Running)
                {
                    int n = from.Receive(buf);
                    if (n <= 0) break;
                    int sent = 0;
                    while (sent < n) sent += to.Send(buf, sent, n - sent, SocketFlags.None);
                }
            }
            catch { /* 切断時は静かに終わる */ }
            finally
            {
                try { to.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            }
        }

        // MARK: - usbmux プロトコル

        /// <summary>usbmuxd へ接続し、接続中デバイスの DevicePort へのトンネルを開く。失敗時は null。</summary>
        private static Socket TryConnectToDevice()
        {
            Socket mux = null;
            try
            {
                mux = OpenUsbmux();
                if (mux == null) { LogUnavailableOnce(); return null; }

                int tag = 1;
                SendPlist(mux, ref tag,
                    "<dict>" +
                    "<key>MessageType</key><string>ListDevices</string>" +
                    "<key>ClientVersionString</key><string>HyperSonic</string>" +
                    "<key>ProgName</key><string>HyperSonic</string>" +
                    "</dict>");
                string listXml = ReceivePlist(mux);
                mux.Close();
                mux = null;
                if (string.IsNullOrEmpty(listXml)) return null;

                var m = Regex.Match(listXml, @"<key>DeviceID</key>\s*<integer>(\d+)</integer>");
                if (!m.Success) return null; // 端末未接続
                int deviceId = int.Parse(m.Groups[1].Value);

                mux = OpenUsbmux();
                if (mux == null) return null;
                int swapped = ((DevicePort & 0xFF) << 8) | ((DevicePort >> 8) & 0xFF);
                SendPlist(mux, ref tag,
                    "<dict>" +
                    "<key>MessageType</key><string>Connect</string>" +
                    $"<key>DeviceID</key><integer>{deviceId}</integer>" +
                    $"<key>PortNumber</key><integer>{swapped}</integer>" +
                    "<key>ProgName</key><string>HyperSonic</string>" +
                    "</dict>");
                string resultXml = ReceivePlist(mux);
                if (resultXml == null ||
                    !Regex.IsMatch(resultXml, @"<key>Number</key>\s*<integer>0</integer>"))
                {
                    mux.Close();
                    return null; // アプリ側が USB 待機中でない等
                }
                var tunnel = mux;
                mux = null;
                return tunnel;
            }
            catch
            {
                try { mux?.Close(); } catch { /* ignore */ }
                return null;
            }
        }

        private static void LogUnavailableOnce()
        {
            if (s_LoggedUnavailable) return;
            s_LoggedUnavailable = true;
            Debug.Log("[HyperSonic] USB relay: usbmuxd not reachable. USB input stays off " +
                      "(Wi-Fi works as usual). On Windows install iTunes or the Apple Devices app; " +
                      "on Linux make sure the usbmuxd daemon is running.");
        }

        private static Socket OpenUsbmux()
        {
            try
            {
#if UNITY_EDITOR_WIN
                var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Connect(IPAddress.Loopback, UsbmuxWindowsPort);
                return tcp;
#else
                var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                unix.Connect(new UnixDomainSocketEndPoint(UsbmuxSocketPath));
                return unix;
#endif
            }
            catch
            {
                return null;
            }
        }

        private static void SendPlist(Socket s, ref int tag, string bodyXml)
        {
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                         "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
                         "<plist version=\"1.0\">" + bodyXml + "</plist>";
            byte[] payload = Encoding.UTF8.GetBytes(xml);
            var header = new byte[16];
            WriteU32(header, 0, (uint)(16 + payload.Length));
            WriteU32(header, 4, 1); // version
            WriteU32(header, 8, 8); // MESSAGE_PLIST
            WriteU32(header, 12, (uint)tag++);
            SendAll(s, header);
            SendAll(s, payload);
        }

        private static string ReceivePlist(Socket s)
        {
            var header = new byte[16];
            if (!ReadExact(s, header, 16)) return null;
            uint total = ReadU32(header, 0);
            if (total < 16 || total > 16 * 1024 * 1024) return null;
            var payload = new byte[total - 16];
            if (!ReadExact(s, payload, payload.Length)) return null;
            return Encoding.UTF8.GetString(payload);
        }

        private static void WriteU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint ReadU32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static void SendAll(Socket s, byte[] data)
        {
            int sent = 0;
            while (sent < data.Length) sent += s.Send(data, sent, data.Length - sent, SocketFlags.None);
        }

        private static bool ReadExact(Socket s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Receive(buf, read, count - read, SocketFlags.None);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
