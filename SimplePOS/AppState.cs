using DataMeshGroup.Fusion.Model;

namespace SimplePOS
{
    public class AppState
    {
        public AppState()
        {
            TransactionInProgress = false;
            MessageHeader = null;
        }

        public bool TransactionInProgress { get; set; }

        public int SelectedTerminalIndex { get; set; }

        public MessageHeader MessageHeader { get; set; }
    }
}
