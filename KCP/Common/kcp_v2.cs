using System;
using System.Collections.Generic;
using System.IO;

using System.Text;

#if NET_20
    public delegate void Action<U,V>(U u,V v);
#endif
namespace KCP.Common {
    /// <summary>
    /// 支持内存池，发送/接收队列和缓存使用链表代替v1中的数组。
    /// </summary>
    public class KCPLib : IDisposable {
        public const int IKCP_RTO_NDL = 30;  // no delay min rto
        public const int IKCP_RTO_MIN = 100; // normal min rto
        public const int IKCP_RTO_DEF = 200;
        public const int IKCP_RTO_MAX = 60000;
        public const int IKCP_CMD_PUSH = 81; // cmd: push data
        public const int IKCP_CMD_ACK = 82; // cmd: ack
        public const int IKCP_CMD_WASK = 83; // cmd: window probe (ask)
        public const int IKCP_CMD_WINS = 84; // cmd: window size (tell)
        public const int IKCP_ASK_SEND = 1;  // need to send IKCP_CMD_WASK
        public const int IKCP_ASK_TELL = 2;  // need to send IKCP_CMD_WINS
        public const int IKCP_WND_SND = 32;
        public const int IKCP_WND_RCV = 32;
        //鉴于Internet上的标准MTU值为576字节,所以我建议在进行Internet的UDP编程时.
        //最好将UDP的数据长度控件在548字节(576-8-20)以内
        public const int IKCP_MTU_DEF = 512;//默认MTU 1400
        public const int IKCP_ACK_FAST = 3;
        public const int IKCP_INTERVAL = 100;
        public const int IKCP_OVERHEAD = 24;
        public const int IKCP_DEADLINK = 20;//原来是10
        public const int IKCP_THRESH_INIT = 2;
        public const int IKCP_THRESH_MIN = 2;
        public const int IKCP_PROBE_INIT = 7000;   // 7 secs to probe window size
        public const int IKCP_PROBE_LIMIT = 120000; // up to 120 secs to probe window

        #region 内存申请和释放
        public static Func<int, byte[]> BufferAlloc;
        public static Action<byte[]> BufferFree;

        private Stack<Segment> m_SegPool = new Stack<Segment>();
        private Segment PopSegment(int size) {
            if(m_SegPool.Count == 0) {
                for(int i = 0; i < 10; i++) {
                    m_SegPool.Push(new Segment());
                }
            }
            var ret = m_SegPool.Pop();
            if(BufferAlloc != null) {
                var buf = BufferAlloc(size);
                ret.data = new ArraySegment<byte>(buf, 0, size);
            }
            else {
                ret.data = new ArraySegment<byte>(new byte[size], 0, size);
            }
            return ret;
        }
        private void PushSegment(Segment seg) {
            if(BufferFree != null) {
                BufferFree(seg.data.Array);
            }

            m_SegPool.Push(seg);
        }
        #endregion

        // encode 8 bits unsigned int
        public static int ikcp_encode8u(byte[] p, int offset, byte c) {
            p[0 + offset] = c;
            return 1;
        }

        // decode 8 bits unsigned int
        public static int ikcp_decode8u(byte[] p, int offset, ref byte c) {
            c = p[0 + offset];
            return 1;
        }

        /* encode 16 bits unsigned int (lsb) */
        public static int ikcp_encode16u(byte[] p, int offset, UInt16 w) {
            p[0 + offset] = (byte)(w >> 0);
            p[1 + offset] = (byte)(w >> 8);
            return 2;
        }

        /* decode 16 bits unsigned int (lsb) */
        public static int ikcp_decode16u(byte[] p, int offset, ref UInt16 c) {
            UInt16 result = 0;
            result |= (UInt16)p[0 + offset];
            result |= (UInt16)(p[1 + offset] << 8);
            c = result;
            return 2;
        }

        /* encode 32 bits unsigned int (lsb) */
        public static int ikcp_encode32u(byte[] p, int offset, UInt32 l) {
            p[0 + offset] = (byte)(l >> 0);
            p[1 + offset] = (byte)(l >> 8);
            p[2 + offset] = (byte)(l >> 16);
            p[3 + offset] = (byte)(l >> 24);
            return 4;
        }

        /* decode 32 bits unsigned int (lsb) */
        public static int ikcp_decode32u(byte[] p, int offset, ref UInt32 c) {
            UInt32 result = 0;
            result |= (UInt32)p[0 + offset];
            result |= (UInt32)(p[1 + offset] << 8);
            result |= (UInt32)(p[2 + offset] << 16);
            result |= (UInt32)(p[3 + offset] << 24);
            c = result;
            return 4;
        }

