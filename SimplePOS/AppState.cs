using DataMeshGroup.Fusion.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimplePOS
{
    public class AppState
    {
        public AppState()
        {
            PaymentInProgress = false;
            MessageHeader = null;
        }

        public bool PaymentInProgress { get; set; }

        public MessageHeader MessageHeader { get; set; }
    }
}
