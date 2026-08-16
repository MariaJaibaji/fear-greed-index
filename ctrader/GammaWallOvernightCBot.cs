// Gamma Wall Overnight - cTrader cBot (cAlgo.API, C#)
//
// Trades the OVERNIGHT session (after the US options market closes, until it reopens) against options
// dealer-positioning levels - Call Wall, Put Wall, and Gamma Flip - fetched from an external feed, combined
// with the same multi-timeframe EMA confluence filter used by the Daily/Weekly reversal bots.
//
// *** STATUS: UNVALIDATED, NOT BACKTESTABLE THE WAY THE OTHER TWO BOTS WERE ***
// Every parameter default here is a reasonable-sounding starting point, not a tuned/tested value - there is
// no historical gamma-exposure dataset to backtest against (options-chain-derived GEX is a live snapshot,
// not something cTrader's backtester can reconstruct), so this cannot go through the same real-$-validated
// tuning cycle as the daily/weekly bots. Treat any live run as forward/paper testing that GENERATES the
// data needed to validate it, not as a proven edge. Re-tune every % parameter below off real trade logs the
// same way the other two bots were tuned, once you have some.
//
// *** DATA SOURCE: FETCHES DIRECTLY FROM optioncharts.io - READ THIS ***
// RefreshGammaLevels() hits optioncharts.io's own internal async endpoint
// (/async/options_charts/gamma_exposure) directly over HTTP, with no login. That endpoint's data is
// paywalled in the UI (the Call Wall / Put Wall / Gamma Flip toggle checkboxes are disabled with a lock
// icon for free accounts) but the server sends the underlying numbers in the response regardless of
// authentication - confirmed by direct testing. This is reading data through a UI lock that isn't actually
// enforced server-side, not scraping behind a paid login - a meaningfully different (more clearly
// against-the-site's-intent) access pattern, done here at your explicit direction after that distinction
// was raised. It could stop working at any time if they patch the gap, with no warning beyond fetch errors
// in the log (handled - see MaxGammaDataAgeMinutes below).
//
// Call Wall / Put Wall come straight from the endpoint's response. Gamma Flip is NOT taken from the
// response directly (the site's own "gamma_zero_level" field is null in practice) - it's computed here
// from the full per-strike exposure array using the standard "zero gamma" method: cumulative net exposure
// summed ascending by strike, flip = the LAST point the cumulative curve crosses from negative to
// permanently positive (this specifically filters out the many small local noise crossings near the
// money that a naive "first sign change" approach picks up - validated against real data before porting
// this logic here).
//
// Strategy logic (as specified):
//   - Regime = POSITIVE gamma whenever price is above Gamma Flip, NEGATIVE whenever below - re-evaluated
//     fresh every bar from live price vs the flip level, not fixed once per session. If price crosses the
//     flip mid-trade, the position's underlying regime premise has inverted and it is closed immediately
//     (see EvaluateTradingLogic) rather than left to ride out its original stop/target blindly.
//   - POSITIVE gamma (dealers long gamma -> hedging dampens volatility -> price tends to pin/mean-revert
//     between the walls): fade the walls, but ONLY when the EMA confluence filter agrees with the fade
//     direction - SHORT at the Call Wall when EMA reads bearish, LONG at the Put Wall when EMA reads
//     bullish. Entry triggers when price comes within Entry Proximity To Wall % of the wall. Target is the
//     Gamma Flip itself (the natural "center of gravity" in a pinned regime); stop is placed just beyond
//     the wall being faded.
//   - NEGATIVE gamma (dealers short gamma -> hedging AMPLIFIES moves -> breakouts tend to extend rather
//     than revert): trade the wall FAILING to hold, not fading it - SHORT when the Put Wall breaks down
//     (closes beyond it by Wall Break Buffer %) and EMA agrees bearish; LONG when the Call Wall breaks up
//     and EMA agrees bullish (the call-wall-breaks-long case is the natural symmetric counterpart to the
//     put-wall-breaks-short case as specified - flagging this assumption since only the short side was
//     spelled out explicitly). Stop is placed back inside the broken wall (a failed retest); target is a
//     configurable reward:risk multiple of the stop distance, since there's no natural "next wall" to aim
//     at without a third data point.
//
// Session: entries are only considered inside an Overnight Session window (Session Start -> Session End,
// in Session Timezone) - the levels are frozen once the US options market closes, so this bot is meant to
// run specifically against that frozen snapshot, not as an all-day strategy. Any open position is force-
// closed at Session End regardless of stop/target, same no-overnight-past-the-window philosophy as the
// daily bot's force-exit (except here the "session" IS the overnight window, not the cash session).
//
// EMA Trend Filter, risk-% position sizing (off free margin, matching the latest weekly-bot revision), and
// the general parameter/state/logging conventions are deliberately identical to DailySessionReversalCBot.cs
// / WeeklyTPReversalWindowCBot.cs for consistency - see those files for the same mechanism explained in
// more depth.
//
// This file has not been compiled inside cTrader. Requires AccessRights.FullAccess (below) for the feed
// HTTP call - cAlgo will prompt for network access permission on start.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class GammaWallOvernightCBot : Robot
    {
        private const string PositionLabel = "GammaWallOvernight";

        // ---------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------

        [Parameter("OptionCharts Base URL", DefaultValue = "https://optioncharts.io", Group = "Gamma Data",
            Description = "Base URL for the gamma exposure fetch - see the file header for exactly what this hits and why.")]
        public string OptionChartsBaseUrl { get; set; }

        [Parameter("Options Ticker (for gamma data)", DefaultValue = "$NDX", Group = "Gamma Data",
            Description = "The OPTIONS-CHAIN ticker to query for gamma exposure - separate from the Symbol this cBot trades (e.g. your broker's 'US100' CFD), since the options chain and the tradeable instrument are quoted under different symbols. Get this wrong and the fetch will 404 or return another instrument's levels entirely.")]
        public string GammaTickerSymbol { get; set; }

        [Parameter("Gamma Exposure Basis", DefaultValue = "volume", Group = "Gamma Data",
            Description = "'volume' or 'open_interest' - confirmed these produce meaningfully different wall levels (one real check: open_interest gave call/put wall 29750/29900, volume gave 30290/30000 for the same expiration, same moment). Defaulted to volume per your stated overnight use case - it reflects the most recent session's actual flow rather than potentially-stale multi-day open interest.")]
        public string GammaExposureBasis { get; set; }

        [Parameter("Expiration Override (blank = site's default/nearest)", DefaultValue = "", Group = "Gamma Data",
            Description = "Leave blank to let optioncharts.io pick its own default expiration (confirmed it auto-selects the nearest one when this is omitted). Format if you want to pin a specific one: 'YYYY-MM-DD:w', e.g. '2026-08-17:w'.")]
        public string GammaExpirationOverride { get; set; }

        [Parameter("Gamma Refresh (minutes)", DefaultValue = 5, MinValue = 1, Group = "Gamma Data")]
        public int GammaRefreshMinutes { get; set; }

        [Parameter("Max Gamma Data Age (minutes)", DefaultValue = 30, MinValue = 1, Group = "Gamma Data",
            Description = "If the feed hasn't been SUCCESSFULLY fetched within this many minutes, treat levels as stale and stand aside - this catches the fetcher/feed silently dying, not the underlying market values being unchanged (those legitimately don't move while the levels are 'locked' between US market close and open).")]
        public int MaxGammaDataAgeMinutes { get; set; }

        [Parameter("Use EMA Trend Filter", DefaultValue = true, Group = "EMA Trend Filter",
            Description = "Same mechanism as the daily/weekly bots: EMA Fast above EMA Slow = bullish, Slow above Fast = bearish, checked across up to 7 independent timeframes, requiring Minimum Timeframes Agreeing of them to agree before it counts as a direction.")]
        public bool UseEmaTrendFilter { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 21, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", DefaultValue = 353, MinValue = 1, Group = "EMA Trend Filter")]
        public int EmaSlowPeriod { get; set; }

        [Parameter("EMA Timeframe 1", DefaultValue = "Minute5", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame1 { get; set; }

        [Parameter("Use EMA Timeframe 2", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame2 { get; set; }

        [Parameter("EMA Timeframe 2", DefaultValue = "Minute15", Group = "EMA Trend Filter")]
        public TimeFrame EmaTimeFrame2 { get; set; }

        [Parameter("Use EMA Timeframe 3", DefaultValue = true, Group = "EMA Trend Filter")]
        public bool UseEmaTimeFrame3 { get; set; }

        [Parameter("EMA Timeframe 3", DefaultValue = "Hour2", Group = "EMA Trend Filter")]
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

        [Parameter("Minimum Timeframes Agreeing", DefaultValue = 5, MinValue = 1, MaxValue = 7, Group = "EMA Trend Filter")]
        public int MinTimeframesAgreeing { get; set; }

        [Parameter("Session Timezone (IANA or Windows id)", DefaultValue = "America/New_York", Group = "Overnight Session",
            Description = "Anchored to US market hours rather than London, since the whole premise is trading against levels frozen at the US options close.")]
        public string SessionTimeZoneId { get; set; }

        [Parameter("Session Start Hour (after US close)", DefaultValue = 16, MinValue = 0, MaxValue = 23, Group = "Overnight Session",
            Description = "Approximate - US cash/options close is ~16:00 ET; the default 16:15 gives a few minutes' buffer for the day's final gamma snapshot to settle. Adjust to match when your feed's data actually goes 'locked'.")]
        public int SessionStartHour { get; set; }

        [Parameter("Session Start Minute", DefaultValue = 15, MinValue = 0, MaxValue = 59, Group = "Overnight Session")]
        public int SessionStartMinute { get; set; }

        [Parameter("Session End Hour (before US open)", DefaultValue = 9, MinValue = 0, MaxValue = 23, Group = "Overnight Session",
            Description = "Approximate - US options market reopens ~9:30 ET; the default 9:35 forces flat just after, since the 'frozen levels' premise stops holding once the market reopens and fresh gamma data starts flowing again.")]
        public int SessionEndHour { get; set; }

        [Parameter("Session End Minute", DefaultValue = 35, MinValue = 0, MaxValue = 59, Group = "Overnight Session")]
        public int SessionEndMinute { get; set; }

        [Parameter("Trade Longs", DefaultValue = true, Group = "Strategy")]
        public bool EnableLongs { get; set; }

        [Parameter("Trade Shorts", DefaultValue = true, Group = "Strategy")]
        public bool EnableShorts { get; set; }

        [Parameter("Entry Proximity To Wall %", DefaultValue = 0.35, MinValue = 0.01, Group = "Strategy",
            Description = "How close (as a % of the wall price) price must come to the Call/Put Wall to trigger a positive-gamma FADE entry. Adjustable, as specified.")]
        public double EntryProximityToWallPercent { get; set; }

        [Parameter("Wall Break Buffer %", DefaultValue = 0.10, MinValue = 0, Group = "Strategy",
            Description = "In negative gamma, price must CLOSE beyond the wall by this % (not just wick-touch it) to count as the wall 'failing to hold' and trigger a breakout entry - avoids triggering on a single-pip poke through the level.")]
        public double WallBreakBufferPercent { get; set; }

        [Parameter("Stop Buffer Beyond Wall %", DefaultValue = 0.15, MinValue = 0, Group = "Strategy",
            Description = "Fade trades: stop is placed this % beyond the wall being faded. Breakout trades: stop is placed this % beyond the broken wall, back on the side it broke from (a failed-retest stop).")]
        public double StopBufferPercent { get; set; }

        [Parameter("Breakout Reward:Risk Ratio", DefaultValue = 2.0, MinValue = 0.1, Group = "Strategy",
            Description = "Negative-gamma breakout trades have no natural 'next wall' target, so the target is this multiple of the stop distance instead.")]
        public double BreakoutRewardToRiskRatio { get; set; }

        [Parameter("Min Reward:Risk To Take Fade Trade", DefaultValue = 1.0, MinValue = 0.1, Group = "Strategy",
            Description = "Positive-gamma fade trades target the Gamma Flip - if that makes for a worse-than-this reward:risk (e.g. the flip is unusually close to the wall being faded), the trade is skipped rather than taken on principle.")]
        public double MinRewardToRiskRatio { get; set; }

        [Parameter("Risk % Of Free Margin Per Trade", DefaultValue = 1.0, MinValue = 0.01, Group = "Strategy")]
        public double RiskPercent { get; set; }

        [Parameter("Max Position Size (lots)", DefaultValue = 5.0, MinValue = 0.01, Group = "Strategy")]
        public double MaxVolumeLots { get; set; }

        [Parameter("Fallback Volume (lots) If Stop Loss Disabled", DefaultValue = 0.10, MinValue = 0.01, Group = "Strategy")]
        public double FallbackVolumeLots { get; set; }

        [Parameter("Show Entry/Exit Reasoning Labels", DefaultValue = true, Group = "Strategy")]
        public bool ShowReasons { get; set; }

        [Parameter("Enable Debug Logging", DefaultValue = false, Group = "Strategy",
            Description = "Prints one diagnostic line per Locked Execution Timeframe bar: current gamma levels/age, regime, EMA confluence state, and distance to each wall.")]
        public bool EnableDebugLogging { get; set; }

        [Parameter("Locked Execution Timeframe", DefaultValue = "Minute1", Group = "Strategy")]
        public TimeFrame ExecutionTimeFrame { get; set; }

        // ---------------------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------------------

        private class GammaLevels
        {
            public double CallWall = double.NaN;
            public double PutWall = double.NaN;
            public double GammaFlip = double.NaN;
            public DateTime FetchedUtc = DateTime.MinValue;
            public bool IsValid => !double.IsNaN(CallWall) && !double.IsNaN(PutWall) && !double.IsNaN(GammaFlip);
        }

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private GammaLevels _gamma = new GammaLevels();

        private Bars _execBars;
        private Bars _emaBars1, _emaBars2, _emaBars3, _emaBars4, _emaBars5, _emaBars6, _emaBars7;
        private ExponentialMovingAverage _emaFast1, _emaSlow1;
        private ExponentialMovingAverage _emaFast2, _emaSlow2;
        private ExponentialMovingAverage _emaFast3, _emaSlow3;
        private ExponentialMovingAverage _emaFast4, _emaSlow4;
        private ExponentialMovingAverage _emaFast5, _emaSlow5;
        private ExponentialMovingAverage _emaFast6, _emaSlow6;
        private ExponentialMovingAverage _emaFast7, _emaSlow7;
        private TimeZoneInfo _sessionTz;

        private Position _openPosition;
        private double _entryPrice;
        private string _entryRegime; // "positive" or "negative" - the regime the open trade was entered under
        private int _objCounter;

        // ---------------------------------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------------------------------

        protected override void OnStart()
        {
            _sessionTz = ResolveTimeZone(SessionTimeZoneId);

            // Headers matching what a real browser sends for this endpoint (confirmed working via direct
            // testing) - htmx backends like this one commonly branch behavior on HX-Request, and a bare/
            // absent User-Agent is a common, cheap bot-blocking trigger.
            _http.DefaultRequestHeaders.Add("HX-Request", "true");
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            _http.DefaultRequestHeaders.Accept.ParseAdd("text/html, */*");

            _execBars = MarketData.GetBars(ExecutionTimeFrame, SymbolName);
            _execBars.BarOpened += OnExecBarOpened;

            if (UseEmaTrendFilter)
            {
                TrySetupEmaSlot(EmaTimeFrame1, out _emaBars1, out _emaFast1, out _emaSlow1);
                if (UseEmaTimeFrame2) TrySetupEmaSlot(EmaTimeFrame2, out _emaBars2, out _emaFast2, out _emaSlow2);
                if (UseEmaTimeFrame3) TrySetupEmaSlot(EmaTimeFrame3, out _emaBars3, out _emaFast3, out _emaSlow3);
                if (UseEmaTimeFrame4) TrySetupEmaSlot(EmaTimeFrame4, out _emaBars4, out _emaFast4, out _emaSlow4);
                if (UseEmaTimeFrame5) TrySetupEmaSlot(EmaTimeFrame5, out _emaBars5, out _emaFast5, out _emaSlow5);
                if (UseEmaTimeFrame6) TrySetupEmaSlot(EmaTimeFrame6, out _emaBars6, out _emaFast6, out _emaSlow6);
                if (UseEmaTimeFrame7) TrySetupEmaSlot(EmaTimeFrame7, out _emaBars7, out _emaFast7, out _emaSlow7);

                int enabledCount = 1 + (UseEmaTimeFrame2 ? 1 : 0) + (UseEmaTimeFrame3 ? 1 : 0)
                                     + (UseEmaTimeFrame4 ? 1 : 0) + (UseEmaTimeFrame5 ? 1 : 0)
                                     + (UseEmaTimeFrame6 ? 1 : 0) + (UseEmaTimeFrame7 ? 1 : 0);
                if (MinTimeframesAgreeing > enabledCount)
                    Print($"Warning: Minimum Timeframes Agreeing ({MinTimeframesAgreeing}) is higher than the number of enabled EMA timeframes ({enabledCount}) - the EMA trend filter will never produce a signal.");
            }

            _openPosition = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == PositionLabel);
            if (_openPosition != null)
            {
                _entryPrice = _openPosition.EntryPrice;
                Print("Recovered an existing open position on start: " + _openPosition.TradeType);
            }

            Positions.Closed += OnPositionsClosed;

            RefreshGammaLevels(); // get an initial read before waiting for the first timer tick
            Timer.Start(TimeSpan.FromMinutes(GammaRefreshMinutes));
        }

        protected override void OnStop()
        {
            if (_execBars != null) _execBars.BarOpened -= OnExecBarOpened;
            Positions.Closed -= OnPositionsClosed;
            Timer.Stop();
            _http.Dispose();
        }

        protected override void OnTimer()
        {
            RefreshGammaLevels();
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
        // Gamma feed
        // ---------------------------------------------------------------------------------------------

        // Deliberately synchronous/blocking (called from OnTimer, on the cBot's own thread) rather than
        // async - cAlgo API objects (Print, Server, Positions, ...) are not guaranteed safe to touch from
        // an arbitrary thread-pool continuation, and this only runs once every GammaRefreshMinutes, so a
        // brief blocking HTTP call here is a deliberate, low-cost tradeoff for staying on the safe thread.
        private void RefreshGammaLevels()
        {
            try
            {
                string url = BuildGammaUrl();
                string html = _http.GetStringAsync(url).Result;
                var parsed = ParseGammaHtml(html);
                if (parsed.IsValid)
                {
                    parsed.FetchedUtc = Server.TimeInUtc;
                    _gamma = parsed;
                    DrawGammaLines();
                    Print($"[GAMMA] Refreshed: CallWall={_gamma.CallWall:F2} PutWall={_gamma.PutWall:F2} GammaFlip={_gamma.GammaFlip:F2}");
                }
                else
                {
                    Print("Gamma fetch returned incomplete/unparseable data (site layout may have changed, or the expiration has no usable exposure data) - keeping previous levels.");
                }
            }
            catch (Exception ex)
            {
                Print($"Gamma fetch failed ({ex.Message}) - keeping previous levels until next refresh.");
            }
        }

        private string BuildGammaUrl()
        {
            string url = $"{OptionChartsBaseUrl.TrimEnd('/')}/async/options_charts/gamma_exposure" +
                          $"?option_type=all&strike_range=all&ticker={Uri.EscapeDataString(GammaTickerSymbol)}" +
                          $"&gamma_exposure_type={Uri.EscapeDataString(GammaExposureBasis)}";
            if (!string.IsNullOrWhiteSpace(GammaExpirationOverride))
                url += $"&expiration_dates={Uri.EscapeDataString(GammaExpirationOverride)}";
            return url;
        }

        // Hand-rolled extraction rather than a JSON library dependency (matching this codebase's existing
        // pattern), since the response is an HTML fragment with the actual data sitting in two inline JS
        // variable assignments, not a clean top-level JSON document.
        private GammaLevels ParseGammaHtml(string html)
        {
            var levels = new GammaLevels();

            // Call Wall / Put Wall come straight from the single-expiration summary blob
            // (`let series_data = [{...,"call_wall":X,"put_wall":Y,...}]`).
            var wallMatch = Regex.Match(html, "\"call_wall\":(-?[0-9]+(?:\\.[0-9]+)?).*?\"put_wall\":(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!wallMatch.Success) return levels;
            if (!TryParseInvariant(wallMatch.Groups[1].Value, out double callWall)) return levels;
            if (!TryParseInvariant(wallMatch.Groups[2].Value, out double putWall)) return levels;
            levels.CallWall = callWall;
            levels.PutWall = putWall;

            // Gamma Flip: computed from the full per-strike array (`var chart_exposure_data = {...,
            // "exposure_by_strike_series":[{"strike":..,"call_exposure":..,"put_exposure":..,
            // "net_exposure":..}, ...]}`) - see file header for the cumulative-crossing method.
            var strikeMatches = Regex.Matches(html,
                "\\{\"strike\":(-?[0-9]+(?:\\.[0-9]+)?),\"call_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"put_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"net_exposure\":(-?[0-9]+(?:\\.[0-9]+)?)\\}");
            if (strikeMatches.Count < 2) return levels; // need at least 2 points to find a crossing

            var points = new List<(double strike, double net)>();
            foreach (Match m in strikeMatches)
            {
                if (!TryParseInvariant(m.Groups[1].Value, out double strike)) continue;
                if (!TryParseInvariant(m.Groups[4].Value, out double net)) continue;
                points.Add((strike, net));
            }
            points.Sort((a, b) => a.strike.CompareTo(b.strike));

            double cum = 0;
            var cumSeries = new List<(double strike, double cum)>();
            foreach (var p in points)
            {
                cum += p.net;
                cumSeries.Add((p.strike, cum));
            }

            // Standard "zero gamma" definition: scan ascending by strike, take the LAST point the
            // cumulative curve is negative before it turns permanently positive - this specifically
            // ignores small local noise crossings near the money (there are often many) in favor of the
            // one dominant, sustained regime change, which is the level that actually matters.
            int lastNegIdx = -1;
            for (int i = 0; i < cumSeries.Count; i++)
                if (cumSeries[i].cum < 0) lastNegIdx = i;

            if (lastNegIdx >= 0 && lastNegIdx < cumSeries.Count - 1)
            {
                var (k1, c1) = cumSeries[lastNegIdx];
                var (k2, c2) = cumSeries[lastNegIdx + 1];
                double frac = -c1 / (c2 - c1);
                levels.GammaFlip = k1 + frac * (k2 - k1);
            }
            // else: cumulative net exposure never goes negative (or never recovers) across the whole
            // strike range - no usable flip today, GammaFlip stays NaN and IsValid stays false.

            return levels;
        }

        private bool TryParseInvariant(string s, out double value)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private bool GammaDataIsFresh()
        {
            if (!_gamma.IsValid) return false;
            return (Server.TimeInUtc - _gamma.FetchedUtc) <= TimeSpan.FromMinutes(MaxGammaDataAgeMinutes);
        }

        // ---------------------------------------------------------------------------------------------
        // EMA trend filter (identical mechanism to the daily/weekly bots)
        // ---------------------------------------------------------------------------------------------

        private (string bias, string reason) GetEmaTrendBias()
        {
            if (!UseEmaTrendFilter) return (null, null);

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
            if (UseEmaTimeFrame2) Add(EmaTimeFrame2.ToString(), _emaFast2, _emaSlow2);
            if (UseEmaTimeFrame3) Add(EmaTimeFrame3.ToString(), _emaFast3, _emaSlow3);
            if (UseEmaTimeFrame4) Add(EmaTimeFrame4.ToString(), _emaFast4, _emaSlow4);
            if (UseEmaTimeFrame5) Add(EmaTimeFrame5.ToString(), _emaFast5, _emaSlow5);
            if (UseEmaTimeFrame6) Add(EmaTimeFrame6.ToString(), _emaFast6, _emaSlow6);
            if (UseEmaTimeFrame7) Add(EmaTimeFrame7.ToString(), _emaFast7, _emaSlow7);

            if (readings.Count == 0)
                return (null, "No EMA readings yet");

            int longCount = readings.Count(r => r.trend == "long");
            int shortCount = readings.Count(r => r.trend == "short");
            string detail = string.Join(", ", readings.Select(r => $"{r.tf}={r.trend}"));

            if (longCount >= MinTimeframesAgreeing)
                return ("long", $"EMA confluence {longCount}/{readings.Count} bullish ({detail})");
            if (shortCount >= MinTimeframesAgreeing)
                return ("short", $"EMA confluence {shortCount}/{readings.Count} bearish ({detail})");

            return (null, $"No confluence: {longCount} long / {shortCount} short of {readings.Count} ({detail})");
        }

        // ---------------------------------------------------------------------------------------------
        // Overnight session window
        // ---------------------------------------------------------------------------------------------

        private (bool inSession, DateTime local) GetSessionState(DateTime timeUtc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc), _sessionTz);
            int nowMinutes = local.Hour * 60 + local.Minute;
            int startMinutes = SessionStartHour * 60 + SessionStartMinute;
            int endMinutes = SessionEndHour * 60 + SessionEndMinute;

            // Overnight window wraps past midnight (start > end), e.g. 16:15 -> 09:35 next day.
            bool inSession = startMinutes > endMinutes
                ? (nowMinutes >= startMinutes || nowMinutes < endMinutes)
                : (nowMinutes >= startMinutes && nowMinutes < endMinutes);

            return (inSession, local);
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
            var (inSession, localTime) = GetSessionState(xBar.OpenTime);

            // Force-flat outside the overnight window, regardless of stop/target - the "frozen levels"
            // premise doesn't hold once the US options market is open and gamma data starts moving again.
            if (!inSession)
            {
                if (_openPosition != null)
                    CloseWithReason("Session end - outside overnight window, forcing flat", xBar.Close, xBar.OpenTime);
                return;
            }

            if (!GammaDataIsFresh())
            {
                if (EnableDebugLogging) Print("[DEBUG] Standing aside - gamma data missing or stale");
                return;
            }

            bool positiveGamma = xBar.Close > _gamma.GammaFlip;
            string regime = positiveGamma ? "positive" : "negative";

            // Regime-flip safety exit: the theoretical basis for an open trade (dampening vs amplifying)
            // has inverted if the current regime no longer matches the regime it was entered under.
            if (_openPosition != null && _entryRegime != null && _entryRegime != regime)
            {
                CloseWithReason($"Regime flip - entered under {_entryRegime} gamma, now {regime} gamma (flip {_gamma.GammaFlip:F2} no longer holding) - premise for this trade has inverted", xBar.Close, xBar.OpenTime);
            }

            (string emaBias, string emaReason) = GetEmaTrendBias();

            if (EnableDebugLogging)
            {
                double distCall = Math.Abs(xBar.Close - _gamma.CallWall) / _gamma.CallWall * 100.0;
                double distPut = Math.Abs(xBar.Close - _gamma.PutWall) / _gamma.PutWall * 100.0;
                Print($"[DEBUG] Close={xBar.Close:F2} Regime={regime} (flip {_gamma.GammaFlip:F2}) CallWall={_gamma.CallWall:F2} ({distCall:F3}% away) PutWall={_gamma.PutWall:F2} ({distPut:F3}% away) | EMA={(emaBias ?? "none")} ({emaReason ?? "n/a"}) | OpenPosition={(_openPosition != null)}");
            }

            if (_openPosition != null || emaBias == null) return;

            if (positiveGamma)
                TryFadeEntry(xBar, emaBias, regime);
            else
                TryBreakoutEntry(xBar, emaBias, regime);
        }

        // Positive gamma: fade toward the Gamma Flip, only with EMA agreement.
        private void TryFadeEntry(Bar xBar, string emaBias, string regime)
        {
            double distCallPct = Math.Abs(xBar.Close - _gamma.CallWall) / _gamma.CallWall * 100.0;
            double distPutPct = Math.Abs(xBar.Close - _gamma.PutWall) / _gamma.PutWall * 100.0;

            if (emaBias == "short" && EnableShorts && distCallPct <= EntryProximityToWallPercent)
            {
                double stop = _gamma.CallWall * (1 + StopBufferPercent / 100.0);
                double target = _gamma.GammaFlip;
                if (!PassesRewardRisk(xBar.Close, stop, target, TradeType.Sell)) return;

                string reason = $"Positive gamma FADE SHORT at Call Wall {_gamma.CallWall:F2} (within {distCallPct:F3}%), target Gamma Flip {target:F2}, {emaBias.ToUpper()} EMA agrees ({emaReasonShort(emaBias)})";
                OpenPosition(TradeType.Sell, xBar, reason, stop, target, regime);
            }
            else if (emaBias == "long" && EnableLongs && distPutPct <= EntryProximityToWallPercent)
            {
                double stop = _gamma.PutWall * (1 - StopBufferPercent / 100.0);
                double target = _gamma.GammaFlip;
                if (!PassesRewardRisk(xBar.Close, stop, target, TradeType.Buy)) return;

                string reason = $"Positive gamma FADE LONG at Put Wall {_gamma.PutWall:F2} (within {distPutPct:F3}%), target Gamma Flip {target:F2}, {emaBias.ToUpper()} EMA agrees ({emaReasonShort(emaBias)})";
                OpenPosition(TradeType.Buy, xBar, reason, stop, target, regime);
            }
        }

        // Negative gamma: trade the wall failing to hold (breakout continuation), only with EMA agreement.
        private void TryBreakoutEntry(Bar xBar, string emaBias, string regime)
        {
            bool putWallBroke = xBar.Close < _gamma.PutWall * (1 - WallBreakBufferPercent / 100.0);
            bool callWallBroke = xBar.Close > _gamma.CallWall * (1 + WallBreakBufferPercent / 100.0);

            if (emaBias == "short" && EnableShorts && putWallBroke)
            {
                double stop = _gamma.PutWall * (1 + StopBufferPercent / 100.0);
                double stopDist = stop - xBar.Close;
                double target = xBar.Close - stopDist * BreakoutRewardToRiskRatio;

                string reason = $"Negative gamma BREAKDOWN SHORT - Put Wall {_gamma.PutWall:F2} failed to hold, target {target:F2} ({BreakoutRewardToRiskRatio:F1}R), {emaBias.ToUpper()} EMA agrees";
                OpenPosition(TradeType.Sell, xBar, reason, stop, target, regime);
            }
            else if (emaBias == "long" && EnableLongs && callWallBroke)
            {
                double stop = _gamma.CallWall * (1 - StopBufferPercent / 100.0);
                double stopDist = xBar.Close - stop;
                double target = xBar.Close + stopDist * BreakoutRewardToRiskRatio;

                string reason = $"Negative gamma BREAKOUT LONG - Call Wall {_gamma.CallWall:F2} failed to hold, target {target:F2} ({BreakoutRewardToRiskRatio:F1}R), {emaBias.ToUpper()} EMA agrees";
                OpenPosition(TradeType.Buy, xBar, reason, stop, target, regime);
            }
        }

        private string emaReasonShort(string bias) => bias == "long" ? "bullish confluence" : "bearish confluence";

        private bool PassesRewardRisk(double entry, double stop, double target, TradeType type)
        {
            double risk = Math.Abs(entry - stop);
            double reward = Math.Abs(target - entry);
            if (risk <= 0) return false;

            // Sanity check: the target must actually sit in the profitable direction, not just be the flip
            // level mechanically - protects against a nonsensical backwards TP if the day's levels are unusual.
            bool directionOk = type == TradeType.Buy ? target > entry : target < entry;
            if (!directionOk) return false;

            return (reward / risk) >= MinRewardToRiskRatio;
        }

        // ---------------------------------------------------------------------------------------------
        // Position management
        // ---------------------------------------------------------------------------------------------

        private void OpenPosition(TradeType type, Bar xBar, string reason, double stopPrice, double targetPrice, string regime)
        {
            double estEntry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double stopDistance = Math.Abs(estEntry - stopPrice);

            double volumeInUnits = CalculateVolume(stopDistance);

            var result = ExecuteMarketOrder(type, SymbolName, volumeInUnits, PositionLabel, null, null, reason);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("Entry failed: " + result.Error);
                return;
            }

            _openPosition = result.Position;
            _entryPrice = result.Position.EntryPrice;
            _entryRegime = regime;

            _openPosition.ModifyStopLossPrice(stopPrice);
            _openPosition.ModifyTakeProfitPrice(targetPrice);

            Print(reason + $" | Volume: {volumeInUnits} units | FillPrice={_entryPrice:F2} Stop={stopPrice:F2} Target={targetPrice:F2} Equity={Account.Equity:F2}");
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
            _entryRegime = null;
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
                _entryRegime = null;
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

        private void DrawGammaLines()
        {
            var now = Server.TimeInUtc;
            var end = now.AddHours(20);
            Chart.RemoveObject("CallWall");
            Chart.RemoveObject("PutWall");
            Chart.RemoveObject("GammaFlip");
            Chart.DrawTrendLine("CallWall", now, _gamma.CallWall, end, _gamma.CallWall, Color.Red, 2, LineStyle.Dots);
            Chart.DrawTrendLine("PutWall", now, _gamma.PutWall, end, _gamma.PutWall, Color.Green, 2, LineStyle.Dots);
            Chart.DrawTrendLine("GammaFlip", now, _gamma.GammaFlip, end, _gamma.GammaFlip, Color.Yellow, 2, LineStyle.Dots);
        }
    }
}