        static UInt32 _imin_(UInt32 a, UInt32 b) {
            return a <= b ? a : b;
        }

        static UInt32 _imax_(UInt32 a, UInt32 b) {
            return a >= b ? a : b;
        }

        static UInt32 _ibound_(UInt32 lower, UInt32 middle, UInt32 upper) {
            return _imin_(_imax_(lower, middle), upper);
        }

        static Int32 _itimediff(UInt32 later, UInt32 earlier) {
            return ((Int32)(later - earlier));
        }

        // KCP Segment Definition
        internal class Segment : IDisposable {
            internal UInt32 conv = 0;
            internal UInt32 cmd = 0;
            internal UInt32 frg = 0;
            internal UInt32 wnd = 0;
            internal UInt32 ts = 0;
            internal UInt32 sn = 0;
            internal UInt32 una = 0;
            internal UInt32 resendts = 0;
            internal UInt32 rto = 0;
            internal UInt32 fastack = 0;
            internal UInt32 xmit = 0;
            //internal byte[] data;
            internal ArraySegment<byte> data;
            internal Segment(int size) {
                if(KCPLib.BufferAlloc != null) {
                    this.data = new ArraySegment<byte>(KCPLib.BufferAlloc(size), 0, size);
                }
                else {
                    this.data = new ArraySegment<byte>(new byte[size], 0, size);
                }
            }
            internal Segment() {
            }
            // encode a segment into buffer
            internal int encode(byte[] ptr, int offset) {

                var offset_ = offset;

                offset += ikcp_encode32u(ptr, offset, conv);
                offset += ikcp_encode8u(ptr, offset, (byte)cmd);
                offset += ikcp_encode8u(ptr, offset, (byte)frg);
                offset += ikcp_encode16u(ptr, offset, (UInt16)wnd);
                offset += ikcp_encode32u(ptr, offset, ts);
                offset += ikcp_encode32u(ptr, offset, sn);
                offset += ikcp_encode32u(ptr, offset, una);
                //offset += ikcp_encode32u(ptr, offset, (UInt32)data.Length);
                offset += ikcp_encode32u(ptr, offset, (UInt32)data.Count);

                return offset - offset_;
            }

            private bool m_Disposed = false;
            public void Dispose() {
                if(!m_Disposed) {
                    //m_Disposed = true;
                    //if(KCPLib.BufferFree != null) {
                    //    KCPLib.BufferFree(this.data.Array);
                    //}
                    //KCPLib.
                    GC.SuppressFinalize(this);
                }
            }
            ~Segment() {
                Dispose();
            }
        }

        // kcp members.
        UInt32 conv; UInt32 mtu; UInt32 mss; UInt32 state;
        UInt32 snd_una; UInt32 snd_nxt; UInt32 rcv_nxt;
        UInt32 ts_recent; UInt32 ts_lastack; UInt32 ssthresh;
        UInt32 rx_rttval; UInt32 rx_srtt; UInt32 rx_rto; UInt32 rx_minrto;
        UInt32 snd_wnd; UInt32 rcv_wnd; UInt32 rmt_wnd; UInt32 cwnd; UInt32 probe;
        UInt32 current; UInt32 interval; UInt32 ts_flush; UInt32 xmit;
        UInt32 nodelay; UInt32 updated;
        UInt32 ts_probe; UInt32 probe_wait;
        UInt32 dead_link; UInt32 incr;

        //Segment[] snd_queue = new Segment[0];
        //Segment[] rcv_queue = new Segment[0];
        //Segment[] snd_buf = new Segment[0];
        //Segment[] rcv_buf = new Segment[0];
        LinkedList<Segment> snd_queue = new LinkedList<Segment>();
        LinkedList<Segment> rcv_queue = new LinkedList<Segment>();
        LinkedList<Segment> snd_buf = new LinkedList<Segment>();
        LinkedList<Segment> rcv_buf = new LinkedList<Segment>();


        UInt32[] acklist = new UInt32[0];

        byte[] buffer;
        Int32 fastresend;
        Int32 nocwnd;
        Int32 logmask;
        // buffer, size
        Action<byte[], int> output;

