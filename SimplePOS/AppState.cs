using DataMeshGroup.Fusion.Model;

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

        public int SelectedTerminalIndex { get; set; }

        public MessageHeader MessageHeader { get; set; }
    }
}
