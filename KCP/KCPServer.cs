#if SERVERSIDE
using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Diagnostics;
using KCP.Common;

namespace KCP.Server {


    public delegate void RecvDataHandler(ClientSession session, byte[] data, int offset, int size);

    /// <summary>
    /// 支持连接管理的KCPserver。
    /// 外部组件通过AddClientKey方法增加合法的客户端conv(index)和key信息。
    /// 客户端握手时，如果不存在握手请求中的conv（index）和key，握手将失败。
    /// </summary>
    public class KCPServer {
        private static readonly DateTime utc_time = new DateTime(1970, 1, 1);

        public static UInt32 iclock() {
            return (UInt32)(Convert.ToInt64(DateTime.UtcNow.Subtract(utc_time).TotalMilliseconds) & 0xffffffff);
        }
        private string m_host;
        private ushort m_port;
        private Socket m_UdpServer;

        internal Stopwatch m_watch;
        private SwitchQueue<SocketAsyncEventArgs> mRecvQueue = new SwitchQueue<SocketAsyncEventArgs>(128);
        private Stack<SocketAsyncEventArgs> m_saePool = new Stack<SocketAsyncEventArgs>();
        private int BUFFSIZE = 8 * 1024;
        private AutoResetEvent m_wait = new AutoResetEvent(true);


        /// <summary>
        /// 收到数据事件
        /// 数据来自KCPServer.BytePool，调用完毕将立即回收。
        /// 如果有需要，请自行Copy。
        /// </summary>
        public event RecvDataHandler RecvData;
        /// <summary>
        /// 新的客户端连接事件
        /// </summary>
        public event RecvDataHandler NewClientSession;
        public event Action<ClientSession> CloseClientSession;

        public INewClientSessionProcessor NewSessionProcessor;
        public ArrayPool<byte> BytePool;

        public KCPServer(string host, UInt16 port) {
            m_host = host;
            m_port = port;

            if(UdpLibConfig.UseBytePool) {
                BytePool = ArrayPool<byte>.Create(8 * 1024, 50);
            }
            else {
                BytePool = ArrayPool<byte>.System();
            }

            KCPLib.BufferAlloc = (size) => {
                return BytePool.Rent(size);
            };
            KCPLib.BufferFree = (buf) => {
                BytePool.Return(buf);
            };

        }
        public void Start() {
            m_watch = Stopwatch.StartNew();
            m_UdpServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_UdpServer.Bind(new IPEndPoint(IPAddress.Parse(m_host), m_port));
            var e = PopSAE();
            m_UdpServer.ReceiveFromAsync(e);
        }

        private void StartRecv() {
            m_UdpServer.ReceiveFromAsync(PopSAE());
        }

        private void E_Completed(object sender, SocketAsyncEventArgs e) {
            if(e.LastOperation == SocketAsyncOperation.ReceiveFrom) {
#if DEV
                uint index = 0, key = 0;
                if(IsHandshake(e.Buffer, 0, e.BytesTransferred, out index, out key)) {
                    IRQLog.AppLog.Log(index.ToString() + ",收到数据");
                }
#endif
                mRecvQueue.Push(e);
                m_wait.Set();
                StartRecv();
            }
            else if(e.LastOperation == SocketAsyncOperation.SendTo) {
                PushSAE(e);
            }
        }
        private SocketAsyncEventArgs PopSAE() {
            lock(m_saePool) {
                if(m_saePool.Count == 0) {
                    for(int i = 0; i < 10; i++) {
                        SocketAsyncEventArgs e = new SocketAsyncEventArgs();
                        e.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        e.Completed += E_Completed;
                        e.SetBuffer(new byte[BUFFSIZE], 0, BUFFSIZE);
                        m_saePool.Push(e);
                    }
                }
                return m_saePool.Pop();
            }
        }
        private void PushSAE(SocketAsyncEventArgs e) {
            lock(m_saePool) {
                m_saePool.Push(e);
            }
        }

        public void Update() {
            m_wait.WaitOne(1);
            ProcessDisposedQueue();
            ProcessRecvQueue();
            ProcessClientSession();

        }

        private void ProcessClientSession() {
            m_SessionLocker.EnterReadLock();
            foreach(var v in m_clients.Values) {
                v.Update();
            }
            m_SessionLocker.ExitReadLock();
        }
        /// <summary>
        /// 处理释放的连接
        /// </summary>
        private void ProcessDisposedQueue() {
            while(m_disposedQueue.Count > 0) {
                var e = m_disposedQueue.Dequeue();
                RemoveClientSession(e);
                RmeoveClientKey(e.NetIndex);
                e.m_Kcp.Dispose();
                if(CloseClientSession != null) {
                    CloseClientSession(e);
                }
            }
        }