        // create a new kcp control object, 'conv' must equal in two endpoint
        // from the same connection.
        /// <summary>
        /// 创建KCP
        /// </summary>
        /// <param name="conv_">连接索引号</param>
        /// <param name="output_">kcp有发送数据的需求时，将主动调用</param>
        public KCPLib(UInt32 conv_, Action<byte[], int> output_) {
            conv = conv_;
            snd_wnd = IKCP_WND_SND;
            rcv_wnd = IKCP_WND_RCV;
            rmt_wnd = IKCP_WND_RCV;
            mtu = IKCP_MTU_DEF;
            mss = mtu - IKCP_OVERHEAD;

            rx_rto = IKCP_RTO_DEF;
            rx_minrto = IKCP_RTO_MIN;
            interval = IKCP_INTERVAL;
            ts_flush = IKCP_INTERVAL;
            ssthresh = IKCP_THRESH_INIT;
            dead_link = IKCP_DEADLINK;
            if(BufferAlloc != null) {
                buffer = BufferAlloc((int)((mtu + IKCP_OVERHEAD) * 3));
            }
            else {
                buffer = new byte[(mtu + IKCP_OVERHEAD) * 3];
            }
            output = output_;
        }

        // check the size of next message in the recv queue
        public int PeekSize() {

            //if(0 == rcv_queue.Length)
            if(0 == rcv_queue.Count)
                return -1;

            //var seq = rcv_queue[0];
            var seq = rcv_queue.First.Value;

            if(0 == seq.frg) {
                //return seq.data.Length;
                return seq.data.Count;
            }


            //if(rcv_queue.Length < seq.frg + 1)
            if(rcv_queue.Count < seq.frg + 1)
                return -1;

            int length = 0;

            foreach(var item in rcv_queue) {
                //length += item.data.Length;
                length += item.data.Count;
                if(0 == item.frg)
                    break;
            }

            return length;
        }

        // user/upper level recv: returns size, returns below zero for EAGAIN
        public int Recv(byte[] buffer) {

            //if(0 == rcv_queue.Length)
            if(0 == rcv_queue.Count)
                return -1;

            var peekSize = PeekSize();
            if(0 > peekSize)
                return -2;

            if(peekSize > buffer.Length)
                return -3;

            var fast_recover = false;
            //if(rcv_queue.Length >= rcv_wnd)
            if(rcv_queue.Count >= rcv_wnd)
                fast_recover = true;

            // merge fragment.
            var count = 0;
            var n = 0;
            var node = rcv_queue.First;
            while(node != null) {

                //Array.Copy(seg.data, 0, buffer, n, seg.data.Length);
                //n += seg.data.Length;
                var seg = node.Value;
                Array.Copy(seg.data.Array, seg.data.Offset, buffer, n, seg.data.Count);
                n += seg.data.Count;
                uint frg = seg.frg;
                var next = node.Next;
                rcv_queue.Remove(node);
                //seg.Dispose();
                PushSegment(seg);
                node = next;
                if(0 == frg) {
                    break;
                }
                else {

                }
            }

            //if(0 < count) {
            //移除count之后的
            //rcv_queue = slice<Segment>(rcv_queue, count, rcv_queue.Length);

            //}

            // move available data from rcv_buf -> rcv_queue
            count = 0;
            //foreach(var seg in rcv_buf) {
            //    if(seg.sn == rcv_nxt && rcv_queue.Length < rcv_wnd) {
            //        rcv_queue = append<Segment>(rcv_queue, seg);

            //        rcv_nxt++;
            //        count++;
            //    }
            //    else {
            //        break;
            //    }
            //}
            node = rcv_buf.First;
            //for(var f = rcv_buf.First; f.Next != null; f = f.Next) {
            while(node != null) {

                Segment seg = node.Value;
                if(seg.sn == rcv_nxt && rcv_queue.Count < rcv_wnd) {
                    var tmp = node.Next;
                    rcv_buf.Remove(node);
                    rcv_queue.AddLast(node);
                    node = tmp;
                    rcv_nxt++;
                    //count++;
                }
                else {
                    break;
                }
            }


            //if(0 < count) {
            //rcv_buf = slice<Segment>(rcv_buf, count, rcv_buf.Length);
            //}

            // fast recover
            // if(rcv_queue.Length < rcv_wnd && fast_recover) {
            if(rcv_queue.Count < rcv_wnd && fast_recover) {
                // ready to send back IKCP_CMD_WINS in ikcp_flush
                // tell remote my window size
                probe |= IKCP_ASK_TELL;
            }

            return n;
        }
        public int Send(byte[] buffer, int index, int bufsize) {
            if(0 == bufsize)
                return -1;

            var count = 0;

            if(bufsize < mss)
                count = 1;
            else
                count = (int)(bufsize + mss - 1) / (int)mss;

            if(255 < count)
                return -2;

            if(0 == count)
                count = 1;

            var offset = index;

            for(var i = 0; i < count; i++) {
                var size = 0;
                if(bufsize > mss)
                    size = (int)mss;
                else
                    size = bufsize;

                var seg = PopSegment(size);// new Segment(size);
                Array.Copy(buffer, offset, seg.data.Array, seg.data.Offset, size);
                offset += size;
                seg.frg = (UInt32)(count - i - 1);
                snd_queue.AddLast(seg);
                //snd_queue = append<Segment>(snd_queue, seg);
            }

            return 0;
        }
        // user/upper level send, returns below zero for error
        public int Send(byte[] buffer) {
            return Send(buffer, 0, buffer.Length);

        }

