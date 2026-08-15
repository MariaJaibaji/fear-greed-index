// Daily Session Reversal - cTrader cBot (cAlgo.API, C#)
//
// A daily-cadence sibling to the Weekly TP Reversal Window cBot. Same EMA multi-timeframe confluence
// filter, risk-based position sizing, and take-profit/stop-loss machinery, but the entry trigger is
// different: instead of a weekly day/time window, this bot watches 3 configurable times of day (default
// 02:30, 08:45, 19:05) and checks whether price consolidated tightly around each one (stayed within a
// configurable % tolerance). A tight consolidation is treated as a confirmed support/resistance level for
// the rest of the day. Once price returns near one of those confirmed levels, a position is opened in
// whichever direction the EMA filter confirms (or the daily-swing mode, if the EMA filter is off).
//
// What's kept the same as the weekly bot:
//   - The full EMA Trend Filter: up to 7 independent timeframes, EMA Fast/Slow periods, Minimum
//     Timeframes Agreeing confluence threshold - identical mechanism, identical defaults.
//   - Risk-% position sizing (equity x Risk% / stop distance), capped by Max Position Size.
//   - Take Profit At Confirmed TP Level (close-confirmed exit through the TP ladder), same logic.
//   - Real broker-side stop loss via Position.ModifyStopLossPrice.
//
// What's different / simplified for this bot:
//   - TP Increment and Stop Loss are PERCENT-ONLY here (no Points unit option) - simpler parameter set.
//   - "Maximum Daily Swing From Open %" replaces the weekly Trend/Reversal Threshold: it's the same idea
//     (how far price must move from the anchor before the day is read as bullish/bearish), just anchored
//     to the day's OPEN instead of the week's high/low.
//   - No day-of-week window - the reversal window's day/time gate + the weekly one-trade-per-window flag
//     are replaced by the 3 daily check-times + a one-trade-per-day flag.
//   - Move SL To Breakeven is a simple one-shot move (once price has moved a configured % AND it's past a
//     configured time of day, tighten the stop to breakeven and leave it there) - not the weekly bot's
//     stepped trailing-through-TP-increments mechanism.
//   - Locked Execution Timeframe is hardcoded to Minute1 - no toggle, no choice.
//   - Show Weekly High/Low is a purely visual reference (two lines), computed independently of the
//     trading logic, which is entirely daily-scoped.
//   - Entry/exit reasoning labels are proper multi-line ChartText (price, why, which check-time level
//     triggered it, P&L in price and %) instead of one long concatenated line.
//   - Use ATR-Relative Sizing (off by default): replaces the fixed-% Stop Loss, TP Increment,
//     Consolidation Tolerance, Maximum Daily Swing, and Daily Open Tolerance with ATR-multiple
//     equivalents computed on the Calculation Timeframe. Chosen as the highest-leverage addition for
//     surviving choppy/trending/high-volatility regimes without re-tuning, since it touches every trade's
//     risk/reward directly (tighter in calm conditions, wider in volatile ones) rather than just gating
//     whether trades happen.
//
// Platform notes (same caveats as the weekly bot):
//   - The stop loss / breakeven move use a REAL broker-side stop order - accurate, broker-filled.
//   - This file has not been compiled inside cTrader - the cAlgo API has shifted slightly across
//     versions. Paste it into cTrader Automate; if the compiler flags a method/property name, it's almost
//     always a one-line signature fix.
//
// Build/test in the cTrader backtester before running on a live or demo account.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class DailySessionReversalCBot : Robot
    {
        private const string PositionLabel = "DailySessionReversal";

        // ---------------------------------------------------------------------------------------------
        // Parameters - EMA Trend Filter (identical mechanism/defaults to the weekly bot)
        // ---------------------------------------------------------------------------------------------

        [Parameter("Use EMA Trend Filter", DefaultValue = true, Group = "EMA Trend Filter",
            Description = "Only takes trades aligned with the EMA trend, checked across up to 7 independent timeframes: EMA Fast above EMA Slow = bullish, EMA Slow above EMA Fast = bearish. A direction is only allowed once at least Minimum Timeframes Agreeing of the enabled timeframes agree on it. When off, direction falls back to the daily-swing mode instead.")]
        public bool UseEmaTrendFilter { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 21, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", DefaultValue = 253, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaSlowPeriod { get; set; }

        [Parameter("EMA Timeframe 1", DefaultValue = "Minute5", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame1 { get; set; }

        [Parameter("Use EMA Timeframe 2", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame2 { get; set; }

        [Parameter("EMA Timeframe 2", DefaultValue = "Minute15", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame2 { get; set; }

        [Parameter("Use EMA Timeframe 3", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame3 { get; set; }

        [Parameter("EMA Timeframe 3", DefaultValue = "Hour1", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame3 { get; set; }

        [Parameter("Use EMA Timeframe 4", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame4 { get; set; }

        [Parameter("EMA Timeframe 4", DefaultValue = "Hour4", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame4 { get; set; }

        [Parameter("Use EMA Timeframe 5", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame5 { get; set; }

        [Parameter("EMA Timeframe 5", DefaultValue = "Hour12", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame5 { get; set; }

        [Parameter("Use EMA Timeframe 6", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame6 { get; set; }

        [Parameter("EMA Timeframe 6", DefaultValue = "Daily", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame6 { get; set; }

        [Parameter("Use EMA Timeframe 7", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame7 { get; set; }

        [Parameter("EMA Timeframe 7", DefaultValue = "Weekly", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame7 { get; set; }

        [Parameter("Minimum Timeframes Agreeing", DefaultValue = 5, MinValue = 1, MaxValue = 7, Group = "EMA Trend Filter",
            Description = "How many of the ENABLED EMA timeframes must agree on a direction before it's treated as a signal.")]
        public int MinTimeframesAgreeing { get; set; }

        // ---------------------------------------------------------------------------------------------
        // Parameters - Daily Levels
        // ---------------------------------------------------------------------------------------------

        [Parameter("TP Increment (% of price)", DefaultValue = 1.66, MinValue = 0.01, Group = "Daily Levels",
            Description = "TP ladder step, as a % of the day's running low (for up-levels) / high (for down-levels). Used unless Use ATR-Relative Sizing is on.")]
        public double TpStepPercent { get; set; }

        [Parameter("Maximum Daily Swing From Open %", DefaultValue = 0.83, MinValue = 0.01, Group = "Daily Levels",
            Description = "How far (as a %) the close must move from the day's open before the day is read as bullish (up) or bearish (down). Used unless Use ATR-Relative Sizing is on.")]
        public double MaxDailySwingPercent { get; set; }

        [Parameter("Calculation Timeframe", DefaultValue = "Minute15", Group = "Daily Levels",
            Description = "Timeframe used for daily open/high/low tracking, the check-time consolidation analysis, and the ATR (when ATR-relative sizing is on).")]
        public TimeFrame CalcTimeFrame { get; set; }

        [Parameter("Use ATR-Relative Sizing", DefaultValue = false, Group = "Daily Levels",
            Description = "Replaces the fixed-% Stop Loss, TP Increment, Consolidation Tolerance, Maximum Daily Swing, and Daily Open Tolerance with ATR-multiple equivalents below, computed on the Calculation Timeframe. Lets the bot self-adjust to the current volatility regime (tighter in calm conditions, wider in volatile ones) instead of assuming one fixed % forever - the single biggest lever for surviving choppy vs trending vs high-volatility conditions without re-tuning.")]
        public bool UseAtrSizing { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Daily Levels")]
        public int AtrPeriod { get; set; }

        [Parameter("TP Increment (ATR multiple)", DefaultValue = 3.0, MinValue = 0.1, Group = "Daily Levels")]
        public double TpStepAtrMultiple { get; set; }

        [Parameter("Maximum Daily Swing (ATR multiple)", DefaultValue = 1.5, MinValue = 0.1, Group = "Daily Levels")]
        public double MaxDailySwingAtrMultiple { get; set; }

        [Parameter("Show Weekly High/Low", DefaultValue = true, Group = "Daily Levels",
            Description = "Purely visual reference lines - independent of the (entirely daily-scoped) trading logic.")]
        public bool ShowWeeklyLines { get; set; }

        [Parameter("Week Start Day (visual only)", DefaultValue = DayOfWeek.Monday, Group = "Daily Levels")]
        public DayOfWeek WeekStartDay { get; set; }

        // ---------------------------------------------------------------------------------------------
        // Parameters - Daily S/R Check Times
        // ---------------------------------------------------------------------------------------------

        [Parameter("Session Timezone (IANA or Windows id)", DefaultValue = "Europe/London", Group = "Daily S/R Check Times",
            Description = "All check times, the daily open/reset boundary, the breakeven time, and the force-exit time are read in this timezone.")]
        public string SessionTimeZoneId { get; set; }

        [Parameter("Check Time 1 Hour", DefaultValue = 2, MinValue = 0, MaxValue = 23, Group = "Daily S/R Check Times")]
        public int CheckTime1Hour { get; set; }

        [Parameter("Check Time 1 Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59, Group = "Daily S/R Check Times")]
        public int CheckTime1Minute { get; set; }

        [Parameter("Check Time 2 Hour", DefaultValue = 8, MinValue = 0, MaxValue = 23, Group = "Daily S/R Check Times")]
        public int CheckTime2Hour { get; set; }

        [Parameter("Check Time 2 Minute", DefaultValue = 45, MinValue = 0, MaxValue = 59, Group = "Daily S/R Check Times")]
        public int CheckTime2Minute { get; set; }

        [Parameter("Check Time 3 Hour", DefaultValue = 19, MinValue = 0, MaxValue = 23, Group = "Daily S/R Check Times")]
        public int CheckTime3Hour { get; set; }

        [Parameter("Check Time 3 Minute", DefaultValue = 5, MinValue = 0, MaxValue = 59, Group = "Daily S/R Check Times")]
        public int CheckTime3Minute { get; set; }

        [Parameter("Consolidation Tolerance %", DefaultValue = 0.2, MinValue = 0.01, Group = "Daily S/R Check Times",
            Description = "A check time is confirmed as support/resistance if price stayed within this % range over the Consolidation Window leading up to it. Used unless Use ATR-Relative Sizing is on.")]
        public double ConsolidationTolerancePercent { get; set; }

        [Parameter("Consolidation Tolerance (ATR multiple)", DefaultValue = 0.15, MinValue = 0.01, Group = "Daily S/R Check Times",
            Description = "Used instead of the % version when Use ATR-Relative Sizing is on.")]
        public double ConsolidationToleranceAtrMultiple { get; set; }

        [Parameter("Consolidation Window (bars)", DefaultValue = 3, MinValue = 1, Group = "Daily S/R Check Times",
            Description = "How many Calculation Timeframe bars (including the one at the check time) are examined for tightness.")]
        public int ConsolidationWindowBars { get; set; }

        // ---------------------------------------------------------------------------------------------
        // Parameters - Strategy
        // ---------------------------------------------------------------------------------------------

        [Parameter("Trade Longs", DefaultValue = true, Group = "Strategy")]
        public bool EnableLongs { get; set; }

        [Parameter("Trade Shorts", DefaultValue = true, Group = "Strategy")]
        public bool EnableShorts { get; set; }

        [Parameter("Entry Proximity %", DefaultValue = 0.088, MinValue = 0.01, Group = "Strategy",
            Description = "How close (as a % of price) price must come to a confirmed check-time S/R level to trigger the entry.")]
        public double EntryProximityPercent { get; set; }

        [Parameter("Daily Open Tolerance %", DefaultValue = 0.0, MinValue = 0, Group = "Strategy",
            Description = "Acts as a small error/tolerance band around the strict rule: a LONG is allowed anywhere below the day's open, plus up to this % ABOVE it as a margin of error; a SHORT anywhere above the open, plus up to this % BELOW it. 0 = strict (long only below open, short only above). Used unless Use ATR-Relative Sizing is on.")]
        public double DailyOpenTolerancePercent { get; set; }

        [Parameter("Daily Open Tolerance (ATR multiple)", DefaultValue = 0.0, MinValue = 0, Group = "Strategy",
            Description = "Used instead of the % version when Use ATR-Relative Sizing is on. Same error-band behavior, sized in ATR units instead of %.")]
        public double DailyOpenToleranceAtrMultiple { get; set; }

        [Parameter("Risk % Of Equity Per Trade", DefaultValue = 1.0, MinValue = 0.01, Group = "Strategy",
            Description = "Position size is calculated from this % of current equity and the trade's actual stop-loss distance. Requires Use Stop Loss to be on.")]
        public double RiskPercent { get; set; }

        [Parameter("Max Position Size (lots)", DefaultValue = 5.0, MinValue = 0.01, Group = "Strategy",
            Description = "Hard safety cap on the risk-% sized volume.")]
        public double MaxVolumeLots { get; set; }

        [Parameter("Fallback Volume (lots) If Stop Loss Disabled", DefaultValue = 0.10, MinValue = 0.01, Group = "Strategy",
            Description = "Used only when Use Stop Loss is off, since risk-% sizing has nothing to size against without a stop distance.")]
        public double FallbackVolumeLots { get; set; }

        [Parameter("Use Stop Loss", DefaultValue = true, Group = "Strategy")]
        public bool UseStopLoss { get; set; }

        [Parameter("Stop Loss (% of entry)", DefaultValue = 0.75, MinValue = 0.01, Group = "Strategy",
            Description = "Used unless Use ATR-Relative Sizing is on (see Daily Levels group).")]
        public double StopLossPercent { get; set; }

        [Parameter("Stop Loss (ATR multiple)", DefaultValue = 1.5, MinValue = 0.1, Group = "Strategy",
            Description = "Used instead of the % version when Use ATR-Relative Sizing is on.")]
        public double StopLossAtrMultiple { get; set; }

        [Parameter("Move SL To Breakeven", DefaultValue = false, Group = "Strategy",
            Description = "Once BOTH conditions are met - price has moved at least Breakeven Trigger % in the trade's favor, AND it's past the Breakeven Time of day - the stop is tightened to breakeven and left there. One-shot: it never loosens, and never moves again afterward.")]
        public bool UseMoveToBreakeven { get; set; }

        [Parameter("Breakeven Trigger %", DefaultValue = 0.5, MinValue = 0.01, Group = "Strategy")]
        public double BreakevenTriggerPercent { get; set; }

        [Parameter("Breakeven Time Hour", DefaultValue = 12, MinValue = 0, MaxValue = 23, Group = "Strategy")]
        public int BreakevenTimeHour { get; set; }

        [Parameter("Breakeven Time Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59, Group = "Strategy")]
        public int BreakevenTimeMinute { get; set; }

        [Parameter("Take Profit At Confirmed TP Level", DefaultValue = true, Group = "Strategy",
            Description = "Close-confirmed exit: fires once price CLOSES past the target TP increment (the level effectively becomes support/resistance), not on a raw price touch.")]
        public bool UseTakeProfit { get; set; }

        [Parameter("Take Profit Levels Ahead", DefaultValue = 2, MinValue = 1, MaxValue = 10, Group = "Strategy")]
        public int TakeProfitLevels { get; set; }

        [Parameter("Force-Exit Hour", DefaultValue = 21, MinValue = 0, MaxValue = 23, Group = "Strategy",
            Description = "Any trade still open is force-closed at this time of day - no overnight hold.")]
        public int ForceExitHour { get; set; }

        [Parameter("Force-Exit Minute", DefaultValue = 45, MinValue = 0, MaxValue = 59, Group = "Strategy")]
        public int ForceExitMinute { get; set; }

        [Parameter("Show Entry/Exit Reasoning Labels", DefaultValue = true, Group = "Strategy")]
        public bool ShowReasons { get; set; }

        [Parameter("Enable Debug Logging", DefaultValue = false, Group = "Strategy",
            Description = "Prints one diagnostic line per Calculation Timeframe bar showing the EMA direction (and why, including confluence counts), the confirmed S/R levels, distance to the nearest one, and the open-tolerance state - use this to see exactly which gate is blocking an entry when no trades are firing.")]
        public bool EnableDebugLogging { get; set; }

        // ---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        private Bars _calcBars;
        private Bars _execBars; // hardcoded Minute1
        private AverageTrueRange _atr;
        private double _currentAtr = double.NaN;
        private Bars _emaBars1, _emaBars2, _emaBars3, _emaBars4, _emaBars5, _emaBars6, _emaBars7;
        private ExponentialMovingAverage _emaFast1, _emaSlow1;
        private ExponentialMovingAverage _emaFast2, _emaSlow2;
        private ExponentialMovingAverage _emaFast3, _emaSlow3;
        private ExponentialMovingAverage _emaFast4, _emaSlow4;
        private ExponentialMovingAverage _emaFast5, _emaSlow5;
        private ExponentialMovingAverage _emaFast6, _emaSlow6;
        private ExponentialMovingAverage _emaFast7, _emaSlow7;
        private TimeZoneInfo _sessionTz;

        private DateTime? _lastDayAnchor;
        private double _dayOpen = double.NaN;
        private double _dayHigh = double.NaN;
        private double _dayLow = double.NaN;
        private string _dayMode; // "up", "down", or null
        private double _tpStepUp, _tpStepDn;

        // Confirmed check-time S/R levels, reset each day.
        private double? _srLevel1, _srLevel2, _srLevel3;
        private bool _checked1Today, _checked2Today, _checked3Today;

        // Purely visual weekly high/low.
        private DateTime? _lastWeekAnchorVisual;
        private double _weekHighVisual = double.NaN;
        private double _weekLowVisual = double.NaN;

        private bool _tradedToday;
        private Position _openPosition;
        private double _entryPrice;
        private bool _movedToBreakeven;

        private int _objCounter;

        // ---------------------------------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------------------------------

        protected override void OnStart()
        {
            _sessionTz = ResolveTimeZone(SessionTimeZoneId);

            _calcBars = MarketData.GetBars(CalcTimeFrame, SymbolName);
            _calcBars.BarOpened += OnCalcBarOpened;

            if (UseAtrSizing)
                _atr = Indicators.AverageTrueRange(_calcBars, AtrPeriod, MovingAverageType.WilderSmoothing);

            _execBars = MarketData.GetBars(TimeFrame.Minute, SymbolName); // locked to 1 minute, no toggle
            _execBars.BarOpened += OnExecBarOpened;

            if (UseEmaTrendFilter)
            {
                _emaBars1 = MarketData.GetBars(EmaTimeFrame1, SymbolName);
                _emaFast1 = Indicators.ExponentialMovingAverage(_emaBars1.ClosePrices, EmaFastPeriod);
                _emaSlow1 = Indicators.ExponentialMovingAverage(_emaBars1.ClosePrices, EmaSlowPeriod);

                if (UseEmaTimeFrame2)
                {
                    _emaBars2 = MarketData.GetBars(EmaTimeFrame2, SymbolName);
                    _emaFast2 = Indicators.ExponentialMovingAverage(_emaBars2.ClosePrices, EmaFastPeriod);
                    _emaSlow2 = Indicators.ExponentialMovingAverage(_emaBars2.ClosePrices, EmaSlowPeriod);
                }

                if (UseEmaTimeFrame3)
                {
                    _emaBars3 = MarketData.GetBars(EmaTimeFrame3, SymbolName);
                    _emaFast3 = Indicators.ExponentialMovingAverage(_emaBars3.ClosePrices, EmaFastPeriod);
                    _emaSlow3 = Indicators.ExponentialMovingAverage(_emaBars3.ClosePrices, EmaSlowPeriod);
                }

                if (UseEmaTimeFrame4)
                {
                    _emaBars4 = MarketData.GetBars(EmaTimeFrame4, SymbolName);
                    _emaFast4 = Indicators.ExponentialMovingAverage(_emaBars4.ClosePrices, EmaFastPeriod);
                    _emaSlow4 = Indicators.ExponentialMovingAverage(_emaBars4.ClosePrices, EmaSlowPeriod);
                }

                if (UseEmaTimeFrame5)
                {
                    _emaBars5 = MarketData.GetBars(EmaTimeFrame5, SymbolName);
                    _emaFast5 = Indicators.ExponentialMovingAverage(_emaBars5.ClosePrices, EmaFastPeriod);
                    _emaSlow5 = Indicators.ExponentialMovingAverage(_emaBars5.ClosePrices, EmaSlowPeriod);
                }

                if (UseEmaTimeFrame6)
                {
                    _emaBars6 = MarketData.GetBars(EmaTimeFrame6, SymbolName);
                    _emaFast6 = Indicators.ExponentialMovingAverage(_emaBars6.ClosePrices, EmaFastPeriod);
                    _emaSlow6 = Indicators.ExponentialMovingAverage(_emaBars6.ClosePrices, EmaSlowPeriod);
                }

                if (UseEmaTimeFrame7)
                {
                    _emaBars7 = MarketData.GetBars(EmaTimeFrame7, SymbolName);
                    _emaFast7 = Indicators.ExponentialMovingAverage(_emaBars7.ClosePrices, EmaFastPeriod);
                    _emaSlow7 = Indicators.ExponentialMovingAverage(_emaBars7.ClosePrices, EmaSlowPeriod);
                }

                int enabledCount = 1 + (UseEmaTimeFrame2 ? 1 : 0) + (UseEmaTimeFrame3 ? 1 : 0)
                                     + (UseEmaTimeFrame4 ? 1 : 0) + (UseEmaTimeFrame5 ? 1 : 0)
                                     + (UseEmaTimeFrame6 ? 1 : 0) + (UseEmaTimeFrame7 ? 1 : 0);
                if (MinTimeframesAgreeing > enabledCount)
                    Print($"Warning: Minimum Timeframes Agreeing ({MinTimeframesAgreeing}) is higher than the number of enabled EMA timeframes ({enabledCount}) - the EMA trend filter will never produce a signal.");
            }

            Positions.Closed += OnPositionsClosed;

            _openPosition = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == PositionLabel);
            if (_openPosition != null)
            {
                _tradedToday = true;
                _entryPrice = _openPosition.EntryPrice;
                Print("Recovered an existing open position on start: " + _openPosition.TradeType);
            }
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
                Print($"Warning: timezone '{id}' not found on this system - falling back to UTC.");
                return TimeZoneInfo.Utc;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Daily open/high/low tracking + check-time consolidation analysis
        // (runs on each closed Calculation Timeframe bar)
        // ---------------------------------------------------------------------------------------------

        private void OnCalcBarOpened(BarOpenedEventArgs args)
        {
            if (_calcBars.Count < 2) return;
            ProcessCalcBar();
        }

        private bool IsNewLocalDay(DateTime localTime, ref DateTime? lastAnchor)
        {
            DateTime anchor = localTime.Date;
            bool isNew = lastAnchor == null || anchor != lastAnchor.Value;
            lastAnchor = anchor;
            return isNew;
        }

        private bool IsNewWeek(DateTime localTime)
        {
            DateTime anchor = localTime.Date;
            while (anchor.DayOfWeek != WeekStartDay) anchor = anchor.AddDays(-1);
            bool isNew = _lastWeekAnchorVisual == null || anchor != _lastWeekAnchorVisual.Value;
            _lastWeekAnchorVisual = anchor;
            return isNew;
        }

        private void ProcessCalcBar()
        {
            Bar bar = _calcBars.Last(1);
            DateTime localTime = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenTime, _sessionTz);

            bool newDay = IsNewLocalDay(localTime, ref _lastDayAnchor);
            if (newDay || double.IsNaN(_dayOpen))
            {
                if (_openPosition != null)
                    CloseWithReason("Day rollover - safety close", bar.Close, bar.OpenTime);

                _dayOpen = bar.Open;
                _dayHigh = bar.High;
                _dayLow = bar.Low;
                _dayMode = null;
                _tradedToday = false;
                _srLevel1 = null;
                _srLevel2 = null;
                _srLevel3 = null;
                _checked1Today = false;
                _checked2Today = false;
                _checked3Today = false;
            }
            else
            {
                if (bar.High > _dayHigh) _dayHigh = bar.High;
                if (bar.Low < _dayLow) _dayLow = bar.Low;
            }

            _currentAtr = UseAtrSizing && _atr != null ? _atr.Result.LastValue : double.NaN;
            bool haveAtr = UseAtrSizing && !double.IsNaN(_currentAtr);

            // TP ladder step and the daily-swing direction threshold both switch to ATR multiples when
            // Use ATR-Relative Sizing is on, so the bot self-adjusts to the current volatility regime
            // instead of assuming one fixed % forever.
            _tpStepUp = haveAtr ? _currentAtr * TpStepAtrMultiple : _dayLow * TpStepPercent / 100.0;
            _tpStepDn = haveAtr ? _currentAtr * TpStepAtrMultiple : _dayHigh * TpStepPercent / 100.0;

            double swingThreshold = haveAtr ? _currentAtr * MaxDailySwingAtrMultiple : _dayOpen * MaxDailySwingPercent / 100.0;
            bool bullSignal = bar.Close >= _dayOpen + swingThreshold;
            bool bearSignal = bar.Close <= _dayOpen - swingThreshold;
            if (bullSignal && !bearSignal) _dayMode = "up";
            else if (bearSignal && !bullSignal) _dayMode = "down";

            EvaluateCheckTime(1, CheckTime1Hour, CheckTime1Minute, ref _checked1Today, ref _srLevel1, localTime);
            EvaluateCheckTime(2, CheckTime2Hour, CheckTime2Minute, ref _checked2Today, ref _srLevel2, localTime);
            EvaluateCheckTime(3, CheckTime3Hour, CheckTime3Minute, ref _checked3Today, ref _srLevel3, localTime);

            // Purely visual weekly high/low.
            bool newWeek = IsNewWeek(localTime);
            if (newWeek || double.IsNaN(_weekHighVisual))
            {
                _weekHighVisual = bar.High;
                _weekLowVisual = bar.Low;
            }
            else
            {
                if (bar.High > _weekHighVisual) _weekHighVisual = bar.High;
                if (bar.Low < _weekLowVisual) _weekLowVisual = bar.Low;
            }
            if (ShowWeeklyLines) DrawWeekLines(bar.OpenTime);

            if (EnableDebugLogging) LogDebugState(bar);
        }

        // Confirms a check time as a support/resistance level if the last ConsolidationWindowBars calc
        // bars (including this one) stayed within ConsolidationTolerancePercent of their average price.
        // Only evaluated once per day per slot, at the first calc bar whose local time reaches the target.
        private void EvaluateCheckTime(int slot, int hour, int minute, ref bool checkedToday, ref double? level, DateTime localTime)
        {
            if (checkedToday) return;
            if (localTime.Hour < hour || (localTime.Hour == hour && localTime.Minute < minute)) return;

            checkedToday = true;

            int n = Math.Min(ConsolidationWindowBars, _calcBars.Count - 1);
            if (n < 1) return;

            double hi = double.MinValue;
            double lo = double.MaxValue;
            double sum = 0;
            for (int i = 1; i <= n; i++)
            {
                Bar b = _calcBars.Last(i);
                if (b.High > hi) hi = b.High;
                if (b.Low < lo) lo = b.Low;
                sum += b.Close;
            }
            double avg = sum / n;
            double range = hi - lo;

            bool haveAtr = UseAtrSizing && !double.IsNaN(_currentAtr);
            bool tight = haveAtr
                ? range <= _currentAtr * ConsolidationToleranceAtrMultiple
                : (avg > 0 && range / avg * 100.0 <= ConsolidationTolerancePercent);

            if (tight)
            {
                level = (hi + lo) / 2.0;
                string detail = haveAtr ? $"{range:F2} vs {_currentAtr * ConsolidationToleranceAtrMultiple:F2} ATR-based" : $"{(avg > 0 ? range / avg * 100.0 : 0):F3}%";
                Print($"Check Time {slot} ({hour:D2}:{minute:D2}) confirmed S/R @ {level:F2} (range {detail} over {n} bars)");
            }
        }

        // One line per Calculation Timeframe bar (opt-in via Enable Debug Logging) showing the state of
        // every AND-ed entry gate: EMA direction (and why), nearest confirmed S/R level and distance,
        // and the open-tolerance side check. Use this to pinpoint which gate is blocking entries instead
        // of guessing at parameters.
        private void LogDebugState(Bar calcBar)
        {
            (string emaBias, string emaReason) = GetEmaTrendBias();
            string direction = UseEmaTrendFilter ? emaBias : _dayMode;
            string directionInfo = UseEmaTrendFilter ? (emaReason ?? "n/a") : $"Daily-swing mode: {_dayMode ?? "none"} (>= {MaxDailySwingPercent}% from open {_dayOpen:F2})";

            (double level, int slot) = NearestConfirmedLevel(calcBar.Close);
            string nearInfo;
            if (double.IsNaN(level))
            {
                nearInfo = "no confirmed S/R level yet today (SR1/2/3 all null)";
            }
            else
            {
                double distPct = level != 0 ? Math.Abs(calcBar.Close - level) / level * 100.0 : double.NaN;
                bool near = distPct <= EntryProximityPercent;
                nearInfo = $"nearest level {level:F2} (slot {slot}), {distPct:F3}% away, need <= {EntryProximityPercent}% -> {(near ? "WITHIN RANGE" : "too far")}";
            }

            bool haveAtrForOpenTol = UseAtrSizing && !double.IsNaN(_currentAtr);
            double openToleranceAbs = haveAtrForOpenTol
                ? _currentAtr * DailyOpenToleranceAtrMultiple
                : _dayOpen * DailyOpenTolerancePercent / 100.0;
            bool openOkLong = calcBar.Close < _dayOpen + openToleranceAbs;
            bool openOkShort = calcBar.Close > _dayOpen - openToleranceAbs;

            Print($"[DEBUG] Close={calcBar.Close:F2} DayOpen={_dayOpen:F2} | Direction={direction ?? "none"} ({directionInfo}) | {nearInfo} | OpenOk: long={openOkLong} short={openOkShort} | TradedToday={_tradedToday} OpenPosition={(_openPosition != null)}");
        }

        // ---------------------------------------------------------------------------------------------
        // EMA trend filter (identical mechanism to the weekly bot)
        // ---------------------------------------------------------------------------------------------

        private (string bias, string reason) GetEmaTrendBias()
        {
            if (!UseEmaTrendFilter) return (null, null);

            var readings = new List<(string tf, string trend)>();

            string t1 = GetSingleEmaTrend(_emaFast1, _emaSlow1);
            if (t1 != null) readings.Add((EmaTimeFrame1.ToString(), t1));

            if (UseEmaTimeFrame2)
            {
                string t2 = GetSingleEmaTrend(_emaFast2, _emaSlow2);
                if (t2 != null) readings.Add((EmaTimeFrame2.ToString(), t2));
            }

            if (UseEmaTimeFrame3)
            {
                string t3 = GetSingleEmaTrend(_emaFast3, _emaSlow3);
                if (t3 != null) readings.Add((EmaTimeFrame3.ToString(), t3));
            }

            if (UseEmaTimeFrame4)
            {
                string t4 = GetSingleEmaTrend(_emaFast4, _emaSlow4);
                if (t4 != null) readings.Add((EmaTimeFrame4.ToString(), t4));
            }

            if (UseEmaTimeFrame5)
            {
                string t5 = GetSingleEmaTrend(_emaFast5, _emaSlow5);
                if (t5 != null) readings.Add((EmaTimeFrame5.ToString(), t5));
            }

            if (UseEmaTimeFrame6)
            {
                string t6 = GetSingleEmaTrend(_emaFast6, _emaSlow6);
                if (t6 != null) readings.Add((EmaTimeFrame6.ToString(), t6));
            }

            if (UseEmaTimeFrame7)
            {
                string t7 = GetSingleEmaTrend(_emaFast7, _emaSlow7);
                if (t7 != null) readings.Add((EmaTimeFrame7.ToString(), t7));
            }

            if (readings.Count == 0)
                return (null, "No EMA readings yet - every enabled timeframe is still NaN (needs EMA Slow Period bars of history on that timeframe to warm up)");

            int longCount = readings.Count(r => r.trend == "long");
            int shortCount = readings.Count(r => r.trend == "short");
            string detail = string.Join(", ", readings.Select(r => $"{r.tf}={r.trend}"));

            if (longCount >= MinTimeframesAgreeing)
                return ("long", $"EMA confluence {longCount}/{readings.Count} bullish ({detail})");
            if (shortCount >= MinTimeframesAgreeing)
                return ("short", $"EMA confluence {shortCount}/{readings.Count} bearish ({detail})");

            return (null, $"No confluence yet: {longCount} long / {shortCount} short of {readings.Count} readings warmed up, need {MinTimeframesAgreeing} to agree ({detail})");
        }

        private string GetSingleEmaTrend(ExponentialMovingAverage fast, ExponentialMovingAverage slow)
        {
            if (fast == null || slow == null) return null;

            double f = fast.Result.LastValue;
            double s = slow.Result.LastValue;
            if (double.IsNaN(f) || double.IsNaN(s)) return null;

            return f >= s ? "long" : "short";
        }

        // ---------------------------------------------------------------------------------------------
        // Entry / exit orchestration (runs once per closed 1-minute bar)
        // ---------------------------------------------------------------------------------------------

        private void EvaluateTradingLogic(Bar xBar)
        {
            if (double.IsNaN(_dayOpen)) return;

            DateTime localTime = TimeZoneInfo.ConvertTimeFromUtc(xBar.OpenTime, _sessionTz);

            (string emaBias, string emaReason) = GetEmaTrendBias();
            string direction = UseEmaTrendFilter ? emaBias : _dayMode;
            string dirReason = UseEmaTrendFilter ? emaReason : $"Daily-swing mode: {_dayMode ?? "none"} (>= {MaxDailySwingPercent}% from open {_dayOpen:F2})";

            // Acts as a small error/tolerance band around the strict rule: a LONG is allowed anywhere
            // below the daily open, plus up to a small margin of error ABOVE it; a SHORT anywhere above
            // the open, plus up to that same margin BELOW it. 0 -> strict (long only below open, short
            // only above). The margin switches to an ATR multiple when Use ATR-Relative Sizing is on.
            bool haveAtrForOpenTol = UseAtrSizing && !double.IsNaN(_currentAtr);
            double openToleranceAbs = haveAtrForOpenTol
                ? _currentAtr * DailyOpenToleranceAtrMultiple
                : _dayOpen * DailyOpenTolerancePercent / 100.0;
            bool openOkLong = xBar.Close < _dayOpen + openToleranceAbs;
            bool openOkShort = xBar.Close > _dayOpen - openToleranceAbs;

            if (!_tradedToday && _openPosition == null && direction != null)
            {
                (double level, int slot) = NearestConfirmedLevel(xBar.Close);
                bool nearLevel = !double.IsNaN(level) && Math.Abs(xBar.Close - level) <= level * EntryProximityPercent / 100.0;

                if (nearLevel)
                {
                    if (direction == "long" && EnableLongs && openOkLong)
                    {
                        string reason = $"LONG @ {xBar.Close:F2}\n{dirReason}\nCheck Time {slot} S/R @ {level:F2} (touched)\nBelow daily open {_dayOpen:F2}";
                        OpenPosition(TradeType.Buy, xBar, reason);
                        _tradedToday = true;
                    }
                    else if (direction == "short" && EnableShorts && openOkShort)
                    {
                        string reason = $"SHORT @ {xBar.Close:F2}\n{dirReason}\nCheck Time {slot} S/R @ {level:F2} (touched)\nAbove daily open {_dayOpen:F2}";
                        OpenPosition(TradeType.Sell, xBar, reason);
                        _tradedToday = true;
                    }
                }
            }

            // ---- Exit ----
            bool forceExit = localTime.Hour > ForceExitHour || (localTime.Hour == ForceExitHour && localTime.Minute >= ForceExitMinute);

            UpdateBreakeven(xBar, localTime);
            CheckTakeProfit(xBar);

            if (_openPosition != null && forceExit)
                CloseWithReason("Force exit - end-of-day time reached, no overnight hold", xBar.Close, xBar.OpenTime);
        }

        // Returns the confirmed S/R level closest to the given price, and which check-time slot (1/2/3)
        // it came from. Returns (NaN, 0) if none are confirmed yet today.
        private (double level, int slot) NearestConfirmedLevel(double price)
        {
            double bestLevel = double.NaN;
            int bestSlot = 0;
            double bestDist = double.MaxValue;

            void Consider(double? lvl, int slot)
            {
                if (lvl == null) return;
                double dist = Math.Abs(price - lvl.Value);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestLevel = lvl.Value;
                    bestSlot = slot;
                }
            }

            Consider(_srLevel1, 1);
            Consider(_srLevel2, 2);
            Consider(_srLevel3, 3);

            return (bestLevel, bestSlot);
        }

        // ---------------------------------------------------------------------------------------------
        // Position management
        // ---------------------------------------------------------------------------------------------

        private void OpenPosition(TradeType type, Bar xBar, string reason)
        {
            double estEntry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double estStopDist = UseStopLoss ? StopDistance(estEntry) : 0;

            double volumeInUnits = UseStopLoss && estStopDist > 0
                ? CalculateVolume(estStopDist)
                : Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(FallbackVolumeLots));

            // The order comment is a single line (cTrader's Positions/History grid doesn't render \n
            // nicely) - the full multi-line reason is reserved for the on-chart label below.
            string singleLineReason = reason.Replace("\n", " | ");
            var result = ExecuteMarketOrder(type, SymbolName, volumeInUnits, PositionLabel, null, null, singleLineReason);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("Entry failed: " + result.Error);
                return;
            }

            _openPosition = result.Position;
            _entryPrice = result.Position.EntryPrice;
            _movedToBreakeven = false;
            SetInitialStop();

            Print(reason.Replace("\n", " | ") + $" | Volume: {volumeInUnits} units");
            if (ShowReasons)
                DrawReasonLabel(reason, xBar.OpenTime, type == TradeType.Buy ? xBar.Low : xBar.High, type == TradeType.Buy, Color.LimeGreen);
        }

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

            double dist = StopDistance(_entryPrice);
            double level = _openPosition.TradeType == TradeType.Buy ? _entryPrice - dist : _entryPrice + dist;
            _openPosition.ModifyStopLossPrice(level);
        }

        // Stop distance switches to an ATR multiple when Use ATR-Relative Sizing is on, otherwise it's
        // the fixed % of entry price.
        private double StopDistance(double price)
        {
            if (UseAtrSizing && !double.IsNaN(_currentAtr))
                return _currentAtr * StopLossAtrMultiple;
            return price * StopLossPercent / 100.0;
        }

        // One-shot: once price has moved BreakevenTriggerPercent in the trade's favor AND it's past
        // Breakeven Time of day, tighten the stop to breakeven and never touch it again.
        private void UpdateBreakeven(Bar xBar, DateTime localTime)
        {
            if (_openPosition == null || !UseStopLoss || !UseMoveToBreakeven || _movedToBreakeven) return;

            bool pastTime = localTime.Hour > BreakevenTimeHour || (localTime.Hour == BreakevenTimeHour && localTime.Minute >= BreakevenTimeMinute);
            if (!pastTime) return;

            if (_openPosition.TradeType == TradeType.Buy)
            {
                double movedPct = (xBar.Close - _entryPrice) / _entryPrice * 100.0;
                if (movedPct < BreakevenTriggerPercent) return;

                double? currentSL = _openPosition.StopLoss;
                if (currentSL == null || _entryPrice > currentSL.Value)
                {
                    _openPosition.ModifyStopLossPrice(_entryPrice);
                    _movedToBreakeven = true;
                    if (ShowReasons) DrawTrailMarker(xBar, _entryPrice, "BE");
                }
            }
            else
            {
                double movedPct = (_entryPrice - xBar.Close) / _entryPrice * 100.0;
                if (movedPct < BreakevenTriggerPercent) return;

                double? currentSL = _openPosition.StopLoss;
                if (currentSL == null || _entryPrice < currentSL.Value)
                {
                    _openPosition.ModifyStopLossPrice(_entryPrice);
                    _movedToBreakeven = true;
                    if (ShowReasons) DrawTrailMarker(xBar, _entryPrice, "BE");
                }
            }
        }

        // Take profit using the same close-confirmation approach as the weekly bot: fires once the CLOSE
        // has travelled TakeProfitLevels increments past the day's low/high, reusing the TP ladder step.
        private void CheckTakeProfit(Bar xBar)
        {
            if (_openPosition == null || !UseTakeProfit) return;

            if (_openPosition.TradeType == TradeType.Buy)
            {
                int steps = _tpStepUp > 0 ? (int)Math.Floor(Math.Max(xBar.Close - _dayLow, 0) / _tpStepUp) : 0;
                if (steps >= TakeProfitLevels)
                {
                    double level = _dayLow + _tpStepUp * TakeProfitLevels;
                    CloseWithReason($"Take profit - TP{TakeProfitLevels} confirmed by close (support formed @ {level:F2})", xBar.Close, xBar.OpenTime);
                }
            }
            else
            {
                int steps = _tpStepDn > 0 ? (int)Math.Floor(Math.Max(_dayHigh - xBar.Close, 0) / _tpStepDn) : 0;
                if (steps >= TakeProfitLevels)
                {
                    double level = _dayHigh - _tpStepDn * TakeProfitLevels;
                    CloseWithReason($"Take profit - TP{TakeProfitLevels} confirmed by close (resistance formed @ {level:F2})", xBar.Close, xBar.OpenTime);
                }
            }
        }

        private void CloseWithReason(string reason, double price, DateTime time)
        {
            if (_openPosition == null) return;

            double pnlPrice = _openPosition.TradeType == TradeType.Buy ? price - _entryPrice : _entryPrice - price;
            double pnlPct = _entryPrice > 0 ? pnlPrice / _entryPrice * 100.0 : 0;
            bool win = pnlPrice >= 0;
            string label = $"EXIT {(_openPosition.TradeType == TradeType.Buy ? "LONG" : "SHORT")} @ {price:F2}\n{reason}\n{(win ? "+" : "")}{pnlPrice:F2} ({(win ? "+" : "")}{pnlPct:F2}%)";

            Print(label.Replace("\n", " | "));
            if (ShowReasons)
                DrawReasonLabel(label, time, price, _openPosition.TradeType != TradeType.Buy, win ? Color.Teal : Color.Maroon);

            ClosePosition(_openPosition);
            _openPosition = null;
            _movedToBreakeven = false;
        }

        // Catches broker-triggered stop-loss fills (we didn't call ClosePosition ourselves for those).
        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != PositionLabel || args.Position.SymbolName != SymbolName) return;
            if (args.Reason != PositionCloseReason.StopLoss) return; // manual closes are already logged at the call site

            double exitPrice = args.Position.StopLoss ?? 0;
            double pnlPrice = args.Position.TradeType == TradeType.Buy ? exitPrice - _entryPrice : _entryPrice - exitPrice;
            double pnlPct = _entryPrice > 0 ? pnlPrice / _entryPrice * 100.0 : 0;
            bool win = pnlPrice >= 0;
            string reason = _movedToBreakeven ? "Breakeven stop hit" : "Stop loss hit";
            string label = $"EXIT {(args.Position.TradeType == TradeType.Buy ? "LONG" : "SHORT")} @ {exitPrice:F2}\n{reason}\n{(win ? "+" : "")}{pnlPrice:F2} ({(win ? "+" : "")}{pnlPct:F2}%)";

            Print(label.Replace("\n", " | "));
            if (ShowReasons)
                DrawReasonLabel(label, Server.TimeInUtc, exitPrice, args.Position.TradeType != TradeType.Buy, win ? Color.Teal : Color.Maroon);

            if (_openPosition != null && _openPosition.Id == args.Position.Id)
            {
                _openPosition = null;
                _movedToBreakeven = false;
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
            label.FontSize = 10;
        }

        private void DrawTrailMarker(Bar xBar, double level, string tag)
        {
            string name = tag + "_" + _objCounter++;
            Chart.DrawTrendLine(name, xBar.OpenTime, level, xBar.OpenTime.AddMinutes(1), level, Color.Yellow, 1, LineStyle.Dots);
        }

        private void DrawWeekLines(DateTime weekBarTime)
        {
            Chart.RemoveObject("WeekHighVisual");
            Chart.RemoveObject("WeekLowVisual");

            var endTime = weekBarTime.AddDays(7);
            Chart.DrawTrendLine("WeekHighVisual", weekBarTime, _weekHighVisual, endTime, _weekHighVisual, Color.Green, 2);
            Chart.DrawTrendLine("WeekLowVisual", weekBarTime, _weekLowVisual, endTime, _weekLowVisual, Color.Red, 2);
        }
    }
}