        private void ProcessRecvQueue() {
            mRecvQueue.Switch();
            while(!mRecvQueue.Empty()) {
                var e = mRecvQueue.Pop();
                if(e.BytesTransferred == 0) {
                    PushSAE(e);
                    //说明客户端关闭了
                    continue;
                }
                uint index = 0, key = 0;
                //使用纯udp进行握手，8字节0xFF+4字节conv+4字节key
                if(IsHandshake(e.Buffer, e.Offset, e.BytesTransferred, out index, out key)) {
                    var c = GetSession(index);
                    uint cc = 0;
                    KCPLib.ikcp_decode32u(e.Buffer, e.Offset + 16, ref cc);
                    if(c == null) {
                        bool add = true;
                        //新连接处理，如果返回false，则不予处理，可以用来进行非法连接的初步处理
                        if(NewSessionProcessor != null) {
                            add = NewSessionProcessor.OnNewSession(index, e.RemoteEndPoint);
                        }
                        if(add == false) {
                            PushSAE(e);
                            continue;
                        }
                        c = AddSession(e.RemoteEndPoint, index);
                        c.m_UdpServer = this;
                        c.m_LastRecvTimestamp = m_watch.Elapsed;
                        OnNewClientSession(c, e.Buffer, e.Offset, e.BytesTransferred);
#if DEV
                        IRQLog.AppLog.Log(index.ToString() + ",连接1," + cc.ToString());
#endif
                    }
                    else {
#if DEV
                        IRQLog.AppLog.Log(index.ToString() + ",连接2," + cc.ToString());
#endif
                        c.EndPoint = e.RemoteEndPoint;
                        //如果客户端关闭并且立刻重连，这时候是连不上的，因为KCP中原有的数据不能正确处理
                        //c.ResetKCP();
                    }

                    c.Status = ClientSessionStatus.Connected;
                    //回发握手请求
                    if(UdpLibConfig.ServerSendAsync) {
                        m_UdpServer.SendToAsync(e);
                    }
                    else {
#if DEBUG
                        Stopwatch sw = Stopwatch.StartNew();
                        m_UdpServer.SendTo(e.Buffer, e.Offset, e.BytesTransferred, SocketFlags.None, e.RemoteEndPoint);
                        Console.WriteLine((sw.ElapsedTicks * 1000f / Stopwatch.Frequency));
#else
                        m_UdpServer.SendTo(e.Buffer, e.Offset, e.BytesTransferred, SocketFlags.None, e.RemoteEndPoint);

#endif
                        PushSAE(e);
                    }
#if DEV
                    IRQLog.AppLog.Log(index.ToString() + ",发送数据UDP");
#endif
                }
                else {
                    try {

                        KCPLib.ikcp_decode32u(e.Buffer, e.Offset, ref index);
                        var c = GetSession(index);
                        if(c != null && c.Status == ClientSessionStatus.Connected) {
                            Debug.Assert(c.EndPoint.ToString() == e.RemoteEndPoint.ToString());
                            //c.EndPoint = e.RemoteEndPoint;

                            c.process_recv_queue(e);
                        }
                    }
                    finally {
                        PushSAE(e);
                    }
                }

            }
        }

        private bool IsHandshake(byte[] buffer, int offset, int size, out uint index, out uint key) {
            if(HandshakeUtility.IsHandshakeDataRight(buffer, offset, size, out index, out key)) {
                return IsClientKeyCorrect(index, (int)key);
            }
            else {
                return false;
            }
        }

        internal void Send(ClientSession session, byte[] data, int offset, int size) {
            //mUdpServer.SendToAsync
            // Stopwatch sw = Stopwatch.StartNew();
            if(UdpLibConfig.ServerSendAsync) {

                var e = PopSAE();
                e.RemoteEndPoint = session.EndPoint;
                Array.Copy(data, offset, e.Buffer, 0, size);
                e.SetBuffer(0, size);
                m_UdpServer.SendToAsync(e);
            }
            else {
                m_UdpServer.SendTo(data, offset, size, SocketFlags.None, session.EndPoint);
            }
#if DEV
            IRQLog.AppLog.Log(session.NetIndex.ToString() + ",发送数据KCP");
#endif
            // Console.WriteLine(((double)sw.ElapsedTicks / Stopwatch.Frequency) * 1000);
        }

