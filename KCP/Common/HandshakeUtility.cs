using System;
namespace KCP.Common {

    internal static class HandshakeUtility {
        /// <summary>
        /// 是否是正确的握手响应
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="size"></param>
        /// <param name="index"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        internal static bool IsHandshakeDataRight(byte[] buffer, int offset, int size, out uint index, out uint key) {
            index = 0;
            key = 0;
            if(size < UdpLibConfig.HandshakeDataSize) {
                return false;
            }
            for(int i = 0; i < UdpLibConfig.HandshakeHeadData.Length; i++) {
                if(buffer[offset + i] != UdpLibConfig.HandshakeHeadData[i]) {
                    return false;
                }
            }

            KCPLib.ikcp_decode32u(buffer, offset + UdpLibConfig.HandshakeHeadData.Length, ref index);
            KCPLib.ikcp_decode32u(buffer, offset + UdpLibConfig.HandshakeHeadData.Length + 4, ref key);
            return true;
        }
    }

}