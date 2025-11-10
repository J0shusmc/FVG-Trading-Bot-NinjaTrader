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
        private string signalsFilePath = @"trade_signals.csv";
        private DateTime lastFileCheckTime = DateTime.MinValue;
        private HashSet<string> processedSignals = new HashSet<string>();
        private DateTime lastFileModified = DateTime.MinValue;

        // Current signal being processed
        private string currentSignalId = "";
        private double signalEntryPrice = 0;
        private double signalStopLoss = 0;
        private double signalProfitTarget = 0;
        private string signalDirection = "";

        // Position tracking
        private bool inPosition = false;

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
                SignalsFilePath = @"trade_signals.csv";
                FileCheckInterval = 2;
            }
            else if (State == State.Configure)
            {
                // Nothing to configure
            }
            else if (State == State.DataLoaded)
            {
                // Initialize processed signals tracking
                processedSignals = new HashSet<string>();
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
            if (!File.Exists(SignalsFilePath))
            {
                return;
            }

            try
            {
                // Check if file has been modified
                DateTime currentModTime = File.GetLastWriteTime(SignalsFilePath);
                if (currentModTime <= lastFileModified)
                {
                    return; // File hasn't changed
                }

                lastFileModified = currentModTime;

                // Read all lines from the CSV file
                string[] lines = File.ReadAllLines(SignalsFilePath);

                // Skip header row
                if (lines.Length <= 1)
                    return;

                // Process the last (most recent) signal
                string lastLine = lines[lines.Length - 1];
                ProcessSignalLine(lastLine);
            }
            catch (Exception ex)
            {
                Print($"Error reading signals file: {ex.Message}");
            }
        }

        private void ProcessSignalLine(string line)
        {
            try
            {
                // Parse CSV line: DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR
                string[] parts = line.Split(',');

                if (parts.Length < 8)
                    return;

                // Create unique signal ID based on datetime and direction
                string signalId = $"{parts[0]}_{parts[2]}";

                // Skip if already processed
                if (processedSignals.Contains(signalId))
                    return;

                // Skip if already in position
                if (Position.MarketPosition != MarketPosition.Flat)
                    return;

                string direction = parts[2].Trim();
                double entryPrice = double.Parse(parts[3]);
                double stopLoss = double.Parse(parts[4]);
                double profitTarget = double.Parse(parts[5]);

                // Store signal information
                currentSignalId = signalId;
                signalEntryPrice = entryPrice;
                signalStopLoss = stopLoss;
                signalProfitTarget = profitTarget;
                signalDirection = direction;

                // Execute trade based on direction
                if (direction.ToUpper() == "LONG")
                {
                    ExecuteLongEntry();
                }
                else if (direction.ToUpper() == "SHORT")
                {
                    ExecuteShortEntry();
                }

                // Mark signal as processed
                processedSignals.Add(signalId);
            }
            catch (Exception ex)
            {
                Print($"Error processing signal: {ex.Message}");
            }
        }

        private void ExecuteLongEntry()
        {
            EnterLongLimit(1, signalEntryPrice, "FVG_Long");
            inPosition = true;
            Print($"LONG Signal Received - Entry: {signalEntryPrice:F2}, SL: {signalStopLoss:F2}, PT: {signalProfitTarget:F2}");
        }

        private void ExecuteShortEntry()
        {
            EnterShortLimit(1, signalEntryPrice, "FVG_Short");
            inPosition = true;
            Print($"SHORT Signal Received - Entry: {signalEntryPrice:F2}, SL: {signalStopLoss:F2}, PT: {signalProfitTarget:F2}");
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
        
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
            {
                double avgPrice = Position.AveragePrice;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    SetProfitTarget(CalculationMode.Price, signalProfitTarget);
                    SetStopLoss(CalculationMode.Price, signalStopLoss);
                    Print($"LONG Filled at {avgPrice:F2} - Target: {signalProfitTarget:F2}, Stop: {signalStopLoss:F2}");
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    SetProfitTarget(CalculationMode.Price, signalProfitTarget);
                    SetStopLoss(CalculationMode.Price, signalStopLoss);
                    Print($"SHORT Filled at {avgPrice:F2} - Target: {signalProfitTarget:F2}, Stop: {signalStopLoss:F2}");
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
        [Range(1, 60)]
        [Display(Name="File Check Interval", Description="Interval in seconds to check for new signals", Order=2, GroupName="FVG Parameters")]
        public int FileCheckInterval
        {
            get { return fileCheckInterval; }
            set { fileCheckInterval = value; }
        }

        #endregion
    }
}
