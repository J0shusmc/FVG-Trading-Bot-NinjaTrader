# FVG Trading Bot for NinjaTrader

Automated Fair Value Gap (FVG) trading system using Python for signal detection and NinjaTrader for order execution.

## System Architecture

### Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                   NinjaTrader Strategies                     │
│  (Data Feed Providers - Export to CSV)                      │
│  • HistoricalData.cs - Exports hourly bars                  │
│  • LiveFeed.cs - Exports real-time tick data                │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
         ┌────────────────┐
         │   data folder   │
         │  • HistoricalData.csv                               │
         │  • LiveFeed.csv                                     │
         └────────┬───────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│                  fvg_bot.py (CORE ENGINE)                    │
│  • Detects Fair Value Gaps from hourly bars                 │
│  • Monitors live price for zone entries                     │
│  • Manages zone cooldowns (60 min)                          │
│  • Sends signals to trade_signals.csv                       │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
         ┌────────────────┐
         │ trade_signals.csv                                   │
         │ (DateTime, Signal, Direction)                       │
         └────────┬───────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│            fvgbot.cs (NinjaTrader Strategy)                  │
│  • Reads signals from CSV                                   │
│  • Executes market orders                                   │
│  • Sets stop loss (10 points)                               │
│  • Sets profit target (5 points)                            │
│  • Manages position size                                    │
└─────────────────────────────────────────────────────────────┘
```

## Components

### 1. **fvg_bot.py** - Signal Detection Engine
**Location:** `fvg_bot.py`

**Responsibilities:**
- Scans hourly bars for Fair Value Gaps (minimum 2.5 points)
- Tracks active FVG zones (up to 100 bars old)
- Monitors real-time price for zone entries
- Enforces 60-minute cooldown per zone
- Writes signals to `data/trade_signals.csv`

**Signal Format:**
```csv
DateTime,Signal,Direction
01/15/2025 14:30:00,FVG_RETEST,LONG
```

**Key Features:**
- Bullish FVG → SHORT signal when price enters zone
- Bearish FVG → LONG signal when price enters zone
- Zone cooldown prevents re-trading same zone
- Real-time monitoring (1-second refresh)

---

### 2. **fvgbot.cs** - NinjaTrader Execution Strategy
**Location:** `ninjascripts/fvgbot.cs`

**Responsibilities:**
- Reads `data/trade_signals.csv` every 2 seconds
- Executes market entry orders
- Sets stop loss at **10 points** from fill price
- Sets profit target at **5 points** from fill price
- Logs actual fills to `trades_taken.csv`

**Configurable Parameters:**
- **Contract Quantity:** Number of contracts to trade (default: 12)
- **Profit Target:** Points from entry (default: 5.0)
- **Stop Loss:** Points from entry (default: 10.0)
- **File Check Interval:** Seconds between CSV checks (default: 2)

**Order Management:**
```
Entry: Market Order (quantity from parameter)
Stop Loss: 10 points from ACTUAL fill price
Profit Target: 5 points from ACTUAL fill price
```

---

### 3. **NinjaTrader Data Providers**
**Location:** `ninjascripts/` (HistoricalData.cs, LiveFeed.cs)

**Purpose:**
Feed data to Python bot by exporting:
- **HistoricalData.csv** - Hourly OHLC bars
- **LiveFeed.csv** - Real-time tick data

These strategies run continuously in NinjaTrader to provide data.

---

## Data Flow

### Signal Generation Flow
1. **HistoricalData.cs** exports hourly bars → `data/HistoricalData.csv`
2. **LiveFeed.cs** exports live ticks → `data/LiveFeed.csv`
3. **fvg_bot.py** scans for FVG zones, monitors price
4. When price enters zone → writes to `data/trade_signals.csv`
5. **fvgbot.cs** reads signal → executes trade
6. **fvgbot.cs** sets stops/targets → logs to `trades_taken.csv`

### CSV File Structure

**trade_signals.csv** (Cleared on fvg_bot.py startup)
```csv
DateTime,Signal,Direction
01/15/2025 14:30:00,FVG_RETEST,LONG
```

**trades_taken.csv** (Python log - zone tracking)
```csv
Signal_DateTime,Signal_Type,Direction,Zone_Bottom,Zone_Top,Gap_Size
01/15/2025 14:30:00,FVG_RETEST,LONG,5823.50,5828.25,4.75
```

**trades_taken.csv** (NinjaTrader log - actual fills)
```csv
Entry_DateTime,Signal_DateTime,Signal_Type,Direction,Actual_Entry_Price,Stop_Loss,Profit_Target,Quantity
01/15/2025 14:30:15,01/15/2025 14:30:00,FVG_RETEST,LONG,5825.00,5815.00,5830.00,12
```

---

## Installation & Setup

### 1. Install Python Bot
```bash
cd "C:\Users\Joshua\Documents\Projects\FVG Bot"
pip install pandas numpy
```

### 2. Install NinjaTrader Strategies
1. Copy all `.cs` files from `ninjascripts/` to NinjaTrader's strategies folder
2. Compile in NinjaTrader (Tools → Compile)
3. Add strategies to chart:
   - **HistoricalData** - 1 Hour chart for data export
   - **LiveFeed** - Tick chart for real-time data
   - **FVG** - Main execution strategy (any timeframe)

### 3. Configure Paths
Ensure these folders exist:
```
C:\Users\Joshua\Documents\Projects\FVG Bot\data\
```

---

## Running the System

### Start Sequence
1. **NinjaTrader:** Start `HistoricalData` and `LiveFeed` strategies
2. **Python:** Run `python fvg_bot.py`
3. **NinjaTrader:** Start `FVG` strategy

### Stop Sequence
1. Stop `FVG` strategy (lets positions close)
2. Stop `fvg_bot.py` (Ctrl+C)
3. Stop data feed strategies

---

## Configuration

### Python Bot (`fvg_bot.py`)
```python
self.zone_cooldown_minutes = 60  # Cooldown per zone
# Minimum FVG gap size: 2.5 points (hardcoded in find_fvgs_in_data)
```

### NinjaTrader Strategy (`fvgbot.cs`)
Access via strategy parameters in NinjaTrader:
- **Contract Quantity:** 1-100 contracts
- **Profit Target:** 1.0-50.0 points
- **Stop Loss:** 1.0-50.0 points
- **File Check Interval:** 1-60 seconds

---

## Trading Rules

### Fair Value Gap Detection
- **Minimum Gap:** 2.5 points
- **Bullish FVG:** Gap between candle1.High and candle3.Low
- **Bearish FVG:** Gap between candle3.High and candle1.Low

### Entry Rules
- **Bullish Zone (SHORT):** Price enters from above
- **Bearish Zone (LONG):** Price enters from below
- **Cooldown:** 60 minutes per zone after signal

### Exit Rules (NinjaTrader Handles)
- **Profit Target:** 5 points from entry
- **Stop Loss:** 10 points from entry
- **Risk:Reward:** 1:2 (10pt risk, 5pt reward)

---

## Monitoring

### Python Bot Console
- Real-time FVG zones display
- Distance to nearest zones
- Signal generation alerts

### NinjaTrader Output
- Entry fills with actual prices
- Stop/target placement
- Exit fills (PT or SL)

### Log Files
- `trades_taken.csv` - Python signal log
- `trades_taken.csv` - NinjaTrader execution log

---

## Troubleshooting

### No Signals Generated
1. Check `data/HistoricalData.csv` has recent data
2. Verify FVG zones exist (check Python console)
3. Ensure price is within zone boundaries

### Signals Not Executing
1. Verify `fvgbot.cs` is running in NinjaTrader
2. Check file paths in strategy parameters
3. Review NinjaTrader Output window for errors

### Multiple Signals for Same Zone
- Check cooldown period (60 min default)
- Verify `trades_taken.csv` has correct timestamps

---

## File Structure
```
FVG Bot/
├── fvg_bot.py                 # Main signal detection engine
├── data/
│   ├── HistoricalData.csv     # Hourly bars (from NinjaTrader)
│   ├── LiveFeed.csv           # Real-time ticks (from NinjaTrader)
│   ├── trade_signals.csv      # Signals to NinjaTrader
│   └── trades_taken.csv       # Trade logs
├── ninjascripts/
│   ├── fvgbot.cs              # Main execution strategy
│   ├── HistoricalData.cs      # Hourly data exporter
│   └── LiveFeed.cs            # Live tick exporter
└── README.md
```

---

## Performance Notes

- **Latency:** ~2-3 seconds from signal to execution
- **Update Frequency:** Python checks every 1 second
- **File Check:** NinjaTrader checks CSV every 2 seconds
- **Zone Lifespan:** Maximum 100 bars (removed after)

---

## Risk Disclaimer

This is an automated trading system. Always:
- Test on simulator before live trading
- Monitor system performance
- Understand risk per trade (10 points)
- Use appropriate position sizing
- Have manual kill switch ready
