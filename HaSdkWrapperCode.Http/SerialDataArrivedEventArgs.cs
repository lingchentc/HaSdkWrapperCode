using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HaSdkWrapper.Http
{
    public class SerialDataArrivedEventArgs : EventArgs
    {
        public byte[] Data { get; internal set; }
    }
}
