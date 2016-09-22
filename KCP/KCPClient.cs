using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Diagnostics;
using KCP.Common;
using System.Threading;

namespace KCP.Client {
    public enum UdpClientEvents {
        Connected = 0,
        ConnectFail = 1,
        Close = 2,
        Recv = 3,
        Send = 4,
        ConnectTimeout = 5
    }
    /// <summary>
    /// 客户端使用服务器生成的conv(index)和key与服务器进行通信。
    /// 服务器使用tcp或者其他方式将conv和key告知客户端。
    /// 客户端首先通过裸UDP与服务器握手，握手成功后使用KCP与服务端通信。
    /// </summary>
    public class KCPClient {
        private static readonly DateTime utc_time = new DateTime(1970, 1, 1);

        public static UInt32 iclock() {
            return (UInt32)(Convert.ToInt64(DateTime.UtcNow.Subtract(utc_time).TotalMilliseconds) & 0xffffffff);
        }

        private UdpClient m_UdpClient;
        private IPEndPoint mIPEndPoint;
        private IPEndPoint mSvrEndPoint;

        private KCPLib m_Kcp;
        private bool m_NeedUpdateFlag;
        private UInt32 m_NextUpdateTime;

        private SwitchQueue<byte[]> m_RecvQueue = new SwitchQueue<byte[]>(128);
        private uint m_NetIndex = 0;
        private int m_Key = 0;
        public ClientSessionStatus Status = ClientSessionStatus.InConnect;
        public event Action<UdpClientEvents, byte[]> Event;

        public KCPClient() {

        }

        /// <summary>
        /// 连接到服务端。
        /// 在update中，状态将会更新。
        /// 如果status为ConnectFail，则需要重新Connect
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="index"></param>
        /// <param name="key"></param>
        public void Connect(string host, UInt16 port, uint index, int key) {
            Close();
            mSvrEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
            m_UdpClient = new UdpClient(host, port);

            m_UdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 5000);

            m_UdpClient.Connect(mSvrEndPoint);
            Status = ClientSessionStatus.InConnect;
            //init_kcp((UInt32)new Random((int)DateTime.Now.Ticks).Next(1, Int32.MaxValue));
            m_NetIndex = index;
            m_Key = key;
            init_kcp(index);
            m_sw = Stopwatch.StartNew();
            SendHandshake();
            m_LastSendHandshake = m_sw.ElapsedMilliseconds;
            m_UdpClient.BeginReceive(ReceiveCallback, this);

           
        }
        private int m_pktCounter = 0;
        //发送握手数据 16字节 8字节头+4字节index+4字节key
        private void SendHandshake() {
            if(UdpLibConfig.DebugLevel != (int)UdpLibLogLevel.None) {
#if DEBUG
                Console.WriteLine("发送握手数据");
#endif
            }
            
            Interlocked.Increment(ref m_pktCounter);
            byte[] buf = new byte[UdpLibConfig.HandshakeDataSize+4];
            Array.Copy(UdpLibConfig.HandshakeHeadData, buf, UdpLibConfig.HandshakeHeadData.Length);
            KCPLib.ikcp_encode32u(buf, UdpLibConfig.HandshakeHeadData.Length, m_NetIndex);
            KCPLib.ikcp_encode32u(buf, UdpLibConfig.HandshakeHeadData.Length + 4, (uint)m_Key);
            KCPLib.ikcp_encode32u(buf, UdpLibConfig.HandshakeHeadData.Length + 8, (uint)m_pktCounter);
            m_UdpClient.Send(buf, buf.Length);
#if DEV
            string s = string.Format("{0},发送握手数据,{1}", m_NetIndex, m_pktCounter.ToString());
            IRQLog.AppLog.Log(s);
            Console.WriteLine(s);
#endif
        }


        void init_kcp(UInt32 conv) {
            m_Kcp = new KCPLib(conv, (byte[] buf, int size) => {
                m_UdpClient.Send(buf, size);
            });

            // fast mode.
            m_Kcp.NoDelay(1, 10, 2, 1);
            m_Kcp.WndSize(128, 128);
        }

