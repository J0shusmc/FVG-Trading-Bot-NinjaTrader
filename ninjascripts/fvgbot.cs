#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
    public class FVG : Strategy
    {
        #region Variables

        // Signal file monitoring
        private string signalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trade_signals.csv";
        private string tradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trades_taken.csv";
        private DateTime lastFileCheckTime = DateTime.MinValue;
        private HashSet<string> processedSignals = new HashSet<string>();
        private DateTime lastFileModified = DateTime.MinValue;

        // Current signal being processed
        private string currentSignalId = "";
        private double signalEntryPrice = 0;
        private double signalStopLoss = 0;
        private double signalProfitTarget1 = 0;
        private double signalProfitTarget2 = 0;
        private int signalQuantity1 = 0;
        private int signalQuantity2 = 0;
        private string signalDirection = "";
        private string signalType = "";
        private string zoneType = "";
        private double signalATR = 0;
        private DateTime signalDateTime = DateTime.MinValue;

        // Position tracking
        private bool inPosition = false;
        private DateTime entryTime = DateTime.MinValue;
        private double actualEntryPrice = 0;
        private bool firstExitTaken = false;

        // File check interval (seconds)
        private int fileCheckInterval = 2;

        // Position sizing (total contracts)
        private int contractQuantity = 3;

        #endregion
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"FVG Strategy - Receives signals from Python fvg_bot.py";
                Name = "FVG";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 1;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Parameters
                SignalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trade_signals.csv";
                TradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trades_taken.csv";
                FileCheckInterval = 2;
                ContractQuantity = 3;
            }
            else if (State == State.Configure)
            {
                // Nothing to configure
            }
            else if (State == State.DataLoaded)
            {
                // FORCE CORRECT FILE PATHS (override any cached config)
                signalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trade_signals.csv";
                tradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\trades_taken.csv";

                // Initialize processed signals tracking
                processedSignals = new HashSet<string>();

                // Initialize trades log file if it doesn't exist
                InitializeTradesLogFile();

                // Force immediate file check by setting lastFileCheckTime to far past
                lastFileCheckTime = DateTime.MinValue;

                Print($"FVG Strategy Initialized - Monitoring signals every {FileCheckInterval} seconds");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Check for new signals periodically
            if ((DateTime.Now - lastFileCheckTime).TotalSeconds >= FileCheckInterval)
            {
                CheckForNewSignals();
                lastFileCheckTime = DateTime.Now;
            }

            // Manage existing position
            ManagePosition();
        }

        private void CheckForNewSignals()
        {
            if (!File.Exists(signalsFilePath))
                return;

            try
            {
                // Check if file has been modified
                DateTime currentModTime = File.GetLastWriteTime(signalsFilePath);

                if (currentModTime <= lastFileModified)
                    return; // File hasn't changed

                lastFileModified = currentModTime;

                // Read all lines from the CSV file
                string[] lines = File.ReadAllLines(signalsFilePath);

                // Skip header row
                if (lines.Length <= 1)
                    return;

                // Process the last (most recent) signal
                string lastLine = lines[lines.Length - 1];
                ProcessSignalLine(lastLine);

                // Clear the signal file after processing (keep only header)
                ClearSignalsFile();
            }
            catch (Exception ex)
            {
                Print($"ERROR reading signals file: {ex.Message}");
            }
        }

        private void ClearSignalsFile()
        {
            try
            {
                // Rewrite file with only header row
                using (StreamWriter sw = new StreamWriter(signalsFilePath, false))
                {
                    sw.WriteLine("DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target_1,Profit_Target_2,Quantity_1,Quantity_2,Zone_Type,ATR");
                }
            }
            catch (Exception ex)
            {
                Print($"ERROR clearing signals file: {ex.Message}");
            }
        }

        private void ProcessSignalLine(string line)
        {
            try
            {
                // Parse CSV line: DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target_1,Profit_Target_2,Quantity_1,Quantity_2,Zone_Type,ATR
                string[] parts = line.Split(',');

                if (parts.Length < 11)
                {
                    Print($"ERROR: Invalid signal format (expected 11 fields, got {parts.Length})");
                    return;
                }

                // Create unique signal ID based on datetime and direction
                string signalId = $"{parts[0]}_{parts[2]}";

                // Skip if already processed
                if (processedSignals.Contains(signalId))
                    return;

                // Skip if already in position
                if (Position.MarketPosition != MarketPosition.Flat)
                    return;

                // Parse all signal data
                DateTime.TryParse(parts[0].Trim(), out signalDateTime);
                signalType = parts[1].Trim();
                signalDirection = parts[2].Trim();
                double.TryParse(parts[3], out signalEntryPrice);
                double.TryParse(parts[4], out signalStopLoss);
                double.TryParse(parts[5], out signalProfitTarget1);
                double.TryParse(parts[6], out signalProfitTarget2);
                int.TryParse(parts[7], out signalQuantity1);
                int.TryParse(parts[8], out signalQuantity2);
                zoneType = parts[9].Trim();
                double.TryParse(parts[10], out signalATR);

                // Store signal information
                currentSignalId = signalId;
                firstExitTaken = false;

                // Execute trade based on direction
                if (signalDirection.ToUpper() == "LONG")
                {
                    ExecuteLongEntry();
                }
                else if (signalDirection.ToUpper() == "SHORT")
                {
                    ExecuteShortEntry();
                }

                // Mark signal as processed
                processedSignals.Add(signalId);
            }
            catch (Exception ex)
            {
                Print($"ERROR processing signal: {ex.Message}");
            }
        }

        private void ExecuteLongEntry()
        {
            EnterLong(contractQuantity, "FVG_Long");
            inPosition = true;
            Print($"[SIGNAL] LONG {signalType} - Entry: MARKET ({contractQuantity} contracts)");
        }

        private void ExecuteShortEntry()
        {
            EnterShort(contractQuantity, "FVG_Short");
            inPosition = true;
            Print($"[SIGNAL] SHORT {signalType} - Entry: MARKET ({contractQuantity} contracts)");
        }

        private void ManagePosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat && inPosition)
            {
                inPosition = false;
                currentSignalId = "";
            }
        }

        private void InitializeTradesLogFile()
        {
            try
            {
                // Only create header if file doesn't exist
                if (!File.Exists(tradesLogFilePath))
                {
                    using (StreamWriter sw = new StreamWriter(tradesLogFilePath, false))
                    {
                        sw.WriteLine("Entry_DateTime,Signal_DateTime,Signal_Type,Direction,Entry_Price,Stop_Loss,Profit_Target_1,Profit_Target_2,Quantity_1,Quantity_2,Zone_Type,ATR,Actual_Entry_Price");
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"ERROR initializing trades log file: {ex.Message}");
            }
        }

        private void LogTradeToFile()
        {
            try
            {
                // Format: Entry_DateTime,Signal_DateTime,Signal_Type,Direction,Entry_Price,Stop_Loss,Profit_Target_1,Profit_Target_2,Quantity_1,Quantity_2,Zone_Type,ATR,Actual_Entry_Price
                string logEntry = string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8},{9},{10},{11:F2},{12:F2}",
                    DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss"),
                    signalDateTime.ToString("MM/dd/yyyy HH:mm:ss"),
                    signalType,
                    signalDirection,
                    signalEntryPrice,
                    signalStopLoss,
                    signalProfitTarget1,
                    signalProfitTarget2,
                    signalQuantity1,
                    signalQuantity2,
                    zoneType,
                    signalATR,
                    actualEntryPrice
                );

                using (StreamWriter sw = new StreamWriter(tradesLogFilePath, true))
                {
                    sw.WriteLine(logEntry);
                }
            }
            catch (Exception ex)
            {
                Print($"ERROR logging trade to file: {ex.Message}");
            }
        }
        
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
            {
                // Check if this is an entry order
                if (execution.Order.Name == "FVG_Long" || execution.Order.Name == "FVG_Short")
                {
                    actualEntryPrice = execution.Price;
                    entryTime = execution.Time;

                    // Calculate distances from SIGNAL prices
                    double stopDistance = Math.Abs(signalStopLoss - signalEntryPrice);
                    double target1Distance = Math.Abs(signalProfitTarget1 - signalEntryPrice);
                    double target2Distance = Math.Abs(signalProfitTarget2 - signalEntryPrice);

                    // Set profit targets and stop loss based on ACTUAL fill price
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        double actualTarget1 = actualEntryPrice + target1Distance;
                        double actualTarget2 = actualEntryPrice + target2Distance;
                        double actualStop = actualEntryPrice - stopDistance;

                        // Set first profit target for partial exit (e.g., 2 contracts at 5 points)
                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget1, signalQuantity1);
                        // Set second profit target for remaining contract (zone boundary)
                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget2, signalQuantity2);
                        // Set stop loss for all contracts
                        SetStopLoss(execution.Order.Name, CalculationMode.Price, actualStop, false);

                        Print($"[FILLED] LONG {quantity} contracts @ {actualEntryPrice:F2}");
                        Print($"  PT1: {actualTarget1:F2} ({signalQuantity1} contracts, +{target1Distance:F2}pts)");
                        Print($"  PT2: {actualTarget2:F2} ({signalQuantity2} contracts, +{target2Distance:F2}pts)");
                        Print($"  SL: {actualStop:F2} (-{stopDistance:F2}pts)");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        double actualTarget1 = actualEntryPrice - target1Distance;
                        double actualTarget2 = actualEntryPrice - target2Distance;
                        double actualStop = actualEntryPrice + stopDistance;

                        // Set first profit target for partial exit (e.g., 2 contracts at 5 points)
                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget1, signalQuantity1);
                        // Set second profit target for remaining contract (zone boundary)
                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget2, signalQuantity2);
                        // Set stop loss for all contracts
                        SetStopLoss(execution.Order.Name, CalculationMode.Price, actualStop, false);

                        Print($"[FILLED] SHORT {quantity} contracts @ {actualEntryPrice:F2}");
                        Print($"  PT1: {actualTarget1:F2} ({signalQuantity1} contracts, -{target1Distance:F2}pts)");
                        Print($"  PT2: {actualTarget2:F2} ({signalQuantity2} contracts, -{target2Distance:F2}pts)");
                        Print($"  SL: {actualStop:F2} (+{stopDistance:F2}pts)");
                    }

                    // Log the trade to trades_taken.csv
                    LogTradeToFile();
                }

                // Check if this is an exit order (profit target or stop loss)
                else if (execution.Order.Name.Contains("Profit target") || execution.Order.Name.Contains("Stop loss"))
                {
                    double exitPrice = execution.Price;
                    double pnl = 0;

                    if (execution.Order.Name.Contains("Profit target"))
                    {
                        if (signalDirection.ToUpper() == "LONG")
                            pnl = (exitPrice - actualEntryPrice) * quantity;
                        else
                            pnl = (actualEntryPrice - exitPrice) * quantity;

                        if (!firstExitTaken)
                        {
                            Print($"[EXIT 1/{contractQuantity}] PROFIT TARGET @ {exitPrice:F2} | {quantity} contracts | P/L: ${pnl:F2}");
                            firstExitTaken = true;
                        }
                        else
                        {
                            Print($"[EXIT 2/{contractQuantity}] PROFIT TARGET @ {exitPrice:F2} | {quantity} contracts | P/L: ${pnl:F2}");
                        }
                    }
                    else if (execution.Order.Name.Contains("Stop loss"))
                    {
                        if (signalDirection.ToUpper() == "LONG")
                            pnl = (exitPrice - actualEntryPrice) * quantity;
                        else
                            pnl = (actualEntryPrice - exitPrice) * quantity;

                        Print($"[EXIT] STOP LOSS @ {exitPrice:F2} | {quantity} contracts | P/L: ${pnl:F2}");
                    }
                }
            }
        }
        
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (orderState == OrderState.Rejected)
                Print($"[ERROR] Order rejected: {order.Name} - {comment}");
        }
        
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                inPosition = false;
            }
        }
        
        #region Properties

        [NinjaScriptProperty]
        [Display(Name="Signals File Path", Description="Path to trade_signals.csv file", Order=1, GroupName="FVG Parameters")]
        public string SignalsFilePath
        {
            get { return signalsFilePath; }
            set { signalsFilePath = value; }
        }

        [NinjaScriptProperty]
        [Display(Name="Trades Log File Path", Description="Path to trades_taken.csv file", Order=2, GroupName="FVG Parameters")]
        public string TradesLogFilePath
        {
            get { return tradesLogFilePath; }
            set { tradesLogFilePath = value; }
        }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="File Check Interval", Description="Interval in seconds to check for new signals", Order=3, GroupName="FVG Parameters")]
        public int FileCheckInterval
        {
            get { return fileCheckInterval; }
            set { fileCheckInterval = value; }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Contract Quantity", Description="Number of contracts to trade per signal", Order=4, GroupName="FVG Parameters")]
        public int ContractQuantity
        {
            get { return contractQuantity; }
            set { contractQuantity = Math.Max(1, value); }
        }

        #endregion
    }
}
