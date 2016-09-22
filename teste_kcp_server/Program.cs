using System;
using System.Collections.Generic;
using System.Text;
using KCP.Server;
using KCP.Common;

class Program {
    static void Main(string[] args) {

        Console.WriteLine("输入ip:");
        string s = Console.ReadLine();
        if(s == "") {
            s = "127.0.0.1";
        }
        Console.WriteLine("异步发送模式？(1:异步 其他:同步)");
        if(Console.ReadLine() == "1") {
            UdpLibConfig.ServerSendAsync = true;
        }
        Console.WriteLine("开始");
        IRQLog.AppLog = new IRQLog();
        IRQLog.AppLog.Start("output.csv");
        KCPServer server = new KCPServer(s, 10001);
        server.NewClientSession += server_NewClientSession;
        server.CloseClientSession += server_CloseClientSession;
        server.RecvData += server_RecvData;
        for(int i = 1; i < 10000; i++) {
            server.AddClientKey((uint)i, i);
        }
        server.Start();
        while(true) {
            server.Update();
        }
    }

    static void server_RecvData(ClientSession session, byte[] data, int offset, int size) {
        //byte cmd = data[offset];
        //offset++;

        string s = System.Text.Encoding.UTF8.GetString(data, offset + 4, size - 4);
        Console.WriteLine("Recv From:" + session.NetIndex.ToString() + " " + session.EndPoint.ToString() + " data:" + s);
        session.Send(s);

    }

    static void server_NewClientSession(ClientSession session, byte[] data, int offset, int size) {
        //int d = BitConverter.ToInt32(data, offset);
        Console.WriteLine("New Client:" + session.NetIndex.ToString() + " " + session.EndPoint.ToString());// + " data:" + d.ToString());
        //session.Send(new byte[1]);
    }

    static void server_CloseClientSession(ClientSession session) {
        Console.WriteLine("Close Client:" + session.NetIndex.ToString());
        IRQLog.AppLog.Log2("Close Client:" + session.NetIndex.ToString() + " " + session.EndPoint.ToString());
    }
}

