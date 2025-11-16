# NinjaTrader Historical Data Exporter

A NinjaTrader 8 strategy that exports historical price data and EMA indicators to CSV format for analysis and backtesting.

## Overview

This strategy runs on NinjaTrader 8 and continuously exports completed bar data to a CSV file, including:
- OHLC (Open, High, Low, Close) price data
- EMA 21, 75, and 150 indicators
- Timestamp for each bar

## Features

- **Real-time Data Export**: Automatically writes completed bars to CSV on bar close
- **EMA Indicators**: Calculates and exports 21, 75, and 150-period exponential moving averages
- **Safe File Operations**: Includes error handling and data validation
- **Configurable Output**: Easy to modify output path and format

## Installation

1. Copy `HistoricalData.cs` to your NinjaTrader scripts folder:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```

2. Compile the strategy in NinjaTrader:
   - Tools ’ Edit NinjaScript ’ Strategy
   - Compile (F5)

3. Create the output directory:
   ```
   C:\Users\[YourUsername]\Documents\Projects\FVG Bot\data\
   ```

## Usage

1. Open a chart in NinjaTrader 8
2. Right-click ’ Strategies ’ HistoricalData
3. Configure your desired timeframe (e.g., 1 hour, 5 minutes)
4. Enable the strategy
5. Data will be written to `data\HistoricalData.csv`

## Output Format

The CSV file contains the following columns:

```csv
DateTime,Open,High,Low,Close,EMA21,EMA75,EMA150
01/15/2025 09:00:00,4850.25,4852.50,4848.00,4851.75,4850.12,4849.88,4848.50
```

## Configuration

To modify the output path, edit line 35 in `HistoricalData.cs`:

```csharp
filePath = @"C:\Your\Custom\Path\HistoricalData.csv";
```

## Technical Details

- **Strategy Type**: OnBarClose calculation
- **Minimum Bars Required**: 20 bars for EMA calculation
- **Indicators Used**: EMA(21), EMA(75), EMA(150)
- **File Format**: CSV with header row
- **Precision**: 2 decimal places for all numeric values

## Error Handling

The strategy includes comprehensive error handling:
- File access validation
- Data availability checks
- Safe bar data retrieval
- Exception logging to NinjaTrader output window

## Requirements

- NinjaTrader 8
- Windows OS
- Write permissions to output directory

## Use Cases

- Historical data analysis
- Custom indicator backtesting
- Machine learning training data
- External trading system integration
- Market research and analysis

## License

MIT License - Free to use and modify

## Support

For issues or questions, please open an issue on GitHub.

## Version History

- **v1.0** - Initial release with OHLC and EMA export functionality