        internal void OnRecvData(ClientSession session, byte[] data, int offset, int size) {
            var e = RecvData;
            if(e != null) {
                e(session, data, offset, size);
            }
        }
        internal void OnNewClientSession(ClientSession clientSession, byte[] buf, int offset, int size) {
            //clientSession.Send("Connect OK");

            var e = NewClientSession;
            if(e != null) {
                e(clientSession, buf, offset, size);
            }
        }
        internal void OnCloseClientSession(ClientSession clientSession) {
            var e = CloseClientSession;
            if(e != null) {
                e(clientSession);
            }
        }
        #region ClientSession
        //Dictionary<EndPoint, ClientSession> m_clients = new Dictionary<EndPoint, ClientSession>();
        Dictionary<uint, int> m_clientsKey = new Dictionary<uint, int>();
        Dictionary<uint, ClientSession> m_clients = new Dictionary<uint, ClientSession>();
        ReaderWriterLockSlim m_SessionLocker = new ReaderWriterLockSlim();
        ReaderWriterLockSlim m_KeyLocker = new ReaderWriterLockSlim();
        private object LOCK = new object();
        private ClientSession GetSession(uint index) {
            ClientSession ret;
            m_SessionLocker.EnterReadLock();
            m_clients.TryGetValue(index, out ret);
            m_SessionLocker.ExitReadLock();
            return ret;
        }
        private ClientSession AddSession(EndPoint remoteEndPoint, uint index) {
            m_SessionLocker.EnterWriteLock();
            var ret = new ClientSession(index);
            ret.EndPoint = remoteEndPoint;

            m_clients.Add(index, ret);
            m_SessionLocker.ExitWriteLock();
            return ret;
        }
        public void RemoveClientSession(ClientSession session) {
            m_SessionLocker.EnterWriteLock();
            m_clients.Remove(session.NetIndex);
            m_SessionLocker.ExitWriteLock();

        }
        /// <summary>
        /// 增加客户端 conv(index)和key        
        /// </summary>
        /// <param name="index"></param>
        /// <param name="key"></param>
        public void AddClientKey(uint index, int key) {
            m_KeyLocker.EnterWriteLock();
            if(m_clientsKey.ContainsKey(index)) {
                m_clientsKey.Remove(index);
            }
            m_clientsKey.Add(index, key);
            m_KeyLocker.ExitWriteLock();
        }
        public void RmeoveClientKey(uint index) {
            m_KeyLocker.EnterWriteLock();
            m_clientsKey.Remove(index);
            m_KeyLocker.ExitWriteLock();
        }
        public bool HasClientKey(uint index) {
            int k;
            m_KeyLocker.EnterReadLock();
            bool has = m_clientsKey.TryGetValue(index, out k);
            m_KeyLocker.ExitReadLock();
            return has;
        }
        public bool IsClientKeyCorrect(uint index, int key) {
            int k;
            m_KeyLocker.EnterReadLock();
            bool has = m_clientsKey.TryGetValue(index, out k);
            m_KeyLocker.ExitReadLock();
            if(has == false) {
                Console.WriteLine("未找到key.Index:" + index.ToString());
            }
            return key == k;
        }
        #endregion
        private Queue<ClientSession> m_disposedQueue = new Queue<ClientSession>();
        internal void AddToDisposedQueue(ClientSession clientSession) {
            m_disposedQueue.Enqueue(clientSession);
        }




    }

    #region ClientSession
    public class ClientSession {
        private uint m_netIndex;
        /// <summary>
        /// 客户端连接索引conv(index)
        /// </summary>
        public uint NetIndex {
            get {
                return m_netIndex;
            }


        }
        ClientSessionStatus m_Status = ClientSessionStatus.InConnect;
        public ClientSessionStatus Status {
            get {
                return m_Status;
            }
            set {
                m_Status = value;
            }
        }
        public int Key;
        public EndPoint @EndPoint;
        public ClientSessionDisposeReason DisposeReason = ClientSessionDisposeReason.None;
        internal KCPServer m_UdpServer;
        /// <summary>
        /// 最后收到数据的时刻。
        /// 当超过UdpLibConfig.MaxTimeNoData时间没有收到客户端的数据，则可以认为是死链接
        /// </summary>
        internal TimeSpan m_LastRecvTimestamp;
        internal KCPLib m_Kcp;
        private bool m_NeedUpdateFlag = false;
        private UInt32 m_NextUpdateTime;
        public ClientSession(uint index) {
            init_kcp(index);
            m_netIndex = index;
        }

