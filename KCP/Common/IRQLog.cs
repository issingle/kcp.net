using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KCP.Common {
    public class IRQLog {
        public static IRQLog AppLog;

        public IRQLog() {

        }
        TextWriter m_sw;
        public void Start(string file) {
            m_sw = StreamWriter.Synchronized(new StreamWriter(file, false, Encoding.UTF8));
        }
        public void Log(string msg) {
            //m_sw.WriteLine(DateTime.Now.ToString("O") + "," + msg);
            //m_sw.Flush();
        }
        public void Log2(string msg) {
            m_sw.WriteLine(DateTime.Now.ToString("O") + "," + msg);
            m_sw.Flush();
        }
    }
}
