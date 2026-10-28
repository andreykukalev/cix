using System.Globalization;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;

namespace Cix.Engine.Paper;

/// <summary>
/// A simulated Revolut X account for paper trading. Market data (pairs, candles) comes from an
/// <see cref="IMarketDataSource"/>, which has no order methods, so nothing here can place a real order.
/// Orders, fills and balances are simulated from the quotes passed to <see cref="OnQuote"/>. State is in memory;
/// it is lost on restart until the journal and state store land (#6).
/// </summary>
/// <remarks>
/// Fill model, deliberately conservative so paper results don't flatter a strategy:
/// <list type="bullet">
/// <item>An order that crosses the book when placed fills at the best opposite price, up to the size shown there,
/// paying the taker fee. The size it takes is removed from the stored quote, so later orders can't reuse it.</item>
/// <item>The remainder of a crossing order rests at its limit and fills there, still paying the taker fee, whenever
/// a later quote reaches its limit. It never gets a better price than its limit.</item>
/// <item>An order that rests without crossing fills at its limit price, with the maker fee, only once the market
/// trades through it (best ask strictly below a buy, best bid strictly above a sell), up to the size shown.</item>
/// <item>A post-only order that would cross is rejected.</item>
/// <item>An immediate-or-cancel order takes what the book shows at its limit, as a crossing order does; the rest is cancelled.</item>
/// <item>Funds are reserved at placement: quote currency for buys (full size at the limit plus the taker fee), base for sells.
/// Fees round down to 8 decimals, so a fill never costs more than its reservation.</item>
/// <item>Placing the same client order ID again returns the existing order; reusing it with different details is rejected.</item>
/// </list>
/// </remarks>
public sealed class PaperExchange : IExchangeClient, IQuoteListener
{
    private readonly IMarketDataSource _marketData;
    private readonly PaperOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Wallet> _wallets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PaperOrder> _byVenueId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperOrder> _byClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketQuote> _quotes = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, PairInfo>? _pairs;
    private DateTimeOffset _pairsLoadedAt;

    public PaperExchange(IMarketDataSource marketData, PaperOptions options, TimeProvider? time = null)
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

    /// <summary>Finished orders kept when saving; older ones are dropped so the saved account stays small.</summary>
    public const int MaxSavedFinishedOrders = 500;

    /// <summary>
    /// False plays an exchange that ignores immediate-or-cancel and lets the unfilled rest of such an order rest on the book,
    /// which Revolut X's live behaviour hasn't confirmed yet. Tests only; the protection must cancel that rest itself.
    /// </summary>
    public bool HonoursImmediateOrCancel { get; set; } = true;

    /// <summary>The account as it stands: balances and every order, for persistence. Quotes are not kept (they go stale).</summary>
    public PaperAccountState ExportState()
    {
        lock (_gate)
        {
            return new PaperAccountState(
                _wallets.Where(w => w.Value.Available != 0 || w.Value.Reserved != 0)
                    .ToDictionary(w => w.Key.ToUpperInvariant(), w => new PaperWalletState(w.Value.Available, w.Value.Reserved), StringComparer.Ordinal),
                // Open orders plus the most recent finished ones: enough for lookups and idempotency, bounded in size.
                _byVenueId.Values.Where(o => o.IsOpen)
                    .Concat(_byVenueId.Values.Where(o => !o.IsOpen).OrderByDescending(o => o.Sequence).Take(MaxSavedFinishedOrders))
                    .OrderBy(o => o.Sequence).Select(o => new PaperOrderState(
                    o.VenueOrderId, o.ClientOrderId, o.Symbol, o.BaseCurrency, o.QuoteCurrency, o.Side, o.Quantity, o.LimitPrice,
                    o.Sequence, o.CreatedAt, o.UpdatedAt, o.Status, o.IsTaker, o.Filled, o.FilledAmount, o.Fee, o.Reserved)).ToList());
        }
    }

    /// <summary>
    /// Replaces the account with a saved one (at startup, before any order or quote). The configured initial balances are
    /// ignored: the saved account is the truth.
    /// </summary>
    public void RestoreState(PaperAccountState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_byVenueId.Count > 0 || _quotes.Count > 0)
                throw new InvalidOperationException("The paper account can only be restored before it is used.");

            _wallets.Clear();
            foreach (var (currency, wallet) in state.Wallets)
            {
                if (wallet.Available < 0 || wallet.Reserved < 0)
                    throw new InvalidOperationException($"Saved paper balance for {currency} is negative.");
                var w = GetWallet(currency);
                w.Available = wallet.Available;
                w.Reserved = wallet.Reserved;
            }