        void init_kcp(UInt32 conv) {
            m_Kcp = new KCPLib(conv, (byte[] buf, int size) => {
                m_UdpServer.Send(this, buf, 0, size);
            });

            // fast mode.
            m_Kcp.NoDelay(1, 10, 2, 1);
            m_Kcp.WndSize(128, 128);
        }
        /// <summary>
        /// 和Update同一个线程调用
        /// </summary>
        /// <param name="buf"></param>
        public void Send(byte[] buf) {
            m_Kcp.Send(buf, 0, buf.Length);
            m_NeedUpdateFlag = true;
        }
        /// <summary>
        /// 和Update同一个线程调用
        /// </summary>
        public void Send(string str) {
            byte[] buf = this.m_UdpServer.BytePool.Rent(32 * 1024);
            int bytes = System.Text.ASCIIEncoding.ASCII.GetBytes(str, 0, str.Length, buf, 0);
            Send(buf, 0, bytes);
            this.m_UdpServer.BytePool.Return(buf, false);
        }

        private void Send(byte[] buf, int offset, int bytes) {
            m_Kcp.Send(buf, offset, bytes);
            m_NeedUpdateFlag = true;
        }

        public void Update() {
            update(KCPServer.iclock());
            if(m_UdpServer.m_watch.Elapsed - m_LastRecvTimestamp > UdpLibConfig.MaxTimeNoData) {
                DisposeReason = ClientSessionDisposeReason.MaxTimeNoData;
                Dispose();
            }
        }

        public void Close() {
            //mUdpServer.Close();
        }

        /// <summary>
        /// 和Update同一个线程调用
        /// </summary>
        internal void process_recv_queue(SocketAsyncEventArgs e) {
#if DEV
            IRQLog.AppLog.Log(this.m_netIndex.ToString() + ",接收1");
#endif
            m_Kcp.Input(e.Buffer, e.Offset, e.BytesTransferred);

            m_NeedUpdateFlag = true;

            for(var size = m_Kcp.PeekSize(); size > 0; size = m_Kcp.PeekSize()) {
                byte[] buffer;
                buffer = (UdpLibConfig.UseBytePool ? m_UdpServer.BytePool.Rent(size) : new byte[size]);
                try {

                    if(m_Kcp.Recv(buffer) > 0) {
                        m_LastRecvTimestamp = m_UdpServer.m_watch.Elapsed;

                        uint key = 0;
                        KCPLib.ikcp_decode32u(buffer, 0, ref key);
                        if(m_UdpServer.IsClientKeyCorrect(this.m_netIndex, (int)key) == false) {
#if DEBUG
                            Console.WriteLine("index:{0} key 不对", this.m_netIndex);
#endif
                            m_UdpServer.BytePool.Return(buffer, true);
                            DisposeReason = ClientSessionDisposeReason.IndexKeyError;
                            //key不对
                            Dispose();
                            return;
                        }
#if DEV
                    IRQLog.AppLog.Log(this.m_netIndex.ToString() + ",接收2");
#endif
                        m_UdpServer.OnRecvData(this, buffer, 0, size);
                    }
                }
                finally {
                    if(UdpLibConfig.UseBytePool) {
                        m_UdpServer.BytePool.Return(buffer, true);
                    }
                }
            }
        }

        private bool m_Disposed = false;
        private void Dispose() {
            if(m_Disposed) {
                return;
            }
            m_Disposed = true;
            m_UdpServer.AddToDisposedQueue(this);
        }



        void update(UInt32 current) {
            if(m_Status != ClientSessionStatus.Connected) {
                return;
            }
            if(m_NeedUpdateFlag || current >= m_NextUpdateTime) {
                m_Kcp.Update(current);
                m_NextUpdateTime = m_Kcp.Check(current);
                m_NeedUpdateFlag = false;
            }
        }

        internal void ResetKCP() {
            init_kcp(m_netIndex);
        }
    }

    public enum ClientSessionDisposeReason {
        None = 0,
        Normal,
        IndexKeyError,
        MaxTimeNoData
    }
    #endregion
    /// <summary>
    /// 新客户端接口
    /// </summary>
    public interface INewClientSessionProcessor {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="index"></param>
        /// <param name="remoteEndPoint"></param>
        /// <returns>如果为false，则丢弃</returns>
        bool OnNewSession(uint index, EndPoint remoteEndPoint);
    }
}
#endif