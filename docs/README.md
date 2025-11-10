# FVG Trading Bot - NinjaTrader

A real-time Fair Value Gap (FVG) trading bot that integrates with NinjaTrader for automated gap detection and trade signal generation.

## 📊 What is Fair Value Gap (FVG) Trading?

Fair Value Gaps (FVGs) are **price inefficiencies** in the market where price moves too quickly, leaving a "gap" between candles. These gaps often get "filled" as the market returns to trade at those missed prices, creating high-probability trading opportunities.

### FVG Types

**Bullish FVG (Gap Up)**
- Occurs when Candle 3's LOW > Candle 1's HIGH
- Creates a price zone between these two levels
- Market tends to fill gaps by moving DOWN
- **Trading Strategy**: SHORT when price returns to the gap (sell at zone top)

**Bearish FVG (Gap Down)**
- Occurs when Candle 3's HIGH < Candle 1's LOW
- Creates a price zone between these two levels
- Market tends to fill gaps by moving UP
- **Trading Strategy**: LONG when price returns to the gap (buy at zone bottom)

## 🎯 Strategy Overview

### How It Works

1. **FVG Detection (Hourly Timeframe)**
   - Monitors completed 1-hour bars for fair value gaps
   - Identifies gaps with minimum size of 2.5 points
   - Tracks active unfilled gaps for trading opportunities

2. **Real-Time Price Monitoring**
   - Uses live price feed to detect gap retests
   - Generates limit order signals when price approaches gaps (within 2.5 points)
   - Automatically calculates entry, stop-loss, and profit targets

3. **Trade Signal Generation**
   - **LONG Signals**: Generated when price retests BEARISH gaps (gap down)
     - Entry: Zone bottom
     - Stop Loss: Below gap by gap size
     - Profit Target: Zone top

   - **SHORT Signals**: Generated when price retests BULLISH gaps (gap up)
     - Entry: Zone top
     - Stop Loss: Above gap by gap size
     - Profit Target: Zone bottom

4. **Risk Management**
   - Positions sized at 1 contract per signal
   - Stop losses placed 1x gap size beyond entry
   - Profit targets set at opposite end of gap zone
   - Gaps automatically invalidated after being filled or after 100 bars

## 🖥️ NinjaTrader Setup Requirements

### CRITICAL: Dual Chart Configuration

This bot requires **TWO SEPARATE CHARTS** in NinjaTrader, each running a different strategy:

#### Chart 1: Historical Data (Hourly Timeframe)
```
Instrument: MES (or your chosen instrument)
Timeframe: 1 Hour
Strategy: HistoricalData.cs
Purpose: Exports completed hourly bars for FVG detection
Output: data/HistoricalData.csv
Calculate: OnBarClose (only completed bars)
```

**Configuration:**
1. Open a new chart with your instrument (e.g., MES 03-25)
2. Set timeframe to **1 Hour**
3. Right-click chart → Strategies → Add HistoricalData strategy
4. This chart writes each completed hourly bar to `HistoricalData.csv`

#### Chart 2: Live Feed (Tick/Minute Timeframe)
```
Instrument: MES (same as Chart 1)
Timeframe: 1 Tick or 1 Minute (for real-time updates)
Strategy: LiveFeed.cs
Purpose: Exports real-time price data for entry detection
Output: data/LiveFeed.csv
Calculate: OnEachTick or OnPriceChange
```

**Configuration:**
1. Open a second chart with the **same instrument**
2. Set timeframe to **1 Tick** or **1 Minute** (for rapid updates)
3. Right-click chart → Strategies → Add LiveFeed strategy
4. This chart continuously updates current price in `LiveFeed.csv`

### Why Two Charts?

- **Historical Chart**: Provides stable, completed hourly bars for accurate FVG detection
- **Live Feed Chart**: Provides real-time tick-by-tick or minute-by-minute price for instant retest detection
- **Separation of Concerns**: Prevents mixing of timeframes and ensures clean data for each purpose

## 📁 Project Structure

```
FVG Bot/
├── fvg_bot.py              # Main Python bot (FVG detection & signals)
├── requirements.txt         # Python dependencies
├── trade_signals.csv        # Generated trade signals (output)
│
├── ninjascripts/            # NinjaTrader C# strategies
│   ├── HistoricalData.cs   # Hourly bar export strategy
│   ├── LiveFeed.cs         # Real-time price feed strategy
│   └── fvgbot.cs           # (Optional) Automated order execution
│
├── data/                    # CSV data files (created by NinjaTrader)
│   ├── HistoricalData.csv  # Hourly OHLC bars
│   └── LiveFeed.csv        # Real-time price updates
│
└── docs/
    └── README.md           # This file
```

## 🚀 Installation & Setup

### 1. Install Python Dependencies

```bash
# Create virtual environment (recommended)
python -m venv venv
venv\Scripts\activate  # Windows
source venv/bin/activate  # Linux/Mac

# Install dependencies
pip install -r requirements.txt
```

### 2. Install NinjaTrader Strategies

1. Copy all `.cs` files from `ninjascripts/` folder
2. In NinjaTrader, go to **Tools → Edit NinjaScript → Strategy**
3. Click **Import** and select the `.cs` files
4. Compile the strategies (F5 or click Compile)

