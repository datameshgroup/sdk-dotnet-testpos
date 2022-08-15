using DataMeshGroup.Fusion;
using DataMeshGroup.Fusion.Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        //File paths
        private readonly string settingsFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private readonly string appStateFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appstate.json");
        private readonly string mockDataFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mockdata.json");
        private readonly string paymentEventsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PaymentEvents.csv");
        private readonly string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

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
                PaymentUIResponse r = await PerformErrorRecovery();
                if(r != null)
                {
                    DisplayPaymentUIResponse(r);
                }
            }
        }

        private async Task CreateFusionClient()
        {
            // Destroy if client already exists
            if (fusionClient != null)
            {
                await fusionClient.DisconnectAsync();
                fusionClient.OnLog -= FusionClient_OnLog;
                fusionClient.OnConnect -= FusionClient_OnConnect;
                fusionClient.OnDisconnect -= FusionClient_OnDisconnect;
                fusionClient.OnConnectError -= FusionClient_OnConnectError;
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
            fusionClient.OnConnect += FusionClient_OnConnect;
            fusionClient.OnDisconnect += FusionClient_OnDisconnect;
            fusionClient.OnConnectError += FusionClient_OnConnectError;
        }

        private void FusionClient_OnConnectError(object sender, EventArgs e)
        {
            AppendPaymentEvent(DateTime.Now, "SocketConnectError", 0, true);
        }

        private void FusionClient_OnDisconnect(object sender, EventArgs e)
        {
            AppendPaymentEvent(DateTime.Now, "SocketDisconnect", 0, true);
        }

        private void FusionClient_OnConnect(object sender, EventArgs e)
        {
            AppendPaymentEvent(DateTime.Now, "SocketConnect", 0, true);
        }

        private void FusionClient_OnLog(object sender, LogEventArgs e)
        {
            File.AppendAllText(logPath, $"{e.CreatedDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")} - {e.LogLevel.ToString().PadRight(12, ' ')} - {e.Data} {e.Exception?.Message ?? ""}{Environment.NewLine}");
        }

        /// <summary>
        /// Appends an event to the PaymentEvents.csv log
        /// </summary>
        private void AppendPaymentEvent(DateTime dateTime, string eventName, long eventDuration = 0, bool? eventSuccess = null)
        {
            if (settings.EnableLogFile)
            {
                // Write out format ... Date,Time,Event,PaymentDuration,Result
                string s = $"{dateTime:yyyy-MM-dd},{dateTime:HH:mm:ss.fff},{eventName},{eventDuration},{(eventSuccess == true ? "Success" : "Failure")}{Environment.NewLine}";
                File.AppendAllText(paymentEventsPath, s);
            }
        }

        #region Settings, AppState, & Mock Data

        private Settings LoadSettings()
        {
            if (!File.Exists(settingsFilePath))
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

            return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(settingsFilePath));
        }

        private AppState LoadAppState()
        {
            return (!File.Exists(appStateFilePath)) ? new AppState() : JsonConvert.DeserializeObject<AppState>(File.ReadAllText(appStateFilePath));
        }

        private void UpdateAppState(bool paymentInProgress, MessageHeader messageHeader = null)
        {
            appState.PaymentInProgress = paymentInProgress;
            appState.MessageHeader = messageHeader ?? appState.MessageHeader;

            File.WriteAllText(appStateFilePath, JsonConvert.SerializeObject(appState));
        }

        private MockData LoadMockData()
        {
            if (!File.Exists(mockDataFilePath))
                return new MockData() { SaleItems = null };

            return JsonConvert.DeserializeObject<MockData>(File.ReadAllText(mockDataFilePath));
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
            File.WriteAllText(settingsFilePath, System.Text.Json.JsonSerializer.Serialize<Settings>(Settings));
            await CreateFusionClient();
            NavigateToMainPage();
        }

        private void BtnViewSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigateToSettingsPage();
        }

        private async void BtnPayment_Click(object sender, RoutedEventArgs e)
        {
            do
            {
                // Perform payment
                DateTime dateTime = DateTime.Now;
                long tc64 = Environment.TickCount64;
                PaymentUIResponse paymentUIResponse = await DoPayment();
                tc64 = Environment.TickCount64 - tc64;
                
                AppendPaymentEvent(DateTime.Now, "Payment", tc64, paymentUIResponse?.PaymentResponse?.Response.Success);

                // Display if not in test mode
                if (!settings.EnableVolumeTest)
                {
                    DisplayPaymentUIResponse(paymentUIResponse);
                }
            } while (settings.EnableVolumeTest);
        }

        private void BtnPaymentCryptoDotCom_Click(object sender, RoutedEventArgs e)
        {
            return;
        }
        


        /// <summary>
        /// Displays a <see cref="PaymentUIResponse"/> and waits for user to acknowledge
        /// </summary>
        private void DisplayPaymentUIResponse(PaymentUIResponse paymentUIResponse)
        {
            if (paymentUIResponse == null)
            {
                ShowPaymentDialogFailed("ERROR", "UNABLE TO DISPLAY RESULT");
                return;
            }
            else if(!paymentUIResponse.Success)
            {
                ShowPaymentDialogFailed(paymentUIResponse.PaymentType, paymentUIResponse.ErrorTitle, paymentUIResponse.ErrorText, paymentUIResponse.PaymentResponse);
            }
            else
            {
                ShowPaymentDialogSuccess(paymentUIResponse.PaymentType, paymentUIResponse.PaymentResponse);
            }
        }


        /// <summary>
        /// Perform a payment based on current app state and returns a response to display on the UI
        /// </summary>
        /// <returns></returns>
        private async Task<PaymentUIResponse> DoPayment()
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
                return new PaymentUIResponse() { PaymentType = paymentTypeName, ErrorTitle = "INVALID AMOUNT" };
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
                paymentType: paymentType
                );

            MockData mockData = LoadMockData();

            if ((mockData.SaleItems != null) && (mockData.SaleItems.Count > 0))
            {
                foreach (SaleItem saleItem in mockData.SaleItems)
                {
                    paymentRequest.AddSaleItem(productCode: saleItem.ProductCode, productLabel: saleItem.ProductLabel, itemAmount: saleItem.ItemAmount, category: saleItem.Category, subCategory: saleItem.SubCategory);
                }
            }
            else
            {
                // Create sale item
                SaleItem parentItem = paymentRequest.AddSaleItem(
                    productCode: "XXVH776",
                    productLabel: "Big Kahuna Burger",
                    itemAmount: purchaseAmount,
                    category: "food",
                    subCategory: "mains"
                    );
                // Sale item modifiers
                paymentRequest.AddSaleItem(
                        productCode: "XXVH776-0",
                        productLabel: "Extra pineapple",
                        parentItemID: parentItem.ItemID,
                       itemAmount: 0,
                       category: "food",
                       subCategory: "mains"
                       );
                paymentRequest.AddSaleItem(
                    productCode: "XXVH776-1",
                   productLabel: "Side of fries",
                   parentItemID: parentItem.ItemID,
                   itemAmount: 0,
                   category: "food",
                   subCategory: "sides"
                   );
                // Full sale item
                //paymentRequest.AddSaleItem(
                //    productCode: "AB54447",
                //    productLabel: "Das Keyboard 4 Ultimate",
                //    itemAmount: 449.95M,
                //    quantity: 1,
                //    unitOfMeasure: UnitOfMeasure.Other,
                //    eanUpc: "DASK4ULTMBLU",
                //    additionalProductInfo: "Mechanical Keyboard - Cherry MX Blue switch",
                //    unitPrice: 299.95M,
                //    taxCode: "GST",
                //    costBase: 249.95m,
                //    discount: 50.00m,
                //    discountReason: "$50 voucher",
                //    categories: new List<string>() { "Input Devices", "Keyboards", "Mechanical Keyboards" },
                //    brand: "Das",
                //    quantityInStock: 42,
                //    pageURL: "https://mypage/keyboards/AB54447.html",
                //    imageURLs: new List<string>() { "https://mypage/keyboards/AB54447.jpg" },
                //    weight: 1.5m,
                //    weightUnitOfMeasure: WeightUnitOfMeasure.Kilogram
                //    );
            }

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
            PaymentUIResponse paymentUIResponse = new PaymentUIResponse();
            try
            {
                SaleToPOIMessage saleToPoiRequest = await fusionClient.SendAsync(paymentRequest);
                UpdateAppState(true, saleToPoiRequest.MessageHeader);
                bool waitingForResponse = true;
                do
                {
                    // Request to RecvAsync() will either result in a response from the host, or an exception (timeout, network error etc)
                    switch (await fusionClient.RecvAsync())
                    {
                        case PaymentResponse r:
                            UpdateAppState(false);
                            waitingForResponse = false;
                            
                            paymentUIResponse.PaymentResponse = r;
                            if (!r.Response.Success)
                            {
                                paymentUIResponse.ErrorTitle = r.Response?.ErrorCondition.ToString();
                                paymentUIResponse.ErrorText = r.Response?.AdditionalResponse;
                            }
                            break;

                        case LoginResponse r:
                            // Response to auto login - could log this
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
                if (fe.ErrorRecoveryRequired)
                {
                    paymentUIResponse = await PerformErrorRecovery();
                }
                else
                {
                    paymentUIResponse.ErrorText = fe.Message;
                }
            }
            catch (Exception ex)
            {
                paymentUIResponse.ErrorTitle = "UNKNOWN ERROR";
                paymentUIResponse.ErrorText = ex.Message;
            }

            return paymentUIResponse;
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

        private async void BtnLastTxnStatus_Click(object sender, RoutedEventArgs e)
        {
            string caption = "LAST TRANSACTION STATUS";
            string title = "NO LAST TRANSACTION";

            // Exit if we don't have anything to recover
            //if (appState is null || appState.MessageHeader is null)
            //{
            //    ShowPaymentDialog(caption, title, null, null, LightBoxDialogType.Normal, true, false);
            //    return;
            //}

            var transactionStatusRequest = new TransactionStatusRequest()
            {
                MessageReference = new MessageReference()
                {
                    MessageCategory = appState?.MessageHeader?.MessageCategory ?? MessageCategory.Payment,
                    POIID = appState?.MessageHeader?.POIID,
                    SaleID = appState?.MessageHeader?.SaleID,
                    ServiceID = appState?.MessageHeader?.ServiceID
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
                        ShowPaymentDialogFailed(caption, r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response?.AdditionalResponse, null, r.RepeatedMessageResponse?.RepeatedResponseMessageBody?.PaymentResponse);
                    }
                }
                // else if the transaction is still in progress, and we haven't reached out timeout
                else if (r.Response.ErrorCondition == ErrorCondition.InProgress)
                {
                    ShowPaymentDialog(caption, "PAYMENT IN PROGRESS", null, "", LightBoxDialogType.Normal, true, false);
                }
                // otherwise, fail
                else
                {
                    ShowPaymentDialogFailed(caption, null, r.Response?.AdditionalResponse);
                }
            }
            catch (DataMeshGroup.Fusion.NetworkException ne)
            {
                ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ne.Message, LightBoxDialogType.Normal, true, false);
            }
            catch (DataMeshGroup.Fusion.TimeoutException)
            {
                ShowPaymentDialog(caption, title, "TIMEOUT WAITING FOR HOST...", null, LightBoxDialogType.Normal, true, false);
            }
            catch (Exception ex)
            {
                ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ex.Message, LightBoxDialogType.Normal, true, false);
            }
        }

        private void BtnDialogOK_Click(object sender, RoutedEventArgs e)
        {
            HideLightBoxDialog();
        }

        private async void BtnDialogCancel_Click(object sender, RoutedEventArgs e)
        {
            settings.EnableVolumeTest = false;

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

                try
                {
                    _ = await fusionClient.SendAsync(abortRequest);
                }
                catch(FusionException fe)
                {
                    // Throws FusionException when Internet is down
                }

                return;
            }
            UpdateAppState(false);
            NavigateToMainPage();
        }


        //private async Task<(PaymentResponse paymentResponse, string paymentType, string errorTitle, string errorText)> PerformErrorRecovery()
        //{
        //    // Exit if we don't have anything to recover
        //    if (appState is null || appState.PaymentInProgress == false || appState.MessageHeader is null)
        //    {
        //        return;
        //    }

        //    // 
        //    string caption = "RECOVERING PAYMENT";
        //    string title = "PAYMENT RECOVERY IN PROGRESS";
        //    ShowPaymentDialog(caption, title, null, null, LightBoxDialogType.Normal, false, true);

        //    var timeout = TimeSpan.FromSeconds(90);
        //    var requestDelay = TimeSpan.FromSeconds(10);
        //    var timeoutTimer = new System.Diagnostics.Stopwatch();
        //    timeoutTimer.Start();

        //    bool errorRecoveryInProgress = true;
        //    while (errorRecoveryInProgress && appState.PaymentInProgress)
        //    {
        //        var transactionStatusRequest = new TransactionStatusRequest()
        //        {
        //            MessageReference = new MessageReference()
        //            {
        //                MessageCategory = appState.MessageHeader.MessageCategory,
        //                POIID = appState.MessageHeader.POIID,
        //                SaleID = appState.MessageHeader.SaleID,
        //                ServiceID = appState.MessageHeader.ServiceID
        //            }
        //        };

        //        try
        //        {
        //            TransactionStatusResponse r = await fusionClient.SendRecvAsync<TransactionStatusResponse>(transactionStatusRequest);

        //            // If the response to our TransactionStatus request is "Success", we have a PaymentResponse to check
        //            if (r.Response.Result == Result.Success)
        //            {
        //                if (r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response.Result != Result.Failure)
        //                {
        //                    ShowPaymentDialogSuccess(caption, r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse);
        //                }
        //                else
        //                {
        //                    ShowPaymentDialogFailed(caption, null, r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response?.AdditionalResponse);
        //                }

        //                errorRecoveryInProgress = false;
        //            }
        //            // else if the transaction is still in progress, and we haven't reached out timeout
        //            else if (r.Response.ErrorCondition == ErrorCondition.InProgress && timeoutTimer.Elapsed < timeout)
        //            {
        //                ShowPaymentDialog(caption, title, "PAYMENT IN PROGRESS", "", LightBoxDialogType.Normal, false, true);
        //            }
        //            // otherwise, fail
        //            else
        //            {
        //                ShowPaymentDialogFailed(caption, null, r.Response?.AdditionalResponse);
        //                errorRecoveryInProgress = false;
        //            }
        //        }
        //        catch (DataMeshGroup.Fusion.NetworkException ne)
        //        {
        //            ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ne.Message, LightBoxDialogType.Normal, false, true);
        //        }
        //        catch (DataMeshGroup.Fusion.TimeoutException)
        //        {
        //            ShowPaymentDialog(caption, title, "TIMEOUT WAITING FOR HOST...", null, LightBoxDialogType.Normal, false, true);
        //        }
        //        catch (Exception ex)
        //        {
        //            ShowPaymentDialog(caption, title, "WAITING FOR CONNECTION...", ex.Message, LightBoxDialogType.Normal, false, true);
        //        }

        //        if (errorRecoveryInProgress)
        //        {
        //            await Task.Delay(requestDelay);
        //        }
        //    }

        //    UpdateAppState(false);
        //}


        private async Task<PaymentUIResponse> PerformErrorRecovery()
        {
            // Exit if we don't have anything to recover
            if (appState is null || appState.PaymentInProgress == false || appState.MessageHeader is null)
            {
                return null;
            }

            // 
            string caption = "RECOVERING PAYMENT";
            string title = "PAYMENT RECOVERY IN PROGRESS";
            ShowPaymentDialog(caption, title, null, null, LightBoxDialogType.Normal, false, true);

            TimeSpan timeout = TimeSpan.FromSeconds(90);
            TimeSpan requestDelay = TimeSpan.FromSeconds(10);
            Stopwatch timeoutTimer = new Stopwatch();
            timeoutTimer.Start();

            PaymentUIResponse paymentUIResponse = new PaymentUIResponse();
            bool errorRecoveryInProgress = true;
            while (errorRecoveryInProgress && appState.PaymentInProgress)
            {
                TransactionStatusRequest transactionStatusRequest = new TransactionStatusRequest()
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
                    TransactionStatusResponse r = await fusionClient.SendRecvAsync<TransactionStatusResponse>(transactionStatusRequest);

                    // If the response to our TransactionStatus request is "Success", we have a PaymentResponse to check
                    if (r.Response.Result == Result.Success)
                    {
                        paymentUIResponse.PaymentResponse = r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse;

                        if (!r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response.Success)
                        {
                            paymentUIResponse.ErrorText = r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response?.AdditionalResponse;
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
                        paymentUIResponse.ErrorText = r.Response?.AdditionalResponse;
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

            UpdateAppState(false);

            // return result
            return paymentUIResponse;
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
            PaymentResponse = paymentResponse;
            TxtReceipt.Text = paymentResponse.GetReceiptAsPlainText();

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "PAYMENT APPROVED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowPaymentDialogFailed(string caption, string displayLine1 = null, string displayText = null, PaymentResponse paymentResponse = null)
        {
            if(paymentResponse == null)
            {
                ShowPaymentDialog(caption, "PAYMENT DECLINED", displayLine1, displayText, LightBoxDialogType.Error, true, false);
                return;
            }

            PaymentResponse = paymentResponse;
            TxtReceipt.Text = paymentResponse.GetReceiptAsPlainText();

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA2, 0x00, 0x25));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "PAYMENT DECLINED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;

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


            if (enableCancel)
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

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            string paymentTypeName = "LOGOUT";
            ShowPaymentDialog(paymentTypeName, "LOGOUT IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                LogoutResponse r = await fusionClient.SendRecvAsync<LogoutResponse>(new LogoutRequest());
                if (r.Response.Result != Result.Failure)
                {
                    ShowPaymentDialog(paymentTypeName, "LOGOUT SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowPaymentDialog(paymentTypeName, "LOGOUT FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, null, ex.Message);
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string paymentTypeName = "LOGIN";
            ShowPaymentDialog(paymentTypeName, "LOGIN IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                LoginResponse r = await fusionClient.SendRecvAsync<LoginResponse>(fusionClient.LoginRequest);
                if (r.Response.Result != Result.Failure)
                {
                    ShowPaymentDialog(paymentTypeName, "LOGIN SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowPaymentDialog(paymentTypeName, "LOGIN FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, null, ex.Message);
            }
        }
    }


    /// <summary>
    /// Wrapper for a payment response to be displayed on the UI
    /// </summary>
    public class PaymentUIResponse
    {
        public bool Success
        {
            get
            {
                return PaymentResponse?.Response.Success == true;
            }
        }

        public PaymentResponse PaymentResponse { get; set; }
        public string PaymentType { get; set; }
        public string ErrorTitle { get; set; }
        public string ErrorText { get; set; }
    }
}