        // update ack.
        void update_ack(Int32 rtt) {
            if(0 == rx_srtt) {
                rx_srtt = (UInt32)rtt;
                rx_rttval = (UInt32)rtt / 2;
            }
            else {
                Int32 delta = (Int32)((UInt32)rtt - rx_srtt);
                if(0 > delta)
                    delta = -delta;

                rx_rttval = (3 * rx_rttval + (uint)delta) / 4;
                rx_srtt = (UInt32)((7 * rx_srtt + rtt) / 8);
                if(rx_srtt < 1)
                    rx_srtt = 1;
            }

            var rto = (int)(rx_srtt + _imax_(1, 4 * rx_rttval));
            rx_rto = _ibound_(rx_minrto, (UInt32)rto, IKCP_RTO_MAX);
        }

        void shrink_buf() {
            //if(snd_buf.Length > 0)
            //    snd_una = snd_buf[0].sn;
            //else
            //    snd_una = snd_nxt;

            if(snd_buf.Count > 0)
                snd_una = snd_buf.First.Value.sn;
            else
                snd_una = snd_nxt;
        }

        void parse_ack(UInt32 sn) {

            if(_itimediff(sn, snd_una) < 0 || _itimediff(sn, snd_nxt) >= 0)
                return;

            //var index = 0;
            //foreach(var seg in snd_buf) {
            //    if(sn == seg.sn) {
            //        //把snd_buf中sn==seg.sn的元素移除掉
            //        snd_buf = append<Segment>(slice<Segment>(snd_buf, 0, index), slice<Segment>(snd_buf, index + 1, snd_buf.Length));
            //        break;
            //    }
            //    else {
            //        seg.fastack++;
            //    }

            //    index++;
            //}

            var node = snd_buf.First;
            while(node != null) {
                var seg = node.Value;
                var tmp = node.Next;
                if(sn == seg.sn) {
                    snd_buf.Remove(node);
                    //seg.Dispose();
                    PushSegment(seg);
                    break;
                }
                else {
                    seg.fastack++;
                }
                node = tmp;
            }
        }

        void parse_una(UInt32 una) {
            //var count = 0;
            //foreach(var seg in snd_buf) {
            //    if(_itimediff(una, seg.sn) > 0)
            //        count++;
            //    else
            //        break;
            //}



            //if(0 < count) {
            //    //count之前的移除掉
            //    snd_buf = slice<Segment>(snd_buf, count, snd_buf.Length);
            //}

            var node = snd_buf.First;
            while(node != null) {
                var seg = node.Value;
                var tmp = node.Next;
                if(_itimediff(una, seg.sn) > 0) {
                    snd_buf.Remove(node);
                    //seg.Dispose();
                    PushSegment(seg);
                }
                else {
                    break;
                }
                node = tmp;
            }

        }

        uint m_ackCount, m_ackBlock;
        byte[] m_ackList;
        unsafe void ack_push(UInt32 sn, UInt32 ts) {
            //acklist = append<UInt32>(acklist, new UInt32[2] { sn, ts });
            uint newsize = this.m_ackCount + 1;
            if(newsize > this.m_ackBlock) {
                uint newblock;
                for(newblock = 8; newblock < newsize; newblock <<= 1)
                    ;
                var tmp_acklist = BufferAlloc((int)(newblock * sizeof(uint) * 2));
                if(this.m_ackList != null) {
                    uint x;

                    fixed (byte* ptmp = tmp_acklist) {
                        fixed (byte* pcur = m_ackList) {

                            uint* ptrtmp = (uint*)ptmp;
                            uint* ptr_cur = (uint*)pcur;

                            for(x = 0; x < this.m_ackCount; x++) {
                                ptrtmp[x * 2 + 0] = ptr_cur[x * 2 + 0];
                                ptrtmp[x * 2 + 1] = ptr_cur[x * 2 + 1];
                            }
                        }
                    }
                    BufferFree(this.m_ackList);
                }

                this.m_ackList = tmp_acklist;
                this.m_ackBlock = newblock;

            }

            fixed (byte* p = this.m_ackList) {
                uint* pint = (uint*)p;
                uint offset = this.m_ackCount * 2;
                pint[offset + 0] = sn;
                pint[offset + 1] = ts;
            }
            this.m_ackCount++;
        }

