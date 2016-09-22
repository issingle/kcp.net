using KCP.Client;
using KCP.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace test_kcp_client {
    class Program {
        static void Main(string[] args) {

            Console.WriteLine("输入ip");
            string host = Console.ReadLine();
            if(host == "") {
                host = "127.0.0.1";
            }
            Console.WriteLine("输入起始用户ID:");
            int id = int.Parse(Console.ReadLine());
            Console.WriteLine("输入客户端数量");
            int c = 300;
            string s = Console.ReadLine();
            if(s != "") {
                c = int.Parse(s);
            }
            Console.WriteLine("开始---");
            IRQLog.AppLog = new IRQLog();
            IRQLog.AppLog.Start("output.csv");
            UdpLibConfig.HandshakeDelay = 2000;
            UdpLibConfig.HandshakeRetry = 10;

            ArrayPool<byte> BytePool;
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

            List<ClientState> ls = new List<ClientState>();
            for(int i = id; i <= c + id; i++) {
                Thread thd = new Thread((_) => {
                    ClientState cs = new ClientState();
                    cs.Start(host, 10001, (uint)((int)_), (int)_);
                });
                thd.Start(i);
            }

            Stopwatch sw = Stopwatch.StartNew();
            while(true) {
                Console.WriteLine(ClientState.m_total / sw.Elapsed.TotalSeconds);
                System.Threading.Thread.Sleep(2000);
            }
            Console.ReadLine();
        }

    }

    class ClientState {
        KCPClient client = null;
        public void Start(string host, UInt16 port, uint index, int key) {

            client = new KCPClient();
            client.Event += client_Event;
            client.Connect(host, port, index, key);

            while(true) {
                client.Update();
                System.Threading.Thread.Sleep(30);
            }

        }
        public void Update() {
            client.Update();
        }

        void client_Event(UdpClientEvents arg1, byte[] arg2) {
            if(arg1 == UdpClientEvents.Recv) {
                RecvData(arg2);
            }
            else if(arg1 == UdpClientEvents.ConnectFail) {
                Console.WriteLine("连接失败");
            }
            else if(arg1 == UdpClientEvents.ConnectTimeout) {
                Console.WriteLine("连接超时");
            }
            else if(arg1 == UdpClientEvents.Connected) {
                client.Send("data:" + m_d.ToString());
            }

        }

        int m_d = 0;
        public static int m_total = 0;
        private void RecvData(byte[] buf) {

            Interlocked.Increment(ref m_total);
            string s = System.Text.Encoding.UTF8.GetString(buf);
            //Console.WriteLine("Recv From Server:" + s);
            m_d++;
            client.Send("data:" + m_d.ToString());
        }
    }
}
