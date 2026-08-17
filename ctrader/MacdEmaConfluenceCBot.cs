// MACD + EMA 3-Timeframe Confluence - cTrader cBot (cAlgo.API, C#)
//
// Trend-following, stop-and-reverse strategy: computes a bullish/bearish bias from BOTH a MACD
// line-vs-signal state AND an EMA fast-vs-slow state, each independently checked for confluence across
// three timeframes (1min/5min/15min by default). A trade only fires when BOTH indicators agree on
// direction across enough of those timeframes (Min Timeframes Agreeing) - and firing means entering (or
// reversing into) that direction THE MOMENT the combined signal changes, not on every bar it happens to
// hold (as specified - "immediate entry on confluence flip"). If the combined signal breaks down
// (indicators disagree, or confluence is lost) with a position open, that position is closed regardless of
// stop/target - the trade's entire premise was the confluence holding.
//
// *** STATUS: BRAND NEW, UNVALIDATED - NO BACKTEST OR FORWARD DATA BEHIND ANY DEFAULT BELOW ***
// Every period/multiplier here is a reasonable-sounding starting point, not a tuned value: MACD(12,26,9)
// is the industry-standard setting; EMA(12,26) deliberately mirrors MACD's own cycles for a consistent
// story; ATR-based stop/target is the standard approach for a system with no natural price level (wall,
// pivot, etc.) to reference. None of it has been tuned against real trade logs the way the daily/weekly
// reversal bots in this repo were - treat a live run as forward/paper testing that GENERATES the data
// needed to validate and tune it, not a proven edge.
//
// *** VERIFY BEFORE RUNNING: MacdCrossOver / AverageTrueRange indicator signatures ***
// This file has not been compiled inside cTrader (same caveat as the other bots in this repo). The
// Indicators.MacdCrossOver(source, longCycle, shortCycle, signalPeriod) parameter order and the
// AverageTrueRange MovingAverageType member used below are written from general cAlgo.API knowledge, not
// verified against a live SDK - double-check both against your installed cAlgo version's own
// autocomplete/reference before trusting the values, since a swapped long/short cycle would silently
// invert the whole MACD read.
//
// No external data feed (unlike GammaWallOvernightCBot.cs) - this bot does NOT require
// AccessRights.FullAccess, so unlike that one, it CAN run on cTrader's own hosted cloud/VPS.
//
// One behavior worth knowing explicitly: after a stop-loss or take-profit hit (broker-triggered close),
// this bot does NOT automatically re-enter even if the MACD+EMA confluence never actually changed - it
// only acts on a CHANGE in the combined bias (see _lastCombinedBias), and a stop/target hit doesn't reset
// that tracker. So if you get stopped out on a wick while the underlying trend confluence is still fully
// intact, the bot stays flat until the bias changes to something else and back. This is a deliberate
// choice (it avoids repeatedly re-entering and re-stopping in a choppy market sitting right at the ATR
// stop distance) but it does mean a stop-out can leave you sidelined through a continuation move - if that
// turns out to be the wrong tradeoff in practice, resetting _lastCombinedBias to null in OnPositionsClosed
// would make it re-enter immediately instead.
//
// 1min MACD trailing stop / fast exit (see ProcessMacdTrailBar): independent of the ATR stop above, a
// dedicated 1-minute-only MACD (separate from the parameterized MACD Timeframe 1-3 slots, which could be
// set to something else) is watched for its OWN crossovers on every 1-minute bar close, regardless of
// Locked Execution Timeframe. A crossover AGAINST the open position's direction closes it immediately -
// tighter and faster than waiting for the full 3-TF combined bias to flip. A crossover WITH the position's
// direction (continued momentum after a shallow pullback) trails the stop to that crossover candle's low
// (longs) / high (shorts), only ever tightening it, never loosening it - starts disengaged at entry and
// first activates on the FIRST such crossover after entry, so the ATR stop protects the trade until then.
//
// Cooldown After Close (minutes) applies uniformly after ANY close, including an immediate stop-and-
// reverse on a fresh confluence flip - same precedent as the cooldown in GammaWallOvernightCBot.cs. This
// deliberately softens the "immediate entry on flip" behavior specified earlier in favor of whipsaw
// protection; set it to 0 if instant reversal is what you actually want back.
//
// EMA trend-filter mechanism, risk-% position sizing, and general parameter/state/logging conventions are
// deliberately similar to the other bots in this repo for consistency, adapted down from 7 timeframes to
// the 3 specified here.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MacdEmaConfluenceCBot : Robot
    {
        private const string PositionLabel = "MacdEmaConfluence";

        // ---------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------

        [Parameter("Use MACD Filter", DefaultValue = true, Group = "MACD Trend Filter")]
        public bool UseMacdFilter { get; set; }

        [Parameter("MACD Fast (short) Cycle", DefaultValue = 12, MinValue = 1, Group = "MACD Trend Filter")]
        public int MacdFastPeriod { get; set; }

        [Parameter("MACD Slow (long) Cycle", DefaultValue = 26, MinValue = 1, Group = "MACD Trend Filter")]
        public int MacdSlowPeriod { get; set; }

        [Parameter("MACD Signal Period", DefaultValue = 9, MinValue = 1, Group = "MACD Trend Filter")]
        public int MacdSignalPeriod { get; set; }

        [Parameter("MACD Timeframe 1", DefaultValue = "Minute1", Group = "MACD Trend Filter")]
        public TimeFrame MacdTimeFrame1 { get; set; }

        [Parameter("MACD Timeframe 2", DefaultValue = "Minute5", Group = "MACD Trend Filter")]
        public TimeFrame MacdTimeFrame2 { get; set; }

        [Parameter("MACD Timeframe 3", DefaultValue = "Minute15", Group = "MACD Trend Filter")]
        public TimeFrame MacdTimeFrame3 { get; set; }

        [Parameter("Use EMA Filter", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaFilter { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 12, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", DefaultValue = 26, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaSlowPeriod { get; set; }

        [Parameter("EMA Timeframe 1", DefaultValue = "Minute1", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame1 { get; set; }

        [Parameter("EMA Timeframe 2", DefaultValue = "Minute5", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame2 { get; set; }

        [Parameter("EMA Timeframe 3", DefaultValue = "Minute15", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame3 { get; set; }

        [Parameter("Min Timeframes Agreeing (of 3)", DefaultValue = 3, MinValue = 1, MaxValue = 3, Group = "EMA Trend Filter",
            Description = "Applies to BOTH the MACD and EMA confluence checks independently - each needs this many of its OWN 3 timeframes agreeing before it counts as a direction, and (when both filters are enabled) the two indicators' resulting directions then also have to match each other. Default 3 requires full agreement across all three timeframes on both indicators.")]
        public int MinTimeframesAgreeing { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Risk Management")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Stop Multiplier", DefaultValue = 2.0, MinValue = 0.1, Group = "Risk Management",
            Description = "Stop distance = ATR (on the Locked Execution Timeframe) x this multiplier - volatility-adaptive since this strategy has no natural price level (wall, pivot, etc.) to place a stop against, unlike the other bots in this repo.")]
        public double AtrStopMultiplier { get; set; }

        [Parameter("Reward:Risk Ratio", DefaultValue = 1.5, MinValue = 0.1, Group = "Risk Management",
            Description = "Target distance = stop distance x this multiple, same reasoning as the breakout trades in GammaWallOvernightCBot.cs - there's no natural target level here either.")]
        public double RewardToRiskRatio { get; set; }

        [Parameter("Use 1min MACD Trailing Stop", DefaultValue = true, Group = "Risk Management",
            Description = "On top of the initial ATR stop: tracks the 1-minute MACD independently of the MACD Timeframe 1-3 slots above (always literally 1 minute, regardless of how those are configured), and every time it produces a NEW crossover in the position's OWN direction (a sign of continued momentum, e.g. a shallow pullback-and-resume), moves the stop to that crossover candle's low (longs) / high (shorts), buffered by MACD Trail Buffer %. Only ever tightens the stop, never loosens it. See the file header for what the paired opposite-direction case does.")]
        public bool UseMacdTrailingStop { get; set; }

        [Parameter("MACD Trail Buffer %", DefaultValue = 0.05, MinValue = 0, Group = "Risk Management",
            Description = "Small buffer beyond the 1min MACD crossover candle's low/high the trailing stop is placed at, so it isn't sitting exactly on the level.")]
        public double MacdTrailBufferPercent { get; set; }

        [Parameter("Risk % Of Free Margin Per Trade", DefaultValue = 1.0, MinValue = 0.01, Group = "Risk Management")]
        public double RiskPercent { get; set; }

        [Parameter("Max Position Size (lots)", DefaultValue = 5.0, MinValue = 0.01, Group = "Risk Management")]
        public double MaxVolumeLots { get; set; }

        [Parameter("Fallback Volume (lots) If Stop Distance Invalid", DefaultValue = 0.10, MinValue = 0.01, Group = "Risk Management")]
        public double FallbackVolumeLots { get; set; }

        [Parameter("Trade Longs", DefaultValue = true, Group = "Strategy")]
        public bool EnableLongs { get; set; }

        [Parameter("Trade Shorts", DefaultValue = true, Group = "Strategy")]
        public bool EnableShorts { get; set; }

        [Parameter("Show Entry/Exit Reasoning Labels", DefaultValue = true, Group = "Strategy")]
        public bool ShowReasons { get; set; }

        [Parameter("Enable Debug Logging", DefaultValue = false, Group = "Strategy",
            Description = "Prints one diagnostic line per Locked Execution Timeframe bar: MACD/EMA readings per timeframe and the resulting combined bias.")]
        public bool EnableDebugLogging { get; set; }

        [Parameter("Locked Execution Timeframe", DefaultValue = "Minute1", Group = "Strategy",
            Description = "Drives when EvaluateTradingLogic runs (once per bar close on this timeframe) and which bars the ATR stop/target is computed from - independent of the MACD/EMA timeframes above.")]
        public TimeFrame ExecutionTimeFrame { get; set; }

        [Parameter("Cooldown After Close (minutes)", DefaultValue = 5, MinValue = 0, Group = "Strategy",
            Description = "Minimum time after ANY position close - a confluence flip/breakdown, the 1min-MACD opposite-flip exit, a stop, or a target - before a new entry is allowed. Applies uniformly, INCLUDING an immediate stop-and-reverse on a fresh confluence flip (same precedent as the cooldown in GammaWallOvernightCBot.cs) - this deliberately softens the 'immediate entry on flip' behavior in exchange for whipsaw protection. Set to 0 to fully restore instant reversal.")]
        public int CooldownMinutes { get; set; }

        // ---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        private Bars _execBars;
        private AverageTrueRange _atr;

        private Bars _macdBars1, _macdBars2, _macdBars3;
        private MacdCrossOver _macd1, _macd2, _macd3;

        private Bars _emaBars1, _emaBars2, _emaBars3;
        private ExponentialMovingAverage _emaFast1, _emaSlow1;
        private ExponentialMovingAverage _emaFast2, _emaSlow2;
        private ExponentialMovingAverage _emaFast3, _emaSlow3;

        // Dedicated 1-minute MACD, independent of the MACD Timeframe 1-3 slots (which are parameterized
        // and could point elsewhere) - always literally 1 minute, for the trailing stop / fast-exit
        // mechanism described in the file header. Fires on its own BarOpened subscription rather than
        // piggybacking on OnExecBarOpened, so it stays truly 1-minute-responsive even if Locked Execution
        // Timeframe is changed away from Minute1.
        private Bars _macdTrailBars;
        private MacdCrossOver _macdTrail;
        private string _macdTrailLastState; // previous 1min bar's MACD state ("long"/"short"/null) - for edge (crossover) detection, not just current state

        private Position _openPosition;
        private double _entryPrice;
        private int _objCounter;

        // Last COMPUTED combined bias (not the position's bias) - compared bar-over-bar to detect a flip.
        // Updated every bar regardless of whether a position is open, so "no signal yet" (null), "long",
        // and "short" are all tracked the same way. See the file header for what NOT resetting this on a
        // stop/target hit means in practice.
        private string _lastCombinedBias;

        // Timestamp of the most recent position close, of any kind - see Cooldown After Close (minutes).
        private DateTime? _lastCloseUtc;

        // ---------------------------------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------------------------------

        protected override void OnStart()
        {
            _execBars = MarketData.GetBars(ExecutionTimeFrame, SymbolName);
            _execBars.BarOpened += OnExecBarOpened;
            _atr = Indicators.AverageTrueRange(_execBars, AtrPeriod, MovingAverageType.Simple);

            _macdTrailBars = MarketData.GetBars(TimeFrame.Minute1, SymbolName);
            _macdTrailBars.BarOpened += OnMacdTrailBarOpened;
            _macdTrail = Indicators.MacdCrossOver(_macdTrailBars.ClosePrices, MacdSlowPeriod, MacdFastPeriod, MacdSignalPeriod);

            if (UseMacdFilter)
            {
                TrySetupMacdSlot(MacdTimeFrame1, out _macdBars1, out _macd1);
                TrySetupMacdSlot(MacdTimeFrame2, out _macdBars2, out _macd2);
                TrySetupMacdSlot(MacdTimeFrame3, out _macdBars3, out _macd3);
            }

            if (UseEmaFilter)
            {
                TrySetupEmaSlot(EmaTimeFrame1, out _emaBars1, out _emaFast1, out _emaSlow1);
                TrySetupEmaSlot(EmaTimeFrame2, out _emaBars2, out _emaFast2, out _emaSlow2);
                TrySetupEmaSlot(EmaTimeFrame3, out _emaBars3, out _emaFast3, out _emaSlow3);
            }

            if (!UseMacdFilter && !UseEmaFilter)
                Print("Warning: both MACD and EMA filters are disabled - this bot will never produce a signal.");

            _openPosition = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == PositionLabel);
            if (_openPosition != null)
            {
                _entryPrice = _openPosition.EntryPrice;
                // Seed the flip-tracker to the recovered position's own direction - if the live confluence
                // has since moved away from it, the very next bar correctly reads that as a flip and closes
                // it, which is the right call: this strategy's only reason to hold a position is the
                // confluence that opened it.
                _lastCombinedBias = _openPosition.TradeType == TradeType.Buy ? "long" : "short";
                Print("Recovered an existing open position on start: " + _openPosition.TradeType);
            }

            Positions.Closed += OnPositionsClosed;
        }

        protected override void OnStop()
        {
            if (_execBars != null) _execBars.BarOpened -= OnExecBarOpened;
            if (_macdTrailBars != null) _macdTrailBars.BarOpened -= OnMacdTrailBarOpened;
            Positions.Closed -= OnPositionsClosed;
        }

        private void TrySetupMacdSlot(TimeFrame tf, out Bars bars, out MacdCrossOver macd)
        {
            bars = null;
            macd = null;
            try
            {
                bars = MarketData.GetBars(tf, SymbolName);
                macd = Indicators.MacdCrossOver(bars.ClosePrices, MacdSlowPeriod, MacdFastPeriod, MacdSignalPeriod);
            }
            catch (Exception ex)
            {
                Print($"Warning: MACD Timeframe {tf} failed to load ({ex.Message}) - this timeframe slot will be skipped.");
            }
        }

        private void TrySetupEmaSlot(TimeFrame tf, out Bars bars, out ExponentialMovingAverage fast, out ExponentialMovingAverage slow)
        {
            bars = null;
            fast = null;
            slow = null;
            try
            {
                bars = MarketData.GetBars(tf, SymbolName);
                fast = Indicators.ExponentialMovingAverage(bars.ClosePrices, EmaFastPeriod);
                slow = Indicators.ExponentialMovingAverage(bars.ClosePrices, EmaSlowPeriod);
            }
            catch (Exception ex)
            {
                Print($"Warning: EMA Timeframe {tf} failed to load ({ex.Message}) - this timeframe slot will be skipped.");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Trend filters
        // ---------------------------------------------------------------------------------------------

        private (string bias, string reason) GetMacdTrendBias()
        {
            var readings = new List<(string tf, string trend)>();

            void Add(string tf, MacdCrossOver macd)
            {
                if (macd == null) return;
                double m = macd.MACD.LastValue;
                double s = macd.Signal.LastValue;
                if (double.IsNaN(m) || double.IsNaN(s)) return;
                readings.Add((tf, m >= s ? "long" : "short"));
            }

            Add(MacdTimeFrame1.ToString(), _macd1);
            Add(MacdTimeFrame2.ToString(), _macd2);
            Add(MacdTimeFrame3.ToString(), _macd3);

            if (readings.Count == 0) return (null, "No MACD readings yet");

            int longCount = readings.Count(r => r.trend == "long");
            int shortCount = readings.Count(r => r.trend == "short");
            string detail = string.Join(", ", readings.Select(r => $"{r.tf}={r.trend}"));

            if (longCount >= MinTimeframesAgreeing) return ("long", $"MACD confluence {longCount}/{readings.Count} bullish ({detail})");
            if (shortCount >= MinTimeframesAgreeing) return ("short", $"MACD confluence {shortCount}/{readings.Count} bearish ({detail})");
            return (null, $"No MACD confluence: {longCount} long / {shortCount} short of {readings.Count} ({detail})");
        }

        private (string bias, string reason) GetEmaTrendBias()
        {
            var readings = new List<(string tf, string trend)>();

            void Add(string tf, ExponentialMovingAverage fast, ExponentialMovingAverage slow)
            {
                if (fast == null || slow == null) return;
                double f = fast.Result.LastValue;
                double s = slow.Result.LastValue;
                if (double.IsNaN(f) || double.IsNaN(s)) return;
                readings.Add((tf, f >= s ? "long" : "short"));
            }

            Add(EmaTimeFrame1.ToString(), _emaFast1, _emaSlow1);
            Add(EmaTimeFrame2.ToString(), _emaFast2, _emaSlow2);
            Add(EmaTimeFrame3.ToString(), _emaFast3, _emaSlow3);

            if (readings.Count == 0) return (null, "No EMA readings yet");

            int longCount = readings.Count(r => r.trend == "long");
            int shortCount = readings.Count(r => r.trend == "short");
            string detail = string.Join(", ", readings.Select(r => $"{r.tf}={r.trend}"));

            if (longCount >= MinTimeframesAgreeing) return ("long", $"EMA confluence {longCount}/{readings.Count} bullish ({detail})");
            if (shortCount >= MinTimeframesAgreeing) return ("short", $"EMA confluence {shortCount}/{readings.Count} bearish ({detail})");
            return (null, $"No EMA confluence: {longCount} long / {shortCount} short of {readings.Count} ({detail})");
        }

        // Requires BOTH filters (when both are enabled) to independently reach confluence AND agree with
        // each other on direction - if only one filter is enabled, that one alone decides.
        private (string bias, string reason) GetCombinedBias()
        {
            if (!UseMacdFilter && !UseEmaFilter) return (null, "Both filters disabled");

            var (macdBias, macdReason) = UseMacdFilter ? GetMacdTrendBias() : (null, "MACD filter disabled");
            var (emaBias, emaReason) = UseEmaFilter ? GetEmaTrendBias() : (null, "EMA filter disabled");

            if (UseMacdFilter && !UseEmaFilter) return (macdBias, macdReason);
            if (!UseMacdFilter && UseEmaFilter) return (emaBias, emaReason);

            if (macdBias != null && macdBias == emaBias)
                return (macdBias, $"MACD+EMA agree {macdBias.ToUpper()} | MACD: {macdReason} | EMA: {emaReason}");

            return (null, $"MACD/EMA disagree or incomplete | MACD: {macdReason} | EMA: {emaReason}");
        }

        // ---------------------------------------------------------------------------------------------
        // Entry / exit orchestration
        // ---------------------------------------------------------------------------------------------

        private void OnExecBarOpened(BarOpenedEventArgs args)
        {
            if (_execBars.Count < 2) return;
            EvaluateTradingLogic(_execBars.Last(1));
        }

        private void EvaluateTradingLogic(Bar xBar)
        {
            var (bias, reason) = GetCombinedBias();

            if (EnableDebugLogging)
                Print($"[DEBUG] Close={xBar.Close:F2} CombinedBias={(bias ?? "none")} ({reason}) | OpenPosition={(_openPosition != null ? _openPosition.TradeType.ToString() : "none")}");

            bool flipped = bias != _lastCombinedBias;
            _lastCombinedBias = bias;

            if (!flipped) return; // nothing changed since the last bar - don't touch anything

            // Close any open position that no longer matches the new bias (including the bias going null -
            // confluence broke down entirely, not just flipped to the opposite side).
            if (_openPosition != null)
            {
                bool positionMatchesBias = (bias == "long" && _openPosition.TradeType == TradeType.Buy)
                                         || (bias == "short" && _openPosition.TradeType == TradeType.Sell);
                if (!positionMatchesBias)
                    CloseWithReason($"Confluence flip away from this position - {reason}", xBar.Close, xBar.OpenTime);
            }

            if (bias == null || _openPosition != null) return; // no actionable direction, or already positioned correctly
            if (bias == "long" && !EnableLongs) return;
            if (bias == "short" && !EnableShorts) return;

            if (_lastCloseUtc != null && (Server.TimeInUtc - _lastCloseUtc.Value) < TimeSpan.FromMinutes(CooldownMinutes))
            {
                if (EnableDebugLogging) Print($"[DEBUG] Standing aside - cooldown after last close not yet elapsed ({(Server.TimeInUtc - _lastCloseUtc.Value).TotalSeconds:F0}s so far, need {CooldownMinutes * 60}s).");
                return;
            }

            OpenPosition(bias == "long" ? TradeType.Buy : TradeType.Sell, xBar, $"MACD+EMA confluence flip {bias.ToUpper()} - {reason}");
        }

        // ---------------------------------------------------------------------------------------------
        // 1min MACD trailing stop / fast exit
        // ---------------------------------------------------------------------------------------------

        private void OnMacdTrailBarOpened(BarOpenedEventArgs args)
        {
            if (_macdTrailBars.Count < 2) return;
            ProcessMacdTrailBar(_macdTrailBars.Last(1));
        }

        // Runs every 1-minute bar close, independent of Locked Execution Timeframe. Two effects on an open
        // position, both keyed off the 1min MACD's OWN crossovers (edge-triggered, not just current state):
        //   - A crossover AGAINST the position's direction closes it immediately - a faster, single-
        //     timeframe early-warning exit than waiting for the full 3-TF combined bias to flip.
        //   - A crossover WITH the position's direction (continued momentum) trails the stop to that
        //     crossover candle's low (longs) / high (shorts), only ever tightening it.
        // No position open -> nothing to manage, just keep the state tracker current for edge detection.
        private void ProcessMacdTrailBar(Bar bar)
        {
            double m = _macdTrail.MACD.LastValue;
            double s = _macdTrail.Signal.LastValue;
            if (double.IsNaN(m) || double.IsNaN(s)) return;

            string state = m >= s ? "long" : "short";
            bool crossed = _macdTrailLastState != null && state != _macdTrailLastState;
            _macdTrailLastState = state;

            if (_openPosition == null || !crossed) return;

            bool isLong = _openPosition.TradeType == TradeType.Buy;
            bool sameDirection = (isLong && state == "long") || (!isLong && state == "short");

            if (!sameDirection)
            {
                CloseWithReason($"1min MACD crossed {state.ToUpper()} against the open {(isLong ? "long" : "short")} - exiting on this flip as configured", bar.Close, bar.OpenTime);
                return;
            }

            if (!UseMacdTrailingStop) return;

            double rawLevel = isLong ? bar.Low : bar.High;
            double candidate = isLong
                ? rawLevel * (1 - MacdTrailBufferPercent / 100.0)
                : rawLevel * (1 + MacdTrailBufferPercent / 100.0);

            double? currentStop = _openPosition.StopLoss;
            bool improves = currentStop == null
                || (isLong && candidate > currentStop.Value)
                || (!isLong && candidate < currentStop.Value);
            if (!improves) return;

            _openPosition.ModifyStopLossPrice(candidate);
            Print($"[TRAIL] Stop trailed to {candidate:F2} (1min MACD same-direction crossover candle {bar.OpenTime:HH:mm}, {(isLong ? "low" : "high")}={rawLevel:F2}).");
        }

        // ---------------------------------------------------------------------------------------------
        // Position management
        // ---------------------------------------------------------------------------------------------

        private void OpenPosition(TradeType type, Bar xBar, string reason)
        {
            double atr = _atr.Result.LastValue;
            if (double.IsNaN(atr) || atr <= 0)
            {
                if (EnableDebugLogging) Print("[DEBUG] Skipping entry - ATR not yet available.");
                return;
            }

            double stopDistance = atr * AtrStopMultiplier;
            double estEntry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double stopPrice = type == TradeType.Buy ? estEntry - stopDistance : estEntry + stopDistance;
            double targetPrice = type == TradeType.Buy ? estEntry + stopDistance * RewardToRiskRatio : estEntry - stopDistance * RewardToRiskRatio;

            double volumeInUnits = CalculateVolume(stopDistance);
            var result = ExecuteMarketOrder(type, SymbolName, volumeInUnits, PositionLabel, null, null, reason);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("Entry failed: " + result.Error);
                return;
            }

            _openPosition = result.Position;
            _entryPrice = result.Position.EntryPrice;

            _openPosition.ModifyStopLossPrice(stopPrice);
            _openPosition.ModifyTakeProfitPrice(targetPrice);

            Print(reason + $" | ATR={atr:F2} StopDist={stopDistance:F2} | Volume: {volumeInUnits} units | FillPrice={_entryPrice:F2} Stop={stopPrice:F2} Target={targetPrice:F2} Equity={Account.Equity:F2}");
            if (ShowReasons)
                DrawReasonLabel(reason, xBar.OpenTime, type == TradeType.Buy ? xBar.Low : xBar.High, type == TradeType.Buy, Color.LimeGreen);
        }

        private double CalculateVolume(double stopDistance)
        {
            if (stopDistance <= 0) return Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(FallbackVolumeLots));

            double riskAmount = Account.FreeMargin * RiskPercent / 100.0;
            double pips = stopDistance / Symbol.PipSize;
            double riskPerLot = pips * Symbol.PipValue;
            if (riskPerLot <= 0) return Symbol.VolumeInUnitsMin;

            double lots = riskAmount / riskPerLot;
            double volumeInUnits = lots * Symbol.LotSize;

            double maxUnits = Symbol.QuantityToVolumeInUnits(MaxVolumeLots);
            volumeInUnits = Math.Min(volumeInUnits, maxUnits);

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin) volumeInUnits = Symbol.VolumeInUnitsMin;

            return volumeInUnits;
        }

        private void CloseWithReason(string reason, double price, DateTime time)
        {
            if (_openPosition == null) return;

            Print(reason);
            if (ShowReasons)
                DrawReasonLabel(reason, time, price, _openPosition.TradeType != TradeType.Buy, Color.Orange);

            ClosePosition(_openPosition);
            _openPosition = null;
            _lastCloseUtc = Server.TimeInUtc;
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != PositionLabel || args.Position.SymbolName != SymbolName) return;
            if (args.Reason != PositionCloseReason.StopLoss && args.Reason != PositionCloseReason.TakeProfit) return;

            string reason = args.Reason == PositionCloseReason.StopLoss
                ? $"Stop loss hit @ {args.Position.StopLoss:F2}"
                : $"Target hit @ {args.Position.TakeProfit:F2}";

            Print(reason);
            if (ShowReasons)
                DrawReasonLabel(reason, Server.TimeInUtc, (args.Reason == PositionCloseReason.StopLoss ? args.Position.StopLoss : args.Position.TakeProfit) ?? 0, args.Position.TradeType != TradeType.Buy, args.Reason == PositionCloseReason.StopLoss ? Color.Red : Color.LimeGreen);

            if (_openPosition != null && _openPosition.Id == args.Position.Id)
            {
                _openPosition = null;
                _lastCloseUtc = Server.TimeInUtc;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Chart drawing
        // ---------------------------------------------------------------------------------------------

        private void DrawReasonLabel(string text, DateTime time, double price, bool above, Color color)
        {
            string name = "Reason_" + _objCounter++;
            var label = Chart.DrawText(name, text, time, price, color);
            label.VerticalAlignment = above ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            label.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }
}
