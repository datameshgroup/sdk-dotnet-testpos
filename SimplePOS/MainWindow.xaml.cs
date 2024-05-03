using DataMeshGroup.Fusion;
using DataMeshGroup.Fusion.Model;
using DataMeshGroup.Fusion.Pairing.WinForms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private int selectedTerminalIndex = 0;
        private bool? displayAdvanceSettings = null;
        private RequestMessageFromHost receivedMessageFromHost = RequestMessageFromHost.None;
        private Input currentInput;
        private MessageCategory transactionCategory = MessageCategory.Payment;        
        private string transactionDesc = null;
        private TransactionIdentification referencePOITransactionID = null;

        //File paths
        private readonly string settingsFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private readonly string appStateFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appstate.json");
        private readonly string mockDataFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mockdata.json");
        private readonly string paymentEventsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PaymentEvents.csv");
        private readonly string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

        public Task Initialization { get; private set; }

        public enum RequestMessageFromHost
        {
            None,
            Display,
            Input,
            Print
        }

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

        private StoredValueResponse storedValueResponse;
        public StoredValueResponse StoredValueResponse
        {
            get => storedValueResponse;
            set
            {
                if (value != storedValueResponse)
                {
                    storedValueResponse = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private StoredValueResult storedValueResult;
        public StoredValueResult StoredValueResult
        {
            get => storedValueResult;
            set
            {
                if (value != storedValueResult)
                {
                    storedValueResult = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private BalanceInquiryResponse balanceInquiryResponse;
        public BalanceInquiryResponse BalanceInquiryResponse
        {
            get => balanceInquiryResponse;
            set
            {
                if (value != balanceInquiryResponse)
                {
                    balanceInquiryResponse = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private PaymentAccountStatus paymentAccountStatus;
        public PaymentAccountStatus PaymentAccountStatus
        {
            get => paymentAccountStatus;
            set
            {
                if (value != paymentAccountStatus)
                {
                    paymentAccountStatus = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public MainWindow()
        {
            DataContext = this;
            InitializeComponent();

            LoadSettings();
            appState = LoadAppState();            

            UpdateTerminalSettings();

            UpdateAdvanceSettingsVisibility();

            NavigateToMainPage(true);

            Initialization = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            selectedTerminalIndex = Settings.SelectedTerminalIndex;

            await CreateFusionClient();

            if (appState?.TransactionInProgress == true)
            {
                TransactionUIResponse r = await PerformErrorRecovery(appState?.MessageHeader?.MessageCategory ?? MessageCategory.Payment);
                if (r != null)
                {
                    DisplayTransactionUIResponse(r);
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

            string saleID = Settings.SaleID;
            string poiID = Settings.POIID;
            string kek = Settings.KEK;

            if ((selectedTerminalIndex == 1) && DoesTerminal2SettingsExist())
            {
                poiID = Settings.POIID2;
            }

            fusionClient = new FusionClient(Settings.UseTestEnvironment)
            {
                SaleID = saleID,
                POIID = poiID,
                KEK = kek,
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

        private bool DoesTerminal2SettingsExist()
        {
            return !String.IsNullOrEmpty(Settings.POIID2);
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
            File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - {e.LogLevel.ToString().PadRight(12, ' ')} - {e.Data} {e.Exception?.Message ?? ""}{Environment.NewLine}");
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

        private void LoadSettings()
        {
            if (File.Exists(settingsFilePath))
            {
                Settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(settingsFilePath));
            }
            else
            {
                Settings = new Settings()
                {
                    SaleID = "",
                    POIID = "",
                    KEK = "",
                    POIID2 = "",
                    ProviderIdentification = "DMG",
                    ApplicationName = "Integration POS App",
                    SoftwareVersion = "01.00.00",
                    CertificationCode = "28bf60f2-ddc6-448c-aa11-aeb463658cd2",
                    OperatorID = null,
                    ShiftNumber = null,
                    POSName = Environment.MachineName
                };
                NavigateToSettingsPage();
            }
        }

        private AppState LoadAppState()
        {
            return (!File.Exists(appStateFilePath)) ? new AppState() : JsonConvert.DeserializeObject<AppState>(File.ReadAllText(appStateFilePath));
        }

        private void UpdateAppState(bool paymentInProgress, MessageHeader messageHeader = null)
        {
            appState.TransactionInProgress = paymentInProgress;
            appState.MessageHeader = messageHeader ?? appState.MessageHeader;

            File.WriteAllText(appStateFilePath, JsonConvert.SerializeObject(appState));
        }

        private void UpdateAdvanceSettingsVisibility()
        {
            if ((displayAdvanceSettings != null) && (displayAdvanceSettings == settings.DisplayAdvanceSettings))
            {
                return;
            }

            if (settings.DisplayAdvanceSettings)
            {
                BtnLastTxnStatus.Visibility = Visibility.Collapsed;

                TxtServiceID.Text = string.Empty;
                WPanelServiceID.Visibility = Visibility.Visible;

                TxtMessageReferenceServiceID.Text = string.Empty;
                SPanelTxnStatus.Visibility = Visibility.Visible;

                TxtDMGTestCaseID.Text = string.Empty;
                WPanelDMGTestCaseID.Visibility = Visibility.Visible;
            }
            else
            {
                BtnLastTxnStatus.Visibility = Visibility.Visible;
                WPanelServiceID.Visibility = Visibility.Collapsed;
                WPanelDMGTestCaseID.Visibility = Visibility.Collapsed;
                SPanelTxnStatus.Visibility = Visibility.Collapsed;
            }

            displayAdvanceSettings = settings.DisplayAdvanceSettings;
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
            UpdateAllTerminalDisplay();
            GridSettings.Visibility = Visibility.Visible;
            GridMain.Visibility = Visibility.Collapsed;
        }

        private void NavigateToMainPage(bool updateTerminalSelectionSettings = false)
        {
            if (updateTerminalSelectionSettings)
            {
                if (DoesTerminal2SettingsExist())
                {
                    CboTerminalSelection.Items.Clear();
                    CboTerminalSelection.Items.Add(Settings.POIID);
                    CboTerminalSelection.Items.Add(Settings.POIID2);
                    if (selectedTerminalIndex > CboTerminalSelection.Items.Count)
                    {
                        selectedTerminalIndex = 0;
                    }
                    SelectTerminalPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    selectedTerminalIndex = 0;
                    SelectTerminalPanel.Visibility = Visibility.Collapsed;
                }
                SaveTerminalIndex();
                CboTerminalSelection.SelectedIndex = selectedTerminalIndex;
            }
            GridSettings.Visibility = Visibility.Collapsed;
            GridMain.Visibility = Visibility.Visible;
        }
        #endregion

        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            UpdateTerminalSettings();
            UpdateAdvanceSettingsVisibility();
            File.WriteAllText(settingsFilePath, System.Text.Json.JsonSerializer.Serialize<Settings>(Settings));
            await CreateFusionClient();
            NavigateToMainPage(true);
        }

        private void UpdateTerminalSettings()
        {
            //If only the Terminal 2 Settings were set, place the settings to the first terminal instead.            
            if (String.IsNullOrEmpty(Settings.POIID) &&
                DoesTerminal2SettingsExist())
            {
                Settings.POIID = (new StringBuilder(Settings.POIID2)).ToString();

                Settings.POIID2 = string.Empty;

                selectedTerminalIndex = 0;

                SaveTerminalIndex();
            }
        }

        private void BtnViewSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigateToSettingsPage();
        }

        private void BtnPairWithTerminal_Click(object sender, RoutedEventArgs e)
        {
            PairTerminal(0);
        }

        private void BtnPairWithTerminal2_Click(object sender, RoutedEventArgs e)
        {
            PairTerminal(1);
        }

        private void BtnUnpairFromTerminal_Click(object sender, RoutedEventArgs e)
        {
            UnpairFromTerminal(0);
        }

        private void BtnUnpairWithTerminal2_Click(object sender, RoutedEventArgs e)
        {
            UnpairFromTerminal(1);
        }

        private async void BtnPayment_Click(object sender, RoutedEventArgs e)
        {
            await CreateFusionClient();
            do
            {
                // Perform payment
                DateTime dateTime = DateTime.Now;
                long tc64 = Environment.TickCount64;
                TransactionUIResponse transactionUIResponse = await DoPayment();
                tc64 = Environment.TickCount64 - tc64;

                if (transactionCategory == MessageCategory.StoredValue)
                {
                    AppendPaymentEvent(DateTime.Now, "Stored Value", tc64, transactionUIResponse?.StoredValueResponse?.Response.Success);
                }
                else if (transactionCategory == MessageCategory.BalanceInquiry)
                {
                    AppendPaymentEvent(DateTime.Now, "Balance Inquiry", tc64, transactionUIResponse?.BalanceInquiryResponse?.Response.Success);
                }
                else
                {
                    AppendPaymentEvent(DateTime.Now, "Payment", tc64, transactionUIResponse?.PaymentResponse?.Response.Success);
                }

                // Display if not in test mode
                if (!settings.EnableVolumeTest)
                {
                    DisplayTransactionUIResponse(transactionUIResponse);
                }
            } while (settings.EnableVolumeTest);
        }

        private void BtnPaymentCryptoDotCom_Click(object sender, RoutedEventArgs e)
        {
            return;
        }

        /// <summary>
        /// Displays a <see cref="TransactionUIResponse"/> and waits for user to acknowledge
        /// </summary>
        private void DisplayTransactionUIResponse(TransactionUIResponse transactionUIResponse)
        {
            if (transactionUIResponse == null)
            {
                ShowPaymentDialogFailed("ERROR", "UNABLE TO DISPLAY RESULT");
                return;
            }
            else if (!transactionUIResponse.Success)
            {
                if (transactionUIResponse.TransactionCategory == MessageCategory.StoredValue)
                {
                    ShowStoredValueDialogFailed(transactionUIResponse.TransactionType, transactionUIResponse.ErrorTitle, transactionUIResponse.ErrorText, transactionUIResponse.StoredValueResponse);
                }
                else if(transactionUIResponse.TransactionCategory == MessageCategory.BalanceInquiry)
                {
                    ShowBalanceInquiryDialogFailed(transactionUIResponse.TransactionType, transactionUIResponse.ErrorTitle, transactionUIResponse.ErrorText, transactionUIResponse.BalanceInquiryResponse);
                }
                else
                {
                    ShowPaymentDialogFailed(transactionUIResponse.TransactionType, transactionUIResponse.ErrorTitle, transactionUIResponse.ErrorText, transactionUIResponse.PaymentResponse);
                }
            }
            else
            {
                if (transactionUIResponse.TransactionCategory == MessageCategory.StoredValue)
                {
                    ShowStoredValueDialogSuccess(transactionUIResponse.TransactionType, transactionUIResponse.StoredValueResponse);
                }
                else if(transactionUIResponse.TransactionCategory == MessageCategory.BalanceInquiry)
                {
                    ShowBalanceInquiryDialogSuccess(transactionUIResponse.TransactionType, transactionUIResponse.BalanceInquiryResponse);
                }
                else
                {
                    ShowPaymentDialogSuccess(transactionUIResponse.TransactionType, transactionUIResponse.PaymentResponse);
                }
            }
        }

        /// <summary>
        /// Perform a payment based on current app state and returns a response to display on the UI
        /// </summary>
        /// <returns></returns>
        private async Task<TransactionUIResponse> DoPayment()
        {
            // Validate input
            PaymentType paymentType = PaymentType.Unknown;
            StoredValueTransactionType storedValueTransactionType = StoredValueTransactionType.Activate;
            string transactionTypeName;
            transactionCategory = MessageCategory.Payment;
            switch (CboTxnType.SelectedIndex)
            {
                case 0:
                    paymentType = PaymentType.Normal;
                    transactionTypeName = "PURCHASE";
                    break;
                case 1:
                    paymentType = PaymentType.Refund;
                    transactionTypeName = "REFUND";
                    break;
                case 3:                    
                    transactionCategory = MessageCategory.StoredValue;
                    storedValueTransactionType = StoredValueTransactionType.Activate;
                    transactionTypeName = "GIFT CARD ACTIVATION";
                    break;
                case 4:
                    transactionCategory = MessageCategory.StoredValue;
                    storedValueTransactionType = StoredValueTransactionType.Reverse;
                    transactionTypeName = "GIFT CARD REVERSAL";
                    break;
                case 5:
                    transactionCategory = MessageCategory.BalanceInquiry;                    
                    transactionTypeName = "BALANCE INQUIRY";
                    break;
                case 2:
                default:
                    paymentType = PaymentType.CashAdvance;
                    transactionTypeName = "CASH ADVANCE";
                    break;
            }

            string transactionServiceID = null;
            MessagePayload transactionRequest = null;
            string txtInProgress;

            if (transactionCategory == MessageCategory.Payment)
            {
                transactionDesc = "PAYMENT";
                txtInProgress = "PAYMENT IN PROGRESS";
                if (!decimal.TryParse(TxtPurchaseAmount.Text, out decimal purchaseAmount))
                {
                    return new TransactionUIResponse(transactionCategory) { TransactionType = transactionTypeName, ErrorTitle = "INVALID AMOUNT" };
                }

                List<string> allowedPaymentBrand = null;
                if(TxtAllowedPaymentBrands.Text != null)
                {
                    string allowedPaymentBrandString = TxtAllowedPaymentBrands.Text.Replace("\"", string.Empty).Replace(@"\""", string.Empty).Trim();
                    if (allowedPaymentBrandString.Length > 0)
                    {
                        try
                        {
                            allowedPaymentBrand = allowedPaymentBrandString.Split(',').Select(t => t.Trim()).ToList();
                        }
                        catch (Exception)
                        {
                            allowedPaymentBrand = null;
                            return new TransactionUIResponse(transactionCategory) { TransactionType = transactionTypeName, ErrorTitle = "INVALID ALLOWED PAYMENT BRAND" };
                        }
                    }
                }

                // Tip amount can be null
                decimal? tipAmount = decimal.TryParse(TxtTipAmount.Text, out decimal tmpTipAmount) ? tmpTipAmount : null;
                // Cash amount can be null
                decimal? cashoutAmount = decimal.TryParse(TxtCashoutAmount.Text, out decimal tmpCashoutAmount) ? tmpCashoutAmount : null;
                bool requestCardToken = ChkRequestToken.IsChecked ?? false;
                //string cardToken = string.IsNullOrWhiteSpace(TxtCardToken.Text) ? null : TxtCardToken.Text;

                ShowTransactionDialog(transactionTypeName, txtInProgress, "", "", LightBoxDialogType.Normal, false, true);

                // Create basic payment
                // Construct payment request
                PaymentRequest paymentRequest = new PaymentRequest()
                {
                    PaymentData = new PaymentData()
                    {
                        PaymentType = paymentType
                    },
                    PaymentTransaction = new PaymentTransaction()
                    {
                        AmountsReq = new AmountsReq()
                        {
                            Currency = CurrencySymbol.AUD,
                            RequestedAmount = purchaseAmount,
                            TipAmount = tipAmount
                        },
                        TransactionConditions = new TransactionConditions()
                        {
                            AllowedPaymentBrand = allowedPaymentBrand
                        }
                    }
                };

                bool isMockDataLoaded = false;
                try
                {
                    MockData mockData = LoadMockData();

                    if ((mockData.SaleItems != null) && (mockData.SaleItems.Count > 0))
                    {
                        paymentRequest.PaymentTransaction.SaleItem = mockData.SaleItems;
                        isMockDataLoaded = true;
                    }
                }
                catch (Exception ex)
                {

                    File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - Error - Unable to load Mock Data: {ex?.Message ?? ""}{Environment.NewLine}");
                }

                SaleData saleData = new SaleData()
                {
                    SaleTransactionID = new TransactionIdentification()
                    {
                        TransactionID = Guid.NewGuid().ToString("N"),
                        TimeStamp = DateTime.UtcNow
                    },
                };
                saleData.OperatorID = settings.OperatorID;
                saleData.ShiftNumber = settings.ShiftNumber;
                paymentRequest.SaleData = saleData;

                if (!isMockDataLoaded)
                {
                    SetDefaultSaleItems(paymentRequest, purchaseAmount);
                }                

                if (displayAdvanceSettings == true)
                {
                    string testCaseID = TxtDMGTestCaseID.Text;
                    if (!string.IsNullOrEmpty(testCaseID))
                    {
                        paymentRequest.AddSaleItem(productCode: testCaseID, productLabel: testCaseID, itemAmount: 0);
                    }

                    if (!string.IsNullOrEmpty(TxtServiceID.Text))
                    {
                        transactionServiceID = TxtServiceID.Text;
                    }
                }

                // Add extra fields
                paymentRequest.PaymentTransaction.AmountsReq.CashBackAmount = cashoutAmount;
                paymentRequest.SaleData.TokenRequestedType = requestCardToken ? TokenRequestedType.Customer : null;
                paymentRequest.PaymentTransaction.AmountsReq.TipAmount = tipAmount;

                transactionRequest = paymentRequest;


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
            } 
            else if(transactionCategory == MessageCategory.StoredValue)
            {
                transactionDesc = StoredValueTransactionAccountTypeToDisplayText(storedValueTransactionType, StoredValueAccountType.GiftCard);
                if (!decimal.TryParse(TxtStoredValueAmount.Text, out decimal transactionAmount))
                {
                    return new TransactionUIResponse(transactionCategory) { TransactionType = transactionTypeName, ErrorTitle = "INVALID AMOUNT" };
                } 
                else if (TxtEanUPCBarcode.Text == null || TxtEanUPCBarcode.Text.Trim().Equals(string.Empty))
                {
                    return new TransactionUIResponse(transactionCategory) { TransactionType = transactionTypeName, ErrorTitle = "INVALID Ean/UPC" };
                }                

                txtInProgress = $"{transactionDesc} IN PROGRESS"; //to do:  update when more stored value account types are supported                

                ShowTransactionDialog(transactionTypeName, txtInProgress, "", "", LightBoxDialogType.Normal, false, true);

                StoredValueRequest storedValueRequest = new StoredValueRequest();
                StoredValueAccountID storedValueAccountID = new StoredValueAccountID()
                {
                    EntryMode = EntryMode.Manual,
                    StoredValueAccountType = StoredValueAccountType.GiftCard,
                    IdentificationType = IdentificationType.BarCode,
                    StoredValueID = TxtStoredValueID.Text
                };
                OriginalPOITransaction originalPOITransaction = null;
                if (storedValueTransactionType == StoredValueTransactionType.Reverse)
                {
                    originalPOITransaction = new OriginalPOITransaction
                    {
                        POITransactionID = referencePOITransactionID
                    };
                }

                storedValueRequest.AddStoredValueData(storedValueTransactionType, transactionAmount, null, storedValueAccountID, originalPOITransaction, "001", TxtEanUPCBarcode.Text, null, "AUD");                
                
                try
                {
                    MockData mockData = LoadMockData();

                    if ((mockData.StoredValueData != null) && (mockData.StoredValueData.Count > 0))
                    {
                        storedValueRequest.StoredValueData = mockData.StoredValueData;
                    }
                }
                catch (Exception ex)
                {

                    File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - Error - Unable to load Mock Data: {ex?.Message ?? ""}{Environment.NewLine}");
                }

                SaleData saleData = new SaleData()
                {
                    SaleTransactionID = new TransactionIdentification()
                    {
                        TransactionID = Guid.NewGuid().ToString("N"),
                        TimeStamp = DateTime.UtcNow
                    },
                };
                saleData.OperatorID = settings.OperatorID;
                saleData.ShiftNumber = settings.ShiftNumber;
                storedValueRequest.SaleData = saleData;

                if (displayAdvanceSettings == true)
                {
                    string testCaseID = TxtDMGTestCaseID.Text;
                    if (!string.IsNullOrEmpty(testCaseID))
                    {
                        storedValueRequest.AddStoredValueData(StoredValueTransactionType.Activate, 0, null, new StoredValueAccountID(), null, testCaseID, testCaseID);                        
                    }

                    if (!string.IsNullOrEmpty(TxtServiceID.Text))
                    {
                        transactionServiceID = TxtServiceID.Text;
                    }
                }

                transactionRequest = storedValueRequest;

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
            }
            else //if (transactionCategory == MessageCategory.BalanceInquiry)
            {
                transactionDesc = "BALANCE INQUIRY";
                txtInProgress = "BALANCE INQUIRY IN PROGRESS";

                ShowTransactionDialog(transactionTypeName, txtInProgress, "", "", LightBoxDialogType.Normal, false, true);               

                BalanceInquiryRequest balanceInquiryRequest = new BalanceInquiryRequest();

                if (displayAdvanceSettings == true)
                {
                    string testCaseID = TxtDMGTestCaseID.Text;
                    if (!string.IsNullOrEmpty(testCaseID))
                    {
                        LoyaltyAccountReq loyaltyAccountReq = new LoyaltyAccountReq()
                        {
                            LoyaltyAccountID = new LoyaltyAccountID()
                            {
                                LoyaltyID = testCaseID
                            }
                        };
                        balanceInquiryRequest.LoyaltyAccountReq = loyaltyAccountReq;
                    }

                    if (!string.IsNullOrEmpty(TxtServiceID.Text))
                    {
                        transactionServiceID = TxtServiceID.Text;
                    }
                }

                transactionRequest = balanceInquiryRequest;                
            }

            bool checkTimer = false;

            int transactionProcessingTime = Settings.TransactionProcessingTimeSecs;
            int transactionResponseTimeout = Settings.TransactionResponseTimeoutSecs * 1000; //convert to milliseconds

            TimeSpan transactionTimeout = TimeSpan.FromSeconds(transactionProcessingTime);
            System.Diagnostics.Stopwatch timeoutTimer = new System.Diagnostics.Stopwatch();
            if (transactionProcessingTime > 0)
            {
                checkTimer = true;
                timeoutTimer.Start();
            }

            // Check environment 
            TransactionUIResponse transactionUIResponse = new TransactionUIResponse(transactionCategory);
            SaleToPOIMessage saleToPoiRequest = null;
            try
            {
                receivedMessageFromHost = RequestMessageFromHost.None;

                if (string.IsNullOrEmpty(transactionServiceID))
                {
                    saleToPoiRequest = await fusionClient.SendAsync(transactionRequest);
                }
                else
                {
                    MessageJSON(transactionRequest, transactionServiceID);
                    saleToPoiRequest = await fusionClient.SendAsync(transactionRequest, transactionServiceID);
                }

                UpdateAppState(true, saleToPoiRequest.MessageHeader);
                bool waitingForResponse = true;
                do
                {
                    MessagePayload messagePayload = await fusionClient.RecvAsync(new System.Threading.CancellationTokenSource(transactionResponseTimeout).Token);
                    // Request to RecvAsync() will either result in a response from the host, or an exception (timeout, network error etc)
                    switch (messagePayload)
                    {
                        case PaymentResponse r:
                            if (transactionCategory == MessageCategory.Payment)
                            {
                                UpdateAppState(false);
                                waitingForResponse = false;
                                
                                transactionUIResponse.PaymentResponse = r;
                                if (!r.Response.Success)
                                {
                                    transactionUIResponse.ErrorTitle = r.Response?.ErrorCondition.ToString();
                                    transactionUIResponse.ErrorText = r.Response?.AdditionalResponse;
                                }
                            }
                            break;
                        case StoredValueResponse r:
                            if (transactionCategory == MessageCategory.StoredValue)
                            {
                                UpdateAppState(false);
                                waitingForResponse = false;
                                
                                transactionUIResponse.StoredValueResponse = r;
                                if (!r.Response.Success)
                                {
                                    transactionUIResponse.ErrorTitle = r.Response?.ErrorCondition.ToString();
                                    transactionUIResponse.ErrorText = r.Response?.AdditionalResponse;
                                }
                                referencePOITransactionID = r.POIData?.POITransactionID;
                            }
                            break;
                        case BalanceInquiryResponse r:                        
                            if (transactionCategory == MessageCategory.BalanceInquiry)
                            {
                                UpdateAppState(false);
                                waitingForResponse = false;
                                
                                transactionUIResponse.BalanceInquiryResponse = r;
                                if (!r.Response.Success)
                                {
                                    transactionUIResponse.ErrorTitle = r.Response?.ErrorCondition.ToString();
                                    transactionUIResponse.ErrorText = r.Response?.AdditionalResponse;
                                }
                            }
                            break;
                        case LoginResponse r:
                            // Response to auto login - could log this
                            break;

                        case DisplayRequest r:                            
                            ShowTransactionDialog(transactionTypeName, txtInProgress, r.GetCashierDisplayAsPlainText()?.ToUpper(System.Globalization.CultureInfo.InvariantCulture), "", LightBoxDialogType.Normal, false, true);
                            break;

                        case InputRequest r:
                            receivedMessageFromHost = RequestMessageFromHost.Input;
                            ShowPaymentInputDialog(transactionTypeName, "INPUT REQUIRED", r.GetCashierDisplayAsPlainText()?.ToUpper(System.Globalization.CultureInfo.InvariantCulture), r.InputData.InputCommand);
                            break;

                        case PrintRequest r:
                            receivedMessageFromHost = RequestMessageFromHost.Print;
                            ShowPaymentPrintDialog(transactionTypeName, "PRINT REQUIRED", r.GetReceiptAsPlainText()?.ToUpper(System.Globalization.CultureInfo.InvariantCulture));
                            break;

                        default:
                            // Ignore unexpected result
                            break;
                    }
                    if (waitingForResponse && checkTimer && (timeoutTimer.Elapsed > transactionTimeout))
                    {
                        File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - Transaction timeout.{Environment.NewLine}");
                        waitingForResponse = false;
                        timeoutTimer.Stop();
                        if (saleToPoiRequest == null)
                        {
                            transactionUIResponse.ErrorText = "TRANSACTION TIMEOUT";
                        }
                        else
                        {
                            transactionUIResponse = await PerformErrorRecovery(transactionCategory);
                        }
                    }
                }
                while (waitingForResponse);
            }
            catch (FusionException fe)
            {
                File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - Error - FusionException: {fe?.Message ?? ""}. Error recovery required: {fe.ErrorRecoveryRequired}{Environment.NewLine}");
                if (fe.ErrorRecoveryRequired && (saleToPoiRequest != null))
                {
                    transactionUIResponse = await PerformErrorRecovery(transactionCategory);
                }
                else
                {
                    transactionUIResponse.ErrorText = fe.Message;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - Error - Exception: {ex?.Message ?? ""}{Environment.NewLine}");
                transactionUIResponse.ErrorTitle = "UNKNOWN ERROR";
                transactionUIResponse.ErrorText = ex.Message;
            }

            return transactionUIResponse;
        }

        private void MessageJSON(MessagePayload requestMessage, string serviceID) {            
            string saleID = settings.SaleID;
            string poiID = settings.POIID;
            string kek = settings.KEK;            
            JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore };

            // Construct MessageHeader from RequestMessage
            MessageHeader messageHeader = new MessageHeader()
            {
                ProtocolVersion = "3.1-dmg",
                MessageClass = requestMessage.MessageClass,
                MessageCategory = requestMessage.MessageCategory,
                MessageType = requestMessage.MessageType,
                ServiceID = serviceID,
                POIID = poiID,
                SaleID = saleID
            };

            string macBody = $"\"MessageHeader\":{JsonConvert.SerializeObject(messageHeader, Formatting.None, jsonSerializerSettings)},\"{messageHeader.GetMessageDescription()}\":{JsonConvert.SerializeObject(requestMessage, Formatting.None, jsonSerializerSettings)}";

            File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} - MAC Body: " + macBody);
        }

        /// <summary>
        /// Pair POS with terminal
        /// </summary>
        /// <param name="terminalIndex"></param>
        /// <returns></returns>
        private void PairTerminal(int terminalIndex)
        {
            FusionClient fusionClient = new FusionClient(useTestEnvironment: true)
            {
                LoginRequest = new LoginRequest(settings.ProviderIdentification, settings.ApplicationName, settings.SoftwareVersion, settings.CertificationCode)
            };
            if (settings.UseTestEnvironment && !string.IsNullOrEmpty(settings.CustomNexoURL))
            {
                fusionClient.CustomURL = settings.CustomNexoURL;
                fusionClient.URL = UnifyURL.Custom;
            }

            PairingDialog pairingDialog = new(fusionClient, settings.POSName);
            if (!string.IsNullOrEmpty(settings.SaleID))
            {
                fusionClient.SaleID = settings.SaleID;
            }
            if (!string.IsNullOrEmpty(settings.KEK))
            {
                fusionClient.KEK = settings.KEK;
            }
            if (pairingDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                settings.SaleID = pairingDialog.SaleID;
                settings.KEK = pairingDialog.KEK;
                if (terminalIndex == 0)
                {
                    if (!pairingDialog.POIID.Equals(settings.POIID2)) //don't pair the same terminal twice
                    {
                        settings.POIID = pairingDialog.POIID;
                    }
                }
                else if (!pairingDialog.POIID.Equals(settings.POIID)) //don't pair the same terminal twice
                {
                    settings.POIID2 = pairingDialog.POIID;
                }
                UpdateTerminalDisplay(terminalIndex);
            }
        }

        private void UpdateAllTerminalDisplay()
        {
            UpdateTerminalDisplay(0);
            UpdateTerminalDisplay(1);
        }

        private void UnpairFromTerminal(int terminalIndex)
        {
            if (terminalIndex == 0)
            {
                Settings.POIID = string.Empty;
            }
            else
            {
                Settings.POIID2 = string.Empty;
            }
            UpdateTerminalDisplay(terminalIndex);
        }

        private void UpdateTerminalDisplay(int terminalIndex)
        {
            if (terminalIndex == 0)
            {
                if (String.IsNullOrEmpty(Settings.POIID))
                {
                    WPPOIID.Visibility = Visibility.Collapsed;
                    WPPairWithTerminal.Visibility = Visibility.Visible;
                    WPUnpairFromTerminal.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WPPOIID.Visibility = Visibility.Visible;
                    WPPairWithTerminal.Visibility = Visibility.Collapsed;
                    WPUnpairFromTerminal.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (String.IsNullOrEmpty(Settings.POIID2))
                {
                    WPPOIID2.Visibility = Visibility.Collapsed;
                    WPPairWithTerminal2.Visibility = Visibility.Visible;
                    WPUnpairFromTerminal2.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WPPOIID2.Visibility = Visibility.Visible;
                    WPPairWithTerminal2.Visibility = Visibility.Collapsed;
                    WPUnpairFromTerminal2.Visibility = Visibility.Visible;
                }
            }
        }

        private void SetDefaultSaleItems(PaymentRequest paymentRequest, decimal purchaseAmount)
        {
            decimal quarterAmount = purchaseAmount / 4;
            // Create sale item
            SaleItem parentItem = paymentRequest.AddSaleItem(
                productCode: "XXVH776",
                productLabel: "Big Kahuna Burger",
                quantity: 3,
                unitPrice: quarterAmount,
                itemAmount: quarterAmount * 3,
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
            productLabel: "Extra sauce",
            parentItemID: parentItem.ItemID,
            itemAmount: 0,
            category: "food",
            subCategory: "sides"
            );
            paymentRequest.AddSaleItem(
                productCode: "XXVH776-2",
               productLabel: "Side of fries (Large)",
               parentItemID: parentItem.ItemID,
               quantity: 1,
               unitPrice: quarterAmount,
               itemAmount: quarterAmount,
               category: "food",
               subCategory: "sides"
               );
        }

        private async void BtnReconciliation_Click(object sender, RoutedEventArgs e)
        {
            string paymentTypeName = "SETTLE";
            ShowTransactionDialog(paymentTypeName, "SETTLEMENT IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                await CreateFusionClient();
                var r = await fusionClient.SendRecvAsync<ReconciliationResponse>(new ReconciliationRequest(ReconciliationType.SaleReconciliation));
                if (r.Response.Result != Result.Failure)
                {
                    string receipt = r.GetReceiptAsPlainText();
                    ShowTransactionDialog(paymentTypeName, "SETTLEMENT SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowTransactionDialog(paymentTypeName, "SETTLEMENT FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, null, ex.Message);
            }
        }

        private void BtnTxnStatus_Click(object sender, RoutedEventArgs e)
        {
            MessageCategory messageCategory;
            switch (CboTxnStatusType.SelectedIndex)
            {
                case 0:
                    messageCategory = appState?.MessageHeader?.MessageCategory ?? MessageCategory.Payment;
                    break;
                default:
                    messageCategory = MessageCategory.Reversal;
                    break;
            }

            string messageReferenceServiceID = TxtMessageReferenceServiceID.Text;
            if (string.IsNullOrEmpty(messageReferenceServiceID))
            {
                messageReferenceServiceID = appState?.MessageHeader?.ServiceID;
            }

            processTransactionStatus(messageCategory, messageReferenceServiceID);
        }

        private void BtnLastTxnStatus_Click(object sender, RoutedEventArgs e)
        {
            processTransactionStatus(appState?.MessageHeader?.MessageCategory ?? MessageCategory.Payment, appState?.MessageHeader?.ServiceID);
        }

        private async void processTransactionStatus(MessageCategory messageCategory, String messageReferenceServiceID)
        {
            string caption = "LAST TRANSACTION STATUS";
            string title = "NO LAST TRANSACTION";

            var transactionStatusRequest = new TransactionStatusRequest()
            {
                MessageReference = new MessageReference()
                {
                    MessageCategory = messageCategory,
                    POIID = appState?.MessageHeader?.POIID,
                    SaleID = appState?.MessageHeader?.SaleID,
                    ServiceID = messageReferenceServiceID
                }
            };

            try
            {
                await CreateFusionClient();

                var r = await fusionClient.SendRecvAsync<TransactionStatusResponse>(transactionStatusRequest);

                // If the response to our TransactionStatus request is "Success", we have a PaymentResponse to check
                if (r.Response.Result == Result.Success)
                {
                    if (messageCategory == MessageCategory.StoredValue)
                    {
                        if (r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse.Response.Result != Result.Failure)
                        {
                            ShowStoredValueDialogSuccess(caption, r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse);
                        }
                        else
                        {
                            ShowStoredValueDialogFailed(caption, r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse.Response?.AdditionalResponse, null, r.RepeatedMessageResponse?.RepeatedResponseMessageBody?.StoredValueResponse);
                        }
                    }
                    else
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
                }
                // else if the transaction is still in progress, and we haven't reached out timeout
                else if (r.Response.ErrorCondition == ErrorCondition.InProgress)
                {
                    ShowTransactionDialog(caption, "TRANSACTION IN PROGRESS", null, "", LightBoxDialogType.Normal, true, false);
                }
                // otherwise, fail
                else
                {
                    ShowPaymentDialogFailed(caption, null, r.Response?.AdditionalResponse);
                }
            }
            catch (DataMeshGroup.Fusion.NetworkException ne)
            {
                ShowTransactionDialog(caption, title, "WAITING FOR CONNECTION...", ne.Message, LightBoxDialogType.Normal, true, false);
            }
            catch (DataMeshGroup.Fusion.TimeoutException)
            {
                ShowTransactionDialog(caption, title, "TIMEOUT WAITING FOR HOST...", null, LightBoxDialogType.Normal, true, false);
            }
            catch (Exception ex)
            {
                ShowTransactionDialog(caption, title, "WAITING FOR CONNECTION...", ex.Message, LightBoxDialogType.Normal, true, false);
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
            if (appState.TransactionInProgress && appState?.MessageHeader is not null)
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
                catch (FusionException)
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


        private async Task<TransactionUIResponse> PerformErrorRecovery(MessageCategory messageCategory)
        {
            // Exit if we don't have anything to recover
            if (appState is null || appState.TransactionInProgress == false || appState.MessageHeader is null)
            {
                return null;
            }

            if(transactionDesc == null)
            {
                switch (messageCategory)
                {
                    case MessageCategory.StoredValue:
                        transactionDesc = "GIFT CARD TRANSACTION";
                        break;
                    case MessageCategory.BalanceInquiry:
                        transactionDesc = "BALANCE INQUIRY";
                        break;
                    default:
                        transactionDesc = "PAYMENT";
                        break;
                }
            }

            string caption = $"RECOVERING {transactionDesc}";
            string title = $"{transactionDesc} IN PROGRESS";
            ShowTransactionDialog(caption, title, null, null, LightBoxDialogType.Normal, false, true);

            TimeSpan timeout = TimeSpan.FromSeconds(Settings.RecoveryProcessingTimeSecs);
            TimeSpan requestDelay = TimeSpan.FromSeconds(Settings.BetweenStatusCheckTimeSecs);
            Stopwatch timeoutTimer = new Stopwatch();
            timeoutTimer.Start();

            TransactionUIResponse transactionUIResponse = new TransactionUIResponse(messageCategory);
            bool errorRecoveryInProgress = true;
            while (errorRecoveryInProgress && appState.TransactionInProgress)
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
                        if (messageCategory == MessageCategory.Payment)
                        {
                            transactionUIResponse.PaymentResponse = r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse;

                            if (!r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response.Success)
                            {
                                transactionUIResponse.ErrorText = r.RepeatedMessageResponse.RepeatedResponseMessageBody.PaymentResponse.Response?.AdditionalResponse;
                            }
                        }
                        else if(messageCategory == MessageCategory.StoredValue) 
                        {
                            transactionUIResponse.StoredValueResponse = r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse;

                            if (!r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse.Response.Success)
                            {
                                transactionUIResponse.ErrorText = r.RepeatedMessageResponse.RepeatedResponseMessageBody.StoredValueResponse.Response?.AdditionalResponse;
                            }

                        }
                        else if (messageCategory == MessageCategory.BalanceInquiry)
                        {
                            transactionUIResponse.BalanceInquiryResponse = r.RepeatedMessageResponse.RepeatedResponseMessageBody.BalanceInquiryResponse;

                            if (!r.RepeatedMessageResponse.RepeatedResponseMessageBody.BalanceInquiryResponse.Response.Success)
                            {
                                transactionUIResponse.ErrorText = r.RepeatedMessageResponse.RepeatedResponseMessageBody.BalanceInquiryResponse.Response?.AdditionalResponse;
                            }

                        }

                        errorRecoveryInProgress = false;
                    }
                    // else if the transaction is still in progress
                    else if (r.Response.ErrorCondition == ErrorCondition.InProgress)
                    {                        
                        //check if we haven't reached out timeout
                        if (timeoutTimer.Elapsed < timeout)
                        {
                            ShowTransactionDialog(caption, title, (messageCategory == MessageCategory.Payment) ? "PAYMENT IN PROGRESS": "TRANSACTION IN PROGRESS", "", LightBoxDialogType.Normal, false, true);
                        }
                        else
                        {
                            if (messageCategory == MessageCategory.Payment)
                            {
                                transactionUIResponse.ErrorText = "PAYMENT RECOVERY TIMEOUT - PLEASE CHECK PINPAD";
                            }
                            else 
                            {
                                transactionUIResponse.ErrorText = $"{transactionDesc} TIMEOUT";                                
                            }
                            errorRecoveryInProgress = false;
                        }
                    }
                    // otherwise, fail
                    else
                    {
                        transactionUIResponse.ErrorText = r.Response?.AdditionalResponse;
                        errorRecoveryInProgress = false;
                    }
                }
                catch (DataMeshGroup.Fusion.NetworkException ne)
                {
                    ShowTransactionDialog(caption, title, "WAITING FOR CONNECTION...", ne.Message, LightBoxDialogType.Normal, false, true);
                }
                catch (DataMeshGroup.Fusion.TimeoutException)
                {
                    ShowTransactionDialog(caption, title, "TIMEOUT WAITING FOR HOST...", null, LightBoxDialogType.Normal, false, true);
                }
                catch (Exception ex)
                {
                    ShowTransactionDialog(caption, title, "WAITING FOR CONNECTION...", ex.Message, LightBoxDialogType.Normal, false, true);
                }

                if (errorRecoveryInProgress)
                {
                    await Task.Delay(requestDelay);
                }
            }

            UpdateAppState(false);

            // return result
            return transactionUIResponse;
        }

        private void SaveTerminalIndex()
        {
            if (Settings.SelectedTerminalIndex != selectedTerminalIndex)
            {
                Settings.SelectedTerminalIndex = selectedTerminalIndex;
                File.WriteAllText(settingsFilePath, System.Text.Json.JsonSerializer.Serialize<Settings>(Settings));
            }
        }

        private void UpdateButton(Button control, String text)
        {
            if (String.IsNullOrEmpty(text))
            {
                control.Visibility = Visibility.Collapsed;
            }
            else
            {
                control.Content = text;
                control.Visibility = Visibility.Visible;
            }
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
            String cashierReceipt = paymentResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "PAYMENT APPROVED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowStoredValueDialogSuccess(string caption, StoredValueResponse storedValueResponse)
        {
            StoredValueResponse = storedValueResponse;
            if(storedValueResponse != null && (storedValueResponse.StoredValueResult != null))
            {
                StoredValueResult = storedValueResponse.StoredValueResult[0];
            }
            else
            {
                StoredValueResult = new StoredValueResult();
            }

            TxtStoredValueReceipt.Text = storedValueResponse.GetReceiptAsPlainText();
            String cashierReceipt = storedValueResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtStoredValueReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblStoredValueCompleteCaption.Content = caption;
            BorderStoredValueCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));            
            LblStoredValueCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblStoredValueCompleteTitle.Content = $"{transactionDesc} APPROVED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowBalanceInquiryDialogSuccess(string caption, BalanceInquiryResponse balanceInquiryResponse)
        {
            BalanceInquiryResponse = balanceInquiryResponse;
            if (balanceInquiryResponse != null && (balanceInquiryResponse.PaymentAccountStatus != null))
            {
                PaymentAccountStatus = balanceInquiryResponse.PaymentAccountStatus;
            }
            else
            {
                PaymentAccountStatus = new PaymentAccountStatus();
            }

            TxtBalanceInquiryReceipt.Text = balanceInquiryResponse.GetReceiptAsPlainText();
            String cashierReceipt = balanceInquiryResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtBalanceInquiryReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblBalanceInquiryCompleteCaption.Content = caption;
            BorderBalanceInquiryCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));
            LblBalanceInquiryCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblBalanceInquiryCompleteTitle.Content = "BALANCE INQUIRY SUCCESSFUL";            

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowPaymentDialogSuccess(string caption)
        {
            PaymentResponse = null;
            TxtReceipt.Text = "";

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x00));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "GET TOTALS APPROVED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowPaymentDialogFailed(string caption, string displayLine1 = null, string displayText = null, PaymentResponse paymentResponse = null)
        {
            if (paymentResponse == null)
            {
                ShowTransactionDialog(caption, "PAYMENT DECLINED", displayLine1, displayText, LightBoxDialogType.Error, true, false);
                return;
            }

            PaymentResponse = paymentResponse;
            TxtReceipt.Text = paymentResponse.GetReceiptAsPlainText();
            String cashierReceipt = paymentResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblPaymentCompleteCaption.Content = caption;
            BorderPaymentCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA2, 0x00, 0x25));
            LblPaymentCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblPaymentCompleteTitle.Content = "PAYMENT DECLINED";

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowStoredValueDialogFailed(string caption, string displayLine1 = null, string displayText = null, StoredValueResponse storedValueResponse = null)
        {
            string dialogTitle = $"{transactionDesc} DECLINED";            
            if (storedValueResponse == null)
            {
                ShowTransactionDialog(caption, dialogTitle, displayLine1, displayText, LightBoxDialogType.Error, true, false);
                return;
            }

            StoredValueResponse = storedValueResponse;
            TxtStoredValueReceipt.Text = storedValueResponse.GetReceiptAsPlainText();
            String cashierReceipt = storedValueResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblStoredValueCompleteCaption.Content = caption;
            BorderStoredValueCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA2, 0x00, 0x25));
            LblStoredValueCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblStoredValueCompleteTitle.Content = dialogTitle;

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;            
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowBalanceInquiryDialogFailed(string caption, string displayLine1 = null, string displayText = null, BalanceInquiryResponse balanceInquiryResponse = null)
        {
            string dialogTitle = "BALANCE INQUIRY DECLINED";            
            if (balanceInquiryResponse == null)
            {
                ShowTransactionDialog(caption, dialogTitle, displayLine1, displayText, LightBoxDialogType.Error, true, false);
                return;
            }

            BalanceInquiryResponse = balanceInquiryResponse;
            TxtBalanceInquiryReceipt.Text = balanceInquiryResponse.GetReceiptAsPlainText();
            String cashierReceipt = balanceInquiryResponse.GetReceiptAsPlainText(DocumentQualifier.CashierReceipt);
            if (!String.IsNullOrEmpty(cashierReceipt))
            {
                TxtReceipt.Text += "\n----------------\n" + cashierReceipt;
            }

            // Set caption
            LblBalanceInquiryCompleteCaption.Content = caption;
            BorderBalanceInquiryCompleteTitle.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA2, 0x00, 0x25));
            LblBalanceInquiryCompleteTitle.Foreground = new SolidColorBrush(Colors.White);
            LblBalanceInquiryCompleteTitle.Content = dialogTitle;

            // Show/hide views
            GridMain.Visibility = Visibility.Collapsed;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Visible;
        }

        private void ShowPaymentInputDialog(string caption, string title, string displayText, InputCommand inputCommand)
        {       
            ((System.Windows.Media.Animation.Storyboard)FindResource("WaitStoryboard")).Stop();

            currentInput = new Input()
            {
                InputCommand = inputCommand
            };

            // Set caption
            LblPaymentInputDialogCaption.Content = caption;

            // Set title            
            SetLabel(LblPaymentInputDialogTitle, title);
            BorderPaymentInputDialogTitle.Background = new SolidColorBrush(Colors.LightYellow);
            LblPaymentInputDialogTitle.Foreground = new SolidColorBrush(Colors.DarkRed);

            //SetText(TxtPaymentInputDialogText, displayText);
            SetLabel(LblPaymentInputDialogLine1, displayText);

            String btnText1 = null;
            String btnText2 = null;

            switch (inputCommand)
            {
                case InputCommand.GetAnyKey:
                    btnText2 = "OK";
                    break;
                case InputCommand.GetConfirmation:
                    btnText1 = "YES";
                    btnText2 = "NO";
                    break;
            }

            UpdateButton(BtnInputDialogOption1, btnText1);
            UpdateButton(BtnInputDialogOption2, btnText2);

            PaymentInputDialogGrid.Visibility = Visibility.Visible;
        }

        private void ShowPaymentPrintDialog(string caption, string title, string receipt)
        {            
            ((System.Windows.Media.Animation.Storyboard)FindResource("WaitStoryboard")).Stop();
            
            // Set caption
            LblPaymentPrintDialogCaption.Content = caption;

            // Set title            
            SetLabel(LblPaymentPrintDialogTitle, title);
            BorderPaymentPrintDialogTitle.Background = new SolidColorBrush(Colors.LightYellow);
            LblPaymentPrintDialogTitle.Foreground = new SolidColorBrush(Colors.DarkRed);
            
            SetText(TxtReceiptPrint, receipt);
           
            UpdateButton(BtnPrintOption, "OK");

            PaymentPrintDialogGrid.Visibility = Visibility.Visible;
        }

        private void ShowTransactionDialog(string caption, string title, string displayLine1, string displayText, LightBoxDialogType lightBoxDialogType, bool enableOk, bool enableCancel)
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

        private async Task SendResponseToHost(MessagePayload messagePayload)
        {
            // Try cancel of payment if one is in progress, otherwise just close this dialog
            if ((messagePayload != null) && appState.TransactionInProgress && (appState?.MessageHeader is not null))
            {
                try
                {
                    _ = await fusionClient.SendAsync(messagePayload, appState.MessageHeader.ServiceID);
                }
                catch (FusionException)
                {
                    // Throws FusionException when Internet is down
                }
                if (receivedMessageFromHost == RequestMessageFromHost.Input)
                {
                    PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
                } 
                else
                {
                    PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
                }                
            }
            else
            {
                UpdateAppState(false);
                NavigateToMainPage();
            }

            receivedMessageFromHost = RequestMessageFromHost.None;
        }

        #endregion

        #region PaymentComplete

        private void BtnTransactionCompleteOK_Click(object sender, RoutedEventArgs e)
        {
            GridMain.Visibility = Visibility.Visible;
            PaymentDialogGrid.Visibility = Visibility.Collapsed;
            PaymentCompleteGrid.Visibility = Visibility.Collapsed;
            PaymentInputDialogGrid.Visibility = Visibility.Collapsed;
            PaymentPrintDialogGrid.Visibility = Visibility.Collapsed;
            StoredValueCompleteGrid.Visibility = Visibility.Collapsed;
            BalanceInquiryCompleteGrid.Visibility = Visibility.Collapsed;
            //WebBrowserReceipt.Visibility = Visibility.Visible;
        }

        #endregion

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            string paymentTypeName = "LOGOUT";
            ShowTransactionDialog(paymentTypeName, "LOGOUT IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                await CreateFusionClient();
                LogoutResponse r = await fusionClient.SendRecvAsync<LogoutResponse>(new LogoutRequest());
                if (r.Response.Result != Result.Failure)
                {
                    ShowTransactionDialog(paymentTypeName, "LOGOUT SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowTransactionDialog(paymentTypeName, "LOGOUT FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
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
            ShowTransactionDialog(paymentTypeName, "LOGIN IN PROGRESS", "", "", LightBoxDialogType.Normal, false, true);

            try
            {
                await CreateFusionClient();
                LoginResponse r = await fusionClient.SendRecvAsync<LoginResponse>(fusionClient.LoginRequest);
                if (r.Response.Result != Result.Failure)
                {
                    ShowTransactionDialog(paymentTypeName, "LOGIN SUCCESS", "", "", LightBoxDialogType.Success, true, false);
                }
                else
                {
                    ShowTransactionDialog(paymentTypeName, "LOGIN FAILED", r.Response.ErrorCondition.ToString(), r.Response.AdditionalResponse, LightBoxDialogType.Error, true, false);
                }
            }
            catch (Exception ex)
            {
                ShowPaymentDialogFailed(paymentTypeName, null, ex.Message);
            }
        }

        private void CboTerminalSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((CboTerminalSelection.SelectedIndex >= 0) && (selectedTerminalIndex != CboTerminalSelection.SelectedIndex))
            {
                selectedTerminalIndex = CboTerminalSelection.SelectedIndex;
                SaveTerminalIndex();
            }
        }

        private void CboTxnType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MessageCategory updatedTransactionCategory = (CboTxnType.SelectedIndex > 2)? ((CboTxnType.SelectedIndex == 5) ? MessageCategory.BalanceInquiry : MessageCategory.StoredValue) : MessageCategory.Payment;            
            if (updatedTransactionCategory != transactionCategory)
            {
                UpdateTransactionCategory(updatedTransactionCategory);
            }
        }

        private async void BtnDialogOption1_Click(object sender, RoutedEventArgs e)
        {
            MessagePayload messagePayload = null;

            if (receivedMessageFromHost == RequestMessageFromHost.Input)
            {
                if (currentInput.InputCommand == InputCommand.GetConfirmation)
                {
                    currentInput.ConfirmedFlag = true;
                }
                messagePayload = new InputResponse()
                {
                    InputResult = new InputResult()
                    {
                        Input = currentInput
                    }
                };
            }
            
            await SendResponseToHost(messagePayload);

        }

        private async void BtnDialogOption2_Click(object sender, RoutedEventArgs e)
        {
            MessagePayload messagePayload = null;
            if (receivedMessageFromHost == RequestMessageFromHost.Input)
            {
                if (currentInput.InputCommand == InputCommand.GetConfirmation)
                {
                    currentInput.ConfirmedFlag = false;
                }
                else if (currentInput.InputCommand == InputCommand.GetAnyKey)
                {
                    currentInput.TextInput = "OK";
                }
                messagePayload = new InputResponse()
                {
                    InputResult = new InputResult()
                    {
                        Input = currentInput
                    }
                };
            }
            else if(receivedMessageFromHost == RequestMessageFromHost.Print)
            {
                messagePayload = new PrintResponse()
                {
                    Response = new Response()
                    {
                        Result = Result.Success
                    }
                };
            }
            
            await SendResponseToHost(messagePayload);            
        }

        private void UpdateTransactionCategory(MessageCategory updatedTransactionCategory)
        {
            if (updatedTransactionCategory == MessageCategory.StoredValue)
            {
                WPanelPayment.Visibility = Visibility.Collapsed;
                WPanelStoredValue.Visibility = Visibility.Visible;
            }
            else if (updatedTransactionCategory == MessageCategory.Payment)
            {
                WPanelStoredValue.Visibility = Visibility.Collapsed;
                WPanelPayment.Visibility = Visibility.Visible;
            }
            else if (updatedTransactionCategory == MessageCategory.BalanceInquiry)
            {
                WPanelStoredValue.Visibility = Visibility.Collapsed;
                WPanelPayment.Visibility = Visibility.Collapsed;
            }
            transactionCategory = updatedTransactionCategory;
        }

        private string StoredValueTransactionAccountTypeToDisplayText(StoredValueTransactionType? svTransactionType, StoredValueAccountType? svAccountType)
        {
            StringBuilder displayText = new StringBuilder();

            switch (svTransactionType)
            {
                case StoredValueTransactionType.Reserve:
                    displayText.Append("RESERVE ");
                    break;
                case StoredValueTransactionType.Load:
                    displayText.Append("lOAD ");
                    break;
                case StoredValueTransactionType.Unload:
                    displayText.Append("UNLOAD ");
                    break;
                case StoredValueTransactionType.Reverse:
                    displayText.Append("REVERSE ");
                    break;
                case StoredValueTransactionType.Duplicate:
                    displayText.Append("DUPLICATE ");
                    break;
                case null:
                default:
                    displayText.Append("ACTIVATE ");
                    break;
            }

            switch (svAccountType)
            {
                case StoredValueAccountType.PhoneCard:
                    displayText.Append("PHONE CARD");
                    break;
                case StoredValueAccountType.Other:
                    displayText.Append("STORED VALUE CARD");
                    break;
                case null:
                default:
                    displayText.Append("GIFT CARD");
                    break;
            }

            return displayText.ToString();
        }

        /// <summary>
        /// Wrapper for a payment response to be displayed on the UI
        /// </summary>
        public class TransactionUIResponse
        {            
            public TransactionUIResponse(MessageCategory transactionCategory) 
            {
                TransactionCategory = transactionCategory;
            }
            public bool Success
            {
                get
                {
                    return (PaymentResponse?.Response.Success == true) || (StoredValueResponse?.Response.Success == true) || (BalanceInquiryResponse?.Response.Success == true);
                }
            }

            public PaymentResponse PaymentResponse { get; set; }

            public StoredValueResponse StoredValueResponse { get; set; }

            public BalanceInquiryResponse BalanceInquiryResponse { get; set; }

            public MessageCategory TransactionCategory { get; private set; }

            public string TransactionType { get; set; }
            public string ErrorTitle { get; set; }
            public string ErrorText { get; set; }
        }        
    }
}
