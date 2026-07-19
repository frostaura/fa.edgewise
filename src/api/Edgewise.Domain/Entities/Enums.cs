namespace Edgewise.Domain.Entities;

public enum BucketKind
{
    LongTerm,
    Trading,
    Prediction,
    Cash,
}

public enum Venue
{
    Manual,
    Csv,
    Binance,
    Coinbase,
    Polymarket,
    EasyEquities,
    Generic,
}

public enum AccountStatus
{
    Connected,
    Error,
    Revoked,
}

public enum AssetClass
{
    Equity,
    Etf,
    Crypto,
    Prediction,
    Cash,
    Custom,
}

public enum CashFlowType
{
    Deposit,
    Withdrawal,
    Transfer,
    Ratchet,
    DividendReceipt,
}

public enum TradeDirection
{
    Long,
    Short,
}

public enum TradePlanStatus
{
    Draft,
    Active,
    Promoted,
    Cancelled,
    Expired,
}

public enum TradeStatus
{
    Open,
    Closed,
}

public enum EmotionTag
{
    None,
    Calm,
    Fomo,
    Tilt,
    Bored,
    Rushed,
}

public enum PositionMethod
{
    Fifo,
    Manual,
}

public enum FillSide
{
    Buy,
    Sell,
}

public enum FillSource
{
    Manual,
    Csv,
    Api,
}

public enum MatchStatus
{
    Proposed,
    Matched,
    Confessed,
}

public enum DecisionKind
{
    Enter,
    Exit,
    Skip,
}

public enum OverrideKind
{
    CircuitBreaker,
    Ladder,
    CockpitRed,
    SizeOverride,
}

public enum InsightType
{
    PostMortem,
    WeeklyPack,
    Bias,
    Calibration,
    Dossier,
    Digest,
    ChatAnswer,
}

public enum InsightFeedback
{
    None,
    Up,
    Down,
}

public enum Timeframe
{
    H1,
    H4,
    D1,
    W1,
}

public enum CatalystKind
{
    Earnings,
    Macro,
    Unlock,
    PmResolution,
    Dividend,
    User,
}

public enum CatalystSeverity
{
    Red,
    Amber,
}

public enum SentimentKind
{
    CryptoFG,
    EquityFG,
}

public enum AlertKind
{
    PriceCross,
    PctMove,
    ZoneTouch,
    FundingRate,
    FgExtreme,
    CatalystT24,
}

public enum StrategyState
{
    Draft,
    Backtested,
    Paper,
    TinyLive,
    Live,
}

public enum BacktestStatus
{
    Pending,
    Running,
    Done,
    Failed,
}