            if (state.Orders.Select(o => o.VenueOrderId).Distinct().Count() != state.Orders.Count
                || state.Orders.Select(o => o.ClientOrderId).Distinct().Count() != state.Orders.Count)
                throw new InvalidOperationException("Saved paper orders have duplicate IDs.");

            foreach (var saved in state.Orders.OrderBy(o => o.Sequence))
            {
                if (saved.Quantity <= 0 || saved.LimitPrice <= 0 || saved.Filled < 0 || saved.Filled > saved.Quantity
                    || saved.FilledAmount < 0 || saved.Fee < 0 || saved.Reserved < 0)
                    throw new InvalidOperationException($"Saved paper order {saved.VenueOrderId} is inconsistent.");

                var order = new PaperOrder(
                    saved.VenueOrderId, saved.ClientOrderId, saved.Symbol, saved.BaseCurrency, saved.QuoteCurrency, saved.Side,
                    saved.Quantity, saved.LimitPrice, saved.Sequence, saved.CreatedAt)
                {
                    UpdatedAt = saved.UpdatedAt,
                    Status = saved.Status,
                    IsTaker = saved.IsTaker,
                    Filled = saved.Filled,
                    FilledAmount = saved.FilledAmount,
                    Fee = saved.Fee,
                    Reserved = saved.Reserved,
                };
                Register(order);
            }
        }
    }

    /// <summary>
    /// Loads the pair rules now. Resting orders only fill once they are loaded, which otherwise happens on the first
    /// placement; after a restore there may be resting orders long before the next placement.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken) => EnsurePairsAsync(cancellationToken);

    /// <summary>
    /// Feeds a new best bid/ask; resting orders on that symbol fill if the market reached them. A quote without sizes (a
    /// ticker) is ignored: the paper exchange fills only against an order book, so it never fills more than the book showed,
    /// and a taker order placed between books gets a book fetched for it (<see cref="Execution.ExecutionService"/>).
    /// </summary>
    public void OnQuote(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (!quote.IsValid)
            throw new ArgumentException("A quote needs positive prices, non-negative sizes and bid <= ask.", nameof(quote));
        if (quote.BidSize <= 0 && quote.AskSize <= 0)
            return;

        lock (_gate)
        {
            var symbol = Normalise(quote.Symbol);
            if (_quotes.TryGetValue(symbol, out var current) && current.AsOf > quote.AsOf)
                return; // Out-of-order delivery: never let an older quote replace a newer one.

            var bidSize = quote.BidSize;
            var askSize = quote.AskSize;
            if (_pairs is not null && _pairs.TryGetValue(symbol, out var pair))
            {
                foreach (var order in _byVenueId.Values.Where(o => o.Symbol == symbol && o.IsOpen).OrderBy(o => o.Sequence))
                {
                    if (order.Side == OrderSide.Buy)
                        askSize -= TryFillResting(order, pair, quote.Ask, askSize);
                    else
                        bidSize -= TryFillResting(order, pair, quote.Bid, bidSize);
                }
            }

            _quotes[symbol] = quote with { Symbol = symbol, BidSize = bidSize, AskSize = askSize };
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

    public async Task<PlacedOrder> PlaceLimitOrderAsync(ApprovedOrder approvedOrder, bool postOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedOrder);
        approvedOrder.Claim();
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedOrder.ClientOrderId);
        var pairs = await EnsurePairsAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var symbol = Normalise(approvedOrder.Symbol);
            var size = approvedOrder.BaseSize;
            var price = approvedOrder.LimitPrice;

            if (_byClientId.TryGetValue(approvedOrder.ClientOrderId, out var existing))
            {
                if (existing.Symbol != symbol || existing.Side != approvedOrder.Side || existing.Quantity != size || existing.LimitPrice != price)
                    throw Reject("paper-duplicate-client-order-id", "This client order ID was already used for a different order.");
                return new PlacedOrder(existing.VenueOrderId, existing.ClientOrderId);
            }

            if (!pairs.TryGetValue(symbol, out var pair) || !pair.Active)
                throw Reject("paper-unknown-symbol", $"Symbol {symbol} is not tradable.");
            if (size <= 0 || price <= 0)
                throw Reject("paper-invalid-order", "Size and price must be positive.");
            if (!IsMultiple(size, pair.BaseStep) || !IsMultiple(price, pair.QuoteStep))
                throw Reject("paper-step", $"Size must be a multiple of {pair.BaseStep} and price of {pair.QuoteStep}.");
            if (size < pair.MinOrderSize || size > pair.MaxOrderSize || size * price < pair.MinOrderSizeQuote)
                throw Reject("paper-size", $"Order size is outside the limits for {symbol}.");

            var now = _time.GetUtcNow();
            if (!_quotes.TryGetValue(symbol, out var quote) || now - quote.AsOf > _options.MaxQuoteAge)
                throw Reject("paper-no-market-data", $"No fresh quote for {symbol}; the paper engine can't price the order.");

            var order = new PaperOrder(
                Guid.NewGuid().ToString(),
                approvedOrder.ClientOrderId,
                symbol,
                pair.Base,
                pair.Quote,
                approvedOrder.Side,
                size,
                price,
                _byVenueId.Count == 0 ? 0 : _byVenueId.Values.Max(o => o.Sequence) + 1, // unique even after a pruned restore
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
                var buy = approvedOrder.Side == OrderSide.Buy;
                var shown = buy ? quote.AskSize : quote.BidSize;
                var quantity = Math.Min(order.Remaining, RoundDown(shown, pair.BaseStep));
                Fill(order, quantity, buy ? quote.Ask : quote.Bid, isTaker: true);

                // The size just taken is gone from the book until a new quote arrives.
                _quotes[symbol] = buy ? quote with { AskSize = shown - quantity } : quote with { BidSize = shown - quantity };
            }

            // Immediate-or-cancel: whatever didn't fill at once is cancelled, so nothing rests.
            if (approvedOrder.TimeInForce == TimeInForce.ImmediateOrCancel && order.IsOpen && HonoursImmediateOrCancel)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = now;
                ReleaseReservation(order);
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
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_pairs is not null && now - _pairsLoadedAt < _options.PairsRefreshInterval)
                return _pairs;
        }

        var pairs = await _marketData.GetPairsAsync(cancellationToken).ConfigureAwait(false);
        var map = pairs.ToDictionary(p => Normalise(p.Symbol), StringComparer.Ordinal);
        lock (_gate)
        {
            _pairs = map;
            _pairsLoadedAt = now;
            return map;
        }
    }

    // Returns the quantity filled so the caller can reduce the size left at that price level.
    private decimal TryFillResting(PaperOrder order, PairInfo pair, decimal touch, decimal sizeAtTouch)
    {
        if (sizeAtTouch <= 0)
            return 0;

        // A crossing remainder sits at the top of the book, so a quote at its limit reaches it.
        // A resting maker order needs the market to trade through it. Either way it fills at its own limit.
        var reached = order.IsTaker
            ? (order.Side == OrderSide.Buy ? touch <= order.LimitPrice : touch >= order.LimitPrice)
            : (order.Side == OrderSide.Buy ? touch < order.LimitPrice : touch > order.LimitPrice);
        if (!reached)
            return 0;

        var quantity = Math.Min(order.Remaining, RoundDown(sizeAtTouch, pair.BaseStep));
        Fill(order, quantity, order.LimitPrice, order.IsTaker);
        return quantity;
    }

    private void Fill(PaperOrder order, decimal quantity, decimal price, bool isTaker)
    {
        if (quantity <= 0)
            return;

        var notional = quantity * price;
        var fee = Math.Round(notional * (isTaker ? _options.TakerFeeRate : _options.MakerFeeRate), 8, MidpointRounding.ToZero);
        var baseWallet = GetWallet(order.BaseCurrency);
        var quoteWallet = GetWallet(order.QuoteCurrency);
        var completes = quantity == order.Remaining;

        if (order.Side == OrderSide.Buy)
        {
            // Exact share of the reservation; on the last fill, everything left. Never less than the cost.
            var share = completes ? order.Reserved : Math.Min(order.Reserved, BuyReservation(quantity, order.LimitPrice));
            order.Reserved -= share;
            quoteWallet.Reserved -= share;
            quoteWallet.Available += share - notional - fee;
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

    // Exact (unrounded) worst case: the full size at the limit price, paying the taker fee.
    private decimal BuyReservation(decimal quantity, decimal limitPrice) =>
        quantity * limitPrice * (1 + _options.TakerFeeRate);

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

        private static string RawStatus(OrderStatus status) => status switch
        {
            OrderStatus.PartiallyFilled => "partially_filled",
            _ => status.ToString().ToLowerInvariant(),
        };
    }
}

/// <summary>A paper account saved for restarts. Amounts are decimals; the store writes them as strings.</summary>
public sealed record PaperAccountState(IReadOnlyDictionary<string, PaperWalletState> Wallets, IReadOnlyList<PaperOrderState> Orders);

public sealed record PaperWalletState(decimal Available, decimal Reserved);

public sealed record PaperOrderState(
    string VenueOrderId,
    string ClientOrderId,
    string Symbol,
    string BaseCurrency,
    string QuoteCurrency,
    OrderSide Side,
    decimal Quantity,
    decimal LimitPrice,
    int Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    OrderStatus Status,
    bool IsTaker,
    decimal Filled,
    decimal FilledAmount,
    decimal Fee,
    decimal Reserved);
