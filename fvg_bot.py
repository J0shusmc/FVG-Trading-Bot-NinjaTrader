#!/usr/bin/env python3
"""
FVG Live Trading Bot with NinjaTrader ATI Integration
Real-time Fair Value Gap trading using NinjaTrader ATI on port 36973
"""

import pandas as pd
import numpy as np
import time
import os
import sys
import csv
from datetime import datetime
from pathlib import Path
import logging

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class FVGATITradingBot:
    def __init__(self, instrument='MES', historical_path='data/HistoricalData.csv', live_feed_path='data/LiveFeed.csv', signals_path='trade_signals.csv'):
        self.instrument = instrument
        self.historical_path = historical_path
        self.live_feed_path = live_feed_path
        self.signals_path = signals_path

        # FVG tracking
        self.active_fvgs = []
        self.last_processed_bar_time = None
        self.last_historical_mod_time = None

        # Trading state
        self.strategy_enabled = True
        self.max_position_size = 1  # Maximum contracts to trade

        # Initialize signals file
        self.initialize_signals_file()
        
    def round_to_quarter(self, price):
        """Round price to nearest 0.25 to match NinjaTrader pricing"""
        return round(price * 4) / 4
    
    def initialize_signals_file(self):
        """Initialize the trade signals CSV file"""
        if not os.path.exists(self.signals_path):
            with open(self.signals_path, 'w', newline='') as f:
                writer = csv.writer(f)
                writer.writerow(['DateTime', 'Signal', 'Direction', 'Entry_Price', 'Stop_Loss', 'Profit_Target', 'Zone_Type', 'ATR'])
    
    def generate_signal(self, signal_type, direction, entry_price, stop_loss, profit_target, zone_type, datetime, gap_size):
        """Write trade signal to CSV file"""
        try:
            with open(self.signals_path, 'a', newline='') as f:
                writer = csv.writer(f)
                writer.writerow([
                    datetime.strftime('%m/%d/%Y %H:%M:%S'),
                    signal_type,
                    direction,
                    f"{entry_price:.2f}",
                    f"{stop_loss:.2f}",
                    f"{profit_target:.2f}",
                    zone_type,
                    f"{gap_size:.2f}"
                ])
            logger.info(f"Signal written: {direction} @ {entry_price:.2f}")
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
        """Clear the console screen"""
        os.system('cls' if os.name == 'nt' else 'clear')
    
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
                self.active_fvgs.append(fvg)
                logger.info(f"NEW BEARISH FVG: Gap {gap_size:.2f}pts ({candle3['High']:.2f} to {candle1['Low']:.2f})")
        
        # Clean up old FVGs
        self.clean_old_fvgs(current_index)
    
    def check_fvg_retest_signals(self, current_price):
        """Check for FVG retest trading opportunities using live price"""
        if not self.strategy_enabled:
            return

        for fvg in self.active_fvgs:
            if fvg['trade_taken'] or fvg['filled']:
                continue

            # Calculate distance to zone
            distance_to_bottom = abs(current_price - fvg['bottom'])
            distance_to_top = abs(current_price - fvg['top'])
            min_distance = min(distance_to_bottom, distance_to_top)

            # Check if price is within 2.5 points of zone
            if min_distance <= 2.5:
                if fvg['type'] == 'bullish':
                    self.evaluate_short_entry(fvg, current_price)  # SHORT bullish zones
                elif fvg['type'] == 'bearish':
                    self.evaluate_long_entry(fvg, current_price)   # LONG bearish zones
    
    def evaluate_long_entry(self, fvg, current_price):
        """Evaluate long entry on BEARISH FVG retest (fill bottom to top)"""
        logger.info(f"=== EVALUATING LONG ENTRY ON BEARISH FVG ===")

        # Calculate limit entry at zone bottom (bearish zones fill bottom to top)
        entry_price = self.round_to_quarter(fvg['bottom'])
        stop_loss = self.round_to_quarter(fvg['bottom'] - fvg['gap_size'])
        profit_target = self.round_to_quarter(fvg['top'])

        logger.info(f"BEARISH FVG LONG SETUP:")
        logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f} ({fvg['gap_size']:.2f}pts)")
        logger.info(f"  Entry Price (Limit): {entry_price:.2f} (zone bottom)")
        logger.info(f"  Stop Loss: {stop_loss:.2f}")
        logger.info(f"  Profit Target: {profit_target:.2f} (zone top)")

        # Write trade signal to CSV
        self.generate_signal(
            signal_type='FVG_RETEST',
            direction='LONG',
            entry_price=entry_price,
            stop_loss=stop_loss,
            profit_target=profit_target,
            zone_type=fvg['type'],
            datetime=datetime.now(),
            gap_size=fvg['gap_size']
        )

        fvg['trade_taken'] = True
        logger.info(f"LONG SIGNAL: Entry {entry_price:.2f}, SL {stop_loss:.2f}, PT {profit_target:.2f}")
    
    def evaluate_short_entry(self, fvg, current_price):
        """Evaluate short entry on BULLISH FVG retest (fill top to bottom)"""
        logger.info(f"=== EVALUATING SHORT ENTRY ON BULLISH FVG ===")

        # Calculate limit entry at zone top (bullish zones fill top to bottom)
        entry_price = self.round_to_quarter(fvg['top'])
        stop_loss = self.round_to_quarter(fvg['top'] + fvg['gap_size'])
        profit_target = self.round_to_quarter(fvg['bottom'])

        logger.info(f"BULLISH FVG SHORT SETUP:")
        logger.info(f"  Zone: {fvg['bottom']:.2f} - {fvg['top']:.2f} ({fvg['gap_size']:.2f}pts)")
        logger.info(f"  Entry Price (Limit): {entry_price:.2f} (zone top)")
        logger.info(f"  Stop Loss: {stop_loss:.2f}")
        logger.info(f"  Profit Target: {profit_target:.2f} (zone bottom)")

        # Write trade signal to CSV
        self.generate_signal(
            signal_type='FVG_RETEST',
            direction='SHORT',
            entry_price=entry_price,
            stop_loss=stop_loss,
            profit_target=profit_target,
            zone_type=fvg['type'],
            datetime=datetime.now(),
            gap_size=fvg['gap_size']
        )

        fvg['trade_taken'] = True
        logger.info(f"SHORT SIGNAL: Entry {entry_price:.2f}, SL {stop_loss:.2f}, PT {profit_target:.2f}")
    
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
    
    def load_historical_fvgs(self):
        """Load FVGs from historical hourly data on startup"""
        logger.info("Scanning historical hourly data for existing FVGs...")

        df = self.read_historical_data()
        if df is None or len(df) < 3:
            logger.info("Not enough historical data to scan for FVGs")
            return

        # Find all FVGs in historical data
        historical_fvgs = self.find_fvgs_in_data(df)

        # Filter out filled FVGs and add to active list
        for fvg in historical_fvgs:
            # Check if this FVG was filled by subsequent price action
            if not self.is_fvg_filled(fvg, df, fvg['index']):
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

        print("="*80)
        print(f"FVG Trading Bot")
        print(f"Time: {datetime.now().strftime('%H:%M:%S')}")
        print(f"Instrument: {self.instrument}")
        print(f"Current Price: {current_price:.2f}")
        print(f"Strategy Status: {'ENABLED' if self.strategy_enabled else 'DISABLED'}")
       

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

            print(f"ACTIVE FVG ZONES")
            print(f"Total Active: {len(active_fvgs)} | Bullish: {len(bullish_sorted)} | Bearish: {len(bearish_sorted)}")
            print("-"*80)

            # Display BEARISH gaps
            print(f"\nBEARISH GAPS (LONG OPPORTUNITIES)")
            print(f"{'Zone Range':<25} {'Gap Size':<12} {'Distance':<15}")
            print("-"*55)
            if bearish_sorted:
                for item in bearish_sorted:
                    fvg = item['fvg']
                    distance = item['distance']
                    zone_range = f"{fvg['bottom']:.2f} - {fvg['top']:.2f}"
                    gap_size = f"{fvg['gap_size']:.2f}pts"
                    distance_str = f"{distance:.2f}pts"

                    # Highlight zones within 5 points
                    if distance <= 5.0:
                        print(f">>> {zone_range:<22} {gap_size:<12} {distance_str:<15}")
                    else:
                        print(f"    {zone_range:<22} {gap_size:<12} {distance_str:<15}")
            else:
                print("    No bearish gaps")

            # Display BULLISH gaps
            print(f"\nBULLISH GAPS (SHORT OPPORTUNITIES)")
            print(f"{'Zone Range':<25} {'Gap Size':<12} {'Distance':<15}")
            print("-"*55)
            if bullish_sorted:
                for item in bullish_sorted:
                    fvg = item['fvg']
                    distance = item['distance']
                    zone_range = f"{fvg['bottom']:.2f} - {fvg['top']:.2f}"
                    gap_size = f"{fvg['gap_size']:.2f}pts"
                    distance_str = f"{distance:.2f}pts"

                    # Highlight zones within 5 points
                    if distance <= 5.0:
                        print(f">>> {zone_range:<22} {gap_size:<12} {distance_str:<15}")
                    else:
                        print(f"    {zone_range:<22} {gap_size:<12} {distance_str:<15}")
            else:
                print("    No bullish gaps")

            print("-"*80)
        else:
            print("\nNo active FVGs")

        print("="*80)
    
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

        try:
            while True:
                # Check for new hourly bars (new FVGs)
                if self.check_historical_updated():
                    self.process_historical_bars()

                # Get current price from LiveFeed
                current_price = self.read_current_price()

                if current_price is not None:
                    # Check for trade signals based on current price
                    self.check_fvg_retest_signals(current_price)

                    # Display status with current price
                    self.clear_screen()
                    self.display_status(current_price)

                # Sleep for 1 second - updates every second
                time.sleep(1)

        except KeyboardInterrupt:
            logger.info("\nStopping FVG ATI Trading Bot...")
            logger.info(f"Final active FVGs: {len(self.active_fvgs)}")
        except Exception as e:
            logger.error(f"Error in main loop: {e}")
            import traceback
            logger.error(traceback.format_exc())
        finally:
            logger.info("FVG Bot stopped")

if __name__ == "__main__":
    # Configure for MES futures
    bot = FVGATITradingBot(instrument='MES')
    bot.run()