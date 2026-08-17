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
// Call Wall / Put Wall come straight from the gamma_exposure endpoint's response. Gamma Flip is NOT taken
// from that response (the site's own "gamma_zero_level" field is null in practice) and is NOT derived from
// the per-strike exposure array either - an earlier version of this file did that (summing net exposure
// cumulatively by strike), which is a fundamentally wrong proxy: it has no real connection to "the
// hypothetical spot price where total dealer gamma exposure flips sign," which is what Gamma Flip actually
// means. That version produced an implausible, wrong number, caught by inspection.
//
// This version computes Gamma Flip properly: fetches the raw option chain (bid/ask/open interest/volume
// per contract, from a second endpoint, pinned to the same expiration as the wall data), inverts
// Black-Scholes on each contract's mid price to get its implied vol, then re-prices Gamma across a grid of
// hypothetical spot prices with that vol held fixed - the flip is where the resulting dealer-gamma curve
// crosses zero nearest current spot. Validated directly against a real reference value from
// optioncharts.io (29,895.14): this method landed at 29,898.68, ~3.5 points off on a ~30,000 level
// (~0.01%). A version using optioncharts' own published implied-vol numbers directly (rather than
// inverting from bid/ask) was tried and performed WORSE (75 points off) - likely because their published
// IV includes illiquid/unstable far-strike quotes that this file's own inversion naturally rejects via
// non-convergence. See ComputeGammaFlip for the full method.
//
// Strategy logic:
//   - Regime = POSITIVE gamma whenever price is above Gamma Flip, NEGATIVE whenever below - re-evaluated
//     fresh every bar from live price vs the flip level, not fixed once per session. If price crosses the
//     flip mid-trade, the position's underlying regime premise has inverted and it is closed immediately
//     (see EvaluateTradingLogic) rather than left to ride out its original stop/target blindly.
//   - Regime alone does NOT gate which trade type is allowed anymore. "Positive gamma dampens / negative
//     gamma amplifies" is a real statistical tilt in the checked data, not a law - a wall can genuinely
//     break in positive gamma, and price can genuinely hold/pin at a wall in negative gamma. Both FADE and
//     BREAKOUT setups are evaluated every bar (fade checked first); what actually gates each one is the
//     wall's own confirmed ROLE, tracked bar-by-bar per wall (see UpdateWallRoles/WallRole): Untested ->
//     Testing (price came within Entry Proximity To Wall %) -> Holding (it pulled back without closing
//     beyond the wall - confirmed as real support/resistance) or Broken (closed beyond it by Wall Break
//     Buffer %, and stays Broken for the rest of the session once it fails).
//   - FADE: only taken against a wall that is NOT Broken - SHORT at the Call Wall when EMA reads bearish,
//     LONG at the Put Wall when EMA reads bullish, triggered on proximity. Target is Gamma Flip; stop is
//     placed just beyond the wall being faded.
//   - BREAKOUT: only taken against a wall that IS Broken, and (by default, Require Wall Tested Before
//     Breakout) was previously confirmed Holding before it broke - a wall that's never been tested hasn't
//     actually demonstrated it was acting as a level at all, so a break of it is a weaker signal than a
//     break of a wall that held once already. SHORT when the Put Wall breaks down and EMA agrees bearish;
//     LONG when the Call Wall breaks up and EMA agrees bullish. Stop is placed back inside the broken wall
//     (a failed retest); target is a configurable reward:risk multiple of the stop distance.
//   - Regime still matters for SIZING, not gating: a trade firing in its natural regime (fade in positive
//     gamma, breakout in negative gamma) gets full conviction; a trade firing OFF-regime (fade in negative
//     gamma, breakout in positive gamma) gets Off-Regime Risk Multiplier applied on top, since the
//     regime-magnitude tilt is still real even though it no longer blocks the trade outright. Similarly,
//     the negative-gamma stop-widen factor (see Volatility Conviction) only applies when the trade is
//     actually IN negative gamma, not just because Use Volatility Conviction is on.
//   - Exit management, in addition to stop/target: Take Profit At Wall Touch closes a position outright
//     the moment price reaches the wall ahead of it in the favorable direction (Call Wall for longs, Put
//     Wall for shorts) - walls are the strongest a-priori levels this bot has, so a live touch is treated
//     as a target reached. Wall Proximity Stop Tighten % moves the stop to breakeven once a profitable
//     position's price is within that % of the same favorable-direction wall, ahead of an actual touch.
//     See ManageWallProximityExit.
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
            Description = "'volume' or 'open_interest' - confirmed these produce meaningfully different wall levels (one real check: open_interest gave call/put wall 29750/29900, volume gave 30290/30000 for the same expiration, same moment). Defaulted to volume per your stated overnight use case - it reflects the most recent session's actual flow rather than potentially-stale multi-day open interest. Also selects which weight (Volume vs Open Interest) is used per-contract in the Gamma Flip calculation below, for consistency with the walls.")]
        public string GammaExposureBasis { get; set; }

        [Parameter("Risk-Free Rate (for Gamma Flip calc)", DefaultValue = 0.045, MinValue = 0, Group = "Gamma Data",
            Description = "Used only for the Black-Scholes implied-vol inversion and gamma repricing that computes Gamma Flip (see file header) - has a small effect on short-dated weekly options. Not used anywhere else.")]
        public double RiskFreeRate { get; set; }

        [Parameter("Dividend Yield (for Gamma Flip calc)", DefaultValue = 0.0, MinValue = 0, Group = "Gamma Data",
            Description = "Used in the same Black-Scholes calc as Risk-Free Rate above. Defaulted to 0 rather than NDX's ~0.7% real yield - empirically (checked against a real reference Gamma Flip value from optioncharts.io) 0% landed closer, within ~3.5 points on a ~30,000 level. Only calibrated against one observation though - worth re-checking if you have another reference point.")]
        public double DividendYield { get; set; }

        [Parameter("Contract Multiplier (for Gamma Flip calc)", DefaultValue = 100, MinValue = 1, Group = "Gamma Data",
            Description = "Standard $100-per-point multiplier for US index options (NDX included). Only affects the absolute scale of the dealer-gamma curve used to find Gamma Flip, not where it crosses zero - safe to leave at 100 unless you know your ticker uses something else.")]
        public double ContractMultiplier { get; set; }

        [Parameter("Gamma Flip Strike Range (% of spot)", DefaultValue = 20.0, MinValue = 1, Group = "Gamma Data",
            Description = "Only strikes within this % of the current underlying price are used in the Gamma Flip calculation - excludes far OTM/ITM strikes, which contribute negligible real gamma but can have unreliable/synthetic-looking quotes (near-zero volume, suspiciously uniform spreads) that would otherwise add noise to the implied-vol inversion.")]
        public double IvStrikeRangePercent { get; set; }

        [Parameter("Skip Trading On Monthly/Weekly Overlap Days", DefaultValue = true, Group = "Gamma Data",
            Description = "Confirmed real case: the 3rd-Friday monthly expiration date lists a SEPARATE weekly (':w') and monthly (':m') series with meaningfully different, individually thin wall data (one real check: weekly gave 30150/30125, monthly gave a degenerate 29900/29900 - same strike for both walls, a strong sign of insufficient real spread). There's no validated way to reconcile two ambiguous series into one trustworthy level, so the default is to stand down entirely for that night rather than guess. Also triggers on ANY day where the resolved call wall and put wall come back identical (a general low-data-quality signal, not just the monthly-overlap case specifically). Off = use whichever series/expiration the site resolves as default, no special handling (not recommended - untested).")]
        public bool SkipMonthlyOverlapDays { get; set; }

        [Parameter("Run Weekly Concentration Sweep", DefaultValue = true, Group = "Gamma Data",
            Description = "Once per calendar day (not every refresh), sweeps every expiration listed for the next Weekly Sweep Max Days and logs Call Wall / Put Wall / Gamma Flip / Concentration Price for each - purely informational, does not affect trading. Concentration Price is the single strike with the largest combined call+put exposure that day (same underlying data Call Wall/Put Wall come from, just looking at both sides together) - logged so you can track whether it tends to line up with that week's eventual high or low, per your hypothesis. Not used in any entry/exit logic yet.")]
        public bool RunWeeklySweep { get; set; }

        [Parameter("Weekly Sweep Max Days Ahead", DefaultValue = 7, MinValue = 1, MaxValue = 21, Group = "Gamma Data")]
        public int WeeklySweepMaxDays { get; set; }

        [Parameter("Expiration Override (blank = site's default/nearest)", DefaultValue = "", Group = "Gamma Data",
            Description = "Leave blank to let optioncharts.io pick its own default expiration (confirmed it auto-selects the nearest one when this is omitted). Format if you want to pin a specific one: 'YYYY-MM-DD:w', e.g. '2026-08-17:w'.")]
        public string GammaExpirationOverride { get; set; }

        [Parameter("Gamma Refresh - Market Open (minutes)", DefaultValue = 5, MinValue = 1, Group = "Gamma Data",
            Description = "Refresh cadence used OUTSIDE the Market Closed Window below (i.e. while the US options market is actually open and gamma data is live/changing). This is also the underlying Timer tick rate - the Market Closed cadence below is enforced by skipping ticks, not by a separate timer.")]
        public int GammaRefreshMinutes { get; set; }

        [Parameter("Gamma Refresh - Market Closed (minutes)", DefaultValue = 60, MinValue = 1, Group = "Gamma Data",
            Description = "Slower refresh cadence used INSIDE the Market Closed Window below, since gamma data is frozen/locked while the US options market is shut and there's nothing new to catch by polling every few minutes.")]
        public int GammaOvernightRefreshMinutes { get; set; }

        [Parameter("Market Closed Window Start Hour", DefaultValue = 21, MinValue = 0, MaxValue = 23, Group = "Gamma Data",
            Description = "In Session Timezone. Approximate US options market close - governs which of the two refresh cadences above is used, separately from the Overnight Session trading window (which can be tuned independently).")]
        public int MarketClosedStartHour { get; set; }

        [Parameter("Market Closed Window Start Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59, Group = "Gamma Data")]
        public int MarketClosedStartMinute { get; set; }

        [Parameter("Market Closed Window End Hour", DefaultValue = 13, MinValue = 0, MaxValue = 23, Group = "Gamma Data",
            Description = "In Session Timezone. Approximate US options market reopen.")]
        public int MarketClosedEndHour { get; set; }

        [Parameter("Market Closed Window End Minute", DefaultValue = 25, MinValue = 0, MaxValue = 59, Group = "Gamma Data")]
        public int MarketClosedEndMinute { get; set; }

        [Parameter("Max Gamma Data Age (minutes)", DefaultValue = 90, MinValue = 1, Group = "Gamma Data",
            Description = "If the feed hasn't been SUCCESSFULLY fetched within this many minutes, treat levels as stale and stand aside - this catches the fetcher/feed silently dying, not the underlying market values being unchanged. Set comfortably above Gamma Refresh - Market Closed (default 60min) so the slower overnight cadence itself doesn't trip this - default 90 gives a 30min buffer over one missed hourly refresh.")]
        public int MaxGammaDataAgeMinutes { get; set; }

        // -----------------------------------------------------------------------------------------------
        // Volatility Conviction - three mechanisms, each grounded in a specific real result checked
        // against 529 days of real NDX daily options history before being implemented:
        //   1. Negative gamma sees ~54% bigger next-day moves than positive gamma (0.865% vs 1.330% mean
        //      |return|) - widens the stop/target on breakout (negative-gamma) trades specifically, since
        //      fade trades only ever fire in positive gamma and don't need this.
        //   2. IV term structure backwardation (near-term ATM IV > ~30d ATM IV) independently predicts
        //      bigger moves too (0.815% vs 1.171%, and combined with negative gamma: 0.776% neither ->
        //      1.447% both - close to double) - boosts risk-% on BOTH trade types when detected.
        //   3. Monthly OPEX pinning-then-release: price drifts away from Max Pain at only +0.019%/day in
        //      the 10 trading days before monthly OPEX (t=2.56 vs the post-OPEX rate, real effect) then
        //      +0.212%/day after - boosts fade-trade risk-% in the pinned pre-OPEX window, reduces it in
        //      the just-released post-OPEX window. FADE TRADES ONLY - this is specifically about pinning
        //      reliability, which is what fades depend on; breakout trades don't rely on that premise.
        //      Tested and explicitly did NOT replicate for weekly (Friday) expirations at 4x the sample
        //      size, so this is scoped to monthly (3rd Friday) OPEX only, not applied every week.
        // -----------------------------------------------------------------------------------------------

        [Parameter("Use Volatility Conviction", DefaultValue = true, Group = "Volatility Conviction",
            Description = "Master toggle for all three mechanisms above. Off = plain Risk % Of Free Margin Per Trade and unwidened stops everywhere, matching the original behavior.")]
        public bool UseVolatilityConviction { get; set; }

        [Parameter("Negative Gamma Stop/Target Widen Factor", DefaultValue = 1.5, MinValue = 1.0, Group = "Volatility Conviction",
            Description = "Multiplies Stop Buffer Beyond Wall % (and therefore the R:R-derived target distance too) for BREAKOUT trades that are actually firing IN negative gamma - gated on the live regime at entry, not just on a trade being a breakout, since breakouts can now also fire in positive gamma (see the Strategy logic header) and shouldn't get the negative-gamma-sized stop when the regime doesn't back that up. Default 1.5 matches the real measured ratio (1.330%/0.865% = 1.54x) of next-day move size in negative vs positive gamma.")]
        public double NegativeGammaStopWidenFactor { get; set; }

        [Parameter("Off-Regime Risk Multiplier", DefaultValue = 0.6, MinValue = 0.05, MaxValue = 1.0, Group = "Volatility Conviction",
            Description = "Applied when a trade fires in its NON-natural regime - a fade trade taken in negative gamma, or a breakout trade taken in positive gamma. Both are allowed now (a wall's confirmed Holding/Broken role gates the trade, not the regime label - see Strategy group), but the regime-magnitude tilt measured in real data (positive gamma dampens, negative amplifies) is still real, so an off-regime trade is sized down rather than given full conviction. Not empirically calibrated to a specific number - a judgment-adjusted haircut, same caveat as the OPEX multipliers below.")]
        public double OffRegimeRiskMultiplier { get; set; }

        [Parameter("Far-Term Expiration Target (days out)", DefaultValue = 30, MinValue = 7, Group = "Volatility Conviction",
            Description = "How many calendar days out to look for the far-term IV comparison point (checked once per day, not every refresh - term structure doesn't move fast enough to need 5-minute freshness).")]
        public int FarTermExpirationDaysTarget { get; set; }

        [Parameter("Backwardation Risk Multiplier", DefaultValue = 1.3, MinValue = 1.0, Group = "Volatility Conviction",
            Description = "Risk % multiplier applied to BOTH trade types when near-term ATM IV > far-term ATM IV (backwardation - a real, independent stress signal in the checked data). Applies on top of the OPEX multiplier below for fade trades, so both-signals-aligned fades get compounded conviction, matching the real combined effect being close to double the baseline.")]
        public double BackwardationRiskMultiplier { get; set; }

        [Parameter("OPEX Pre-Window (trading days)", DefaultValue = 10, MinValue = 1, Group = "Volatility Conviction",
            Description = "Fade trades in the N trading days BEFORE monthly OPEX get the boosted multiplier below - this is the window that measured genuinely flat/pinned (near-zero drift rate) in real data.")]
        public int OpexPreWindowTradingDays { get; set; }

        [Parameter("OPEX Pre-Window Risk Multiplier", DefaultValue = 1.2, MinValue = 1.0, Group = "Volatility Conviction",
            Description = "Deliberately more conservative than the raw 11x drift-rate finding would suggest - that stat measures RATE OF DRIFT, not return magnitude, so it isn't directly a sizing number. Treat this as a modest, judgment-adjusted translation of a real but differently-shaped result, not a precise calibration.")]
        public double OpexPreWindowRiskMultiplier { get; set; }

        [Parameter("OPEX Post-Window (trading days)", DefaultValue = 5, MinValue = 1, Group = "Volatility Conviction",
            Description = "Fade trades in the N trading days AFTER monthly OPEX get the reduced multiplier below - the old pinning anchor has just released and fade reliability is specifically weaker right here.")]
        public int OpexPostWindowTradingDays { get; set; }

        [Parameter("OPEX Post-Window Risk Multiplier", DefaultValue = 0.8, MinValue = 0.1, MaxValue = 1.0, Group = "Volatility Conviction",
            Description = "Same judgment-adjusted caveat as the pre-window multiplier above applies here too.")]
        public double OpexPostWindowRiskMultiplier { get; set; }

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

        [Parameter("Require Wall Tested Before Breakout", DefaultValue = true, Group = "Strategy",
            Description = "A wall only counts as a valid breakout trigger if price previously approached it (within Entry Proximity To Wall %) and pulled back WITHOUT closing beyond it, at least once, before it finally broke (see UpdateWallRoles/WallRole - this is the 'EverHeld' flag). Off = trade any wall break immediately, including a first-touch gap straight through with no prior confirmation it was acting as a real level. On (default) is the more conservative reading of 'check if walls are support or resistance' - a never-tested wall hasn't actually demonstrated that role yet.")]
        public bool RequireWallTestedBeforeBreakout { get; set; }

        [Parameter("Wall Proximity Stop Tighten %", DefaultValue = 0.2, MinValue = 0, Group = "Strategy",
            Description = "When an open position is in profit and price comes within this % of the wall ahead of it in the favorable direction (Call Wall for longs, Put Wall for shorts), the stop is moved to breakeven (entry price) if it isn't already better than that. Set to 0 to disable. See ManageWallProximityExit.")]
        public double WallProximityStopTightenPercent { get; set; }

        [Parameter("Take Profit At Wall Touch", DefaultValue = true, Group = "Strategy",
            Description = "Closes the position outright the moment price reaches the wall ahead of it in the favorable direction (Call Wall for longs, Put Wall for shorts), regardless of the original stop/target - walls are the strongest a-priori levels this bot has, so a live touch is treated as a real target reached rather than waiting for the original R:R target/stop to eventually catch up. See ManageWallProximityExit.")]
        public bool TakeProfitAtWallTouch { get; set; }

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
        private bool _entryIsFadeTrade; // true = fade, false = breakout - see ManageWallProximityExit for why this matters
        private int _objCounter;

        private bool _standDownForBadGammaData; // monthly/weekly overlap or degenerate (call_wall == put_wall) data
        private bool _isBackwardation; // near-term ATM IV > far-term ATM IV, refreshed once per session

        // Pinned once per overnight session so a mid-session rollover in the site's own "nearest expiration"
        // resolution can't silently swap which expiration's (supposedly frozen) levels are being traded -
        // see RefreshGammaLevels(). Cleared whenever we're not in-session, so the next session re-pins fresh.
        private string _pinnedExpirationId;
        private DateTime? _currentSessionStartDate; // local calendar date the ACTIVE session started on

        // Weekly sweep is processed a couple of items per tick (see ProcessSweepQueueTick) rather than all
        // at once, so the one-per-day discovery burst can't block the cBot's thread for an extended period.
        private Queue<(string expirationId, DateTime date)> _sweepQueue = new Queue<(string, DateTime)>();
        private Dictionary<DateTime, int> _sweepDateCounts = new Dictionary<DateTime, int>();
        private const int SweepItemsPerTick = 2;

        // Informational-only extremes across the current sweep: the highest Call Wall and lowest Put Wall
        // seen among the expirations discovered this session (degenerate call==put rows excluded). NOT
        // used in any entry/exit logic - see DrawSweepExtremesLabel for the caveats on what these numbers
        // do and don't mean. _sweepTotalCount doubles as a "sweep pending" flag: >0 while a sweep is queued
        // or in progress, reset to 0 once its extremes have been drawn so ProcessSweepQueueTick doesn't
        // redraw on every subsequent idle tick.
        private double _sweepExtremeCallWall = double.NaN;
        private string _sweepExtremeCallWallExpiration;
        private double _sweepExtremePutWall = double.NaN;
        private string _sweepExtremePutWallExpiration;
        private int _sweepTotalCount;

        // Minimum cooldown after ANY position close before a new entry is allowed - guards against the
        // regime-flip exit immediately reopening a position in the opposite direction on the same or next
        // bar if price is choppy right around the Gamma Flip level (which can sit close to a wall on
        // thin-data days), which would otherwise churn repeatedly at real spread/slippage cost each time.
        private DateTime? _lastCloseUtc;
        private const int MinSecondsBetweenTrades = 300;

        // Per-wall confirmed role, updated every bar off live price action (see UpdateWallRoles) - this is
        // what actually gates fade vs breakout entries now, not the GEX regime label alone (see the
        // Strategy logic section of the file header for why). Untested = never approached. Testing = price
        // is currently within Entry Proximity To Wall % of it, outcome not yet resolved. Holding = it was
        // tested and price pulled back without closing beyond it - confirmed as real support/resistance.
        // Broken = price closed beyond it by Wall Break Buffer % - sticky for the rest of the session once
        // it fails, even if price later comes back inside. EverHeld persists independently of the current
        // role (a wall can go Holding -> later Broken - EverHeld still remembers it held at least once),
        // used by Require Wall Tested Before Breakout to distinguish a break of a proven level from a
        // straight-through break of a level that never demonstrated it mattered.
        private enum WallRole { Untested, Testing, Holding, Broken }
        private WallRole _callWallRole = WallRole.Untested;
        private WallRole _putWallRole = WallRole.Untested;
        private bool _callWallEverHeld;
        private bool _putWallEverHeld;

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
                // cAlgo doesn't persist custom fields on a Position, so a recovered position's fade/breakout
                // origin is unknown here - _entryIsFadeTrade stays false (its default), which conservatively
                // means ManageWallProximityExit skips wall-touch management for it rather than guessing.
                Print("Recovered an existing open position on start: " + _openPosition.TradeType);
            }

            Positions.Closed += OnPositionsClosed;

            CheckSessionTransition(); // pins the expiration + runs the daily sweep/IV setup if we're starting mid-session
            RefreshGammaLevels(); // get an initial read before waiting for the first timer tick (also a no-op-safe re-fetch if the line above already did one)
            Timer.Start(TimeSpan.FromMinutes(GammaRefreshMinutes));
        }

        protected override void OnStop()
        {
            if (_execBars != null) _execBars.BarOpened -= OnExecBarOpened;
            Positions.Closed -= OnPositionsClosed;
            Timer.Stop();
            _http.Dispose();
        }

        // The Timer always ticks at GammaRefreshMinutes (the fast/market-open cadence) - during the Market
        // Closed Window, most ticks are skipped so the EFFECTIVE cadence slows to GammaOvernightRefreshMinutes,
        // without needing a second Timer.
        protected override void OnTimer()
        {
            CheckSessionTransition(); // detects a new session starting, pins the expiration, kicks off daily setup

            bool marketClosed = IsMarketClosedWindow(Server.TimeInUtc);
            double minutesSinceLastFetch = _gamma.FetchedUtc == DateTime.MinValue
                ? double.MaxValue
                : (Server.TimeInUtc - _gamma.FetchedUtc).TotalMinutes;

            bool dueForRefresh = marketClosed
                ? minutesSinceLastFetch >= GammaOvernightRefreshMinutes
                : true; // Timer's own tick rate already IS the market-open cadence

            if (dueForRefresh) RefreshGammaLevels();

            ProcessSweepQueueTick(); // pops a couple of pending sweep items, if any - see the field comment
        }

        private DateTime GetSessionStartDate(DateTime localTime)
        {
            int nowMinutes = localTime.Hour * 60 + localTime.Minute;
            int startMinutes = SessionStartHour * 60 + SessionStartMinute;
            // On/after session start -> today IS the start date. Before it -> we're in the early-morning
            // tail of a session that started yesterday (only meaningful to call this while inSession==true).
            return nowMinutes >= startMinutes ? localTime.Date : localTime.Date.AddDays(-1);
        }

        // Runs ONCE per overnight session, the moment it begins - pins the expiration (see the
        // _pinnedExpirationId field comment) and kicks off the once-per-day sweep/IV-term-structure setup.
        // Replaces the old calendar-midnight-triggered daily checks, which fired in the middle of a session
        // that actually starts in the evening, not at midnight - this fires right at session start instead,
        // so the conviction signals are fresh for the WHOLE session rather than already stale for its
        // first several hours.
        private void CheckSessionTransition()
        {
            var (inSession, localTime) = GetSessionState(Server.TimeInUtc);
            if (!inSession)
            {
                _currentSessionStartDate = null;
                _pinnedExpirationId = null; // release the pin so the next session starts fresh
                return;
            }

            DateTime sessionStartDate = GetSessionStartDate(localTime);
            if (_currentSessionStartDate != null && _currentSessionStartDate.Value == sessionStartDate)
                return; // already set up for this session

            _currentSessionStartDate = sessionStartDate;
            Print($"[SESSION] New overnight session starting ({sessionStartDate:yyyy-MM-dd} local) - pinning gamma expiration and refreshing daily conviction signals.");

            if (RunWeeklySweep) BuildSweepQueue();
            if (UseVolatilityConviction) RefreshVolatilityTermStructure();
        }

        private bool IsMarketClosedWindow(DateTime timeUtc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc), _sessionTz);
            int nowMinutes = local.Hour * 60 + local.Minute;
            int startMinutes = MarketClosedStartHour * 60 + MarketClosedStartMinute;
            int endMinutes = MarketClosedEndHour * 60 + MarketClosedEndMinute;

            // Wraps past midnight (start > end), e.g. 21:00 -> 13:25 next day.
            return startMinutes > endMinutes
                ? (nowMinutes >= startMinutes || nowMinutes < endMinutes)
                : (nowMinutes >= startMinutes && nowMinutes < endMinutes);
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
        // Two fetches, deliberately pinned to the SAME expiration: (1) the gamma_exposure endpoint for
        // Call Wall / Put Wall plus the expiration's id/timestamp, (2) the raw option_chain endpoint
        // (bid/ask/OI/volume per contract) needed to compute Gamma Flip properly - see ComputeGammaFlip.
        private void RefreshGammaLevels()
        {
            try
            {
                // Once a session has pinned an expiration, ALWAYS use that exact one, ignoring whatever
                // the site would otherwise resolve as "nearest" - see the _pinnedExpirationId field
                // comment for why this matters. An explicit user override always wins outright.
                string effectiveOverride = !string.IsNullOrWhiteSpace(GammaExpirationOverride)
                    ? GammaExpirationOverride
                    : _pinnedExpirationId;

                string gexHtml = _http.GetStringAsync(BuildGammaExposureUrl(effectiveOverride)).Result;
                var (callWall, putWall, expirationId, expirationUnix) = ParseGammaExposureHtml(gexHtml);
                if (double.IsNaN(callWall) || double.IsNaN(putWall) || expirationId == null)
                {
                    Print("Gamma fetch returned incomplete/unparseable wall data (site layout may have changed) - keeping previous levels.");
                    return;
                }

                bool inSession = GetSessionState(Server.TimeInUtc).inSession;
                bool isNewPinThisSession = inSession && _pinnedExpirationId == null;
                if (isNewPinThisSession)
                {
                    _pinnedExpirationId = expirationId;
                    Print($"[GAMMA] Pinned this session to expiration '{expirationId}' - will keep using this exact expiration for the rest of the overnight session, regardless of what the site later resolves as 'nearest'.");
                }

                // The overlap/degenerate-data check makes its own extra HTTP call and only needs to run
                // ONCE per session (the answer is a fixed fact about today's date, not something that
                // changes on re-fetch) - gated to the pin moment rather than every refresh.
                if (isNewPinThisSession && SkipMonthlyOverlapDays && IsUnreliableGammaDay(callWall, putWall, expirationId))
                {
                    _standDownForBadGammaData = true;
                    return; // reason already logged by IsUnreliableGammaDay
                }
                if (isNewPinThisSession)
                {
                    _standDownForBadGammaData = false;

                    // Fresh session, fresh (frozen) levels - a wall's role earned against yesterday's
                    // levels says nothing about today's, so reset all of it here rather than carrying it
                    // forward.
                    _callWallRole = WallRole.Untested;
                    _putWallRole = WallRole.Untested;
                    _callWallEverHeld = false;
                    _putWallEverHeld = false;
                }

                string chainHtml = _http.GetStringAsync(BuildOptionChainUrl(expirationId)).Result;
                var (spot, legs) = ParseOptionChainHtml(chainHtml);
                if (double.IsNaN(spot) || legs.Count == 0)
                {
                    Print("Option chain fetch returned no usable contracts - keeping previous levels.");
                    return;
                }

                double nowUnix = (Server.TimeInUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double gammaFlip = ComputeGammaFlip(legs, spot, expirationUnix, nowUnix, out int contractsUsed);

                var levels = new GammaLevels { CallWall = callWall, PutWall = putWall, GammaFlip = gammaFlip };
                if (levels.IsValid)
                {
                    levels.FetchedUtc = Server.TimeInUtc;
                    _gamma = levels;
                    DrawGammaLines();
                    Print($"[GAMMA] Refreshed: CallWall={_gamma.CallWall:F2} PutWall={_gamma.PutWall:F2} GammaFlip={_gamma.GammaFlip:F2} (spot={spot:F2}, {contractsUsed}/{legs.Count} contracts used)");
                }
                else
                {
                    Print($"Gamma Flip could not be computed from the option chain (no zero-gamma crossing found near spot={spot:F2}) - keeping previous levels.");
                }
            }
            catch (Exception ex)
            {
                Print($"Gamma fetch failed ({ex.Message}) - keeping previous levels until next refresh.");
            }
        }

        // Detects the two real, confirmed data-quality problems: (1) a degenerate wall read (call wall ==
        // put wall - a strong sign of insufficient real spread in the data), and (2) today's date having
        // BOTH a weekly (':w') and monthly (':m') expiration listed, whose wall data was confirmed to
        // differ meaningfully and both come back individually thin. Either one sets the stand-down flag.
        private bool IsUnreliableGammaDay(double callWall, double putWall, string expirationId)
        {
            if (callWall == putWall)
            {
                Print($"[GAMMA] Standing down - Call Wall and Put Wall resolved to the identical strike ({callWall:F2}), a strong sign of insufficient real data spread for '{expirationId}'.");
                return true;
            }

            int colonIdx = expirationId.IndexOf(':');
            if (colonIdx < 0) return false;
            string datePart = expirationId.Substring(0, colonIdx);
            string suffix = expirationId.Substring(colonIdx + 1);
            string otherSuffix = suffix == "w" ? "m" : suffix == "m" ? "w" : null;
            if (otherSuffix == null) return false; // unrecognized suffix format - don't guess, just proceed normally

            try
            {
                string otherId = $"{datePart}:{otherSuffix}";
                string otherHtml = _http.GetStringAsync(BuildGammaExposureUrl(otherId)).Result;
                var (otherCallWall, otherPutWall, otherExpirationId, _) = ParseGammaExposureHtml(otherHtml);

                if (!double.IsNaN(otherCallWall) && !double.IsNaN(otherPutWall) && otherExpirationId == otherId)
                {
                    Print($"[GAMMA] Standing down - '{expirationId}' AND '{otherId}' both list valid, DIFFERENT wall data for the same date " +
                          $"({expirationId}: {callWall:F2}/{putWall:F2} vs {otherId}: {otherCallWall:F2}/{otherPutWall:F2}) - no validated way to reconcile two series into one trustworthy level.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Print($"[GAMMA] Overlap-day check failed ({ex.Message}) - proceeding without it this refresh.");
            }

            return false;
        }

        private string BuildGammaExposureUrl(string expirationOverride)
        {
            string url = $"{OptionChartsBaseUrl.TrimEnd('/')}/async/options_charts/gamma_exposure" +
                          $"?option_type=all&strike_range=all&ticker={Uri.EscapeDataString(GammaTickerSymbol)}" +
                          $"&gamma_exposure_type={Uri.EscapeDataString(GammaExposureBasis)}";
            if (!string.IsNullOrWhiteSpace(expirationOverride))
                url += $"&expiration_dates={Uri.EscapeDataString(expirationOverride)}";
            return url;
        }

        private string BuildOptionChainUrl(string expirationId)
        {
            // Pinned to the SAME expiration the wall data came from, so both halves of this refresh are
            // guaranteed to describe the same option chain, not two independently-defaulted "nearest" picks.
            return $"{OptionChartsBaseUrl.TrimEnd('/')}/async/option_chain" +
                   $"?option_type=all&strike_range=all&ticker={Uri.EscapeDataString(GammaTickerSymbol)}" +
                   $"&expiration_dates={Uri.EscapeDataString(expirationId)}";
        }

        // Hand-rolled extraction rather than a JSON library dependency (matching this codebase's existing
        // pattern), since the response is an HTML fragment with the actual data sitting in an inline JS
        // variable assignment, not a clean top-level JSON document.
        private (double callWall, double putWall, string expirationId, double expirationUnix) ParseGammaExposureHtml(string html)
        {
            var m = Regex.Match(html,
                "\"net_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"expiration_date_display\":\"[^\"]*\",\"expiration_int\":([0-9]+),\"call_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"put_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"call_wall\":(-?[0-9]+(?:\\.[0-9]+)?),\"put_wall\":(-?[0-9]+(?:\\.[0-9]+)?),\"gamma_zero_level\":(?:null|-?[0-9.]+),\"expiration_date_id\":\"([^\"]+)\"");
            if (!m.Success) return (double.NaN, double.NaN, null, double.NaN);

            TryParseInvariant(m.Groups[2].Value, out double expirationUnix);
            TryParseInvariant(m.Groups[5].Value, out double callWall);
            TryParseInvariant(m.Groups[6].Value, out double putWall);
            string expirationId = m.Groups[7].Value;

            return (callWall, putWall, expirationId, expirationUnix);
        }

        private class ChainLeg
        {
            public double Strike;
            public bool IsCall;
            public double Bid;
            public double Ask;
            public double OpenInterest;
            public double Volume;
        }

        // Parses the raw option chain table via its per-contract detail links
        // (/option/contract/{OCC-STYLE-SYMBOL}), grouping every 5 consecutive same-symbol matches into
        // [last, bid, ask, volume, oi] - the fixed column order per leg - rather than trying to match the
        // surrounding <tr>/<td> HTML structure directly, which is far more brittle to whitespace/markup
        // changes. The OCC-style symbol itself (e.g. NDXP260817C30000000) encodes strike and type, so no
        // separate strike column needs parsing either.
        private (double spot, List<ChainLeg> legs) ParseOptionChainHtml(string html)
        {
            var legs = new List<ChainLeg>();

            var spotMatch = Regex.Match(html, "underlyingPrice:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!spotMatch.Success || !TryParseInvariant(spotMatch.Groups[1].Value, out double spot))
                return (double.NaN, legs);

            var cellRegex = new Regex("<a href=\"/option/contract/([A-Za-z]+[0-9]{6}[CP][0-9]{8})\"[^>]*>([^<]*)</a>");
            var symbolRegex = new Regex("^([A-Za-z]+)([0-9]{6})([CP])([0-9]{8})$");

            string curSymbol = null;
            var buffer = new List<string>();

            void Flush()
            {
                if (curSymbol == null || buffer.Count < 5) return;
                var sm = symbolRegex.Match(curSymbol);
                if (!sm.Success) return;

                bool isCall = sm.Groups[3].Value == "C";
                if (!TryParseInvariant(sm.Groups[4].Value, out double strikeRaw)) return;
                double strike = strikeRaw / 1000.0;

                if (!TryParseInvariant(buffer[1].Replace(",", ""), out double bid)) return;
                if (!TryParseInvariant(buffer[2].Replace(",", ""), out double ask)) return;
                if (bid <= 0 || ask <= 0) return; // "-" or blank quote, nothing usable

                TryParseInvariant(buffer[3].Replace(",", ""), out double volume);
                TryParseInvariant(buffer[4].Replace(",", ""), out double oi);

                legs.Add(new ChainLeg { Strike = strike, IsCall = isCall, Bid = bid, Ask = ask, Volume = volume, OpenInterest = oi });
            }

            foreach (Match m in cellRegex.Matches(html))
            {
                string sym = m.Groups[1].Value;
                if (sym != curSymbol)
                {
                    Flush();
                    curSymbol = sym;
                    buffer = new List<string>();
                }
                buffer.Add(m.Groups[2].Value);
            }
            Flush();

            return (spot, legs);
        }

        // ---------------------------------------------------------------------------------------------
        // Gamma Flip - real Black-Scholes zero-gamma calculation (not a proxy)
        // ---------------------------------------------------------------------------------------------
        //
        // The correct definition of "gamma flip" / "zero gamma level" is the hypothetical underlying
        // price at which TOTAL dealer-facing gamma exposure, re-priced across the whole option chain,
        // crosses zero - NOT any aggregation of exposure already attributed to strikes at the CURRENT
        // price (an earlier version of this file did exactly that, produced an implausible result sitting
        // oddly between the two walls, and was wrong for that reason - this replaces it entirely).
        //
        // Method: invert Black-Scholes on each contract's mid price (using real bid/ask from the option
        // chain) to get its implied vol, then hold that vol fixed per contract while re-pricing Gamma
        // across a grid of hypothetical spot prices, weighting each contract by Volume or Open Interest
        // (matching Gamma Exposure Basis) and signing calls positive / puts negative (matching the
        // convention observed in optioncharts.io's own call_exposure/put_exposure fields). The flip is
        // the zero-crossing of that curve nearest the current spot - the standard convention when (as is
        // common with real, noisy chain data) more than one crossing exists.
        private double ComputeGammaFlip(List<ChainLeg> legs, double spot, double expirationUnix, double nowUnix, out int contractsUsed)
        {
            contractsUsed = 0;
            double T = (expirationUnix - nowUnix) / (365.25 * 86400.0);
            if (T <= 0) return double.NaN;

            bool useVolume = GammaExposureBasis.Equals("volume", StringComparison.OrdinalIgnoreCase);
            double rangeAbs = spot * IvStrikeRangePercent / 100.0;

            var priced = new List<(double strike, bool isCall, double sigma, double weight)>();
            foreach (var leg in legs)
            {
                if (Math.Abs(leg.Strike - spot) > rangeAbs) continue;
                double weight = useVolume ? leg.Volume : leg.OpenInterest;
                if (weight <= 0) continue;

                double mid = (leg.Bid + leg.Ask) / 2.0;
                double sigma = ImpliedVol(mid, spot, leg.Strike, RiskFreeRate, DividendYield, T, leg.IsCall);
                if (double.IsNaN(sigma) || sigma <= 0.001 || sigma >= 4.9) continue;

                priced.Add((leg.Strike, leg.IsCall, sigma, weight));
            }

            contractsUsed = priced.Count;
            if (priced.Count < 2) return double.NaN;

            const int steps = 300;
            double lowS = spot * 0.85;
            double highS = spot * 1.15;

            double GexAt(double s)
            {
                double total = 0;
                foreach (var c in priced)
                {
                    double g = BsGamma(s, c.strike, RiskFreeRate, DividendYield, c.sigma, T);
                    double sign = c.isCall ? 1.0 : -1.0;
                    total += g * c.weight * ContractMultiplier * s * s * 0.01 * sign;
                }
                return total;
            }

            double prevS = lowS;
            double prevG = GexAt(lowS);
            double bestFlip = double.NaN;
            double bestDist = double.MaxValue;

            for (int i = 1; i <= steps; i++)
            {
                double s = lowS + (highS - lowS) * i / steps;
                double g = GexAt(s);
                if ((prevG < 0) != (g < 0))
                {
                    double frac = -prevG / (g - prevG);
                    double crossing = prevS + frac * (s - prevS);
                    double dist = Math.Abs(crossing - spot);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestFlip = crossing;
                    }
                }
                prevS = s;
                prevG = g;
            }

            return bestFlip;
        }

        // ---------------------------------------------------------------------------------------------
        // Concentration Price + weekly sweep (informational only - not used in entry/exit logic)
        // ---------------------------------------------------------------------------------------------

        // The single strike with the largest COMBINED call+put exposure that day - same underlying
        // per-strike data Call Wall (max call exposure) / Put Wall (max |put exposure|) are each already
        // the peak of, just considering both sides together instead of separately. Reuses the
        // gamma_exposure response already fetched for the walls - no extra HTTP call needed.
        private (double strike, double weight) ComputeConcentrationStrike(string gexHtml)
        {
            var strikeMatches = Regex.Matches(gexHtml,
                "\\{\"strike\":(-?[0-9]+(?:\\.[0-9]+)?),\"call_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"put_exposure\":(-?[0-9]+(?:\\.[0-9]+)?),\"net_exposure\":(-?[0-9]+(?:\\.[0-9]+)?)\\}");

            double bestStrike = double.NaN;
            double bestMag = -1;
            foreach (Match m in strikeMatches)
            {
                if (!TryParseInvariant(m.Groups[1].Value, out double strike)) continue;
                if (!TryParseInvariant(m.Groups[2].Value, out double callExp)) continue;
                if (!TryParseInvariant(m.Groups[3].Value, out double putExp)) continue;

                double mag = Math.Abs(callExp) + Math.Abs(putExp);
                if (mag > bestMag)
                {
                    bestMag = mag;
                    bestStrike = strike;
                }
            }
            return (bestStrike, bestMag);
        }

        // Reads the expiration dropdown off the option chain page (no expiration override, so it lists
        // everything available) to find what's actually listed for the next N calendar days, rather than
        // guessing weekday dates - avoids assuming a fixed Mon-Fri cadence that may not hold for every
        // ticker/period. Returns one entry per listed (date, suffix) pair, so a monthly-overlap date
        // legitimately produces two entries here.
        private List<(string expirationId, DateTime date)> DiscoverUpcomingExpirations(int maxDays)
        {
            var result = new List<(string expirationId, DateTime date)>();
            try
            {
                string url = $"{OptionChartsBaseUrl.TrimEnd('/')}/async/option_chain?option_type=all&strike_range=all&ticker={Uri.EscapeDataString(GammaTickerSymbol)}";
                string html = _http.GetStringAsync(url).Result;

                var matches = Regex.Matches(html, "value=\"([0-9]{4}-[0-9]{2}-[0-9]{2}):([a-z])\"");
                DateTime today = Server.TimeInUtc.Date;
                DateTime cutoff = today.AddDays(maxDays);

                foreach (Match m in matches)
                {
                    if (!DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out DateTime date)) continue;
                    if (date < today || date > cutoff) continue;
                    result.Add(($"{m.Groups[1].Value}:{m.Groups[2].Value}", date));
                }
                result.Sort((a, b) => a.date.CompareTo(b.date));
            }
            catch (Exception ex)
            {
                Print($"[SWEEP] Failed to discover upcoming expirations ({ex.Message}).");
            }
            return result;
        }

        // Runs once per session (triggered from CheckSessionTransition), but only DISCOVERS the list and
        // queues it - see ProcessSweepQueueTick for why the actual fetching is spread across ticks instead
        // of done here in one blocking burst.
        private void BuildSweepQueue()
        {
            var expirations = DiscoverUpcomingExpirations(WeeklySweepMaxDays);
            _sweepQueue = new Queue<(string, DateTime)>(expirations);

            // Track which calendar dates have more than one listed expiration (the monthly-overlap case),
            // purely for a clearer log annotation - doesn't change what gets fetched/computed.
            _sweepDateCounts = new Dictionary<DateTime, int>();
            foreach (var e in expirations)
                _sweepDateCounts[e.date] = _sweepDateCounts.TryGetValue(e.date, out int c) ? c + 1 : 1;

            // Fresh sweep, fresh extremes - see the field comment.
            _sweepExtremeCallWall = double.NaN;
            _sweepExtremeCallWallExpiration = null;
            _sweepExtremePutWall = double.NaN;
            _sweepExtremePutWallExpiration = null;
            _sweepTotalCount = expirations.Count;

            if (expirations.Count > 0)
                Print($"[SWEEP] === Weekly gamma sweep queued: {expirations.Count} expiration(s) over the next {WeeklySweepMaxDays} days, processing {SweepItemsPerTick}/tick to avoid a long blocking burst ===");
            else
                Print("[SWEEP] No upcoming expirations discovered - skipping this sweep.");
        }

        // Pops a few pending sweep items off the queue each tick (called from OnTimer) instead of walking
        // the whole list in one shot, which could otherwise mean 20-30+ sequential blocking HTTP calls in a
        // single OnTimer invocation - a real risk of stalling the cBot's thread for an extended period.
        private void ProcessSweepQueueTick()
        {
            for (int i = 0; i < SweepItemsPerTick && _sweepQueue.Count > 0; i++)
            {
                var (expirationId, date) = _sweepQueue.Dequeue();
                SweepOneExpiration(expirationId, date);
            }

            // Sweep just drained - draw the extremes label once, not on every idle tick afterward.
            if (_sweepQueue.Count == 0 && _sweepTotalCount > 0)
            {
                DrawSweepExtremesLabel();
                _sweepTotalCount = 0;
            }
        }

        // Logs Call Wall / Put Wall / Gamma Flip / Concentration Price for one expiration - purely
        // informational, does not feed into any trading decision.
        private void SweepOneExpiration(string expirationId, DateTime date)
        {
            try
            {
                string gexHtml = _http.GetStringAsync(BuildGammaExposureUrl(expirationId)).Result;
                var (callWall, putWall, resolvedId, expirationUnix) = ParseGammaExposureHtml(gexHtml);
                if (double.IsNaN(callWall) || double.IsNaN(putWall))
                {
                    Print($"[SWEEP] {expirationId}: no usable wall data.");
                    return;
                }

                var (concStrike, concWeight) = ComputeConcentrationStrike(gexHtml);

                string chainHtml = _http.GetStringAsync(BuildOptionChainUrl(expirationId)).Result;
                var (spot, legs) = ParseOptionChainHtml(chainHtml);

                string flipStr = "n/a";
                string contractsStr = "";
                if (!double.IsNaN(spot) && legs.Count > 0)
                {
                    double nowUnix = (Server.TimeInUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    double flip = ComputeGammaFlip(legs, spot, expirationUnix, nowUnix, out int contractsUsed);
                    flipStr = double.IsNaN(flip) ? "n/a" : flip.ToString("F2");
                    contractsStr = $", {contractsUsed}/{legs.Count} contracts";
                }

                string overlapNote = _sweepDateCounts.TryGetValue(date, out int cnt) && cnt > 1
                    ? " [MONTHLY/WEEKLY OVERLAP DATE - two series listed, treat both with caution]"
                    : "";
                bool degenerate = callWall == putWall;
                string degenerateNote = degenerate ? " [DEGENERATE - walls identical, low confidence]" : "";

                Print($"[SWEEP] {date:yyyy-MM-dd} ({expirationId}): CallWall={callWall:F2} PutWall={putWall:F2} GammaFlip={flipStr} ConcentrationPrice={concStrike:F2}{contractsStr}{overlapNote}{degenerateNote}");

                // Feed the informational extremes tracker - degenerate rows excluded, same low-data-quality
                // signal used elsewhere in this file. No other filtering (e.g. by distance from spot) is
                // applied - see DrawSweepExtremesLabel for why that's shown as a caveat instead of a filter.
                if (!degenerate)
                {
                    if (double.IsNaN(_sweepExtremeCallWall) || callWall > _sweepExtremeCallWall)
                    {
                        _sweepExtremeCallWall = callWall;
                        _sweepExtremeCallWallExpiration = expirationId;
                    }
                    if (double.IsNaN(_sweepExtremePutWall) || putWall < _sweepExtremePutWall)
                    {
                        _sweepExtremePutWall = putWall;
                        _sweepExtremePutWallExpiration = expirationId;
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"[SWEEP] {expirationId}: fetch/compute failed ({ex.Message}).");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Volatility Conviction - IV term structure (checked once per day, see OnTimer)
        // ---------------------------------------------------------------------------------------------

        // Average implied vol of the few contracts nearest the money - a simple, robust ATM IV proxy.
        // Reuses the same ImpliedVol() inversion already validated for Gamma Flip (rather than trusting
        // optioncharts' own published IV numbers, which were confirmed elsewhere in this file's history to
        // include unstable/illiquid quotes that self-inverted IV naturally filters out via non-convergence).
        private double GetAtmImpliedVol(List<ChainLeg> legs, double spot, double expirationUnix, double nowUnix)
        {
            double T = (expirationUnix - nowUnix) / (365.25 * 86400.0);
            if (T <= 0) return double.NaN;

            var candidates = legs
                .Select(l => new { l, dist = Math.Abs(l.Strike - spot) })
                .OrderBy(x => x.dist)
                .Take(6)
                .Select(x => x.l);

            var ivs = new List<double>();
            foreach (var leg in candidates)
            {
                double mid = (leg.Bid + leg.Ask) / 2.0;
                double sigma = ImpliedVol(mid, spot, leg.Strike, RiskFreeRate, DividendYield, T, leg.IsCall);
                if (!double.IsNaN(sigma) && sigma > 0.001 && sigma < 4.9) ivs.Add(sigma);
            }
            return ivs.Count > 0 ? ivs.Average() : double.NaN;
        }

        // Runs once per session (see CheckSessionTransition). Compares near-term ATM IV (the SAME pinned
        // expiration RefreshGammaLevels trades off, when a pin exists) against a far-term expiration near
        // Far-Term Expiration Target days out, picked from whatever's actually listed rather than assumed.
        private void RefreshVolatilityTermStructure()
        {
            try
            {
                string nearOverride = !string.IsNullOrWhiteSpace(GammaExpirationOverride) ? GammaExpirationOverride : _pinnedExpirationId;
                string nearGexHtml = _http.GetStringAsync(BuildGammaExposureUrl(nearOverride)).Result;
                var (_, _, nearExpirationId, nearExpirationUnix) = ParseGammaExposureHtml(nearGexHtml);
                if (nearExpirationId == null) { Print("[IV] Could not resolve near-term expiration - skipping term structure check."); return; }

                string nearChainHtml = _http.GetStringAsync(BuildOptionChainUrl(nearExpirationId)).Result;
                var (nearSpot, nearLegs) = ParseOptionChainHtml(nearChainHtml);
                if (double.IsNaN(nearSpot) || nearLegs.Count == 0) { Print("[IV] Near-term chain had no usable contracts - skipping term structure check."); return; }

                double nowUnix = (Server.TimeInUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double nearIv = GetAtmImpliedVol(nearLegs, nearSpot, nearExpirationUnix, nowUnix);

                var upcoming = DiscoverUpcomingExpirations(FarTermExpirationDaysTarget + 15);
                if (upcoming.Count == 0) { Print("[IV] No upcoming expirations discovered for far-term comparison - skipping."); return; }

                DateTime today = Server.TimeInUtc.Date;
                var farPick = upcoming.OrderBy(e => Math.Abs((e.date - today).Days - FarTermExpirationDaysTarget)).First();

                string farGexHtml = _http.GetStringAsync(BuildGammaExposureUrl(farPick.expirationId)).Result;
                var (_, _, farExpirationId, farExpirationUnix) = ParseGammaExposureHtml(farGexHtml);
                if (farExpirationId == null) { Print($"[IV] Could not resolve far-term expiration '{farPick.expirationId}' - skipping."); return; }

                string farChainHtml = _http.GetStringAsync(BuildOptionChainUrl(farExpirationId)).Result;
                var (farSpot, farLegs) = ParseOptionChainHtml(farChainHtml);
                if (double.IsNaN(farSpot) || farLegs.Count == 0) { Print("[IV] Far-term chain had no usable contracts - skipping."); return; }

                double farIv = GetAtmImpliedVol(farLegs, farSpot, farExpirationUnix, nowUnix);

                if (double.IsNaN(nearIv) || double.IsNaN(farIv))
                {
                    Print("[IV] Could not compute a usable ATM IV on one or both sides - leaving previous backwardation state unchanged.");
                    return;
                }

                _isBackwardation = nearIv > farIv;
                Print($"[IV] Term structure: near({nearExpirationId})={nearIv * 100:F2}% far({farExpirationId}, ~{(farPick.date - today).Days}d)={farIv * 100:F2}% -> {(_isBackwardation ? "BACKWARDATION (elevated near-term risk)" : "normal/contango")}");
            }
            catch (Exception ex)
            {
                Print($"[IV] Term structure check failed ({ex.Message}) - leaving previous backwardation state unchanged.");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Volatility Conviction - monthly OPEX cycle (pure date math, no fetch needed)
        // ---------------------------------------------------------------------------------------------

        private static DateTime ThirdFridayOfMonth(int year, int month)
        {
            DateTime d = new DateTime(year, month, 1);
            int fridaysSeen = 0;
            while (true)
            {
                if (d.DayOfWeek == DayOfWeek.Friday)
                {
                    fridaysSeen++;
                    if (fridaysSeen == 3) return d;
                }
                d = d.AddDays(1);
            }
        }

        // Fade-trade-only multiplier: boosted in the real, measured pinned window before monthly OPEX,
        // reduced in the real, measured just-released window after it, neutral otherwise. Confirmed this
        // does NOT generalize to weekly (Friday) expirations at 4x the sample size, so deliberately scoped
        // to monthly (3rd Friday) OPEX only - see the header comment above the parameter group.
        private double GetOpexFadeConvictionMultiplier(DateTime localDate)
        {
            DateTime thisMonthOpex = ThirdFridayOfMonth(localDate.Year, localDate.Month);
            DateTime lastMonth = localDate.AddMonths(-1);
            DateTime lastMonthOpex = ThirdFridayOfMonth(lastMonth.Year, lastMonth.Month);
            DateTime nextMonth = localDate.AddMonths(1);
            DateTime nextMonthOpex = ThirdFridayOfMonth(nextMonth.Year, nextMonth.Month);

            DateTime mostRecentOpex = localDate >= thisMonthOpex ? thisMonthOpex : lastMonthOpex;
            DateTime upcomingOpex = localDate < thisMonthOpex ? thisMonthOpex : nextMonthOpex;

            int tradingDaysSincePastOpex = CountTradingDays(mostRecentOpex, localDate);
            int tradingDaysUntilNextOpex = CountTradingDays(localDate, upcomingOpex);

            if (tradingDaysSincePastOpex >= 0 && tradingDaysSincePastOpex <= OpexPostWindowTradingDays)
                return OpexPostWindowRiskMultiplier;
            if (tradingDaysUntilNextOpex >= 0 && tradingDaysUntilNextOpex <= OpexPreWindowTradingDays)
                return OpexPreWindowRiskMultiplier;
            return 1.0;
        }

        private static int CountTradingDays(DateTime from, DateTime to)
        {
            int count = 0;
            for (DateTime d = from.Date; d < to.Date; d = d.AddDays(1))
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) count++;
            return count;
        }

        // Combined risk-% multiplier: backwardation applies to both trade types, the OPEX cycle multiplier
        // applies to fade trades only (see the parameter group header for why), and the off-regime haircut
        // applies whenever a trade is firing outside its natural regime (see the Strategy logic header).
        private double GetConvictionRiskMultiplier(bool isFadeTrade, bool isOffRegime, DateTime localDate)
        {
            if (!UseVolatilityConviction) return 1.0;

            double multiplier = _isBackwardation ? BackwardationRiskMultiplier : 1.0;
            if (isFadeTrade)
                multiplier *= GetOpexFadeConvictionMultiplier(localDate);
            if (isOffRegime)
                multiplier *= OffRegimeRiskMultiplier;
            return multiplier;
        }

        private static double NormalCdf(double x)
        {
            double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
            double d = 0.3989422804014327 * Math.Exp(-x * x / 2.0);
            double prob = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
            return x >= 0 ? 1.0 - prob : prob;
        }

        private static double NormalPdf(double x) => 0.3989422804014327 * Math.Exp(-x * x / 2.0);

        private static double BsPrice(double S, double K, double r, double q, double sigma, double T, bool isCall)
        {
            if (sigma <= 0 || T <= 0) return isCall ? Math.Max(S - K, 0) : Math.Max(K - S, 0);
            double d1 = (Math.Log(S / K) + (r - q + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
            double d2 = d1 - sigma * Math.Sqrt(T);
            return isCall
                ? S * Math.Exp(-q * T) * NormalCdf(d1) - K * Math.Exp(-r * T) * NormalCdf(d2)
                : K * Math.Exp(-r * T) * NormalCdf(-d2) - S * Math.Exp(-q * T) * NormalCdf(-d1);
        }

        private static double BsVega(double S, double K, double r, double q, double sigma, double T)
        {
            if (sigma <= 0 || T <= 0) return 0;
            double d1 = (Math.Log(S / K) + (r - q + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
            return S * Math.Exp(-q * T) * NormalPdf(d1) * Math.Sqrt(T);
        }

        private static double BsGamma(double S, double K, double r, double q, double sigma, double T)
        {
            if (sigma <= 0 || T <= 0) return 0;
            double d1 = (Math.Log(S / K) + (r - q + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
            return Math.Exp(-q * T) * NormalPdf(d1) / (S * sigma * Math.Sqrt(T));
        }

        // Newton-Raphson vol inversion from a market mid price - starts from a flat 30% guess (reasonable
        // for index options) and bails out (returns NaN) rather than risk a garbage value if it doesn't
        // converge cleanly, since a bad implied vol would silently corrupt the gamma-flip calc.
        private static double ImpliedVol(double marketPrice, double S, double K, double r, double q, double T, bool isCall)
        {
            if (marketPrice <= 0 || T <= 0) return double.NaN;

            double sigma = 0.3;
            for (int i = 0; i < 50; i++)
            {
                double price = BsPrice(S, K, r, q, sigma, T, isCall);
                double vega = BsVega(S, K, r, q, sigma, T);
                if (vega < 1e-8) return double.NaN;

                double diff = price - marketPrice;
                if (Math.Abs(diff) < 1e-4) return sigma;

                sigma -= diff / vega;
                if (sigma <= 0.001) sigma = 0.001;
                if (sigma > 5.0) sigma = 5.0;
            }
            return double.NaN; // didn't converge within 50 iterations
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
        // Wall role tracking - support/resistance confirmation, independent of GEX regime
        // ---------------------------------------------------------------------------------------------

        private void UpdateWallRoles(Bar xBar)
        {
            UpdateOneWallRole(ref _callWallRole, ref _callWallEverHeld, xBar.Close, _gamma.CallWall, isCallWall: true);
            UpdateOneWallRole(ref _putWallRole, ref _putWallEverHeld, xBar.Close, _gamma.PutWall, isCallWall: false);
        }

        // A wall being a "call wall" or "put wall" only describes where dealer gamma exposure peaks - it
        // says nothing on its own about whether price will actually respect it. This turns live price
        // action against the wall into an explicit, checkable role: Testing -> Holding (confirmed as real
        // support/resistance) or Broken (confirmed as failed) - see the WallRole field comment for the full
        // state machine.
        private void UpdateOneWallRole(ref WallRole role, ref bool everHeld, double close, double wall, bool isCallWall)
        {
            if (double.IsNaN(wall) || wall <= 0) return;

            bool broke = isCallWall
                ? close > wall * (1 + WallBreakBufferPercent / 100.0)
                : close < wall * (1 - WallBreakBufferPercent / 100.0);
            if (broke)
            {
                if (role != WallRole.Broken)
                    Print($"[WALL] {(isCallWall ? "Call" : "Put")} Wall {wall:F2} BROKE (close {close:F2}, everHeld={everHeld}).");
                role = WallRole.Broken;
                return;
            }

            if (role == WallRole.Broken) return; // sticky for the rest of the session once it fails

            double distPct = Math.Abs(close - wall) / wall * 100.0;
            if (distPct <= EntryProximityToWallPercent)
            {
                role = WallRole.Testing;
            }
            else if (role == WallRole.Testing)
            {
                // Was testing, pulled back away without ever closing beyond it - confirmed holding.
                role = WallRole.Holding;
                everHeld = true;
                Print($"[WALL] {(isCallWall ? "Call" : "Put")} Wall {wall:F2} HELD (tested then pulled back, close {close:F2}).");
            }
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

            if (_standDownForBadGammaData)
            {
                if (EnableDebugLogging) Print("[DEBUG] Standing aside - unreliable gamma data for today (see [GAMMA] log line above for why)");
                return;
            }

            if (!GammaDataIsFresh())
            {
                if (EnableDebugLogging) Print("[DEBUG] Standing aside - gamma data missing or stale");
                return;
            }

            bool positiveGamma = xBar.Close > _gamma.GammaFlip;
            string regime = positiveGamma ? "positive" : "negative";

            UpdateWallRoles(xBar); // keeps Holding/Broken state current for both entry gating and exit management below

            // Regime-flip safety exit: the theoretical basis for an open trade (dampening vs amplifying)
            // has inverted if the current regime no longer matches the regime it was entered under.
            if (_openPosition != null && _entryRegime != null && _entryRegime != regime)
            {
                CloseWithReason($"Regime flip - entered under {_entryRegime} gamma, now {regime} gamma (flip {_gamma.GammaFlip:F2} no longer holding) - premise for this trade has inverted", xBar.Close, xBar.OpenTime);
            }

            // Wall-proximity exit management (take-profit-at-wall-touch / breakeven stop tighten) - runs
            // regardless of what happens below, on whatever position is (still) open at this point.
            ManageWallProximityExit(xBar);

            (string emaBias, string emaReason) = GetEmaTrendBias();

            if (EnableDebugLogging)
            {
                double distCall = Math.Abs(xBar.Close - _gamma.CallWall) / _gamma.CallWall * 100.0;
                double distPut = Math.Abs(xBar.Close - _gamma.PutWall) / _gamma.PutWall * 100.0;
                Print($"[DEBUG] Close={xBar.Close:F2} Regime={regime} (flip {_gamma.GammaFlip:F2}) CallWall={_gamma.CallWall:F2} ({distCall:F3}% away) PutWall={_gamma.PutWall:F2} ({distPut:F3}% away) | EMA={(emaBias ?? "none")} ({emaReason ?? "n/a"}) | OpenPosition={(_openPosition != null)}");
            }

            if (_openPosition != null || emaBias == null) return;

            // Cooldown after ANY close (including the regime-flip exit just above, which can otherwise
            // reopen a position in the opposite direction on the same or next bar if price is choppy right
            // around the Gamma Flip level) - see the _lastCloseUtc field comment.
            if (_lastCloseUtc != null && (Server.TimeInUtc - _lastCloseUtc.Value).TotalSeconds < MinSecondsBetweenTrades)
            {
                if (EnableDebugLogging) Print($"[DEBUG] Standing aside - {MinSecondsBetweenTrades}s cooldown after last close not yet elapsed ({(Server.TimeInUtc - _lastCloseUtc.Value).TotalSeconds:F0}s so far)");
                return;
            }

            // Both setups are evaluated every bar now - wall role (not regime) gates which one can actually
            // fire (see the Strategy logic section of the file header). Fade checked first; if it opens a
            // position, breakout is skipped for this bar (can't open two at once anyway).
            TryFadeEntry(xBar, emaBias, regime);
            if (_openPosition == null)
                TryBreakoutEntry(xBar, emaBias, regime);
        }

        // Fade toward the Gamma Flip - only against a wall that is NOT Broken, only with EMA agreement.
        // Natural regime is positive gamma (dampening); firing in negative gamma is still allowed but
        // sized down via Off-Regime Risk Multiplier.
        private void TryFadeEntry(Bar xBar, string emaBias, string regime)
        {
            double distCallPct = Math.Abs(xBar.Close - _gamma.CallWall) / _gamma.CallWall * 100.0;
            double distPutPct = Math.Abs(xBar.Close - _gamma.PutWall) / _gamma.PutWall * 100.0;

            bool isOffRegime = regime == "negative";
            DateTime localDate = TimeZoneInfo.ConvertTimeFromUtc(xBar.OpenTime, _sessionTz).Date;
            double riskMult = GetConvictionRiskMultiplier(isFadeTrade: true, isOffRegime, localDate);
            string regimeTag = isOffRegime ? "Off-regime (negative gamma) FADE" : "Positive gamma FADE";

            if (emaBias == "short" && EnableShorts && distCallPct <= EntryProximityToWallPercent && _callWallRole != WallRole.Broken)
            {
                double stop = _gamma.CallWall * (1 + StopBufferPercent / 100.0);
                double target = _gamma.GammaFlip;
                if (!PassesRewardRisk(xBar.Close, stop, target, TradeType.Sell)) return;

                string reason = $"{regimeTag} SHORT at Call Wall {_gamma.CallWall:F2} (within {distCallPct:F3}%, role={_callWallRole}), target Gamma Flip {target:F2}, {emaBias.ToUpper()} EMA agrees ({emaReasonShort(emaBias)})" + ConvictionSuffix(riskMult);
                OpenPosition(TradeType.Sell, xBar, reason, stop, target, regime, riskMult, isFadeTrade: true);
            }
            else if (emaBias == "long" && EnableLongs && distPutPct <= EntryProximityToWallPercent && _putWallRole != WallRole.Broken)
            {
                double stop = _gamma.PutWall * (1 - StopBufferPercent / 100.0);
                double target = _gamma.GammaFlip;
                if (!PassesRewardRisk(xBar.Close, stop, target, TradeType.Buy)) return;

                string reason = $"{regimeTag} LONG at Put Wall {_gamma.PutWall:F2} (within {distPutPct:F3}%, role={_putWallRole}), target Gamma Flip {target:F2}, {emaBias.ToUpper()} EMA agrees ({emaReasonShort(emaBias)})" + ConvictionSuffix(riskMult);
                OpenPosition(TradeType.Buy, xBar, reason, stop, target, regime, riskMult, isFadeTrade: true);
            }
        }

        // Trade the wall FAILING to hold (breakout continuation) - only against a wall that IS Broken, and
        // (by default) was previously confirmed Holding before it broke. Only with EMA agreement. Natural
        // regime is negative gamma (amplifying); firing in positive gamma is still allowed but sized down
        // via Off-Regime Risk Multiplier, and does NOT get the negative-gamma stop-widen treatment.
        private void TryBreakoutEntry(Bar xBar, string emaBias, string regime)
        {
            bool putWallBroke = _putWallRole == WallRole.Broken && (!RequireWallTestedBeforeBreakout || _putWallEverHeld);
            bool callWallBroke = _callWallRole == WallRole.Broken && (!RequireWallTestedBeforeBreakout || _callWallEverHeld);

            bool isOffRegime = regime == "positive";
            string regimeTag = isOffRegime ? "Off-regime (positive gamma) BREAKOUT" : "Negative gamma BREAKOUT";

            // Negative gamma measured ~1.54x bigger next-day moves than positive gamma in real NDX data -
            // widen the stop (and therefore the R:R-derived target) only when the trade is actually firing
            // IN negative gamma, not just because Use Volatility Conviction is on generally.
            bool applyWiden = UseVolatilityConviction && regime == "negative";
            double stopBuf = applyWiden ? StopBufferPercent * NegativeGammaStopWidenFactor : StopBufferPercent;
            double riskMult = GetConvictionRiskMultiplier(isFadeTrade: false, isOffRegime, TimeZoneInfo.ConvertTimeFromUtc(xBar.OpenTime, _sessionTz).Date);
            string widenNote = applyWiden ? $", stop widened {NegativeGammaStopWidenFactor:F2}x" : "";

            if (emaBias == "short" && EnableShorts && putWallBroke)
            {
                double stop = _gamma.PutWall * (1 + stopBuf / 100.0);
                double stopDist = stop - xBar.Close;
                double target = xBar.Close - stopDist * BreakoutRewardToRiskRatio;

                string reason = $"{regimeTag} SHORT - Put Wall {_gamma.PutWall:F2} failed to hold (everHeld={_putWallEverHeld}), target {target:F2} ({BreakoutRewardToRiskRatio:F1}R{widenNote}), {emaBias.ToUpper()} EMA agrees" + ConvictionSuffix(riskMult);
                OpenPosition(TradeType.Sell, xBar, reason, stop, target, regime, riskMult, isFadeTrade: false);
            }
            else if (emaBias == "long" && EnableLongs && callWallBroke)
            {
                double stop = _gamma.CallWall * (1 - stopBuf / 100.0);
                double stopDist = xBar.Close - stop;
                double target = xBar.Close + stopDist * BreakoutRewardToRiskRatio;

                string reason = $"{regimeTag} LONG - Call Wall {_gamma.CallWall:F2} failed to hold (everHeld={_callWallEverHeld}), target {target:F2} ({BreakoutRewardToRiskRatio:F1}R{widenNote}), {emaBias.ToUpper()} EMA agrees" + ConvictionSuffix(riskMult);
                OpenPosition(TradeType.Buy, xBar, reason, stop, target, regime, riskMult, isFadeTrade: false);
            }
        }

        // Take-profit-at-wall-touch and breakeven-stop-tighten, both keyed off the wall AHEAD of the open
        // position in its favorable direction (Call Wall for longs, Put Wall for shorts).
        //
        // FADE TRADES ONLY: a fade enters AT one wall (e.g. long at the Put Wall) with the OPPOSITE wall
        // (Call Wall) still genuinely ahead and unreached - that's a real, known level to manage the exit
        // against. A BREAKOUT trade enters having just closed BEYOND its wall (e.g. long above a broken
        // Call Wall) - the "favorable" wall by this same isLong/isShort mapping would be that same Call
        // Wall, which price has already passed, so "wait for price to reach it" would fire instantly at
        // entry. There's no known next wall ahead of a breakout trade (see TryBreakoutEntry's R:R-based
        // target for why), so this is intentionally skipped for breakout trades rather than applied wrong.
        private void ManageWallProximityExit(Bar xBar)
        {
            if (_openPosition == null) return;
            if (!_entryIsFadeTrade) return;
            if (!TakeProfitAtWallTouch && WallProximityStopTightenPercent <= 0) return;
            if (!_gamma.IsValid) return;

            bool isLong = _openPosition.TradeType == TradeType.Buy;
            double favorableWall = isLong ? _gamma.CallWall : _gamma.PutWall;
            if (double.IsNaN(favorableWall) || favorableWall <= 0) return;

            bool reachedWall = isLong ? xBar.Close >= favorableWall : xBar.Close <= favorableWall;
            if (TakeProfitAtWallTouch && reachedWall)
            {
                CloseWithReason($"Take profit - price reached {(isLong ? "Call" : "Put")} Wall {favorableWall:F2} (close {xBar.Close:F2})", xBar.Close, xBar.OpenTime);
                return;
            }

            if (WallProximityStopTightenPercent <= 0) return;

            double distPct = Math.Abs(xBar.Close - favorableWall) / favorableWall * 100.0;
            if (distPct > WallProximityStopTightenPercent) return;

            bool inProfit = isLong ? xBar.Close > _entryPrice : xBar.Close < _entryPrice;
            if (!inProfit) return;

            double? currentStop = _openPosition.StopLoss;
            bool shouldTighten = currentStop == null
                || (isLong && currentStop.Value < _entryPrice)
                || (!isLong && currentStop.Value > _entryPrice);
            if (!shouldTighten) return;

            _openPosition.ModifyStopLossPrice(_entryPrice);
            Print($"[WALL] Tightened stop to breakeven ({_entryPrice:F2}) - price within {WallProximityStopTightenPercent:F2}% of favorable {(isLong ? "Call" : "Put")} Wall {favorableWall:F2}.");
        }

        private string ConvictionSuffix(double riskMult) => Math.Abs(riskMult - 1.0) > 0.001 ? $" [conviction risk x{riskMult:F2}]" : "";

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

        private void OpenPosition(TradeType type, Bar xBar, string reason, double stopPrice, double targetPrice, string regime, double riskMultiplier, bool isFadeTrade)
        {
            double estEntry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double stopDistance = Math.Abs(estEntry - stopPrice);

            double volumeInUnits = CalculateVolume(stopDistance, riskMultiplier);

            var result = ExecuteMarketOrder(type, SymbolName, volumeInUnits, PositionLabel, null, null, reason);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("Entry failed: " + result.Error);
                return;
            }

            _openPosition = result.Position;
            _entryPrice = result.Position.EntryPrice;
            _entryRegime = regime;
            _entryIsFadeTrade = isFadeTrade;

            _openPosition.ModifyStopLossPrice(stopPrice);
            _openPosition.ModifyTakeProfitPrice(targetPrice);

            Print(reason + $" | Volume: {volumeInUnits} units | FillPrice={_entryPrice:F2} Stop={stopPrice:F2} Target={targetPrice:F2} Equity={Account.Equity:F2}");
            if (ShowReasons)
                DrawReasonLabel(reason, xBar.OpenTime, type == TradeType.Buy ? xBar.Low : xBar.High, type == TradeType.Buy, Color.LimeGreen);
        }

        private double CalculateVolume(double stopDistance, double riskMultiplier = 1.0)
        {
            if (stopDistance <= 0) return Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(FallbackVolumeLots));

            double riskAmount = Account.FreeMargin * (RiskPercent * riskMultiplier) / 100.0;
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
                _entryRegime = null;
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

        // Purely informational - the highest Call Wall and lowest Put Wall seen across this week's swept
        // expirations (see SweepOneExpiration/BuildSweepQueue). NOT a support/resistance claim and NOT
        // used anywhere in entry/exit logic. Deliberately shown RAW rather than filtered by distance from
        // spot: the more distant expirations are swept before they've accumulated much real trading
        // activity, so an extreme far from spot may be a genuine level or may just be sparse-data noise
        // from a still-thin book - there's no reliable way to tell which from a single snapshot, so both
        // possibilities are left visible rather than one being silently filtered out on assumption.
        private void DrawSweepExtremesLabel()
        {
            if (double.IsNaN(_sweepExtremeCallWall) && double.IsNaN(_sweepExtremePutWall)) return;

            string text = $"Week sweep extremes (informational, raw, not a trade level): Call Wall high {_sweepExtremeCallWall:F2} ({_sweepExtremeCallWallExpiration}) | Put Wall low {_sweepExtremePutWall:F2} ({_sweepExtremePutWallExpiration})";
            Print($"[SWEEP] {text}");

            Chart.RemoveObject("SweepExtremeCallWall");
            Chart.RemoveObject("SweepExtremePutWall");
            Chart.RemoveObject("SweepExtremeLabel");

            var now = Server.TimeInUtc;
            var end = now.AddDays(WeeklySweepMaxDays);
            Chart.DrawTrendLine("SweepExtremeCallWall", now, _sweepExtremeCallWall, end, _sweepExtremeCallWall, Color.OrangeRed, 1, LineStyle.LinesDots);
            Chart.DrawTrendLine("SweepExtremePutWall", now, _sweepExtremePutWall, end, _sweepExtremePutWall, Color.DodgerBlue, 1, LineStyle.LinesDots);

            var label = Chart.DrawText("SweepExtremeLabel", text, now, _sweepExtremeCallWall, Color.Silver);
            label.VerticalAlignment = VerticalAlignment.Top;
            label.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }
}
