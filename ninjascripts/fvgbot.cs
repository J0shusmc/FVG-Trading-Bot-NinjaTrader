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
        private string signalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trade_signals.csv";
        private string tradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trades_taken.csv";
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

        // Position sizing and targets
        private int contractQuantity = 12;  // Total contracts to trade
        private double profitTargetPoints = 5.0;  // Profit target in points
        private double stopLossPoints = 10.0;  // Stop loss in points

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
                SignalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trade_signals.csv";
                TradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trades_taken.csv";
                FileCheckInterval = 2;
                ContractQuantity = 12;
                ProfitTargetPoints = 5.0;
                StopLossPoints = 10.0;
            }
            else if (State == State.Configure)
            {
                // Nothing to configure
            }
            else if (State == State.DataLoaded)
            {
                // FORCE CORRECT FILE PATHS (override any cached config)
                signalsFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trade_signals.csv";
                tradesLogFilePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\trades_taken.csv";

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
                    sw.WriteLine("DateTime,Direction,Entry_Price");
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
                // Parse CSV line: DateTime,Direction,Entry_Price
                string[] parts = line.Split(',');

                if (parts.Length < 3)
                {
                    Print($"ERROR: Invalid signal format (expected 3 fields, got {parts.Length})");
                    return;
                }

                // Create unique signal ID based on datetime and direction
                string signalId = $"{parts[0]}_{parts[1]}";

                // Skip if already processed
                if (processedSignals.Contains(signalId))
                    return;

                // Skip if already in position
                if (Position.MarketPosition != MarketPosition.Flat)
                    return;

                // Parse signal data
                DateTime.TryParse(parts[0].Trim(), out signalDateTime);
                signalDirection = parts[1].Trim();  // LONG or SHORT
                double.TryParse(parts[2].Trim(), out signalEntryPrice);

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
            Print($"[SIGNAL] LONG @ {signalEntryPrice:F2} - Entry: MARKET ({contractQuantity} contracts)");
        }

        private void ExecuteShortEntry()
        {
            EnterShort(contractQuantity, "FVG_Short");
            inPosition = true;
            Print($"[SIGNAL] SHORT @ {signalEntryPrice:F2} - Entry: MARKET ({contractQuantity} contracts)");
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
            // DO NOT INITIALIZE - Python bot owns this file
            // Python creates: DateTime,Direction,Entry_Price
            // C# only reads trade_signals.csv, never writes to trades_taken.csv
        }

        private void LogTradeToFile()
        {
            // DO NOT LOG - Python bot already logged the signal when it was generated
            // Python owns trades_taken.csv for cooldown tracking
            // NinjaTrader only needs to read trade_signals.csv (which C# clears after processing)
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

                    // Set profit targets and stop loss based on ACTUAL fill price
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        double profitTarget = actualEntryPrice + profitTargetPoints;
                        double actualStop = actualEntryPrice - stopLossPoints;

                        // Submit stop loss FIRST as a market order (highest priority)
                        ExitLongStopMarket(0, true, contractQuantity, actualStop, "SL", "FVG_Long");
                        // Then place limit order for profit target
                        ExitLongLimit(0, true, contractQuantity, profitTarget, "PT", "FVG_Long");

                        Print($"[FILLED] LONG {quantity} contracts @ {actualEntryPrice:F2}");
                        Print($"  SL: {actualStop:F2} (-{stopLossPoints:F2}pts)");
                        Print($"  PT: {profitTarget:F2} (+{profitTargetPoints:F2}pts)");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        double profitTarget = actualEntryPrice - profitTargetPoints;
                        double actualStop = actualEntryPrice + stopLossPoints;

                        // Submit stop loss FIRST as a market order (highest priority)
                        ExitShortStopMarket(0, true, contractQuantity, actualStop, "SL", "FVG_Short");
                        // Then place limit order for profit target
                        ExitShortLimit(0, true, contractQuantity, profitTarget, "PT", "FVG_Short");

                        Print($"[FILLED] SHORT {quantity} contracts @ {actualEntryPrice:F2}");
                        Print($"  SL: {actualStop:F2} (+{stopLossPoints:F2}pts)");
                        Print($"  PT: {profitTarget:F2} (-{profitTargetPoints:F2}pts)");
                    }
                }

                // Check if this is an exit order (PT or SL)
                else if (execution.Order.Name == "PT" || execution.Order.Name == "SL")
                {
                    double exitPrice = execution.Price;
                    double pnl = 0;

                    if (execution.Order.Name == "PT")
                    {
                        if (signalDirection.ToUpper() == "LONG")
                            pnl = (exitPrice - actualEntryPrice) * quantity;
                        else
                            pnl = (actualEntryPrice - exitPrice) * quantity;

                        Print($"[EXIT PT] {quantity} contracts @ {exitPrice:F2} | P/L: ${pnl:F2}");
                    }
                    else if (execution.Order.Name == "SL")
                    {
                        if (signalDirection.ToUpper() == "LONG")
                            pnl = (exitPrice - actualEntryPrice) * quantity;
                        else
                            pnl = (actualEntryPrice - exitPrice) * quantity;

                        Print($"[EXIT SL] STOP LOSS @ {exitPrice:F2} | {quantity} contracts | P/L: ${pnl:F2}");
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
        [Display(Name="Contract Quantity", Description="Number of contracts to trade", Order=4, GroupName="FVG Parameters")]
        public int ContractQuantity
        {
            get { return contractQuantity; }
            set { contractQuantity = Math.Max(1, value); }
        }

        [NinjaScriptProperty]
        [Range(1.0, 50.0)]
        [Display(Name="Profit Target (Points)", Description="Profit target in points", Order=5, GroupName="FVG Parameters")]
        public double ProfitTargetPoints
        {
            get { return profitTargetPoints; }
            set { profitTargetPoints = Math.Max(1.0, value); }
        }

        [NinjaScriptProperty]
        [Range(1.0, 50.0)]
        [Display(Name="Stop Loss (Points)", Description="Stop loss in points", Order=6, GroupName="FVG Parameters")]
        public double StopLossPoints
        {
            get { return stopLossPoints; }
            set { stopLossPoints = Math.Max(1.0, value); }
        }

        #endregion
    }
}