        unsafe void ack_get(int p, ref UInt32 sn, ref UInt32 ts) {
            //sn = acklist[p * 2 + 0];
            //ts = acklist[p * 2 + 1];
            fixed (byte* pbyte = this.m_ackList) {
                uint* ptr = (uint*)pbyte;
                sn = ptr[p * 2 + 0];
                ts = ptr[p * 2 + 1];
            }
        }

        void parse_data(Segment newseg) {
            var sn = newseg.sn;
            if(_itimediff(sn, rcv_nxt + rcv_wnd) >= 0 || _itimediff(sn, rcv_nxt) < 0) {
                //newseg.Dispose();
                PushSegment(newseg);
                return;
            }


            //var n = rcv_buf.Length - 1;
            var n = rcv_buf.Count - 1;
            var after_idx = -1;
            var repeat = false;

            //for(var i = n; i >= 0; i--) {
            //    var seg = rcv_buf[i];
            //    if(seg.sn == sn) {
            //        repeat = true;
            //        break;
            //    }

            //    if(_itimediff(sn, seg.sn) > 0) {
            //        after_idx = i;
            //        break;
            //    }
            //}

            LinkedListNode<Segment> p, prev;
            for(p = rcv_buf.Last; p != rcv_buf.First; p = prev) {
                var seg = p.Value;
                prev = p.Previous;
                if(seg.sn == sn) {
                    repeat = true;
                    break;
                }

                if(_itimediff(sn, seg.sn) > 0) {
                    break;
                }
            }

            if(repeat == false) {
                if(p == null) {
                    rcv_buf.AddFirst(newseg);
                }
                else {
                    rcv_buf.AddBefore(p, newseg);
                }
            }
            else {
                //newseg.Dispose();
                PushSegment(newseg);
                // rcv_buf = append<Segment>(slice<Segment>(rcv_buf, 0, after_idx + 1), append<Segment>(new Segment[1] { newseg }, slice<Segment>(rcv_buf, after_idx + 1, rcv_buf.Length)));
            }


            // move available data from rcv_buf -> rcv_queue
            //var count = 0;
            //foreach(var seg in rcv_buf) {
            //    if(seg.sn == rcv_nxt && rcv_queue.Length < rcv_wnd) {
            //        rcv_queue = append<Segment>(rcv_queue, seg);
            //        rcv_nxt++;
            //        count++;
            //    }
            //    else {
            //        break;
            //    }
            //}

            //if(0 < count) {
            //    rcv_buf = slice<Segment>(rcv_buf, count, rcv_buf.Length);
            //}


            var node = rcv_buf.First;
            while(node != null) {
                var seg = node.Value;
                var tmp = node.Next;
                if(seg.sn == rcv_nxt && rcv_queue.Count < rcv_wnd) {
                    rcv_buf.Remove(node);
                    rcv_queue.AddLast(node);
                    rcv_nxt++;
                }
                else {
                    break;
                }
                node = tmp;
            }
        }

