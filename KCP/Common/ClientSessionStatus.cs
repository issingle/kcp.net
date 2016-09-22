using System;
namespace KCP.Common {

    public enum ClientSessionStatus : byte {
        InConnect = 0,
        Connected = 1,
        ConnectFail = 2,
    }

}