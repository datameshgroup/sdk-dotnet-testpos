using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimplePOS
{
    public class Settings : INotifyPropertyChanged
    {
        public Settings()
        {
            useTestEnvironment = true;
        }

        private string profileName;
        public string ProfileName
        {
            get => profileName ?? "default";
            set
            {
                if (value != profileName)
                {
                    profileName = value;
                    NotifyPropertyChanged();
                }
            }
        }


        private string saleID;
        public string SaleID
        {
            get => saleID;
            set
            {
                if (value != saleID)
                {
                    saleID = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string poiID;
        public string POIID
        {
            get => poiID;
            set
            {
                if (value != poiID)
                {
                    poiID = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string kek;
        public string KEK
        {
            get => kek;
            set
            {
                if (value != kek)
                {
                    kek = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string providerIdentification;
        public string ProviderIdentification
        {
            get => providerIdentification;
            set
            {
                if (value != providerIdentification)
                {
                    providerIdentification = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string applicationName;
        public string ApplicationName
        {
            get => applicationName;
            set
            {
                if (value != applicationName)
                {
                    applicationName = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string softwareVersion;
        public string SoftwareVersion
        {
            get => softwareVersion;
            set
            {
                if (value != softwareVersion)
                {
                    softwareVersion = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string certificationCode;
        public string CertificationCode
        {
            get => certificationCode;
            set
            {
                if (value != certificationCode)
                {
                    certificationCode = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string operatorID;
        public string OperatorID
        {
            get => operatorID;
            set
            {
                if (value != operatorID)
                {
                    operatorID = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string shiftNumber;
        public string ShiftNumber
        {
            get => shiftNumber;
            set
            {
                if (value != shiftNumber)
                {
                    shiftNumber = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private bool enableLogFile;
        public bool EnableLogFile
        {
            get => enableLogFile;
            set
            {
                if (value != enableLogFile)
                {
                    enableLogFile = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private string customNexoURL;
        public string CustomNexoURL
        {
            get => customNexoURL;
            set
            {
                if (value != customNexoURL)
                {
                    customNexoURL = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private bool useTestEnvironment;
        public bool UseTestEnvironment
        {
            get => useTestEnvironment;
            set
            {
                if (value != useTestEnvironment)
                {
                    useTestEnvironment = value;
                    NotifyPropertyChanged();
                }
            }
        }


        private bool enableVolumeTest;
        public bool EnableVolumeTest
        {
            get => enableVolumeTest;
            set
            {
                if (value != enableVolumeTest)
                {
                    enableVolumeTest = value;
                    NotifyPropertyChanged();
                }
            }
        }

        



        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }


}