        // when you received a low level packet (eg. UDP packet), call it
        public int Input(byte[] data, int dataOffset, int dataSize) {

            var s_una = snd_una;
            //if(data.Length < IKCP_OVERHEAD)
            if(dataSize < IKCP_OVERHEAD) {
                return -1;
            }

            //int offset = 0;
            int offset = dataOffset;
            while(true) {
                UInt32 ts = 0;
                UInt32 sn = 0;
                UInt32 length = 0;
                UInt32 una = 0;
                UInt32 conv_ = 0;

                UInt16 wnd = 0;

                byte cmd = 0;
                byte frg = 0;

                if(dataSize - offset < IKCP_OVERHEAD) {
                    break;
                }

                offset += ikcp_decode32u(data, offset, ref conv_);

                if(conv != conv_) {
                    return -1;
                }

                offset += ikcp_decode8u(data, offset, ref cmd);
                offset += ikcp_decode8u(data, offset, ref frg);
                offset += ikcp_decode16u(data, offset, ref wnd);
                offset += ikcp_decode32u(data, offset, ref ts);
                offset += ikcp_decode32u(data, offset, ref sn);
                offset += ikcp_decode32u(data, offset, ref una);
                offset += ikcp_decode32u(data, offset, ref length);

                //if(data.Length - offset < length)
                if(dataSize - offset < length)
                    return -2;

                switch(cmd) {
                    case IKCP_CMD_PUSH:
                    case IKCP_CMD_ACK:
                    case IKCP_CMD_WASK:
                    case IKCP_CMD_WINS:
                        break;
                    default:
                        return -3;
                }

                rmt_wnd = (UInt32)wnd;
                parse_una(una);
                shrink_buf();

                if(IKCP_CMD_ACK == cmd) {
                    if(_itimediff(current, ts) >= 0) {
                        update_ack(_itimediff(current, ts));
                    }
                    parse_ack(sn);
                    shrink_buf();
                }
                else if(IKCP_CMD_PUSH == cmd) {
                    if(_itimediff(sn, rcv_nxt + rcv_wnd) < 0) {
                        ack_push(sn, ts);
                        if(_itimediff(sn, rcv_nxt) >= 0) {
                            var seg = PopSegment((int)length);// new Segment((int)length);
                            seg.conv = conv_;
                            seg.cmd = (UInt32)cmd;
                            seg.frg = (UInt32)frg;
                            seg.wnd = (UInt32)wnd;
                            seg.ts = ts;
                            seg.sn = sn;
                            seg.una = una;

                            if(length > 0) {
                                //Array.Copy(data, offset, seg.data, 0, length);
                                Array.Copy(data, offset, seg.data.Array, seg.data.Offset, length);
                            }


                            parse_data(seg);
                        }
                    }
                }
                else if(IKCP_CMD_WASK == cmd) {
                    // ready to send back IKCP_CMD_WINS in Ikcp_flush
                    // tell remote my window size
                    probe |= IKCP_ASK_TELL;
                }
                else if(IKCP_CMD_WINS == cmd) {
                    // do nothing
                }
                else {
                    return -3;
                }

                offset += (int)length;
            }

            if(_itimediff(snd_una, s_una) > 0) {
                if(cwnd < rmt_wnd) {
                    var mss_ = mss;
                    if(cwnd < ssthresh) {
                        cwnd++;
                        incr += mss_;
                    }
                    else {
                        if(incr < mss_) {
                            incr = mss_;
                        }
                        incr += (mss_ * mss_) / incr + (mss_ / 16);
                        if((cwnd + 1) * mss_ <= incr)
                            cwnd++;
                    }
                    if(cwnd > rmt_wnd) {
                        cwnd = rmt_wnd;
                        incr = rmt_wnd * mss_;
                    }
                }
            }

            return 0;
        }

        Int32 wnd_unused() {
            if(rcv_queue.Count < rcv_wnd)
                return (Int32)(int)rcv_wnd - rcv_queue.Count;
            return 0;
        }

