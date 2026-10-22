using System.Globalization;
using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Engine.Paper;

/// <summary>
/// A simulated Revolut X account for paper trading. Market data (pairs, candles) comes from the real exchange;
/// orders, fills and balances are simulated from the quotes passed to <see cref="OnQuote"/>.
/// </summary>
/// <remarks>
/// Fill model, deliberately conservative so paper results don't flatter a strategy:
/// <list type="bullet">
/// <item>An order that crosses the book when placed fills at the best opposite price, up to the size shown there,
/// paying the taker fee. Any remainder keeps resting and fills as a taker on later quotes.</item>
/// <item>An order that rests without crossing fills at its limit price, with the maker fee, only once the market
/// trades through it (best ask strictly below a buy, best bid strictly above a sell), up to the size shown.</item>
/// <item>A post-only order that would cross is rejected.</item>
/// <item>Funds are reserved at placement: quote currency for buys (including the worst-case taker fee), base for sells.</item>
/// <item>Placing the same client order ID again returns the existing order and changes nothing.</item>
/// </list>
/// </remarks>
public sealed class PaperExchange : IExchangeClient
{
    private readonly IExchangeClient _marketData;
    private readonly PaperOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Wallet> _wallets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PaperOrder> _byVenueId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperOrder> _byClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketQuote> _quotes = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, PairInfo>? _pairs;

    public PaperExchange(IExchangeClient marketData, PaperOptions options, TimeProvider? time = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.TakerFeeRate is < 0 or > 0.01m || options.MakerFeeRate is < 0 or > 0.01m)
            throw new ArgumentException("Paper fee rates must be between 0 and 1%.", nameof(options));

