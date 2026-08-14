// Weekly TP Reversal Window - cTrader cBot (cAlgo.API, C#)
//
// Port of the "Weekly TP Reversal Window Strategy" Pine Script to cTrader Automate, plus a few cTrader-side
// additions beyond the original Pine script (risk-based sizing, take profit, seasonal bias - see below).
// Core trading logic: weekly high/low/open tracking, TP levels (Points or Percent of the weekly extreme),
// a reversal-window day/time/timezone gate (with wrap-around support, e.g. Fri -> Mon), a proximity-based
// reversal/TP-sweep entry trigger, a real broker-side stop loss, a "trail to breakeven then step through
// Trail Step increments" trailing stop with close-based confirmation, a TP-flip exit, a force-exit at the
// window-end day/time, and a week-rollover safety close. Entry/exit reasoning is attached as the
// position's comment and drawn on the chart, matching the Pine version's labels.
//
// Added beyond the Pine script, specifically for cTrader (all optional, off by default unless noted):
//   - Risk-% position sizing (on by default) - volume is calculated from equity and each trade's actual
//     stop distance, capped by a hard max-lots safety backstop.
//   - Take Profit At Confirmed TP Level - a close-confirmed profit target using the same support/
//     resistance-style confirmation as the trailing stop, reusing the existing TP Increment step.
//   - Seasonal Bias - an optional full directional flip by calendar month (two configurable "favor longs"
//     windows, shorts-only outside them). Independent Recent-Trend Override compares this cBot's own
//     closed-trade P&L by direction over a lookback window, only counted if fresh (a separate max-signal-
//     age check, so a trade from weeks ago can't be silently read as "the current trend"). By default the
//     two combine in "priority" mode - Recent-Trend wins when it has a fresh, significant reading,
//     otherwise it falls back to Seasonal Bias. Require Both Biases To Match switches to strict AND mode:
//     both must be on and agree, or the trade is skipped rather than guessed.
//
// NOT ported: the Anchored Volume Profile (purely visual, no effect on trading decisions - skipped by
// request to keep this file focused on the trading logic).
//
// Platform differences worth knowing before you trade this live:
//   - The stop loss and trailing stop use a REAL broker-side stop order (Position.ModifyStopLossPrice),
//     which is more accurate than the Pine indicator's simulated wick-check - the broker fills it exactly
//     when hit, intrabar, same as any other stop order on this platform.
//   - "Points" in every input below are RAW PRICE UNITS added to/subtracted from price (matching the Pine
//     script, which was tuned on an index/crypto-style instrument), NOT pips and NOT Symbol.PipSize
//     multiples. If your instrument's pip size differs from 1 price unit, re-tune the point-based inputs
//     or switch to Percent mode.
//   - "New week" is detected by a configurable WeekStartDay (default Monday) transition on the
//     Calculation Timeframe, since Pine's time("W") week boundary can vary by exchange/instrument. Set it
//     to match your instrument's actual weekly session start.
//   - This file has not been compiled inside cTrader - the cAlgo API has shifted slightly across versions.
//     Paste it into cTrader Automate; if the compiler flags a method/property name, it is almost always a
//     one-line signature fix (e.g. an overload with a slightly different parameter list on your version).
//
// Build/test in the cTrader backtester before running on a live or demo account.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum UnitType
    {
        Points,
        Percent
    }

    public enum Month
    {
        January = 1, February = 2, March = 3, April = 4, May = 5, June = 6,
        July = 7, August = 8, September = 9, October = 10, November = 11, December = 12
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeeklyTPReversalWindowCBot : Robot
    {
        private const string PositionLabel = "WeeklyTPReversal";

        // ---------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------

        [Parameter("TP / Threshold Units", DefaultValue = UnitType.Percent, Group = "TP Levels")]
        public UnitType TpUnit { get; set; }

        [Parameter("TP Increment (points)", DefaultValue = 500, MinValue = 1, Group = "TP Levels")]
        public double TpStepPoints { get; set; }

        [Parameter("TP Increment (% of price)", DefaultValue = 1.66, MinValue = 0.01, Group = "TP Levels")]
        public double TpStepPercent { get; set; }

        [Parameter("Number of TP Levels", DefaultValue = 9, MinValue = 1, MaxValue = 10, Group = "TP Levels")]
        public int TpLevelsCount { get; set; }

        [Parameter("Trend / Reversal Threshold (points)", DefaultValue = 250, MinValue = 1, Group = "TP Levels")]
        public double TrendThresholdPoints { get; set; }

        [Parameter("Trend / Reversal Threshold (% of price)", DefaultValue = 0.83, MinValue = 0.01, Group = "TP Levels")]
        public double TrendThresholdPercent { get; set; }

        [Parameter("Calculation Timeframe", DefaultValue = "Minute5", Group = "TP Levels")]
        public TimeFrame CalcTimeFrame { get; set; }

        [Parameter("Week Start Day", DefaultValue = DayOfWeek.Monday, Group = "TP Levels",
            Description = "Which weekday the weekly high/low/open resets on. Pine's time(\"W\") boundary varies by exchange/instrument - set this to match yours.")]
        public DayOfWeek WeekStartDay { get; set; }

        [Parameter("Show Weekly High/Low/Open Lines", DefaultValue = true, Group = "TP Levels")]
        public bool ShowWeekLines { get; set; }

        [Parameter("Show Reversal Warning Background", DefaultValue = true, Group = "Reversal Warning")]
        public bool ShowWarning { get; set; }

        [Parameter("Warning Timezone (IANA or Windows id)", DefaultValue = "Europe/London", Group = "Reversal Warning",
            Description = "e.g. 'Europe/London' (IANA) or 'GMT Standard Time' (Windows). Falls back to UTC with a log warning if not found on this system.")]
        public string WarningTimeZoneId { get; set; }

        [Parameter("Warning Start Day", DefaultValue = DayOfWeek.Wednesday, Group = "Reversal Warning")]
        public DayOfWeek WarnStartDay { get; set; }

        [Parameter("Warning Start Hour", DefaultValue = 13, MinValue = 0, MaxValue = 23, Group = "Reversal Warning")]
        public int WarnStartHour { get; set; }

        [Parameter("Warning Start Minute", DefaultValue = 15, MinValue = 0, MaxValue = 59, Group = "Reversal Warning")]
        public int WarnStartMinute { get; set; }

        [Parameter("Warning End Day", DefaultValue = DayOfWeek.Friday, Group = "Reversal Warning")]
        public DayOfWeek WarnEndDay { get; set; }

        [Parameter("Warning End Hour", DefaultValue = 9, MinValue = 0, MaxValue = 23, Group = "Reversal Warning")]
        public int WarnEndHour { get; set; }

        [Parameter("Warning End Minute", DefaultValue = 45, MinValue = 0, MaxValue = 59, Group = "Reversal Warning")]
        public int WarnEndMinute { get; set; }

        [Parameter("Use Seasonal Bias", DefaultValue = false, Group = "Seasonal Bias",
            Description = "Full flip: only longs are taken inside Window 1/Window 2, only shorts everywhere else.")]
        public bool UseSeasonalBias { get; set; }

        [Parameter("Long Window 1 Start Month", DefaultValue = Month.April, Group = "Seasonal Bias")]
        public Month SeasonalLongStartMonth1 { get; set; }

        [Parameter("Long Window 1 End Month", DefaultValue = Month.June, Group = "Seasonal Bias")]
        public Month SeasonalLongEndMonth1 { get; set; }

        [Parameter("Long Window 2 Start Month", DefaultValue = Month.September, Group = "Seasonal Bias")]
        public Month SeasonalLongStartMonth2 { get; set; }

        [Parameter("Long Window 2 End Month", DefaultValue = Month.November, Group = "Seasonal Bias")]
        public Month SeasonalLongEndMonth2 { get; set; }

        [Parameter("Use Recent-Trend Override", DefaultValue = false, Group = "Seasonal Bias",
            Description = "Tracks this cBot's own closed trades by direction over the lookback window. Works standalone, independent of Use Seasonal Bias - if one side's P&L beats the other by more than the % factor below (as a % of equity), that's treated as the current macro trend. When Use Seasonal Bias is also on and Require Both Biases To Match is off, this takes priority over the calendar bias whenever it has a fresh, significant reading.")]
        public bool UseRecentTrendDetection { get; set; }

        [Parameter("Recent-Trend Lookback (days)", DefaultValue = 7, MinValue = 1, Group = "Seasonal Bias",
            Description = "Closed trades within this many days are summed (by direction) to compute the recent P&L comparison.")]
        public int RecentTrendLookbackDays { get; set; }

        [Parameter("Recent-Trend Max Signal Age (days)", DefaultValue = 7, MinValue = 1, Group = "Seasonal Bias",
            Description = "Separate from the Lookback above: the signal is only used at all if at least one trade closed within this many days. Stops a trade from weeks ago - still technically inside a longer Lookback window - from being read as \"the current trend\" when nothing has traded recently (e.g. no trade for 2+ weeks shouldn't silently reuse a 2-week-old result).")]
        public int RecentTrendMaxSignalAgeDays { get; set; }

        [Parameter("Recent-Trend Override Factor (% of equity)", DefaultValue = 1.0, MinValue = 0.01, Group = "Seasonal Bias",
            Description = "How much one direction's recent P&L must beat the other's, as a % of current equity, before it's treated as significant enough to count as a signal.")]
        public double RecentTrendOverridePercent { get; set; }

        [Parameter("Require Both Biases To Match", DefaultValue = false, Group = "Seasonal Bias",
            Description = "AND mode: a direction is only allowed if Use Seasonal Bias AND Use Recent-Trend Override are BOTH on and BOTH currently agree on it. If either is off, stale, or they disagree, no trade is taken in either direction - it skips rather than guesses. Requires both toggles above to be on to ever trade.")]
        public bool RequireBiasMatch { get; set; }

        [Parameter("Trade Longs (bearish-into-window fade)", DefaultValue = true, Group = "Strategy")]
        public bool EnableLongs { get; set; }

        [Parameter("Trade Shorts (bullish-into-window fade)", DefaultValue = true, Group = "Strategy")]
        public bool EnableShorts { get; set; }

        [Parameter("Enter Only At A Reversal/TP Sweep", DefaultValue = true, Group = "Strategy")]
        public bool UseTPEntry { get; set; }

        [Parameter("Also Enter At The Reversal Threshold", DefaultValue = false, Group = "Strategy",
            Description = "On = the shallow reversal-threshold touch (e.g. ~250pt/0.83%) can trigger the entry, not just a full TP level.")]
        public bool EntryAtReversal { get; set; }

        [Parameter("Entry Proximity %", DefaultValue = 0.088, MinValue = 0.01, Group = "Strategy",
            Description = "How close (as a % of price) the exec bar's wick must come to a level to trigger the entry.")]
        public double EntryProximityPercent { get; set; }

        [Parameter("Weekly-Open Tolerance %", DefaultValue = 0.0, MinValue = 0, Group = "Strategy")]
        public double WeeklyOpenTolerancePercent { get; set; }

        [Parameter("Risk % Of Equity Per Trade", DefaultValue = 1.0, MinValue = 0.01, Group = "Strategy",
            Description = "Position size is calculated from this % of current equity and the trade's actual stop-loss distance, so every trade risks the same amount regardless of how wide the stop is that week. Requires Use Stop Loss to be on.")]
        public double RiskPercent { get; set; }

        [Parameter("Max Position Size (lots)", DefaultValue = 5.0, MinValue = 0.01, Group = "Strategy",
            Description = "Hard safety cap - the risk-% calculation is never allowed to size a position above this, even if the stop is unusually tight or the risk % / equity is mis-set.")]
        public double MaxVolumeLots { get; set; }

        [Parameter("Fallback Volume (lots) If Stop Loss Disabled", DefaultValue = 0.10, MinValue = 0.01, Group = "Strategy",
            Description = "Risk-% sizing needs a stop-loss distance to size off. Used only when Use Stop Loss is off, since there's nothing to size risk against.")]
        public double FallbackVolumeLots { get; set; }

        [Parameter("Use Stop Loss", DefaultValue = true, Group = "Strategy")]
        public bool UseStopLoss { get; set; }

        [Parameter("Stop Loss Units", DefaultValue = UnitType.Points, Group = "Strategy")]
        public UnitType StopUnit { get; set; }

        [Parameter("Stop Loss (points)", DefaultValue = 275, MinValue = 1, Group = "Strategy")]
        public double StopLossPoints { get; set; }

        [Parameter("Stop Loss (% of entry)", DefaultValue = 0.89, MinValue = 0.01, Group = "Strategy")]
        public double StopLossPercent { get; set; }

        [Parameter("Trail Stop To Breakeven + Step Increments", DefaultValue = false, Group = "Strategy")]
        public bool UseTrailStop { get; set; }

        [Parameter("Trail Step Units", DefaultValue = UnitType.Points, Group = "Strategy")]
        public UnitType TrailStepUnit { get; set; }

        [Parameter("Trail Step (points)", DefaultValue = 500, MinValue = 1, Group = "Strategy")]
        public double TrailStepPoints { get; set; }

        [Parameter("Trail Step (% of price)", DefaultValue = 1.66, MinValue = 0.01, Group = "Strategy")]
        public double TrailStepPercent { get; set; }

        [Parameter("Stop Trail Day", DefaultValue = DayOfWeek.Thursday, Group = "Strategy")]
        public DayOfWeek TrailDay { get; set; }

        [Parameter("Stop Trail Hour", DefaultValue = 12, MinValue = 0, MaxValue = 23, Group = "Strategy")]
        public int TrailHour { get; set; }

        [Parameter("Stop Trail Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59, Group = "Strategy")]
        public int TrailMinute { get; set; }

        [Parameter("Take Profit At Confirmed TP Level", DefaultValue = false, Group = "Strategy",
            Description = "Uses the SAME close-confirmation logic as the reversal threshold and trailing stop: exits once price CLOSES past the target TP increment (the level effectively becomes support/resistance), not on a raw price touch. Reuses the existing TP Increment step - no separate size to tune.")]
        public bool UseTakeProfit { get; set; }

        [Parameter("Take Profit Levels Ahead", DefaultValue = 1, MinValue = 1, MaxValue = 10, Group = "Strategy",
            Description = "How many TP increments past the weekly extreme must be CLOSE-confirmed before taking profit. 1 = the very next level; higher = wait for a deeper, more confirmed move.")]
        public int TakeProfitLevels { get; set; }

        [Parameter("Show Entry/Exit Reasoning Labels", DefaultValue = true, Group = "Strategy")]
        public bool ShowReasons { get; set; }

        [Parameter("Force-Exit Hour (window-end day)", DefaultValue = 21, MinValue = 0, MaxValue = 23, Group = "Strategy")]
        public int ExitHour { get; set; }

        [Parameter("Force-Exit Minute (window-end day)", DefaultValue = 45, MinValue = 0, MaxValue = 59, Group = "Strategy")]
        public int ExitMinute { get; set; }

        [Parameter("Lock Execution To A Fixed Timeframe", DefaultValue = true, Group = "Strategy",
            Description = "On = evaluate entries/exits/window off a fixed timeframe below, so changing the chart timeframe doesn't change results. Off = evaluate on every tick using this chart's own bars.")]
        public bool LockExecutionTimeframe { get; set; }

        [Parameter("Locked Execution Timeframe", DefaultValue = "Minute15", Group = "Strategy")]
        public TimeFrame ExecutionTimeFrame { get; set; }

        // ---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        private Bars _calcBars;
        private Bars _execBars;
        private TimeZoneInfo _warnTz;

        private DateTime? _lastWeekAnchor;
        private double _weekHigh = double.NaN;
        private double _weekLow = double.NaN;
        private double _weekOpen = double.NaN;
        private string _mode; // "up", "down", or null
        private double _tpStepUp, _tpStepDn, _threshUp, _threshDn;

        private bool _inWarnWindowPrev;
        private string _warnTrend; // captured direction entering the window
        private bool _tradedWindow;

        private Position _openPosition;
        private bool _stopTrailed;

        private class ClosedTradeRecord
        {
            public DateTime ClosedAtUtc;
            public TradeType Direction;
            public double NetProfit;
        }
        private readonly List<ClosedTradeRecord> _recentClosedTrades = new List<ClosedTradeRecord>();

        private int _objCounter;

        // ---------------------------------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------------------------------

        protected override void OnStart()
        {
            _warnTz = ResolveTimeZone(WarningTimeZoneId);

            _calcBars = MarketData.GetBars(CalcTimeFrame, SymbolName);
            _calcBars.BarOpened += OnCalcBarOpened;

            if (LockExecutionTimeframe)
            {
                _execBars = MarketData.GetBars(ExecutionTimeFrame, SymbolName);
                _execBars.BarOpened += OnExecBarOpened;
            }

            Positions.Closed += OnPositionsClosed;

            // Recover an already-open position if the cBot was restarted mid-trade.
            _openPosition = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == PositionLabel);
            if (_openPosition != null)
            {
                _tradedWindow = true;
                Print("Recovered an existing open position on start: " + _openPosition.TradeType);
            }
        }

        protected override void OnBar()
        {
            if (LockExecutionTimeframe) return; // driven by OnExecBarOpened instead
            if (Bars.Count < 2) return;
            EvaluateTradingLogic(Bars.Last(1));
        }

        protected override void OnStop()
        {
            if (_calcBars != null) _calcBars.BarOpened -= OnCalcBarOpened;
            if (_execBars != null) _execBars.BarOpened -= OnExecBarOpened;
            Positions.Closed -= OnPositionsClosed;
        }

        private void OnExecBarOpened(BarOpenedEventArgs args)
        {
            if (_execBars.Count < 2) return;
            EvaluateTradingLogic(_execBars.Last(1));
        }

        private TimeZoneInfo ResolveTimeZone(string id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                Print($"Warning: timezone '{id}' not found on this system - falling back to UTC. " +
                      "Try an IANA id like 'Europe/London' or a Windows id like 'GMT Standard Time'.");
                return TimeZoneInfo.Utc;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Weekly high/low/open + TP-level tracking (runs on each closed Calculation Timeframe bar)
        // ---------------------------------------------------------------------------------------------

        private void OnCalcBarOpened(BarOpenedEventArgs args)
        {
            if (_calcBars.Count < 2) return;
            ProcessCalcBar(_calcBars.Last(1));
        }

        private bool IsNewWeek(DateTime barOpenTimeUtc)
        {
            DateTime anchor = barOpenTimeUtc.Date;
            while (anchor.DayOfWeek != WeekStartDay) anchor = anchor.AddDays(-1);

            bool isNew = _lastWeekAnchor == null || anchor != _lastWeekAnchor.Value;
            _lastWeekAnchor = anchor;
            return isNew;
        }

        private void ProcessCalcBar(Bar bar)
        {
            bool newWeek = IsNewWeek(bar.OpenTime);

            if (newWeek || double.IsNaN(_weekHigh))
            {
                // A lingering position across a week boundary is a safety-net close, same as the Pine
                // script's week-rollover fallback (the force-exit on the window-end day should normally
                // have already closed it).
                if (_openPosition != null)
                    CloseWithReason("Week rollover - safety close", bar.Close, bar.OpenTime);

                _weekHigh = bar.High;
                _weekLow = bar.Low;
                _weekOpen = bar.Open;
                _mode = null;
                _warnTrend = null;
            }
            else
            {
                if (bar.High > _weekHigh) _weekHigh = bar.High;
                if (bar.Low < _weekLow) _weekLow = bar.Low;
            }

            // Effective TP increment / trend threshold - Percent mode is an exact % of the relevant
            // weekly extreme, so levels stay proportional across a long backtest.
            _tpStepUp = TpUnit == UnitType.Percent && !double.IsNaN(_weekLow) ? _weekLow * TpStepPercent / 100.0 : TpStepPoints;
            _tpStepDn = TpUnit == UnitType.Percent && !double.IsNaN(_weekHigh) ? _weekHigh * TpStepPercent / 100.0 : TpStepPoints;
            _threshUp = TpUnit == UnitType.Percent && !double.IsNaN(_weekLow) ? _weekLow * TrendThresholdPercent / 100.0 : TrendThresholdPoints;
            _threshDn = TpUnit == UnitType.Percent && !double.IsNaN(_weekHigh) ? _weekHigh * TrendThresholdPercent / 100.0 : TrendThresholdPoints;

            // Trend / reversal, both sides, always. (Note: the Pine script also tracks a legHigh/legLow +
            // confirmedUp/confirmedDown "TP hit count" here, but that machinery only ever feeds its TP-level
            // chart labels - it has no effect on any entry/exit decision - so it's intentionally left out of
            // this port along with the rest of the skipped visuals.)
            bool bearSignal = !double.IsNaN(_weekHigh) && !double.IsNaN(_weekLow) && bar.Close <= _weekHigh - _threshDn;
            bool bullSignal = !double.IsNaN(_weekHigh) && !double.IsNaN(_weekLow) && bar.Close >= _weekLow + _threshUp;

            if (bullSignal && !bearSignal) _mode = "up";
            else if (bearSignal && !bullSignal) _mode = "down";

            if (ShowWeekLines) DrawWeekLines(bar.OpenTime);
        }

        // ---------------------------------------------------------------------------------------------
        // Reversal window
        // ---------------------------------------------------------------------------------------------

        private (bool inWindow, DayOfWeek dow, int hour, int minute) GetWindowState(DateTime timeUtc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc), _warnTz);
            DayOfWeek dow = local.DayOfWeek;
            int hour = local.Hour;
            int minute = local.Minute;

            int startIdx = (int)WarnStartDay;
            int endIdx = (int)WarnEndDay;
            int curIdx = (int)dow;

            bool afterStart = curIdx > startIdx || (curIdx == startIdx && (hour > WarnStartHour || (hour == WarnStartHour && minute >= WarnStartMinute)));
            bool beforeEnd = curIdx < endIdx || (curIdx == endIdx && (hour < WarnEndHour || (hour == WarnEndHour && minute < WarnEndMinute)));

            // Normal window (start day on/before end day): both must hold. Wrapping window (start day
            // after end day, e.g. Fri -> Mon): either holds.
            bool inWindow = startIdx <= endIdx ? (afterStart && beforeEnd) : (afterStart || beforeEnd);
            return (inWindow, dow, hour, minute);
        }

        // ---------------------------------------------------------------------------------------------
        // Entry / exit orchestration (runs once per locked exec bar, or every chart bar when unlocked)
        // ---------------------------------------------------------------------------------------------

        private void EvaluateTradingLogic(Bar xBar)
        {
            if (double.IsNaN(_weekHigh) || double.IsNaN(_weekLow)) return;

            var (inWindow, dow, hour, minute) = GetWindowState(xBar.OpenTime);

            bool windowStart = inWindow && !_inWarnWindowPrev;
            if (windowStart)
            {
                _tradedWindow = false;
                _warnTrend = _mode;
                if (ShowWarning) DrawWindowMarkers(xBar.OpenTime, _warnTrend);
            }

            string dir = _warnTrend ?? _mode;

            // Reversal/TP-sweep proximity entry gate - first (shallowest) match wins.
            bool nearTP = false;
            string nearReason = null;
            if (dir == "down")
            {
                if (EntryAtReversal)
                {
                    double rlvl = _weekHigh - _threshDn;
                    if (rlvl > 0 && Math.Abs(xBar.Low - rlvl) <= rlvl * EntryProximityPercent / 100.0)
                    {
                        nearTP = true;
                        nearReason = $"reversal threshold ({_threshDn:F0}pt) off weekly high, touched {rlvl:F2}";
                    }
                }
                for (int i = 1; i <= TpLevelsCount && nearReason == null; i++)
                {
                    double lvl = _weekHigh - _tpStepDn * i;
                    if (lvl > 0 && Math.Abs(xBar.Low - lvl) <= lvl * EntryProximityPercent / 100.0)
                    {
                        nearTP = true;
                        nearReason = $"TP{i} sweep ({_tpStepDn * i:F0}pt) off weekly high, touched {lvl:F2}";
                    }
                }
            }
            else if (dir == "up")
            {
                if (EntryAtReversal)
                {
                    double rlvl = _weekLow + _threshUp;
                    if (rlvl > 0 && Math.Abs(xBar.High - rlvl) <= rlvl * EntryProximityPercent / 100.0)
                    {
                        nearTP = true;
                        nearReason = $"reversal threshold ({_threshUp:F0}pt) off weekly low, touched {rlvl:F2}";
                    }
                }
                for (int i = 1; i <= TpLevelsCount && nearReason == null; i++)
                {
                    double lvl = _weekLow + _tpStepUp * i;
                    if (lvl > 0 && Math.Abs(xBar.High - lvl) <= lvl * EntryProximityPercent / 100.0)
                    {
                        nearTP = true;
                        nearReason = $"TP{i} sweep ({_tpStepUp * i:F0}pt) off weekly low, touched {lvl:F2}";
                    }
                }
            }
            bool entryTrigger = UseTPEntry ? nearTP : true;

            bool openOkLong = !double.IsNaN(_weekOpen) && xBar.Close < _weekOpen * (1 + WeeklyOpenTolerancePercent / 100.0);
            bool openOkShort = !double.IsNaN(_weekOpen) && xBar.Close > _weekOpen * (1 - WeeklyOpenTolerancePercent / 100.0);

            (string bias, string biasReason) = GetDirectionalBias(xBar.OpenTime);
            bool longAllowed = EnableLongs && (bias == null || bias == "long");
            bool shortAllowed = EnableShorts && (bias == null || bias == "short");

            if (inWindow && !_tradedWindow && _openPosition == null && dir != null && entryTrigger)
            {
                if (dir == "down" && longAllowed && openOkLong)
                {
                    string reason = "LONG - bearish into window, reversal UP: " +
                                     (UseTPEntry ? nearReason : "window start (no TP-sweep filter)") +
                                     (biasReason != null ? " | " + biasReason : "");
                    OpenPosition(TradeType.Buy, xBar, reason);
                    _tradedWindow = true;
                }
                else if (dir == "up" && shortAllowed && openOkShort)
                {
                    string reason = "SHORT - bullish into window, reversal DOWN: " +
                                     (UseTPEntry ? nearReason : "window start (no TP-sweep filter)") +
                                     (biasReason != null ? " | " + biasReason : "");
                    OpenPosition(TradeType.Sell, xBar, reason);
                    _tradedWindow = true;
                }
                else if (dir == "down" && !longAllowed && EnableLongs && openOkLong)
                {
                    Print($"Entry skipped - LONG signal blocked by directional bias ({biasReason})");
                }
                else if (dir == "up" && !shortAllowed && EnableShorts && openOkShort)
                {
                    Print($"Entry skipped - SHORT signal blocked by directional bias ({biasReason})");
                }
            }

            // ---- Exit ----
            bool afterWindowEnd = dow == WarnEndDay && (hour > WarnEndHour || (hour == WarnEndHour && minute >= WarnEndMinute));
            bool forceExit = dow == WarnEndDay && (hour > ExitHour || (hour == ExitHour && minute >= ExitMinute));

            UpdateTrailingStop(xBar, dow, hour, minute);
            CheckTakeProfit(xBar);

            if (_openPosition != null && afterWindowEnd)
            {
                if (_openPosition.TradeType == TradeType.Buy && _mode == "down")
                    CloseWithReason("LONG exit - trend flipped DOWN (up-TP became resistance)", xBar.Close, xBar.OpenTime);
                else if (_openPosition.TradeType == TradeType.Sell && _mode == "up")
                    CloseWithReason("SHORT exit - trend flipped UP (down-TP became support)", xBar.Close, xBar.OpenTime);
            }

            if (_openPosition != null && forceExit)
                CloseWithReason("Force exit - window-end time reached, no weekend hold", xBar.Close, xBar.OpenTime);

            _inWarnWindowPrev = inWindow;
        }

        // ---------------------------------------------------------------------------------------------
        // Position management
        // ---------------------------------------------------------------------------------------------

        private void OpenPosition(TradeType type, Bar xBar, string reason)
        {
            // Volume must be decided BEFORE the order goes out, so the stop distance is estimated off the
            // current market price (Ask for a buy, Bid for a sell). The real entry may differ slightly by
            // spread/slippage - the stop itself is set from the actual fill price right after, in
            // SetInitialStop(), so only the SIZE (not the stop level) relies on this estimate.
            double estEntry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double estStopDist = UseStopLoss
                ? (StopUnit == UnitType.Percent ? estEntry * StopLossPercent / 100.0 : StopLossPoints)
                : 0;

            double volumeInUnits = UseStopLoss && estStopDist > 0
                ? CalculateVolume(estStopDist)
                : Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(FallbackVolumeLots));

            var result = ExecuteMarketOrder(type, SymbolName, volumeInUnits, PositionLabel, null, null, reason);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("Entry failed: " + result.Error);
                return;
            }

            _openPosition = result.Position;
            _stopTrailed = false;
            SetInitialStop();

            Print(reason + $" | Volume: {volumeInUnits} units");
            if (ShowReasons)
                DrawReasonLabel(reason, xBar.OpenTime, type == TradeType.Buy ? xBar.Low : xBar.High, type == TradeType.Buy, Color.LimeGreen);
        }

        // Sizes the position so that a stop-loss hit at `stopDistance` away from entry loses RiskPercent%
        // of current equity, capped at MaxVolumeLots as a hard safety backstop.
        private double CalculateVolume(double stopDistance)
        {
            if (stopDistance <= 0) return Symbol.VolumeInUnitsMin;

            double riskAmount = Account.Equity * RiskPercent / 100.0;

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

        private void SetInitialStop()
        {
            if (!UseStopLoss || _openPosition == null) return;

            double entry = _openPosition.EntryPrice;
            double dist = StopUnit == UnitType.Percent ? entry * StopLossPercent / 100.0 : StopLossPoints;
            double level = _openPosition.TradeType == TradeType.Buy ? entry - dist : entry + dist;
            _openPosition.ModifyStopLossPrice(level);
        }

        private void UpdateTrailingStop(Bar xBar, DayOfWeek dow, int hour, int minute)
        {
            if (_openPosition == null || !UseStopLoss || !UseTrailStop) return;

            int trailIdx = (int)TrailDay;
            int curIdx = (int)dow;
            bool pastTrailTime = curIdx > trailIdx || (curIdx == trailIdx && (hour > TrailHour || (hour == TrailHour && minute >= TrailMinute)));
            if (!pastTrailTime) return;

            double entry = _openPosition.EntryPrice;
            double trailStepUp = TrailStepUnit == UnitType.Percent && !double.IsNaN(_weekLow) ? _weekLow * TrailStepPercent / 100.0 : TrailStepPoints;
            double trailStepDn = TrailStepUnit == UnitType.Percent && !double.IsNaN(_weekHigh) ? _weekHigh * TrailStepPercent / 100.0 : TrailStepPoints;

            double? currentSL = _openPosition.StopLoss;

            if (_openPosition.TradeType == TradeType.Buy)
            {
                // "Smart" confirmation: requires the CLOSE, not just a wick, to have travelled that many
                // steps past the weekly low - mirrors the close-based reversal-threshold logic above.
                int steps = trailStepUp > 0 ? (int)Math.Floor(Math.Max(xBar.Close - _weekLow, 0) / trailStepUp) : 0;
                double target = entry; // breakeven baseline
                if (steps > 0) target = Math.Max(target, _weekLow + trailStepUp * steps);

                if (currentSL == null || target > currentSL.Value)
                {
                    _openPosition.ModifyStopLossPrice(target);
                    _stopTrailed = true;
                    if (ShowReasons) DrawTrailMarker(xBar, target);
                }
            }
            else
            {
                int steps = trailStepDn > 0 ? (int)Math.Floor(Math.Max(_weekHigh - xBar.Close, 0) / trailStepDn) : 0;
                double target = entry;
                if (steps > 0) target = Math.Min(target, _weekHigh - trailStepDn * steps);

                if (currentSL == null || target < currentSL.Value)
                {
                    _openPosition.ModifyStopLossPrice(target);
                    _stopTrailed = true;
                    if (ShowReasons) DrawTrailMarker(xBar, target);
                }
            }
        }

        // Take profit using the SAME close-confirmation approach as the reversal threshold and the
        // trailing stop: it doesn't fire on a raw price touch, it requires the CLOSE to have travelled
        // TakeProfitLevels increments past the weekly extreme (i.e. that level has effectively become
        // support/resistance), reusing the same TP Increment step as the main TP ladder. Always active
        // once in a trade (not gated to the window-end, unlike the TP-flip exit below).
        private void CheckTakeProfit(Bar xBar)
        {
            if (_openPosition == null || !UseTakeProfit) return;

            if (_openPosition.TradeType == TradeType.Buy)
            {
                int steps = _tpStepUp > 0 ? (int)Math.Floor(Math.Max(xBar.Close - _weekLow, 0) / _tpStepUp) : 0;
                if (steps >= TakeProfitLevels)
                {
                    double level = _weekLow + _tpStepUp * TakeProfitLevels;
                    CloseWithReason($"Take profit - TP{TakeProfitLevels} confirmed by close (support formed @ {level:F2})", xBar.Close, xBar.OpenTime);
                }
            }
            else
            {
                int steps = _tpStepDn > 0 ? (int)Math.Floor(Math.Max(_weekHigh - xBar.Close, 0) / _tpStepDn) : 0;
                if (steps >= TakeProfitLevels)
                {
                    double level = _weekHigh - _tpStepDn * TakeProfitLevels;
                    CloseWithReason($"Take profit - TP{TakeProfitLevels} confirmed by close (resistance formed @ {level:F2})", xBar.Close, xBar.OpenTime);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Seasonal / recent-trend directional bias
        // ---------------------------------------------------------------------------------------------

        // Returns the allowed direction ("long"/"short"), "none" to block both directions, or null for no
        // restriction - plus a human-readable reason to fold into the entry label/log.
        //
        // The two signals are computed independently, so Recent-Trend works standalone regardless of
        // whether Seasonal Bias is on. How they combine depends on Require Both Biases To Match:
        //   - Off (default, "priority" mode): Recent-Trend wins whenever it has a fresh, significant
        //     reading (live evidence beats a static calendar assumption); otherwise falls back to
        //     Seasonal Bias if that's on; otherwise no restriction.
        //   - On ("AND" mode): both must be enabled AND currently agree on the same direction, or no
        //     trade is taken in either direction - it skips rather than guesses.
        private (string bias, string reason) GetDirectionalBias(DateTime timeUtc)
        {
            (string bias, string reason) recent = GetRecentTrendBias();
            (string bias, string reason) seasonal = UseSeasonalBias ? GetSeasonalBias(timeUtc) : (null, null);

            if (RequireBiasMatch)
            {
                if (recent.bias != null && seasonal.bias != null && recent.bias == seasonal.bias)
                    return (recent.bias, $"Bias match: {recent.reason} AND {seasonal.reason}");

                if (UseSeasonalBias || UseRecentTrendDetection)
                    return ("none", "Bias match required but signals disagree or one is unavailable - skipping");

                return (null, null);
            }

            if (recent.bias != null) return recent;
            if (seasonal.bias != null) return seasonal;
            return (null, null);
        }

        private (string bias, string reason) GetSeasonalBias(DateTime timeUtc)
        {
            bool favorLong = InSeasonalLongWindow((Month)timeUtc.Month);
            return favorLong
                ? ("long", $"Seasonal bias: LONG window ({SeasonalLongStartMonth1}-{SeasonalLongEndMonth1} / {SeasonalLongStartMonth2}-{SeasonalLongEndMonth2})")
                : ("short", "Seasonal bias: SHORT (outside long windows)");
        }

        private (string bias, string reason) GetRecentTrendBias()
        {
            if (!UseRecentTrendDetection || !HasFreshRecentTrendData()) return (null, null);

            var (longPnl, shortPnl) = GetRecentPnL();
            double threshold = Account.Equity * RecentTrendOverridePercent / 100.0;

            if (shortPnl - longPnl >= threshold)
                return ("short", $"Recent-trend: shorts {shortPnl:F0} vs longs {longPnl:F0} over last {RecentTrendLookbackDays}d");
            if (longPnl - shortPnl >= threshold)
                return ("long", $"Recent-trend: longs {longPnl:F0} vs shorts {shortPnl:F0} over last {RecentTrendLookbackDays}d");

            return (null, null);
        }

        private bool InSeasonalLongWindow(Month month)
        {
            bool InRange(Month m, Month start, Month end) =>
                start <= end ? m >= start && m <= end : m >= start || m <= end; // wrap-around support (e.g. Nov -> Feb)

            return InRange(month, SeasonalLongStartMonth1, SeasonalLongEndMonth1)
                || InRange(month, SeasonalLongStartMonth2, SeasonalLongEndMonth2);
        }

        private void PruneOldClosedTrades()
        {
            // Prune against whichever window is longer, so a record isn't dropped by the Lookback prune
            // before the (potentially stricter) Max Signal Age freshness check ever sees it.
            int keepDays = Math.Max(RecentTrendLookbackDays, RecentTrendMaxSignalAgeDays);
            DateTime cutoff = Server.TimeInUtc.AddDays(-keepDays);
            _recentClosedTrades.RemoveAll(t => t.ClosedAtUtc < cutoff);
        }

        // The signal is only considered fresh if at least one trade closed within RecentTrendMaxSignalAgeDays -
        // separate from (and typically stricter than) the Lookback window used to sum P&L. Prevents a trade
        // from weeks ago being read as "the current trend" just because it's still inside a longer lookback.
        private bool HasFreshRecentTrendData()
        {
            PruneOldClosedTrades();
            DateTime freshCutoff = Server.TimeInUtc.AddDays(-RecentTrendMaxSignalAgeDays);
            return _recentClosedTrades.Any(t => t.ClosedAtUtc >= freshCutoff);
        }

        private (double longPnl, double shortPnl) GetRecentPnL()
        {
            DateTime cutoff = Server.TimeInUtc.AddDays(-RecentTrendLookbackDays);
            var inWindow = _recentClosedTrades.Where(t => t.ClosedAtUtc >= cutoff).ToList();
            double longPnl = inWindow.Where(t => t.Direction == TradeType.Buy).Sum(t => t.NetProfit);
            double shortPnl = inWindow.Where(t => t.Direction == TradeType.Sell).Sum(t => t.NetProfit);
            return (longPnl, shortPnl);
        }

        private void CloseWithReason(string reason, double price, DateTime time)
        {
            if (_openPosition == null) return;

            Print(reason);
            if (ShowReasons)
                DrawReasonLabel(reason, time, price, _openPosition.TradeType != TradeType.Buy, Color.Orange);

            ClosePosition(_openPosition);
            _openPosition = null;
            _stopTrailed = false;
        }

        // Fires for every close of our own positions - both broker-triggered (stop loss) and our own
        // manual ClosePosition() calls (take profit, TP-flip, force-exit, week-rollover).
        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != PositionLabel || args.Position.SymbolName != SymbolName) return;

            // Record P&L for the recent-trend detector regardless of why it closed.
            _recentClosedTrades.Add(new ClosedTradeRecord
            {
                ClosedAtUtc = Server.TimeInUtc,
                Direction = args.Position.TradeType,
                NetProfit = args.Position.NetProfit
            });

            if (args.Reason != PositionCloseReason.StopLoss) return; // manual closes are already logged at the call site

            string reason = _stopTrailed
                ? $"Trailing stop hit @ {args.Position.StopLoss:F2} (locked-in profit)"
                : $"Stop loss hit @ {args.Position.StopLoss:F2}";

            Print(reason);
            if (ShowReasons)
                DrawReasonLabel(reason, Server.TimeInUtc, args.Position.StopLoss ?? 0, args.Position.TradeType != TradeType.Buy, Color.Red);

            if (_openPosition != null && _openPosition.Id == args.Position.Id)
            {
                _openPosition = null;
                _stopTrailed = false;
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

        private void DrawTrailMarker(Bar xBar, double level)
        {
            string name = "Trail_" + _objCounter++;
            Chart.DrawTrendLine(name, xBar.OpenTime, level, xBar.OpenTime.AddMinutes(1), level, Color.Yellow, 1, LineStyle.Dots);
        }

        private void DrawWindowMarkers(DateTime windowStartUtc, string trend)
        {
            // Pine's per-bar bgcolor tint has no direct cBot equivalent, so the window is marked with a
            // pair of vertical lines (start/end) instead - colored the same way: bearish-into-window
            // (green, reversal UP expected -> LONG fade) or bullish-into-window (red, reversal DOWN
            // expected -> SHORT fade).
            DateTime localStart = TimeZoneInfo.ConvertTimeFromUtc(windowStartUtc, _warnTz);
            DateTime localEndDay = localStart.Date;
            while (localEndDay.DayOfWeek != WarnEndDay) localEndDay = localEndDay.AddDays(1);
            DateTime localEnd = localEndDay.AddHours(WarnEndHour).AddMinutes(WarnEndMinute);
            if (localEnd <= localStart) localEnd = localEnd.AddDays(7);
            DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), _warnTz);

            Color c = trend == "down" ? Color.Green : trend == "up" ? Color.Red : Color.Gray;
            Chart.RemoveObject("WarnStart");
            Chart.RemoveObject("WarnEnd");
            Chart.DrawVerticalLine("WarnStart", windowStartUtc, c, 1, LineStyle.Dots);
            Chart.DrawVerticalLine("WarnEnd", endUtc, c, 1, LineStyle.Dots);
        }

        private void DrawWeekLines(DateTime weekBarTime)
        {
            Chart.RemoveObject("WeekHigh");
            Chart.RemoveObject("WeekLow");
            Chart.RemoveObject("WeekOpen");

            var endTime = weekBarTime.AddDays(7);
            Chart.DrawTrendLine("WeekHigh", weekBarTime, _weekHigh, endTime, _weekHigh, Color.Green, 2);
            Chart.DrawTrendLine("WeekLow", weekBarTime, _weekLow, endTime, _weekLow, Color.Red, 2);
            Chart.DrawTrendLine("WeekOpen", weekBarTime, _weekOpen, endTime, _weekOpen, Color.Yellow, 2);
        }
    }
}