        void ReceiveCallback(IAsyncResult ar) {
            Byte[] data = null;
            try {
                data = (mIPEndPoint == null) ?
                m_UdpClient.Receive(ref mIPEndPoint) :
                m_UdpClient.EndReceive(ar, ref mIPEndPoint);
            }
            catch(SocketException ee) {
#if DEBUG
                if(ee.NativeErrorCode == 10054) {
                    Console.WriteLine("无法连接到远程服务器");
                }
                else {
                    Console.WriteLine(ee.ToString());
                }
#endif
                this.Status = ClientSessionStatus.ConnectFail;
                var e = Event;
                if(e != null) {
                    e(UdpClientEvents.ConnectFail, null);
                }
                return;
            }

            if(null != data) {
                // push udp packet to switch queue.
                m_RecvQueue.Push(data);
            }

            if(m_UdpClient != null) {
                // try to receive again.
                m_UdpClient.BeginReceive(ReceiveCallback, this);
            }
        }
        /// <summary>
        /// 发送数据。发送时，默认在头部加上4字节的key
        /// </summary>
        /// <param name="buf"></param>
        public void Send(byte[] buf) {
            byte[] newbuf = new byte[buf.Length + 4];
            //把key附上，服务端合法性检测用
            KCPLib.ikcp_encode32u(newbuf, 0, (uint)m_Key);
            Array.Copy(buf, 0, newbuf, 4, buf.Length);

            m_Kcp.Send(newbuf);
            m_NeedUpdateFlag = true;
            var e = Event;
            if(e != null) {
                e(UdpClientEvents.Send, buf);
            }
        }

        public void Send(string str) {
            Send(System.Text.ASCIIEncoding.ASCII.GetBytes(str));
        }

        public void Update() {
            update(iclock());
        }

        public void Close() {
            if(m_UdpClient != null) {
                m_UdpClient.Close();
                m_UdpClient = null;
            }
        }

        void process_recv_queue() {

            m_RecvQueue.Switch();

            while(!m_RecvQueue.Empty()) {
                var buf = m_RecvQueue.Pop();
                if(this.Status == ClientSessionStatus.InConnect) {
                    //服务端将返回和握手请求相同的响应数据
                    uint _index = 0, _key = 0;
                    if(HandshakeUtility.IsHandshakeDataRight(buf, 0, buf.Length, out _index, out _key)) {
                        if(_index == m_NetIndex && _key == m_Key) {
#if DEBUG
                            Console.WriteLine("连接握手成功");
#endif
                            this.Status = ClientSessionStatus.Connected;
                            var e = Event;
                            if(e != null) {
                                e(UdpClientEvents.Connected, null);
                            }
                            continue;
                        }
                    }
                }


                m_Kcp.Input(buf, 0, buf.Length);
                m_NeedUpdateFlag = true;

                for(var size = m_Kcp.PeekSize(); size > 0; size = m_Kcp.PeekSize()) {
                    var buffer = new byte[size];
                    if(m_Kcp.Recv(buffer) > 0) {
                        var e = Event;
                        if(e != null) {
                            e(UdpClientEvents.Recv, buffer);
                        }
                    }
                }
            }
        }


        private long m_LastSendHandshake = 0;
        private Stopwatch m_sw;
        private int m_retryCount = 0;
        void update(UInt32 current) {
            switch(this.Status) {
                case ClientSessionStatus.InConnect:
                    ProcessHandshake();
                    break;
                case ClientSessionStatus.ConnectFail:
                    return;
                default:
                    break;
            }

            process_recv_queue();

            if(m_NeedUpdateFlag || current >= m_NextUpdateTime) {
                m_Kcp.Update(current);
                m_NextUpdateTime = m_Kcp.Check(current);
                m_NeedUpdateFlag = false;
            }


        }

        private void ProcessHandshake() {


            //每隔多久发一下握手信息
            if(m_sw.ElapsedMilliseconds - m_LastSendHandshake > UdpLibConfig.HandshakeDelay) {
                m_LastSendHandshake = m_sw.ElapsedMilliseconds;
                SendHandshake();
                m_retryCount++;
                if(m_retryCount >= UdpLibConfig.HandshakeRetry) {
                    Status = ClientSessionStatus.ConnectFail;
                    Event(UdpClientEvents.ConnectTimeout, null);
                }
            }
        }
    }
}
