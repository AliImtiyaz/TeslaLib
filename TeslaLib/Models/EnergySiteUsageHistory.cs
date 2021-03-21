﻿using System;
using Newtonsoft.Json;

namespace TeslaLib.Models
{
    public class EnergySiteUsageHistory
    {
        [JsonProperty(PropertyName = "serial_number")]
        public String SerialNumber { get; set; }

        /// <summary>
        /// Looks like this is in Watts
        /// </summary>
        [JsonProperty(PropertyName = "installation_time_zone")]
        public String InstallationTimeZone { get; set; }

        /// <summary>
        /// Power in Watts(?)
        /// </summary>
        [JsonProperty(PropertyName = "time_series")]
        public EnergySiteUsageHistoryData[] TimeSeries { get; set; }
    }
}