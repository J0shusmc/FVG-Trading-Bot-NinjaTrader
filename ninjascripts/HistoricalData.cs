#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class HistoricalData : Strategy
    {
        private string filePath;
        private bool isFileInitialized = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Historical hourly data feed for FVG detection";
                Name = "HistoricalData";
                Calculate = Calculate.OnBarClose;  // Only write completed bars
                EntriesPerDirection = 1;
                BarsRequiredToTrade = 20;
            }
            else if (State == State.Configure)
            {
                filePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\HistoricalData.csv";
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Safety check to ensure bar data is valid
            if (Bars == null || CurrentBar < 0)
                return;

            if (!isFileInitialized)
            {
                InitializeFile();
                isFileInitialized = true;
            }

            AppendDataToFile();
        }

        private void InitializeFile()
        {
            try
            {
                // Create file with header
                using (StreamWriter writer = new StreamWriter(filePath, false))
                {
                    writer.WriteLine("DateTime,Open,High,Low,Close");
                }
            }
            catch (Exception ex)
            {
                Print($"HistoricalData Error initializing file: {ex.Message}");
            }
        }

        private void AppendDataToFile()
        {
            try
            {
                // Safety checks before accessing bar data
                if (Time.Count == 0 || Open.Count == 0 || High.Count == 0 || Low.Count == 0 || Close.Count == 0)
                    return;

                // Write completed bar to file
                using (StreamWriter writer = new StreamWriter(filePath, true))
                {
                    writer.WriteLine(
                        $"{Time[0]:MM/dd/yyyy HH:mm:ss},{Open[0]:F2},{High[0]:F2},{Low[0]:F2},{Close[0]:F2}"
                    );
                }
            }
            catch (Exception ex)
            {
                Print($"HistoricalData Error writing to file: {ex.Message}");
            }
        }
    }
}
