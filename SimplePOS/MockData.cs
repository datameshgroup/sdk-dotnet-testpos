using DataMeshGroup.Fusion.Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimplePOS
{
    public class MockData
    {
        [JsonIgnore]
        public List<SaleItem> SaleItems { get; set; }

        public List<PaymentSaleItem> PaymentSaleItems {
            set 
            {
                SaleItems = new List<SaleItem>();
                if (value != null)
                {
                    foreach (PaymentSaleItem item in value)
                    {
                        SaleItem si = new SaleItem();
                        si = new SaleItem() { ProductCode = item.ProductCode, ProductLabel = item.ProductLabel, ItemAmount = item.ItemAmount };
                        si.Categories = new List<string>();
                        si.Category = item.Category;
                        si.SubCategory = item.SubCategory;
                        SaleItems.Add(si);
                    }
                }
            }  
        }

        public class PaymentSaleItem
        {
            public string ProductCode;
            public string ProductLabel;
            public decimal ItemAmount;
            public string Category;
            public string SubCategory;
        }
    }


}
