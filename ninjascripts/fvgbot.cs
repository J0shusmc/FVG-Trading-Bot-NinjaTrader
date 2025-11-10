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
        private double signalProfitTarget = 0;
        private string signalDirection = "";
        private string signalType = "";
        private string zoneType = "";
        private double signalATR = 0;
        private DateTime signalDateTime = DateTime.MinValue;

        // Position tracking
        private bool inPosition = false;
        private DateTime entryTime = DateTime.MinValue;
        private double actualEntryPrice = 0;

        // File check interval (seconds)
        private int fileCheckInterval = 2;

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

                Print($"=== FVG Strategy Initialized ===");
                Print($"Signals File Path: {signalsFilePath}");
                Print($"Trades Log Path: {tradesLogFilePath}");
                Print($"File Check Interval: {FileCheckInterval} seconds");
                Print($"File exists: {File.Exists(signalsFilePath)}");
                Print($"================================");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Check for new signals periodically
            if ((DateTime.Now - lastFileCheckTime).TotalSeconds >= FileCheckInterval)
            {
                Print($"[{DateTime.Now:HH:mm:ss}] Checking for new signals...");
                CheckForNewSignals();
                lastFileCheckTime = DateTime.Now;
            }

            // Manage existing position
            ManagePosition();
        }

        private void CheckForNewSignals()
        {
            Print($"CheckForNewSignals() called - checking file: {signalsFilePath}");

            if (!File.Exists(signalsFilePath))
            {
                Print($"ERROR: Signals file does not exist: {signalsFilePath}");
                return;
            }

            Print($"Signals file exists, checking modification time...");

            try
            {
                // Check if file has been modified
                DateTime currentModTime = File.GetLastWriteTime(signalsFilePath);
                Print($"File last modified: {currentModTime:MM/dd/yyyy HH:mm:ss}");
                Print($"Last processed time: {lastFileModified:MM/dd/yyyy HH:mm:ss}");

                if (currentModTime <= lastFileModified)
                {
                    Print($"File has not changed since last check - skipping");
                    return; // File hasn't changed
                }

                lastFileModified = currentModTime;
                Print($"File HAS been modified - reading signals...");

                // Read all lines from the CSV file
                string[] lines = File.ReadAllLines(signalsFilePath);
                Print($"Read {lines.Length} lines from file");

                // Skip header row
                if (lines.Length <= 1)
                {
                    Print($"No signals found (only header row)");
                    return;
                }

                // Process the last (most recent) signal
                string lastLine = lines[lines.Length - 1];
                Print($"Processing signal: {lastLine}");
                ProcessSignalLine(lastLine);

                // Clear the signal file after processing (keep only header)
                ClearSignalsFile();
            }
            catch (Exception ex)
            {
                Print($"ERROR reading signals file: {ex.Message}");
                Print($"Stack trace: {ex.StackTrace}");
            }
        }

        private void ClearSignalsFile()
        {
            try
            {
                // Rewrite file with only header row
                using (StreamWriter sw = new StreamWriter(signalsFilePath, false))
                {
                    sw.WriteLine("DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR");
                }
                Print($"Signal file cleared - ready for new signals");
            }
            catch (Exception ex)
            {
                Print($"ERROR clearing signals file: {ex.Message}");
            }
        }

        private void ProcessSignalLine(string line)
        {
            Print($"=== ProcessSignalLine START ===");
            try
            {
                // Parse CSV line: DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR
                string[] parts = line.Split(',');
                Print($"Split into {parts.Length} parts");

                if (parts.Length < 8)
                {
                    Print($"ERROR: Not enough parts in CSV line (expected 8, got {parts.Length})");
                    return;
                }

                // Create unique signal ID based on datetime and direction
                string signalId = $"{parts[0]}_{parts[2]}";
                Print($"Signal ID: {signalId}");

                // Skip if already processed
                if (processedSignals.Contains(signalId))
                {
                    Print($"SKIPPED: Signal already processed (ID: {signalId})");
                    return;
                }

                Print($"Current position: {Position.MarketPosition}");

                // Skip if already in position
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    Print($"SKIPPED: Already in position ({Position.MarketPosition})");
                    return;
                }

                // Parse all signal data
                DateTime.TryParse(parts[0].Trim(), out signalDateTime);
                signalType = parts[1].Trim();
                signalDirection = parts[2].Trim();
                double.TryParse(parts[3], out signalEntryPrice);
                double.TryParse(parts[4], out signalStopLoss);
                double.TryParse(parts[5], out signalProfitTarget);
                zoneType = parts[6].Trim();
                double.TryParse(parts[7], out signalATR);

                Print($"Parsed signal: {signalDirection} @ {signalEntryPrice}, SL: {signalStopLoss}, PT: {signalProfitTarget}");

                // Store signal information
                currentSignalId = signalId;

                // Execute trade based on direction
                if (signalDirection.ToUpper() == "LONG")
                {
                    Print($"Executing LONG entry...");
                    ExecuteLongEntry();
                }
                else if (signalDirection.ToUpper() == "SHORT")
                {
                    Print($"Executing SHORT entry...");
                    ExecuteShortEntry();
                }

                // Mark signal as processed
                processedSignals.Add(signalId);
                Print($"Signal marked as processed: {signalId}");
            }
            catch (Exception ex)
            {
                Print($"ERROR processing signal: {ex.Message}");
                Print($"Stack trace: {ex.StackTrace}");
            }
            Print($"=== ProcessSignalLine END ===");
        }

        private void ExecuteLongEntry()
        {
            Print($"=== LONG ENTRY DIAGNOSTICS ===");
            Print($"Instrument: {Instrument.FullName}");
            Print($"Current Bar Time: {Time[0]:MM/dd/yyyy HH:mm:ss}");
            Print($"Current Bar Close: {Close[0]:F2}");
            Print($"State: {State}");
            Print($"Historical: {Historical}");
            Print($"Signal Entry Price: {signalEntryPrice:F2}");
            Print($"================================");

            EnterLong(1, "FVG_Long");
            inPosition = true;
            Print($"LONG Signal Received - Entry: MARKET, SL: {signalStopLoss:F2}, PT: {signalProfitTarget:F2}");
        }

        private void ExecuteShortEntry()
        {
            Print($"=== SHORT ENTRY DIAGNOSTICS ===");
            Print($"Instrument: {Instrument.FullName}");
            Print($"Current Bar Time: {Time[0]:MM/dd/yyyy HH:mm:ss}");
            Print($"Current Bar Close: {Close[0]:F2}");
            Print($"State: {State}");
            Print($"Historical: {Historical}");
            Print($"Signal Entry Price: {signalEntryPrice:F2}");
            Print($"================================");

            EnterShort(1, "FVG_Short");
            inPosition = true;
            Print($"SHORT Signal Received - Entry: MARKET, SL: {signalStopLoss:F2}, PT: {signalProfitTarget:F2}");
        }

        private void ManagePosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat && inPosition)
            {
                inPosition = false;
                currentSignalId = "";
                Print("Position closed");
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
                        sw.WriteLine("Entry_DateTime,Signal_DateTime,Signal_Type,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR,Actual_Entry_Price");
                    }
                    Print($"Created trades log file: {tradesLogFilePath}");
                }
                else
                {
                    Print($"Using existing trades log file: {tradesLogFilePath}");
                }
            }
            catch (Exception ex)
            {
                Print($"Error initializing trades log file: {ex.Message}");
            }
        }

        private void LogTradeToFile()
        {
            try
            {
                // Format: Entry_DateTime,Signal_DateTime,Signal_Type,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR,Actual_Entry_Price
                // Note: We don't have Zone_Bottom/Zone_Top in the signal, so we skip them
                string logEntry = string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8:F2},{9:F2}",
                    DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss"),  // Use actual current time for entry
                    signalDateTime.ToString("MM/dd/yyyy HH:mm:ss"),
                    signalType,
                    signalDirection,
                    signalEntryPrice,
                    signalStopLoss,
                    signalProfitTarget,
                    zoneType,
                    signalATR,
                    actualEntryPrice
                );

                using (StreamWriter sw = new StreamWriter(tradesLogFilePath, true))
                {
                    sw.WriteLine(logEntry);
                }

                Print($"Trade logged: Entry={actualEntryPrice:F2}, SL={signalStopLoss:F2}, PT={signalProfitTarget:F2}");
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

                    // Calculate stop/target distances from SIGNAL prices
                    double stopDistance = Math.Abs(signalStopLoss - signalEntryPrice);
                    double targetDistance = Math.Abs(signalProfitTarget - signalEntryPrice);

                    Print($"Stop Distance: {stopDistance:F2}, Target Distance: {targetDistance:F2}");

                    // Set stop loss and profit target based on ACTUAL fill price
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        double actualTarget = actualEntryPrice + targetDistance;
                        double actualStop = actualEntryPrice - stopDistance;

                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget);
                        SetStopLoss(execution.Order.Name, CalculationMode.Price, actualStop, false);
                        Print($"LONG Filled at {actualEntryPrice:F2} - Target: {actualTarget:F2}, Stop: {actualStop:F2}");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        double actualTarget = actualEntryPrice - targetDistance;
                        double actualStop = actualEntryPrice + stopDistance;

                        SetProfitTarget(execution.Order.Name, CalculationMode.Price, actualTarget);
                        SetStopLoss(execution.Order.Name, CalculationMode.Price, actualStop, false);
                        Print($"SHORT Filled at {actualEntryPrice:F2} - Target: {actualTarget:F2}, Stop: {actualStop:F2}");
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

                        Print($"PROFIT TARGET Hit at {exitPrice:F2} - P/L: ${pnl:F2}");
                    }
                    else if (execution.Order.Name.Contains("Stop loss"))
                    {
                        if (signalDirection.ToUpper() == "LONG")
                            pnl = (exitPrice - actualEntryPrice) * quantity;
                        else
                            pnl = (actualEntryPrice - exitPrice) * quantity;

                        Print($"STOP LOSS Hit at {exitPrice:F2} - P/L: ${pnl:F2}");
                    }
                }
            }
        }
        
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (orderState == OrderState.Filled)
                Print($"{order.Name} filled at {averageFillPrice:F2}");
            else if (orderState == OrderState.Rejected)
                Print($"Order rejected: {order.Name} - {comment}");
        }
        
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                Print("Position flat");
                inPosition = false;
            }
            else
            {
                Print($"Position: {marketPosition}, Qty: {quantity}, Avg Price: {averagePrice:F2}");
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

        #endregion
    }
}
