﻿using DataMeshGroup.Fusion.Model;
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

        public List<SaleItem> PaymentSaleItems {
            set 
            {
                SaleItems = new List<SaleItem>();
                if (value != null)
                {
                    foreach (SaleItem item in value)
                    {
                        SaleItems.Add(item);
                    }
                }
            }  
        }
    }
}
