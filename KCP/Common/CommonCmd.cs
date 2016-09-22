using System;
namespace KCP.Common {

    public enum UdpCommonCmd : byte {
        None = 0,
        Handshark = 1,
        Bye = 2
    }
}