        _time = time ?? TimeProvider.System;
        foreach (var (currency, amount) in options.InitialBalances)
        {
            if (amount < 0)
                throw new ArgumentException($"Initial balance for {currency} is negative.", nameof(options));
            GetWallet(currency).Available = amount;
        }
    }

    /// <summary>Feeds a new best bid/ask; resting orders on that symbol fill if the market reached them.</summary>
    public void OnQuote(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.Bid <= 0 || quote.Ask <= 0 || quote.Bid > quote.Ask)
            throw new ArgumentException("A quote needs positive prices and bid <= ask.", nameof(quote));

        lock (_gate)
        {
            var symbol = Normalise(quote.Symbol);
            _quotes[symbol] = quote with { Symbol = symbol };

            if (_pairs is null || !_pairs.TryGetValue(symbol, out var pair))
                return;

            var bidSize = quote.BidSize;
            var askSize = quote.AskSize;
            foreach (var order in _byVenueId.Values.Where(o => o.Symbol == symbol && o.IsOpen).OrderBy(o => o.Sequence))
            {
                if (order.Side == OrderSide.Buy)
                    askSize -= TryFillResting(order, pair, quote.Ask, askSize);
                else
                    bidSize -= TryFillResting(order, pair, quote.Bid, bidSize);
            }
        }
    }

    public Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Balance> balances = _wallets
                .Where(w => w.Value.Available != 0 || w.Value.Reserved != 0)
                .OrderBy(w => w.Key, StringComparer.Ordinal)
                .Select(w => new Balance(w.Key, w.Value.Available, w.Value.Reserved, w.Value.Available + w.Value.Reserved))
                .ToList();
            return Task.FromResult(balances);
        }
    }

    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
        _marketData.GetPairsAsync(cancellationToken);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
        _marketData.GetCandlesAsync(symbol, intervalMinutes, since, cancellationToken);

    public async Task<PlacedOrder> PlaceLimitOrderAsync(OrderIntent approvedOrder, bool postOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedOrder.ClientOrderId);
        var pairs = await EnsurePairsAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_byClientId.TryGetValue(approvedOrder.ClientOrderId, out var existing))
                return new PlacedOrder(existing.VenueOrderId, existing.ClientOrderId);

            var symbol = Normalise(approvedOrder.Symbol);
            if (!pairs.TryGetValue(symbol, out var pair) || !pair.Active)
                throw Reject("paper-unknown-symbol", $"Symbol {symbol} is not tradable.");

            var size = approvedOrder.BaseSize;
            var price = approvedOrder.LimitPrice;
            if (size <= 0 || price <= 0)
                throw Reject("paper-invalid-order", "Size and price must be positive.");
            if (!IsMultiple(size, pair.BaseStep) || !IsMultiple(price, pair.QuoteStep))
                throw Reject("paper-step", $"Size must be a multiple of {pair.BaseStep} and price of {pair.QuoteStep}.");
            if (size < pair.MinOrderSize || size > pair.MaxOrderSize || size * price < pair.MinOrderSizeQuote)
                throw Reject("paper-size", $"Order size is outside the limits for {symbol}.");

            if (!_quotes.TryGetValue(symbol, out var quote) || _time.GetUtcNow() - quote.AsOf > _options.MaxQuoteAge)
                throw Reject("paper-no-market-data", $"No fresh quote for {symbol}; the paper engine can't price the order.");

            var now = _time.GetUtcNow();
            var order = new PaperOrder(
                Guid.NewGuid().ToString(),
                approvedOrder.ClientOrderId,
                symbol,
                pair.Base,
                pair.Quote,
                approvedOrder.Side,
                size,
                price,
                _byVenueId.Count,
                now);

            var crosses = approvedOrder.Side == OrderSide.Buy ? price >= quote.Ask : price <= quote.Bid;
            if (crosses && postOnly)
            {
                // Recorded like the exchange would: the order exists but is rejected, nothing reserved.
                order.Status = OrderStatus.Rejected;
                Register(order);
                return new PlacedOrder(order.VenueOrderId, order.ClientOrderId);
            }

            Reserve(order);
            order.IsTaker = crosses;
            Register(order);

            if (crosses)
            {
                var available = approvedOrder.Side == OrderSide.Buy ? quote.AskSize : quote.BidSize;
                var touch = approvedOrder.Side == OrderSide.Buy ? quote.Ask : quote.Bid;
                Fill(order, pair, Math.Min(order.Remaining, RoundDown(available, pair.BaseStep)), touch, isTaker: true);
            }

            return new PlacedOrder(order.VenueOrderId, order.ClientOrderId);
        }
    }

    public Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_byVenueId.TryGetValue(venueOrderId, out var order) ? order.ToExchangeOrder() : null);
        }
    }

    public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var wanted = symbol is null ? null : Normalise(symbol);
            IReadOnlyList<ExchangeOrder> open = _byVenueId.Values
                .Where(o => o.IsOpen && (wanted is null || o.Symbol == wanted))
                .OrderBy(o => o.Sequence)
                .Select(o => o.ToExchangeOrder())
                .ToList();
            return Task.FromResult(open);
        }
    }

    public Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byVenueId.TryGetValue(venueOrderId, out var order))
                return Task.FromResult(false);

            if (order.IsOpen)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = _time.GetUtcNow();
                ReleaseReservation(order);
            }

            return Task.FromResult(true);
        }
    }

    private async Task<IReadOnlyDictionary<string, PairInfo>> EnsurePairsAsync(CancellationToken cancellationToken)
    {
        var cached = _pairs;
        if (cached is not null)
            return cached;

        var pairs = await _marketData.GetPairsAsync(cancellationToken).ConfigureAwait(false);
        var map = pairs.ToDictionary(p => Normalise(p.Symbol), StringComparer.Ordinal);
        lock (_gate)
        {
            _pairs ??= map;
            return _pairs;
        }
    }

    // Returns the quantity filled so the caller can reduce the size left at that price level.
    private decimal TryFillResting(PaperOrder order, PairInfo pair, decimal touch, decimal sizeAtTouch)
    {
        if (sizeAtTouch <= 0)
            return 0;

        bool reached;
        decimal fillPrice;
        if (order.IsTaker)
        {
            // Remainder of a crossing order: fills whenever the book still crosses its limit.
            reached = order.Side == OrderSide.Buy ? touch <= order.LimitPrice : touch >= order.LimitPrice;
            fillPrice = touch;
        }
        else
        {
            // Resting maker order: needs the market to trade through it.
            reached = order.Side == OrderSide.Buy ? touch < order.LimitPrice : touch > order.LimitPrice;
            fillPrice = order.LimitPrice;
        }

        if (!reached)
            return 0;

        var quantity = Math.Min(order.Remaining, RoundDown(sizeAtTouch, pair.BaseStep));
        Fill(order, pair, quantity, fillPrice, order.IsTaker);
        return quantity;
    }

    private void Fill(PaperOrder order, PairInfo pair, decimal quantity, decimal price, bool isTaker)
    {
        if (quantity <= 0)
            return;

        var notional = quantity * price;
        var fee = Math.Round(notional * (isTaker ? _options.TakerFeeRate : _options.MakerFeeRate), 8, MidpointRounding.AwayFromZero);
        var baseWallet = GetWallet(order.BaseCurrency);
        var quoteWallet = GetWallet(order.QuoteCurrency);

        if (order.Side == OrderSide.Buy)
        {
            var reservedForQuantity = Math.Min(order.Reserved, BuyReservation(quantity, order.LimitPrice));
            order.Reserved -= reservedForQuantity;
            quoteWallet.Reserved -= reservedForQuantity;
            quoteWallet.Available += reservedForQuantity - notional - fee;
            baseWallet.Available += quantity;
        }
        else
        {
            order.Reserved -= quantity;
            baseWallet.Reserved -= quantity;
            quoteWallet.Available += notional - fee;
        }

        order.Filled += quantity;
        order.FilledAmount += notional;
        order.Fee += fee;
        order.UpdatedAt = _time.GetUtcNow();
        order.Status = order.Remaining == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        if (order.Status == OrderStatus.Filled)
            ReleaseReservation(order);
    }

    private void Reserve(PaperOrder order)
    {
        var (currency, amount) = order.Side == OrderSide.Buy
            ? (order.QuoteCurrency, BuyReservation(order.Quantity, order.LimitPrice))
            : (order.BaseCurrency, order.Quantity);

        var wallet = GetWallet(currency);
        if (wallet.Available < amount)
            throw Reject("paper-insufficient-balance", $"Insufficient balance: {currency} available {wallet.Available.ToString(CultureInfo.InvariantCulture)}, needed {amount.ToString(CultureInfo.InvariantCulture)}.");

        wallet.Available -= amount;
        wallet.Reserved += amount;
        order.Reserved = amount;
    }

    private void ReleaseReservation(PaperOrder order)
    {
        if (order.Reserved == 0)
            return;

        var wallet = GetWallet(order.Side == OrderSide.Buy ? order.QuoteCurrency : order.BaseCurrency);
        wallet.Reserved -= order.Reserved;
        wallet.Available += order.Reserved;
        order.Reserved = 0;
    }

    // Reserves enough for the worst case: the full size at the limit price, paying the taker fee.
    private decimal BuyReservation(decimal quantity, decimal limitPrice) =>
        Math.Round(quantity * limitPrice * (1 + _options.TakerFeeRate), 8, MidpointRounding.ToPositiveInfinity);

    private void Register(PaperOrder order)
    {
        _byVenueId.Add(order.VenueOrderId, order);
        _byClientId.Add(order.ClientOrderId, order);
    }

    private Wallet GetWallet(string currency)
    {
        if (!_wallets.TryGetValue(currency, out var wallet))
        {
            wallet = new Wallet();
            _wallets[currency] = wallet;
        }

        return wallet;
    }

    private static ExchangeException Reject(string errorId, string message) => new(message, 400, errorId);

    private static string Normalise(string symbol) => symbol.Trim().Replace('/', '-').ToUpperInvariant();

    private static bool IsMultiple(decimal value, decimal step) => step <= 0 || value % step == 0;

    private static decimal RoundDown(decimal value, decimal step) =>
        step <= 0 ? value : Math.Floor(value / step) * step;

    private sealed class Wallet
    {
        public decimal Available { get; set; }
        public decimal Reserved { get; set; }
    }

    private sealed class PaperOrder(
        string venueOrderId,
        string clientOrderId,
        string symbol,
        string baseCurrency,
        string quoteCurrency,
        OrderSide side,
        decimal quantity,
        decimal limitPrice,
        int sequence,
        DateTimeOffset createdAt)
    {
        public string VenueOrderId { get; } = venueOrderId;
        public string ClientOrderId { get; } = clientOrderId;
        public string Symbol { get; } = symbol;
        public string BaseCurrency { get; } = baseCurrency;
        public string QuoteCurrency { get; } = quoteCurrency;
        public OrderSide Side { get; } = side;
        public decimal Quantity { get; } = quantity;
        public decimal LimitPrice { get; } = limitPrice;
        public int Sequence { get; } = sequence;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset UpdatedAt { get; set; } = createdAt;
        public OrderStatus Status { get; set; } = OrderStatus.New;
        public bool IsTaker { get; set; }
        public decimal Filled { get; set; }
        public decimal FilledAmount { get; set; }
        public decimal Fee { get; set; }
        public decimal Reserved { get; set; }

        public decimal Remaining => Quantity - Filled;

        private static string RawStatus(OrderStatus status) => status switch
        {
            OrderStatus.PartiallyFilled => "partially_filled",
            _ => status.ToString().ToLowerInvariant(),
        };
        public bool IsOpen => Status is OrderStatus.New or OrderStatus.PartiallyFilled;

        public ExchangeOrder ToExchangeOrder() => new(
            VenueOrderId,
            ClientOrderId,
            Symbol,
            Side,
            Quantity,
            Filled,
            FilledAmount,
            LimitPrice,
            Fee,
            QuoteCurrency,
            Status,
            RawStatus(Status),
            CreatedAt,
            UpdatedAt);
    }
}