        // flush pending data
        void flush() {
            var current_ = current;
            var buffer_ = buffer;
            var change = 0;
            var lost = 0;

            if(0 == updated)
                return;

            var seg = PopSegment(0);// new Segment(0);
            seg.conv = conv;
            seg.cmd = IKCP_CMD_ACK;
            seg.wnd = (UInt32)wnd_unused();
            seg.una = rcv_nxt;

            // flush acknowledges
            //var count = acklist.Length / 2;
            var count = this.m_ackCount;// acklist.Length / 2;
            var offset = 0;
            for(var i = 0; i < count; i++) {
                if(offset + IKCP_OVERHEAD > mtu) {
                    output(buffer, offset);
                    //Array.Clear(buffer, 0, offset);
                    offset = 0;
                }
                ack_get(i, ref seg.sn, ref seg.ts);
                offset += seg.encode(buffer, offset);
            }
            this.m_ackCount = 0;
            //acklist = new UInt32[0];

            // probe window size (if remote window size equals zero)
            if(0 == rmt_wnd) {
                if(0 == probe_wait) {
                    probe_wait = IKCP_PROBE_INIT;
                    ts_probe = current + probe_wait;
                }
                else {
                    if(_itimediff(current, ts_probe) >= 0) {
                        if(probe_wait < IKCP_PROBE_INIT)
                            probe_wait = IKCP_PROBE_INIT;
                        probe_wait += probe_wait / 2;
                        if(probe_wait > IKCP_PROBE_LIMIT)
                            probe_wait = IKCP_PROBE_LIMIT;
                        ts_probe = current + probe_wait;
                        probe |= IKCP_ASK_SEND;
                    }
                }
            }
            else {
                ts_probe = 0;
                probe_wait = 0;
            }

            // flush window probing commands
            if((probe & IKCP_ASK_SEND) != 0) {
                seg.cmd = IKCP_CMD_WASK;
                if(offset + IKCP_OVERHEAD > (int)mtu) {
                    output(buffer, offset);
                    //Array.Clear(buffer, 0, offset);
                    offset = 0;
                }
                offset += seg.encode(buffer, offset);
            }

            probe = 0;

            // calculate window size
            var cwnd_ = _imin_(snd_wnd, rmt_wnd);
            if(0 == nocwnd)
                cwnd_ = _imin_(cwnd, cwnd_);

            //count = 0;
            //将发送队列（snd_queue)中的数据添加到发送缓存(snd_bu)
            // move data from snd_queue to snd_buf
            var node = snd_queue.First;
            while(node != null) {
                var newseg = node.Value;
                var next = node.Next;

                if(_itimediff(snd_nxt, snd_una + cwnd_) >= 0)
                    break;
                snd_queue.Remove(node);
                newseg.conv = conv;
                newseg.cmd = IKCP_CMD_PUSH;
                newseg.wnd = seg.wnd;
                newseg.ts = current_;
                newseg.sn = snd_nxt;
                newseg.una = rcv_nxt;
                newseg.resendts = current_;
                newseg.rto = rx_rto;
                newseg.fastack = 0;
                newseg.xmit = 0;
                snd_buf.AddLast(node);// = append<Segment>(snd_buf, newseg);
                node = next;
                snd_nxt++;
                //count++;
            }

            //if(0 < count) {
            //从snd_queue移除已添加到发送缓存（snd_buf)的元素
            //    snd_queue = slice<Segment>(snd_queue, count, snd_queue.Length);
            //}

            // calculate resent
            var resent = (UInt32)fastresend;
            if(fastresend <= 0)
                resent = 0xffffffff;
            var rtomin = rx_rto >> 3;
            if(nodelay != 0)
                rtomin = 0;

            // flush data segments
            foreach(var segment in snd_buf) {
                var needsend = false;
                var debug = _itimediff(current_, segment.resendts);
                if(0 == segment.xmit) {
                    needsend = true;
                    segment.xmit++;
                    segment.rto = rx_rto;
                    segment.resendts = current_ + segment.rto + rtomin;
                }
                else if(_itimediff(current_, segment.resendts) >= 0) {
                    needsend = true;
                    segment.xmit++;
                    xmit++;
                    if(0 == nodelay)
                        segment.rto += rx_rto;
                    else
                        segment.rto += rx_rto / 2;
                    segment.resendts = current_ + segment.rto;
                    lost = 1;
                }
                else if(segment.fastack >= resent) {
                    needsend = true;
                    segment.xmit++;
                    segment.fastack = 0;
                    segment.resendts = current_ + segment.rto;
                    change++;
                }

                if(needsend) {
                    segment.ts = current_;
                    segment.wnd = seg.wnd;
                    segment.una = rcv_nxt;

                    //var need = IKCP_OVERHEAD + segment.data.Length;
                    var need = IKCP_OVERHEAD + segment.data.Count;
                    if(offset + need >= mtu) {
                        output(buffer, offset);
                        //Array.Clear(buffer, 0, offset);
                        offset = 0;
                    }

                    offset += segment.encode(buffer, offset);
                    //if(segment.data.Length > 0) {
                    //    Array.Copy(segment.data, 0, buffer, offset, segment.data.Length);
                    //    offset += segment.data.Length;
                    //}
                    if(segment.data.Count > 0) {
                        Array.Copy(segment.data.Array, segment.data.Offset, buffer, offset, segment.data.Count);
                        offset += segment.data.Count;
                    }


                    if(segment.xmit >= dead_link) {
                        state = 0;
                    }
                }
            }

            // flash remain segments
            if(offset > 0) {
                output(buffer, offset);
                //Array.Clear(buffer, 0, offset);
                offset = 0;
            }

            // update ssthresh
            if(change != 0) {
                var inflight = snd_nxt - snd_una;
                ssthresh = inflight / 2;
                if(ssthresh < IKCP_THRESH_MIN)
                    ssthresh = IKCP_THRESH_MIN;
                cwnd = ssthresh + resent;
                incr = cwnd * mss;
            }

            if(lost != 0) {
                ssthresh = cwnd / 2;
                if(ssthresh < IKCP_THRESH_MIN)
                    ssthresh = IKCP_THRESH_MIN;
                cwnd = 1;
                incr = mss;
            }

            if(cwnd < 1) {
                cwnd = 1;
                incr = mss;
            }

            PushSegment(seg);
        }

        // update state (call it repeatedly, every 10ms-100ms), or you can ask
        // ikcp_check when to call it again (without ikcp_input/_send calling).
        // 'current' - current timestamp in millisec.
        public void Update(UInt32 current_) {

            current = current_;

            if(0 == updated) {
                updated = 1;
                ts_flush = current;
            }

            var slap = _itimediff(current, ts_flush);

            if(slap >= 10000 || slap < -10000) {
                ts_flush = current;
                slap = 0;
            }

            if(slap >= 0) {
                ts_flush += interval;
                if(_itimediff(current, ts_flush) >= 0)
                    ts_flush = current + interval;
                flush();
            }
        }