### 3. Configure NinjaTrader Charts

**Chart 1 - Historical Data:**
```
1. New Chart → Select instrument (MES)
2. Data Series → 1 Hour
3. Right-click → Strategies → Add "HistoricalData"
4. Verify path: C:\Users\[YourName]\Documents\Projects\FVG Bot\data\HistoricalData.csv
5. Enable the strategy
```

**Chart 2 - Live Feed:**
```
1. New Chart → Select SAME instrument (MES)
2. Data Series → 1 Tick (or 1 Minute)
3. Right-click → Strategies → Add "LiveFeed"
4. Verify path: C:\Users\[YourName]\Documents\Projects\FVG Bot\data\LiveFeed.csv
5. Enable the strategy
```

### 4. Create Data Directory

```bash
mkdir data
```

The NinjaTrader strategies will automatically create the CSV files in this directory.

### 5. Run the Python Bot

```bash
python fvg_bot.py
```

## 📊 How to Use

1. **Start NinjaTrader** and ensure both charts are running with strategies enabled
2. **Run the Python bot**: `python fvg_bot.py`
3. **Monitor the console** for:
   - Active FVG zones (bullish and bearish)
   - Current price and distance to zones
   - Trade signal generation
4. **Check `trade_signals.csv`** for generated entry signals with:
   - Entry price (limit orders)
   - Stop loss levels
   - Profit targets
   - Signal timestamp

## 📈 Trade Signal Format

Signals are written to `trade_signals.csv` with the following columns:

| Column | Description |
|--------|-------------|
| DateTime | Signal generation timestamp |
| Signal | Signal type (e.g., FVG_RETEST) |
| Direction | LONG or SHORT |
| Entry_Price | Limit order entry price |
| Stop_Loss | Stop loss price |
| Profit_Target | Take profit price |
| Zone_Type | bullish or bearish |
| ATR | Gap size in points |

### Example Signal

```csv
DateTime,Signal,Direction,Entry_Price,Stop_Loss,Profit_Target,Zone_Type,ATR
11/09/2025 14:32:15,FVG_RETEST,LONG,5850.25,5845.75,5854.75,bearish,4.50
```

**Interpretation**:
- LONG entry at 5850.25 (zone bottom)
- Stop at 5845.75 (risk: 4.50 pts)
- Target at 5854.75 (reward: 4.50 pts)
- 1:1 risk-reward ratio

## ⚙️ Configuration

### Python Bot (fvg_bot.py)

```python
# Default configuration
instrument = 'MES'                          # Trading instrument
historical_path = 'data/HistoricalData.csv' # Hourly bars
live_feed_path = 'data/LiveFeed.csv'       # Real-time price
signals_path = 'trade_signals.csv'         # Output signals
max_position_size = 1                       # Contracts per trade
```

### NinjaScript (HistoricalData.cs / LiveFeed.cs)

```csharp
// Update file paths to match your system
filePath = @"C:\Users\Joshua\Documents\Projects\FVG Bot\data\HistoricalData.csv";
```

## 🎯 Strategy Parameters

| Parameter | Value | Description |
|-----------|-------|-------------|
| Min Gap Size | 2.5 points | Minimum FVG size to trade |
| Retest Distance | 2.5 points | Trigger distance to zone |
| Max FVG Age | 100 bars | Auto-invalidate old gaps |
| Position Size | 1 contract | Per signal |
| Risk/Reward | 1:1 | Gap size for both |

## 🔧 Troubleshooting

### No FVGs Detected
- Ensure historical chart has at least 20 completed hourly bars
- Check minimum gap size (2.5 points) - reduce if needed
- Verify HistoricalData.csv is being updated

### No Trade Signals Generated
- Confirm LiveFeed.csv is updating in real-time
- Check if price is within 2.5 points of active gaps
- Ensure gaps haven't been marked as filled or trade_taken

### CSV Files Not Created
- Verify NinjaTrader strategies are enabled
- Check file paths in .cs files match your system
- Ensure data/ directory exists
- Check NinjaTrader Output window for errors

### Bot Not Updating
- Verify both CSV files exist and contain data
- Check file modification timestamps
- Restart bot to reload historical FVGs

## 📝 Notes

- **Timeframe**: FVG detection uses 1-hour bars; live monitoring uses tick/minute data
- **Instrument**: Default configured for MES futures (Micro E-mini S&P 500)
- **Trading Hours**: Bot runs 24/5 with market hours
- **Data Persistence**: Signals saved to CSV for manual or automated execution
- **No Auto-Execution**: Bot generates signals only; execution requires manual order placement or separate automation

## 🛡️ Risk Disclaimer

This bot is for **educational and research purposes only**. Trading futures and derivatives carries substantial risk of loss. Always:
- Test thoroughly on paper/simulation before live trading
- Understand the strategy and its limitations
- Use appropriate position sizing and risk management
- Never risk more than you can afford to lose

## 📞 Support

For issues or questions:
1. Check NinjaTrader Output window for strategy errors
2. Review Python bot console logs for debugging
3. Verify CSV file paths and permissions
4. Ensure both charts are running simultaneously

## 📄 License

MIT License - See LICENSE file for details

---

**Remember**: Both NinjaTrader charts (hourly historical + live feed) must be running simultaneously for the bot to function correctly!
