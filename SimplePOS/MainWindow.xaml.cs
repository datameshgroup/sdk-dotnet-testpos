using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DataMeshGroup.Fusion;
using DataMeshGroup.Fusion.Model;
using System.Threading;
using Newtonsoft.Json;

namespace SimplePOS
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private enum LightBoxDialogType { Normal, Success, Error }

        private readonly AppState appState;
        private Settings settings;
        private IFusionClient fusionClient;

        public Task Initialization { get; private set; }

        public Settings Settings
        {
            get => settings;
            set
            {
                if (value != settings)
                {
                    settings = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private PaymentResponse paymentResponse;
        public PaymentResponse PaymentResponse
        {
            get => paymentResponse;
            set
            {
                if (value != paymentResponse)
                {
                    paymentResponse = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public MainWindow()
        {
            DataContext = this;
            InitializeComponent();

            NavigateToMainPage();

            Settings = LoadSettings();
            appState = LoadAppState();

            Initialization = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            await CreateFusionClient();

            if (appState?.PaymentInProgress == true)
            {
                await PerformErrorRecovery();
            }
        }

        private async Task CreateFusionClient()
        {
            // Destroy if client already exists
            if(fusionClient != null)
            { 
                await fusionClient.DisconnectAsync();
                fusionClient.OnLog -= FusionClient_OnLog;
                fusionClient.Dispose();
                fusionClient = null;
            }

            fusionClient = new FusionClient(Settings.UseTestEnvironment)
            {
                SaleID = Settings.SaleID,
                POIID = Settings.POIID,
                KEK = Settings.KEK,
                CustomURL = Settings.CustomNexoURL,
                LogLevel = DataMeshGroup.Fusion.LogLevel.Trace,
                LoginRequest = BuildLoginRequest()
            };
            fusionClient.URL = string.IsNullOrWhiteSpace(Settings.CustomNexoURL) ? fusionClient.URL : UnifyURL.Custom;
            fusionClient.OnLog += FusionClient_OnLog;
        }

        private void FusionClient_OnLog(object sender, DataMeshGroup.Fusion.LogEventArgs e)
        {
            File.AppendAllText("log.txt", $"{e.CreatedDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")} - {e.LogLevel.ToString().PadRight(12, ' ')} - {e.Data} {e.Exception?.Message ?? ""}{Environment.NewLine}");
        }

        #region Settings & AppState

        private Settings LoadSettings()
        {
            if (!File.Exists("settings.json"))
            {
                NavigateToSettingsPage();
                return new Settings()
                {
                    SaleID = "",
                    POIID = "",
                    KEK = "",
                    ProviderIdentification = "Company A",
                    ApplicationName = "POS Retail",
                    SoftwareVersion = "01.00.00",
                    CertificationCode = "98cf9dfc-0db7-4a92-8b8cb66d4d2d7169",
                    OperatorID = null,
                    ShiftNumber = null
                };
            }

            return JsonConvert.DeserializeObject<Settings>(File.ReadAllText("settings.json"));
        }

        private AppState LoadAppState()
        {
            return (!File.Exists("appstate.json")) ? new AppState() : JsonConvert.DeserializeObject<AppState>(File.ReadAllText("appstate.json"));
        }

        private void UpdateAppState(bool paymentInProgress, MessageHeader messageHeader)
        {
            appState.PaymentInProgress = paymentInProgress;
            appState.MessageHeader = messageHeader;

            File.WriteAllText("appstate.json", JsonConvert.SerializeObject(appState));
        }

        #endregion

        #region Navigation
        private void NavigateToSettingsPage()
        {
            GridSettings.Visibility = Visibility.Visible;
            GridMain.Visibility = Visibility.Collapsed;
        }

        private void NavigateToMainPage()
        {
            GridSettings.Visibility = Visibility.Collapsed;
            GridMain.Visibility = Visibility.Visible;
        }
        #endregion

        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            File.WriteAllText("settings.json", System.Text.Json.JsonSerializer.Serialize<Settings>(Settings));
            await CreateFusionClient();
            NavigateToMainPage();
        }

        private void BtnViewSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigateToSettingsPage();
        }

        private async void BtnPayment_Click(object sender, RoutedEventArgs e)
        {
            // Validate input
            PaymentType paymentType;
            string paymentTypeName;
            switch (CboTxnType.SelectedIndex)
            {
                case 0:
                    paymentType = PaymentType.Normal;
                    paymentTypeName = "PURCHASE";
                    break;
                case 1:
                    paymentType = PaymentType.Refund;
                    paymentTypeName = "REFUND";
                    break;
                case 2:
                default:
                    paymentType = PaymentType.CashAdvance;
                    paymentTypeName = "CASH ADVANCE";
                    break;
            }

            if (!decimal.TryParse(TxtPurchaseAmount.Text, out decimal purchaseAmount))
            {
                ShowPaymentDialogFailed(paymentTypeName, "INVALID AMOUNT");
                return;
            }
            // Tip amount can be null
            decimal? tipAmount = decimal.TryParse(TxtTipAmount.Text, out decimal tmpTipAmount) ? tmpTipAmount : null;
            // Cash amount can be null
            decimal? cashoutAmount = decimal.TryParse(TxtCashoutAmount.Text, out decimal tmpCashoutAmount) ? tmpCashoutAmount : null;
            bool requestCardToken = ChkRequestToken.IsChecked ?? false;
            //string cardToken = string.IsNullOrWhiteSpace(TxtCardToken.Text) ? null : TxtCardToken.Text;



            ShowPaymentDialog(paymentTypeName, "PAYMENT IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            // Create basic payment
            var paymentRequest = new PaymentRequest(
                transactionID: Guid.NewGuid().ToString("N"),
                requestedAmount: purchaseAmount,
                saleItems: new List<SaleItem>()
                {
                    new SaleItem()
                    {
                        ItemID = "0",
                        ProductCode = "0",
                        ProductLabel = "Product",
                        UnitPrice = purchaseAmount,
                        ItemAmount = purchaseAmount
                    }
                },
                paymentType: paymentType
                );
            // Add extra fields
            paymentRequest.PaymentTransaction.AmountsReq.CashBackAmount = cashoutAmount;
            paymentRequest.SaleData.TokenRequestedType = requestCardToken ? TokenRequestedType.Customer : null;
            paymentRequest.PaymentTransaction.AmountsReq.TipAmount = tipAmount;


            //if(cardToken != null)
            //{
            //    paymentRequest.PaymentData.PaymentInstrumentData = new PaymentInstrumentData()
            //    {
            //        CardData = new CardData()
            //        {
            //            EntryMode = EntryMode.File,
            //            PaymentToken = new PaymentToken()
            //            {
            //                TokenRequestedType = TokenRequestedType.Customer,
            //                TokenValue = cardToken
            //            }
            //        }
            //    };
            //}



            // Check environment 
            try
            {
                SaleToPOIMessage saleToPoiRequest = await fusionClient.SendAsync(paymentRequest);
                UpdateAppState(true, saleToPoiRequest.MessageHeader);
                bool waitingForResponse = true;
                do
                {
                    switch (await fusionClient.RecvAsync())
                    {
                        case PaymentResponse r:
                            // Validate SaleTransactionID
                            if (r.SaleData?.SaleTransactionID != null && !r.SaleData.SaleTransactionID.Equals(paymentRequest.SaleData.SaleTransactionID))
                            {
                                // Ignore unexpected result
                                continue;
                            }

                            if (r.Response.Result != Result.Failure)
                            {
                                ShowPaymentDialogSuccess(paymentTypeName, r);
                            }
                            else
                            {
                                ShowPaymentDialogFailed(paymentTypeName, null, r.Response?.AdditionalResponse);
                            }

                            UpdateAppState(false, null);
                            waitingForResponse = false;
                            break;

                        case LoginResponse r:
                            // Response to auto login
                            break;

                        case DisplayRequest r:
                            ShowPaymentDialog(paymentTypeName, "PAYMENT IN PROGRESS", r.GetCashierDisplayAsPlainText()?.ToUpper(System.Globalization.CultureInfo.InvariantCulture), "", LightBoxDialogType.Normal, false, true);
                            break;

                        default:
                            // Ignore unexpected result
                            break;
                    }

                }
                while (waitingForResponse);
            }
            catch (FusionException fe)
            {
                if(fe.ErrorRecoveryRequired)
                {
                    await PerformErrorRecovery();
                }
                else
                {
                    ShowPaymentDialogFailed(paymentTypeName, null, fe.Message);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, "UNKNOWN ERROR", ex.Message);
            }
        }

        private async void BtnReconciliation_Click(object sender, RoutedEventArgs e)
        {
            string paymentTypeName = "SETTLE";
            ShowPaymentDialog(paymentTypeName, "SETTLEMENT IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                var r = await fusionClient.SendRecvAsync<ReconciliationResponse>(new ReconciliationRequest(ReconciliationType.SaleReconciliation));
                if (r.Response.Result != Result.Failure)
                {
                    string receipt = r.GetReceiptAsPlainText();
                    ShowPaymentDialog(paymentTypeName, "SETTLEMENT SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowPaymentDialog(paymentTypeName, "SETTLEMENT FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, null, ex.Message);
            }
        }

        private void BtnDialogOK_Click(object sender, RoutedEventArgs e)
        {
            HideLightBoxDialog();
        }

        private async void BtnDialogCancel_Click(object sender, RoutedEventArgs e)
        {
            // Try cancel of payment if one is in progress, otherwise just close this dialog
            if (appState.PaymentInProgress && appState?.MessageHeader is not null)
            {
                var abortRequest = new AbortRequest()
                {
                    MessageReference = new MessageReference()
                    {
                        DeviceID = appState.MessageHeader.DeviceID,
                        MessageCategory = appState.MessageHeader.MessageCategory,
                        POIID = appState.MessageHeader.POIID,
                        SaleID = appState.MessageHeader.SaleID,
                        ServiceID = appState.MessageHeader.ServiceID
                    }
                };

                _ = await fusionClient.SendAsync(abortRequest);
                return;
            }

            UpdateAppState(false, null);
            NavigateToMainPage();
        }

        private async Task PerformErrorRecovery()
        {
            // Exit if we don't have anything to recover
            if (appState is null || appState.PaymentInProgress == false || appState.MessageHeader is null)
            {
                return;
            }

            // 
            string caption = "RECOVERING PAYMENT";
            string title = "PAYMENT RECOVERY IN PROGRESS";
            ShowPaymentDialog(caption, title, null, null, LightBoxDialogType.Normal, false, true);

            var timeout = TimeSpan.FromSeconds(90);
            var requestDelay = TimeSpan.FromSeconds(10);
            var timeoutTimer = new System.Diagnostics.Stopwatch();
            timeoutTimer.Start();

            bool errorRecoveryInProgress = true;
            while (errorRecoveryInProgress && appState.PaymentInProgress)
            {
                var transactionStatusRequest = new TransactionStatusRequest()
                {
                    MessageReference = new MessageReference()
                    {
                        MessageCategory = appState.MessageHeader.MessageCategory,
                        POIID = appState.MessageHeader.POIID,
                        SaleID = appState.MessageHeader.SaleID,
                        ServiceID = appState.MessageHeader.ServiceID
                    }
                };
                
                try
                {
                    var r = await fusionClient.SendRecvAsync<TransactionStatusResponse>(transactionStatusRequest);

                    // If the response to our TransactionStatus request is "Success", we have a PaymentResponse to check
                    if (r.Response.Result == Result.Success)
                    {
                        if (r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response.Result != Result.Failure)
                        {
                            ShowPaymentDialogSuccess(caption, r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse);
                        }
                        else
                        {
                            ShowPaymentDialogFailed(caption, null, r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response?.AdditionalResponse);
                        }

                        errorRecoveryInProgress = false;
                    }
                    // else if the transaction is still in progress, and we haven't reached out timeout
                    else if (r.Response.ErrorCondition == ErrorCondition.InProgress && timeoutTimer.Elapsed < timeout)
                    {
                        ShowPaymentDialog(caption, title, "PAYMENT IN PROGRESS", "", LightBoxDialogType.Normal, false, true);
                    }
                    // otherwise, fail
                    else
                    {
                        ShowPaymentDialogFailed(caption, null, r.Response?.AdditionalResponse);
                        errorRecoveryInProgress = false;
                    }
                }
                catch (DataMeshGroup.Fusion.NetworkException ne)
                {
                    ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ne.Message, LightBoxDialogType.Normal, false, true);
                }
                catch (DataMeshGroup.Fusion.TimeoutException)
                {
                    ShowPaymentDialog(caption, title, "TIMEOUT WAITING FOR HOST...", null, LightBoxDialogType.Normal, false, true);
                }
                catch (Exception ex)
                {
                    ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ex.Message, LightBoxDialogType.Normal, false, true);
                }

                if (errorRecoveryInProgress)
                {
                    await Task.Delay(requestDelay);
                }
            }

            UpdateAppState(false, null);
        }


        #region Message build helpers
        private LoginRequest BuildLoginRequest()
        {
            return new LoginRequest()
            {
                SaleSoftware = new SaleSoftware()
                {
                    ProviderIdentification = settings.ProviderIdentification,
                    ApplicationName = settings.ApplicationName,
                    SoftwareVersion = settings.SoftwareVersion,
                    CertificationCode = settings.CertificationCode
                },
                SaleTerminalData = new SaleTerminalData()
                {
                    SaleCapabilities = new List<SaleCapability>()
                    {
                        SaleCapability.CashierStatus,
                        SaleCapability.CashierError,
                        SaleCapability.CashierInput,
                        SaleCapability.CustomerAssistance,
                        SaleCapability.PrinterReceipt
                    }
                },
                OperatorID = settings.OperatorID,
                ShiftNumber = settings.ShiftNumber
            };
        }

        #endregion

        #region Implement INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        #region PaymentDialog
        private static void SetLabel(Label label, string content)
        {
            label.Content = content;
            label.Visibility = string.IsNullOrEmpty(content) ? Visibility.Hidden : Visibility.Visible;
        }

        private static void SetText(TextBlock textBlock, string content)
        {
            textBlock.Text = content;
            textBlock.Visibility = string.IsNullOrEmpty(content) ? Visibility.Hidden : Visibility.Visible;
        }

        private void ShowPaymentDialogSuccess(string caption, PaymentResponse paymentResponse)
        {
            //ShowPaymentDialog(caption, "PAYMENT APPROVED", null, null, LightBoxDialogType.Success, true, false);
            PaymentResponse = paymentResponse;
            TxtReceipt.Text = paymentResponse.GetReceiptAsPlainText();

            //WebBrowserReceipt.NavigateToString(paymentResponse.PaymentReceipt?.FirstOrDefault()?.OutputContent?.OutputXHTML ?? "");

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "PAYMENT APPROVED";


            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;
            //WebBrowserReceipt.Visibility = Visibility.Visible;
        }

        private void ShowPaymentDialogFailed(string caption, string displayLine1 = null, string displayText = null)
        {
            ShowPaymentDialog(caption, "PAYMENT DECLINED", displayLine1, displayText, LightBoxDialogType.Error, true, false);
        }

        private void ShowPaymentDialog(string caption, string title, string displayLine1, string displayText, LightBoxDialogType lightBoxDialogType, bool enableOk, bool enableCancel)
        {
            // Set caption
            LblPaymentDialogCaption.Content = caption;

            // Set title
            System.Windows.Media.Color foreground, background;
            SetLabel(LblPaymentDialogTitle, title);
            switch (lightBoxDialogType)
            {
                case LightBoxDialogType.Error:
                    background = System.Windows.Media.Color.FromRgb(0xA2, 0x00, 0x25); //red
                    foreground = Colors.White;
                    break;
                case LightBoxDialogType.Success:
                    background = System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00); // green
                    foreground = Colors.White;
                    break;
                case LightBoxDialogType.Normal:
                default:
                    background = Colors.White;
                    foreground = Colors.Black;
                    break;
            }
            BorderPaymentDialogTitle.Background = new SolidColorBrush(background);
            LblPaymentDialogTitle.Foreground = new SolidColorBrush(foreground);

            SetLabel(LblPaymentDialogLine1, displayLine1);
            SetText(TxtPaymentDialogText, displayText);


            if(enableCancel)
            {
                BusyIndicator.Visibility = Visibility.Visible;
                ((System.Windows.Media.Animation.Storyboard)FindResource("WaitStoryboard")).Begin();
            }
            else
            {
                BusyIndicator.Visibility = Visibility.Collapsed;
                ((System.Windows.Media.Animation.Storyboard)FindResource("WaitStoryboard")).Stop();
            }


            BtnDialogOK.Visibility = enableOk ? Visibility.Visible : Visibility.Collapsed;
            BtnDialogCancel.Visibility = enableCancel ? Visibility.Visible : Visibility.Collapsed;

            PaymentDialogGrid.Visibility = Visibility.Visible;
        }

        private void HideLightBoxDialog()
        {
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region PaymentComplete

        private void BtnPaymentCompleteOK_Click(object sender, RoutedEventArgs e)
        {
            GridMain.Visibility = Visibility.Visible;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            //WebBrowserReceipt.Visibility = Visibility.Visible;
        }

        #endregion


    }
}