        // Determine when should you invoke ikcp_update:
        // returns when you should invoke ikcp_update in millisec, if there
        // is no ikcp_input/_send calling. you can call ikcp_update in that
        // time, instead of call update repeatly.
        // Important to reduce unnacessary ikcp_update invoking. use it to
        // schedule ikcp_update (eg. implementing an epoll-like mechanism,
        // or optimize ikcp_update when handling massive kcp connections)
        public UInt32 Check(UInt32 current_) {

            if(0 == updated)
                return current_;

            var ts_flush_ = ts_flush;
            var tm_flush_ = 0x7fffffff;
            var tm_packet = 0x7fffffff;
            var minimal = 0;

            if(_itimediff(current_, ts_flush_) >= 10000 || _itimediff(current_, ts_flush_) < -10000) {
                ts_flush_ = current_;
            }

            if(_itimediff(current_, ts_flush_) >= 0)
                return current_;

            tm_flush_ = (int)_itimediff(ts_flush_, current_);

            foreach(var seg in snd_buf) {
                var diff = _itimediff(seg.resendts, current_);
                if(diff <= 0)
                    return current_;
                if(diff < tm_packet)
                    tm_packet = (int)diff;
            }

            minimal = (int)tm_packet;
            if(tm_packet >= tm_flush_)
                minimal = (int)tm_flush_;
            if(minimal >= interval)
                minimal = (int)interval;

            return current_ + (UInt32)minimal;
        }

        // change MTU size, default is 1400
        //public int SetMtu(Int32 mtu_) {
        //    if(mtu_ < 50 || mtu_ < (Int32)IKCP_OVERHEAD)
        //        return -1;

        //    var buffer_ = new byte[(mtu_ + IKCP_OVERHEAD) * 3];
        //    if(null == buffer_)
        //        return -2;

        //    mtu = (UInt32)mtu_;
        //    mss = mtu - IKCP_OVERHEAD;
        //    buffer = buffer_;
        //    return 0;
        //}

        public int Interval(Int32 interval_) {
            if(interval_ > 5000) {
                interval_ = 5000;
            }
            else if(interval_ < 10) {
                interval_ = 10;
            }
            interval = (UInt32)interval_;
            return 0;
        }

        // fastest: ikcp_nodelay(kcp, 1, 20, 2, 1)
        // nodelay: 0:disable(default), 1:enable
        // interval: internal update timer interval in millisec, default is 100ms
        // resend: 0:disable fast resend(default), 1:enable fast resend
        // nc: 0:normal congestion control(default), 1:disable congestion control
        public int NoDelay(int nodelay_, int interval_, int resend_, int nc_) {

            if(nodelay_ > 0) {
                nodelay = (UInt32)nodelay_;
                if(nodelay_ != 0)
                    rx_minrto = IKCP_RTO_NDL;
                else
                    rx_minrto = IKCP_RTO_MIN;
            }

            if(interval_ >= 0) {
                if(interval_ > 5000) {
                    interval_ = 5000;
                }
                else if(interval_ < 10) {
                    interval_ = 10;
                }
                interval = (UInt32)interval_;
            }

            if(resend_ >= 0)
                fastresend = resend_;

            if(nc_ >= 0)
                nocwnd = nc_;

            return 0;
        }

        // set maximum window size: sndwnd=32, rcvwnd=32 by default
        public int WndSize(int sndwnd, int rcvwnd) {
            if(sndwnd > 0)
                snd_wnd = (UInt32)sndwnd;

            if(rcvwnd > 0)
                rcv_wnd = (UInt32)rcvwnd;
            return 0;
        }

        // get how many packet is waiting to be sent
        public int WaitSnd() {
            return snd_buf.Count + snd_queue.Count;
        }

        public void Dispose() {

            foreach(var v in snd_buf) {
                PushSegment(v);
            }
            snd_buf.Clear();
            foreach(var v in snd_queue) {
                PushSegment(v);
            }
            snd_queue.Clear();
            foreach(var v in rcv_buf) {
                PushSegment(v);
            }
            rcv_buf.Clear();
            foreach(var v in rcv_queue) {
                PushSegment(v);
            }
            rcv_queue.Clear();

            m_SegPool.Clear();

            if(BufferFree != null) {
                if(m_ackList != null) {
                    BufferFree(this.m_ackList);
                }
                BufferFree(this.buffer);
            }
        }
    }
}