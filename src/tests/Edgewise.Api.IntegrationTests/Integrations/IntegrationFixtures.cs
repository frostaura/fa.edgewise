namespace Edgewise.Api.IntegrationTests.Integrations;

/// <summary>Recorded-style JSON payloads matching the real provider response shapes.</summary>
public static class IntegrationFixtures
{
    public const string Wallet = "0x56687bf447db6ffa42ffe2204a05edaa20f55839";

    public const string ConditionId = "0xdd22472e552920b8438158ea7238bfadfa4f736aa4cee91a6b86c39ead110917";

    // ------------------------------------------------------------ polymarket

    public const string PolymarketTrades = $$"""
        [
          {
            "proxyWallet": "{{Wallet}}",
            "side": "BUY",
            "asset": "71321045679252212594626385532706912750332728571942532289631379312455583992563",
            "conditionId": "{{ConditionId}}",
            "size": 100,
            "price": 0.62,
            "timestamp": 1750000000,
            "title": "Will BTC close above $120k in 2026?",
            "slug": "will-btc-close-above-120k-in-2026",
            "icon": "https://polymarket-upload.s3.us-east-2.amazonaws.com/btc.png",
            "eventSlug": "btc-2026",
            "outcome": "Yes",
            "outcomeIndex": 0,
            "name": "edgewise-tester",
            "pseudonym": "Curious-Capybara",
            "transactionHash": "0x1f60d267c9d1fce2eff6e78e232b95d6e05b1c81b169e131fbc26b355421bcd0"
          },
          {
            "proxyWallet": "{{Wallet}}",
            "side": "BUY",
            "asset": "71321045679252212594626385532706912750332728571942532289631379312455583992563",
            "conditionId": "{{ConditionId}}",
            "size": 50,
            "price": 0.7,
            "timestamp": 1750100000,
            "title": "Will BTC close above $120k in 2026?",
            "slug": "will-btc-close-above-120k-in-2026",
            "outcome": "Yes",
            "outcomeIndex": 0,
            "transactionHash": "0x2a51e378d0e2fdf3f007f89f343ca6e7f16c2d92c270f242fcd37c466532cde1"
          },
          {
            "proxyWallet": "{{Wallet}}",
            "side": "SELL",
            "asset": "52114319501245915516055106046884209969926127482827954674443846427813813222426",
            "conditionId": "0xaa5b2af97bd8474b6bd7ef65f04ec36e611d19cbb63f4a1e3f2f3c9cf9c53a05",
            "size": 20,
            "price": 0.31,
            "timestamp": 1750200000,
            "title": "Fed cuts rates in September?",
            "slug": "fed-cuts-rates-in-september",
            "outcome": "No",
            "outcomeIndex": 1,
            "transactionHash": "0x3b62f489e1f30e04f118f9af454db7f8f27d3ea3d381f353fde48d577643def2"
          }
        ]
        """;

    public const string PolymarketPositionsResolved = $$"""
        [
          {
            "proxyWallet": "{{Wallet}}",
            "asset": "71321045679252212594626385532706912750332728571942532289631379312455583992563",
            "conditionId": "{{ConditionId}}",
            "size": 150,
            "avgPrice": 0.6466,
            "initialValue": 97,
            "currentValue": 150,
            "cashPnl": 53,
            "percentPnl": 54.6,
            "curPrice": 1.0,
            "redeemable": true,
            "title": "Will BTC close above $120k in 2026?",
            "slug": "will-btc-close-above-120k-in-2026",
            "outcome": "Yes",
            "outcomeIndex": 0,
            "endDate": "2026-12-31T12:00:00Z"
          }
        ]
        """;

    public const string PolymarketPositionsOpen = $$"""
        [
          {
            "proxyWallet": "{{Wallet}}",
            "conditionId": "{{ConditionId}}",
            "size": 150,
            "avgPrice": 0.6466,
            "curPrice": 0.66,
            "redeemable": false,
            "title": "Will BTC close above $120k in 2026?",
            "slug": "will-btc-close-above-120k-in-2026",
            "outcome": "Yes",
            "outcomeIndex": 0
          }
        ]
        """;

    public const string EmptyArray = "[]";

    // --------------------------------------------------------------- binance

    public const string BinanceRestrictionsReadOnly = """
        {
          "ipRestrict": false,
          "createTime": 1698645219000,
          "enableReading": true,
          "enableSpotAndMarginTrading": false,
          "enableWithdrawals": false,
          "enableInternalTransfer": false,
          "enableMargin": false,
          "enableFutures": false,
          "permitsUniversalTransfer": false,
          "enableVanillaOptions": false
        }
        """;

