import pandas as pd
import numpy as np
import time
import os
import sys
import csv
from datetime import datetime
from pathlib import Path
import logging
import subprocess

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class FVGATITradingBot:
    def __init__(self, instrument='MES', historical_path='data/HistoricalData.csv', live_feed_path='data/LiveFeed.csv', signals_path='data/trade_signals.csv', trades_log_path='data/trades_taken.csv'):
        self.instrument = instrument
        self.historical_path = historical_path
        self.live_feed_path = live_feed_path
        self.signals_path = signals_path
        self.trades_log_path = trades_log_path

        # FVG tracking
        self.active_fvgs = []
        self.last_processed_bar_time = None
        self.last_historical_mod_time = None

        # Trading state
        self.strategy_enabled = True

        # Initialize files
        self.initialize_signals_file()
        self.initialize_trades_log()
        
    def round_to_quarter(self, price):
        """Round price to nearest 0.25 to match NinjaTrader pricing"""
        return round(price * 4) / 4

    
    def initialize_signals_file(self):
        """Initialize the trade signals CSV file - clears old signals on startup"""
        # Always create fresh file on startup to prevent duplicate trades
        with open(self.signals_path, 'w', newline='') as f:
            writer = csv.writer(f)
            writer.writerow(['DateTime', 'Direction', 'Entry_Price'])
        logger.info(f"Initialized fresh signals file: {self.signals_path}")

    def initialize_trades_log(self):
        """Initialize the trades taken log file - preserves historical trade data"""
        # Only create header if file doesn't exist (preserve historical data)
        if not os.path.exists(self.trades_log_path):
            with open(self.trades_log_path, 'w', newline='') as f:
                writer = csv.writer(f)
                # Simplified format: DateTime, Direction, Entry_Price
                writer.writerow(['DateTime', 'Direction', 'Entry_Price'])
            logger.info(f"Created new trades log file: {self.trades_log_path}")
        else:
            logger.info(f"Using existing trades log file: {self.trades_log_path}")
    
    def generate_signal(self, signal_type, direction, signal_datetime, zone_bottom, zone_top, gap_size):
        """Write trade signal to CSV - NinjaTrader handles all order management"""
        try:
            # Entry_Price = zone boundary that triggers the trade
            entry_price = zone_top if direction == 'SHORT' else zone_bottom

            # Write to trade_signals.csv (NinjaTrader reads this)
            # Simplified format: DateTime, Direction, Entry_Price
            with open(self.signals_path, 'a', newline='') as f:
                writer = csv.writer(f)
                writer.writerow([
                    signal_datetime.strftime('%m/%d/%Y %H:%M:%S'),
                    direction,
                    f"{entry_price:.2f}"
                ])

            # Write to trades_taken.csv (persistent historical log for Python bot)
            # Same simplified format: DateTime, Direction, Entry_Price
            with open(self.trades_log_path, 'a', newline='') as f:
                writer = csv.writer(f)
                writer.writerow([
                    signal_datetime.strftime('%m/%d/%Y %H:%M:%S'),
                    direction,
                    f"{entry_price:.2f}"
                ])

            logger.info(f"Signal sent: {direction} @ {signal_datetime.strftime('%H:%M:%S')}")
            logger.info(f"  Entry Price: {entry_price:.2f} (Zone: {zone_bottom:.2f}-{zone_top:.2f})")
            logger.info(f"  NinjaTrader will handle entry, stops (10pts), and targets (5pts)")
        except Exception as e:
            logger.error(f"Error writing signal: {e}")
        
    
    def check_historical_updated(self):
        """Check if HistoricalData.csv has been updated (new hourly bar)"""
        try:
            if not os.path.exists(self.historical_path):
                return False

            current_mod_time = os.path.getmtime(self.historical_path)

            if self.last_historical_mod_time is None:
                self.last_historical_mod_time = current_mod_time
                return True

            if current_mod_time > self.last_historical_mod_time:
                self.last_historical_mod_time = current_mod_time
                return True

            return False
        except Exception as e:
            logger.error(f"Error checking historical file: {e}")
            return False
    
    def read_historical_data(self):
        """Read historical hourly data for FVG detection"""
        try:
            if not os.path.exists(self.historical_path):
                return None

            df = pd.read_csv(self.historical_path)
            if df.empty:
                return None

            df['DateTime'] = pd.to_datetime(df['DateTime'])
            df = df.sort_values('DateTime').reset_index(drop=True)
            return df
        except Exception as e:
            logger.error(f"Error reading historical data: {e}")
            return None

    def read_current_price(self):
        """Read current price from live feed (last line)"""
        try:
            if not os.path.exists(self.live_feed_path):
                return None

            df = pd.read_csv(self.live_feed_path)
            if df.empty:
                return None

            # Get the last line for current price
            last_row = df.iloc[-1]
            return float(last_row['Last'])
        except Exception as e:
            logger.error(f"Error reading current price: {e}")
            return None
        

    def find_fvgs_in_data(self, df, start_index=2):
        """Find FVGs in price data"""
        fvgs = []
        
        for i in range(start_index, len(df)):
            candle1 = df.iloc[i - 2]
            candle2 = df.iloc[i - 1]  
            candle3 = df.iloc[i]
            
            # Check for bullish FVG (gap up)
            if candle3['Low'] > candle1['High']:
                gap_size = candle3['Low'] - candle1['High']
                if gap_size >= 2.5:  # Minimum gap size
                    fvg = {
                        'type': 'bullish',
                        'top': candle3['Low'],
                        'bottom': candle1['High'],
                        'gap_size': gap_size,
                        'datetime': candle3['DateTime'],
                        'index': i,
                        'filled': False,
                        'trade_taken': False
                    }
                    fvgs.append(fvg)

            # Check for bearish FVG (gap down)
            elif candle3['High'] < candle1['Low']:
                gap_size = candle1['Low'] - candle3['High']
                if gap_size >= 2.5:
                    fvg = {
                        'type': 'bearish',
                        'top': candle1['Low'],
                        'bottom': candle3['High'],
                        'gap_size': gap_size,
                        'datetime': candle3['DateTime'],
                        'index': i,
                        'filled': False,
                        'trade_taken': False
                    }
                    fvgs.append(fvg)
        
        return fvgs
    
    def is_fvg_filled(self, fvg, df, start_index):
        """Check if FVG has been filled by subsequent price action"""
        for j in range(start_index + 1, len(df)):
            check_candle = df.iloc[j]
            
            if fvg['type'] == 'bullish' and check_candle['Close'] <= fvg['bottom']:
                return True
            elif fvg['type'] == 'bearish' and check_candle['Close'] >= fvg['top']:
                return True
                
        return False
    
    def process_historical_bars(self):
        """Process historical data for new FVG detection"""
        df = self.read_historical_data()

        if df is None or df.empty or len(df) < 3:
            return

        # Get the latest bar time
        latest_bar_time = df.iloc[-1]['DateTime']
        current_index = len(df) - 1

        # Check if this is a new bar
        is_new_bar = self.last_processed_bar_time != latest_bar_time

        if is_new_bar:
            logger.info(f"New hourly bar detected at {latest_bar_time}")

            # Look for new FVGs
            self.find_new_fvgs(df, current_index)

            # Check if any FVGs got filled
            self.check_fvg_fill_status(df, current_index)

            self.last_processed_bar_time = latest_bar_time
    
    def clear_screen(self):
        """Clear screen - optimized for Windows"""
        if os.name == 'nt':
            # Windows: use standard os.system for proper clearing
            os.system('cls')
        else:
            # Unix: use ANSI codes
            os.system('clear')

    def zones_overlap(self, zone1_bottom, zone1_top, zone2_bottom, zone2_top):
        """Check if two zones overlap"""
        if zone1_bottom >= zone2_top or zone2_bottom >= zone1_top:
            return False
        return True

    def is_duplicate_zone(self, new_fvg):
        """Check if a new FVG overlaps with existing zones - keep smaller zone (silent)"""
        zones_to_remove = []

        for i, existing_fvg in enumerate(self.active_fvgs):
            if existing_fvg['type'] != new_fvg['type']:
                continue
            if existing_fvg['filled']:
                continue

            if self.zones_overlap(existing_fvg['bottom'], existing_fvg['top'],
                                 new_fvg['bottom'], new_fvg['top']):
                existing_size = existing_fvg['gap_size']
                new_size = new_fvg['gap_size']

                if new_size < existing_size:
                    zones_to_remove.append(i)
                else:
                    return True

        for i in reversed(zones_to_remove):
            self.active_fvgs.pop(i)

        return False

    def find_new_fvgs(self, df, current_index):
        """Find new FVGs in the latest price data"""
        if current_index < 2:
            return

        candle1 = df.iloc[current_index - 2]
        candle2 = df.iloc[current_index - 1]
        candle3 = df.iloc[current_index]

        # Check for bullish FVG
        if candle3['Low'] > candle1['High']:
            gap_size = candle3['Low'] - candle1['High']
            if gap_size >= 2.5:
                fvg = {
                    'type': 'bullish',
                    'top': candle3['Low'],
                    'bottom': candle1['High'],
                    'gap_size': gap_size,
                    'datetime': candle3['DateTime'],
                    'index': current_index,
                    'filled': False,
                    'trade_taken': False
                }
                if not self.is_duplicate_zone(fvg):
                    self.active_fvgs.append(fvg)
                    logger.info(f"NEW BULLISH FVG: Gap {gap_size:.2f}pts ({candle1['High']:.2f} to {candle3['Low']:.2f})")

        # Check for bearish FVG
        elif candle3['High'] < candle1['Low']:
            gap_size = candle1['Low'] - candle3['High']
            if gap_size >= 2.5:
                fvg = {
                    'type': 'bearish',
                    'top': candle1['Low'],
                    'bottom': candle3['High'],
                    'gap_size': gap_size,
                    'datetime': candle3['DateTime'],
                    'index': current_index,
                    'filled': False,
                    'trade_taken': False
                }
                if not self.is_duplicate_zone(fvg):
                    self.active_fvgs.append(fvg)
                    logger.info(f"NEW BEARISH FVG: Gap {gap_size:.2f}pts ({candle3['High']:.2f} to {candle1['Low']:.2f})")

        # Clean up old FVGs
        self.clean_old_fvgs(current_index)
    
    def check_fvg_retest_signals(self, current_price):
        """Check for FVG retest trading opportunities using live price"""
        if not self.strategy_enabled:
            return

        for fvg in self.active_fvgs:
            # Skip if already traded or filled
            if fvg['trade_taken'] or fvg['filled']:
                continue

            # Skip zones already traded (simple flag check - no cooldown needed)
            # Zone is marked trade_taken=True after signal generation

            # Check if price has ENTERED the zone boundaries
            price_in_zone = (current_price >= fvg['bottom'] and current_price <= fvg['top'])

            if price_in_zone:
                # Log zone entry detection
                logger.info(f"*** PRICE IN ZONE DETECTED ***")
                logger.info(f"  Zone Type: {fvg['type'].upper()}")
                logger.info(f"  Zone Range: {fvg['bottom']:.2f} - {fvg['top']:.2f}")
                logger.info(f"  Current Price: {current_price:.2f}")
                logger.info(f"  Price Check: {current_price:.2f} >= {fvg['bottom']:.2f} AND {current_price:.2f} <= {fvg['top']:.2f} = TRUE")

                # Price is inside the zone - trigger trade ONCE
                if fvg['type'] == 'bullish':
                    # For bullish zones: SHORT when price enters zone from above
                    self.evaluate_short_entry(fvg, current_price)
                    # Flag is set inside evaluate function to prevent duplicates
                elif fvg['type'] == 'bearish':
                    # For bearish zones: LONG when price enters zone from below
                    self.evaluate_long_entry(fvg, current_price)
                    # Flag is set inside evaluate function to prevent duplicates
    
    def evaluate_long_entry(self, fvg, current_price):
        """Evaluate long entry on BEARISH FVG retest"""
        logger.info(f"=== LONG SIGNAL - BEARISH FVG ===")
        logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f} ({fvg['gap_size']:.2f}pts)")
        logger.info(f"  Current Price: {current_price:.2f}")

        # Send signal to NinjaTrader
        self.generate_signal(
            signal_type='FVG_RETEST',
            direction='LONG',
            signal_datetime=datetime.now(),
            zone_bottom=fvg['bottom'],
            zone_top=fvg['top'],
            gap_size=fvg['gap_size']
        )

        fvg['trade_taken'] = True
    
    def evaluate_short_entry(self, fvg, current_price):
        """Evaluate short entry on BULLISH FVG retest"""
        logger.info(f"=== SHORT SIGNAL - BULLISH FVG ===")
        logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f} ({fvg['gap_size']:.2f}pts)")
        logger.info(f"  Current Price: {current_price:.2f}")

        # Send signal to NinjaTrader
        self.generate_signal(
            signal_type='FVG_RETEST',
            direction='SHORT',
            signal_datetime=datetime.now(),
            zone_bottom=fvg['bottom'],
            zone_top=fvg['top'],
            gap_size=fvg['gap_size']
        )

        fvg['trade_taken'] = True
    
    def check_fvg_fill_status(self, df, current_index):
        """Check if any FVGs have been filled by completed bars"""
        current_bar = df.iloc[current_index]

        for fvg in self.active_fvgs:
            if fvg['filled']:
                continue

            if fvg['type'] == 'bullish' and current_bar['Close'] <= fvg['bottom']:
                fvg['filled'] = True
                logger.info(f"BULLISH FVG FILLED at {current_bar['Close']:.2f}")
            elif fvg['type'] == 'bearish' and current_bar['Close'] >= fvg['top']:
                fvg['filled'] = True
                logger.info(f"BEARISH FVG FILLED at {current_bar['Close']:.2f}")

    def check_live_fvg_fills(self, current_price):
        """Check if any FVGs have been filled by current live price"""
        for fvg in self.active_fvgs:
            if fvg['filled']:
                continue

            # Bearish FVG fills when price reaches the TOP (zone filled completely)
            if fvg['type'] == 'bearish' and current_price >= fvg['top']:
                fvg['filled'] = True
                logger.info(f"*** BEARISH FVG FILLED (LIVE) ***")
                logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f}")
                logger.info(f"  Fill Price: {current_price:.2f}")
                logger.info(f"  Zone removed from active list")

            # Bullish FVG fills when price reaches the BOTTOM (zone filled completely)
            elif fvg['type'] == 'bullish' and current_price <= fvg['bottom']:
                fvg['filled'] = True
                logger.info(f"*** BULLISH FVG FILLED (LIVE) ***")
                logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f}")
                logger.info(f"  Fill Price: {current_price:.2f}")
                logger.info(f"  Zone removed from active list")
    
    def load_historical_fvgs(self):
        """Load FVGs from historical hourly data on startup"""
        logger.info("Scanning historical hourly data for existing FVGs...")

        df = self.read_historical_data()
        if df is None or len(df) < 3:
            logger.info("Not enough historical data to scan for FVGs")
            return

        # Find all FVGs in historical data
        historical_fvgs = self.find_fvgs_in_data(df)

        # Filter out filled FVGs and duplicates, then add to active list
        for fvg in historical_fvgs:
            if not self.is_fvg_filled(fvg, df, fvg['index']):
                if not self.is_duplicate_zone(fvg):
                    self.active_fvgs.append(fvg)

        logger.info(f"Loaded {len(self.active_fvgs)} active FVGs from historical data")
        bullish_count = len([f for f in self.active_fvgs if f['type'] == 'bullish'])
        bearish_count = len([f for f in self.active_fvgs if f['type'] == 'bearish'])
        logger.info(f"  - {bullish_count} bullish FVGs")
        logger.info(f"  - {bearish_count} bearish FVGs")

    def clean_old_fvgs(self, current_index):
        """Remove old or filled FVGs"""
        cleaned_fvgs = []
        for fvg in self.active_fvgs:
            # Remove filled FVGs
            if fvg['filled']:
                continue

            # Keep FVGs if less than 100 bars old
            if current_index - fvg['index'] < 100:
                cleaned_fvgs.append(fvg)

        removed_count = len(self.active_fvgs) - len(cleaned_fvgs)
        self.active_fvgs = cleaned_fvgs

        if removed_count > 0:
            logger.info(f"Cleaned {removed_count} old/filled FVGs")
    
    def display_status(self, current_price):
        """Display current bot status with real-time updates"""
        if current_price is None:
            return

        # Build the entire display as a string buffer first
        lines = []
        lines.append("="*80)
        lines.append(f"  FVG TRADING BOT - LIVE MONITORING")
        lines.append(f"  Time: {datetime.now().strftime('%H:%M:%S')} | Instrument: {self.instrument} | Current Price: {current_price:.2f}")
        lines.append(f"  Strategy: {'ENABLED' if self.strategy_enabled else 'DISABLED'}")
        lines.append("="*80)
        lines.append("")

        # Get all active FVGs and calculate distances
        active_fvgs = [fvg for fvg in self.active_fvgs if not fvg['filled']]

        if active_fvgs:
            # Add distance to each FVG and sort by distance
            fvgs_with_distance = []
            for fvg in active_fvgs:
                distance = min(abs(current_price - fvg['bottom']), abs(current_price - fvg['top']))
                fvgs_with_distance.append({
                    'fvg': fvg,
                    'distance': distance
                })

            # Sort by distance (closest first)
            fvgs_with_distance.sort(key=lambda x: x['distance'])

            # Separate by type but keep distance ordering
            bullish_sorted = [item for item in fvgs_with_distance if item['fvg']['type'] == 'bullish']
            bearish_sorted = [item for item in fvgs_with_distance if item['fvg']['type'] == 'bearish']

            # Display BEARISH gaps (bottom first - closest to price when looking for longs)
            lines.append(f"BEARISH GAPS (LONG OPPORTUNITIES)")
            lines.append(f"{'Zone Range':<25} {'Gap Size':<12} {'Distance':<15}")
            lines.append("-"*55)
            if bearish_sorted:
                for item in bearish_sorted:
                    fvg = item['fvg']
                    distance = item['distance']
                    # Show BOTTOM first for bearish zones (price approaches from below)
                    zone_range = f"{fvg['bottom']:.2f} - {fvg['top']:.2f}"
                    gap_size = f"{fvg['gap_size']:.2f}pts"
                    distance_str = f"{distance:.2f}pts"

                    # Highlight zones within 5 points
                    if distance <= 5.0:
                        lines.append(f">>> {zone_range:<22} {gap_size:<12} {distance_str:<15}")
                    else:
                        lines.append(f"    {zone_range:<22} {gap_size:<12} {distance_str:<15}")
            else:
                lines.append("    No bearish gaps")

            # Display BULLISH gaps (top first - closest to price when looking for shorts)
            lines.append(f"\nBULLISH GAPS (SHORT OPPORTUNITIES)")
            lines.append(f"{'Zone Range':<25} {'Gap Size':<12} {'Distance':<15}")
            lines.append("-"*55)
            if bullish_sorted:
                for item in bullish_sorted:
                    fvg = item['fvg']
                    distance = item['distance']
                    # Show TOP first for bullish zones (price approaches from above)
                    zone_range = f"{fvg['top']:.2f} - {fvg['bottom']:.2f}"
                    gap_size = f"{fvg['gap_size']:.2f}pts"
                    distance_str = f"{distance:.2f}pts"

                    # Highlight zones within 5 points
                    if distance <= 5.0:
                        lines.append(f">>> {zone_range:<22} {gap_size:<12} {distance_str:<15}")
                    else:
                        lines.append(f"    {zone_range:<22} {gap_size:<12} {distance_str:<15}")
            else:
                lines.append("    No bullish gaps")

            lines.append("-"*80)
        else:
            lines.append("\nNo active FVGs")

        lines.append("="*80)

        # Print all lines at once with proper flushing
        output = '\n'.join(lines)
        print(output, end='', flush=True)
    
    def run(self):
        """Main trading loop"""
        logger.info("Starting FVG Trading Bot...")
        logger.info("Monitoring Fair Value Gaps in real-time")
        logger.info("="*60)

        # Load historical FVGs on startup
        self.load_historical_fvgs()
        logger.info("="*60)

        logger.info(f"FVG signal generation enabled for {self.instrument}")
        logger.info("Monitoring HistoricalData.csv for new hourly bars and FVGs...")
        logger.info("Monitoring LiveFeed.csv for real-time price updates...")
        logger.info("Display updates every second with live price data...")

        # Enable ANSI escape codes for Windows 10+
        if os.name == 'nt':
            os.system('color')

        # Hide cursor for cleaner display
        if os.name == 'nt':
            # Windows cursor control
            os.system('echo off')
        else:
            print('\033[?25l', end='', flush=True)

        # Clear screen once at startup
        os.system('cls' if os.name == 'nt' else 'clear')

        try:
            while True:
                # Check for new hourly bars (new FVGs)
                if self.check_historical_updated():
                    self.process_historical_bars()

                # Get current price from LiveFeed
                current_price = self.read_current_price()

                if current_price is not None:
                    # Check if any zones have been filled (real-time)
                    self.check_live_fvg_fills(current_price)

                    # Check for trade signals based on current price
                    self.check_fvg_retest_signals(current_price)

                    # Display status with current price
                    self.clear_screen()
                    self.display_status(current_price)

                # Sleep for 1 second - updates every second
                time.sleep(1)

        except KeyboardInterrupt:
            # Show cursor again before exiting
            if os.name == 'nt':
                os.system('echo on')
            else:
                print('\033[?25h', end='', flush=True)
            logger.info("\nStopping FVG ATI Trading Bot...")
            logger.info(f"Final active FVGs: {len(self.active_fvgs)}")
        except Exception as e:
            # Show cursor again on error
            if os.name == 'nt':
                os.system('echo on')
            else:
                print('\033[?25h', end='', flush=True)
            logger.error(f"Error in main loop: {e}")
            import traceback
            logger.error(traceback.format_exc())
        finally:
            # Ensure cursor is visible
            if os.name == 'nt':
                os.system('echo on')
            else:
                print('\033[?25h', end='', flush=True)
            logger.info("FVG Bot stopped")

if __name__ == "__main__":
    # Configure for MES futures
    bot = FVGATITradingBot(instrument='MES')
    bot.run()