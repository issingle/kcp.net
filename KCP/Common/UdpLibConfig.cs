using System;
namespace KCP.Common {

    public enum UdpLibLogLevel {
        None = 0,
        Level1 = 1,
        Level2 = 2
    }
    public static class UdpLibConfig {
        /// <summary>
        /// 多久没有收到数据则认为是死链,默认10分钟
        /// </summary>
        public static TimeSpan MaxTimeNoData = new TimeSpan(0, 1, 0);

        /// <summary>
        /// 每隔多少毫秒发送一次握手请求
        /// </summary>
        public static int HandshakeDelay = 1000;

        /// <summary>
        /// 握手重试，默认5次
        /// </summary>
        public static int HandshakeRetry = 5;

        public static int DebugLevel = (int)UdpLibLogLevel.Level2;

        public static byte[] HandshakeHeadData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        public static int HandshakeDataSize = 16;

        /// <summary>
        /// 服务端发送udp数据时，是否使用异步发送
        /// </summary>
        public static bool ServerSendAsync = false;
        /// <summary>
        /// 是否使用内存池
        /// </summary>
        public static bool UseBytePool = true;
    }
}