    public const string BinanceRestrictionsTradingEnabled = """
        {
          "ipRestrict": false,
          "createTime": 1698645219000,
          "enableReading": true,
          "enableSpotAndMarginTrading": true,
          "enableWithdrawals": false,
          "enableInternalTransfer": false,
          "enableMargin": false,
          "enableFutures": false,
          "permitsUniversalTransfer": false
        }
        """;

    public const string BinanceRestrictionsNoReading = """
        {
          "ipRestrict": true,
          "createTime": 1698645219000,
          "enableReading": false,
          "enableSpotAndMarginTrading": false,
          "enableWithdrawals": false
        }
        """;

    public const string BinanceAccount = """
        {
          "makerCommission": 10,
          "takerCommission": 10,
          "canTrade": false,
          "canWithdraw": false,
          "canDeposit": true,
          "accountType": "SPOT",
          "balances": [
            { "asset": "BTC", "free": "0.51230000", "locked": "0.00000000" },
            { "asset": "USDT", "free": "1043.55000000", "locked": "0.00000000" },
            { "asset": "ETH", "free": "0.00000000", "locked": "0.00000000" }
          ]
        }
        """;

    public const string BinanceExchangeInfo = """
        {
          "timezone": "UTC",
          "serverTime": 1750000000000,
          "symbols": [
            { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT" },
            { "symbol": "BTCUSDC", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDC" },
            { "symbol": "ETHUSDT", "status": "TRADING", "baseAsset": "ETH", "quoteAsset": "USDT" },
            { "symbol": "LUNAUSDT", "status": "BREAK", "baseAsset": "LUNA", "quoteAsset": "USDT" }
          ]
        }
        """;

    public const string BinanceMyTradesBtcUsdt = """
        [
          {
            "symbol": "BTCUSDT",
            "id": 28457,
            "orderId": 100234,
            "orderListId": -1,
            "price": "60000.00000000",
            "qty": "0.00500000",
            "quoteQty": "300.00000000",
            "commission": "0.30000000",
            "commissionAsset": "USDT",
            "time": 1750001000000,
            "isBuyer": true,
            "isMaker": false,
            "isBestMatch": true
          },
          {
            "symbol": "BTCUSDT",
            "id": 28460,
            "orderId": 100301,
            "orderListId": -1,
            "price": "61000.00000000",
            "qty": "0.00200000",
            "quoteQty": "122.00000000",
            "commission": "0.00010000",
            "commissionAsset": "BNB",
            "time": 1750002000000,
            "isBuyer": false,
            "isMaker": true,
            "isBestMatch": true
          }
        ]
        """;

    // -------------------------------------------------------------- coinbase

    public const string CoinbaseAccounts = """
        {
          "accounts": [
            {
              "uuid": "8bfc20d7-f7c6-4422-bf07-8243ca4169fe",
              "name": "BTC Wallet",
              "currency": "BTC",
              "available_balance": { "value": "0.05", "currency": "BTC" },
              "type": "ACCOUNT_TYPE_CRYPTO",
              "ready": true
            }
          ],
          "has_next": false,
          "cursor": "",
          "size": 1
        }
        """;

    public const string CoinbaseFillsPage = """
        {
          "fills": [
            {
              "entry_id": "22222-000000-000001",
              "trade_id": "1111-11d1-8f36-1111",
              "order_id": "0000-000000-000000",
              "trade_time": "2026-05-31T09:59:59Z",
              "trade_type": "FILL",
              "price": "68000.00",
              "size": "0.001",
              "commission": "1.25",
              "product_id": "BTC-USD",
              "sequence_timestamp": "2026-05-31T10:00:00.000Z",
              "liquidity_indicator": "TAKER",
              "size_in_quote": false,
              "user_id": "3333-333333-3333333",
              "side": "BUY"
            },
            {
              "entry_id": "22222-000000-000002",
              "trade_id": "1111-11d1-8f36-2222",
              "order_id": "0000-000000-000001",
              "trade_time": "2026-06-02T14:10:00Z",
              "trade_type": "FILL",
              "price": "3500.00",
              "size": "0.4",
              "commission": "0.80",
              "product_id": "ETH-USD",
              "sequence_timestamp": "2026-06-02T14:10:01.000Z",
              "liquidity_indicator": "MAKER",
              "size_in_quote": false,
              "user_id": "3333-333333-3333333",
              "side": "SELL"
            }
          ],
          "cursor": ""
        }
        """;
}
